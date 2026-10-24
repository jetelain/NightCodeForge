namespace NightCodeForge.Web.Services;

/// <summary>
/// Service providing dev-queue monitoring, task prioritization, job cancellation,
/// and workspace concurrency lock management.
/// </summary>
public interface IDevQueueService
{
    /// <summary>
    /// Returns a real-time snapshot of all Hangfire queue depths, processing tasks, and active locks.
    /// </summary>
    Task<DevQueueSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Promotes a queued inference task to the head of the llm-urgent priority queue.
    /// </summary>
    Task<bool> PromoteToUrgentAsync(string jobId, int taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Escalates a task to P1 Critical and promotes it immediately to the head of the llm-urgent queue.
    /// Automatically discovers any active or queued job for this task.
    /// </summary>
    Task<bool> PromoteToUrgentAsync(int taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a queued or executing background job.
    /// </summary>
    Task<bool> CancelJobAsync(string jobId, int? taskId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requeues a failed or aborted job back into the execution queue.
    /// </summary>
    Task<bool> RequeueJobAsync(string jobId, int? taskId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forcibly releases an active concurrency lock and purges any dangling .git/index.lock files.
    /// </summary>
    Task<bool> EmergencyReleaseLockAsync(int workspaceId, int? taskId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retries or re-enqueues the current stage of a task without resetting pipeline progress.
    /// </summary>
    Task<bool> RetryCurrentStageAsync(
        int taskId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restarts the task workflow from Stage 1 using the specified Git worktree strategy,
    /// delegating to ITaskOrchestrator.RestartTaskPipelineAsync.
    /// </summary>
    Task<bool> RestartPipelineAsync(
        int taskId,
        Core.Domain.TaskRestartGitMode gitMode = Core.Domain.TaskRestartGitMode.PreserveWork,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-sequences waiting jobs in the Hangfire inference queue strictly by Priority ASC (1 Highest -> 5 Lowest),
    /// dispatching any Priority 1 tasks directly to the llm-urgent queue.
    /// Returns the number of jobs reordered/rescheduled.
    /// </summary>
    Task<int> ReorderInferenceQueueByPriorityAsync(CancellationToken cancellationToken = default);
}
