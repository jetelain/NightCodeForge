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
    /// Checks out baseBranch, performs a squash merge of feature/task-{id}, and commits the final message.
    /// </summary>
    Task SquashAndMergeAsync(Workspace workspace, int taskId, string baseBranch, string commitMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures that an isolated Git worktree exists for the given task (task-{id}) checked out on feature/task-{id}.
    /// If the worktree or branch does not yet exist, creates it branching from baseBranch.
    /// </summary>
    Task<string> EnsureWorktreeAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default);

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
}
