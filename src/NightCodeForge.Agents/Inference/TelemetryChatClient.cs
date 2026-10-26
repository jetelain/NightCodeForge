using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

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
    private readonly ILogger<TelemetryChatClient>? _logger;

    public TelemetryChatClient(
        IChatClient innerClient,
        IOperationsTelemetryService telemetryService,
        string defaultModel,
        string defaultTarget,
        ILogger<TelemetryChatClient> logger,
        string? endpoint = null)
        : base(innerClient ?? throw new ArgumentNullException(nameof(innerClient)))
    {
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        _defaultModel = !string.IsNullOrWhiteSpace(defaultModel) ? defaultModel : "unknown";
        _defaultTarget = !string.IsNullOrWhiteSpace(defaultTarget) ? defaultTarget : "Auto";
        _endpoint = !string.IsNullOrWhiteSpace(endpoint) ? endpoint : "http://localhost:11434";
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public TelemetryChatClient(
        IChatClient innerClient,
        IOperationsTelemetryService telemetryService,
        string defaultModel,
        string defaultTarget,
        string? endpoint = null)
        : this(innerClient, telemetryService, defaultModel, defaultTarget, Microsoft.Extensions.Logging.Abstractions.NullLogger<TelemetryChatClient>.Instance, endpoint)
    {
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
        var promptTokens = EstimateTotalContextTokens(messagesList);
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
            ThinkingEffort: thinkingEffort);

        _telemetryService.RecordLlmCallStarted(runningRecord);
        var sw = Stopwatch.StartNew();

        try
        {
            var response = await base.GetResponseAsync(messagesList, options, cancellationToken);
            sw.Stop();

            var durationMs = sw.Elapsed.TotalMilliseconds;
            var (completionPreview, extractedReasoningTokens, extractedTextTokens) = ExtractResponseDetails(response);

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
                    completionTokens = EstimateTokens(completionPreview);
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
            var failedRecord = runningRecord with
            {
                DurationMs = durationMs,
                Status = ex is OperationCanceledException ? LlmCallStatus.Cancelled : LlmCallStatus.Failed,
                ErrorMessage = ex.Message
            };

            _telemetryService.RecordLlmCallCompleted(failedRecord);
            _logger?.LogDebug(ex, "Intercepted LLM call failure for {CallId}", callId);
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
        var promptTokens = EstimateTotalContextTokens(messagesList);
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
            ThinkingEffort: thinkingEffort);

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
                    var failedRecord = runningRecord with
                    {
                        DurationMs = sw.Elapsed.TotalMilliseconds,
                        PromptTokens = finalPromptTokens,
                        CompletionTokens = tokenCount,
                        ReasoningTokens = reasoningTokens,
                        CompletionPreview = BuildCombinedPreview(reasoningBuffer, textBuffer),
                        Status = ex is OperationCanceledException ? LlmCallStatus.Cancelled : LlmCallStatus.Failed,
                        ErrorMessage = ex.Message
                    };
                    completionRecorded = true;
                    _telemetryService.RecordLlmCallCompleted(failedRecord);
                    _logger?.LogDebug(ex, "Intercepted streaming LLM call failure for {CallId}", callId);
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
                            var tokens = EstimateTokens(reasoning.Text);
                            reasoningTokens += tokens;
                            tokenCount += tokens;
                            if (reasoningBuffer.Length < 1000)
                            {
                                reasoningBuffer.Append(reasoning.Text);
                            }
                        }
                        else if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
                        {
                            ProcessTextChunk(text.Text, ref inThinkTag, ref reasoningTokens, ref tokenCount, reasoningBuffer, textBuffer);
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
                    ProcessTextChunk(update.Text, ref inThinkTag, ref reasoningTokens, ref tokenCount, reasoningBuffer, textBuffer);
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

    private static string ExtractPromptPreview(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0) return string.Empty;

        var lastUserMsg = messages.LastOrDefault(m => m.Role == ChatRole.User);
        if (lastUserMsg != null && !string.IsNullOrWhiteSpace(lastUserMsg.Text))
        {
            return Truncate(lastUserMsg.Text.Trim(), 500);
        }

        var lastMsg = messages[^1];
        return Truncate(lastMsg.Text?.Trim() ?? string.Empty, 500);
    }

    private static (string Preview, int ReasoningTokens, int TextTokens) ExtractResponseDetails(ChatResponse response)
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
                            var tokens = EstimateTokens(reasoning.Text);
                            reasoningTokens += tokens;
                            tokenCount += tokens;
                            if (reasoningBuffer.Length < 1000) reasoningBuffer.Append(reasoning.Text);
                        }
                        else if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
                        {
                            ProcessTextChunk(text.Text, ref inThinkTag, ref reasoningTokens, ref tokenCount, reasoningBuffer, textBuffer);
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(message.Text))
                {
                    ProcessTextChunk(message.Text, ref inThinkTag, ref reasoningTokens, ref tokenCount, reasoningBuffer, textBuffer);
                }
            }
        }
        else if (!string.IsNullOrEmpty(response.Text))
        {
            ProcessTextChunk(response.Text, ref inThinkTag, ref reasoningTokens, ref tokenCount, reasoningBuffer, textBuffer);
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
        StringBuilder textBuffer)
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
                        var beforeTokens = EstimateTokens(before);
                        tokenCount += beforeTokens;
                        if (textBuffer.Length < 1000) textBuffer.Append(before);
                    }
                    inThinkTag = true;
                    chunk = chunk[(thinkIdx + 7)..];
                }
                else
                {
                    var tokens = EstimateTokens(chunk);
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
                        var thinkTokens = EstimateTokens(thinkPart);
                        reasoningTokens += thinkTokens;
                        tokenCount += thinkTokens;
                        if (reasoningBuffer.Length < 1000) reasoningBuffer.Append(thinkPart);
                    }
                    inThinkTag = false;
                    chunk = chunk[(endThinkIdx + 8)..];
                }
                else
                {
                    var thinkTokens = EstimateTokens(chunk);
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
            return $"[Reasoning]\n{Truncate(reasoningTrimmed, 300)}\n\n[Response]\n{Truncate(textTrimmed, 400)}";
        }
        if (!string.IsNullOrEmpty(reasoningTrimmed))
        {
            return $"[Reasoning]\n{Truncate(reasoningTrimmed, 500)}";
        }
        return Truncate(textTrimmed, 500);
    }

    public static int EstimateTotalContextTokens(IEnumerable<ChatMessage>? messages)
    {
        if (messages == null) return 0;
        int totalTokens = 0;
        bool hasAnyMessage = false;

        foreach (var message in messages)
        {
            hasAnyMessage = true;
            // Per-message framing overhead (~4 tokens for role and boundary delimiters)
            totalTokens += 4;

            if (message.Contents != null && message.Contents.Count > 0)
            {
                foreach (var content in message.Contents)
                {
                    switch (content)
                    {
                        case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                            totalTokens += EstimateTokens(tc.Text);
                            break;
                        case TextReasoningContent trc when !string.IsNullOrEmpty(trc.Text):
                            totalTokens += EstimateTokens(trc.Text);
                            break;
                        case FunctionCallContent fcc:
                            if (!string.IsNullOrEmpty(fcc.Name))
                            {
                                totalTokens += EstimateTokens(fcc.Name);
                            }
                            if (fcc.Arguments != null && fcc.Arguments.Count > 0)
                            {
                                foreach (var (k, v) in fcc.Arguments)
                                {
                                    totalTokens += EstimateTokens(k);
                                    if (v != null)
                                    {
                                        var valStr = v is string s ? s : System.Text.Json.JsonSerializer.Serialize(v);
                                        totalTokens += EstimateTokens(valStr);
                                    }
                                }
                            }
                            break;
                        case FunctionResultContent frc:
                            if (!string.IsNullOrEmpty(frc.CallId))
                            {
                                totalTokens += EstimateTokens(frc.CallId);
                            }
                            if (frc.Result != null)
                            {
                                var resStr = frc.Result is string s ? s : System.Text.Json.JsonSerializer.Serialize(frc.Result);
                                totalTokens += EstimateTokens(resStr);
                            }
                            break;
                    }
                }
            }
            else if (!string.IsNullOrEmpty(message.Text))
            {
                totalTokens += EstimateTokens(message.Text);
            }
        }

        return hasAnyMessage ? Math.Max(1, totalTokens) : 0;
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    public static string ResolveThinkingEffort(ChatOptions? options)
    {
        if (options?.AdditionalProperties == null)
        {
            return "unknown";
        }
        if (options.AdditionalProperties.TryGetValue("think", out var thinkVal) && thinkVal is bool enabled)
        {
            if (!enabled)
            {
                return "none";
            }
            if (options.AdditionalProperties.TryGetValue("reasoning_effort", out var re) && re != null && !string.IsNullOrWhiteSpace(re.ToString()))
            {
                return re.ToString()!;
            }
            return "default";
        }
        return "unknown";
    }
}
