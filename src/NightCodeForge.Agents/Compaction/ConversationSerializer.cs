using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace NightCodeForge.Agents.Compaction;

/// <summary>
/// Serializes chat messages into structured plain text transcripts for summarization,
/// preventing the model from treating history as an ongoing conversation to continue.
/// Clamps individual tool results to bounded character limits and omits raw thinking traces.
/// </summary>
public static class ConversationSerializer
{
    public const int DefaultToolResultMaxChars = 2000;

    /// <summary>
    /// Serializes a collection of ChatMessages into a formatted transcript.
    /// </summary>
    public static string SerializeConversation(IEnumerable<ChatMessage> messages, int maxToolResultChars = DefaultToolResultMaxChars)
    {
        var parts = new List<string>();

        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.User)
            {
                var text = ExtractTextContent(msg);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (text.StartsWith("[CONTEXT COMPACTION CHECKPOINT]", StringComparison.OrdinalIgnoreCase))
                    {
                        parts.Add($"[Summary Checkpoint]: {text}");
                    }
                    else
                    {
                        parts.Add($"[User]: {text}");
                    }
                }
            }
            else if (msg.Role == ChatRole.Assistant)
            {
                var text = ExtractTextContent(msg);
                var toolCalls = new List<string>();

                foreach (var content in msg.Contents)
                {
                    if (content is FunctionCallContent fcc)
                    {
                        var argsStr = FormatArguments(fcc.Arguments, maxToolResultChars);
                        toolCalls.Add($"{fcc.Name}({argsStr})");
                    }
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add($"[Assistant]: {text}");
                }

                if (toolCalls.Count > 0)
                {
                    parts.Add($"[Assistant tool calls]: {string.Join("; ", toolCalls)}");
                }
            }
            else if (msg.Role == ChatRole.Tool)
            {
                var resultText = ExtractToolResultText(msg);
                if (!string.IsNullOrWhiteSpace(resultText))
                {
                    parts.Add($"[Tool result]: {TruncateForSummary(resultText, maxToolResultChars)}");
                }
            }
            else if (msg.Role == ChatRole.System)
            {
                var text = ExtractTextContent(msg);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add($"[System]: {text}");
                }
            }
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Truncates tool result text to a maximum length, appending a human-readable truncation marker.
    /// </summary>
    public static string TruncateForSummary(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        int truncatedChars = text.Length - maxChars;
        return $"{text[..maxChars]}\n\n[... {truncatedChars} more characters truncated]";
    }

    private static string ExtractTextContent(ChatMessage msg)
    {
        var sb = new StringBuilder();
        foreach (var content in msg.Contents)
        {
            // Omit TextReasoningContent per Pi compaction specification
            if (content is TextContent tc && content is not TextReasoningContent)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(tc.Text);
            }
        }

        return sb.ToString().Trim();
    }

    private static string ExtractToolResultText(ChatMessage msg)
    {
        var sb = new StringBuilder();
        foreach (var content in msg.Contents)
        {
            if (content is FunctionResultContent frc)
            {
                var str = frc.Result?.ToString();
                if (!string.IsNullOrEmpty(str))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(str);
                }
            }
            else if (content is TextContent tc && content is not TextReasoningContent)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(tc.Text);
            }
        }

        return sb.ToString().Trim();
    }

    private static string FormatArguments(IDictionary<string, object?>? arguments, int maxValueChars)
    {
        if (arguments == null || arguments.Count == 0) return string.Empty;
        var parts = new List<string>();
        foreach (var kvp in arguments)
        {
            // Tool arguments (e.g. full file contents for write tools) can be arbitrarily large: clamp them like tool results.
            string serialized;
            if (kvp.Value is string s)
            {
                serialized = $"\"{TruncateForSummary(s, maxValueChars)}\"";
            }
            else if (kvp.Value is JsonElement je && je.ValueKind == JsonValueKind.String)
            {
                serialized = $"\"{TruncateForSummary(je.GetString() ?? string.Empty, maxValueChars)}\"";
            }
            else
            {
                serialized = TruncateForSummary(JsonSerializer.Serialize(kvp.Value), maxValueChars);
            }

            parts.Add($"{kvp.Key}={serialized}");
        }
        return string.Join(", ", parts);
    }
}
