using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Status filter modes for the tabular All Tasks page.
/// </summary>
public enum TaskStatusFilter
{
    All,
    Suggested,
    Open,
    InFlight,
    Ready,
    Failed,
    Closed,
    Rejected,
    Cancelled
}

/// <summary>
/// Recency filter modes for the tabular All Tasks page.
/// </summary>
public enum TaskRecencyFilter
{
    AllTime,
    Last24Hours,
    Last7Days,
    OlderThan7Days
}

/// <summary>
/// Sortable columns for the tabular All Tasks page.
/// </summary>
public enum TaskSortColumn
{
    Id,
    Priority,
    Status,
    UpdatedAt,
    CreatedAt
}

/// <summary>
/// Presentation service extracting filtering and sorting logic for the All Tasks list page
/// to ensure clean separation between Razor views and business/presentation operations.
/// </summary>
public static class TaskTableFilterService
{
    public static IReadOnlyList<ForgeTask> FilterAndSort(
        IEnumerable<ForgeTask>? tasks,
        string? searchQuery,
        TaskStatusFilter statusFilter,
        int? priorityFilter,
        TaskRecencyFilter recencyFilter,
        TaskSortColumn sortColumn,
        bool sortAscending,
        DateTime? utcNow = null,
        int? activeTaskId = null)
    {
        if (tasks == null) return Array.Empty<ForgeTask>();

        var query = tasks.AsEnumerable();
        var now = utcNow ?? DateTime.UtcNow;

        // 1. Text search by #ID, title, or description
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var q = searchQuery.Trim();
            if (int.TryParse(q.TrimStart('#'), out var searchId))
            {
                query = query.Where(t =>
                    t.Id == searchId ||
                    t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(t.Description) && t.Description.Contains(q, StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                query = query.Where(t =>
                    t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(t.Description) && t.Description.Contains(q, StringComparison.OrdinalIgnoreCase)));
            }
        }

        // 2. Status filter
        query = statusFilter switch
        {
            TaskStatusFilter.Suggested => query.Where(t => t.Status is TaskOverallStatus.Suggested or TaskOverallStatus.Draft),
            TaskStatusFilter.Open => query.Where(t => BacklogStatusClassifier.IsOpenBacklog(t, activeTaskId)),
            TaskStatusFilter.InFlight => query.Where(t => BacklogStatusClassifier.IsInFlight(t, activeTaskId)),
            TaskStatusFilter.Ready => query.Where(t => t.Status == TaskOverallStatus.Ready),
            TaskStatusFilter.Failed => query.Where(t => t.Status == TaskOverallStatus.Failed),
            TaskStatusFilter.Closed => query.Where(t => t.Status == TaskOverallStatus.Closed),
            TaskStatusFilter.Rejected => query.Where(t => t.Status == TaskOverallStatus.Rejected),
            TaskStatusFilter.Cancelled => query.Where(t => t.Status == TaskOverallStatus.Cancelled),
            _ => query
        };

        // 3. Priority filter (null or 0 means All, 1..5 means P1..P5)
        if (priorityFilter.HasValue && priorityFilter.Value >= 1 && priorityFilter.Value <= 5)
        {
            query = query.Where(t => t.Priority == priorityFilter.Value);
        }

        // 4. Recency filter
        query = recencyFilter switch
        {
            TaskRecencyFilter.Last24Hours => query.Where(t => t.UpdatedAt >= now - TimeSpan.FromHours(24)),
            TaskRecencyFilter.Last7Days => query.Where(t => t.UpdatedAt >= now - TimeSpan.FromDays(7)),
            TaskRecencyFilter.OlderThan7Days => query.Where(t => t.UpdatedAt < now - TimeSpan.FromDays(7)),
            _ => query
        };

        // 5. Deterministic sorting
        IOrderedEnumerable<ForgeTask> orderedQuery;
        if (sortAscending)
        {
            orderedQuery = sortColumn switch
            {
                TaskSortColumn.Id => query.OrderBy(t => t.Id),
                TaskSortColumn.Priority => query.OrderBy(t => t.Priority).ThenBy(t => t.Id),
                TaskSortColumn.Status => query.OrderBy(t => t.Status).ThenBy(t => t.Id),
                TaskSortColumn.UpdatedAt => query.OrderBy(t => t.UpdatedAt).ThenBy(t => t.Id),
                TaskSortColumn.CreatedAt => query.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id),
                _ => query.OrderBy(t => t.Id)
            };
        }
        else
        {
            orderedQuery = sortColumn switch
            {
                TaskSortColumn.Id => query.OrderByDescending(t => t.Id),
                TaskSortColumn.Priority => query.OrderByDescending(t => t.Priority).ThenBy(t => t.Id),
                TaskSortColumn.Status => query.OrderByDescending(t => t.Status).ThenBy(t => t.Id),
                TaskSortColumn.UpdatedAt => query.OrderByDescending(t => t.UpdatedAt).ThenBy(t => t.Id),
                TaskSortColumn.CreatedAt => query.OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id),
                _ => query.OrderByDescending(t => t.Id)
            };
        }

        return orderedQuery.ToList();
    }
}

