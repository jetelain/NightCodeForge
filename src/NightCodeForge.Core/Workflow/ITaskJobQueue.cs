using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Abstraction for enqueuing task execution jobs with priority routing.
/// </summary>
public interface ITaskJobQueue
{
    /// <summary>
    /// Enqueues a task with urgent priority (llm-urgent queue) for manual maintainer promotion.
    /// Optionally preserves original enqueue timestamp for queue aging.
    /// </summary>
    string EnqueueUrgent(int taskId, DateTime? originalEnqueuedAt = null);

    /// <summary>
    /// Enqueues a task execution job routed by stage queue tier and task priority.
    /// P1 Critical tasks are routed to llm-urgent regardless of stage tier.
    /// Non-critical tasks are routed according to the stage's designated tier.
    /// Optionally preserves original enqueue timestamp for queue aging.
    /// </summary>
    string EnqueueStage(int taskId, StageQueueTier tier, int priority, DateTime? originalEnqueuedAt = null);

    /// <summary>
    /// Deletes or cancels an enqueued job by its Hangfire job identifier.
    /// </summary>
    bool Delete(string jobId);

    /// <summary>
    /// Checks whether any P1 urgent execution jobs are waiting in the queue.
    /// </summary>
    Task<bool> HasUrgentJobsWaitingAsync(CancellationToken cancellationToken = default);
}
