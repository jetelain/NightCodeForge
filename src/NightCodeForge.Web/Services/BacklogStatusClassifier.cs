using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Kanban column classification for backlog tasks.
/// </summary>
public enum BacklogKanbanColumn
{
    Suggested,
    Open,
    InFlight,
    ReadyForReview,
    Closed
}

/// <summary>
/// Presentation service extracting backlog status and Kanban column classification
/// logic to decouple Razor components from business state categorization.
/// </summary>
public static class BacklogStatusClassifier
{
    public static BacklogKanbanColumn GetColumn(ForgeTask task, int? activeTaskId = null, IEnumerable<int>? runningTaskIds = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.Status is TaskOverallStatus.Draft or TaskOverallStatus.Suggested)
            return BacklogKanbanColumn.Suggested;

        if (task.Status == TaskOverallStatus.Ready)
            return BacklogKanbanColumn.ReadyForReview;

        if (task.Status is TaskOverallStatus.Closed or TaskOverallStatus.Rejected or TaskOverallStatus.Failed or TaskOverallStatus.Cancelled)
            return BacklogKanbanColumn.Closed;

        // An actively executing task is In-Flight, unless it already reached a terminal/review state above.
        var isRunning = (activeTaskId.HasValue && task.Id == activeTaskId.Value) ||
                        (runningTaskIds != null && (runningTaskIds is ISet<int> set ? set.Contains(task.Id) : runningTaskIds.Contains(task.Id)));

        if (isRunning)
            return BacklogKanbanColumn.InFlight;

        if (task.Status is TaskOverallStatus.Reviewed or TaskOverallStatus.Implemented)
            return BacklogKanbanColumn.InFlight;

        if (task.Status == TaskOverallStatus.Open)
        {
            // An Open task is In-Flight if it has started execution or has an active job or stage
            if ((task.HasExecutionRun || !string.IsNullOrWhiteSpace(task.LastHangfireJobId)) && !string.IsNullOrWhiteSpace(task.CurrentStageId))
            {
                return BacklogKanbanColumn.InFlight;
            }

            return BacklogKanbanColumn.Open;
        }

        return BacklogKanbanColumn.Open;
    }

    public static BacklogKanbanColumn GetColumn(ForgeTask task, IEnumerable<int>? runningTaskIds) =>
        GetColumn(task, activeTaskId: null, runningTaskIds: runningTaskIds);

    public static bool IsEligibleToRun(ForgeTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsWaitingForHuman)
            return false;

        return task.Status is not (
            TaskOverallStatus.Draft or
            TaskOverallStatus.Suggested or
            TaskOverallStatus.Ready or
            TaskOverallStatus.Closed or
            TaskOverallStatus.Rejected or
            TaskOverallStatus.Failed or
            TaskOverallStatus.Cancelled);
    }

    public static bool IsRunning(ForgeTask task, int? activeTaskId = null, IEnumerable<int>? runningTaskIds = null)
    {
        if (!IsEligibleToRun(task))
            return false;

        return (activeTaskId.HasValue && task.Id == activeTaskId.Value) ||
               (runningTaskIds != null && (runningTaskIds is ISet<int> set ? set.Contains(task.Id) : runningTaskIds.Contains(task.Id)));
    }

    public static bool IsRunning(ForgeTask task, IEnumerable<int>? runningTaskIds) =>
        IsRunning(task, activeTaskId: null, runningTaskIds: runningTaskIds);

    public static bool IsInFlight(ForgeTask task, int? activeTaskId = null, IEnumerable<int>? runningTaskIds = null) =>
        GetColumn(task, activeTaskId, runningTaskIds) == BacklogKanbanColumn.InFlight;

    public static bool IsInFlight(ForgeTask task, IEnumerable<int>? runningTaskIds) =>
        GetColumn(task, activeTaskId: null, runningTaskIds: runningTaskIds) == BacklogKanbanColumn.InFlight;

    public static bool IsOpenBacklog(ForgeTask task, int? activeTaskId = null, IEnumerable<int>? runningTaskIds = null) =>
        GetColumn(task, activeTaskId, runningTaskIds) == BacklogKanbanColumn.Open;

    public static bool IsOpenBacklog(ForgeTask task, IEnumerable<int>? runningTaskIds) =>
        GetColumn(task, activeTaskId: null, runningTaskIds: runningTaskIds) == BacklogKanbanColumn.Open;
}
