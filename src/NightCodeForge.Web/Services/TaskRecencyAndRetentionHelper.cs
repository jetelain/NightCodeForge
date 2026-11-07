using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Helper for 24-hour recency classification of terminal tasks (Failed and Closed)
/// and 7-day archival retention cutoff filtering for Backlog Kanban and Visual DAG views.
/// </summary>
public static class TaskRecencyAndRetentionHelper
{
    public static readonly TimeSpan RecentTerminalTaskWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan BacklogClosedRetentionWindow = TimeSpan.FromDays(7);

    public const string RecentBadgeText = "⚡ <24h";

    /// <summary>
    /// Checks whether a task is a recent terminal task (Failed or Closed updated within the last 24 hours).
    /// </summary>
    public static bool IsRecentTerminal(ForgeTask? task, DateTime? utcNow = null)
    {
        if (task == null) return false;
        if (task.Status is not (TaskOverallStatus.Failed or TaskOverallStatus.Closed))
            return false;

        var now = utcNow ?? DateTime.UtcNow;
        return task.UpdatedAt >= now - RecentTerminalTaskWindow;
    }

    /// <summary>
    /// Evaluates whether a task should be excluded from Backlog Kanban and DAG views due to 7-day archival cutoff.
    /// Unresolved Failed tasks remain visible. Only terminal Closed, Rejected, and Cancelled tasks older than 7 days are excluded.
    /// </summary>
    public static bool IsExcludedFromBacklog(ForgeTask? task, DateTime? utcNow = null)
    {
        if (task == null) return false;
        if (task.Status == TaskOverallStatus.Failed) return false;

        if (task.Status is TaskOverallStatus.Closed or TaskOverallStatus.Rejected or TaskOverallStatus.Cancelled)
        {
            var now = utcNow ?? DateTime.UtcNow;
            return task.UpdatedAt < now - BacklogClosedRetentionWindow;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the task is retained (visible) in the Backlog Kanban and DAG views.
    /// </summary>
    public static bool IsRetainedInBacklog(ForgeTask? task, DateTime? utcNow = null) =>
        !IsExcludedFromBacklog(task, utcNow);

    /// <summary>
    /// Sorts tasks for the Closed &amp; Archived Kanban column with recent terminal tasks (within 24h, Failed first)
    /// pinned to the top, followed by older terminal tasks.
    /// </summary>
    public static IEnumerable<ForgeTask> OrderClosedColumnTasks(IEnumerable<ForgeTask> tasks, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        return tasks
            .OrderByDescending(t => IsRecentTerminal(t, now))
            .ThenByDescending(t => t.Status == TaskOverallStatus.Failed)
            .ThenByDescending(t => t.UpdatedAt)
            .ThenBy(t => t.Id);
    }

    /// <summary>
    /// Computes a concise human-readable relative time span string (e.g. "12m ago", "3h ago", "2d ago").
    /// </summary>
    public static string FormatRelativeAge(DateTime utcTimestamp, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var elapsed = now - utcTimestamp;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        if (elapsed.TotalMinutes < 1)
            return "just now";
        if (elapsed.TotalMinutes < 60)
            return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalHours < 24)
            return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed.TotalDays < 7)
            return $"{(int)elapsed.TotalDays}d ago";

        return utcTimestamp.ToString("yyyy-MM-dd");
    }
}

