namespace NightCodeForge.Core.Domain;

/// <summary>
/// Execution priority queue tier designated by a workflow stage.
/// </summary>
public enum StageQueueTier
{
    /// <summary>
    /// Tier 3 (Lowest Stage Priority): Task Review, architecture planning, triage, and unstarted backlog tasks.
    /// </summary>
    TaskReview = 1,

    /// <summary>
    /// Tier 2 (Mid Stage Priority): Pre/post deterministic quality gates, implementation turns, and autonomous conflict resolution turns.
    /// </summary>
    Implementation = 2,

    /// <summary>
    /// Tier 1 (Highest Stage Priority): Code Review &amp; Fix, and post-review verification.
    /// </summary>
    CodeReview = 3
}
