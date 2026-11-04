using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Encapsulates low-level Git filesystem recovery operations during startup reconciliation:
/// WSL2 availability verification, stale .git/*.lock purging, ext4 mirror alignment,
/// dirty worktree rescue commits, and relative worktree path normalization.
/// </summary>
public interface IWorkspaceGitRecoveryService
{
    /// <summary>
    /// Verifies that WSL2 is installed and operational when running on Windows.
    /// </summary>
    void VerifyWslAvailability();

    /// <summary>
    /// Executes full Git lock purge, ext4 mirror alignment, dirty worktree rescue, and relative worktree path normalization for a workspace.
    /// </summary>
    Task RecoverWorkspaceAsync(Workspace workspace, CancellationToken cancellationToken = default);

    /// <summary>
    /// Purges stale .git/*.lock files from the specified repository or worktree directory.
    /// </summary>
    void PurgeStaleGitLocks(string repoPath, int workspaceId);

    /// <summary>
    /// Aligns the workspace's ext4 mirror with its host-origin base branch.
    /// </summary>
    Task AlignMirrorWithHostOriginAsync(Workspace workspace, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discovers all active task worktrees for the workspace that may need lock purging or uncommitted change rescue.
    /// </summary>
    Task<IReadOnlyList<string>> GetWorktreesToRescueAsync(Workspace workspace, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits any uncommitted dirty working tree changes as a WIP-RECOVERY commit.
    /// </summary>
    Task RescueUncommittedChangesAsync(string repoPath, CancellationToken cancellationToken = default);
}

