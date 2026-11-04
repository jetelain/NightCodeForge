using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Default implementation of <see cref="ITaskWorkspacePreparer"/> encapsulating workspace locks,
/// ext4 mirror initialization and sync, feature branch &amp; worktree management, markdown task file syncing,
/// and review diff computation.
/// </summary>
public class TaskWorkspacePreparer : ITaskWorkspacePreparer
{
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkspaceLockProvider _workspaceLockProvider;
    private readonly IGitWorkspaceManager _gitWorkspaceManager;
    private readonly IGitMirrorSyncService _gitMirrorSyncService;
    private readonly ITaskMarkdownSyncService _markdownSyncService;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IExecutionEnvironmentProvider _environmentProvider;
    private readonly ILogger<TaskWorkspacePreparer> _logger;

    public TaskWorkspacePreparer(
        IWorkspaceRepository workspaceRepository,
        IWorkspaceLockProvider workspaceLockProvider,
        IGitWorkspaceManager gitWorkspaceManager,
        IGitMirrorSyncService gitMirrorSyncService,
        ITaskMarkdownSyncService markdownSyncService,
        IWorkspacePathResolver pathResolver,
        IExecutionEnvironmentProvider environmentProvider,
        ILogger<TaskWorkspacePreparer> logger)
    {
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _workspaceLockProvider = workspaceLockProvider ?? throw new ArgumentNullException(nameof(workspaceLockProvider));
        _gitWorkspaceManager = gitWorkspaceManager ?? throw new ArgumentNullException(nameof(gitWorkspaceManager));
        _gitMirrorSyncService = gitMirrorSyncService ?? throw new ArgumentNullException(nameof(gitMirrorSyncService));
        _markdownSyncService = markdownSyncService ?? throw new ArgumentNullException(nameof(markdownSyncService));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _environmentProvider = environmentProvider ?? throw new ArgumentNullException(nameof(environmentProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<IDisposable> AcquireWorkspaceLockAsync(int workspaceId, CancellationToken cancellationToken = default) =>
        _workspaceLockProvider.AcquireLockAsync(workspaceId, cancellationToken);

    public Task<IDisposable> AcquireTaskLockAsync(int workspaceId, int taskId, CancellationToken cancellationToken = default) =>
        _workspaceLockProvider.AcquireTaskLockAsync(workspaceId, taskId, cancellationToken);

    public async Task<Workspace?> GetWorkspaceAsync(ForgeTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.Workspace ?? await _workspaceRepository.GetByIdAsync(task.WorkspaceId, cancellationToken);
    }

    public async Task<AgentProfile?> ResolveAgentProfileAsync(
        Workspace workspace,
        ForgeTask task,
        string stageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(task);

        AgentProfile? profile = null;
        if (!string.IsNullOrWhiteSpace(task.InterruptedAgentProfileId))
        {
            profile = workspace.AgentProfiles?.FirstOrDefault(p =>
                string.Equals(p.Id, task.InterruptedAgentProfileId, StringComparison.OrdinalIgnoreCase));
            task.InterruptedAgentProfileId = null;
        }

        if (profile == null)
        {
            profile = await _workspaceRepository.GetEffectiveAgentProfileAsync(workspace.Id, stageId, cancellationToken);
        }

        if (profile == null)
        {
            profile = workspace.AgentProfiles?.FirstOrDefault(p =>
                string.Equals(p.Id, stageId, StringComparison.OrdinalIgnoreCase) ||
                (p.StageDirectives != null && p.StageDirectives.ContainsKey(stageId)));
        }

        return profile;
    }

    public async Task PrepareTaskBranchAndMirrorAsync(
        Workspace workspace,
        ForgeTask task,
        Func<string, string, CancellationToken, Task> onBaseBranchResolved,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(onBaseBranchResolved);

        if (string.IsNullOrWhiteSpace(task.FeatureBranch))
        {
            task.FeatureBranch = $"feature/task-{task.Id}";
        }

        using (await _workspaceLockProvider.AcquireLockAsync(workspace.Id, cancellationToken))
        {
            var baseBranchResolved = false;

            if (workspace.ExecutionMode != ExecutionMode.BareHost)
            {
                if (string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
                {
                    try
                    {
                        var defaultMirrorDir = _pathResolver.GetAgentWorkingClonePath(workspace.Id);
                        var initializedPath = await _gitMirrorSyncService.InitializeMirrorAsync(workspace, defaultMirrorDir, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(initializedPath))
                        {
                            workspace.Ext4MirrorPath = initializedPath;
                            await _workspaceRepository.UpdateAsync(workspace, cancellationToken);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Automatic ext4 mirror initialization failed for workspace #{WorkspaceId}", workspace.Id);
                    }
                }

                if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
                {
                    try
                    {
                        await _gitMirrorSyncService.ConfigureHostRepositoryAsync(workspace.RepositoryPath, cancellationToken);
                        var resolvedForMirror = await _gitWorkspaceManager.ResolveEffectiveBaseBranchAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(resolvedForMirror) &&
                            !string.Equals(task.BaseBranch, resolvedForMirror, StringComparison.Ordinal))
                        {
                            var previousBaseBranch = task.BaseBranch;
                            task.BaseBranch = resolvedForMirror;
                            task.UpdatedAt = DateTime.UtcNow;
                            await onBaseBranchResolved(previousBaseBranch, resolvedForMirror, cancellationToken);
                        }
                        baseBranchResolved = true;

                        await _gitMirrorSyncService.PullFromHostAsync(workspace.Ext4MirrorPath, task.BaseBranch, cancellationToken);
                    }
                    catch (Exception mex) when (mex is not OperationCanceledException)
                    {
                        _logger.LogWarning(mex, "Ext4 mirror fetch synchronization failed for task #{TaskId}", task.Id);
                    }
                }
            }

            if (!baseBranchResolved)
            {
                var resolvedBaseBranch = await _gitWorkspaceManager.ResolveEffectiveBaseBranchAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
                if (!string.IsNullOrWhiteSpace(resolvedBaseBranch) &&
                    !string.Equals(task.BaseBranch, resolvedBaseBranch, StringComparison.Ordinal))
                {
                    var previousBaseBranch = task.BaseBranch;
                    task.BaseBranch = resolvedBaseBranch;
                    task.UpdatedAt = DateTime.UtcNow;
                    await onBaseBranchResolved(previousBaseBranch, resolvedBaseBranch, cancellationToken);
                }
            }

            await _gitWorkspaceManager.CheckoutTaskBranchAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
        }
    }

    public ResolvedTaskWorkspacePaths ResolveWorkspacePaths(Workspace workspace, int taskId)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var taskWorkingPath = _gitWorkspaceManager.GetWorktreePath(workspace, taskId);
        var hostWorkingPath = !string.IsNullOrWhiteSpace(taskWorkingPath) ? _pathResolver.ToHostPath(taskWorkingPath) : null;
        if (string.IsNullOrWhiteSpace(hostWorkingPath))
        {
            hostWorkingPath = taskWorkingPath;
        }

        var hostRepoPath = !string.IsNullOrWhiteSpace(workspace.RepositoryPath) ? _pathResolver.ToHostPath(workspace.RepositoryPath) : null;
        if (string.IsNullOrWhiteSpace(hostRepoPath))
        {
            hostRepoPath = workspace.RepositoryPath;
        }

        var targetSyncPath = (!string.IsNullOrWhiteSpace(hostWorkingPath) && Directory.Exists(hostWorkingPath))
            ? hostWorkingPath
            : hostRepoPath;

        return new ResolvedTaskWorkspacePaths(taskWorkingPath, hostWorkingPath, hostRepoPath, targetSyncPath);
    }

    public async Task<PreStageMarkdownSyncOutcome> SyncPreStageMarkdownAsync(
        Workspace workspace,
        ForgeTask task,
        PipelineContext pipelineContext,
        IWorkflowStage stage,
        string? targetSyncPath,
        string? hostRepoPath,
        bool isResumedRun,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetSyncPath) || !Directory.Exists(targetSyncPath))
        {
            return new PreStageMarkdownSyncOutcome(null, false);
        }

        IReadOnlyDictionary<int, TaskBaselineEntry>? baselineSnapshot = null;
        bool modifiedDuringPreResumeSync = false;

        try
        {
            if (isResumedRun)
            {
                var preResumeVersion = task.Version;
                var preResumeDescription = task.Description;
                var preResumeTitle = task.Title;
                var preserveActor = AuditActors.GetActorForStage(stage.StageId);
                var preResumeTasks = await _markdownSyncService.SyncAllTasksFromDiskAsync(
                    workspace,
                    targetSyncPath,
                    hostRepoPath,
                    baselineSnapshot: null,
                    preserveActor,
                    TaskSyncConstants.PreserveEditsBeforeResumeReason,
                    currentTaskId: task.Id,
                    cancellationToken: cancellationToken);
                var preResumeTask = preResumeTasks?.FirstOrDefault(t => t.Id == task.Id);
                if (preResumeTask != null)
                {
                    if (preResumeTask.Version > preResumeVersion ||
                        !string.Equals(preResumeTask.Description?.Trim(), preResumeDescription?.Trim(), StringComparison.Ordinal) ||
                        !string.Equals(preResumeTask.Title?.Trim(), preResumeTitle?.Trim(), StringComparison.Ordinal))
                    {
                        modifiedDuringPreResumeSync = true;
                    }

                    CopyTaskMetadata(task, preResumeTask);
                }
            }

            var preSyncResult = await _markdownSyncService.SyncAllTasksToDiskAsync(
                workspace,
                targetSyncPath,
                hostRepoPath,
                cancellationToken);

            if (preSyncResult != null)
            {
                baselineSnapshot = preSyncResult.Baseline;

                if (preSyncResult.WrittenPaths != null && preSyncResult.WrittenPaths.TryGetValue(task.Id, out var currentTaskFile))
                {
                    var relPath = Path.GetRelativePath(targetSyncPath, currentTaskFile).Replace('\\', '/');
                    pipelineContext.DataBag.Set(PipelineDataBagKeys.TaskMarkdownPath, relPath);
                }
            }

            return new PreStageMarkdownSyncOutcome(baselineSnapshot, modifiedDuringPreResumeSync);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TaskMarkdownSyncException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new TaskMarkdownSyncException($"Pre-stage task synchronization failed for task #{task.Id}: {ex.Message}", ex, taskId: task.Id);
        }
    }

    public async Task<IReadOnlyList<ForgeTask>?> SyncAllTasksFromDiskAsync(
        Workspace workspace,
        string targetSyncPath,
        string? hostRepoPath,
        IReadOnlyDictionary<int, TaskBaselineEntry>? baselineSnapshot,
        string actor,
        string reason,
        int currentTaskId,
        CancellationToken cancellationToken = default) =>
        await _markdownSyncService.SyncAllTasksFromDiskAsync(
            workspace,
            targetSyncPath,
            hostRepoPath,
            baselineSnapshot,
            actor,
            reason,
            currentTaskId: currentTaskId,
            cancellationToken: cancellationToken);

    public async Task SyncTaskToWorktreeSafeAsync(ForgeTask task, string targetSyncPath, CancellationToken cancellationToken = default)
    {
        try
        {
            await _markdownSyncService.SyncTaskToDiskAsync(task, targetSyncPath, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed syncing authoritative task #{TaskId} markdown to worktree '{TargetSyncPath}'", task.Id, targetSyncPath);
        }
    }

    public async Task SyncMarkdownTaskSafeAsync(Workspace workspace, ForgeTask task, CancellationToken cancellationToken = default)
    {
        var hostRepoPath = !string.IsNullOrWhiteSpace(workspace.RepositoryPath) ? _pathResolver.ToHostPath(workspace.RepositoryPath) : null;
        if (string.IsNullOrWhiteSpace(hostRepoPath))
        {
            hostRepoPath = workspace.RepositoryPath;
        }
        if (!string.IsNullOrWhiteSpace(hostRepoPath))
        {
            try
            {
                await _markdownSyncService.SyncTaskToDiskAsync(task, hostRepoPath, cancellationToken: cancellationToken);
            }
            catch (Exception mex) when (mex is not OperationCanceledException)
            {
                _logger.LogWarning(mex, "Failed to mirror task #{TaskId} to docs/tasks/*.md", task.Id);
            }
        }
    }

    public async Task SyncTaskMarkdownAndMirrorAsync(ForgeTask task, Workspace workspace, CancellationToken cancellationToken = default)
    {
        await SyncMarkdownTaskSafeAsync(workspace, task, cancellationToken);

        if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            try
            {
                await _gitMirrorSyncService.PushToHostAsync(workspace.Ext4MirrorPath, task.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed pushing ext4 mirror changes for task #{TaskId} to host repository", task.Id);
            }
        }
    }

    public async Task ArchiveTaskMarkdownSafeAsync(Workspace workspace, ForgeTask task, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(workspace.RepositoryPath))
        {
            try
            {
                await _markdownSyncService.ArchiveOrCleanupTaskFileAsync(task, workspace.RepositoryPath, archive: true, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed archiving markdown file for closed task #{TaskId}", task.Id);
            }
        }
    }

    public Task<string> CreateWipCheckpointAsync(Workspace workspace, int taskId, string stageId, string message, CancellationToken cancellationToken = default) =>
        _gitWorkspaceManager.CreateWipCheckpointAsync(workspace, taskId, stageId, message, cancellationToken);

    public Task<MergeSyncResult> SyncFromBaseBranchAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) =>
        _gitWorkspaceManager.SyncFromBaseBranchAsync(workspace, taskId, baseBranch, cancellationToken);

    public Task AbortMergeAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default) =>
        _gitWorkspaceManager.AbortMergeAsync(workspace, taskId, cancellationToken);

    public Task<MergeabilityCheckResult?> CheckMergeableAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) =>
        _gitWorkspaceManager.CheckMergeableAsync(workspace, taskId, baseBranch, cancellationToken)!;

    public async Task PullMirrorFromHostLockedAsync(Workspace workspace, string baseBranch, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            using (await _workspaceLockProvider.AcquireLockAsync(workspace.Id, cancellationToken))
            {
                await _gitMirrorSyncService.PullFromHostAsync(workspace.Ext4MirrorPath, baseBranch, cancellationToken);
            }
        }
    }

    public Task PullMirrorFromHostUnlockedAsync(string mirrorPath, string baseBranch, CancellationToken cancellationToken = default) =>
        _gitMirrorSyncService.PullFromHostAsync(mirrorPath, baseBranch, cancellationToken);

    public Task<string?> GetBranchShaAsync(Workspace workspace, string branchName, CancellationToken cancellationToken = default) =>
        _gitWorkspaceManager.GetBranchShaAsync(workspace, branchName, cancellationToken)!;

    public Task<string> SquashAndMergeAsync(Workspace workspace, int taskId, string baseBranch, string commitMessage, CancellationToken cancellationToken = default) =>
        _gitWorkspaceManager.SquashAndMergeAsync(workspace, taskId, baseBranch, commitMessage, cancellationToken);

    public Task ResetCanonicalBranchAsync(Workspace workspace, string branchName, string targetSha, CancellationToken cancellationToken = default) =>
        _gitWorkspaceManager.ResetCanonicalBranchAsync(workspace, branchName, targetSha, cancellationToken);

    public Task<GitSyncResult> PushBaseBranchToHostAsync(string mirrorPath, string baseBranch, CancellationToken cancellationToken = default) =>
        _gitMirrorSyncService.PushBaseBranchToHostAsync(mirrorPath, baseBranch, cancellationToken);

    public async Task RemoveWorktreeSafeAsync(Workspace workspace, int taskId, string contextDescription, CancellationToken cancellationToken = default)
    {
        try
        {
            await _gitWorkspaceManager.RemoveWorktreeAsync(workspace, taskId, cancellationToken);
        }
        catch (Exception wex) when (wex is not OperationCanceledException)
        {
            _logger.LogWarning(wex, "Failed to remove worktree for {Context} task #{TaskId}", contextDescription, taskId);
        }
    }

    public async Task CleanupTaskContainerSafeAsync(Workspace workspace, int taskId, string contextDescription, CancellationToken cancellationToken = default)
    {
        try
        {
            await _environmentProvider.CleanupTaskContainerAsync(workspace, taskId, cancellationToken);
        }
        catch (Exception cex) when (cex is not OperationCanceledException)
        {
            _logger.LogWarning(cex, "Failed to clean up container for {Context} task #{TaskId}", contextDescription, taskId);
        }
    }

    public async Task ResetTaskWorkspaceToBaseAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default)
    {
        await _environmentProvider.CleanupTaskContainerAsync(workspace, taskId, cancellationToken);
        await _gitWorkspaceManager.ResetTaskBranchToBaseAsync(workspace, taskId, baseBranch, cancellationToken);
    }

    public async Task MaterializeConflictMarkersForImplementationAsync(
        Workspace workspace,
        ForgeTask task,
        PipelineContext pipelineContext,
        CancellationToken cancellationToken = default)
    {
        var conflictFiles = pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.ConflictFiles);
        if (conflictFiles is { Count: > 0 })
        {
            _logger.LogInformation("Materializing conflict markers before Implementation Agent runs for task #{TaskId}", task.Id);
            var conflictSync = await _gitWorkspaceManager.SyncFromBaseBranchAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
            if (conflictSync.HasConflict)
            {
                // An empty list means the agent already staged its resolutions in a previous attempt:
                // keep the original list so the deterministic verification still runs.
                if (conflictSync.ConflictedFiles.Count > 0)
                {
                    pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictFiles, conflictSync.ConflictedFiles.ToList());
                }
            }
            else
            {
                _logger.LogInformation("Conflict previously reported for task #{TaskId} is no longer present; clearing conflict keys", task.Id);
                pipelineContext.DataBag.Remove(PipelineDataBagKeys.ConflictFiles);
                pipelineContext.DataBag.Remove(PipelineDataBagKeys.ConflictBaseBranch);
                pipelineContext.DataBag.Remove(PipelineDataBagKeys.ConflictDetectedAt);
            }
        }
    }

    public async Task PopulateReviewDiffsAsync(
        Workspace workspace,
        ForgeTask task,
        PipelineContext pipelineContext,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var diffStat = await _gitWorkspaceManager.GenerateDiffStatAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
            pipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewDiffStat, diffStat);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed generating review diff stat for task #{TaskId}", task.Id);
        }

        try
        {
            var liveDiff = await _gitWorkspaceManager.GenerateLiveDiffAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
            if (!string.IsNullOrWhiteSpace(liveDiff) && liveDiff.Length <= WorkflowDefaults.MaxInlineReviewDiffChars)
            {
                pipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewDiff, liveDiff.Trim());
            }
            else
            {
                pipelineContext.DataBag.Remove(PipelineDataBagKeys.ReviewDiff);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed generating live diff for review stage for task #{TaskId}", task.Id);
        }
    }

    public async Task<string> GenerateDiffStatSafeAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _gitWorkspaceManager.GenerateDiffStatAsync(workspace, taskId, baseBranch, cancellationToken) ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed generating review diff stat for task #{TaskId}", taskId);
            return string.Empty;
        }
    }

    public async Task<bool> HasImplementationChangesAsync(Workspace workspace, ForgeTask task, CancellationToken cancellationToken = default)
    {
        string? diffStat = null;
        try
        {
            diffStat = await _gitWorkspaceManager.GenerateLiveDiffStatAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
        }
        catch (WorktreeNotFoundException ex)
        {
            _logger.LogDebug(ex, "Live worktree for task #{TaskId} not found while evaluating implementation diff; falling back to branch diff stat.", task.Id);
        }

        if (diffStat == null)
        {
            diffStat = await _gitWorkspaceManager.GenerateDiffStatAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
        }

        if (diffStat == null)
        {
            return true;
        }

        return ImplementationDiffEvaluator.HasProductionChanges(diffStat);
    }

    internal static void CopyTaskMetadata(ForgeTask target, ForgeTask source)
    {
        target.Title = source.Title;
        target.Description = source.Description;
        target.Priority = source.Priority;
        target.MergeMode = source.MergeMode;
        target.BaseBranch = source.BaseBranch;
        target.FeatureBranch = source.FeatureBranch;
        target.Origin = source.Origin;
        target.UpdatedAt = source.UpdatedAt;
        target.Version = source.Version;
        target.Dependencies = source.Dependencies;
    }
}
