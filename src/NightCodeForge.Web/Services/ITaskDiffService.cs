using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Service providing file modifications and diffs for tasks across active and closed states.
/// </summary>
public interface ITaskDiffService
{
    /// <summary>
    /// Retrieves comprehensive diff result for a given task, including modified files and provenance.
    /// </summary>
    Task<TaskDiffResult> GetTaskDiffAsync(int taskId, CancellationToken cancellationToken = default);
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

    public Task<TaskDiffResult> GetTaskDiffAsync(int taskId, CancellationToken cancellationToken = default)
    {
        return _taskReviewService.GetTaskDiffAsync(taskId, cancellationToken);
    }
}
