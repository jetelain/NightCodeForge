namespace NightCodeForge.Core.Domain;

/// <summary>
/// Type of file system change recorded in a Git diff.
/// </summary>
public enum FileChangeType
{
    Modified,
    Added,
    Deleted,
    Renamed
}

/// <summary>
/// Summary of diff modifications for a specific file.
/// </summary>
public record FileDiffSummary(
    string Path,
    FileChangeType ChangeType,
    int Additions,
    int Deletions,
    string OldContent,
    string NewContent);

/// <summary>
/// Provenance source indicating how a task's diff was generated.
/// </summary>
public enum DiffProvenance
{
    /// <summary>Generated from the live task worktree, including uncommitted, staged, and untracked changes.</summary>
    LiveWorktree,

    /// <summary>Generated from committed branch head refs because the worktree directory was not found or was pruned.</summary>
    BranchHeadFallback,

    /// <summary>Generated from the single squashed merge commit of a closed task.</summary>
    SquashCommit,

    /// <summary>Retrieved directly from task pipeline metadata bag (e.g. synthetic test snapshots).</summary>
    PipelineSnapshot
}

/// <summary>
/// Result of a task diff inspection including modified files, provenance, base branch, and computed metrics.
/// </summary>
public record TaskDiffResult(
    IReadOnlyList<FileDiffSummary> Files,
    DiffProvenance Provenance,
    string BaseBranch,
    string? SquashCommitSha = null)
{
    public int TotalAdditions => Files.Sum(f => f.Additions);
    public int TotalDeletions => Files.Sum(f => f.Deletions);
}
