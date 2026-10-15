using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Web.Services;

/// <summary>
/// View model representing an item in a Hangfire background queue.
/// </summary>
/// <param name="JobId">Hangfire job identifier.</param>
/// <param name="QueueName">Queue name ('llm-urgent', 'llm-inference', 'default').</param>
/// <param name="TaskId">Associated ForgeTask ID (or 0 for system utility jobs).</param>
/// <param name="TaskTitle">Human-readable task or job title.</param>
/// <param name="StageId">Current workflow stage or job type.</param>
/// <param name="EnqueuedAt">Timestamp when the job was enqueued.</param>
/// <param name="Status">Display status ('Urgent', 'Queued', 'Running', 'Failed').</param>
public record QueuedJobItem(
    string JobId,
    string QueueName,
    int TaskId,
    string TaskTitle,
    string StageId,
    DateTime EnqueuedAt,
    string Status);

/// <summary>
/// Consolidated snapshot of all dev-queues, active jobs, and concurrency locks.
/// </summary>
public record DevQueueSnapshot(
    IReadOnlyList<QueuedJobItem> UrgentJobs,
    IReadOnlyList<QueuedJobItem> InferenceJobs,
    IReadOnlyList<QueuedJobItem> UtilityJobs,
    IReadOnlyList<QueuedJobItem> ProcessingJobs,
    IReadOnlyList<QueuedJobItem> FailedJobs,
    IReadOnlyList<ActiveLockSnapshot> ActiveLocks);
