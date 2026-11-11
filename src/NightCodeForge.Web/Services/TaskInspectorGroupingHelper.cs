using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Model representing a group of tasks for inspector dropdown navigation.
/// </summary>
public record TaskInspectorGroup(string Label, IReadOnlyList<ForgeTask> Tasks);

/// <summary>
/// Categorizes tasks for the Task Inspector dropdown into clean semantic optgroups:
/// Running, Waiting for Human, Failed, In Flight (Waiting for Slot), Backlog / Open, and Closed,
/// sorted strictly by Task ID ascending.
/// </summary>
public static class TaskInspectorGroupingHelper
{
    public const string GroupRunning = "🚀 Running";
    public const string GroupWaitingForHuman = "💬 Waiting for Human";
    public const string GroupFailed = "❌ Failed";
    public const string GroupWaitingForSlot = "⏳ In Flight (Waiting for Slot)";
    public const string GroupOpen = "📋 Backlog / Open";
    public const string GroupClosed = "✅ Closed";

    /// <summary>
    /// Aggregates all currently running autonomous task IDs across worker status snapshots and telemetry records.
    /// </summary>
    public static HashSet<int> ResolveRunningTaskIds(
        WorkerStatusSnapshot? snapshot,
        IOperationsTelemetryService? telemetryService = null)
    {
        var ids = new HashSet<int>();
        if (snapshot != null)
        {
            if (snapshot.Status == WorkerExecutionStatus.Running &&
                AutonomousTaskClassifier.IsAutonomousTask(snapshot.ActiveTaskId, snapshot.StageId))
            {
                ids.Add(snapshot.ActiveTaskId!.Value);
            }
            if (snapshot.ActiveTasks != null)
            {
                foreach (var t in snapshot.ActiveTasks)
                {
                    if (AutonomousTaskClassifier.IsAutonomousTask(t))
                    {
                        ids.Add(t.TaskId);
                    }
                }
            }
        }

        if (telemetryService != null)
        {
            var telemetryTasks = telemetryService.GetActiveTasks();
            if (telemetryTasks != null)
            {
                foreach (var t in telemetryTasks)
                {
                    if (AutonomousTaskClassifier.IsAutonomousTask(t))
                    {
                        ids.Add(t.TaskId);
                    }
                }
            }
            var singleTelemetry = telemetryService.GetActiveTask();
            if (AutonomousTaskClassifier.IsAutonomousTask(singleTelemetry))
            {
                ids.Add(singleTelemetry.TaskId);
            }
        }

        return ids;
    }

    /// <summary>
    /// Combines an optional primary active task ID and a collection of running task IDs into a unified set.
    /// </summary>
    public static HashSet<int> ResolveRunningTaskIds(int? activeTaskId, IEnumerable<int>? runningTaskIds)
    {
        var ids = new HashSet<int>(runningTaskIds ?? Array.Empty<int>());
        if (activeTaskId.HasValue && activeTaskId.Value > 0)
        {
            ids.Add(activeTaskId.Value);
        }
        return ids;
    }

    /// <summary>
    /// Resolves the primary active running task ID across the worker status snapshot and telemetry service.
    /// </summary>
    public static int? ResolveEffectiveActiveTaskId(
        WorkerStatusSnapshot? snapshot,
        IOperationsTelemetryService? telemetryService = null)
    {
        if (snapshot != null &&
            snapshot.Status == WorkerExecutionStatus.Running &&
            AutonomousTaskClassifier.IsAutonomousTask(snapshot.ActiveTaskId, snapshot.StageId))
        {
            return snapshot.ActiveTaskId;
        }

        var singleTelemetry = telemetryService?.GetActiveTask();
        if (AutonomousTaskClassifier.IsAutonomousTask(singleTelemetry))
        {
            return singleTelemetry.TaskId;
        }

        var runningIds = ResolveRunningTaskIds(snapshot, telemetryService);
        return runningIds.OrderBy(id => id).Cast<int?>().FirstOrDefault();
    }

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
        var failed = new List<ForgeTask>();
        var waitingForSlot = new List<ForgeTask>();
        var open = new List<ForgeTask>();
        var closed = new List<ForgeTask>();

        foreach (var task in taskList)
        {
            if (task.Status is TaskOverallStatus.Closed or TaskOverallStatus.Rejected or TaskOverallStatus.Cancelled)
            {
                closed.Add(task);
            }
            else if (task.Status == TaskOverallStatus.Failed)
            {
                failed.Add(task);
            }
            else if (task.IsWaitingForHuman)
            {
                waitingForHuman.Add(task);
            }
            else if ((activeTaskId.HasValue && task.Id == activeTaskId.Value) || runningIds.Contains(task.Id))
            {
                running.Add(task);
            }
            else if (queued.Contains(task.Id)
                || (task.HasExecutionRun && (task.Status != TaskOverallStatus.Open || !string.IsNullOrWhiteSpace(task.CurrentStageId)))
                || (!string.IsNullOrWhiteSpace(task.LastHangfireJobId) && !string.IsNullOrWhiteSpace(task.CurrentStageId))
                || task.Status is TaskOverallStatus.Reviewed or TaskOverallStatus.Implemented)
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
        AddGroupIfNotEmpty(groups, GroupFailed, failed);
        AddGroupIfNotEmpty(groups, GroupWaitingForSlot, waitingForSlot);
        AddGroupIfNotEmpty(groups, GroupOpen, open);
        AddGroupIfNotEmpty(groups, GroupClosed, closed);

        return groups;
    }

    private static void AddGroupIfNotEmpty(List<TaskInspectorGroup> groups, string label, List<ForgeTask> tasks)
    {
        if (tasks.Count > 0)
        {
            var sorted = tasks.OrderBy(t => t.Id).ToList();
            groups.Add(new TaskInspectorGroup(label, sorted));
        }
    }
}
