using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Model representing a group of tasks for inspector dropdown navigation.
/// </summary>
public record TaskInspectorGroup(string Label, IReadOnlyList<ForgeTask> Tasks);

/// <summary>
/// Categorizes tasks for the Task Inspector dropdown into clean semantic optgroups:
/// Running, Waiting for Human, In Flight (Waiting for Slot), Backlog / Open, and Closed,
/// sorted by Priority ASC, then UpdatedAt/CreatedAt DESC.
/// </summary>
public static class TaskInspectorGroupingHelper
{
    public const string GroupRunning = "🚀 Running";
    public const string GroupWaitingForHuman = "💬 Waiting for Human";
    public const string GroupWaitingForSlot = "⏳ In Flight (Waiting for Slot)";
    public const string GroupOpen = "📋 Backlog / Open";
    public const string GroupClosed = "✅ Closed";

    public static IReadOnlyList<TaskInspectorGroup> GroupTasks(
        IEnumerable<ForgeTask>? tasks,
        HashSet<int>? queuedTaskIds = null,
        int? activeTaskId = null,
        HashSet<int>? runningTaskIds = null)
    {
        if (tasks == null) return Array.Empty<TaskInspectorGroup>();

        var taskList = tasks.ToList();
        var queued = queuedTaskIds ?? new HashSet<int>();
        var runningIds = runningTaskIds ?? new HashSet<int>();

        var running = new List<ForgeTask>();
        var waitingForHuman = new List<ForgeTask>();
        var waitingForSlot = new List<ForgeTask>();
        var open = new List<ForgeTask>();
        var closed = new List<ForgeTask>();

        foreach (var task in taskList)
        {
            if (task.Status is TaskOverallStatus.Closed or TaskOverallStatus.Rejected or TaskOverallStatus.Cancelled)
            {
                closed.Add(task);
            }
            else if (task.IsWaitingForHuman)
            {
                waitingForHuman.Add(task);
            }
            else if ((activeTaskId.HasValue && task.Id == activeTaskId.Value) || runningIds.Contains(task.Id))
            {
                running.Add(task);
            }
            else if (queued.Contains(task.Id) || (task.HasExecutionRun && task.Status != TaskOverallStatus.Open))
            {
                waitingForSlot.Add(task);
            }
            else
            {
                open.Add(task);
            }
        }

        var groups = new List<TaskInspectorGroup>();

        AddGroupIfNotEmpty(groups, GroupRunning, running);
        AddGroupIfNotEmpty(groups, GroupWaitingForHuman, waitingForHuman);
        AddGroupIfNotEmpty(groups, GroupWaitingForSlot, waitingForSlot);
        AddGroupIfNotEmpty(groups, GroupOpen, open);
        AddGroupIfNotEmpty(groups, GroupClosed, closed);

        return groups;
    }

    private static void AddGroupIfNotEmpty(List<TaskInspectorGroup> groups, string label, List<ForgeTask> tasks)
    {
        if (tasks.Count > 0)
        {
            var sorted = tasks
                .OrderBy(t => t.Priority)
                .ThenByDescending(t => t.UpdatedAt)
                .ThenByDescending(t => t.CreatedAt)
                .ToList();
            groups.Add(new TaskInspectorGroup(label, sorted));
        }
    }
}
