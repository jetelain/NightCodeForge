using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Manages Git repository operations for workspaces, tasks, branches, WIP checkpoints, and diffs.
/// </summary>
public interface IGitWorkspaceManager
{
    /// <summary>
    /// Validates that the specified path points to a healthy Git repository working tree.
    /// </summary>
    Task EnsureRepositoryValidAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks out the feature branch for the given task (feature/task-{id}).
    /// If the branch does not yet exist, creates it branching from baseBranch.
    /// </summary>
    Task CheckoutTaskBranchAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages all changes (git add -A) and creates an automated WIP commit with --no-verify.
    /// Returns the 40-character commit SHA hash.
    /// </summary>
    Task<string> CreateWipCheckpointAsync(Workspace workspace, int taskId, string stageName, string summary, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a noise-filtered git diff between baseBranch and feature/task-{id} with -U2 and lockfile exclusions.
    /// </summary>
    Task<string> GenerateDiffAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a live git diff of uncommitted and committed changes in the task's worktree against baseBranch.
    /// </summary>
    Task<string> GenerateLiveDiffAsync(Workspace workspace, int taskId, string baseBranch = "main", CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a noise-filtered git diff stat between baseBranch and feature/task-{id} with extended width and exclusions.
    /// </summary>
    Task<string> GenerateDiffStatAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a live git diff stat of uncommitted and committed changes in the task's worktree against baseBranch.
    /// </summary>
    Task<string> GenerateLiveDiffStatAsync(Workspace workspace, int taskId, string baseBranch = "main", CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the noise-filtered patch diff (-U2) of a specific commit SHA (e.g. squashed task commit).
    /// </summary>
    Task<string> GetCommitDiffAsync(Workspace workspace, string commitSha, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks out baseBranch, performs a squash merge of feature/task-{id}, and commits the final message.
    /// Returns the 40-character commit SHA of the squashed commit.
    /// </summary>
    Task<string> SquashAndMergeAsync(Workspace workspace, int taskId, string baseBranch, string commitMessage, CancellationToken cancellationToken = default);
    Task<string> SquashAndMergeAsync(Workspace workspace, int taskId, string baseBranch, string commitMessage, bool allowCreateBaseBranch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures that an isolated Git worktree exists for the given task (task-{id}) checked out on feature/task-{id}.
    /// If the worktree or branch does not yet exist, creates it branching from baseBranch.
    /// </summary>
    Task<string> EnsureWorktreeAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies and enforces that both the worktree .git pointer and the canonical clone worktree gitdir
    /// use bidirectional relative paths, ensuring portability across host, WSL, and Docker containers.
    /// </summary>
    /// <param name="workspace">The workspace owning the repository.</param>
    /// <param name="taskId">The task ID identifying the worktree.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task EnsureWorktreeRelativePathsAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the ephemeral Git worktree for the given task and prunes worktree metadata.
    /// </summary>
    Task RemoveWorktreeAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the filesystem or UNC path for the given task's isolated worktree.
    /// </summary>
    string GetWorktreePath(Workspace workspace, int taskId);

    /// <summary>
    /// Hard resets and cleans the working tree to recover from interrupted or dirty operations.
    /// If taskId is specified, cleans the task's worktree; otherwise cleans the canonical repository.
    /// </summary>
    Task RevertOrCleanAsync(Workspace workspace, int? taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard resets and cleans the canonical working tree.
    /// </summary>
    Task RevertOrCleanAsync(Workspace workspace, CancellationToken cancellationToken = default) =>
        RevertOrCleanAsync(workspace, null, cancellationToken);

    /// <summary>
    /// Validates that the installed Git version satisfies the minimum version requirements (Git 2.38+).
    /// </summary>
    Task ValidateGitVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronizes the task worktree from the base branch (git merge baseBranch with diff3).
    /// Safe to call if merge is already in progress (resumes and enumerates unmerged files).
    /// </summary>
    Task<MergeSyncResult> SyncFromBaseBranchAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a non-destructive read-only preview of merging feature/task-{id} into baseBranch via git merge-tree.
    /// </summary>
    Task<MergeabilityCheckResult> CheckMergeableAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Safely aborts an in-progress merge (git merge --abort) in the task worktree if MERGE_HEAD exists.
    /// </summary>
    Task AbortMergeAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the current commit SHA of a branch in the canonical repository.
    /// </summary>
    Task<string> GetBranchShaAsync(Workspace workspace, string branchName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the specified branch in the canonical repository to a target commit SHA (hard reset).
    /// </summary>
    Task ResetCanonicalBranchAsync(Workspace workspace, string baseBranch, string targetSha, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards all uncommitted changes and commits on feature/task-{id}, hard-resetting
    /// the branch directly to the head of the specified baseBranch.
    /// </summary>
    Task ResetTaskBranchToBaseAsync(
        Workspace workspace,
        int taskId,
        string baseBranch,
        CancellationToken cancellationToken = default);
}

