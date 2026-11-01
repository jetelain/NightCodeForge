using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

public enum BacklogMutationCardKind
{
    Loaded,
    Updated,
    Reprioritized,
    Removed
}

/// <summary>Per-task priority chip of a reprioritization card (e.g. "#14 P3 -> P2").</summary>
public sealed record BacklogMutationChip(int TaskId, string Label);

/// <summary>Compact, presentation-ready summary of a backlog mutation tool call.</summary>
public sealed record BacklogMutationCard(
    BacklogMutationCardKind Kind,
    string Icon,
    string Heading,
    int? TaskId,
    string? Subtitle,
    IReadOnlyList<string> Highlights,
    IReadOnlyList<BacklogMutationChip> Chips,
    string? Reason,
    bool IsError,
    bool IsRunning,
    string? ErrorMessage);

/// <summary>
/// Parses backlog mutation tool payloads (get_task, edit_task, set_task_priority, rework_priorities, remove_task)
/// into <see cref="BacklogMutationCard"/> view models, keeping Razor components presentation-only.
/// </summary>
public static partial class BacklogMutationCardParser
{
    private const string LoadedIcon = "📖";
    private const string UpdatedIcon = "📝";
    private const string ReprioritizedIcon = "🏷️";
    private const string RemovedIcon = "🗑️";
    private const string ChangeSeparator = "; ";
    private const string UpdatedMarker = " updated (";
    private const string PrioritiesMarker = "): ";

    private static readonly string[] TaskIdKeys = ["task_id", "taskId", "id", "TaskId"];
    private static readonly string[] UpdateKeys = ["updates", "priorities", "tasks", "items"];
    private static readonly string[] ReasonKeys = ["reason", "comment", "summary"];

    public static bool IsBacklogMutationTool(string? toolName) =>
        ResolveBacklogToolName(toolName) != null;

    /// <summary>Returns the card for a backlog tool call, or null when the tool is not a backlog tool.</summary>
    public static BacklogMutationCard? Parse(string? toolName, string? arguments, string? result, bool isError, bool isRunning)
    {
        var name = ResolveBacklogToolName(toolName);
        if (name == null)
        {
            return null;
        }

        var effectiveIsError = isError || IsErrorResult(result);
        using var args = ParseJson(arguments);
        var argsRoot = args?.RootElement;

        return name switch
        {
            CanonicalToolNames.GetTask => BuildLoaded(argsRoot, result, effectiveIsError, isRunning),
            CanonicalToolNames.EditTask => BuildUpdated(argsRoot, result, effectiveIsError, isRunning),
            CanonicalToolNames.SetTaskPriority or CanonicalToolNames.ReworkPriorities => BuildReprioritized(argsRoot, result, effectiveIsError, isRunning),
            CanonicalToolNames.RemoveTask => BuildRemoved(argsRoot, result, effectiveIsError, isRunning),
            _ => null
        };
    }

    private static bool IsErrorResult(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return false;
        }

        var trimmed = result.TrimStart();
        return trimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Error executing tool", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Exception:", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveBacklogToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        var clean = toolName.Trim();
        var colonIdx = clean.IndexOf(':');
        if (colonIdx >= 0) clean = clean[(colonIdx + 1)..];
        var dotIdx = clean.IndexOf('.');
        if (dotIdx >= 0) clean = clean[(dotIdx + 1)..];

        return clean.ToLowerInvariant() switch
        {
            CanonicalToolNames.GetTask or "gettask" or "read_task" or "show_task" => CanonicalToolNames.GetTask,
            CanonicalToolNames.EditTask or "edittask" or "update_task" or "updatetask" => CanonicalToolNames.EditTask,
            CanonicalToolNames.SetTaskPriority or "settaskpriority" or "set_priority" => CanonicalToolNames.SetTaskPriority,
            CanonicalToolNames.ReworkPriorities or "reworkpriorities" or "rework_priority" or "set_task_priorities" => CanonicalToolNames.ReworkPriorities,
            CanonicalToolNames.RemoveTask or "removetask" or "delete_task" or "deletetask" or "cancel_task" => CanonicalToolNames.RemoveTask,
            _ => null
        };
    }

    private static BacklogMutationCard BuildLoaded(JsonElement? args, string? result, bool isError, bool isRunning)
    {
        var taskId = ReadTaskId(args);
        string? title = null;
        string? subtitle = null;

        using var doc = isError ? null : ParseJson(result);
        if (doc?.RootElement.ValueKind == JsonValueKind.Object)
        {
            var root = doc.RootElement;
            taskId = ReadInt(root, "id") ?? taskId;
            title = ReadString(root, "title");
            var priority = ReadInt(root, "priority");
            subtitle = priority.HasValue ? $"{title} — {FormatPriority(priority.Value)}" : title;
        }

        return new BacklogMutationCard(
            BacklogMutationCardKind.Loaded,
            LoadedIcon,
            $"Loaded Task {FormatId(taskId)}",
            taskId,
            subtitle,
            [],
            [],
            null,
            isError,
            isRunning,
            isError ? result : null);
    }

    private static BacklogMutationCard BuildUpdated(JsonElement? args, string? result, bool isError, bool isRunning)
    {
        var taskId = ReadTaskId(args);
        var highlights = new List<string>();

        if (!isError && result != null)
        {
            var start = result.IndexOf(UpdatedMarker, StringComparison.Ordinal);
            var end = result.LastIndexOf(").", StringComparison.Ordinal);
            if (start >= 0 && end > start)
            {
                var changes = result[(start + UpdatedMarker.Length)..end];
                highlights.AddRange(changes
                    .Split(ChangeSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(FormatChange)
                    .Where(h => !string.IsNullOrEmpty(h)));
            }
            else if (result.Contains("unchanged", StringComparison.OrdinalIgnoreCase))
            {
                highlights.Add("No changes (values already matched)");
            }
        }

        if (highlights.Count == 0 && !isError && args is { ValueKind: JsonValueKind.Object } root)
        {
            foreach (var property in root.EnumerateObject().Where(p =>
                p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                && !TaskIdKeys.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
            {
                var formatted = FormatChange(property.Name);
                if (!string.IsNullOrEmpty(formatted))
                {
                    highlights.Add(formatted);
                }
            }
        }

        return new BacklogMutationCard(
            BacklogMutationCardKind.Updated,
            UpdatedIcon,
            $"Updated Task {FormatId(taskId)}",
            taskId,
            null,
            highlights,
            [],
            null,
            isError,
            isRunning,
            isError ? result : null);
    }

    private static BacklogMutationCard BuildReprioritized(JsonElement? args, string? result, bool isError, bool isRunning)
    {
        var chips = new List<BacklogMutationChip>();

        if (!isError && result != null)
        {
            var start = result.IndexOf(PrioritiesMarker, StringComparison.Ordinal);
            if (start >= 0)
            {
                var summary = result[(start + PrioritiesMarker.Length)..].TrimEnd('.');
                foreach (var entry in summary.Split(ChangeSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var match = PriorityEntryRegex().Match(entry);
                    if (match.Success)
                    {
                        chips.Add(new BacklogMutationChip(
                            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                            $"#{match.Groups[1].Value} {match.Groups[2].Value.Trim()}"));
                    }
                }
            }
        }

        if (chips.Count == 0 && args is { ValueKind: JsonValueKind.Object } root)
        {
            if (TryReadArray(root, UpdateKeys, out var updates))
            {
                foreach (var update in updates.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.Object))
                {
                    AddArgsChip(chips, update);
                }
            }
            else
            {
                AddArgsChip(chips, root);
            }
        }

        return new BacklogMutationCard(
            BacklogMutationCardKind.Reprioritized,
            ReprioritizedIcon,
            "Reprioritized Backlog",
            chips.Count == 1 ? chips[0].TaskId : null,
            null,
            [],
            chips,
            null,
            isError,
            isRunning,
            isError ? result : null);
    }

    private static BacklogMutationCard BuildRemoved(JsonElement? args, string? result, bool isError, bool isRunning)
    {
        var taskId = ReadTaskId(args);
        var reason = args is { ValueKind: JsonValueKind.Object } root ? ReadString(root, ReasonKeys) : null;

        return new BacklogMutationCard(
            BacklogMutationCardKind.Removed,
            RemovedIcon,
            $"Removed Task {FormatId(taskId)}",
            taskId,
            null,
            [],
            [],
            string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            isError,
            isRunning,
            isError ? result : null);
    }

    private static void AddArgsChip(List<BacklogMutationChip> chips, JsonElement element)
    {
        var id = ReadTaskId(element);
        var priority = ReadInt(element, "priority");
        if (id.HasValue && priority.HasValue)
        {
            chips.Add(new BacklogMutationChip(id.Value, $"#{id.Value} → P{priority.Value}"));
        }
    }

    private static string FormatChange(string change)
    {
        var trimmed = change.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.Equals("description", StringComparison.OrdinalIgnoreCase))
        {
            return "Description updated";
        }

        var space = trimmed.IndexOf(' ');
        if (space > 0)
        {
            var field = trimmed[..space];
            var detail = trimmed[(space + 1)..];
            return $"{char.ToUpperInvariant(field[0])}{field[1..]}: {detail}";
        }

        return $"{char.ToUpperInvariant(trimmed[0])}{trimmed[1..]} updated";
    }

    private static string FormatPriority(int priority) => $"P{priority} ({TaskPriority.GetLabel(priority)})";

    private static string FormatId(int? taskId) => taskId.HasValue ? $"#{taskId.Value}" : "(unknown)";

    private static int? ReadTaskId(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } root)
        {
            return null;
        }

        foreach (var key in TaskIdKeys)
        {
            var value = ReadInt(root, key);
            if (value.HasValue)
            {
                return value;
            }
        }

        return null;
    }

    private static int? ReadInt(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var value))
        {
            var match = element.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
            if (match.Value.ValueKind == JsonValueKind.Undefined)
            {
                return null;
            }

            value = match.Value;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString()?.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ReadString(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            var match = element.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
            if (match.Value.ValueKind == JsonValueKind.String)
            {
                return match.Value.GetString();
            }
        }

        return null;
    }

    private static bool TryReadArray(JsonElement element, string[] keys, out JsonElement array)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                array = value;
                return true;
            }

            var match = element.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
            if (match.Value.ValueKind == JsonValueKind.Array)
            {
                array = match.Value;
                return true;
            }
        }

        array = default;
        return false;
    }

    /// <summary>Parses a JSON payload; malformed or partial (streaming) payloads yield null and the card degrades gracefully.</summary>
    private static JsonDocument? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^#(\d+):\s*(.+)$")]
    private static partial Regex PriorityEntryRegex();
}

