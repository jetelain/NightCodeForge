using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Result of a Git mirror push/sync operation to the host repository.
/// </summary>
/// <param name="Success">Whether the push succeeded (either primary branch or fallback branch).</param>
/// <param name="FallbackUsed">Whether the dirty host tree fallback branch (feature/issue-{id}-ncf) was used.</param>
/// <param name="BranchPushed">The actual branch name pushed to host-origin.</param>
/// <param name="WarningMessage">Warning or guidance message if fallback occurred.</param>
public record GitSyncResult(
    bool Success,
    bool FallbackUsed,
    string BranchPushed,
    string? WarningMessage);

/// <summary>
/// Notification interface for Git alerts (e.g. dirty host tree push rejections).
/// </summary>
public interface IGitAlertNotifier
{
    Task NotifyAlertAsync(string title, string message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Synchronizes isolated ext4 repository clones with the developer's host repository.
/// </summary>
public interface IGitMirrorSyncService
{
    /// <summary>
    /// Configures the host repository to accept pushes to the checked-out branch via receive.denyCurrentBranch updateInstead.
    /// </summary>
    Task ConfigureHostRepositoryAsync(string hostRepoPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clones an isolated ext4 working tree, configures host-origin remote pointing to the host repository,
    /// and configures receive.denyCurrentBranch updateInstead on the host.
    /// </summary>
    Task<string> InitializeMirrorAsync(Workspace workspace, string mirrorDirectory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the latest base branch commits from host-origin.
    /// </summary>
    Task FetchFromHostAsync(string mirrorRepoPath, string baseBranch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes the issue branch to host-origin. If the host working tree has uncommitted edits to tracked files,
    /// falls back to pushing to feature/issue-{id}-ncf and raises an alert.
    /// </summary>
    Task<GitSyncResult> PushToHostAsync(string mirrorRepoPath, int issueId, CancellationToken cancellationToken = default);
}
