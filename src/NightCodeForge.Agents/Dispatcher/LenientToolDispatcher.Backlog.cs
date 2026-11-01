using System.Text.Json;
using System.Text.RegularExpressions;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Dispatcher;

public partial class LenientToolDispatcher
{
    private static readonly string[] TaskIdKeys = ["task_id", "taskId", "id", "TaskId"];
    private static readonly string[] DependsOnKeys = ["depends_on_ids", "dependsOnIds", "depends_on", "dependsOn", "prerequisites", "prerequisite_ids"];
    private static readonly string[] MergeModeKeys = ["merge_mode", "mergeMode"];
    private static readonly string[] BaseBranchKeys = ["base_branch", "baseBranch"];
    private static readonly string[] PriorityUpdateKeys = ["updates", "priorities", "tasks", "items"];

    [GeneratedRegex(@"-?\d+")]
    private static partial Regex IntegerTokenRegex();

    private void RegisterBacklogTools(CanonicalTools tools)
    {
        RegisterTool(new ToolDefinition(
            CanonicalToolNames.GetTask,
            "Fetch the complete details of a backlog task (task_id)",
            (args, ct) => tools.GetTaskAsync(GetRequiredTaskId(args), ct),
            aliases: ["gettask", "read_task", "show_task"]));

        RegisterTool(new ToolDefinition(
            CanonicalToolNames.EditTask,
            "Edit an unstarted backlog task (task_id, title?, description?, priority?, depends_on_ids?, merge_mode?, base_branch?)",
            (args, ct) => tools.EditTaskAsync(
                GetRequiredTaskId(args),
                GetOptionalString(args, "title"),
                GetOptionalString(args, "description"),
                GetOptionalInt(args, "priority"),
                ExtractIntList(args, DependsOnKeys),
                GetOptionalString(args, MergeModeKeys),
                GetOptionalString(args, BaseBranchKeys),
                ct),
            aliases: ["edittask", "update_task", "updatetask"]));

        RegisterTool(new ToolDefinition(
            CanonicalToolNames.SetTaskPriority,
            "Change the priority tier (1 Critical to 5 Trivial) of an unstarted backlog task (task_id, priority)",
            (args, ct) => tools.SetTaskPriorityAsync(
                GetRequiredTaskId(args),
                GetOptionalInt(args, "priority") ?? throw new ArgumentException("Missing required parameter: 'priority'"),
                ct),
            aliases: ["settaskpriority", "set_priority"]));

        RegisterTool(new ToolDefinition(
            CanonicalToolNames.ReworkPriorities,
            "Change the priority tier of several unstarted backlog tasks (updates: [{ task_id, priority }])",
            (args, ct) => tools.ReworkPrioritiesAsync(ExtractPriorityUpdates(args), ct),
            aliases: ["reworkpriorities", "rework_priority", "set_task_priorities"]));

        RegisterTool(new ToolDefinition(
            CanonicalToolNames.RemoveTask,
            "Remove (cancel) an unstarted backlog task without active dependents (task_id, reason?)",
            (args, ct) => tools.RemoveTaskAsync(GetRequiredTaskId(args), GetOptionalString(args, "reason"), ct),
            aliases: ["removetask", "delete_task", "deletetask", "cancel_task"]));
    }

    private static int GetRequiredTaskId(Dictionary<string, object?> args)
    {
        foreach (var key in TaskIdKeys)
        {
            if (args.TryGetValue(key, out var val) && TryCoerceTaskId(val, out var id))
            {
                return id;
            }
        }

        throw new ArgumentException("Missing required parameter: 'task_id'");
    }

    private static bool TryCoerceTaskId(object? val, out int id)
    {
        id = 0;
        if (val == null)
        {
            return false;
        }

        if (val is JsonElement je && je.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
        {
            return false;
        }

        var direct = CoerceToInt(val);
        if (direct.HasValue)
        {
            id = direct.Value;
            return true;
        }

        var match = IntegerTokenRegex().Match(val is JsonElement el ? el.ToString() : val.ToString() ?? string.Empty);
        return match.Success && int.TryParse(match.Value, out id);
    }

    private static string? GetOptionalString(Dictionary<string, object?> args, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (args.TryGetValue(key, out var val) && val != null)
            {
                if (val is JsonElement je)
                {
                    if (je.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    {
                        continue;
                    }

                    return je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString();
                }

                return val.ToString();
            }
        }

        return null;
    }

    private static IReadOnlyList<int>? ExtractIntList(Dictionary<string, object?> args, string[] keys)
    {
        foreach (var key in keys)
        {
            if (args.TryGetValue(key, out var val) && !IsNullArgumentValue(val))
            {
                return CoerceToIntList(val!);
            }
        }

        return null;
    }

    private static bool IsNullArgumentValue(object? val) =>
        val is null
        || val is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }
        || (val is string s && string.Equals(s.Trim(), "null", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<int> CoerceToIntList(object val)
    {
        var result = new List<int>();
        switch (val)
        {
            case JsonElement { ValueKind: JsonValueKind.Array } array:
                foreach (var item in array.EnumerateArray())
                {
                    if (!TryCoerceTaskId(item, out var itemId))
                    {
                        throw new ArgumentException($"Invalid task ID '{item}' in 'depends_on_ids'. Expected numeric task IDs.");
                    }

                    result.Add(itemId);
                }
                break;
            case string text:
                var trimmed = text.Trim().TrimStart('[').TrimEnd(']').Trim();
                if (trimmed.Length == 0)
                {
                    break;
                }

                foreach (var token in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var unquoted = token.Trim('"', '\'');
                    if (!TryCoerceTaskId(unquoted, out var tokenId))
                    {
                        throw new ArgumentException($"Invalid task ID '{token}' in 'depends_on_ids'. Expected numeric task IDs.");
                    }

                    result.Add(tokenId);
                }
                break;
            case System.Collections.IEnumerable items when val is not string:
                foreach (var item in items)
                {
                    if (!TryCoerceTaskId(item, out var itemId))
                    {
                        throw new ArgumentException($"Invalid task ID '{item}' in 'depends_on_ids'. Expected numeric task IDs.");
                    }

                    result.Add(itemId);
                }
                break;
            default:
                if (!TryCoerceTaskId(val, out var single))
                {
                    throw new ArgumentException($"Invalid task ID '{val}' in 'depends_on_ids'. Expected numeric task IDs.");
                }

                result.Add(single);
                break;
        }

        return result;
    }

    private static IReadOnlyList<TaskPriorityUpdate> ExtractPriorityUpdates(Dictionary<string, object?> args)
    {
        foreach (var key in PriorityUpdateKeys)
        {
            if (args.TryGetValue(key, out var raw) && !IsNullArgumentValue(raw))
            {
                return ParsePriorityUpdates(raw);
            }
        }

        return ParsePriorityUpdates(null);
    }

    /// <summary>Parses the <c>updates</c> argument of rework_priorities (array, JSON element or JSON string).</summary>
    internal static IReadOnlyList<TaskPriorityUpdate> ParsePriorityUpdates(object? raw)
    {
        if (raw == null)
        {
            throw new ArgumentException("Missing required parameter: 'updates'");
        }

        if (raw is string text && !string.IsNullOrWhiteSpace(text))
        {
            using var doc = JsonDocument.Parse(text);
            raw = doc.RootElement.Clone();
        }
        var updates = new List<TaskPriorityUpdate>();
        IEnumerable<object?> items = raw switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().Select(e => (object?)e).ToList(),
            System.Collections.IEnumerable enumerable when raw is not string => enumerable.Cast<object?>().ToList(),
            _ => throw new ArgumentException("Parameter 'updates' must be an array of { task_id, priority } objects.")
        };

        foreach (var item in items)
        {
            var entry = ToDictionary(item)
                ?? throw new ArgumentException("Each entry of 'updates' must be an object with task_id and priority.");
            var priority = GetOptionalInt(entry, "priority")
                ?? throw new ArgumentException("Each entry of 'updates' requires a 'priority'.");
            updates.Add(new TaskPriorityUpdate(GetRequiredTaskId(entry), priority));
        }

        return updates;
    }

    private static Dictionary<string, object?>? ToDictionary(object? item)
    {
        switch (item)
        {
            case JsonElement { ValueKind: JsonValueKind.Object } obj:
                return obj.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.OrdinalIgnoreCase);
            case IDictionary<string, object?> dict:
                return new Dictionary<string, object?>(dict, StringComparer.OrdinalIgnoreCase);
            default:
                return null;
        }
    }
}
