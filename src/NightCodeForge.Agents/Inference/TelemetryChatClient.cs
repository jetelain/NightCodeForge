using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Compaction;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Core.Workflow;
using Pmad.AI.Tokens;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Transparent DelegatingChatClient intercepting all LLM inference calls across the application
/// to capture execution duration, prompt and completion previews, token generation rates, and statuses.
/// </summary>
public class TelemetryChatClient : DelegatingChatClient
{
    private readonly IOperationsTelemetryService _telemetryService;
    private readonly string _defaultModel;
    private readonly string _defaultTarget;
    private readonly string _endpoint;
    private readonly string? _sessionId;
    private readonly int? _taskId;
    private readonly string? _stageId;
    private readonly ILogger<TelemetryChatClient> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _firstTokenTimeout;
    private readonly TimeSpan _streamIdleTimeout;

    public TelemetryChatClient(
        IChatClient innerClient,
        IOperationsTelemetryService telemetryService,
        string defaultModel,
        string defaultTarget,
        ILogger<TelemetryChatClient> logger,
        string? endpoint,
        string? sessionId,
        TimeProvider timeProvider,
        TimeSpan firstTokenTimeout,
        TimeSpan streamIdleTimeout)
        : base(innerClient ?? throw new ArgumentNullException(nameof(innerClient)))
    {
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        _defaultModel = !string.IsNullOrWhiteSpace(defaultModel) ? defaultModel : "unknown";
        _defaultTarget = !string.IsNullOrWhiteSpace(defaultTarget) ? defaultTarget : "Auto";
        _endpoint = !string.IsNullOrWhiteSpace(endpoint) ? endpoint : "http://localhost:11434";
        _sessionId = !string.IsNullOrWhiteSpace(sessionId) ? sessionId.Trim() : null;
        if (!string.IsNullOrWhiteSpace(_sessionId))
        {
            var (parsedTaskId, parsedStageId) = LlmCorrelationHeaders.ParseSessionId(_sessionId);
            _taskId = parsedTaskId;
            _stageId = parsedStageId;
        }
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _firstTokenTimeout = firstTokenTimeout > TimeSpan.Zero ? firstTokenTimeout : TimeSpan.FromMinutes(10);
        _streamIdleTimeout = streamIdleTimeout > TimeSpan.Zero ? streamIdleTimeout : TimeSpan.FromMinutes(LlmProviderOptions.DefaultStreamIdleTimeoutMinutes);
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var callId = Guid.NewGuid().ToString("N");
        var model = !string.IsNullOrWhiteSpace(options?.ModelId) ? options.ModelId : _defaultModel;
        var target = ResolveTarget(options);
        var thinkingEffort = ResolveThinkingEffort(options);
        var messagesList = chatMessages?.ToList() ?? new List<ChatMessage>();
        var promptPreview = ExtractPromptPreview(messagesList);
        var estimator = ModelCalibratedTokenEstimator.Resolve(model);
        var promptTokens = EstimateTotalContextTokens(messagesList, model);
        var startTime = _timeProvider.GetUtcNow().UtcDateTime;

        var runningRecord = new LlmCallRecord(
            CallId: callId,
            Model: model,
            TargetBackend: target,
            StartTime: startTime,
            DurationMs: 0,
            PromptTokens: promptTokens,
            CompletionTokens: 0,
            TokensPerSecond: 0,
            PromptPreview: promptPreview,
            CompletionPreview: null,
            Status: LlmCallStatus.Running,
            ErrorMessage: null,
            ReasoningTokens: 0,
            Endpoint: _endpoint,
            ThinkingEffort: thinkingEffort,
            SessionId: _sessionId,
            TaskId: _taskId,
            StageId: _stageId);

        _telemetryService.RecordLlmCallStarted(runningRecord);
        var startTimestamp = _timeProvider.GetTimestamp();

        try
        {
            var response = await base.GetResponseAsync(messagesList, options, cancellationToken);
            var elapsed = _timeProvider.GetElapsedTime(startTimestamp);

            var durationMs = elapsed.TotalMilliseconds;
            var (completionPreview, extractedReasoningTokens, extractedTextTokens) = ExtractResponseDetails(response, estimator);

            var reasoningTokens = extractedReasoningTokens;
            if (response.Usage?.AdditionalCounts != null)
            {
                foreach (var (k, v) in response.Usage.AdditionalCounts)
                {
                    if (k.Contains("reason", StringComparison.OrdinalIgnoreCase) && v > reasoningTokens)
                    {
                        reasoningTokens = (int)v;
                    }
                }
            }

            var usageOutputTokens = (int?)response.Usage?.OutputTokenCount;
            int completionTokens;
            if (usageOutputTokens.HasValue)
            {
                if (usageOutputTokens.Value < reasoningTokens)
                {
                    completionTokens = usageOutputTokens.Value + reasoningTokens;
                }
                else
                {
                    completionTokens = Math.Max(usageOutputTokens.Value, extractedReasoningTokens + extractedTextTokens);
                }
            }
            else
            {
                completionTokens = extractedReasoningTokens + extractedTextTokens;
                if (completionTokens == 0)
                {
                    completionTokens = EstimateTokens(completionPreview, estimator);
                }
            }

            var finalPromptTokens = (response.Usage?.InputTokenCount.HasValue == true && response.Usage.InputTokenCount.Value > 0)
                ? (int)response.Usage.InputTokenCount.Value
                : promptTokens;
            var tokensPerSec = durationMs > 50 ? (completionTokens / (durationMs / 1000.0)) : 0.0;

            var completedRecord = runningRecord with
            {
                DurationMs = durationMs,
                PromptTokens = finalPromptTokens,
                CompletionTokens = completionTokens,
                TokensPerSecond = Math.Round(tokensPerSec, 1),
                ReasoningTokens = reasoningTokens,
                CompletionPreview = completionPreview,
                Status = LlmCallStatus.Completed
            };

            _telemetryService.RecordLlmCallCompleted(completedRecord);
            return response;
        }
        catch (Exception ex)
        {
            var elapsed = _timeProvider.GetElapsedTime(startTimestamp);
            var durationMs = elapsed.TotalMilliseconds;
            var detailedError = HttpErrorDetailHelper.FormatLlmInvocationError(ex);
            var failedRecord = runningRecord with
            {
                DurationMs = durationMs,
                Status = (ex is OperationCanceledException && !TransientErrorDetector.IsHttpTimeout(ex) && cancellationToken.IsCancellationRequested) ? LlmCallStatus.Cancelled : LlmCallStatus.Failed,
                ErrorMessage = detailedError
            };

            _telemetryService.RecordLlmCallCompleted(failedRecord);
            _logger?.LogDebug(ex, "Intercepted LLM call failure for {CallId}: {ErrorMessage}", callId, detailedError);
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var callId = Guid.NewGuid().ToString("N");
        var model = !string.IsNullOrWhiteSpace(options?.ModelId) ? options.ModelId : _defaultModel;
        var target = ResolveTarget(options);
        var thinkingEffort = ResolveThinkingEffort(options);
        var messagesList = chatMessages?.ToList() ?? new List<ChatMessage>();
        var promptPreview = ExtractPromptPreview(messagesList);
        var estimator = ModelCalibratedTokenEstimator.Resolve(model);
        var promptTokens = EstimateTotalContextTokens(messagesList, model);
        var startTime = _timeProvider.GetUtcNow().UtcDateTime;

        var runningRecord = new LlmCallRecord(
            CallId: callId,
            Model: model,
            TargetBackend: target,
            StartTime: startTime,
            DurationMs: 0,
            PromptTokens: promptTokens,
            CompletionTokens: 0,
            TokensPerSecond: 0,
            PromptPreview: promptPreview,
            CompletionPreview: null,
            Status: LlmCallStatus.Running,
            ErrorMessage: null,
            ReasoningTokens: 0,
            Endpoint: _endpoint,
            ThinkingEffort: thinkingEffort,
            SessionId: _sessionId,
            TaskId: _taskId,
            StageId: _stageId);

        _telemetryService.RecordLlmCallStarted(runningRecord);
        var startTimestamp = _timeProvider.GetTimestamp();
        var reasoningBuffer = new StringBuilder();
        var textBuffer = new StringBuilder();
        int tokenCount = 0;
        int reasoningTokens = 0;
        int? streamingInputTokens = null;
        bool inThinkTag = false;
        bool completionRecorded = false;
        bool completedNaturally = false;

        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var streamToken = streamCts.Token;
        using var streamScope = StreamingCancellationScope.Enter(streamToken, startTimestamp, out var streamState);

        await using var enumerator = base.GetStreamingResponseAsync(messagesList, options, streamToken)
            .GetAsyncEnumerator(streamToken);

        bool isFirstChunk = true;
        try
        {
            while (true)
            {
                ChatResponseUpdate? update = null;
                bool hasMore = false;
                var currentTimeout = isFirstChunk ? _firstTokenTimeout : _streamIdleTimeout;

                try
                {
                    var moveNextTask = enumerator.MoveNextAsync().AsTask();
                    try
                    {
                        hasMore = await AwaitMoveNextWithWatchdogAsync(moveNextTask, isFirstChunk, streamState, cancellationToken);
                    }
                    catch (TimeoutException timeoutEx)
                    {
                        // Watchdog fired!
                        // 1. Cancel linked CTS immediately to abort underlying stream read and unblock ReadLineAsync
                        streamCts.Cancel();

                        // 2. Await moveNextTask to let async iterator state machine complete cleanly and run using blocks
                        try
                        {
                            await moveNextTask;
                        }
                        catch (Exception)
                        {
                            // Justification: Suppress expected cancellation or IO faults from the aborted stream read so the enriched timeout exception is thrown.
                        }

                        // 3. Build structured diagnostics and throw enriched LlmStreamTimeoutException
                        var phase = isFirstChunk
                            ? (streamState.AttemptNumber > 0 && !streamState.ResponseHeadersReceived
                                ? StreamTimeoutPhase.PreHeaderPrefill
                                : StreamTimeoutPhase.FirstTokenPrefill)
                            : StreamTimeoutPhase.MidStreamIdleStall;
                        var timeoutElapsedSec = _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds;
                        var timeoutTps = timeoutElapsedSec > 0.05 ? Math.Round(tokenCount / timeoutElapsedSec, 1) : 0.0;

                        var diagnostics = new StreamTimeoutDiagnostics
                        {
                            Phase = phase,
                            ElapsedSeconds = timeoutElapsedSec,
                            ConfiguredTimeoutSeconds = currentTimeout.TotalSeconds,
                            GeneratedTokens = tokenCount,
                            TokensPerSecond = timeoutTps
                        };

                        var enrichedEx = new LlmStreamTimeoutException(
                            HttpErrorDetailHelper.FormatStreamTimeoutError(diagnostics),
                            timeoutEx,
                            diagnostics);

                        if (timeoutEx.Data.Contains(ResilienceConstants.RetriedKey))
                        {
                            enrichedEx.Data[ResilienceConstants.RetriedKey] = true;
                        }

                        throw enrichedEx;
                    }

                    if (hasMore)
                    {
                        update = enumerator.Current;
                    }
                }
                catch (Exception ex)
                {
                    var failElapsed = _timeProvider.GetElapsedTime(startTimestamp);
                    var failElapsedSec = failElapsed.TotalSeconds;
                    var failTps = failElapsedSec > 0.05 ? Math.Round(tokenCount / failElapsedSec, 1) : 0.0;

                    if (ex is not LlmStreamTimeoutException && tokenCount > 0 && IsMidStreamConnectionAbort(ex, cancellationToken))
                    {
                        ex.Data[StreamTimeoutDiagnostics.DataKey] = new StreamTimeoutDiagnostics
                        {
                            Phase = StreamTimeoutPhase.MidStreamConnectionClosed,
                            ElapsedSeconds = failElapsedSec,
                            ConfiguredTimeoutSeconds = currentTimeout.TotalSeconds,
                            GeneratedTokens = tokenCount,
                            TokensPerSecond = failTps
                        };
                    }

                    var finalPromptTokens = streamingInputTokens.HasValue && streamingInputTokens.Value > 0
                        ? streamingInputTokens.Value
                        : promptTokens;
                    var detailedError = HttpErrorDetailHelper.FormatLlmInvocationError(ex);
                    var failedRecord = runningRecord with
                    {
                        DurationMs = failElapsed.TotalMilliseconds,
                        PromptTokens = finalPromptTokens,
                        CompletionTokens = tokenCount,
                        ReasoningTokens = reasoningTokens,
                        TokensPerSecond = failTps,
                        CompletionPreview = BuildCombinedPreview(reasoningBuffer, textBuffer),
                        Status = (ex is OperationCanceledException && !TransientErrorDetector.IsHttpTimeout(ex) && cancellationToken.IsCancellationRequested) ? LlmCallStatus.Cancelled : LlmCallStatus.Failed,
                        ErrorMessage = detailedError
                    };
                    completionRecorded = true;
                    _telemetryService.RecordLlmCallCompleted(failedRecord);
                    _logger?.LogDebug(ex, "Intercepted streaming LLM call failure for {CallId}: {ErrorMessage}", callId, detailedError);
                    throw;
                }

                if (!hasMore || update == null)
                {
                    break;
                }

                if (isFirstChunk && HasNonEmptyStreamContent(update))
                {
                    isFirstChunk = false;
                }

                if (update.Contents != null)
                {
                    foreach (var content in update.Contents)
                    {
                        if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                        {
                            var tokens = EstimateTokens(reasoning.Text, estimator);
                            reasoningTokens += tokens;
                            tokenCount += tokens;
                            if (reasoningBuffer.Length < 1000)
                            {
                                reasoningBuffer.Append(reasoning.Text);
                            }
                        }
                        else if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
                        {
                            ProcessTextChunk(text.Text, ref inThinkTag, ref reasoningTokens, ref tokenCount, reasoningBuffer, textBuffer, estimator);
                        }
                        else if (content is FunctionCallContent fcc)
                        {
                            ProcessFunctionCallChunk(fcc, ref tokenCount, textBuffer, estimator);
                        }
                        else if (content is UsageContent uc && uc.Details != null)
                        {
                            if (uc.Details.InputTokenCount.HasValue && uc.Details.InputTokenCount.Value > 0)
                            {
                                streamingInputTokens = (int)uc.Details.InputTokenCount.Value;
                            }
                            if (uc.Details.OutputTokenCount.HasValue && uc.Details.OutputTokenCount.Value > tokenCount)
                            {
                                tokenCount = (int)uc.Details.OutputTokenCount.Value;
                            }
                            if (uc.Details.AdditionalCounts != null)
                            {
                                foreach (var (k, v) in uc.Details.AdditionalCounts)
                                {
                                    if (k.Contains("reason", StringComparison.OrdinalIgnoreCase) && v > reasoningTokens)
                                    {
                                        reasoningTokens = (int)v;
                                    }
                                }
                            }
                            if (tokenCount < reasoningTokens)
                            {
                                tokenCount = reasoningTokens;
                            }
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(update.Text))
                {
                    ProcessTextChunk(update.Text, ref inThinkTag, ref reasoningTokens, ref tokenCount, reasoningBuffer, textBuffer, estimator);
                }

                var elapsed = _timeProvider.GetElapsedTime(startTimestamp);
                var elapsedSec = elapsed.TotalSeconds;
                var tps = elapsedSec > 0.05 ? Math.Round(tokenCount / elapsedSec, 1) : 0.0;
                _telemetryService.UpdateLlmCallProgress(callId, tokenCount, elapsed.TotalMilliseconds, tps, reasoningTokens);

                yield return update;
            }

            completedNaturally = true;
        }
        finally
        {
            if (!completionRecorded)
            {
                var elapsedFinal = _timeProvider.GetElapsedTime(startTimestamp);
                var durationMs = elapsedFinal.TotalMilliseconds;
                var elapsedSecFinal = elapsedFinal.TotalSeconds;
                var finalTps = elapsedSecFinal > 0.05 ? Math.Round(tokenCount / elapsedSecFinal, 1) : 0.0;

                var status = completedNaturally
                    ? LlmCallStatus.Completed
                    : (cancellationToken.IsCancellationRequested ? LlmCallStatus.Cancelled : LlmCallStatus.Completed);

                var finalPromptTokens = streamingInputTokens.HasValue && streamingInputTokens.Value > 0
                    ? streamingInputTokens.Value
                    : promptTokens;

                var completedRecord = runningRecord with
                {
                    DurationMs = durationMs,
                    PromptTokens = finalPromptTokens,
                    CompletionTokens = tokenCount,
                    TokensPerSecond = finalTps,
                    ReasoningTokens = reasoningTokens,
                    CompletionPreview = BuildCombinedPreview(reasoningBuffer, textBuffer),
                    Status = status
                };

                _telemetryService.RecordLlmCallCompleted(completedRecord);
            }
        }
    }

    private async Task<bool> AwaitMoveNextWithWatchdogAsync(
        Task<bool> moveNextTask,
        bool isFirstChunk,
        StreamingCallState streamState,
        CancellationToken cancellationToken)
    {
        if (!isFirstChunk)
        {
            return await moveNextTask.WaitAsync(_streamIdleTimeout, _timeProvider, cancellationToken);
        }

        // Phase 1: When TransientHttpErrorRetryHandler is enforcing its own per-attempt timeout before HTTP headers arrive,
        // wait for either HTTP response headers or handler completion (including any handler-level retries and backoffs)
        // so the stream watchdog does not prematurely cancel an in-flight pre-header retry attempt.
        if (streamState.HasHandlerAttemptTimeout && !streamState.ResponseHeadersReceived)
        {
            await Task.WhenAny(moveNextTask, streamState.WaitForHeadersAsync(cancellationToken));
            if (moveNextTask.IsCompleted)
            {
                return await moveNextTask;
            }
        }

        // Phase 2A: HTTP headers have been received (or no handler per-attempt timeout is active).
        // Enforce the remaining FirstTokenTimeout budget for the current attempt until the first non-empty chunk arrives.
        var attemptElapsed = _timeProvider.GetElapsedTime(streamState.CurrentAttemptStartTimestamp);
        var remaining = _firstTokenTimeout - attemptElapsed;
        var waitBudget = remaining > TimeSpan.FromMilliseconds(1) ? remaining : TimeSpan.FromMilliseconds(1);

        return await moveNextTask.WaitAsync(waitBudget, _timeProvider, cancellationToken);
    }

    private static bool HasNonEmptyStreamContent(ChatResponseUpdate update)
    {
        if (!string.IsNullOrEmpty(update.Text))
        {
            return true;
        }

        if (update.Contents == null || update.Contents.Count == 0)
        {
            return false;
        }

        foreach (var content in update.Contents)
        {
            if (content is TextContent tc && !string.IsNullOrEmpty(tc.Text))
            {
                return true;
            }
            if (content is TextReasoningContent trc && !string.IsNullOrEmpty(trc.Text))
            {
                return true;
            }
            if (content is FunctionCallContent)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMidStreamConnectionAbort(Exception ex, CancellationToken callerToken)
    {
        if (callerToken.IsCancellationRequested)
        {
            return false;
        }

        if (HttpErrorDetailHelper.FindException<OperationsPausedException>(ex) != null)
        {
            return false;
        }

        return ex is HttpRequestException or IOException or SocketException or OperationCanceledException
            || TransientErrorDetector.IsTransient(ex);
    }

    private string ResolveTarget(ChatOptions? options)
    {
        if (string.Equals(_defaultTarget, "OpenAI Compatible", StringComparison.OrdinalIgnoreCase))
        {
            return "OpenAI Compatible";
        }

        if (options?.AdditionalProperties != null)
        {
            if (options.AdditionalProperties.TryGetValue("RoutingTarget", out var targetVal) && targetVal != null)
            {
                return targetVal.ToString()!;
            }
            if (options.AdditionalProperties.TryGetValue("X-OllamaRouter-Target", out var headerVal) && headerVal != null)
            {
                return headerVal.ToString()!;
            }
        }
        return _defaultTarget;
    }

    public const string CompactionSummarizerPreviewPrefix = "[Context Compaction Summarizer] ";
    public const int MaxPreviewLength = 500;
    private const int MaxReasoningPreviewLength = 300;
    private const int MaxCombinedResponsePreviewLength = 400;
    private const string TruncationEllipsis = "...";

    public static string ExtractPromptPreview(IReadOnlyList<ChatMessage> messages)
    {
        if (messages == null || messages.Count == 0) return string.Empty;

        var isCompactionSummarizer = messages.Any(m =>
            m.Role == ChatRole.System
            && !string.IsNullOrWhiteSpace(m.Text)
            && m.Text.Contains(PiStyleCrucibleCompactionStrategy.SummarizationSystemPrompt, StringComparison.Ordinal));

        var prefix = isCompactionSummarizer ? CompactionSummarizerPreviewPrefix : string.Empty;
        var maxBodyLength = MaxPreviewLength - prefix.Length;

        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (msg.Role == ChatRole.System)
            {
                continue;
            }

            if (msg.Role == ChatRole.User
                && i < messages.Count - 1
                && PiStyleCrucibleCompactionStrategy.IsCompactionCheckpointMessage(msg))
            {
                continue;
            }

            var formatted = FormatMessageForPreview(msg);
            if (string.IsNullOrWhiteSpace(formatted))
            {
                continue;
            }

            var truncated = Truncate(formatted, Math.Max(0, maxBodyLength));
            return string.IsNullOrEmpty(prefix)
                ? truncated
                : prefix + Truncate(formatted, Math.Max(0, maxBodyLength - TruncationEllipsis.Length));
        }

        var lastMsg = messages[^1];
        var fallback = FormatMessageForPreview(lastMsg);
        if (string.IsNullOrWhiteSpace(fallback))
        {
            return string.Empty;
        }

        return string.IsNullOrEmpty(prefix)
            ? Truncate(fallback, MaxPreviewLength)
            : prefix + Truncate(fallback, Math.Max(0, maxBodyLength - TruncationEllipsis.Length));
    }

    private static string FormatMessageForPreview(ChatMessage message)
    {
        if (message.Contents != null && message.Contents.Count > 0)
        {
            var toolResult = message.Contents.OfType<FunctionResultContent>().LastOrDefault();
            if (toolResult != null)
            {
                var resultStr = toolResult.Result?.ToString()?.Trim() ?? string.Empty;
                return string.IsNullOrWhiteSpace(resultStr)
                    ? $"[Tool Result: {toolResult.CallId}]"
                    : $"[Tool Result] {resultStr}";
            }

            var toolCall = message.Contents.OfType<FunctionCallContent>().LastOrDefault();
            if (toolCall != null)
            {
                var argsStr = FormatFunctionCallArguments(toolCall.Arguments);
                var textPart = !string.IsNullOrWhiteSpace(message.Text) ? $"{message.Text.Trim()} " : string.Empty;
                return string.IsNullOrWhiteSpace(argsStr)
                    ? $"{textPart}[Tool Call: {toolCall.Name}]".Trim()
                    : $"{textPart}[Tool Call: {toolCall.Name}] {argsStr}".Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            var trimmed = message.Text.Trim();
            if (message.Role == ChatRole.Tool && !trimmed.StartsWith("[Tool Result", StringComparison.OrdinalIgnoreCase))
            {
                return $"[Tool Result] {trimmed}";
            }
            return trimmed;
        }

        return string.Empty;
    }

    private static (string Preview, int ReasoningTokens, int TextTokens) ExtractResponseDetails(ChatResponse response, HeuristicTokenEstimator estimator)
    {
        var reasoningBuffer = new StringBuilder();
        var textBuffer = new StringBuilder();
        int reasoningTokens = 0;
        int tokenCount = 0;
        bool inThinkTag = false;

        if (response.Messages != null && response.Messages.Count > 0)
        {
            foreach (var message in response.Messages)
            {
                if (message.Contents != null && message.Contents.Count > 0)
                {
                    foreach (var content in message.Contents)
                    {
                        if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                        {
                            var tokens = EstimateTokens(reasoning.Text, estimator);
                            reasoningTokens += tokens;
                            tokenCount += tokens;
                            if (reasoningBuffer.Length < 1000) reasoningBuffer.Append(reasoning.Text);
                        }
                        else if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
                        {
                            ProcessTextChunk(text.Text, ref inThinkTag, ref reasoningTokens, ref tokenCount, reasoningBuffer, textBuffer, estimator);
                        }
                        else if (content is FunctionCallContent fcc)
                        {
                            ProcessFunctionCallChunk(fcc, ref tokenCount, textBuffer, estimator);
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(message.Text))
                {
                    ProcessTextChunk(message.Text, ref inThinkTag, ref reasoningTokens, ref tokenCount, reasoningBuffer, textBuffer, estimator);
                }
            }
        }
        else if (!string.IsNullOrEmpty(response.Text))
        {
            ProcessTextChunk(response.Text, ref inThinkTag, ref reasoningTokens, ref tokenCount, reasoningBuffer, textBuffer, estimator);
        }

        var preview = BuildCombinedPreview(reasoningBuffer, textBuffer);
        var textTokens = Math.Max(0, tokenCount - reasoningTokens);
        return (preview, reasoningTokens, textTokens);
    }

    private static void ProcessFunctionCallChunk(
        FunctionCallContent fcc,
        ref int tokenCount,
        StringBuilder textBuffer,
        HeuristicTokenEstimator estimator)
    {
        var tokens = ModelCalibratedTokenEstimator.EstimateFunctionCallTokens(fcc, estimator);
        tokenCount += tokens;

        if (textBuffer.Length < 1000)
        {
            var argsStr = FormatFunctionCallArguments(fcc.Arguments);
            var formatted = !string.IsNullOrEmpty(fcc.Name)
                ? (string.IsNullOrWhiteSpace(argsStr) ? $"[Tool Call: {fcc.Name}]" : $"[Tool Call: {fcc.Name}] {argsStr}")
                : argsStr;

            if (!string.IsNullOrEmpty(formatted))
            {
                if (textBuffer.Length > 0 && !char.IsWhiteSpace(textBuffer[^1]))
                {
                    textBuffer.Append(' ');
                }
                textBuffer.Append(formatted);
            }
        }
    }

    private static string FormatFunctionCallArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments == null || arguments.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(", ", arguments.Select(kv => $"{kv.Key}={FormatFunctionCallArgumentValue(kv.Value)}"));
    }

    private static string FormatFunctionCallArgumentValue(object? value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        if (value is string s)
        {
            return s;
        }

        if (value is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.String ? (je.GetString() ?? string.Empty) : je.GetRawText();
        }

        if (value.GetType().IsPrimitive || value is decimal)
        {
            return value.ToString() ?? string.Empty;
        }

        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (Exception)
        {
            // Justification: Fallback to ToString() when diagnostic preview argument contains a non-JSON-serializable value.
            return value.ToString() ?? string.Empty;
        }
    }

    private static void ProcessTextChunk(
        string chunk,
        ref bool inThinkTag,
        ref int reasoningTokens,
        ref int tokenCount,
        StringBuilder reasoningBuffer,
        StringBuilder textBuffer,
        HeuristicTokenEstimator estimator)
    {
        while (!string.IsNullOrEmpty(chunk))
        {
            if (!inThinkTag)
            {
                var thinkIdx = chunk.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                if (thinkIdx >= 0)
                {
                    var before = chunk[..thinkIdx];
                    if (!string.IsNullOrEmpty(before))
                    {
                        var beforeTokens = EstimateTokens(before, estimator);
                        tokenCount += beforeTokens;
                        if (textBuffer.Length < 1000) textBuffer.Append(before);
                    }
                    inThinkTag = true;
                    chunk = chunk[(thinkIdx + 7)..];
                }
                else
                {
                    var tokens = EstimateTokens(chunk, estimator);
                    tokenCount += tokens;
                    if (textBuffer.Length < 1000) textBuffer.Append(chunk);
                    chunk = string.Empty;
                }
            }
            else
            {
                var endThinkIdx = chunk.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (endThinkIdx >= 0)
                {
                    var thinkPart = chunk[..endThinkIdx];
                    if (!string.IsNullOrEmpty(thinkPart))
                    {
                        var thinkTokens = EstimateTokens(thinkPart, estimator);
                        reasoningTokens += thinkTokens;
                        tokenCount += thinkTokens;
                        if (reasoningBuffer.Length < 1000) reasoningBuffer.Append(thinkPart);
                    }
                    inThinkTag = false;
                    chunk = chunk[(endThinkIdx + 8)..];
                }
                else
                {
                    var thinkTokens = EstimateTokens(chunk, estimator);
                    reasoningTokens += thinkTokens;
                    tokenCount += thinkTokens;
                    if (reasoningBuffer.Length < 1000) reasoningBuffer.Append(chunk);
                    chunk = string.Empty;
                }
            }
        }
    }

    private static string BuildCombinedPreview(StringBuilder reasoningBuffer, StringBuilder textBuffer)
    {
        var reasoningTrimmed = reasoningBuffer.ToString().Trim();
        var textTrimmed = textBuffer.ToString().Trim();

        if (!string.IsNullOrEmpty(reasoningTrimmed) && !string.IsNullOrEmpty(textTrimmed))
        {
            return $"[Reasoning]\n{Truncate(reasoningTrimmed, MaxReasoningPreviewLength)}\n\n[Response]\n{Truncate(textTrimmed, MaxCombinedResponsePreviewLength)}";
        }
        if (!string.IsNullOrEmpty(reasoningTrimmed))
        {
            return $"[Reasoning]\n{Truncate(reasoningTrimmed, MaxPreviewLength)}";
        }
        return Truncate(textTrimmed, MaxPreviewLength);
    }

    public static int EstimateTotalContextTokens(IEnumerable<ChatMessage>? messages, string? modelId = null) =>
        ModelCalibratedTokenEstimator.EstimateMessagesTokens(messages, modelId);

    private static int EstimateTokens(string? text, HeuristicTokenEstimator estimator) =>
        ModelCalibratedTokenEstimator.EstimateTextTokens(text, estimator);

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength] + TruncationEllipsis;
    }

    public static string ResolveThinkingEffort(ChatOptions? options)
    {
        if (options?.AdditionalProperties == null)
        {
            return "unknown";
        }
        
        // For Ollama from "think" directly (boolean or string for effort)
        if (options.AdditionalProperties.TryGetValue("think", out var thinkVal))
        {
            if (thinkVal is bool enabled)
            {
                return enabled ? "default" : "none";
            }
            return thinkVal?.ToString() ?? "default";
        }

        // For OpenAI-compatible endpoints resolve from "reasoning_effort"
        if (options.AdditionalProperties.TryGetValue("reasoning_effort", out var openAiRe) && openAiRe != null && !string.IsNullOrWhiteSpace(openAiRe.ToString()))
        {
            return openAiRe.ToString()!;
        }

        return "unknown";
    }
}
