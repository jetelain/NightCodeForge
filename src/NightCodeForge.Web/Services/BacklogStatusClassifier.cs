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
    public static BacklogKanbanColumn GetColumn(ForgeTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.Status == TaskOverallStatus.Suggested)
            return BacklogKanbanColumn.Suggested;

        if (task.Status == TaskOverallStatus.Ready)
            return BacklogKanbanColumn.ReadyForReview;

        if (task.Status is TaskOverallStatus.Closed or TaskOverallStatus.Rejected or TaskOverallStatus.Failed or TaskOverallStatus.Cancelled)
            return BacklogKanbanColumn.Closed;

        if (task.Status is TaskOverallStatus.Reviewed or TaskOverallStatus.Implemented)
            return BacklogKanbanColumn.InFlight;

        if (task.Status == TaskOverallStatus.Open)
        {
            // An Open task is only In-Flight if it has actually started execution
            if (task.HasExecutionRun && !string.IsNullOrWhiteSpace(task.CurrentStageId))
            {
                return BacklogKanbanColumn.InFlight;
            }

            return BacklogKanbanColumn.Open;
        }

        return BacklogKanbanColumn.Open;
    }

    public static bool IsInFlight(ForgeTask task) => GetColumn(task) == BacklogKanbanColumn.InFlight;
    public static bool IsOpenBacklog(ForgeTask task) => GetColumn(task) == BacklogKanbanColumn.Open;
}
