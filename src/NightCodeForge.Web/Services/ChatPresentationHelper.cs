using System.Text.Json;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Presentation service extracting tool icon mapping, payload previews, and role metadata
/// to standardize conversational chat bubble styling across Ideation, Task Inspector, and Transcripts.
/// </summary>
public static class ChatPresentationHelper
{
    public static string GetToolIcon(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return "⚙️";
        return toolName.ToLowerInvariant() switch
        {
            "bash" or "run_command" or "exec" or "cmd" or "shell" => "⚡",
            "read" or "read_file" or "view_file" or "cat" => "📄",
            "write" or "edit" or "write_file" or "replace_file_content" or "multi_replace_file_content" => "✏️",
            "grep_search" or "find_files" or "find" or "search" => "🔍",
            "list_dir" or "ls" or "list_directory" => "📁",
            "web_search" or "search_web" or "read_url_content" => "🌐",
            "propose_tasks" => "📋",
            "ask" or "ask_question" => "❓",
            _ => "⚙️"
        };
    }

    public static string GetToolSummary(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("command", out var cmd)) return $"({Truncate(cmd.GetString(), 45)})";
                if (root.TryGetProperty("CommandLine", out var cl)) return $"({Truncate(cl.GetString(), 45)})";
                if (root.TryGetProperty("path", out var path)) return $"({Truncate(path.GetString(), 45)})";
                if (root.TryGetProperty("AbsolutePath", out var ap)) return $"({Truncate(ap.GetString(), 45)})";
                if (root.TryGetProperty("TargetFile", out var tf)) return $"({Truncate(tf.GetString(), 45)})";
                if (root.TryGetProperty("query", out var query)) return $"(\"{Truncate(query.GetString(), 35)}\")";
                if (root.TryGetProperty("Query", out var q)) return $"(\"{Truncate(q.GetString(), 35)}\")";
                if (root.TryGetProperty("prompt", out var prompt)) return $"(\"{Truncate(prompt.GetString(), 35)}\")";
                if (root.TryGetProperty("Prompt", out var pr)) return $"(\"{Truncate(pr.GetString(), 35)}\")";
            }
        }
        catch (JsonException)
        {
            // If tool call arguments are incomplete or malformed JSON during streaming, return empty preview
        }
        return string.Empty;
    }

    public static string GetRoleIcon(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return "💬";
        return role.ToLowerInvariant() switch
        {
            "user" => "👤",
            "assistant" => "🤖",
            "system" => "⚙️",
            "thinking" => "🧠",
            "tool" or "tool_call" => "⚙️",
            "tool_result" => "↳",
            "tool_error" => "❌",
            "optimization" or "compaction" => "⚡",
            "ask" => "❓",
            _ => "💬"
        };
    }

    public static string GetRoleAuthor(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return "System";
        return role.ToLowerInvariant() switch
        {
            "user" => "User",
            "assistant" => "Agent",
            "system" => "System Directive",
            "thinking" => "Reasoning",
            "tool" or "tool_call" => "Tool Call",
            "tool_result" => "Tool Output",
            "tool_error" => "Tool Error",
            "optimization" or "compaction" => "Optimization",
            "ask" => "Agent Question",
            _ => role
        };
    }

    public static string GetRoleBubbleClass(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return "system";
        return role.ToLowerInvariant() switch
        {
            "user" => "user",
            "assistant" => "assistant",
            "system" => "system",
            "thinking" => "assistant turn-thinking",
            "tool" or "tool_call" or "tool_result" => "assistant turn-tool",
            "tool_error" => "assistant turn-tool error",
            "optimization" or "compaction" => "assistant turn-optimization",
            _ => "assistant"
        };
    }

    public static string Truncate(string? value, int maxLen)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLen ? value : value[..maxLen] + "...";
    }
}
