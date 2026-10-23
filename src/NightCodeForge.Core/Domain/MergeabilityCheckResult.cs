namespace NightCodeForge.Core.Domain;

/// <summary>
/// Structured result of a non-destructive mergeability preview (git merge-tree --write-tree --name-only).
/// </summary>
/// <param name="IsMergeable">True if the feature branch merges cleanly into the base branch without conflicts.</param>
/// <param name="ConflictedFiles">The file paths that would conflict if merged.</param>
/// <param name="BaseBranchSha">The commit SHA of the base branch at preview time.</param>
/// <param name="FeatureBranchSha">The commit SHA of the feature branch at preview time.</param>
public record MergeabilityCheckResult(
    bool IsMergeable,
    IReadOnlyList<string> ConflictedFiles,
    string BaseBranchSha,
    string FeatureBranchSha);
