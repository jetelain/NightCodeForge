namespace NightCodeForge.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire queue identifiers for strict concurrency partitioning.
/// </summary>
public static class HangfireQueues
{
    /// <summary>
    /// High-priority queue for P1 Critical tasks and manual maintainer promotion ('Promote to Urgent').
    /// </summary>
    public const string LlmUrgent = "llm-urgent";

    /// <summary>
    /// Tier 1 Stage Priority queue for Code Review &amp; Fix (Stage 5) and Post-Review Verification (Stage 6).
    /// Tasks are ~90% complete; finishing review advances to Ready/Merge, keeping branches fresh.
    /// </summary>
    public const string LlmCodeReview = "llm-code-review";

    /// <summary>
    /// Tier 2 Stage Priority queue for Pre-Implementation (Stage 2), Implementation (Stage 3),
    /// Post-Implementation Gates (Stage 4), and autonomous conflict resolution turns.
    /// </summary>
    public const string LlmImplementation = "llm-implementation";

    /// <summary>
    /// Tier 3 Stage Priority queue for ForgeTask Review (Stage 1), Triage, and unstarted backlog tasks.
    /// </summary>
    public const string LlmTaskReview = "llm-task-review";

    /// <summary>
    /// Legacy draining queue for in-flight jobs during rolling upgrade. No new jobs enqueued here.
    /// </summary>
    public const string LlmInference = "llm-inference";

    /// <summary>
    /// Default queue for background utility, Git mirror synchronizations, and maintenance tasks.
    /// </summary>
    public const string Default = "default";

    /// <summary>
    /// All queues recognized by NightCodeForge, in priority order including the default utility queue.
    /// </summary>
    public static readonly string[] All =
    [
        LlmUrgent,
        LlmCodeReview,
        LlmImplementation,
        LlmTaskReview,
        LlmInference,
        Default
    ];

    /// <summary>
    /// LLM inference queues in strict priority sequence consumed by NCF-LLM-Inference-Worker.
    /// </summary>
    public static readonly string[] InferenceQueues =
    [
        LlmUrgent,
        LlmCodeReview,
        LlmImplementation,
        LlmTaskReview,
        LlmInference
    ];
}
