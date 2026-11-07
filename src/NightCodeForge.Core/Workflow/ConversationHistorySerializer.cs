using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Serializes and deserializes database-persisted <see cref="ConversationTurn"/> records
/// to and from structured JSON format for lossless agent session rehydration and crash recovery.
/// </summary>
public static class ConversationHistorySerializer
{
    public record SerializedContent(
        string Type,
        string? Text = null,
        string? CallId = null,
        string? Name = null,
        object? Arguments = null,
        string? Result = null,
        string? ProtectedData = null);

    public record SerializedFunctionCall(string? CallId, string Name, object? Arguments);
    public record SerializedFunctionResult(string? CallId, string? Result);

    public record SerializedMessageItem(
        string Role,
        string? Text = null,
        List<SerializedFunctionCall>? FunctionCalls = null,
        List<SerializedFunctionResult>? FunctionResults = null,
        string? AuthorName = null,
        List<SerializedContent>? Contents = null);

    public record CompactionCheckpointMetadata(
        bool IsCompactionCheckpoint = true,
        int? CutTurnIndex = null,
        int? FirstKeptTurnIndex = null,
        string? FirstKeptCallId = null,
        int? KeptRecentMessageCount = null,
        int? KeptRecentTurnCount = null,
        int? KeptRecentGroupCount = null,
        int? SummarizedGroupCount = null,
        int? SummarizedMessageCount = null,
        int? PreCompactionTokens = null,
        int? PostCompactionTokens = null,
        double? DurationMs = null);

    public const string CompactionCheckpointHeader = "[CONTEXT COMPACTION CHECKPOINT]";

    /// <summary>Result injected for a tool call whose execution was interrupted before its result was persisted.</summary>
    public const string InterruptedToolResult = "[Tool execution was interrupted by a host restart; the result is unknown. Verify the workspace state before retrying.]";

    public const string ContentTypeText = "text";
    public const string ContentTypeReasoning = "reasoning";
    public const string ContentTypeCall = "call";
    public const string ContentTypeResult = "result";

    private const string RoleUser = "user";
    private const string RoleAssistant = "assistant";
    private const string RoleTool = "tool";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Returns true when the value is a JSON array produced by <see cref="Serialize"/> (as opposed to an opaque
    /// harness-specific session identifier).
    /// </summary>
    public static bool IsSerializedHistory([NotNullWhen(true)] string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.TrimStart().StartsWith('[');
    }

    /// <summary>
    /// Serializes ordered conversation turns into a structured JSON representation compatible with agent harnesses.
    /// </summary>
    public static string Serialize(IEnumerable<ConversationTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);

        var orderedTurns = turns.OrderBy(t => t.TurnIndex).ToList();
        var items = new List<SerializedMessageItem>();
        int callCounter = 1;
        string? lastCallId = null;
        var pendingCallIds = new List<string>();

        // A tool call without its result (crash between the two) is an invalid history for the model:
        // close every pending call with an explicit "interrupted" result.
        void FlushPendingCalls()
        {
            foreach (var pendingId in pendingCallIds)
            {
                items.Add(new SerializedMessageItem(
                    Role: RoleTool,
                    FunctionResults: new List<SerializedFunctionResult> { new(pendingId, InterruptedToolResult) },
                    Contents: new List<SerializedContent> { new(ContentTypeResult, CallId: pendingId, Result: InterruptedToolResult) }));
            }
            pendingCallIds.Clear();
        }

        void AppendTurn(ConversationTurn turn)
        {
            if (turn.Role != StageTurnRole.Tool && turn.Role != StageTurnRole.ToolResult && turn.Role != StageTurnRole.Optimization)
            {
                FlushPendingCalls();
            }

            switch (turn.Role)
            {
                case StageTurnRole.User:
                    items.Add(new SerializedMessageItem(
                        Role: RoleUser,
                        Text: turn.Content,
                        Contents: new List<SerializedContent>
                        {
                            new(ContentTypeText, Text: turn.Content)
                        }));
                    break;

                case StageTurnRole.Assistant:
                    items.Add(new SerializedMessageItem(
                        Role: RoleAssistant,
                        Text: turn.Content,
                        Contents: new List<SerializedContent>
                        {
                            new(ContentTypeText, Text: turn.Content)
                        }));
                    break;

                case StageTurnRole.Thinking:
                    items.Add(new SerializedMessageItem(
                        Role: RoleAssistant,
                        Contents: new List<SerializedContent>
                        {
                            new(ContentTypeReasoning, Text: turn.ThinkingTrace ?? turn.Content)
                        }));
                    break;

                case StageTurnRole.Tool:
                    var callId = ExtractCallId(turn.MetadataJson) ?? $"call-{callCounter++}";
                    lastCallId = callId;
                    pendingCallIds.Add(callId);
                    object? parsedArgs = null;
                    if (!string.IsNullOrWhiteSpace(turn.ToolArguments))
                    {
                        try
                        {
                            parsedArgs = JsonSerializer.Deserialize<Dictionary<string, object?>>(turn.ToolArguments, JsonOptions);
                        }
                        catch
                        {
                            parsedArgs = turn.ToolArguments;
                        }
                    }

                    items.Add(new SerializedMessageItem(
                        Role: RoleAssistant,
                        FunctionCalls: new List<SerializedFunctionCall>
                        {
                            new(callId, turn.ToolName ?? string.Empty, parsedArgs)
                        },
                        Contents: new List<SerializedContent>
                        {
                            new(ContentTypeCall, CallId: callId, Name: turn.ToolName, Arguments: parsedArgs)
                        }));
                    break;

                case StageTurnRole.ToolResult:
                    var resultCallId = ExtractCallId(turn.MetadataJson) ?? lastCallId ?? $"call-{callCounter++}";
                    pendingCallIds.Remove(resultCallId);
                    items.Add(new SerializedMessageItem(
                        Role: RoleTool,
                        FunctionResults: new List<SerializedFunctionResult>
                        {
                            new(resultCallId, turn.ToolResult)
                        },
                        Contents: new List<SerializedContent>
                        {
                            new(ContentTypeResult, CallId: resultCallId, Result: turn.ToolResult)
                        }));
                    break;

                case StageTurnRole.System:
                    // Notices are replayed as user messages: several chat templates (Qwen, Gemma) reject or
                    // reorder system messages that are not the very first message.
                    items.Add(new SerializedMessageItem(
                        Role: RoleUser,
                        Text: turn.Content,
                        Contents: new List<SerializedContent>
                        {
                            new(ContentTypeText, Text: turn.Content)
                        }));
                    break;

                case StageTurnRole.Optimization:
                    // Compaction metrics/records are handled explicitly above when a compaction checkpoint is present.
                    break;
            }
        }

        int latestCheckpointIdx = -1;
        for (int i = orderedTurns.Count - 1; i >= 0; i--)
        {
            if (IsCompactionCheckpointTurn(orderedTurns[i]))
            {
                latestCheckpointIdx = i;
                break;
            }
        }

        if (latestCheckpointIdx < 0)
        {
            foreach (var turn in orderedTurns)
            {
                AppendTurn(turn);
            }

            FlushPendingCalls();
            return JsonSerializer.Serialize(items, JsonOptions);
        }

        var latestCheckpoint = orderedTurns[latestCheckpointIdx];
        int anchorIdx = orderedTurns.FindIndex(t => t.Role == StageTurnRole.User);
        if (anchorIdx >= 0 && anchorIdx < latestCheckpointIdx)
        {
            AppendTurn(orderedTurns[anchorIdx]);
        }

        FlushPendingCalls();

        var checkpointText = !string.IsNullOrWhiteSpace(latestCheckpoint.Content)
            ? latestCheckpoint.Content
            : CompactionCheckpointHeader;
        items.Add(new SerializedMessageItem(
            Role: RoleUser,
            Text: checkpointText,
            Contents: new List<SerializedContent>
            {
                new(ContentTypeText, Text: checkpointText)
            }));

        int cutListIdx = ResolveCompactionCutListIndex(orderedTurns, anchorIdx, latestCheckpointIdx);
        var postCutTurns = new List<ConversationTurn>();
        for (int i = cutListIdx; i < orderedTurns.Count; i++)
        {
            if (i <= anchorIdx || i == latestCheckpointIdx)
            {
                continue;
            }

            if (orderedTurns[i].Role == StageTurnRole.Optimization)
            {
                continue;
            }

            postCutTurns.Add(orderedTurns[i]);
        }

        // Prevent orphan ToolResult turns at the start of postCutTurns whose matching Tool turn was pre-cut.
        var postCutToolCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in postCutTurns)
        {
            if (t.Role == StageTurnRole.Tool)
            {
                var cid = ExtractCallId(t.MetadataJson);
                if (!string.IsNullOrEmpty(cid))
                {
                    postCutToolCallIds.Add(cid);
                }
            }
        }

        bool seenPostCutToolCall = false;
        foreach (var turn in postCutTurns)
        {
            if (turn.Role == StageTurnRole.Tool)
            {
                seenPostCutToolCall = true;
            }
            else if (turn.Role == StageTurnRole.ToolResult)
            {
                var cid = ExtractCallId(turn.MetadataJson);
                if (!string.IsNullOrEmpty(cid))
                {
                    if (!postCutToolCallIds.Contains(cid))
                    {
                        continue;
                    }
                }
                else if (!seenPostCutToolCall)
                {
                    continue;
                }
            }

            AppendTurn(turn);
        }

        FlushPendingCalls();

        return JsonSerializer.Serialize(items, JsonOptions);
    }

    public static bool IsCompactionCheckpointTurn(ConversationTurn? turn)
    {
        if (turn == null || turn.Role != StageTurnRole.Optimization)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(turn.Content) &&
            turn.Content.TrimStart().StartsWith(CompactionCheckpointHeader, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var metadata = ParseCompactionCheckpointMetadata(turn.MetadataJson);
        return metadata != null && (
            metadata.IsCompactionCheckpoint ||
            metadata.CutTurnIndex.HasValue ||
            metadata.FirstKeptTurnIndex.HasValue ||
            !string.IsNullOrWhiteSpace(metadata.FirstKeptCallId) ||
            metadata.KeptRecentMessageCount.HasValue ||
            metadata.KeptRecentTurnCount.HasValue);
    }

    public static CompactionCheckpointMetadata? ParseCompactionCheckpointMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var root = doc.RootElement;
            bool hasKnownProperty =
                root.TryGetProperty("IsCompactionCheckpoint", out _) ||
                root.TryGetProperty("isCompactionCheckpoint", out _) ||
                root.TryGetProperty("CutTurnIndex", out _) ||
                root.TryGetProperty("cutTurnIndex", out _) ||
                root.TryGetProperty("FirstKeptTurnIndex", out _) ||
                root.TryGetProperty("firstKeptTurnIndex", out _) ||
                root.TryGetProperty("FirstKeptCallId", out _) ||
                root.TryGetProperty("firstKeptCallId", out _) ||
                root.TryGetProperty("KeptRecentMessageCount", out _) ||
                root.TryGetProperty("keptRecentMessageCount", out _) ||
                root.TryGetProperty("KeptRecentTurnCount", out _) ||
                root.TryGetProperty("keptRecentTurnCount", out _);

            if (!hasKnownProperty)
            {
                return null;
            }

            return JsonSerializer.Deserialize<CompactionCheckpointMetadata>(metadataJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int ResolveCompactionCutListIndex(
        IReadOnlyList<ConversationTurn> orderedTurns,
        int anchorIdx,
        int latestCheckpointIdx)
    {
        int minPostAnchorIdx = anchorIdx >= 0 ? anchorIdx + 1 : 0;
        var checkpoint = orderedTurns[latestCheckpointIdx];
        var metadata = ParseCompactionCheckpointMetadata(checkpoint.MetadataJson);
        int rawCutIdx = latestCheckpointIdx;

        if (metadata != null)
        {
            var cutTurnIndex = metadata.CutTurnIndex ?? metadata.FirstKeptTurnIndex;
            if (cutTurnIndex.HasValue)
            {
                int found = -1;
                for (int i = minPostAnchorIdx; i < orderedTurns.Count; i++)
                {
                    if (i != latestCheckpointIdx && orderedTurns[i].TurnIndex >= cutTurnIndex.Value)
                    {
                        found = i;
                        break;
                    }
                }
                rawCutIdx = found >= 0 ? found : latestCheckpointIdx;
            }
            else if (!string.IsNullOrWhiteSpace(metadata.FirstKeptCallId))
            {
                int found = -1;
                for (int i = minPostAnchorIdx; i < latestCheckpointIdx; i++)
                {
                    if (orderedTurns[i].Role == StageTurnRole.Tool &&
                        string.Equals(ExtractCallId(orderedTurns[i].MetadataJson), metadata.FirstKeptCallId, StringComparison.Ordinal))
                    {
                        found = i;
                        break;
                    }
                }
                if (found >= 0)
                {
                    while (found > minPostAnchorIdx &&
                           (orderedTurns[found - 1].Role is StageTurnRole.Thinking or StageTurnRole.Assistant))
                    {
                        found--;
                    }
                    rawCutIdx = found;
                }
            }
            else if ((metadata.KeptRecentTurnCount ?? metadata.KeptRecentMessageCount) is int keptCount and >= 0)
            {
                int remaining = keptCount;
                int cursor = latestCheckpointIdx;
                while (cursor > minPostAnchorIdx && remaining > 0)
                {
                    cursor--;
                    if (orderedTurns[cursor].Role != StageTurnRole.Optimization)
                    {
                        remaining--;
                    }
                }
                rawCutIdx = cursor;
            }
        }

        rawCutIdx = Math.Clamp(rawCutIdx, minPostAnchorIdx, orderedTurns.Count);

        // Ensure we never split a Tool -> ToolResult pair across the cut boundary.
        while (rawCutIdx > minPostAnchorIdx && rawCutIdx < orderedTurns.Count &&
               orderedTurns[rawCutIdx].Role == StageTurnRole.ToolResult)
        {
            var resultCallId = ExtractCallId(orderedTurns[rawCutIdx].MetadataJson);
            if (!string.IsNullOrEmpty(resultCallId))
            {
                int toolIdx = -1;
                for (int i = rawCutIdx - 1; i >= minPostAnchorIdx; i--)
                {
                    if (orderedTurns[i].Role == StageTurnRole.Tool &&
                        string.Equals(ExtractCallId(orderedTurns[i].MetadataJson), resultCallId, StringComparison.Ordinal))
                    {
                        toolIdx = i;
                        break;
                    }
                }
                if (toolIdx >= minPostAnchorIdx)
                {
                    rawCutIdx = toolIdx;
                    while (rawCutIdx > minPostAnchorIdx &&
                           (orderedTurns[rawCutIdx - 1].Role is StageTurnRole.Thinking or StageTurnRole.Assistant))
                    {
                        rawCutIdx--;
                    }
                    break;
                }
            }
            rawCutIdx--;
        }

        return rawCutIdx;
    }

    /// <summary>
    /// Deserializes a JSON string into a list of <see cref="SerializedMessageItem"/>.
    /// Malformed JSON throws a <see cref="JsonException"/> instead of silently producing an empty history.
    /// </summary>
    public static List<SerializedMessageItem> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<SerializedMessageItem>();
        }

        return JsonSerializer.Deserialize<List<SerializedMessageItem>>(json, JsonOptions) ?? new List<SerializedMessageItem>();
    }

    /// <summary>
    /// Completes any pending unanswered tool call (such as 'ask') in the serialized session memory
    /// with the provided human answer as a tool result, followed by an instruction to proceed.
    /// </summary>
    public static string CompletePendingAskToolCall(string? json, string answer, string proceedInstruction = "Please proceed with your plan based on the clarified answers.")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json ?? string.Empty;
        }

        List<SerializedMessageItem> items;
        try
        {
            items = Deserialize(json);
        }
        catch
        {
            return json;
        }

        if (items.Count == 0)
        {
            return json;
        }

        var pendingCallIds = new HashSet<string>();
        var interruptedCallIds = new HashSet<string>();

        foreach (var item in items)
        {
            if (item.FunctionCalls != null)
            {
                foreach (var fc in item.FunctionCalls)
                {
                    if (!string.IsNullOrEmpty(fc.CallId)) pendingCallIds.Add(fc.CallId);
                }
            }
            if (item.Contents != null)
            {
                foreach (var c in item.Contents)
                {
                    if (c.Type == ContentTypeCall && !string.IsNullOrEmpty(c.CallId))
                    {
                        pendingCallIds.Add(c.CallId);
                    }
                    else if (c.Type == ContentTypeResult && !string.IsNullOrEmpty(c.CallId))
                    {
                        if (string.Equals(c.Result, InterruptedToolResult, StringComparison.Ordinal))
                        {
                            interruptedCallIds.Add(c.CallId);
                        }
                        else
                        {
                            pendingCallIds.Remove(c.CallId);
                        }
                    }
                }
            }
            if (item.FunctionResults != null)
            {
                foreach (var fr in item.FunctionResults)
                {
                    if (!string.IsNullOrEmpty(fr.CallId))
                    {
                        if (string.Equals(fr.Result, InterruptedToolResult, StringComparison.Ordinal))
                        {
                            interruptedCallIds.Add(fr.CallId);
                        }
                        else
                        {
                            pendingCallIds.Remove(fr.CallId);
                        }
                    }
                }
            }
        }

        bool modified = false;

        // If any tool calls were marked interrupted, update their results in-place
        if (interruptedCallIds.Count > 0)
        {
            foreach (var item in items)
            {
                if (item.FunctionResults != null)
                {
                    for (int i = 0; i < item.FunctionResults.Count; i++)
                    {
                        var fr = item.FunctionResults[i];
                        if (!string.IsNullOrEmpty(fr.CallId) && interruptedCallIds.Contains(fr.CallId))
                        {
                            item.FunctionResults[i] = new SerializedFunctionResult(fr.CallId, answer);
                            modified = true;
                        }
                    }
                }
                if (item.Contents != null)
                {
                    for (int i = 0; i < item.Contents.Count; i++)
                    {
                        var c = item.Contents[i];
                        if (c.Type == ContentTypeResult && !string.IsNullOrEmpty(c.CallId) && interruptedCallIds.Contains(c.CallId))
                        {
                            item.Contents[i] = new SerializedContent(ContentTypeResult, CallId: c.CallId, Result: answer);
                            modified = true;
                        }
                    }
                }
            }

            foreach (var cid in interruptedCallIds)
            {
                pendingCallIds.Remove(cid);
            }
        }

        if (pendingCallIds.Count > 0)
        {
            foreach (var callId in pendingCallIds)
            {
                items.Add(new SerializedMessageItem(
                    Role: RoleTool,
                    FunctionResults: new List<SerializedFunctionResult> { new(callId, answer) },
                    Contents: new List<SerializedContent> { new(ContentTypeResult, CallId: callId, Result: answer) }));
            }
            modified = true;
        }

        if (modified)
        {
            if (!string.IsNullOrWhiteSpace(proceedInstruction))
            {
                items.Add(new SerializedMessageItem(
                    Role: RoleUser,
                    Text: proceedInstruction,
                    Contents: new List<SerializedContent> { new(ContentTypeText, Text: proceedInstruction) }));
            }

            return JsonSerializer.Serialize(items, JsonOptions);
        }

        return json;
    }

    /// <summary>
    /// Formats a serialized session into a textual prompt representation for harnesses without native MAF history support.
    /// </summary>
    public static string FormatForPrompt(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        var items = Deserialize(json);
        if (items.Count == 0)
        {
            return json;
        }

        var sb = new StringBuilder();
        foreach (var item in items)
        {
            if (item.Contents is { Count: > 0 })
            {
                foreach (var c in item.Contents)
                {
                    switch (c.Type)
                    {
                        case ContentTypeReasoning:
                            sb.AppendLine($"[Thinking]: {c.Text}");
                            break;
                        case ContentTypeText:
                            var prefix = string.Equals(item.Role, "system", StringComparison.OrdinalIgnoreCase)
                                ? "[System Notice]"
                                : (string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "[Assistant]" : "[User]");
                            sb.AppendLine($"{prefix}: {c.Text}");
                            break;
                        case ContentTypeCall:
                            var args = c.Arguments is string s ? s : JsonSerializer.Serialize(c.Arguments, JsonOptions);
                            sb.AppendLine($"[Tool Call]: {c.Name}({args})");
                            break;
                        case ContentTypeResult:
                            sb.AppendLine($"[Tool Result]: {c.Result}");
                            break;
                    }
                }
            }
            else if (!string.IsNullOrEmpty(item.Text))
            {
                sb.AppendLine($"[{item.Role}]: {item.Text}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string? ExtractCallId(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                (doc.RootElement.TryGetProperty("CallId", out var prop) || doc.RootElement.TryGetProperty("callId", out prop)) &&
                prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static string? ExtractCallId(ConversationTurn? turn) => ExtractCallId(turn?.MetadataJson);

    /// <summary>
    /// Identifies any trailing unanswered non-ask tool turns (and any immediately preceding thinking or
    /// tool-call-accompanying assistant turns belonging to that aborted tool call step) that should be
    /// removed when a conversation is cleanly paused.
    /// </summary>
    public static IReadOnlyList<ConversationTurn> GetTrailingInterruptedTurns(IEnumerable<ConversationTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);

        var ordered = turns.OrderBy(t => t.TurnIndex).ToList();
        if (ordered.Count == 0)
        {
            return Array.Empty<ConversationTurn>();
        }

        var answeredCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var turn in ordered)
        {
            if (turn.Role == StageTurnRole.ToolResult)
            {
                var resultCallId = ExtractCallId(turn);
                if (!string.IsNullOrEmpty(resultCallId))
                {
                    answeredCallIds.Add(resultCallId);
                }
            }
        }

        var toRemove = new List<ConversationTurn>();
        int idx = ordered.Count - 1;

        var trailingAssistantTurns = new List<ConversationTurn>();
        while (idx >= 0 && ordered[idx].Role == StageTurnRole.Assistant)
        {
            trailingAssistantTurns.Add(ordered[idx]);
            idx--;
        }

        // In multi-tool steps, ThinkingAndActivityTracingChatClient writes all StageTurnRole.Tool rows
        // before any tool runs, and completed tools write StageTurnRole.ToolResult rows after them.
        // Skip trailing ToolResult/Optimization rows only when they belong to a trailing Tool block.
        int scanIdx = idx;
        while (scanIdx >= 0 && ordered[scanIdx].Role is StageTurnRole.ToolResult or StageTurnRole.Optimization)
        {
            scanIdx--;
        }

        int unansweredToolCount = 0;
        int answeredToolCount = 0;
        if (scanIdx >= 0 && ordered[scanIdx].Role == StageTurnRole.Tool)
        {
            idx = scanIdx;
            while (idx >= 0 && ordered[idx].Role == StageTurnRole.Tool)
            {
                var turn = ordered[idx];
                if (string.Equals(turn.ToolName, "ask", StringComparison.OrdinalIgnoreCase))
                {
                    answeredToolCount++;
                }
                else
                {
                    var callId = ExtractCallId(turn);
                    var isAnswered = (!string.IsNullOrEmpty(callId) && answeredCallIds.Contains(callId)) ||
                                     (string.IsNullOrEmpty(callId) && turn.ToolResult != null);
                    if (!isAnswered)
                    {
                        toRemove.Add(turn);
                        unansweredToolCount++;
                    }
                    else
                    {
                        answeredToolCount++;
                    }
                }
                idx--;
            }
        }

        if (unansweredToolCount > 0 && answeredToolCount == 0)
        {
            if (trailingAssistantTurns.Count > 0)
            {
                toRemove.AddRange(trailingAssistantTurns);
            }

            while (idx >= 0 && (ordered[idx].Role == StageTurnRole.Thinking || ordered[idx].Role == StageTurnRole.Assistant))
            {
                toRemove.Add(ordered[idx]);
                idx--;
            }
        }
        else if (unansweredToolCount == 0 && answeredToolCount == 0 && trailingAssistantTurns.Count == 0)
        {
            while (idx >= 0 && ordered[idx].Role == StageTurnRole.Thinking)
            {
                toRemove.Add(ordered[idx]);
                idx--;
            }
        }

        return toRemove;
    }
}
