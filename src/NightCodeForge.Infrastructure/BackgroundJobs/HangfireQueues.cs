namespace NightCodeForge.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire queue identifiers for strict concurrency partitioning.
/// </summary>
public static class HangfireQueues
{
    /// <summary>
    /// High-priority queue for human interruption replies, review rework, and crash recovery.
    /// Dequeued before standard inference to minimize developer wait times.
    /// </summary>
    public const string LlmUrgent = "llm-urgent";

    /// <summary>
    /// Standard queue for new backlog tasks and cascade-unblocked DAG tasks.
    /// </summary>
    public const string LlmInference = "llm-inference";

    /// <summary>
    /// Default queue for background utility, Git mirror synchronizations, and maintenance tasks.
    /// </summary>
    public const string Default = "default";
}
