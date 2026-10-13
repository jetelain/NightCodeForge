using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Manages Git repository operations for workspaces, issues, branches, WIP checkpoints, and diffs.
/// </summary>
public interface IGitWorkspaceManager
{
    /// <summary>
    /// Validates that the specified path points to a healthy Git repository working tree.
    /// </summary>
    Task EnsureRepositoryValidAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks out the feature branch for the given issue (feature/issue-{id}).
    /// If the branch does not yet exist, creates it branching from baseBranch.
    /// </summary>
    Task CheckoutIssueBranchAsync(Workspace workspace, int issueId, string baseBranch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages all changes (git add -A) and creates an automated WIP commit with --no-verify.
    /// Returns the 40-character commit SHA hash.
    /// </summary>
    Task<string> CreateWipCheckpointAsync(Workspace workspace, int issueId, string stageName, string summary, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a noise-filtered git diff between baseBranch and feature/issue-{id} with -U2 and lockfile exclusions.
    /// </summary>
    Task<string> GenerateDiffAsync(Workspace workspace, int issueId, string baseBranch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks out baseBranch, performs a squash merge of feature/issue-{id}, and commits the final message.
    /// </summary>
    Task SquashAndMergeAsync(Workspace workspace, int issueId, string baseBranch, string commitMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard resets and cleans the working tree to recover from interrupted or dirty operations.
    /// </summary>
    Task RevertOrCleanAsync(Workspace workspace, CancellationToken cancellationToken = default);
}
