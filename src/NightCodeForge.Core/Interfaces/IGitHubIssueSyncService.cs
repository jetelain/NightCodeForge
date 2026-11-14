namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Polls configured GitHub repositories for untrusted issues and routes eligible issues into automated triage.
/// </summary>
public interface IGitHubIssueSyncService
{
    /// <summary>
    /// Synchronizes open GitHub issues across all enabled workspaces.
    /// </summary>
    Task SyncOpenIssuesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronizes open GitHub issues for a single workspace.
    /// </summary>
    Task SyncWorkspaceIssuesAsync(int workspaceId, CancellationToken cancellationToken = default);
}
