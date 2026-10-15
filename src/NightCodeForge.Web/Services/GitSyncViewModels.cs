using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Snapshot of the isolated Ext4 mirror repository and its divergence from host-origin.
/// </summary>
public record MirrorStatusSnapshot(
    int WorkspaceId,
    string MirrorPath,
    string HostOriginPath,
    string BaseBranch,
    int CommitsAhead,
    int CommitsBehind,
    DateTime? LastFetchedAt,
    bool IsHostDirty,
    string HostRepoPath = "");

/// <summary>
/// Snapshot of an agent worktree in the workspace.
/// </summary>
public record WorktreeSnapshot(
    string Path,
    int TaskId,
    string BranchName,
    bool IsActive,
    DateTime CreatedAt);

/// <summary>
/// Event model for a host working tree dirty alert.
/// </summary>
public record HostDirtyAlertEvent(
    int TaskId,
    string BranchName,
    DateTime Timestamp);

/// <summary>
/// Web service interface for Git mirror status, worktree tracking, and host reconciliation.
/// </summary>
public interface IGitSyncService
{
    Task<MirrorStatusSnapshot> GetMirrorStatusAsync(Workspace workspace, CancellationToken cancellationToken = default);
    Task<List<WorktreeSnapshot>> GetWorktreesAsync(Workspace workspace, CancellationToken cancellationToken = default);
    Task PruneWorktreesAsync(Workspace workspace, CancellationToken cancellationToken = default);
    Task<GitSyncResult> ReconcileHostBranchAsync(Workspace workspace, int taskId, string branchName, string strategy, CancellationToken cancellationToken = default);
    void RecordFetch(int workspaceId);
}
