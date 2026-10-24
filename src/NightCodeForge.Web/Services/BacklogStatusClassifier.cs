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
    public static BacklogKanbanColumn GetColumn(ForgeTask task, int? activeTaskId = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.Status == TaskOverallStatus.Suggested)
            return BacklogKanbanColumn.Suggested;

        if (task.Status == TaskOverallStatus.Ready)
            return BacklogKanbanColumn.ReadyForReview;

        if (task.Status is TaskOverallStatus.Closed or TaskOverallStatus.Rejected or TaskOverallStatus.Failed or TaskOverallStatus.Cancelled)
            return BacklogKanbanColumn.Closed;

        // An actively executing task is In-Flight, unless it already reached a terminal/review state above.
        if (activeTaskId.HasValue && task.Id == activeTaskId.Value)
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

    public static bool IsInFlight(ForgeTask task, int? activeTaskId = null) => GetColumn(task, activeTaskId) == BacklogKanbanColumn.InFlight;
    public static bool IsOpenBacklog(ForgeTask task, int? activeTaskId = null) => GetColumn(task, activeTaskId) == BacklogKanbanColumn.Open;
}
