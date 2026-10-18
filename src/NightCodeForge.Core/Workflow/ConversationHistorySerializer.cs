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

        foreach (var turn in orderedTurns)
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
                    // Compaction metrics/records are not active conversational messages to the LLM
                    break;
            }
        }

        FlushPendingCalls();

        return JsonSerializer.Serialize(items, JsonOptions);
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

    private static string? ExtractCallId(string? metadataJson)
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
}
