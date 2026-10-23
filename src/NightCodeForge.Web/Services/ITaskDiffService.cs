using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Service providing file modifications and diffs for tasks across active and closed states.
/// </summary>
public interface ITaskDiffService
{
    /// <summary>
    /// Retrieves changed files and diffs for a given task, inspecting either live worktrees or closed squash commits.
    /// </summary>
    Task<IReadOnlyList<FileDiffSummary>> GetTaskDiffFilesAsync(int taskId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of ITaskDiffService delegating to ITaskReviewService.
/// </summary>
public class TaskDiffService : ITaskDiffService
{
    private readonly ITaskReviewService _taskReviewService;

    public TaskDiffService(ITaskReviewService taskReviewService)
    {
        _taskReviewService = taskReviewService ?? throw new ArgumentNullException(nameof(taskReviewService));
    }

    public Task<IReadOnlyList<FileDiffSummary>> GetTaskDiffFilesAsync(int taskId, CancellationToken cancellationToken = default)
    {
        return _taskReviewService.GetTaskDiffFilesAsync(taskId, cancellationToken);
    }
}
