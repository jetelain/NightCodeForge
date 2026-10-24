using NightCodeForge.Core.Domain;
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
/// <param name="Priority">Task priority level (1 = Critical, 2 = High, 3 = Normal, 4 = Low, 5 = Trivial).</param>
public record QueuedJobItem(
    string JobId,
    string QueueName,
    int TaskId,
    string TaskTitle,
    string StageId,
    DateTime EnqueuedAt,
    string Status,
    int Priority = TaskPriority.Normal);

/// <summary>
/// Represents a job that was skipped during queue prioritization/reordering (e.g. unresolvable pipeline or stage).
/// </summary>
/// <param name="JobId">Hangfire job identifier.</param>
/// <param name="QueueName">Queue name where the job currently resides.</param>
/// <param name="TaskId">Associated task ID.</param>
/// <param name="Reason">Reason why the job was skipped.</param>
/// <param name="EnqueuedAt">When the job was enqueued.</param>
public record SkippedJobItem(
    string JobId,
    string QueueName,
    int TaskId,
    string Reason,
    DateTime EnqueuedAt);

/// <summary>
/// Consolidated snapshot of all dev-queues, active jobs, and concurrency locks.
/// </summary>
public record DevQueueSnapshot(
    IReadOnlyList<QueuedJobItem> UrgentJobs,
    IReadOnlyList<QueuedJobItem> CodeReviewJobs,
    IReadOnlyList<QueuedJobItem> ImplementationJobs,
    IReadOnlyList<QueuedJobItem> TaskReviewJobs,
    IReadOnlyList<QueuedJobItem> InferenceJobs,
    IReadOnlyList<QueuedJobItem> UtilityJobs,
    IReadOnlyList<QueuedJobItem> ProcessingJobs,
    IReadOnlyList<QueuedJobItem> FailedJobs,
    IReadOnlyList<ActiveLockSnapshot> ActiveLocks,
    IReadOnlyList<SkippedJobItem>? SkippedJobs = null)
{
    public IReadOnlyList<SkippedJobItem> SkippedJobs { get; init; } = SkippedJobs ?? Array.Empty<SkippedJobItem>();

    public DevQueueSnapshot(
        IReadOnlyList<QueuedJobItem> UrgentJobs,
        IReadOnlyList<QueuedJobItem> InferenceJobs,
        IReadOnlyList<QueuedJobItem> UtilityJobs,
        IReadOnlyList<QueuedJobItem> ProcessingJobs,
        IReadOnlyList<QueuedJobItem> FailedJobs,
        IReadOnlyList<ActiveLockSnapshot> ActiveLocks)
        : this(
            UrgentJobs,
            Array.Empty<QueuedJobItem>(),
            Array.Empty<QueuedJobItem>(),
            Array.Empty<QueuedJobItem>(),
            InferenceJobs,
            UtilityJobs,
            ProcessingJobs,
            FailedJobs,
            ActiveLocks,
            Array.Empty<SkippedJobItem>())
    {
    }

    public TimeSpan? OldestUrgentAge => GetOldestAge(UrgentJobs);
    public TimeSpan? OldestCodeReviewAge => GetOldestAge(CodeReviewJobs);
    public TimeSpan? OldestImplementationAge => GetOldestAge(ImplementationJobs);
    public TimeSpan? OldestTaskReviewAge => GetOldestAge(TaskReviewJobs);
    public TimeSpan? OldestInferenceAge => GetOldestAge(InferenceJobs);

    private static TimeSpan? GetOldestAge(IReadOnlyList<QueuedJobItem> jobs)
    {
        if (jobs == null || jobs.Count == 0) return null;
        var oldest = jobs.Min(j => j.EnqueuedAt);
        var age = DateTime.UtcNow - oldest;
        return age > TimeSpan.Zero ? age : TimeSpan.Zero;
    }
}
