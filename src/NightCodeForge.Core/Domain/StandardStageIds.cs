namespace NightCodeForge.Core.Domain;

/// <summary>
/// Canonical machine identifiers for standard stages across the default workflow pipeline and agent profiles.
/// </summary>
public static class StandardStageIds
{
    public const string TaskReview = "task-review";
    public const string PreImplementationBaseline = "deterministic-pre-impl";
    public const string Implementation = "implementation";
    public const string PostImplementationGates = "deterministic-post-impl";
    public const string CodeReviewAndFix = "code-review-fix";
    public const string PostReviewVerification = "deterministic-post-review";
    public const string Triage = "triage";
    public const string Merge = "merge";
    public const string Ideation = "ideation";
}
