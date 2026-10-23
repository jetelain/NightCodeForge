namespace NightCodeForge.Core.Domain;

/// <summary>
/// Structured result of a Git worktree branch synchronization operation (git merge baseBranch).
/// </summary>
/// <param name="Success">Whether the synchronization completed cleanly without merge conflicts.</param>
/// <param name="HasConflict">Whether merge conflicts were detected and left in progress in the worktree.</param>
/// <param name="ConflictedFiles">The file paths with unmerged conflicts.</param>
/// <param name="MergeCommitSha">The merge commit SHA if a new merge commit was created or already up to date.</param>
public record MergeSyncResult(
    bool Success,
    bool HasConflict,
    IReadOnlyList<string> ConflictedFiles,
    string? MergeCommitSha);
