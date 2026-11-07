using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Compaction;
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

    public TelemetryChatClient(
        IChatClient innerClient,
        IOperationsTelemetryService telemetryService,
        string defaultModel,
        string defaultTarget,
        ILogger<TelemetryChatClient> logger,
        string? endpoint,
        string? sessionId)
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
        var startTime = DateTime.UtcNow;

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
        var sw = Stopwatch.StartNew();

        try
        {
            var response = await base.GetResponseAsync(messagesList, options, cancellationToken);
            sw.Stop();

            var durationMs = sw.Elapsed.TotalMilliseconds;
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
            sw.Stop();
            var durationMs = sw.Elapsed.TotalMilliseconds;
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
        var startTime = DateTime.UtcNow;

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
        var sw = Stopwatch.StartNew();
        var reasoningBuffer = new StringBuilder();
        var textBuffer = new StringBuilder();
        int tokenCount = 0;
        int reasoningTokens = 0;
        int? streamingInputTokens = null;
        bool inThinkTag = false;
        bool completionRecorded = false;
        bool completedNaturally = false;

        await using var enumerator = base.GetStreamingResponseAsync(messagesList, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                ChatResponseUpdate? update = null;
                bool hasMore = false;
                try
                {
                    hasMore = await enumerator.MoveNextAsync();
                    if (hasMore)
                    {
                        update = enumerator.Current;
                    }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    var finalPromptTokens = streamingInputTokens.HasValue && streamingInputTokens.Value > 0
                        ? streamingInputTokens.Value
                        : promptTokens;
                    var detailedError = HttpErrorDetailHelper.FormatLlmInvocationError(ex);
                    var failedRecord = runningRecord with
                    {
                        DurationMs = sw.Elapsed.TotalMilliseconds,
                        PromptTokens = finalPromptTokens,
                        CompletionTokens = tokenCount,
                        ReasoningTokens = reasoningTokens,
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

                var elapsedSec = sw.Elapsed.TotalSeconds;
                var tps = elapsedSec > 0.05 ? Math.Round(tokenCount / elapsedSec, 1) : 0.0;
                _telemetryService.UpdateLlmCallProgress(callId, tokenCount, sw.Elapsed.TotalMilliseconds, tps, reasoningTokens);

                yield return update;
            }

            completedNaturally = true;
        }
        finally
        {
            if (!completionRecorded)
            {
                sw.Stop();
                var durationMs = sw.Elapsed.TotalMilliseconds;
                var elapsedSecFinal = sw.Elapsed.TotalSeconds;
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
                var argsStr = toolCall.Arguments != null && toolCall.Arguments.Count > 0
                    ? string.Join(", ", toolCall.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                    : string.Empty;
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
