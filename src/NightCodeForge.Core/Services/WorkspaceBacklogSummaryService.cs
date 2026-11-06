using System.Globalization;
using System.Text;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Options controlling the compact backlog digest injected into agent prompts.
/// </summary>
public class WorkspaceBacklogDigestOptions
{
    public const int DefaultMaxActiveTasks = 20;

    /// <summary>Maximum number of active tasks listed in the digest.</summary>
    public int MaxActiveTasks { get; set; } = DefaultMaxActiveTasks;
}

/// <summary>
/// Builds a lean, token-optimized digest of the active workspace backlog for agent prompts.
/// </summary>
public interface IWorkspaceBacklogSummaryService
{
    /// <summary>
    /// Builds the <c>&lt;existing_workspace_tasks&gt;</c> block for a workspace. Returns an empty string
    /// when the workspace has no active task to report.
    /// </summary>
    /// <param name="workspaceId">Workspace whose backlog is summarized.</param>
    /// <param name="excludeTaskId">Optional task (typically the one under review) left out of the digest.</param>
    Task<string> BuildDigestAsync(int workspaceId, int? excludeTaskId = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IWorkspaceBacklogSummaryService"/>: active tasks only, ordered by priority then recency, capped.
/// </summary>
public class WorkspaceBacklogSummaryService : IWorkspaceBacklogSummaryService
{
    public const string RootElement = "existing_workspace_tasks";

    private static readonly TaskOverallStatus[] ActiveStatuses =
    {
        TaskOverallStatus.Draft,
        TaskOverallStatus.Suggested,
        TaskOverallStatus.Open,
        TaskOverallStatus.Reviewed,
        TaskOverallStatus.Implemented,
        TaskOverallStatus.Ready
    };

    private readonly ITaskRepository _taskRepository;
    private readonly WorkspaceBacklogDigestOptions _options;

    public WorkspaceBacklogSummaryService(ITaskRepository taskRepository, WorkspaceBacklogDigestOptions options)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.MaxActiveTasks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxActiveTasks must be greater than zero.");
        }
    }

    public async Task<string> BuildDigestAsync(int workspaceId, int? excludeTaskId = null, CancellationToken cancellationToken = default)
    {
        var tasks = await _taskRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);

        var active = tasks
            .Where(t => ActiveStatuses.Contains(t.Status) && t.Id != excludeTaskId)
            .OrderBy(t => t.Priority)
            .ThenByDescending(t => t.UpdatedAt)
            .ThenBy(t => t.Id)
            .ToList();

        if (active.Count == 0)
        {
            return string.Empty;
        }

        var shown = active.Take(_options.MaxActiveTasks).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"<{RootElement}>");
        if (shown.Count < active.Count)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"<!-- Showing top {shown.Count} of {active.Count} active tasks by priority order (P{TaskPriority.Critical} Critical -> P{TaskPriority.Trivial} Trivial). Lower-priority tasks omitted. -->"));
        }
        else
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"<!-- Showing top {shown.Count} of {active.Count} active tasks by priority order (excluding archived/closed tasks) -->"));
        }

        foreach (var task in shown)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- #{task.Id} [P{task.Priority}/{ShortPriorityLabel(task.Priority)} - {task.Status}]: {task.Title}"));
        }

        sb.Append($"</{RootElement}>");
        return sb.ToString();
    }

    private static string ShortPriorityLabel(int priority) => priority switch
    {
        TaskPriority.Critical => nameof(TaskPriority.Critical),
        TaskPriority.High => nameof(TaskPriority.High),
        TaskPriority.Normal => nameof(TaskPriority.Normal),
        TaskPriority.Low => nameof(TaskPriority.Low),
        TaskPriority.Trivial => nameof(TaskPriority.Trivial),
        _ => TaskPriority.GetLabel(priority)
    };
}
