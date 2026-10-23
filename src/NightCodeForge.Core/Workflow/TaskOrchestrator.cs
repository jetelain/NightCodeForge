using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Central implementation of ITaskOrchestrator orchestrating DAG validation, workspace locks,
/// Git branch checkouts, stage execution, metric recording, clean human interruption, and cascade unblocking.
/// </summary>
public class TaskOrchestrator : ITaskOrchestrator
{
    private readonly ITaskRepository _taskRepository;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IDependencyResolutionEngine _dependencyEngine;
    private readonly IWorkspaceLockProvider _workspaceLockProvider;
    private readonly IGitWorkspaceManager _gitWorkspaceManager;
    private readonly IWorkflowPipelineProvider _pipelineProvider;
    private readonly ITaskJobQueue _jobQueue;
    private readonly ITaskDependencyStore _dependencyStore;
    private readonly IHarnessRegistry _harnessRegistry;
    private readonly IGitMirrorSyncService _gitMirrorSyncService;
    private readonly Services.ITaskMarkdownSyncService _markdownSyncService;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly Streaming.IAgentActivityStreamer _streamer;
    private readonly ITaskCancellationManager _cancellationManager;
    private readonly IConflictResolutionVerifier _conflictResolutionVerifier;
    private readonly ILogger<TaskOrchestrator> _logger;

    public TaskOrchestrator(
        ITaskRepository taskRepository,
        IWorkspaceRepository workspaceRepository,
        IDependencyResolutionEngine dependencyEngine,
        IWorkspaceLockProvider workspaceLockProvider,
        IGitWorkspaceManager gitWorkspaceManager,
        IWorkflowPipelineProvider pipelineProvider,
        ITaskJobQueue jobQueue,
        ITaskDependencyStore dependencyStore,
        IHarnessRegistry harnessRegistry,
        IGitMirrorSyncService gitMirrorSyncService,
        Services.ITaskMarkdownSyncService markdownSyncService,
        IWorkspacePathResolver pathResolver,
        Streaming.IAgentActivityStreamer streamer,
        ITaskCancellationManager cancellationManager,
        IConflictResolutionVerifier conflictResolutionVerifier,
        ILogger<TaskOrchestrator> logger)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _dependencyEngine = dependencyEngine ?? throw new ArgumentNullException(nameof(dependencyEngine));
        _workspaceLockProvider = workspaceLockProvider ?? throw new ArgumentNullException(nameof(workspaceLockProvider));
        _gitWorkspaceManager = gitWorkspaceManager ?? throw new ArgumentNullException(nameof(gitWorkspaceManager));
        _pipelineProvider = pipelineProvider ?? throw new ArgumentNullException(nameof(pipelineProvider));
        _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
        _dependencyStore = dependencyStore ?? throw new ArgumentNullException(nameof(dependencyStore));
        _harnessRegistry = harnessRegistry ?? throw new ArgumentNullException(nameof(harnessRegistry));
        _gitMirrorSyncService = gitMirrorSyncService ?? throw new ArgumentNullException(nameof(gitMirrorSyncService));
        _markdownSyncService = markdownSyncService ?? throw new ArgumentNullException(nameof(markdownSyncService));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _streamer = streamer ?? throw new ArgumentNullException(nameof(streamer));
        _cancellationManager = cancellationManager ?? throw new ArgumentNullException(nameof(cancellationManager));
        _conflictResolutionVerifier = conflictResolutionVerifier ?? throw new ArgumentNullException(nameof(conflictResolutionVerifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [Hangfire.AutomaticRetry(Attempts = 0, OnAttemptsExceeded = Hangfire.AttemptsExceededAction.Fail)]
    public async Task ProcessTaskAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task == null)
        {
            _logger.LogError("ForgeTask #{TaskId} not found for processing.", taskId);
            return;
        }

        if (task.Status == TaskOverallStatus.Cancelled || task.Status == TaskOverallStatus.Closed || task.Status == TaskOverallStatus.Rejected)
        {
            _logger.LogInformation("ForgeTask #{TaskId} is already in {Status} status; aborting execution.", taskId, task.Status);
            return;
        }

        // The cancellation source is registered only once the task lock is held, so that a duplicate job
        // waiting on the lock can never cancel the execution that is already running.
        CancellationTokenSource? taskCts = null;

        try
        {
        // 1. DAG Readiness Check
        var isReady = await _dependencyEngine.IsReadyForExecutionAsync(taskId, cancellationToken);
        if (!isReady)
        {
            var isWaiting = await _dependencyStore.IsTaskWaitingForHumanAsync(taskId, cancellationToken);
            var status = await _dependencyStore.GetTaskStatusAsync(taskId, cancellationToken);
            string reason = isWaiting
                ? "waiting for human clarification input"
                : (status != TaskOverallStatus.Open && status != TaskOverallStatus.Reviewed && status != TaskOverallStatus.Implemented)
                    ? $"not in an executable status (Status: {status})"
                    : "waiting on prerequisite tasks before execution";

            _logger.LogInformation("ForgeTask #{TaskId} is not ready for execution ({Reason}); waiting.", taskId, reason);
            await _taskRepository.AddExecutionLogAsync(new ExecutionLog
            {
                TaskId = task.Id,
                Level = LogLevel.Information,
                Message = $"ForgeTask #{task.Id} is {reason}.",
                Timestamp = DateTime.UtcNow
            }, cancellationToken);
            return;
        }

        // 2. ForgeTask Lock Acquisition (Enables concurrent execution of different tasks on the same workspace)
        using var lockReleaser = await _workspaceLockProvider.AcquireTaskLockAsync(task.WorkspaceId, task.Id, cancellationToken);

        // The task may have been cancelled while this job was waiting for the lock.
        var persistedStatus = await _taskRepository.GetStatusAsync(task.Id, cancellationToken);
        if (persistedStatus == TaskOverallStatus.Cancelled || persistedStatus == TaskOverallStatus.Closed || persistedStatus == TaskOverallStatus.Rejected)
        {
            _logger.LogInformation("ForgeTask #{TaskId} is now in {Status} status; aborting execution.", taskId, persistedStatus);
            return;
        }

        taskCts = _cancellationManager.Register(task.Id, cancellationToken);
        cancellationToken = taskCts.Token;

        var workspace = task.Workspace ?? await _workspaceRepository.GetByIdAsync(task.WorkspaceId, cancellationToken);
        if (workspace == null)
        {
            var err = $"Workspace #{task.WorkspaceId} not found for task #{taskId}.";
            _logger.LogError(err);
            task.Status = TaskOverallStatus.Failed;
            await _taskRepository.UpdateWithHistoryAsync(
                task,
                dependsOn: null,
                changedBy: AuditActors.TaskOrchestrator,
                reason: err,
                cancellationToken: cancellationToken);
            await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Failed, cancellationToken);
            await _taskRepository.AddExecutionLogAsync(new ExecutionLog
            {
                TaskId = task.Id,
                Level = LogLevel.Error,
                Message = err,
                Timestamp = DateTime.UtcNow
            }, cancellationToken);
            return;
        }

        // 3. Branch & Environment Setup
        if (string.IsNullOrWhiteSpace(task.FeatureBranch))
        {
            task.FeatureBranch = $"feature/task-{task.Id}";
        }

        try
        {
            using (await _workspaceLockProvider.AcquireLockAsync(workspace.Id, cancellationToken))
            {
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
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Automatic ext4 mirror initialization failed for workspace #{WorkspaceId}", workspace.Id);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
                    {
                        try
                        {
                            await _gitMirrorSyncService.ConfigureHostRepositoryAsync(workspace.RepositoryPath, cancellationToken);
                            await _gitMirrorSyncService.PullFromHostAsync(workspace.Ext4MirrorPath, task.BaseBranch, cancellationToken);
                        }
                        catch (Exception mex)
                        {
                            _logger.LogWarning(mex, "Ext4 mirror fetch synchronization failed for task #{TaskId}", taskId);
                        }
                    }
                }

                await _gitWorkspaceManager.CheckoutTaskBranchAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed checking out feature branch for task #{TaskId}", taskId);
            task.Status = TaskOverallStatus.Failed;
            await _taskRepository.UpdateWithHistoryAsync(
                task,
                dependsOn: null,
                changedBy: AuditActors.TaskOrchestrator,
                reason: $"Failed checkout branch: {ex.Message}",
                cancellationToken: cancellationToken);
            await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Failed, cancellationToken);
            await _taskRepository.AddExecutionLogAsync(new ExecutionLog
            {
                TaskId = task.Id,
                Level = LogLevel.Error,
                Message = $"Failed checkout branch: {ex.Message}",
                Timestamp = DateTime.UtcNow
            }, cancellationToken);
            return;
        }

        // 4. Resolve Stage & Pipeline
        var pipeline = await _pipelineProvider.GetPipelineAsync(task, cancellationToken);
        IWorkflowStage? stage;

        if (string.IsNullOrWhiteSpace(task.CurrentStageId))
        {
            stage = pipeline.Stages.FirstOrDefault();
            if (stage != null)
            {
                task.CurrentStageId = stage.StageId;
            }
        }
        else
        {
            stage = pipeline.GetStage(task.CurrentStageId);
        }

        if (stage == null)
        {
            var err = $"Stage '{task.CurrentStageId}' not found in pipeline for task #{taskId}.";
            _logger.LogError(err);
            task.Status = TaskOverallStatus.Failed;
            await _taskRepository.UpdateWithHistoryAsync(
                task,
                dependsOn: null,
                changedBy: AuditActors.TaskOrchestrator,
                reason: err,
                cancellationToken: cancellationToken);
            await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Failed, cancellationToken);
            await _taskRepository.AddExecutionLogAsync(new ExecutionLog
            {
                TaskId = task.Id,
                Level = LogLevel.Error,
                Message = err,
                Timestamp = DateTime.UtcNow
            }, cancellationToken);
            return;
        }

        // Persist current stage immediately so UI / Kanban / DAG shows task is actively executing
        var stageIndex = pipeline.Stages.ToList().FindIndex(s => string.Equals(s.StageId, stage.StageId, StringComparison.OrdinalIgnoreCase));
        var stageOrder = stageIndex >= 0 ? stageIndex + 1 : 1;
        task.HasExecutionRun = true;
        task.MaxExecutedStageOrder = Math.Max(task.MaxExecutedStageOrder, stageOrder);
        await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);

        try
        {
            await _streamer.BroadcastAsync(new Streaming.AgentActivityEvent(
                Guid.NewGuid().ToString("N"),
                task.Id,
                stage.StageId,
                Streaming.AgentActivityKind.StageStarted,
                $"Starting stage '{stage.StageId}'",
                DateTime.UtcNow,
                new Dictionary<string, object?> { ["StageKind"] = stage.Kind.ToString() }), cancellationToken);
        }
        catch (Exception sex)
        {
            _logger.LogDebug(sex, "Failed broadcasting stage started event for task #{TaskId}", task.Id);
        }

        // Resolve Agent Profile matching interrupted profile or current stage, or fallback to first
        AgentProfile? profile = null;
        if (!string.IsNullOrWhiteSpace(task.InterruptedAgentProfileId))
        {
            profile = workspace.AgentProfiles?.FirstOrDefault(p =>
                string.Equals(p.Id, task.InterruptedAgentProfileId, StringComparison.OrdinalIgnoreCase));
            task.InterruptedAgentProfileId = null;
        }

        if (profile == null)
        {
            profile = await _workspaceRepository.GetEffectiveAgentProfileAsync(workspace.Id, stage.StageId, cancellationToken);
        }

        if (profile == null)
        {
            profile = workspace.AgentProfiles?.FirstOrDefault(p =>
                string.Equals(p.Id, stage.StageId, StringComparison.OrdinalIgnoreCase) ||
                (p.StageDirectives != null && p.StageDirectives.ContainsKey(stage.StageId)));
        }

        if (stage.Kind == WorkflowStageKind.Agentic && !HasCustomHandler(stage))
        {
            if (profile == null)
            {
                var notFoundEx = new AgentProfileNotFoundException(stage.StageId, workspace.Id);
                _logger.LogError(notFoundEx, "Agent profile not found for stage '{StageId}' in workspace #{WorkspaceId}", stage.StageId, workspace.Id);
                task.Status = TaskOverallStatus.Failed;
                await _taskRepository.UpdateWithHistoryAsync(
                    task,
                    dependsOn: null,
                    changedBy: AuditActors.TaskOrchestrator,
                    reason: notFoundEx.Message,
                    cancellationToken: cancellationToken);
                await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Failed, cancellationToken);
                await _taskRepository.AddExecutionLogAsync(new ExecutionLog
                {
                    TaskId = task.Id,
                    Level = LogLevel.Error,
                    Message = notFoundEx.Message,
                    Timestamp = DateTime.UtcNow
                }, cancellationToken);

                throw notFoundEx;
            }
        }

        bool isResumedRun = task.IsWaitingForHuman || !string.IsNullOrWhiteSpace(task.InterruptedStageId);
        if (task.IsWaitingForHuman)
        {
            task.IsWaitingForHuman = false;
            await _dependencyStore.SetWaitingForHumanAsync(task.Id, false, cancellationToken);
        }
        if (!string.IsNullOrWhiteSpace(task.InterruptedStageId))
        {
            task.InterruptedStageId = null;
        }

        var taskWorkingPath = _gitWorkspaceManager.GetWorktreePath(workspace, task.Id);
        var pipelineContext = new PipelineContext(task, workspace)
        {
            WorkingDirectory = taskWorkingPath,
            Pipeline = pipeline,
            CurrentProfile = profile
        };

        // Explicit maintainer resume after an escalation: grant a fresh conflict-resolution budget.
        if (isResumedRun &&
            (pipelineContext.DataBag.Get<int?>(PipelineDataBagKeys.ConflictResolutionAttempts) ?? 0) >= ConflictResolutionConstants.MaxConflictResolutionAttempts)
        {
            pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictResolutionAttempts, 0);
        }

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

        IReadOnlyDictionary<int, Services.TaskBaselineEntry>? baselineSnapshot = null;
        if (!string.IsNullOrWhiteSpace(targetSyncPath) && Directory.Exists(targetSyncPath))
        {
            try
            {
                // If resuming from human interruption or interrupted stage, run read-back first to preserve edits made before pause
                if (isResumedRun)
                {
                    var preserveActor = AuditActors.GetActorForStage(stage.StageId);
                    await _markdownSyncService.SyncAllTasksFromDiskAsync(
                        workspace,
                        targetSyncPath,
                        hostRepoPath,
                        baselineSnapshot: null,
                        preserveActor,
                        TaskSyncConstants.PreserveEditsBeforeResumeReason,
                        cancellationToken);
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

        // Gate 1: Pre-Implementation Baseline Synchronization
        if (string.Equals(stage.StageId, StandardStageIds.PreImplementationBaseline, StringComparison.OrdinalIgnoreCase))
        {
            await _gitWorkspaceManager.CreateWipCheckpointAsync(
                workspace,
                task.Id,
                StandardStageIds.TaskReview,
                "WIP checkpoint before Pre-Implementation Baseline sync",
                cancellationToken);

            var baselineSyncResult = await _gitWorkspaceManager.SyncFromBaseBranchAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
            if (baselineSyncResult.HasConflict)
            {
                await _gitWorkspaceManager.AbortMergeAsync(workspace, task.Id, cancellationToken);
                var conflictMsg = $"Merge conflict during Pre-Implementation Baseline synchronization with '{task.BaseBranch}' in {baselineSyncResult.ConflictedFiles.Count} files.";

                task.Status = TaskOverallStatus.Failed;
                await _taskRepository.UpdateWithHistoryAsync(
                    task,
                    dependsOn: null,
                    changedBy: AuditActors.TaskOrchestrator,
                    reason: conflictMsg,
                    cancellationToken: cancellationToken);
                await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Failed, cancellationToken);
                _logger.LogError("Gate 1 failed for task #{TaskId}: {Message}", task.Id, conflictMsg);

                await _taskRepository.AddExecutionLogAsync(new ExecutionLog
                {
                    TaskId = task.Id,
                    Level = LogLevel.Error,
                    Message = conflictMsg,
                    Timestamp = DateTime.UtcNow
                }, cancellationToken);

                throw new MergeConflictException(task.Id, task.BaseBranch, baselineSyncResult.ConflictedFiles, conflictMsg);
            }

            await _taskRepository.AddExecutionLogAsync(new ExecutionLog
            {
                TaskId = task.Id,
                Level = LogLevel.Information,
                Message = string.Format(ConflictResolutionConstants.AuditBaselineSyncSuccessFormat, task.BaseBranch),
                Timestamp = DateTime.UtcNow
            }, cancellationToken);
        }

        // Marker Materialization at Implementation stage start
        if (string.Equals(stage.StageId, StandardStageIds.Implementation, StringComparison.OrdinalIgnoreCase))
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

        var startedAt = DateTime.UtcNow;
        StageExecutionResult result;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (stage.Kind == WorkflowStageKind.Agentic && !HasCustomHandler(stage))
            {
                var harnessId = profile!.HarnessId;
                if (_harnessRegistry.TryGetHarness(harnessId, out var harness) && harness != null)
                {
                    var lastCheckpoint = task.Checkpoints?.OrderByDescending(c => c.CreatedAt).FirstOrDefault();
                    var lastHumanAnswer = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.LastHumanAnswer);

                    var defaultDirective = (profile.StageDirectives != null && profile.StageDirectives.TryGetValue(stage.StageId, out var customDirective) && !string.IsNullOrWhiteSpace(customDirective))
                        ? customDirective
                        : stage.StageDirective;

                    var stageDirective = WorkspacePromptResolver.ResolveStageDirective(taskWorkingPath, stage.StageId, defaultDirective);

                    bool isModeMismatch = profile.Mode == AgentMode.ReadOnly &&
                        (stage.RequiredMode == AgentMode.Full || stage.RequiredMode == AgentMode.Review);

                    if (isModeMismatch)
                    {
                        var errorMsg = $"Stage '{stage.StageId}' requires AgentMode.{stage.RequiredMode} capabilities, but profile '{profile.Id}' is configured as AgentMode.ReadOnly.";
                        _logger.LogError(errorMsg);
                        result = StageExecutionResult.Failure(errorMsg);
                    }
                    else
                    {
                        var resumeMemory = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.RecoveredSerializedMemory);
                        if (!string.IsNullOrEmpty(resumeMemory))
                        {
                            // Consume once: a failed/cancelled run must not replay stale recovered history later.
                            pipelineContext.DataBag.Remove(PipelineDataBagKeys.RecoveredSerializedMemory);
                        }
                        else if (lastCheckpoint != null && string.Equals(lastCheckpoint.StageId, stage.StageId, StringComparison.OrdinalIgnoreCase))
                        {
                            resumeMemory = lastCheckpoint.SerializedMemory;
                        }

                        var reviewDiffStat = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.ReviewDiffStat);
                        if (string.IsNullOrWhiteSpace(reviewDiffStat) &&
                            (string.Equals(stage.StageId, StandardStageIds.CodeReviewAndFix, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(stage.StageId, StandardStageIds.PostReviewVerification, StringComparison.OrdinalIgnoreCase)))
                        {
                            try
                            {
                                reviewDiffStat = await _gitWorkspaceManager.GenerateDiffStatAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
                                pipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewDiffStat, reviewDiffStat);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed generating review diff stat during stage setup for {StageId} (task #{TaskId})", stage.StageId, task.Id);
                            }
                        }

                        var agentContext = new AgentSessionContext
                        {
                            ForgeTask = task,
                            Workspace = workspace,
                            Profile = profile,
                            StageId = stage.StageId,
                            StageDirective = stageDirective,
                            RequiredMode = stage.RequiredMode,
                            WorkspacePath = taskWorkingPath,
                            SerializedStateJson = resumeMemory,
                            PendingHumanAnswer = lastHumanAnswer,
                            PendingHumanAnswerJson = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.LastHumanAnswerJson),
                            TaskMarkdownPath = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath),
                            ReviewDiffStat = reviewDiffStat,
                            ApprovedPlan = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.ApprovedPlan)
                                ?? pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.StageSummary(StandardStageIds.TaskReview)),
                            ReviewerFeedback = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.ReviewerFeedback),
                            PriorDiagnostics = pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.CompilerErrors),
                            BootstrapPrompt = stage.BuildBootstrapPrompt(task, pipelineContext, stageDirective),
                            OptimizationFlags = pipelineContext.DataBag.Get<LocalOptimizationFlags>(PipelineDataBagKeys.OptimizationFlags)
                                ?? profile.OptimizationFlags ?? new LocalOptimizationFlags(),
                            DiscoveredSymbols = pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.DiscoveredSymbols),
                            PipelineContext = pipelineContext
                        };

                        var runResult = await harness.RunStageAsync(agentContext, cancellationToken);

                        if (!string.IsNullOrEmpty(runResult.UpdatedState))
                        {
                            pipelineContext.DataBag.Set(PipelineDataBagKeys.SerializedMemory, runResult.UpdatedState);
                        }

                        if (!string.IsNullOrEmpty(runResult.Summary))
                        {
                            pipelineContext.DataBag.Set(PipelineDataBagKeys.StageSummary(stage.StageId), runResult.Summary);
                            if (string.Equals(stage.StageId, StandardStageIds.TaskReview, StringComparison.OrdinalIgnoreCase))
                            {
                                pipelineContext.DataBag.Set(PipelineDataBagKeys.ApprovedPlan, runResult.Summary);
                            }
                        }

                        if (!string.IsNullOrEmpty(runResult.ReviewerFeedback))
                        {
                            pipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewerFeedback, runResult.ReviewerFeedback);
                        }

                        if (runResult.Proposals is { Count: > 0 })
                        {
                            var existingProposals = pipelineContext.DataBag.Get<List<TaskProposal>>(PipelineDataBagKeys.PendingProposals) ?? new List<TaskProposal>();
                            existingProposals.AddRange(runResult.Proposals);
                            pipelineContext.DataBag.Set(PipelineDataBagKeys.PendingProposals, existingProposals);
                        }

                        result = MapAgentRunResultToStageResult(runResult, stage, profile, startedAt);
                    }
                }
                else
                {
                    result = await stage.ExecuteAsync(pipelineContext, cancellationToken);
                }
            }
            else
            {
                result = await stage.ExecuteAsync(pipelineContext, cancellationToken);
            }
        }
        catch (HumanInterruptionException hex)
        {
            var summaryPrompt = string.Join("; ", hex.Questions.Select(q => q.Prompt));
            _logger.LogInformation("Stage '{StageId}' triggered HumanInterruptionException: {Prompt}", stage.StageId, summaryPrompt);
            result = StageExecutionResult.WaitingForHuman(hex.Questions);
        }
        catch (OperationCanceledException ocex)
        {
            _logger.LogInformation(ocex, "Stage '{StageId}' for task #{TaskId} was cancelled.", stage.StageId, taskId);
            result = StageExecutionResult.Cancelled("Stage execution was cancelled.");
        }
        catch (MergeConflictException mex)
        {
            task.Status = TaskOverallStatus.Failed;
            await _taskRepository.UpdateWithHistoryAsync(
                task,
                dependsOn: null,
                changedBy: AuditActors.TaskOrchestrator,
                reason: mex.Message,
                cancellationToken: cancellationToken);
            await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Failed, cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stage '{StageId}' failed with unhandled exception for task #{TaskId}", stage.StageId, taskId);
            result = StageExecutionResult.Failure(ex.Message);
        }

        var completedAt = DateTime.UtcNow;
        var durationMs = (int)(completedAt - startedAt).TotalMilliseconds;

        // Save execution metrics record
        var record = result.ExecutionRecord ?? new StageExecutionRecord();
        record.TaskId = task.Id;
        record.StageId = stage.StageId;
        record.StageKind = stage.Kind.ToString();
        record.AgentProfileId = profile?.Id ?? string.Empty;
        record.HarnessId = profile?.HarnessId ?? string.Empty;
        record.ModelName = profile?.ModelName ?? string.Empty;
        record.StartedAt = startedAt;
        record.CompletedAt = completedAt;
        record.DurationMs = durationMs;
        record.Status = result.NeedsClarification
            ? StageExecutionStatus.Completed
            : (result.IsCancelled ? StageExecutionStatus.Cancelled : (result.IsSuccess ? StageExecutionStatus.Completed : StageExecutionStatus.Failed));

        using (var saveTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var saveToken = result.IsCancelled ? saveTimeoutCts.Token : cancellationToken;
            try
            {
                await _taskRepository.AddExecutionRecordAsync(record, saveToken);
            }
            catch (Exception rex)
            {
                _logger.LogWarning(rex, "Failed to record stage execution record for task #{TaskId}", task.Id);
            }
        }

        // Record Conversation for deterministic stages
        if (stage.Kind == WorkflowStageKind.Deterministic && !result.IsCancelled)
        {
            try
            {
                int attemptIndex = (pipelineContext.StageRetries.GetValueOrDefault(stage.StageId, 0)) + 1;
                var deterministicConv = await _taskRepository.StartConversationAsync(
                    task.Id,
                    stage.StageId,
                    WorkflowStageKind.Deterministic,
                    attemptIndex,
                    agentProfileId: null,
                    harnessId: "native-cli",
                    modelName: "deterministic-cpu",
                    cancellationToken: cancellationToken);

                // Turn 1: User prompt
                await _taskRepository.AddConversationTurnAsync(
                    deterministicConv.Id,
                    StageTurnRole.User,
                    content: $"Execute deterministic gate '{stage.DisplayName}' ({stage.StageId})",
                    cancellationToken: cancellationToken);

                // Turn 2: Tool execution(s)
                var commandExecutions = pipelineContext.DataBag.Get<List<StageCommandExecution>>(PipelineDataBagKeys.StageCommands(stage.StageId));
                if (commandExecutions != null && commandExecutions.Count > 0)
                {
                    foreach (var exec in commandExecutions)
                    {
                        var toolStageName = !string.IsNullOrWhiteSpace(exec.StageName)
                            ? $"{stage.StageId}-{exec.StageName}"
                            : stage.StageId;

                        await _taskRepository.AddConversationTurnAsync(
                            deterministicConv.Id,
                            StageTurnRole.Tool,
                            content: $"Execute {exec.Command}",
                            toolName: toolStageName,
                            toolArguments: exec.Command,
                            toolResult: exec.Output,
                            durationMs: exec.DurationMs > 0 ? exec.DurationMs : durationMs,
                            isError: !exec.IsSuccess,
                            cancellationToken: cancellationToken);
                    }
                }
                else
                {
                    var cmd = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.StageCommand(stage.StageId)) 
                        ?? pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.BuildCommand) 
                        ?? "dotnet build";
                    var output = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.StageOutput(stage.StageId)) 
                        ?? (result.IsSuccess ? "Verification completed successfully with exit code 0." : result.ErrorMessage ?? "Deterministic step failed");
                    await _taskRepository.AddConversationTurnAsync(
                        deterministicConv.Id,
                        StageTurnRole.Tool,
                        content: $"Execute {cmd}",
                        toolName: stage.StageId,
                        toolArguments: cmd,
                        toolResult: output,
                        durationMs: durationMs,
                        isError: !result.IsSuccess,
                        cancellationToken: cancellationToken);
                }

                // Turn 3: Assistant result summary
                var summary = result.IsSuccess 
                    ? $"Deterministic gate '{stage.DisplayName}' passed successfully."
                    : $"Deterministic gate '{stage.DisplayName}' failed: {result.ErrorMessage}";
                await _taskRepository.AddConversationTurnAsync(
                    deterministicConv.Id,
                    StageTurnRole.Assistant,
                    content: summary,
                    cancellationToken: cancellationToken);

                await _taskRepository.CompleteConversationAsync(
                    deterministicConv.Id,
                    result.IsSuccess ? StageExecutionStatus.Completed : StageExecutionStatus.Failed,
                    summary: summary,
                    cancellationToken: cancellationToken);

                if (result.IsSuccess)
                {
                    try
                    {
                        await _streamer.BroadcastAsync(new Streaming.AgentActivityEvent(
                            Guid.NewGuid().ToString("N"),
                            task.Id,
                            stage.StageId,
                            Streaming.AgentActivityKind.StageCompleted,
                            summary,
                            DateTime.UtcNow,
                            new Dictionary<string, object?> { ["StageKind"] = "Deterministic" }), cancellationToken);
                    }
                    catch (Exception sex)
                    {
                        _logger.LogDebug(sex, "Failed broadcasting deterministic stage completion event for task #{TaskId}", task.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist deterministic Conversation for task #{TaskId}, stage '{StageId}'", task.Id, stage.StageId);
            }
        }

        // Auto-persist any backlog proposals emitted by triage or discovery agents (propose_tasks)
        var pendingProposals = pipelineContext.DataBag.Get<List<TaskProposal>>(PipelineDataBagKeys.PendingProposals);
        if (pendingProposals != null && pendingProposals.Count > 0)
        {
            foreach (var p in pendingProposals)
            {
                var suggestedTask = new ForgeTask
                {
                    WorkspaceId = workspace.Id,
                    Title = p.Title,
                    Description = p.Description,
                    Status = TaskOverallStatus.Suggested,
                    MergeMode = MergeMode.ManualReviewRequired,
                    BaseBranch = workspace.DefaultBaseBranch ?? WorkflowDefaults.DefaultBaseBranch,
                    Priority = p.Priority > 0 ? p.Priority : TaskPriority.Normal,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                await _taskRepository.CreateWithBaselineAsync(
                    suggestedTask,
                    dependsOn: null,
                    createdBy: AuditActors.GetActorForStage(stage.StageId),
                    reason: $"Proposed by stage '{stage.StageId}' during execution of task #{task.Id}",
                    cancellationToken: cancellationToken);
                _logger.LogInformation("Triage/Discovery created suggested task #{NewTaskId}: {Title}", suggestedTask.Id, suggestedTask.Title);
                await SyncMarkdownTaskSafeAsync(workspace, suggestedTask, cancellationToken);
            }
            pipelineContext.DataBag.Remove(PipelineDataBagKeys.PendingProposals);
        }

        // Read back all disk modifications to tasks using single authority (TaskMarkdownSyncService)
        if (!string.IsNullOrWhiteSpace(targetSyncPath) && Directory.Exists(targetSyncPath))
        {
            var stageActor = AuditActors.GetActorForStage(stage.StageId);
            var stageReason = $"Stage '{stage.StageId}' completed disk synchronization";

            IReadOnlyList<ForgeTask> refreshedTasks;
            try
            {
                refreshedTasks = await _markdownSyncService.SyncAllTasksFromDiskAsync(
                    workspace,
                    targetSyncPath,
                    hostRepoPath,
                    baselineSnapshot,
                    stageActor,
                    stageReason,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TaskMarkdownSyncException)
            {
                throw;
            }
            catch (TaskConcurrencyException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new TaskMarkdownSyncException(
                    $"Post-stage disk synchronization failed for stage '{stage.StageId}' on task #{task.Id}: {ex.Message}",
                    ex,
                    taskId: task.Id);
            }

            var refreshedTask = refreshedTasks?.FirstOrDefault(t => t.Id == task.Id);
            if (refreshedTask != null)
            {
                task.Title = refreshedTask.Title;
                task.Description = refreshedTask.Description;
                task.Priority = refreshedTask.Priority;
                task.BaseBranch = refreshedTask.BaseBranch;
                task.FeatureBranch = refreshedTask.FeatureBranch;
                task.UpdatedAt = refreshedTask.UpdatedAt;
                task.Version = refreshedTask.Version;
            }

            if (string.Equals(stage.StageId, StandardStageIds.TaskReview, StringComparison.OrdinalIgnoreCase) && result.IsSuccess && !result.IsCancelled && !result.NeedsClarification)
            {
                bool modifiedInThisStage = false;
                if (baselineSnapshot != null && baselineSnapshot.TryGetValue(task.Id, out var baseEntry))
                {
                    if (task.Version > baseEntry.Version ||
                        !string.Equals(task.Description?.Trim(), baseEntry.Description?.Trim(), StringComparison.Ordinal) ||
                        !string.Equals(task.Title?.Trim(), baseEntry.Frontmatter.Title?.Trim(), StringComparison.Ordinal))
                    {
                        modifiedInThisStage = true;
                    }
                }

                // Only an earlier recorded modification (version beyond the creation baseline) excuses an unchanged resumed run.
                bool alreadyModifiedByEarlierRun = baselineSnapshot != null &&
                                                   baselineSnapshot.TryGetValue(task.Id, out var b) &&
                                                   b.Version > 1;

                if (!modifiedInThisStage && !alreadyModifiedByEarlierRun)
                {
                    throw new WorkflowExecutionException($"Stage '{stage.StageId}' completed without producing a modified task specification document.");
                }
            }
        }

        // 5. Result Evaluation
        if (result.IsCancelled || cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Processing of task #{TaskId} at stage '{StageId}' was cancelled.", task.Id, stage.StageId);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ct = timeoutCts.Token;

            try
            {
                var convs = await _taskRepository.GetConversationsAsync(task.Id, ct);
                var activeConvs = convs.Where(c => c.Status == StageExecutionStatus.Running || c.Status == StageExecutionStatus.WaitingForHuman).ToList();
                foreach (var activeConv in activeConvs)
                {
                    await _taskRepository.CompleteConversationAsync(
                        activeConv.Id,
                        StageExecutionStatus.Cancelled,
                        summary: "Execution was cancelled.",
                        cancellationToken: ct);
                }
            }
            catch (Exception cex)
            {
                _logger.LogWarning(cex, "Failed to mark active conversation as Cancelled for task #{TaskId}", task.Id);
            }

            // Only an explicit cancel request is terminal; a parent cancellation (e.g. shutdown) must leave the task recoverable.
            if (_cancellationManager.IsCancelRequested(task.Id))
            {
                var persistedTask = await _taskRepository.GetByIdAsync(task.Id, ct);
                if (persistedTask?.Status != TaskOverallStatus.Closed && persistedTask?.Status != TaskOverallStatus.Rejected)
                {
                    task.Status = TaskOverallStatus.Cancelled;
                    task.IsWaitingForHuman = false;
                    pipelineContext.SyncDataBagToTask();
                    await _taskRepository.UpdateWithHistoryAsync(
                        task,
                        dependsOn: null,
                        changedBy: AuditActors.User,
                        reason: $"Stage '{stage.StageId}' was cancelled.",
                        cancellationToken: ct);
                    await _dependencyStore.UpdateTaskStatusAsync(task.Id, task.Status, ct);
                }
            }
            else
            {
                pipelineContext.SyncDataBagToTask();
                await _taskRepository.UpdateAsync(task, cancellationToken: ct);
                await _dependencyStore.UpdateTaskStatusAsync(task.Id, task.Status, ct);
            }

            await _taskRepository.AddExecutionLogAsync(new ExecutionLog
            {
                TaskId = task.Id,
                Level = LogLevel.Information,
                Message = $"Stage '{stage.StageId}' was cancelled.",
                Timestamp = DateTime.UtcNow
            }, ct);

            return;
        }

        if (result.NeedsClarification)
        {
            if (result.ClarificationQuestions == null || result.ClarificationQuestions.Count == 0)
            {
                throw new InvalidOperationException($"Stage '{stage.StageId}' signaled clarification required but provided no questions.");
            }

            var batchId = Guid.NewGuid().ToString("N");
            foreach (var question in result.ClarificationQuestions)
            {
                question.TaskId = task.Id;
                question.StageId = stage.StageId;
                question.Status = QuestionStatus.Pending;
                question.AgentProfileId = profile?.Id;
                question.AskedAt = DateTime.UtcNow;
                question.BatchId = batchId;
                task.Questions.Add(question);
            }

            var summaryPrompt = string.Join("; ", result.ClarificationQuestions.Select(q => q.Prompt));

            string commitHash = string.Empty;
            try
            {
                commitHash = await _gitWorkspaceManager.CreateWipCheckpointAsync(
                    workspace,
                    task.Id,
                    stage.StageId,
                    $"WIP checkpoint before human clarification ({result.ClarificationQuestions.Count} question(s)): {summaryPrompt}",
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed creating WIP checkpoint commit for task #{TaskId}", task.Id);
            }

            var checkpoint = new StageCheckpoint
            {
                TaskId = task.Id,
                StageId = stage.StageId,
                GitCommitHash = commitHash,
                SerializedMemory = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.SerializedMemory) ?? string.Empty,
                CreatedAt = DateTime.UtcNow
            };
            await _taskRepository.AddCheckpointAsync(checkpoint, cancellationToken);

            // Universal Interruption: human interruption NEVER mutates task.Status!
            task.IsWaitingForHuman = true;
            task.InterruptedStageId = stage.StageId;
            task.InterruptedAgentProfileId = profile?.Id;

            pipelineContext.SyncDataBagToTask();
            await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
            await _dependencyStore.UpdateTaskStatusAsync(task.Id, task.Status, cancellationToken);
            await _dependencyStore.SetWaitingForHumanAsync(task.Id, true, cancellationToken);
            await SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

            await _taskRepository.AddExecutionLogAsync(new ExecutionLog
            {
                TaskId = task.Id,
                Level = LogLevel.Information,
                Message = $"ForgeTask #{task.Id} paused waiting for human input ({result.ClarificationQuestions.Count} question(s), batch {batchId}): {summaryPrompt}",
                Timestamp = DateTime.UtcNow
            }, cancellationToken);

            // Clean return: Hangfire marks job as Succeeded, GPU worker freed
            return;
        }

        // Finding L4: Re-verify cancellation token and persisted (untracked) status to ensure we never overwrite cancellation
        if (cancellationToken.IsCancellationRequested)
        {
            if (_cancellationManager.IsCancelRequested(task.Id))
            {
                var persistedStatusAfterStage = await _taskRepository.GetStatusAsync(task.Id, CancellationToken.None);
                if (persistedStatusAfterStage != TaskOverallStatus.Closed && persistedStatusAfterStage != TaskOverallStatus.Rejected)
                {
                    task.Status = TaskOverallStatus.Cancelled;
                    task.IsWaitingForHuman = false;
                    await _taskRepository.UpdateWithHistoryAsync(
                        task,
                        dependsOn: null,
                        changedBy: AuditActors.User,
                        reason: $"Execution cancelled after stage '{stage.StageId}'.",
                        cancellationToken: CancellationToken.None);
                    await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Cancelled, CancellationToken.None);
                }
                return;
            }

            // Parent cancellation (e.g. shutdown): leave status untouched so the task stays recoverable.
            cancellationToken.ThrowIfCancellationRequested();
        }

        var persistedStageStatus = await _taskRepository.GetStatusAsync(task.Id, CancellationToken.None);
        if (persistedStageStatus == TaskOverallStatus.Cancelled)
        {
            _logger.LogInformation("ForgeTask #{TaskId} was cancelled out-of-band; aborting stage completion.", task.Id);
            task.Status = TaskOverallStatus.Cancelled;
            task.IsWaitingForHuman = false;
            await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Cancelled, CancellationToken.None);
            return;
        }

        if (result.IsSuccess)
        {
            if (string.Equals(stage.StageId, StandardStageIds.Implementation, StringComparison.OrdinalIgnoreCase) &&
                pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.ConflictFiles) is { Count: > 0 } activeConflictFiles)
            {
                var verifierResult = await _conflictResolutionVerifier.VerifyResolutionAsync(
                    workspace,
                    task.Id,
                    task.BaseBranch,
                    activeConflictFiles,
                    cancellationToken);

                if (!verifierResult.IsSuccess)
                {
                    var attempts = (pipelineContext.DataBag.Get<int?>(PipelineDataBagKeys.ConflictResolutionAttempts) ?? 0) + 1;
                    pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictResolutionAttempts, attempts);

                    if (attempts >= ConflictResolutionConstants.MaxConflictResolutionAttempts)
                    {
                        _logger.LogWarning("Conflict resolution budget exhausted for task #{TaskId} ({Attempts} attempts). Escalating to maintainer.",
                            task.Id, attempts);

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

                        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
                        {
                            TaskId = task.Id,
                            Level = LogLevel.Warning,
                            Message = ConflictResolutionConstants.AuditConflictResolutionBudgetExhausted,
                            Timestamp = DateTime.UtcNow
                        }, cancellationToken);

                        return;
                    }

                    // Loopback to Implementation with concrete feedback
                    result = StageExecutionResult.Failure(verifierResult.ErrorMessage ?? "Conflict resolution verification failed.", loopBackStageId: StandardStageIds.Implementation);

                    var compilerErrors = pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.CompilerErrors) ?? new List<string>();
                    compilerErrors.Add(verifierResult.ErrorMessage ?? "Conflict markers or unmerged index entries remain.");
                    pipelineContext.DataBag.Set(PipelineDataBagKeys.CompilerErrors, compilerErrors);
                }
                else
                {
                    // Verified successfully!
                    pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictResolutionAttempts, 0);
                    pipelineContext.DataBag.Remove(PipelineDataBagKeys.ConflictFiles);
                    pipelineContext.DataBag.Remove(PipelineDataBagKeys.ConflictBaseBranch);
                    pipelineContext.DataBag.Remove(PipelineDataBagKeys.ConflictDetectedAt);
                    pipelineContext.DataBag.Set(PipelineDataBagKeys.HasResolvedConflict, true);

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
                }
            }
        }

        if (result.IsSuccess)
        {
            // Case B: Stage Completed
            pipelineContext.ResetRetries();

            if (stage.Kind != WorkflowStageKind.Deterministic)
            {
                try
                {
                    await _streamer.BroadcastAsync(new Streaming.AgentActivityEvent(
                        Guid.NewGuid().ToString("N"),
                        task.Id,
                        stage.StageId,
                        Streaming.AgentActivityKind.StageCompleted,
                        $"Stage '{stage.StageId}' completed successfully.",
                        DateTime.UtcNow,
                        new Dictionary<string, object?> { ["StageKind"] = stage.Kind.ToString() }), cancellationToken);
                }
                catch (Exception sex)
                {
                    _logger.LogDebug(sex, "Failed broadcasting stage completion event for task #{TaskId}", task.Id);
                }
            }

            string commitHash = string.Empty;
            try
            {
                commitHash = await _gitWorkspaceManager.CreateWipCheckpointAsync(
                    workspace,
                    task.Id,
                    stage.StageId,
                    $"Completed stage '{stage.StageId}'",
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed creating stage completion checkpoint commit for task #{TaskId}", task.Id);
            }

            var checkpoint = new StageCheckpoint
            {
                TaskId = task.Id,
                StageId = stage.StageId,
                GitCommitHash = commitHash,
                SerializedMemory = string.Empty,
                CreatedAt = DateTime.UtcNow
            };
            await _taskRepository.AddCheckpointAsync(checkpoint, cancellationToken);
            pipelineContext.DataBag.Remove(PipelineDataBagKeys.SerializedMemory);

            // Status stamps per stage
            if (string.Equals(stage.StageId, StandardStageIds.TaskReview, StringComparison.OrdinalIgnoreCase))
            {
                task.Status = TaskOverallStatus.Reviewed;
            }
            else if (string.Equals(stage.StageId, StandardStageIds.Implementation, StringComparison.OrdinalIgnoreCase))
            {
                task.Status = TaskOverallStatus.Implemented;
                try
                {
                    var diffStat = await _gitWorkspaceManager.GenerateDiffStatAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
                    pipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewDiffStat, diffStat);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed generating review diff stat after implementation for task #{TaskId}", task.Id);
                }
            }
            else if (string.Equals(stage.StageId, StandardStageIds.PostReviewVerification, StringComparison.OrdinalIgnoreCase))
            {
                // Task status will transition to Ready after Gate 2 verification below
            }

            var nextStageId = result.TargetRoutingStageId;
            IWorkflowStage? nextStage = !string.IsNullOrWhiteSpace(nextStageId)
                ? pipeline.GetStage(nextStageId)
                : pipeline.GetNextStage(stage.StageId);

            if (nextStage != null)
            {
                task.CurrentStageId = nextStage.StageId;

                if (string.Equals(nextStage.StageId, StandardStageIds.CodeReviewAndFix, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nextStage.StageId, StandardStageIds.PostReviewVerification, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var diffStat = await _gitWorkspaceManager.GenerateDiffStatAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
                        pipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewDiffStat, diffStat);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed generating review diff stat on transition to {StageId} for task #{TaskId}", nextStage.StageId, task.Id);
                    }
                }

                pipelineContext.SyncDataBagToTask();
                await _taskRepository.UpdateWithHistoryAsync(
                    task,
                    dependsOn: null,
                    changedBy: AuditActors.TaskOrchestrator,
                    reason: $"Stage '{stage.StageId}' completed. Advanced to '{nextStage.StageId}'. Status: {task.Status}",
                    cancellationToken: cancellationToken);
                await _dependencyStore.UpdateTaskStatusAsync(task.Id, task.Status, cancellationToken);
                await SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

                // Enqueue next stage with priority routing (P1 to llm-urgent, others to llm-inference)
                if (task.Priority == TaskPriority.Critical)
                {
                    task.LastHangfireJobId = _jobQueue.EnqueueUrgent(task.Id);
                }
                else
                {
                    task.LastHangfireJobId = _jobQueue.EnqueueInference(task.Id);
                }
                await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
            }
            else
            {
                // Gate 2: Pre-Review / Ready Gate Verification
                // 1. Commit any pending worktree output
                await _gitWorkspaceManager.CreateWipCheckpointAsync(
                    workspace,
                    task.Id,
                    stage.StageId,
                    "WIP checkpoint before Gate 2 mergeability check",
                    cancellationToken);

                // Refresh base branch
                if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
                {
                    using (await _workspaceLockProvider.AcquireLockAsync(workspace.Id, cancellationToken))
                    {
                        await _gitMirrorSyncService.PullFromHostAsync(workspace.Ext4MirrorPath, task.BaseBranch, cancellationToken);
                    }
                }

                var mergeCheck = await _gitWorkspaceManager.CheckMergeableAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
                if (mergeCheck != null && !mergeCheck.IsMergeable)
                {
                    _logger.LogWarning("Gate 2 detected merge conflict for task #{TaskId} with '{BaseBranch}' in {Count} files",
                        task.Id, task.BaseBranch, mergeCheck.ConflictedFiles.Count);
                    await InitiateConflictResolutionAsync(task, workspace, pipelineContext, mergeCheck.ConflictedFiles, cancellationToken);
                    return;
                }

                // Mergeable: sync from base branch in worktree
                var syncResult = await _gitWorkspaceManager.SyncFromBaseBranchAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
                if (syncResult != null && syncResult.HasConflict)
                {
                    await InitiateConflictResolutionAsync(task, workspace, pipelineContext, syncResult.ConflictedFiles, cancellationToken);
                    return;
                }

                var baseHadAdvanced = syncResult != null && syncResult.MergeCommitSha != null && mergeCheck != null && syncResult.MergeCommitSha != mergeCheck.FeatureBranchSha;
                if (baseHadAdvanced)
                {
                    _logger.LogInformation("Base branch advanced during task #{TaskId} execution. Re-running deterministic verification gates before Ready.", task.Id);
                    var reverifyResult = await stage.ExecuteAsync(pipelineContext, cancellationToken);
                    if (!reverifyResult.IsSuccess)
                    {
                        _logger.LogWarning("Re-verification after sync failed for task #{TaskId}: {Error}. Routing back to Implementation.",
                            task.Id, reverifyResult.ErrorMessage);
                        var loopBackStageId = stage.LoopBackStageId ?? StandardStageIds.Implementation;
                        await HandleLoopBackAsync(task, workspace, pipelineContext, stage, loopBackStageId, reverifyResult, cancellationToken);
                        return;
                    }
                }

                // Final stage reached: Transition to Ready and generate review diff stat
                task.Status = TaskOverallStatus.Ready;

                string diffStat = string.Empty;
                try
                {
                    diffStat = await _gitWorkspaceManager.GenerateDiffStatAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed generating review diff stat for task #{TaskId}", task.Id);
                }

                pipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewDiffStat, diffStat);
                var hasResolvedConflict = pipelineContext.DataBag.Get<bool?>(PipelineDataBagKeys.HasResolvedConflict) == true;
                var readyReason = hasResolvedConflict
                    ? ConflictResolutionConstants.AuditPostReviewVerificationPassedReopened
                    : $"Pipeline completed all stages at '{stage.StageId}'. Status: {task.Status}";

                pipelineContext.SyncDataBagToTask();
                await _taskRepository.UpdateWithHistoryAsync(
                    task,
                    dependsOn: null,
                    changedBy: AuditActors.TaskOrchestrator,
                    reason: readyReason,
                    cancellationToken: cancellationToken);
                await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Ready, cancellationToken);

                if (task.MergeMode == MergeMode.Automatic)
                {
                    _logger.LogInformation("ForgeTask #{TaskId} reached Ready with MergeMode.Automatic; executing auto-merge.", task.Id);
                    using (await _workspaceLockProvider.AcquireLockAsync(workspace.Id, cancellationToken))
                    {
                        await CompleteTaskAndCascadeInternalAsync(task, workspace, $"Auto-merge task #{task.Id}: {task.Title}", cancellationToken);
                    }
                }
                else
                {
                    await SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);
                    await _taskRepository.AddExecutionLogAsync(new ExecutionLog
                    {
                        TaskId = task.Id,
                        Level = LogLevel.Information,
                        Message = $"Pipeline completed all stages for task #{task.Id}. Awaiting maintainer manual review to merge.",
                        Timestamp = DateTime.UtcNow
                    }, cancellationToken);
                }
            }
            return;
        }

        // Loopback / Retry Routing (e.g. deterministic build, test, or lint quality gate failed and routed back to implementation)
        var targetLoopBackId = result.TargetRoutingStageId ?? stage.LoopBackStageId;
        if (!string.IsNullOrWhiteSpace(targetLoopBackId))
        {
            await HandleLoopBackAsync(task, workspace, pipelineContext, stage, targetLoopBackId, result, cancellationToken);
            return;
        }

        // Case C: Permanent Failure
        task.Status = TaskOverallStatus.Failed;
        pipelineContext.SyncDataBagToTask();
        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.TaskOrchestrator,
            reason: $"Pipeline failed at stage '{stage.StageId}'. Error: {result.ErrorMessage ?? "Unknown failure"}",
            cancellationToken: cancellationToken);
        await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Failed, cancellationToken);
        await SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);
        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Error,
            Message = $"Stage '{stage.StageId}' failed: {result.ErrorMessage}",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);
        }
        catch (OperationCanceledException ocex)
        {
            _logger.LogInformation(ocex, "ForgeTask #{TaskId} processing was cancelled.", taskId);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ct = timeoutCts.Token;
            var explicitCancel = _cancellationManager.IsCancelRequested(taskId);
            try
            {
                var convs = await _taskRepository.GetConversationsAsync(taskId, ct);
                var activeConvs = convs.Where(c => c.Status == StageExecutionStatus.Running || c.Status == StageExecutionStatus.WaitingForHuman).ToList();
                foreach (var activeConv in activeConvs)
                {
                    await _taskRepository.CompleteConversationAsync(
                        activeConv.Id,
                        StageExecutionStatus.Cancelled,
                        summary: "Execution was cancelled.",
                        cancellationToken: ct);
                }

                // Only an explicit cancel request is terminal; parent cancellation (e.g. shutdown) leaves the task recoverable.
                if (explicitCancel)
                {
                    var currentTask = await _taskRepository.GetByIdAsync(taskId, ct);
                    if (currentTask != null && currentTask.Status != TaskOverallStatus.Closed && currentTask.Status != TaskOverallStatus.Rejected)
                    {
                        currentTask.Status = TaskOverallStatus.Cancelled;
                        currentTask.IsWaitingForHuman = false;
                        await _taskRepository.UpdateWithHistoryAsync(
                            currentTask,
                            dependsOn: null,
                            changedBy: AuditActors.User,
                            reason: "Execution was cancelled.",
                            cancellationToken: ct);
                        await _dependencyStore.UpdateTaskStatusAsync(taskId, TaskOverallStatus.Cancelled, ct);
                    }
                }
            }
            catch (Exception cex)
            {
                _logger.LogWarning(cex, "Failed to mark active conversation and status as Cancelled for task #{TaskId}", taskId);
            }
        }
        finally
        {
            if (taskCts != null)
            {
                _cancellationManager.Unregister(taskId, taskCts);
                taskCts.Dispose();
            }
        }
    }

    public async Task SubmitHumanAnswerAsync(int questionId, string answer, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new ArgumentException("Answer cannot be blank or empty.", nameof(answer));
        }

        var question = await _taskRepository.GetQuestionByIdAsync(questionId, cancellationToken);
        if (question == null)
        {
            throw new KeyNotFoundException($"Question #{questionId} not found.");
        }

        await SubmitAnswersAsync(question.TaskId, new Dictionary<int, string> { [questionId] = answer }, cancellationToken);
    }

    public async Task SubmitAnswersAsync(int taskId, IReadOnlyDictionary<int, string> answers, CancellationToken cancellationToken = default)
    {
        if (answers == null || answers.Count == 0)
        {
            throw new ArgumentException("Answers collection cannot be null or empty.", nameof(answers));
        }

        foreach (var (qId, ans) in answers)
        {
            if (string.IsNullOrWhiteSpace(ans))
            {
                throw new ArgumentException($"Answer for question #{qId} cannot be blank or empty.", nameof(answers));
            }
        }

        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task == null)
        {
            throw new KeyNotFoundException($"ForgeTask #{taskId} not found.");
        }

        using var lockReleaser = await _workspaceLockProvider.AcquireTaskLockAsync(task.WorkspaceId, task.Id, cancellationToken);

        // Reload fresh task under lock
        task = await _taskRepository.GetByIdAsync(taskId, cancellationToken)
            ?? throw new KeyNotFoundException($"ForgeTask #{taskId} not found.");

        var questionMap = task.Questions.ToDictionary(q => q.Id);
        var updatedBatchIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (qId, ans) in answers)
        {
            if (!questionMap.TryGetValue(qId, out var question))
            {
                question = await _taskRepository.GetQuestionByIdAsync(qId, cancellationToken);
                if (question == null)
                {
                    throw new KeyNotFoundException($"Question #{qId} not found.");
                }
                if (question.TaskId != taskId)
                {
                    throw new InvalidOperationException($"Question #{qId} belongs to task #{question.TaskId}, not task #{taskId}.");
                }
                task.Questions.Add(question);
                questionMap[qId] = question;
            }
            else if (question.TaskId != taskId)
            {
                throw new InvalidOperationException($"Question #{qId} belongs to task #{question.TaskId}, not task #{taskId}.");
            }

            if (question.Status == QuestionStatus.Answered || !string.IsNullOrWhiteSpace(question.Answer))
            {
                throw new InvalidOperationException($"Question #{qId} has already been answered.");
            }

            question.Answer = ans.Trim();
            question.Status = QuestionStatus.Answered;
            question.AnsweredAt = DateTime.UtcNow;

            if (!string.IsNullOrEmpty(question.BatchId))
            {
                updatedBatchIds.Add(question.BatchId);
            }
        }

        if (updatedBatchIds.Count > 1)
        {
            throw new ArgumentException("Submitted questions span multiple clarification batches.", nameof(answers));
        }

        // Determine if all questions in the interruption batch are answered
        string? activeBatchId = updatedBatchIds.FirstOrDefault();
        bool hasPendingQuestionsInBatch = false;

        if (!string.IsNullOrEmpty(activeBatchId))
        {
            hasPendingQuestionsInBatch = task.Questions.Any(q =>
                string.Equals(q.BatchId, activeBatchId, StringComparison.OrdinalIgnoreCase) &&
                q.Status == QuestionStatus.Pending);
        }
        else
        {
            hasPendingQuestionsInBatch = task.Questions.Any(q => q.Status == QuestionStatus.Pending);
        }

        if (hasPendingQuestionsInBatch)
        {
            // Partial submission: keep task waiting
            await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
            await _taskRepository.AddExecutionLogAsync(new ExecutionLog
            {
                TaskId = task.Id,
                Level = LogLevel.Information,
                Message = $"Partial human answers submitted for task #{taskId} ({answers.Count} question(s) answered; awaiting remaining batch questions)",
                Timestamp = DateTime.UtcNow
            }, cancellationToken);
            return;
        }

        // Atomic check-and-clear: resume exactly once
        if (!task.IsWaitingForHuman)
        {
            await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
            return;
        }

        task.IsWaitingForHuman = false;

        // Format numbered question/answer pairs of the current batch
        var batchQuestions = (!string.IsNullOrEmpty(activeBatchId)
            ? task.Questions.Where(q => string.Equals(q.BatchId, activeBatchId, StringComparison.OrdinalIgnoreCase))
            : task.Questions)
            .Where(q => q.Status == QuestionStatus.Answered && !string.IsNullOrWhiteSpace(q.Answer))
            .OrderBy(q => q.Id)
            .ToList();

        var sb = new StringBuilder();
        for (int i = 0; i < batchQuestions.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            sb.AppendLine($"{i + 1}. Question: {batchQuestions[i].Prompt}");
            sb.AppendLine($"   Answer: {batchQuestions[i].Answer}");
        }
        var formattedQna = sb.ToString().TrimEnd();

        var answerEntries = new List<QuestionAnswerEntry>();
        for (int i = 0; i < batchQuestions.Count; i++)
        {
            var q = batchQuestions[i];
            var ans = q.Answer ?? string.Empty;
            int? selectedOptionIndex = null;
            string? selectedOption = null;

            if (!string.IsNullOrWhiteSpace(q.OptionsJson))
            {
                try
                {
                    var options = JsonSerializer.Deserialize<List<string>>(q.OptionsJson);
                    if (options != null)
                    {
                        for (int optIdx = 0; optIdx < options.Count; optIdx++)
                        {
                            if (string.Equals(options[optIdx]?.Trim(), ans.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                selectedOptionIndex = optIdx;
                                selectedOption = options[optIdx];
                                break;
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed options JSON
                }
            }

            answerEntries.Add(new QuestionAnswerEntry(
                QuestionIndex: i,
                QuestionId: q.Id,
                SelectedOptionIndex: selectedOptionIndex,
                SelectedOption: selectedOption,
                AnswerText: ans));
        }

        var structuredJson = JsonSerializer.Serialize(answerEntries);

        var bag = PipelineDataBag.FromJson(task.PipelineDataJson);
        bag.Set(PipelineDataBagKeys.LastHumanAnswer, formattedQna);
        bag.Set(PipelineDataBagKeys.LastHumanAnswerJson, structuredJson);

        // Complete pending tool call in saved conversation memory
        var currentMemory = bag.Get<string>(PipelineDataBagKeys.RecoveredSerializedMemory)
            ?? bag.Get<string>(PipelineDataBagKeys.SerializedMemory);

        if (string.IsNullOrEmpty(currentMemory))
        {
            var lastCheckpoint = task.Checkpoints.OrderByDescending(c => c.CreatedAt).FirstOrDefault();
            if (lastCheckpoint != null && !string.IsNullOrEmpty(lastCheckpoint.SerializedMemory))
            {
                currentMemory = lastCheckpoint.SerializedMemory;
            }
        }

        if (!string.IsNullOrEmpty(currentMemory))
        {
            var updatedMemory = ConversationHistorySerializer.CompletePendingAskToolCall(currentMemory, formattedQna);
            bag.Set(PipelineDataBagKeys.RecoveredSerializedMemory, updatedMemory);
            bag.Set(PipelineDataBagKeys.SerializedMemory, updatedMemory);
        }

        task.PipelineDataJson = bag.ToJson();

        await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
        await _dependencyStore.UpdateTaskStatusAsync(task.Id, task.Status, cancellationToken);
        await _dependencyStore.SetWaitingForHumanAsync(task.Id, false, cancellationToken);

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Information,
            Message = $"Human answers submitted for task #{taskId} (batch {activeBatchId ?? "default"} completed); resumed and enqueued to llm-urgent",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);

        // Preemptive top-priority resumption on llm-urgent
        task.LastHangfireJobId = _jobQueue.EnqueueUrgent(task.Id);
        await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
    }

    public async Task RequestChangesAsync(int taskId, string revisionNotes, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task == null)
        {
            throw new KeyNotFoundException($"ForgeTask #{taskId} not found.");
        }

        var pipeline = await _pipelineProvider.GetPipelineAsync(task, cancellationToken);
        var currentStage = !string.IsNullOrWhiteSpace(task.CurrentStageId) ? pipeline.GetStage(task.CurrentStageId) : null;

        var targetStageId = currentStage?.LoopBackStageId
            ?? pipeline.Stages.LastOrDefault(s => !string.IsNullOrWhiteSpace(s.LoopBackStageId))?.LoopBackStageId
            ?? StandardStageIds.Implementation;

        // Re-route task back to dynamic target stage
        task.Status = TaskOverallStatus.Reviewed;
        task.CurrentStageId = targetStageId;

        var bag = PipelineDataBag.FromJson(task.PipelineDataJson);
        bag.Set(PipelineDataBagKeys.ReviewerFeedback, revisionNotes);
        task.PipelineDataJson = bag.ToJson();

        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.User,
            reason: $"Reviewer requested changes (routing back to '{targetStageId}'): {revisionNotes}",
            cancellationToken: cancellationToken);
        await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Reviewed, cancellationToken);

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Information,
            Message = $"Reviewer requested changes for task #{task.Id} (routing back to '{targetStageId}'): {revisionNotes}",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);

        // Enqueue to llm-urgent for rapid rework
        task.LastHangfireJobId = _jobQueue.EnqueueUrgent(task.Id);
        await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
    }

    public async Task PromoteSuggestedTaskAsync(
        int taskId,
        TaskOverallStatus targetStatus,
        int priority,
        MergeMode mergeMode,
        CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task == null)
        {
            throw new KeyNotFoundException($"ForgeTask #{taskId} not found.");
        }

        if (targetStatus == TaskOverallStatus.Open)
        {
            task.Status = TaskOverallStatus.Open;
            task.Priority = priority;
            task.MergeMode = mergeMode;
            task.UpdatedAt = DateTime.UtcNow;

            await _taskRepository.UpdateWithHistoryAsync(
                task,
                dependsOn: null,
                changedBy: AuditActors.User,
                reason: $"Promoted task from Suggested to Open with Priority P{priority} and {mergeMode}.",
                cancellationToken: cancellationToken);
            await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Open, cancellationToken);

            var isReady = await _dependencyEngine.IsReadyForExecutionAsync(task.Id, cancellationToken);
            if (isReady)
            {
                if (task.Priority == TaskPriority.Critical)
                {
                    task.LastHangfireJobId = _jobQueue.EnqueueUrgent(task.Id);
                }
                else
                {
                    task.LastHangfireJobId = _jobQueue.EnqueueInference(task.Id);
                }

                await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
            }
        }
        else if (targetStatus == TaskOverallStatus.Rejected)
        {
            await RejectTaskAsync(taskId, "Rejected suggested task.", cancellationToken);
            return;
        }
        else
        {
            throw new ArgumentException($"Invalid target status '{targetStatus}' for promotion of suggested task. Must be Open or Rejected.", nameof(targetStatus));
        }

        var workspace = task.Workspace ?? await _workspaceRepository.GetByIdAsync(task.WorkspaceId, cancellationToken);
        if (workspace != null)
        {
            await SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);
        }

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Information,
            Message = $"ForgeTask #{task.Id} promoted to status {targetStatus} (Priority: {priority}, MergeMode: {mergeMode}).",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);
    }

    public async Task RejectTaskAsync(int taskId, string? reason = null, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task == null)
        {
            throw new KeyNotFoundException($"ForgeTask #{taskId} not found.");
        }

        if (task.Status == TaskOverallStatus.Closed || task.Status == TaskOverallStatus.Rejected)
        {
            throw new InvalidOperationException($"Task #{taskId} is already in a terminal state ({task.Status}).");
        }

        try
        {
            _cancellationManager.Cancel(task.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cancel active execution token for rejected Task #{TaskId}", taskId);
        }

        if (!string.IsNullOrWhiteSpace(task.LastHangfireJobId))
        {
            try
            {
                _jobQueue.Delete(task.LastHangfireJobId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cancel Hangfire job {JobId} for rejected Task #{TaskId}", task.LastHangfireJobId, taskId);
            }
            task.LastHangfireJobId = null;
        }

        task.Status = TaskOverallStatus.Rejected;
        task.IsWaitingForHuman = false;
        task.UpdatedAt = DateTime.UtcNow;

        var bag = PipelineDataBag.FromJson(task.PipelineDataJson);
        bag.Remove(PipelineDataBagKeys.HasResolvedConflict);
        task.PipelineDataJson = bag.ToJson();

        var rejectionReason = string.IsNullOrWhiteSpace(reason)
            ? "Task manually rejected by maintainer."
            : reason;

        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.User,
            reason: rejectionReason,
            cancellationToken: cancellationToken);

        await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Rejected, cancellationToken);

        var workspace = task.Workspace ?? await _workspaceRepository.GetByIdAsync(task.WorkspaceId, cancellationToken);
        if (workspace != null)
        {
            await SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);
        }

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Warning,
            Message = $"ForgeTask #{task.Id} was rejected by maintainer. Reason: {rejectionReason}",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);
    }

    public async Task<CompleteTaskResult> CompleteTaskAndCascadeAsync(int taskId, string finalCommitMessage, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task == null)
        {
            throw new KeyNotFoundException($"ForgeTask #{taskId} not found.");
        }

        using var lockReleaser = await _workspaceLockProvider.AcquireLockAsync(task.WorkspaceId, cancellationToken);
        var workspace = task.Workspace ?? await _workspaceRepository.GetByIdAsync(task.WorkspaceId, cancellationToken);
        if (workspace == null)
        {
            throw new InvalidOperationException($"Workspace #{task.WorkspaceId} not found.");
        }

        return await CompleteTaskAndCascadeInternalAsync(task, workspace, finalCommitMessage, cancellationToken);
    }

    private async Task<CompleteTaskResult> CompleteTaskAndCascadeInternalAsync(
        ForgeTask task,
        Workspace workspace,
        string finalCommitMessage,
        CancellationToken cancellationToken)
    {
        int pushRetries = 0;
        while (true)
        {
            var preSquashSha = await _gitWorkspaceManager.GetBranchShaAsync(workspace, task.BaseBranch, cancellationToken);

            string squashSha;
            try
            {
                squashSha = await _gitWorkspaceManager.SquashAndMergeAsync(
                    workspace,
                    task.Id,
                    task.BaseBranch,
                    finalCommitMessage,
                    cancellationToken);
            }
            catch (MergeConflictException ex)
            {
                _logger.LogWarning(ex, "Squash and merge failed for ForgeTask #{TaskId} due to merge conflict with '{BaseBranch}'.", task.Id, task.BaseBranch);

                var pipelineContext = new PipelineContext(task, workspace);
                await InitiateConflictResolutionAsync(task, workspace, pipelineContext, ex.ConflictedFiles, cancellationToken, discardApproval: task.MergeMode == MergeMode.ManualReviewRequired);

                return new CompleteTaskResult(CompleteTaskStatus.ConflictResolutionDispatched, ConflictedFiles: ex.ConflictedFiles);
            }


            // If ext4 mirror is active, push the merged base branch back to host-origin so developer host repository is updated!
            if (_gitMirrorSyncService != null && !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
            {
                var pushResult = await _gitMirrorSyncService.PushBaseBranchToHostAsync(
                    workspace.Ext4MirrorPath,
                    task.BaseBranch,
                    cancellationToken);

                if (pushResult.IsNonFastForward)
                {
                    if (pushRetries < ConflictResolutionConstants.MaxPushRetries)
                    {
                        pushRetries++;
                        _logger.LogWarning("Non-fast-forward push rejection for task #{TaskId}. Resetting to pre-squash SHA {PreSquashSha}, pulling from host and retrying (attempt {Attempt}/{Max}).",
                            task.Id, preSquashSha, pushRetries, ConflictResolutionConstants.MaxPushRetries);

                        if (!string.IsNullOrEmpty(preSquashSha))
                        {
                            await _gitWorkspaceManager.ResetCanonicalBranchAsync(workspace, task.BaseBranch, preSquashSha, cancellationToken);
                        }
                        await _gitMirrorSyncService.PullFromHostAsync(workspace.Ext4MirrorPath, task.BaseBranch, cancellationToken);
                        continue;
                    }

                    _logger.LogError("Mirror push rejected as non-fast-forward after {MaxRetries} retries for task #{TaskId}.",
                        ConflictResolutionConstants.MaxPushRetries, task.Id);
                    if (!string.IsNullOrEmpty(preSquashSha))
                    {
                        await _gitWorkspaceManager.ResetCanonicalBranchAsync(workspace, task.BaseBranch, preSquashSha, cancellationToken);
                    }
                    throw new MirrorPushRejectedException(task.BaseBranch, workspace.Ext4MirrorPath, pushResult.WarningMessage ?? "Non-fast-forward push rejection.");
                }
                else if (!pushResult.Success)
                {
                    if (!string.IsNullOrEmpty(preSquashSha))
                    {
                        await _gitWorkspaceManager.ResetCanonicalBranchAsync(workspace, task.BaseBranch, preSquashSha, cancellationToken);
                    }
                    throw new GitCommandException(workspace.Ext4MirrorPath, new[] { "push", "host-origin", task.BaseBranch }, 1, string.Empty, pushResult.WarningMessage ?? "Push failed");
                }
            }

            task.SquashCommitSha = squashSha;

            // Push succeeded! Remove task worktree now
            try
            {
                await _gitWorkspaceManager.RemoveWorktreeAsync(workspace, task.Id, cancellationToken);
            }
            catch (Exception wex)
            {
                _logger.LogWarning(wex, "Failed to remove worktree for completed task #{TaskId}", task.Id);
            }

            break;
        }

        // 2. Mark task as completed (Closed)
        task.Status = TaskOverallStatus.Closed;
        task.CurrentStageId = string.Empty;

        var closedBag = PipelineDataBag.FromJson(task.PipelineDataJson);
        closedBag.Remove(PipelineDataBagKeys.HasResolvedConflict);
        closedBag.Remove(PipelineDataBagKeys.ConflictResolutionAttempts);
        task.PipelineDataJson = closedBag.ToJson();
        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.TaskOrchestrator,
            reason: $"ForgeTask #{task.Id} completed and squash-merged to {task.BaseBranch}.",
            cancellationToken: cancellationToken);
        await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Closed, cancellationToken);

        try
        {
            await _streamer.BroadcastAsync(new Streaming.AgentActivityEvent(
                Guid.NewGuid().ToString("N"),
                task.Id,
                StandardStageIds.Merge,
                Streaming.AgentActivityKind.StageCompleted,
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
            var orderedUnblocked = unblockedTasks
                .OrderBy(t => t.Priority)     // 1 (Highest) first
                .ThenBy(t => t.CreatedAt)    // Older tasks first on priority tie
                .ToList();

            foreach (var unblockedTask in orderedUnblocked)
            {
                _logger.LogInformation(
                    "ForgeTask #{PrereqId} completed; unblocked dependent task #{UnblockedId} (Priority: {Priority}).",
                    task.Id, unblockedTask.Id, unblockedTask.Priority);

                if (unblockedTask.Priority == TaskPriority.Critical)
                {
                    unblockedTask.LastHangfireJobId = _jobQueue.EnqueueUrgent(unblockedTask.Id);
                }
                else
                {
                    unblockedTask.LastHangfireJobId = _jobQueue.EnqueueInference(unblockedTask.Id);
                }
                await _taskRepository.UpdateAsync(unblockedTask, cancellationToken: cancellationToken);
            }

            var foundIds = unblockedTasks.Select(t => t.Id).ToHashSet();
            foreach (var missingId in unblocked.Where(id => !foundIds.Contains(id)))
            {
                _logger.LogWarning("ForgeTask #{PrereqId} completed; unblocked dependent task #{UnblockedId} was not found in task repository.", task.Id, missingId);
                _jobQueue.EnqueueInference(missingId);
            }
        }

        await SyncMarkdownTaskSafeAsync(workspace, task, cancellationToken);

        if (!string.IsNullOrWhiteSpace(workspace.RepositoryPath))
        {
            try
            {
                await _markdownSyncService.ArchiveOrCleanupTaskFileAsync(task, workspace.RepositoryPath, archive: true, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed archiving markdown file for closed task #{TaskId}", task.Id);
            }
        }

        return new CompleteTaskResult(CompleteTaskStatus.Completed);
    }

    private async Task InitiateConflictResolutionAsync(
        ForgeTask task,
        Workspace workspace,
        PipelineContext pipelineContext,
        IReadOnlyList<string> conflictedFiles,
        CancellationToken cancellationToken,
        bool discardApproval = false)
    {
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

        if (task.Priority == TaskPriority.Critical)
        {
            task.LastHangfireJobId = _jobQueue.EnqueueUrgent(task.Id);
        }
        else
        {
            task.LastHangfireJobId = _jobQueue.EnqueueInference(task.Id);
        }

        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.TaskOrchestrator,
            reason: reason,
            cancellationToken: cancellationToken);

        await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Open, cancellationToken);
        await _dependencyStore.SetWaitingForHumanAsync(task.Id, false, cancellationToken);

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Warning,
            Message = reason,
            Timestamp = DateTime.UtcNow
        }, cancellationToken);
    }

    private async Task HandleLoopBackAsync(
        ForgeTask task,
        Workspace workspace,
        PipelineContext pipelineContext,
        IWorkflowStage stage,
        string targetLoopBackId,
        StageExecutionResult result,
        CancellationToken cancellationToken)
    {
        var retries = pipelineContext.IncrementRetry(stage.StageId);
        var effectiveMaxRetries = pipelineContext.DataBag.Get<int?>(PipelineDataBagKeys.MaxRetries) ?? WorkflowDefaults.DefaultMaxStageRetries;
        if (retries <= effectiveMaxRetries)
        {
            _logger.LogWarning("Stage '{StageId}' failed, routing backward to '{TargetStage}' (Retry {Retry}/{Max}): {Reason}",
                stage.StageId, targetLoopBackId, retries, effectiveMaxRetries, result.ErrorMessage);

            var errors = pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.CompilerErrors) ?? new List<string>();
            var failedTests = pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.FailedTests);
            if (failedTests is { Count: > 0 })
            {
                errors.AddRange(failedTests.Select(ft => $"[{stage.StageId} test failure] {ft}"));
            }
            var newWarnings = pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.WarningsNew);
            if (newWarnings is { Count: > 0 } && (string.Equals(stage.StageId, StageImplementationTypes.DeterministicRatchet, StringComparison.OrdinalIgnoreCase) || stage.StageId.Contains("ratchet", StringComparison.OrdinalIgnoreCase)))
            {
                errors.AddRange(newWarnings.Select(w => $"[warning ratchet] {w}"));
            }
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                errors.Add($"[{stage.StageId} attempt {retries}] {result.ErrorMessage}");
            }
            pipelineContext.DataBag.Set(PipelineDataBagKeys.CompilerErrors, errors);

            task.CurrentStageId = targetLoopBackId;
            pipelineContext.SyncDataBagToTask();
            await _taskRepository.UpdateWithHistoryAsync(
                task,
                dependsOn: null,
                changedBy: AuditActors.TaskOrchestrator,
                reason: $"Quality gate '{stage.StageId}' failed. Looping back to '{targetLoopBackId}' (attempt {retries}/{effectiveMaxRetries}).",
                cancellationToken: cancellationToken);
            await _dependencyStore.UpdateTaskStatusAsync(task.Id, task.Status, cancellationToken);
            await SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

            await _taskRepository.AddExecutionLogAsync(new ExecutionLog
            {
                TaskId = task.Id,
                Level = LogLevel.Warning,
                Message = $"Quality gate '{stage.StageId}' failed: {result.ErrorMessage}. Looping back to '{targetLoopBackId}' (attempt {retries}/{effectiveMaxRetries}).",
                Timestamp = DateTime.UtcNow
            }, cancellationToken);

            task.LastHangfireJobId = _jobQueue.EnqueueUrgent(task.Id);
            await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
            return;
        }

        task.Status = TaskOverallStatus.Failed;
        pipelineContext.SyncDataBagToTask();
        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.TaskOrchestrator,
            reason: $"Stage '{stage.StageId}' failed and exceeded max retry limit ({effectiveMaxRetries}). Error: {result.ErrorMessage}",
            cancellationToken: cancellationToken);
        await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Failed, cancellationToken);
        await SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);
        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Error,
            Message = $"Stage '{stage.StageId}' exceeded max retry limit ({effectiveMaxRetries}): {result.ErrorMessage}",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);
    }

    private static bool HasCustomHandler(IWorkflowStage stage)
    {
        var prop = stage.GetType().GetProperty("Handler");
        return prop != null && prop.GetValue(stage) != null;
    }

    private static StageExecutionResult MapAgentRunResultToStageResult(
        AgentRunResult runResult,
        IWorkflowStage stage,
        AgentProfile? profile,
        DateTime startedAt)
    {
        var completedAt = DateTime.UtcNow;
        var durationMs = (int)(completedAt - startedAt).TotalMilliseconds;
        var metrics = runResult.Metrics ?? new AgentRunMetrics(durationMs);

        var record = new StageExecutionRecord
        {
            StageId = stage.StageId,
            StageKind = stage.Kind.ToString(),
            AgentProfileId = profile?.Id ?? string.Empty,
            HarnessId = profile?.HarnessId ?? string.Empty,
            ModelName = profile?.ModelName ?? string.Empty,
            TargetRouting = runResult.TargetRouting,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationMs = durationMs,
            PromptTokens = (int)metrics.PromptTokens,
            CompletionTokens = (int)metrics.CompletionTokens,
            TotalTokens = (int)metrics.TotalTokens,
            Status = runResult.Status switch
            {
                AgentRunStatus.Completed => StageExecutionStatus.Completed,
                AgentRunStatus.WaitingForHuman => StageExecutionStatus.Completed,
                AgentRunStatus.Cancelled => StageExecutionStatus.Cancelled,
                _ => StageExecutionStatus.Failed
            }
        };

        if (runResult.Status == AgentRunStatus.Cancelled)
        {
            return StageExecutionResult.Cancelled(runResult.Error ?? "Agent execution cancelled.", record);
        }

        if (runResult.Status == AgentRunStatus.WaitingForHuman)
        {
            var questions = runResult.PendingQuestions
                ?? throw new InvalidOperationException("AgentRunResult WaitingForHuman must contain PendingQuestions.");
            return StageExecutionResult.WaitingForHuman(questions, record);
        }

        if (runResult.Status == AgentRunStatus.Completed)
        {
            if (!string.IsNullOrWhiteSpace(runResult.TargetRouting))
            {
                var loopTarget = (!string.IsNullOrWhiteSpace(stage.LoopBackStageId) && string.Equals(runResult.TargetRouting, StandardStageIds.Implementation, StringComparison.OrdinalIgnoreCase))
                    ? stage.LoopBackStageId
                    : (runResult.TargetRouting ?? stage.LoopBackStageId);

                return StageExecutionResult.Failure(
                    runResult.Summary ?? "Code reviewer requested changes.",
                    loopBackStageId: loopTarget,
                    record: record);
            }

            return StageExecutionResult.Success(record: record);
        }

        return StageExecutionResult.Failure(runResult.Error ?? "Stage failed.", record: record);
    }

    private async Task SyncMarkdownTaskSafeAsync(Workspace workspace, ForgeTask task, CancellationToken cancellationToken)
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
            catch (Exception mex)
            {
                _logger.LogWarning(mex, "Failed to mirror task #{TaskId} to docs/tasks/*.md", task.Id);
            }
        }
    }

    private async Task SyncTaskMarkdownAndMirrorAsync(ForgeTask task, Workspace workspace, CancellationToken cancellationToken)
    {
        await SyncMarkdownTaskSafeAsync(workspace, task, cancellationToken);

        if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            try
            {
                await _gitMirrorSyncService.PushToHostAsync(workspace.Ext4MirrorPath, task.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed pushing ext4 mirror changes for task #{TaskId} to host repository", task.Id);
            }
        }
    }
}
