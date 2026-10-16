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
    /// Reschedules a task to run again, either retrying the current stage or restarting the entire pipeline from scratch.
    /// </summary>
    Task<bool> RescheduleTaskAsync(int taskId, bool restartPipeline = false, CancellationToken cancellationToken = default);
}
