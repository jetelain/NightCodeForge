using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Default implementation of <see cref="ITaskMergeAndCascadeCoordinator"/> handling DAG readiness,
/// conflict resolution verification and initiation, squash-and-merge execution, and downstream DAG cascade unblocking.
/// </summary>
public class TaskMergeAndCascadeCoordinator : ITaskMergeAndCascadeCoordinator
{
    private readonly ITaskRepository _taskRepository;
    private readonly ITaskWorkspacePreparer _workspacePreparer;
    private readonly IDependencyResolutionEngine _dependencyEngine;
    private readonly ITaskDependencyStore _dependencyStore;
    private readonly ITaskJobQueue _jobQueue;
    private readonly IWorkflowPipelineProvider _pipelineProvider;
    private readonly IConflictResolutionVerifier _conflictResolutionVerifier;
    private readonly IAgentActivityStreamer _streamer;
    private readonly ILogger<TaskMergeAndCascadeCoordinator> _logger;

    public TaskMergeAndCascadeCoordinator(
        ITaskRepository taskRepository,
        ITaskWorkspacePreparer workspacePreparer,
        IDependencyResolutionEngine dependencyEngine,
        ITaskDependencyStore dependencyStore,
        ITaskJobQueue jobQueue,
        IWorkflowPipelineProvider pipelineProvider,
        IConflictResolutionVerifier conflictResolutionVerifier,
        IAgentActivityStreamer streamer,
        ILogger<TaskMergeAndCascadeCoordinator> logger)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _workspacePreparer = workspacePreparer ?? throw new ArgumentNullException(nameof(workspacePreparer));
        _dependencyEngine = dependencyEngine ?? throw new ArgumentNullException(nameof(dependencyEngine));
        _dependencyStore = dependencyStore ?? throw new ArgumentNullException(nameof(dependencyStore));
        _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
        _pipelineProvider = pipelineProvider ?? throw new ArgumentNullException(nameof(pipelineProvider));
        _conflictResolutionVerifier = conflictResolutionVerifier ?? throw new ArgumentNullException(nameof(conflictResolutionVerifier));
        _streamer = streamer ?? throw new ArgumentNullException(nameof(streamer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<IWorkflowPipeline> GetPipelineAsync(ForgeTask task, CancellationToken cancellationToken = default) =>
        _pipelineProvider.GetPipelineAsync(task, cancellationToken);

    public Task<bool> IsReadyForExecutionAsync(int taskId, CancellationToken cancellationToken = default) =>
        _dependencyEngine.IsReadyForExecutionAsync(taskId, cancellationToken);

    public async Task<bool> EnsureReadyForExecutionAsync(ForgeTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var isReady = await _dependencyEngine.IsReadyForExecutionAsync(task.Id, cancellationToken);
        if (isReady)
        {
            return true;
        }

        var isWaiting = await _dependencyStore.IsTaskWaitingForHumanAsync(task.Id, cancellationToken);
        var status = await _dependencyStore.GetTaskStatusAsync(task.Id, cancellationToken);
        string reason = isWaiting
            ? "waiting for human clarification input"
            : (status != TaskOverallStatus.Open && status != TaskOverallStatus.Reviewed && status != TaskOverallStatus.Implemented)
                ? $"not in an executable status (Status: {status})"
                : "waiting on prerequisite tasks before execution";

        _logger.LogInformation("ForgeTask #{TaskId} is not ready for execution ({Reason}); waiting.", task.Id, reason);
        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Information,
            Message = $"ForgeTask #{task.Id} is {reason}.",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);

        return false;
    }

    public Task UpdateTaskStatusInStoreAsync(int taskId, TaskOverallStatus status, CancellationToken cancellationToken = default) =>
        _dependencyStore.UpdateTaskStatusAsync(taskId, status, cancellationToken);

    public Task SetWaitingForHumanInStoreAsync(int taskId, bool isWaiting, CancellationToken cancellationToken = default) =>
        _dependencyStore.SetWaitingForHumanAsync(taskId, isWaiting, cancellationToken);

    public string EnqueueStage(int taskId, StageQueueTier tier, int priority, DateTime? originalEnqueuedAt = null) =>
        _jobQueue.EnqueueStage(taskId, tier, priority, originalEnqueuedAt);

    public Task<bool> HasUrgentJobsWaitingAsync(CancellationToken cancellationToken = default) =>
        _jobQueue.HasUrgentJobsWaitingAsync(cancellationToken);

    public async Task<ConflictVerificationOutcome> VerifyAndHandleImplementationConflictsAsync(
        ForgeTask task,
        Workspace workspace,
        PipelineContext pipelineContext,
        IWorkflowPipeline pipeline,
        CancellationToken cancellationToken = default)
    {
        if (pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.ConflictFiles) is not { Count: > 0 } activeConflictFiles)
        {
            return ConflictVerificationOutcome.VerifiedOrNone;
        }

        var verifierResult = await _conflictResolutionVerifier.VerifyResolutionAsync(
            workspace,
            task.Id,
            task.BaseBranch,
            activeConflictFiles,
            pipelineContext.DataBag,
            cancellationToken);

        if (!verifierResult.IsSuccess)
        {
            var attempts = (pipelineContext.DataBag.Get<int?>(PipelineDataBagKeys.ConflictResolutionAttempts) ?? 0) + 1;
            pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictResolutionAttempts, attempts);

            var totalAttempts = (pipelineContext.DataBag.Get<int?>(PipelineDataBagKeys.TotalConflictResolutionAttempts) ?? 0) + 1;
            pipelineContext.DataBag.Set(PipelineDataBagKeys.TotalConflictResolutionAttempts, totalAttempts);

            if (attempts >= ConflictResolutionConstants.MaxConflictResolutionAttempts ||
                totalAttempts >= ConflictResolutionConstants.MaxTotalConflictResolutionAttempts)
            {
                _logger.LogWarning("Conflict resolution budget exhausted for task #{TaskId} ({Attempts} immediate attempts, {TotalAttempts} total attempts). Escalating to maintainer.",
                    task.Id, attempts, totalAttempts);

                task.IsWaitingForHuman = true;
                task.InterruptedStageId = StandardStageIds.Implementation;
                pipelineContext.SyncDataBagToTask();

                await _taskRepository.UpdateWithHistoryAsync(
                    task,
                    dependsOn: null,
                    changedBy: AuditActors.TaskOrchestrator,
                    reason: ConflictResolutionConstants.AuditConflictResolutionBudgetExhausted,
                    cancellationToken: cancellationToken);
                await _dependencyStore.SetWaitingForHumanAsync(task.Id, true, cancellationToken);
                await _workspacePreparer.SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

                await _taskRepository.AddExecutionLogAsync(new ExecutionLog
                {
                    TaskId = task.Id,
                    Level = LogLevel.Warning,
                    Message = ConflictResolutionConstants.AuditConflictResolutionBudgetExhausted,
                    Timestamp = DateTime.UtcNow
                }, cancellationToken);

                return ConflictVerificationOutcome.EscalatedToHuman;
            }

            var errorToReport = !string.IsNullOrWhiteSpace(verifierResult.CompilerErrors)
                ? verifierResult.CompilerErrors
                : verifierResult.ErrorMessage;

            if (!string.IsNullOrWhiteSpace(errorToReport))
            {
                pipelineContext.DataBag.Set(PipelineDataBagKeys.CompilerErrors, new List<string> { errorToReport });
            }
            else
            {
                pipelineContext.DataBag.Remove(PipelineDataBagKeys.CompilerErrors);
            }

            // Preserve worktree state and MERGE_HEAD; re-enqueue to Implementation directly without incrementing quality gate retries
            task.CurrentStageId = StandardStageIds.Implementation;
            task.Status = TaskOverallStatus.Open;
            pipelineContext.SyncDataBagToTask();

            var retryMsg = $"Conflict resolution verification failed ({verifierResult.ErrorMessage}). Looping back to '{StandardStageIds.Implementation}' (attempt {attempts}/{ConflictResolutionConstants.MaxConflictResolutionAttempts}, total: {totalAttempts}/{ConflictResolutionConstants.MaxTotalConflictResolutionAttempts}).";
            _logger.LogWarning("Task #{TaskId}: {Message}", task.Id, retryMsg);

            await _taskRepository.UpdateWithHistoryAsync(
                task,
                dependsOn: null,
                changedBy: AuditActors.TaskOrchestrator,
                reason: retryMsg,
                cancellationToken: cancellationToken);
            await _dependencyStore.UpdateTaskStatusAsync(task.Id, task.Status, cancellationToken);
            await _workspacePreparer.SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

            await _taskRepository.AddExecutionLogAsync(new ExecutionLog
            {
                TaskId = task.Id,
                Level = LogLevel.Warning,
                Message = retryMsg,
                Timestamp = DateTime.UtcNow
            }, cancellationToken);

            var implementationStage = pipeline.GetStage(StandardStageIds.Implementation)
                ?? throw new WorkflowValidationException($"Task #{task.Id} workflow does not contain target implementation stage '{StandardStageIds.Implementation}' for conflict resolution loopback.");

            task.LastHangfireJobId = _jobQueue.EnqueueStage(task.Id, implementationStage.QueueTier, task.Priority);
            await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
            return ConflictVerificationOutcome.LoopedBackToImplementation;
        }

        // Verified successfully!
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictResolutionAttempts, 0);
        pipelineContext.DataBag.Remove(PipelineDataBagKeys.ConflictFiles);
        pipelineContext.DataBag.Remove(PipelineDataBagKeys.ConflictBaseBranch);
        pipelineContext.DataBag.Remove(PipelineDataBagKeys.ConflictDetectedAt);
        pipelineContext.DataBag.Remove(PipelineDataBagKeys.CompilerErrors);
        pipelineContext.DataBag.Set(PipelineDataBagKeys.HasResolvedConflict, true);
        pipelineContext.SyncDataBagToTask();

        var resolvedMsg = string.Format(ConflictResolutionConstants.AuditConflictResolvedByAgentFormat, activeConflictFiles.Count, string.Join(", ", activeConflictFiles));
        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.TaskOrchestrator,
            reason: resolvedMsg,
            cancellationToken: cancellationToken);

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Information,
            Message = resolvedMsg,
            Timestamp = DateTime.UtcNow
        }, cancellationToken);

        return ConflictVerificationOutcome.VerifiedOrNone;
    }

    public async Task<Gate2MergeabilityOutcome> CheckAndSyncGate2MergeabilityAsync(
        ForgeTask task,
        Workspace workspace,
        PipelineContext pipelineContext,
        string stageId,
        CancellationToken cancellationToken = default)
    {
        // 1. Commit any pending worktree output
        await _workspacePreparer.CreateWipCheckpointAsync(
            workspace,
            task.Id,
            stageId,
            "WIP checkpoint before Gate 2 mergeability check",
            cancellationToken);

        // Refresh base branch
        await _workspacePreparer.PullMirrorFromHostLockedAsync(workspace, task.BaseBranch, cancellationToken);

        var mergeCheck = await _workspacePreparer.CheckMergeableAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
        if (mergeCheck != null && !mergeCheck.IsMergeable)
        {
            _logger.LogWarning("Gate 2 detected merge conflict for task #{TaskId} with '{BaseBranch}' in {Count} files",
                task.Id, task.BaseBranch, mergeCheck.ConflictedFiles.Count);
            await InitiateConflictResolutionAsync(task, workspace, pipelineContext, mergeCheck.ConflictedFiles, cancellationToken);
            return new Gate2MergeabilityOutcome(DispatchedConflictResolution: true, BaseHadAdvanced: false);
        }

        // Mergeable: sync from base branch in worktree
        var syncResult = await _workspacePreparer.SyncFromBaseBranchAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
        if (syncResult != null && syncResult.HasConflict)
        {
            await InitiateConflictResolutionAsync(task, workspace, pipelineContext, syncResult.ConflictedFiles, cancellationToken);
            return new Gate2MergeabilityOutcome(DispatchedConflictResolution: true, BaseHadAdvanced: false);
        }

        var baseHadAdvanced = syncResult != null && syncResult.MergeCommitSha != null && mergeCheck != null && syncResult.MergeCommitSha != mergeCheck.FeatureBranchSha;
        return new Gate2MergeabilityOutcome(DispatchedConflictResolution: false, BaseHadAdvanced: baseHadAdvanced);
    }

    public async Task<CompleteTaskResult> CompleteTaskAndCascadeAsync(
        int taskId,
        string finalCommitMessage,
        CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task == null)
        {
            throw new KeyNotFoundException($"ForgeTask #{taskId} not found.");
        }

        using var lockReleaser = await _workspacePreparer.AcquireWorkspaceLockAsync(task.WorkspaceId, cancellationToken);
        var workspace = await _workspacePreparer.GetWorkspaceAsync(task, cancellationToken);
        if (workspace == null)
        {
            throw new InvalidOperationException($"Workspace #{task.WorkspaceId} not found.");
        }

        return await CompleteTaskAndCascadeInternalAsync(task, workspace, finalCommitMessage, cancellationToken);
    }

    public async Task<CompleteTaskResult> CompleteTaskAndCascadeInternalAsync(
        ForgeTask task,
        Workspace workspace,
        string finalCommitMessage,
        CancellationToken cancellationToken = default)
    {
        int pushRetries = 0;
        while (true)
        {
            var preSquashSha = await _workspacePreparer.GetBranchShaAsync(workspace, task.BaseBranch, cancellationToken);

            string squashSha;
            try
            {
                // Critical Section: SquashAndMerge must be atomic and non-cancellable by pause
                squashSha = await _workspacePreparer.SquashAndMergeAsync(
                    workspace,
                    task.Id,
                    task.BaseBranch,
                    finalCommitMessage,
                    CancellationToken.None);
            }
            catch (MergeConflictException ex)
            {
                _logger.LogWarning(ex, "Squash and merge failed for ForgeTask #{TaskId} due to merge conflict with '{BaseBranch}'.", task.Id, task.BaseBranch);

                var pipelineContext = new PipelineContext(task, workspace);
                await InitiateConflictResolutionAsync(task, workspace, pipelineContext, ex.ConflictedFiles, cancellationToken, discardApproval: task.MergeMode == MergeMode.ManualReviewRequired);

                return new CompleteTaskResult(CompleteTaskStatus.ConflictResolutionDispatched, ConflictedFiles: ex.ConflictedFiles);
            }

            // If ext4 mirror is active, push the merged base branch back to host-origin so developer host repository is updated!
            if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
            {
                var pushResult = await _workspacePreparer.PushBaseBranchToHostAsync(
                    workspace.Ext4MirrorPath,
                    task.BaseBranch,
                    CancellationToken.None);

                if (pushResult.IsNonFastForward)
                {
                    if (pushRetries < ConflictResolutionConstants.MaxPushRetries)
                    {
                        pushRetries++;
                        _logger.LogWarning("Non-fast-forward push rejection for task #{TaskId}. Resetting to pre-squash SHA {PreSquashSha}, pulling from host and retrying (attempt {Attempt}/{Max}).",
                            task.Id, preSquashSha, pushRetries, ConflictResolutionConstants.MaxPushRetries);

                        if (!string.IsNullOrEmpty(preSquashSha))
                        {
                            await _workspacePreparer.ResetCanonicalBranchAsync(workspace, task.BaseBranch, preSquashSha, CancellationToken.None);
                        }
                        await _workspacePreparer.PullMirrorFromHostUnlockedAsync(workspace.Ext4MirrorPath, task.BaseBranch, CancellationToken.None);
                        continue;
                    }

                    _logger.LogError("Mirror push rejected as non-fast-forward after {MaxRetries} retries for task #{TaskId}.",
                        ConflictResolutionConstants.MaxPushRetries, task.Id);
                    if (!string.IsNullOrEmpty(preSquashSha))
                    {
                        await _workspacePreparer.ResetCanonicalBranchAsync(workspace, task.BaseBranch, preSquashSha, CancellationToken.None);
                    }
                    throw new MirrorPushRejectedException(task.BaseBranch, workspace.Ext4MirrorPath, pushResult.WarningMessage ?? "Non-fast-forward push rejection.");
                }
                else if (!pushResult.Success)
                {
                    if (!string.IsNullOrEmpty(preSquashSha))
                    {
                        await _workspacePreparer.ResetCanonicalBranchAsync(workspace, task.BaseBranch, preSquashSha, CancellationToken.None);
                    }
                    throw new GitCommandException(workspace.Ext4MirrorPath, new[] { "push", "host-origin", task.BaseBranch }, 1, string.Empty, pushResult.WarningMessage ?? "Push failed");
                }
            }

            task.SquashCommitSha = squashSha;
            task.Status = TaskOverallStatus.Closed;
            task.CurrentStageId = string.Empty;

            var closedBag = PipelineDataBag.FromJson(task.PipelineDataJson);
            closedBag.Remove(PipelineDataBagKeys.HasResolvedConflict);
            closedBag.Remove(PipelineDataBagKeys.ConflictResolutionAttempts);
            closedBag.Remove(PipelineDataBagKeys.TotalConflictResolutionAttempts);
            closedBag.Remove(PipelineDataBagKeys.LifetimeConflictDetections);
            task.PipelineDataJson = closedBag.ToJson();

            await _taskRepository.UpdateWithHistoryAsync(
                task,
                dependsOn: null,
                changedBy: AuditActors.TaskOrchestrator,
                reason: $"ForgeTask #{task.Id} completed and squash-merged to {task.BaseBranch}.",
                cancellationToken: cancellationToken);
            await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Closed, cancellationToken);

            // Push succeeded and Closed status persisted! Remove task worktree now
            await _workspacePreparer.RemoveWorktreeSafeAsync(workspace, task.Id, "completed", CancellationToken.None);

            break;
        }

        await _workspacePreparer.CleanupTaskContainerSafeAsync(workspace, task.Id, "completed", cancellationToken);

        try
        {
            await _streamer.BroadcastAsync(new AgentActivityEvent(
                Guid.NewGuid().ToString("N"),
                task.Id,
                StandardStageIds.Merge,
                AgentActivityKind.StageCompleted,
                $"ForgeTask #{task.Id} completed and squash-merged.",
                DateTime.UtcNow,
                new Dictionary<string, object?> { ["StageKind"] = "Deterministic" }), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish merge completion event for task #{TaskId}", task.Id);
        }

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Information,
            Message = $"ForgeTask #{task.Id} completed and squash-merged to {task.BaseBranch}.",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);

        // 3. Cascade unblocking to dependent DAG tasks
        var unblocked = await _dependencyEngine.ResolveCascadeUnblockedTasksAsync(task.Id);
        if (unblocked.Count > 0)
        {
            var unblockedTasks = await _taskRepository.GetByIdsAsync(unblocked, cancellationToken);
            var foundIds = unblockedTasks.Select(t => t.Id).ToHashSet();
            var missingIds = unblocked.Where(id => !foundIds.Contains(id)).ToList();
            if (missingIds.Count > 0)
            {
                var missingList = string.Join(", ", missingIds);
                _logger.LogCritical("ForgeTask #{PrereqId} cascade unblocking failed: dependent task(s) [{MissingIds}] not found in task repository.", task.Id, missingList);
                throw new TaskNotFoundException(missingIds[0], $"Dependent task #{missingIds[0]} unblocked by task #{task.Id} was not found in task repository.");
            }

            var orderedUnblocked = unblockedTasks
                .OrderBy(t => t.Priority)     // 1 (Highest) first
                .ThenBy(t => t.CreatedAt)    // Older tasks first on priority tie
                .ToList();

            foreach (var unblockedTask in orderedUnblocked)
            {
                _logger.LogInformation(
                    "ForgeTask #{PrereqId} completed; unblocked dependent task #{UnblockedId} (Priority: {Priority}).",
                    task.Id, unblockedTask.Id, unblockedTask.Priority);

                var unblockedPipeline = await _pipelineProvider.GetPipelineAsync(unblockedTask, cancellationToken);
                var initialStage = unblockedPipeline.InitialStage
                    ?? throw new WorkflowValidationException($"Workflow does not contain an initial stage for unblocked task #{unblockedTask.Id}.");
                unblockedTask.IsDeferredByPause = false;
                unblockedTask.LastHangfireJobId = _jobQueue.EnqueueStage(unblockedTask.Id, initialStage.QueueTier, unblockedTask.Priority);
                await _taskRepository.UpdateAsync(unblockedTask, cancellationToken: cancellationToken);
            }
        }

        await _workspacePreparer.SyncMarkdownTaskSafeAsync(workspace, task, cancellationToken);
        await _workspacePreparer.ArchiveTaskMarkdownSafeAsync(workspace, task, cancellationToken);

        return new CompleteTaskResult(CompleteTaskStatus.Completed);
    }

    public async Task InitiateConflictResolutionAsync(
        ForgeTask task,
        Workspace workspace,
        PipelineContext pipelineContext,
        IReadOnlyList<string> conflictedFiles,
        CancellationToken cancellationToken = default,
        bool discardApproval = false)
    {
        var lifetimeDetections = (pipelineContext.DataBag.Get<int?>(PipelineDataBagKeys.LifetimeConflictDetections) ?? 0) + 1;
        pipelineContext.DataBag.Set(PipelineDataBagKeys.LifetimeConflictDetections, lifetimeDetections);
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictResolutionAttempts, 0);

        if (lifetimeDetections >= ConflictResolutionConstants.MaxLifetimeConflictDetections)
        {
            var escalationMsg = $"Task has experienced {ConflictResolutionConstants.MaxLifetimeConflictDetections} distinct merge conflicts against advancing base branch. Escalated to maintainer to rebase manually.";
            _logger.LogWarning("Task #{TaskId}: {Message}", task.Id, escalationMsg);

            task.IsWaitingForHuman = true;
            task.InterruptedStageId = StandardStageIds.Implementation;
            pipelineContext.SyncDataBagToTask();

            await _taskRepository.UpdateWithHistoryAsync(
                task,
                dependsOn: null,
                changedBy: AuditActors.System,
                reason: escalationMsg,
                cancellationToken: cancellationToken);
            await _dependencyStore.SetWaitingForHumanAsync(task.Id, true, cancellationToken);
            await _workspacePreparer.SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

            await _taskRepository.AddExecutionLogAsync(new ExecutionLog
            {
                TaskId = task.Id,
                Level = LogLevel.Warning,
                Message = escalationMsg,
                Timestamp = DateTime.UtcNow
            }, cancellationToken);

            return;
        }

        pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictFiles, conflictedFiles.ToList());
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictBaseBranch, task.BaseBranch);
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictDetectedAt, DateTime.UtcNow);

        task.Status = TaskOverallStatus.Open;
        task.CurrentStageId = StandardStageIds.Implementation;
        task.IsWaitingForHuman = false;
        pipelineContext.SyncDataBagToTask();

        var reason = discardApproval
            ? string.Format(ConflictResolutionConstants.AuditApprovalDiscardedFormat, task.BaseBranch)
            : string.Format(ConflictResolutionConstants.AuditConflictResolutionRoutedFormat, task.BaseBranch);

        var pipeline = await _pipelineProvider.GetPipelineAsync(task, cancellationToken);
        var implementationStage = pipeline.GetStage(StandardStageIds.Implementation)
            ?? throw new WorkflowValidationException($"Task #{task.Id} workflow does not contain target implementation stage '{StandardStageIds.Implementation}' for conflict resolution.");

        task.LastHangfireJobId = _jobQueue.EnqueueStage(task.Id, implementationStage.QueueTier, task.Priority);

        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.TaskOrchestrator,
            reason: reason,
            cancellationToken: cancellationToken);

        await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Open, cancellationToken);
        await _dependencyStore.SetWaitingForHumanAsync(task.Id, false, cancellationToken);
        await _workspacePreparer.SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Warning,
            Message = reason,
            Timestamp = DateTime.UtcNow
        }, cancellationToken);
    }
}

