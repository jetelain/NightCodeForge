using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// IChatClient middleware (dual-seam transcript logger) modeled after ComicGen's LiveLogChatClient.
/// Placed on the inner IChatClient passed to MAF or agent harnesses.
/// Captures prompt injections, tool schemas, streaming reasoning traces (TextReasoningContent),
/// and tool execution calls to provide complete inspectability of what MAF and the model produce.
/// </summary>
public class ThinkingAndActivityTracingChatClient : DelegatingChatClient
{
    private readonly List<TranscriptEntry> _capturedEntries = new();
    private readonly object _lock = new();
    private readonly Action<TranscriptEntry>? _onEntry;
    private readonly IAgentActivityStreamer? _activityStreamer;
    private readonly string? _sessionId;
    private readonly int _taskId;
    private readonly string _stageId;

    public string? LastCapturedSystemPrompt { get; private set; }
    public IReadOnlyList<string> LastCapturedTools { get; private set; } = Array.Empty<string>();
    public string? DetectedMafConfiguration { get; private set; }

    /// <summary>Separator placed between <c>ChatOptions.Instructions</c> and System messages found in the history.</summary>
    public const string SystemMessageSeparator = "\n\n--- [System message in chat history] ---\n\n";

    /// <summary>Maximum number of model-call snapshots kept in memory (first <see cref="RetainedLeadingModelCalls"/> plus the most recent ones).</summary>
    public const int MaxRetainedModelCalls = 300;
    public const int RetainedLeadingModelCalls = 100;

    private readonly List<ModelCallSnapshot> _modelCalls = new();
    private List<ModelCallMessage> _previousMessages = new();
    private string? _previousInstructions;
    private string? _previousToolsSignature;
    private int _callCount;

    /// <summary>
    /// Every request sent to the model, as seen at this seam (after MAF providers, compaction and any other
    /// middleware placed above this client). Each entry is diffed against the previous request.
    /// </summary>
    public IReadOnlyList<ModelCallSnapshot> ModelCalls
    {
        get
        {
            lock (_lock)
            {
                return _modelCalls.ToList();
            }
        }
    }

    public IReadOnlyList<TranscriptEntry> CapturedEntries
    {
        get
        {
            lock (_lock)
            {
                return _capturedEntries.ToList();
            }
        }
    }

    public ThinkingAndActivityTracingChatClient(IChatClient innerClient)
        : this(innerClient, null, null, null, 0, "execution")
    {
    }

    public ThinkingAndActivityTracingChatClient(
        IChatClient innerClient,
        Action<TranscriptEntry>? onEntry,
        IAgentActivityStreamer? activityStreamer,
        string? sessionId,
        int taskId,
        string stageId)
        : base(innerClient)
    {
        _onEntry = onEntry;
        _activityStreamer = activityStreamer;
        _sessionId = sessionId;
        _taskId = taskId;
        _stageId = stageId;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messages = chatMessages.ToList();
        InspectIncomingRequest(messages, options);

        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        ProcessResponseMessages(response.Messages);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = chatMessages.ToList();
        InspectIncomingRequest(messages, options);

        var reasoningBuilder = new StringBuilder();
        var textBuilder = new StringBuilder();
        var functionCalls = new List<FunctionCallContent>();

        await using var enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
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
            catch (Exception)
            {
                FlushStream(reasoningBuilder, textBuilder, functionCalls);
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
                    if (content is FunctionCallContent fc)
                    {
                        functionCalls.Add(fc);
                    }
                    else if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                    {
                        reasoningBuilder.Append(reasoning.Text);
                        BroadcastStreamDelta(AgentActivityKind.ThinkingDelta, reasoning.Text, cancellationToken);
                    }
                }
            }

            if (!string.IsNullOrEmpty(update.Text))
            {
                textBuilder.Append(update.Text);
                BroadcastStreamDelta(AgentActivityKind.MessageDelta, update.Text, cancellationToken);
            }

            yield return update;
        }

        FlushStream(reasoningBuilder, textBuilder, functionCalls);
    }

    private void InspectIncomingRequest(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        // 1. Inspect Tools Catalog
        if (options?.Tools != null && options.Tools.Count > 0)
        {
            var tools = options.Tools.Select(t => t.Name).ToList();
            LastCapturedTools = tools;
        }

        // 2. Inspect the effective system prompt: ChatOptions.Instructions plus any System message present in the history.
        var instructions = options?.Instructions;
        var systemMessageTexts = messages
            .Where(m => m.Role == ChatRole.System && !string.IsNullOrEmpty(m.Text))
            .Select(m => m.Text)
            .ToList();

        string? systemText = null;
        if (!string.IsNullOrEmpty(instructions) && systemMessageTexts.Count > 0)
        {
            systemText = instructions + SystemMessageSeparator + string.Join(SystemMessageSeparator, systemMessageTexts);
        }
        else if (!string.IsNullOrEmpty(instructions))
        {
            systemText = instructions;
        }
        else if (systemMessageTexts.Count > 0)
        {
            systemText = string.Join(SystemMessageSeparator, systemMessageTexts);
        }

        if (!string.IsNullOrEmpty(systemText))
        {
            LastCapturedSystemPrompt = systemText;

            // Detect MAF provider signatures
            var mafFeatures = new List<string>();
            if (systemText.Contains("Current Tasks:") || systemText.Contains("todo_")) mafFeatures.Add("TodoProvider: active");
            if (systemText.Contains("agent_mode") || systemText.Contains("mode_")) mafFeatures.Add("AgentModeProvider: active");
            if (systemText.Contains("file_memory")) mafFeatures.Add("FileMemory: active");
            if (systemText.Contains("available_skills")) mafFeatures.Add("AgentSkillsProvider: active");

            if (mafFeatures.Count > 0)
            {
                DetectedMafConfiguration = string.Join("; ", mafFeatures);
            }
        }

        // 3. Record the full request (as sent to the model), diffed against the previous one.
        RecordModelCall(messages, options);
    }

    private void RecordModelCall(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var rendered = messages.Select(m => new ModelCallMessage(RoleName(m.Role), DescribeMessage(m))).ToList();

        int prefix = 0;
        while (prefix < rendered.Count && prefix < _previousMessages.Count && rendered[prefix] == _previousMessages[prefix])
        {
            prefix++;
        }
        bool rewritten = _previousMessages.Count > prefix;

        var instructions = options?.Instructions;
        bool instructionsChanged = _callCount == 0 || !string.Equals(instructions, _previousInstructions, StringComparison.Ordinal);

        var toolSnapshots = (options?.Tools ?? new List<AITool>()).Select(DescribeTool).ToList();
        var toolsSignature = string.Join("\n", toolSnapshots.Select(t => $"{t.Name}\u0001{t.Description}\u0001{t.JsonSchema}"));
        bool toolsChanged = _callCount == 0 || !string.Equals(toolsSignature, _previousToolsSignature, StringComparison.Ordinal);

        var optionsSummary = DescribeOptions(options);

        _callCount++;
        var snapshot = new ModelCallSnapshot(
            Index: _callCount,
            Timestamp: DateTime.UtcNow,
            TotalMessages: rendered.Count,
            UnchangedPrefixCount: prefix,
            HistoryRewritten: rewritten,
            Messages: rendered.Skip(prefix).ToList(),
            InstructionsChanged: instructionsChanged,
            Instructions: instructionsChanged ? instructions : null,
            ToolsChanged: toolsChanged,
            ToolCount: toolSnapshots.Count,
            Tools: toolsChanged ? toolSnapshots : null,
            OptionsSummary: optionsSummary);

        _previousMessages = rendered;
        _previousInstructions = instructions;
        _previousToolsSignature = toolsSignature;

        lock (_lock)
        {
            _modelCalls.Add(snapshot);
            // Keep the first calls and the most recent ones; the renderer reports the gap.
            if (_modelCalls.Count > MaxRetainedModelCalls)
            {
                _modelCalls.RemoveAt(RetainedLeadingModelCalls);
            }
        }
    }

    private static string RoleName(ChatRole role) => role.Value;

    /// <summary>Renders every content item of a message (text, reasoning, tool calls and results...).</summary>
    public static string DescribeMessage(ChatMessage message)
    {
        var parts = new List<string>();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning:
                    parts.Add($"[reasoning] {reasoning.Text}");
                    break;
                case TextContent text:
                    parts.Add(text.Text);
                    break;
                case FunctionCallContent call:
                    var args = call.Arguments != null ? SafeSerialize(call.Arguments) : "{}";
                    parts.Add($"[tool call] {call.Name}({args})");
                    break;
                case FunctionResultContent result:
                    var value = result.Result is string s ? s : SafeSerialize(result.Result);
                    parts.Add($"[tool result {result.CallId}] {value}");
                    break;
                default:
                    parts.Add($"[{content.GetType().Name}]");
                    break;
            }
        }

        if (parts.Count == 0 && !string.IsNullOrEmpty(message.Text))
        {
            parts.Add(message.Text);
        }

        return string.Join("\n", parts);
    }

    private static ToolSchemaSnapshot DescribeTool(AITool tool)
    {
        string? schema = null;
        if (tool is AIFunctionDeclaration declaration)
        {
            try { schema = declaration.JsonSchema.GetRawText(); }
            catch (Exception) { /* JsonSchema may be uninitialized or unsupported */ schema = null; }
        }

        return new ToolSchemaSnapshot(tool.Name, tool.Description, schema);
    }

    private static string DescribeOptions(ChatOptions? options)
    {
        if (options == null) return "(no options)";

        var sb = new StringBuilder();
        sb.AppendLine($"model: {options.ModelId ?? "(default)"}");
        if (options.Temperature.HasValue) sb.AppendLine($"temperature: {options.Temperature.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (options.MaxOutputTokens.HasValue) sb.AppendLine($"max_output_tokens: {options.MaxOutputTokens.Value}");
        if (options.ToolMode != null) sb.AppendLine($"tool_mode: {options.ToolMode}");
        if (options.AdditionalProperties is { Count: > 0 })
        {
            sb.AppendLine($"additional_properties: {SafeSerialize(options.AdditionalProperties)}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string SafeSerialize(object? value)
    {
        if (value == null) return "null";
        try { return JsonSerializer.Serialize(value); }
        catch (Exception) { /* Fallback for types that fail System.Text.Json serialization */ return value.ToString() ?? string.Empty; }
    }

    private void ProcessResponseMessages(IEnumerable<ChatMessage>? responseMessages)
    {
        if (responseMessages == null) return;

        foreach (var msg in responseMessages)
        {
            if (msg.Contents != null)
            {
                foreach (var content in msg.Contents)
                {
                    if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                    {
                        AddEntry(new TranscriptEntry(DateTime.UtcNow, "THINKING", reasoning.Text));
                    }
                }
            }

            if (!string.IsNullOrEmpty(msg.Text))
            {
                AddEntry(new TranscriptEntry(DateTime.UtcNow, "ASSISTANT", msg.Text));
            }

            if (msg.Contents != null)
            {
                foreach (var content in msg.Contents)
                {
                    if (content is FunctionCallContent fc)
                    {
                        var args = fc.Arguments != null ? JsonSerializer.Serialize(fc.Arguments) : "{}";
                        AddEntry(new TranscriptEntry(DateTime.UtcNow, "TOOL", string.Empty, fc.Name, args, CallId: fc.CallId));
                    }
                }
            }
        }
    }

    private void FlushStream(StringBuilder reasoningBuilder, StringBuilder textBuilder, List<FunctionCallContent> functionCalls)
    {
        if (reasoningBuilder.Length > 0)
        {
            AddEntry(new TranscriptEntry(DateTime.UtcNow, "THINKING", reasoningBuilder.ToString()));
        }

        if (textBuilder.Length > 0)
        {
            AddEntry(new TranscriptEntry(DateTime.UtcNow, "ASSISTANT", textBuilder.ToString()));
        }

        if (functionCalls.Count > 0)
        {
            foreach (var fc in functionCalls)
            {
                var args = fc.Arguments != null ? JsonSerializer.Serialize(fc.Arguments) : "{}";
                AddEntry(new TranscriptEntry(DateTime.UtcNow, "TOOL", string.Empty, fc.Name, args, CallId: fc.CallId));
            }
        }
    }

    private void AddEntry(TranscriptEntry entry)
    {
        lock (_lock)
        {
            _capturedEntries.Add(entry);
        }
        _onEntry?.Invoke(entry);
    }

    private void BroadcastStreamDelta(AgentActivityKind kind, string content, CancellationToken ct)
    {
        if (_activityStreamer != null && !string.IsNullOrEmpty(_sessionId))
        {
            var evt = new AgentActivityEvent(_sessionId, _taskId, _stageId, kind, content, DateTime.UtcNow);
            try
            {
                var broadcast = _activityStreamer.BroadcastAsync(evt, ct);
                // Observe faults: a failing live feed must not surface as an unobserved task exception.
                _ = broadcast.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception)
            {
                // A failing live feed must never break the model stream.
            }
        }
    }
}
