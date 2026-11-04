using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Resolved host and worktree filesystem paths for a task execution cycle.
/// </summary>
public readonly record struct ResolvedTaskWorkspacePaths(
    string TaskWorkingPath,
    string? HostWorkingPath,
    string? HostRepoPath,
    string? TargetSyncPath);

/// <summary>
/// Result of pre-stage markdown synchronization (read-back on resume + write to disk).
/// </summary>
public readonly record struct PreStageMarkdownSyncOutcome(
    IReadOnlyDictionary<int, TaskBaselineEntry>? BaselineSnapshot,
    bool ModifiedDuringPreResumeSync);

/// <summary>
/// Collaborator encapsulating workspace lookup, mutual exclusion locks, ext4 mirror initialization and sync,
/// feature branch and task worktree preparation, markdown task file syncing, and review diff computation.
/// </summary>
public interface ITaskWorkspacePreparer
{
    Task<IDisposable> AcquireWorkspaceLockAsync(int workspaceId, CancellationToken cancellationToken = default);
    Task<IDisposable> AcquireTaskLockAsync(int workspaceId, int taskId, CancellationToken cancellationToken = default);
    Task<Workspace?> GetWorkspaceAsync(ForgeTask task, CancellationToken cancellationToken = default);
    Task<AgentProfile?> ResolveAgentProfileAsync(Workspace workspace, ForgeTask task, string stageId, CancellationToken cancellationToken = default);

    Task PrepareTaskBranchAndMirrorAsync(
        Workspace workspace,
        ForgeTask task,
        Func<string, string, CancellationToken, Task> onBaseBranchResolved,
        CancellationToken cancellationToken = default);

    ResolvedTaskWorkspacePaths ResolveWorkspacePaths(Workspace workspace, int taskId);

    Task<PreStageMarkdownSyncOutcome> SyncPreStageMarkdownAsync(
        Workspace workspace,
        ForgeTask task,
        PipelineContext pipelineContext,
        IWorkflowStage stage,
        string? targetSyncPath,
        string? hostRepoPath,
        bool isResumedRun,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ForgeTask>?> SyncAllTasksFromDiskAsync(
        Workspace workspace,
        string targetSyncPath,
        string? hostRepoPath,
        IReadOnlyDictionary<int, TaskBaselineEntry>? baselineSnapshot,
        string actor,
        string reason,
        int currentTaskId,
        CancellationToken cancellationToken = default);

    Task SyncTaskToWorktreeSafeAsync(ForgeTask task, string targetSyncPath, CancellationToken cancellationToken = default);
    Task SyncMarkdownTaskSafeAsync(Workspace workspace, ForgeTask task, CancellationToken cancellationToken = default);
    Task SyncTaskMarkdownAndMirrorAsync(ForgeTask task, Workspace workspace, CancellationToken cancellationToken = default);
    Task ArchiveTaskMarkdownSafeAsync(Workspace workspace, ForgeTask task, CancellationToken cancellationToken = default);

    Task<string> CreateWipCheckpointAsync(Workspace workspace, int taskId, string stageId, string message, CancellationToken cancellationToken = default);
    Task<MergeSyncResult> SyncFromBaseBranchAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default);
    Task AbortMergeAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default);
    Task<MergeabilityCheckResult?> CheckMergeableAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default);
    Task PullMirrorFromHostLockedAsync(Workspace workspace, string baseBranch, CancellationToken cancellationToken = default);
    Task PullMirrorFromHostUnlockedAsync(string mirrorPath, string baseBranch, CancellationToken cancellationToken = default);
    Task<string?> GetBranchShaAsync(Workspace workspace, string branchName, CancellationToken cancellationToken = default);
    Task<string> SquashAndMergeAsync(Workspace workspace, int taskId, string baseBranch, string commitMessage, CancellationToken cancellationToken = default);
    Task ResetCanonicalBranchAsync(Workspace workspace, string branchName, string targetSha, CancellationToken cancellationToken = default);
    Task<GitSyncResult> PushBaseBranchToHostAsync(string mirrorPath, string baseBranch, CancellationToken cancellationToken = default);
    Task RemoveWorktreeSafeAsync(Workspace workspace, int taskId, string contextDescription, CancellationToken cancellationToken = default);
    Task CleanupTaskContainerSafeAsync(Workspace workspace, int taskId, string contextDescription, CancellationToken cancellationToken = default);
    Task ResetTaskWorkspaceToBaseAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default);

    Task MaterializeConflictMarkersForImplementationAsync(Workspace workspace, ForgeTask task, PipelineContext pipelineContext, CancellationToken cancellationToken = default);
    Task PopulateReviewDiffsAsync(Workspace workspace, ForgeTask task, PipelineContext pipelineContext, CancellationToken cancellationToken = default);
    Task<string> GenerateDiffStatSafeAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default);
    Task<bool> HasImplementationChangesAsync(Workspace workspace, ForgeTask task, CancellationToken cancellationToken = default);
}

