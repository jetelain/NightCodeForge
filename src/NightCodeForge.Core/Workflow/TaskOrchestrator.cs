using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Streaming;
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
    private readonly IExecutionEnvironmentProvider _environmentProvider;
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
    private readonly IOperationsControlService _operationsControlService;
    private readonly IOperationsTelemetryService _telemetryService;
    private readonly ILogger<TaskOrchestrator> _logger;

    private static readonly TimeSpan PauseAbortTimeout = TimeSpan.FromSeconds(5);

    public TaskOrchestrator(
        ITaskRepository taskRepository,
        IWorkspaceRepository workspaceRepository,
        IDependencyResolutionEngine dependencyEngine,
        IWorkspaceLockProvider workspaceLockProvider,
        IGitWorkspaceManager gitWorkspaceManager,
        IExecutionEnvironmentProvider environmentProvider,
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
        IOperationsControlService operationsControlService,
        IOperationsTelemetryService telemetryService,
        ILogger<TaskOrchestrator> logger)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _dependencyEngine = dependencyEngine ?? throw new ArgumentNullException(nameof(dependencyEngine));
        _workspaceLockProvider = workspaceLockProvider ?? throw new ArgumentNullException(nameof(workspaceLockProvider));
        _gitWorkspaceManager = gitWorkspaceManager ?? throw new ArgumentNullException(nameof(gitWorkspaceManager));
        _environmentProvider = environmentProvider ?? throw new ArgumentNullException(nameof(environmentProvider));
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
        _operationsControlService = operationsControlService ?? throw new ArgumentNullException(nameof(operationsControlService));
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
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

        if (_operationsControlService.IsPaused)
        {
            _logger.LogInformation("Task #{TaskId} execution deferred: operations are globally paused.", taskId);
            if (task.Status == TaskOverallStatus.Open)
            {
                task.IsDeferredByPause = true;
                await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
            }
            return;
        }

        // The cancellation source is registered only once the task lock is held, so that a duplicate job
        // waiting on the lock can never cancel the execution that is already running.
        CancellationTokenSource? taskCts = null;
        IWorkflowStage? currentExecutingStage = null;
        AgentProfile? currentExecutingProfile = null;
        var cohortSw = System.Diagnostics.Stopwatch.StartNew();
        CohortExitReason cohortExitReason = CohortExitReason.Aborted;

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

        var initialJobId = task.LastHangfireJobId;

        // 2. ForgeTask Lock Acquisition (Enables concurrent execution of different tasks on the same workspace)
        using var lockReleaser = await _workspaceLockProvider.AcquireTaskLockAsync(task.WorkspaceId, task.Id, cancellationToken);

        // Re-read task state after acquiring the lock to guarantee we have the latest persisted state
        var reloadedTask = await _taskRepository.GetByIdAsync(task.Id, cancellationToken);
        if (reloadedTask == null)
        {
            _logger.LogError("ForgeTask #{TaskId} not found after acquiring lock.", taskId);
            return;
        }

        // The task may have been cancelled, closed, or rejected while waiting for the lock.
        if (reloadedTask.Status == TaskOverallStatus.Cancelled || reloadedTask.Status == TaskOverallStatus.Closed || reloadedTask.Status == TaskOverallStatus.Rejected)
        {
            _logger.LogInformation("ForgeTask #{TaskId} is now in {Status} status; aborting execution.", taskId, reloadedTask.Status);
            if (reloadedTask.Status == TaskOverallStatus.Cancelled)
            {
                cohortExitReason = CohortExitReason.Cancelled;
            }
            return;
        }

        // Detect if this execution was superseded by a restart or re-enqueue while waiting for the lock.
        if (!string.IsNullOrWhiteSpace(initialJobId) && !string.IsNullOrWhiteSpace(reloadedTask.LastHangfireJobId) && !string.Equals(reloadedTask.LastHangfireJobId, initialJobId, StringComparison.Ordinal))
        {
            _logger.LogInformation("ForgeTask #{TaskId} execution was superseded while waiting for lock (Old JobId: '{OldJobId}', New JobId: '{NewJobId}'); aborting stale execution.",
                taskId, initialJobId, reloadedTask.LastHangfireJobId);
            return;
        }

        task = reloadedTask;

        if (_operationsControlService.IsPaused)
        {
            _logger.LogInformation("Task #{TaskId} execution deferred after acquiring lock: operations are globally paused.", taskId);
            cohortExitReason = CohortExitReason.Paused;
            if (task.Status == TaskOverallStatus.Open)
            {
                task.IsDeferredByPause = true;
                await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
            }
            return;
        }

        taskCts = _cancellationManager.Register(task.Id, cancellationToken);
        cancellationToken = taskCts.Token;

        var workspace = task.Workspace ?? await _workspaceRepository.GetByIdAsync(task.WorkspaceId, cancellationToken);
        if (workspace == null)
        {
            var err = $"Workspace #{task.WorkspaceId} not found for task #{taskId}.";
            _logger.LogError(err);
            cohortExitReason = CohortExitReason.Failed;
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed checking out feature branch for task #{TaskId}", taskId);
            cohortExitReason = CohortExitReason.Failed;
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
            cohortExitReason = CohortExitReason.Failed;
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

        bool isResumedRun = task.IsWaitingForHuman || !string.IsNullOrWhiteSpace(task.InterruptedStageId);
        string? activeInterruptedStageId = task.InterruptedStageId ?? (task.IsWaitingForHuman ? task.CurrentStageId : null);
        if (task.IsWaitingForHuman)
        {
            task.IsWaitingForHuman = false;
            await _dependencyStore.SetWaitingForHumanAsync(task.Id, false, cancellationToken);
        }
        if (!string.IsNullOrWhiteSpace(task.InterruptedStageId))
        {
            task.InterruptedStageId = null;
        }
        if (task.IsDeferredByPause)
        {
            task.IsDeferredByPause = false;
            await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
        }

        var taskWorkingPath = _gitWorkspaceManager.GetWorktreePath(workspace, task.Id);
        var pipelineContext = new PipelineContext(task, workspace)
        {
            WorkingDirectory = taskWorkingPath,
            Pipeline = pipeline
        };

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

        // Explicit maintainer resume after an escalation: grant a fresh conflict-resolution budget.
        if (isResumedRun &&
            (pipelineContext.DataBag.Get<int?>(PipelineDataBagKeys.ConflictResolutionAttempts) ?? 0) >= ConflictResolutionConstants.MaxConflictResolutionAttempts)
        {
            pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictResolutionAttempts, 0);
        }

        var isFirstIteration = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            // 1. Check Global Pause
            if (_operationsControlService.IsPaused)
            {
                _logger.LogInformation("Task #{TaskId} execution deferred: operations are globally paused.", taskId);
                task.IsDeferredByPause = true;
                task.CurrentStageId = stage.StageId;
                task.InterruptedStageId = activeInterruptedStageId ?? stage.StageId;
                pipelineContext.SyncDataBagToTask();
                await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
                cohortExitReason = CohortExitReason.Paused;
                return;
            }

            // 2. Check Urgent Preemption (skipped on the first iteration)
            if (!isFirstIteration && await _jobQueue.HasUrgentJobsWaitingAsync(cancellationToken))
            {
                _logger.LogInformation("Cooperatively yielding task #{TaskId} before stage '{StageId}' (not yet executed; CurrentStageId == StageId, resume re-enters here) for pending P1 Urgent job.", task.Id, stage.StageId);
                task.CurrentStageId = stage.StageId;
                task.InterruptedStageId = activeInterruptedStageId;
                pipelineContext.SyncDataBagToTask();
                task.LastHangfireJobId = _jobQueue.EnqueueStage(task.Id, stage.QueueTier, task.Priority);
                await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
                cohortExitReason = CohortExitReason.Yielded;
                return;
            }

            isFirstIteration = false;

            currentExecutingStage = stage;
            task.CurrentStageId = stage.StageId;

            // Persist current stage immediately so UI / Kanban / DAG shows task is actively executing
            var stageIndex = pipeline.Stages.ToList().FindIndex(s => string.Equals(s.StageId, stage.StageId, StringComparison.OrdinalIgnoreCase));
            var stageOrder = stageIndex >= 0 ? stageIndex + 1 : 1;
            task.HasExecutionRun = true;
            task.MaxExecutedStageOrder = Math.Max(task.MaxExecutedStageOrder, stageOrder);
            await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);

            // Record active executing task authoritatively in telemetry
            var isDeterministic = stage.Kind == WorkflowStageKind.Deterministic;
            var queueName = isDeterministic
                ? DeterministicStageConstants.CpuDeterministicQueue
                : DeterministicStageConstants.LlmInferenceQueue;
            var hardwareTarget = isDeterministic ? "Deterministic CPU" : "Local GPU";

            _telemetryService.RecordActiveTask(new ActiveTaskSummary(
                task.Id,
                task.Title ?? string.Empty,
                stage.StageId,
                task.Priority,
                queueName,
                DateTime.UtcNow,
                hardwareTarget));

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

            currentExecutingProfile = profile;
            pipelineContext.CurrentProfile = profile;

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

                    cohortExitReason = CohortExitReason.Failed;
                    throw notFoundEx;
                }
            }

            // Start Conversation immediately for deterministic stages so Task Inspector tracks active run and progression in real-time
            Conversation? deterministicConv = null;
            bool deterministicConvFinalized = false;
            IReadOnlyDictionary<int, Services.TaskBaselineEntry>? baselineSnapshot = null;
            StageExecutionResult? result = null;
            var startedAt = DateTime.UtcNow;
            int durationMs = 0;
        if (stage.Kind == WorkflowStageKind.Deterministic)
        {
            try
            {
                int attemptIndex = (pipelineContext.StageRetries.GetValueOrDefault(stage.StageId, 0)) + 1;
                deterministicConv = await _taskRepository.StartConversationAsync(
                    task.Id,
                    stage.StageId,
                    WorkflowStageKind.Deterministic,
                    attemptIndex,
                    agentProfileId: null,
                    harnessId: StandardHarnessIds.NativeCli,
                    modelName: DeterministicStageConstants.CpuModelName,
                    cancellationToken: cancellationToken);

                pipelineContext.CurrentConversation = deterministicConv;

                // Turn 1: User prompt
                await _taskRepository.AddConversationTurnAsync(
                    deterministicConv.Id,
                    StageTurnRole.User,
                    content: $"Execute deterministic gate '{stage.DisplayName}' ({stage.StageId})",
                    cancellationToken: cancellationToken);
            }
            catch (Exception dex)
            {
                _logger.LogWarning(dex, "Failed starting deterministic conversation for task #{TaskId}, stage '{StageId}'", task.Id, stage.StageId);
            }
        }

        try
        {
            try
            {
                await _streamer.BroadcastAsync(new Streaming.AgentActivityEvent(
                Guid.NewGuid().ToString("N"),
                task.Id,
                stage.StageId,
                Streaming.AgentActivityKind.StageStarted,
                $"Starting stage '{stage.StageId}'",
                DateTime.UtcNow,
                new Dictionary<string, object?>
                {
                    [Streaming.AgentActivityMetadataKeys.StageKind] = stage.Kind.ToString(),
                    [Streaming.AgentActivityMetadataKeys.TaskTitle] = task.Title,
                    [Streaming.AgentActivityMetadataKeys.Priority] = task.Priority,
                    [Streaming.AgentActivityMetadataKeys.QueueType] = stage.Kind == WorkflowStageKind.Deterministic
                        ? DeterministicStageConstants.CpuDeterministicQueue
                        : DeterministicStageConstants.LlmInferenceQueue
                }), cancellationToken);
        }
        catch (Exception sex)
        {
            _logger.LogDebug(sex, "Failed broadcasting stage started event for task #{TaskId}", task.Id);
        }

        baselineSnapshot = null;
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
                    isResumedRun = false;
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
            var syncToolName = $"{stage.StageId}{DeterministicStageConstants.GitSyncToolSuffix}";
            var syncCmd = $"git merge {task.BaseBranch}";
            var syncSw = System.Diagnostics.Stopwatch.StartNew();

            if (deterministicConv != null)
            {
                try
                {
                    await _streamer.BroadcastAsync(new Streaming.AgentActivityEvent(
                        Guid.NewGuid().ToString("N"),
                        task.Id,
                        stage.StageId,
                        Streaming.AgentActivityKind.ToolStarted,
                        $"{syncToolName}: Syncing from '{task.BaseBranch}'...",
                        DateTime.UtcNow,
                        new Dictionary<string, object?>
                        {
                            [Streaming.AgentActivityMetadataKeys.ToolName] = syncToolName
                        }), cancellationToken);

                    await _taskRepository.AddConversationTurnAsync(
                        deterministicConv.Id,
                        StageTurnRole.Tool,
                        content: $"Sync from base branch '{task.BaseBranch}'",
                        toolName: syncToolName,
                        toolArguments: syncCmd,
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed broadcasting or recording Gate 1 sync tool started turn for task #{TaskId}", task.Id);
                }
            }

            await _gitWorkspaceManager.CreateWipCheckpointAsync(
                workspace,
                task.Id,
                StandardStageIds.TaskReview,
                "WIP checkpoint before Pre-Implementation Baseline sync",
                cancellationToken);

            var baselineSyncResult = await _gitWorkspaceManager.SyncFromBaseBranchAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
            syncSw.Stop();
            var syncDuration = (int)syncSw.ElapsedMilliseconds;

            if (deterministicConv != null)
            {
                try
                {
                    var syncResultMsg = baselineSyncResult.HasConflict
                        ? $"Merge conflict with '{task.BaseBranch}' in {baselineSyncResult.ConflictedFiles.Count} files."
                        : $"Successfully synchronized from base branch '{task.BaseBranch}'.";

                    await _taskRepository.AddConversationTurnAsync(
                        deterministicConv.Id,
                        StageTurnRole.ToolResult,
                        content: syncResultMsg,
                        toolName: syncToolName,
                        toolResult: syncResultMsg,
                        durationMs: syncDuration,
                        isError: baselineSyncResult.HasConflict,
                        cancellationToken: cancellationToken);

                    await _streamer.BroadcastAsync(new Streaming.AgentActivityEvent(
                        Guid.NewGuid().ToString("N"),
                        task.Id,
                        stage.StageId,
                        Streaming.AgentActivityKind.ToolCompleted,
                        $"{syncToolName}: {syncResultMsg}",
                        DateTime.UtcNow,
                        new Dictionary<string, object?>
                        {
                            [Streaming.AgentActivityMetadataKeys.ToolName] = syncToolName,
                            [Streaming.AgentActivityMetadataKeys.DurationMs] = syncDuration
                        }), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed broadcasting or recording Gate 1 sync tool completed turn for task #{TaskId}", task.Id);
                }
            }

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

                if (deterministicConv != null)
                {
                    try
                    {
                        var summary = $"Deterministic gate '{stage.DisplayName}' failed: {conflictMsg}";
                        await _taskRepository.AddConversationTurnAsync(
                            deterministicConv.Id,
                            StageTurnRole.Assistant,
                            content: summary,
                            cancellationToken: CancellationToken.None);

                        await _taskRepository.CompleteConversationAsync(
                            deterministicConv.Id,
                            StageExecutionStatus.Failed,
                            summary: summary,
                            cancellationToken: CancellationToken.None);
                        deterministicConvFinalized = true;
                    }
                    catch (Exception cex)
                    {
                        _logger.LogWarning(cex, "Failed completing deterministic conversation on Gate 1 merge conflict for task #{TaskId}", task.Id);
                    }
                }

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

        startedAt = DateTime.UtcNow;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (stage.Kind == WorkflowStageKind.Agentic && !HasCustomHandler(stage))
            {
                var harnessId = profile!.HarnessId;
                if (_harnessRegistry.TryGetHarness(harnessId, out var harness) && harness != null)
                {
                    var lastCheckpoint = task.Checkpoints?.OrderByDescending(c => c.CreatedAt).FirstOrDefault();
                    bool isInterruptedStage = !string.IsNullOrWhiteSpace(activeInterruptedStageId) &&
                        string.Equals(stage.StageId, activeInterruptedStageId, StringComparison.OrdinalIgnoreCase);

                    string? pendingHumanAnswer = null;
                    string? pendingHumanAnswerJson = null;

                    if (isInterruptedStage)
                    {
                        pendingHumanAnswer = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.LastHumanAnswer);
                        pendingHumanAnswerJson = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.LastHumanAnswerJson);
                        // Single-consumption: remove immediately upon consumption
                        pipelineContext.DataBag.Remove(PipelineDataBagKeys.LastHumanAnswer);
                        pipelineContext.DataBag.Remove(PipelineDataBagKeys.LastHumanAnswerJson);
                        activeInterruptedStageId = null;
                    }
                    else if (string.IsNullOrWhiteSpace(activeInterruptedStageId))
                    {
                        // Defensive: ensure stale keys are purged if there is no pending interrupted stage waiting to consume them
                        pipelineContext.DataBag.Remove(PipelineDataBagKeys.LastHumanAnswer);
                        pipelineContext.DataBag.Remove(PipelineDataBagKeys.LastHumanAnswerJson);
                    }

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
                            await PopulateReviewDiffsAsync(workspace, task, pipelineContext, cancellationToken);
                        }

                        var bootstrapPrompt = stage.BuildBootstrapPrompt(task, pipelineContext, stageDirective);

                        SyntheticPrimingExecution? syntheticPriming = null;
                        var pendingPriming = pipelineContext.DataBag.Get<SyntheticPrimingExecution>(PipelineDataBagKeys.PendingSyntheticPriming);
                        var effectiveOptimizationFlags = pipelineContext.DataBag.Get<LocalOptimizationFlags>(PipelineDataBagKeys.OptimizationFlags)
                            ?? profile.OptimizationFlags ?? new LocalOptimizationFlags();

                        if (pendingPriming != null)
                        {
                            pipelineContext.DataBag.Remove(PipelineDataBagKeys.PendingSyntheticPriming);
                            if (effectiveOptimizationFlags.SyntheticConversationPriming)
                            {
                                syntheticPriming = pendingPriming;
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
                            PendingHumanAnswer = pendingHumanAnswer,
                            PendingHumanAnswerJson = pendingHumanAnswerJson,
                            SyntheticPriming = syntheticPriming,
                            TaskMarkdownPath = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath),
                            ReviewDiffStat = reviewDiffStat,
                            ApprovedPlan = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.ApprovedPlan)
                                ?? pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.StageSummary(StandardStageIds.TaskReview)),
                            ReviewerFeedback = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.ReviewerFeedback),
                            PriorDiagnostics = pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.CompilerErrors),
                            BootstrapPrompt = bootstrapPrompt,
                            OptimizationFlags = effectiveOptimizationFlags,
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
                bool isStandaloneDeterministic = stage.Kind == WorkflowStageKind.Deterministic && stage is not Stages.CompositeDeterministicStage;
                if (isStandaloneDeterministic)
                {
                    try
                    {
                        await _streamer.BroadcastAsync(new Streaming.AgentActivityEvent(
                            Guid.NewGuid().ToString("N"),
                            task.Id,
                            stage.StageId,
                            Streaming.AgentActivityKind.ToolStarted,
                            $"{stage.StageId}: Running {stage.DisplayName}...",
                            DateTime.UtcNow,
                            new Dictionary<string, object?>
                            {
                                [Streaming.AgentActivityMetadataKeys.ToolName] = stage.StageId
                            }), cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed broadcasting standalone deterministic tool started for task #{TaskId}", task.Id);
                    }

                    if (deterministicConv != null)
                    {
                        try
                        {
                            await _taskRepository.AddConversationTurnAsync(
                                deterministicConv.Id,
                                StageTurnRole.Tool,
                                content: $"Execute {stage.DisplayName}",
                                toolName: stage.StageId,
                                toolArguments: stage.DisplayName,
                                cancellationToken: cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed adding standalone deterministic tool turn for task #{TaskId}", task.Id);
                        }
                    }
                }

                result = await stage.ExecuteAsync(pipelineContext, cancellationToken);

                if (isStandaloneDeterministic && deterministicConv != null)
                {
                    var standaloneOut = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.StageOutput(stage.StageId))
                        ?? (result.IsSuccess ? "Completed successfully." : result.ErrorMessage ?? "Deterministic step failed.");
                    var standaloneDuration = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

                    try
                    {
                        await _taskRepository.AddConversationTurnAsync(
                            deterministicConv.Id,
                            StageTurnRole.ToolResult,
                            content: standaloneOut,
                            toolName: stage.StageId,
                            toolResult: standaloneOut,
                            durationMs: standaloneDuration,
                            isError: !result.IsSuccess,
                            cancellationToken: cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed adding standalone deterministic tool result turn for task #{TaskId}", task.Id);
                    }

                    try
                    {
                        await _streamer.BroadcastAsync(new Streaming.AgentActivityEvent(
                            Guid.NewGuid().ToString("N"),
                            task.Id,
                            stage.StageId,
                            Streaming.AgentActivityKind.ToolCompleted,
                            $"{stage.StageId}: {standaloneOut}",
                            DateTime.UtcNow,
                            new Dictionary<string, object?>
                            {
                                [Streaming.AgentActivityMetadataKeys.ToolName] = stage.StageId,
                                [Streaming.AgentActivityMetadataKeys.DurationMs] = standaloneDuration
                            }), cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed broadcasting standalone deterministic tool completed for task #{TaskId}", task.Id);
                    }
                }
            }
        }
        catch (HumanInterruptionException hex)
        {
            var summaryPrompt = string.Join("; ", hex.Questions.Select(q => q.Prompt));
            _logger.LogInformation("Stage '{StageId}' triggered HumanInterruptionException: {Prompt}", stage.StageId, summaryPrompt);
            result = StageExecutionResult.WaitingForHuman(hex.Questions);
        }
        catch (NightCodeForge.Core.Exceptions.OperationsPausedException opex)
        {
            _logger.LogInformation(opex, "Stage '{StageId}' for task #{TaskId} was paused by global operations pause.", stage.StageId, taskId);
            result = StageExecutionResult.Cancelled("Operations paused by user (GPU VRAM reclaimed).");
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

        if (result == null)
        {
            throw new InvalidOperationException($"Stage execution for '{stage.StageId}' did not produce a result.");
        }

        var completedAt = DateTime.UtcNow;
        durationMs = (int)(completedAt - startedAt).TotalMilliseconds;

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

        using (var saveTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(DeterministicStageConstants.DefaultPersistTimeoutSeconds)))
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

        // Finalize Conversation for deterministic stages
        if (stage.Kind == WorkflowStageKind.Deterministic && !deterministicConvFinalized)
        {
            try
            {
                using var finishCts = new CancellationTokenSource(TimeSpan.FromSeconds(DeterministicStageConstants.DefaultPersistTimeoutSeconds));
                var finishToken = finishCts.Token;

                if (deterministicConv == null)
                {
                    int attemptIndex = (pipelineContext.StageRetries.GetValueOrDefault(stage.StageId, 0)) + 1;
                    deterministicConv = await _taskRepository.StartConversationAsync(
                        task.Id,
                        stage.StageId,
                        WorkflowStageKind.Deterministic,
                        attemptIndex,
                        agentProfileId: null,
                        harnessId: StandardHarnessIds.NativeCli,
                        modelName: DeterministicStageConstants.CpuModelName,
                        cancellationToken: finishToken);

                    // Turn 1: User prompt
                    await _taskRepository.AddConversationTurnAsync(
                        deterministicConv.Id,
                        StageTurnRole.User,
                        content: $"Execute deterministic gate '{stage.DisplayName}' ({stage.StageId})",
                        cancellationToken: finishToken);
                }

                if (!result.IsCancelled)
                {
                    var existingTurns = (await _taskRepository.GetConversationWithTurnsAsync(deterministicConv.Id, finishToken))?.Turns;
                    var existingToolNames = existingTurns?
                        .Where(t => t.Role == StageTurnRole.Tool && !string.IsNullOrWhiteSpace(t.ToolName))
                        .Select(t => t.ToolName!)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // Turn 2: Tool execution(s)
                    var commandExecutions = pipelineContext.DataBag.Get<List<StageCommandExecution>>(PipelineDataBagKeys.StageCommands(stage.StageId));
                    if (commandExecutions != null && commandExecutions.Count > 0)
                    {
                        foreach (var exec in commandExecutions)
                        {
                            var toolStageName = !string.IsNullOrWhiteSpace(exec.StageName)
                                ? $"{stage.StageId}-{exec.StageName}"
                                : stage.StageId;

                            // Prevent duplicate turn if this sub-stage was already persisted live
                            if (existingToolNames.Contains(toolStageName))
                            {
                                continue;
                            }

                            await _taskRepository.AddConversationTurnAsync(
                                deterministicConv.Id,
                                StageTurnRole.Tool,
                                content: $"Execute {exec.Command}",
                                toolName: toolStageName,
                                toolArguments: exec.Command,
                                toolResult: exec.Output,
                                durationMs: exec.DurationMs > 0 ? exec.DurationMs : durationMs,
                                isError: !exec.IsSuccess,
                                cancellationToken: finishToken);

                            existingToolNames.Add(toolStageName);
                        }
                    }
                    else if (!existingToolNames.Contains(stage.StageId))
                    {
                        var cmd = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.StageCommand(stage.StageId)) 
                            ?? pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.BuildCommand) 
                            ?? DeterministicStageConstants.DefaultBuildCommand;
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
                            cancellationToken: finishToken);

                        existingToolNames.Add(stage.StageId);
                    }

                    // Turn 3: Assistant result summary
                    var summary = result.IsSuccess 
                        ? $"Deterministic gate '{stage.DisplayName}' passed successfully."
                        : $"Deterministic gate '{stage.DisplayName}' failed: {result.ErrorMessage}";
                    await _taskRepository.AddConversationTurnAsync(
                        deterministicConv.Id,
                        StageTurnRole.Assistant,
                        content: summary,
                        cancellationToken: finishToken);

                    await _taskRepository.CompleteConversationAsync(
                        deterministicConv.Id,
                        result.IsSuccess ? StageExecutionStatus.Completed : StageExecutionStatus.Failed,
                        summary: summary,
                        cancellationToken: finishToken);
                    deterministicConvFinalized = true;

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
                                new Dictionary<string, object?> { [Streaming.AgentActivityMetadataKeys.StageKind] = nameof(WorkflowStageKind.Deterministic) }), finishToken);
                        }
                        catch (Exception sex)
                        {
                            _logger.LogDebug(sex, "Failed broadcasting deterministic stage completion event for task #{TaskId}", task.Id);
                        }
                    }
                }
                else
                {
                    await _taskRepository.CompleteConversationAsync(
                        deterministicConv.Id,
                        StageExecutionStatus.Cancelled,
                        summary: "Deterministic execution was cancelled.",
                        cancellationToken: finishToken);
                    deterministicConvFinalized = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist deterministic Conversation for task #{TaskId}, stage '{StageId}'", task.Id, stage.StageId);
            }
        }
        }
        finally
        {
            if (stage.Kind == WorkflowStageKind.Deterministic && deterministicConv != null && !deterministicConvFinalized)
            {
                try
                {
                    bool isCancelled = cancellationToken.IsCancellationRequested || (result?.IsCancelled == true);
                    var status = isCancelled ? StageExecutionStatus.Cancelled : StageExecutionStatus.Failed;
                    var summary = isCancelled
                        ? "Deterministic execution was cancelled."
                        : "Deterministic execution terminated unexpectedly.";

                    await _taskRepository.CompleteConversationAsync(
                        deterministicConv.Id,
                        status,
                        summary: summary,
                        cancellationToken: CancellationToken.None);
                    deterministicConvFinalized = true;
                }
                catch (Exception fex)
                {
                    _logger.LogWarning(fex, "Failed completing dangling deterministic conversation #{ConversationId} for task #{TaskId}", deterministicConv.Id, task.Id);
                }
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

            IReadOnlyList<ForgeTask>? refreshedTasks = null;
            const int maxConflictRetries = 5;
            int conflictRetries = 0;

            while (true)
            {
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
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (TaskConcurrencyException cex)
                {
                    conflictRetries++;
                    _logger.LogWarning(cex, "Post-stage disk synchronization detected a concurrency conflict for task #{TaskId}. Retaining authoritative database state and continuing pipeline.", cex.TaskId);
                    await _taskRepository.AddExecutionLogAsync(new ExecutionLog
                    {
                        TaskId = task.Id,
                        Level = LogLevel.Warning,
                        Message = $"Post-stage disk synchronization detected a concurrency conflict for task #{cex.TaskId}: {cex.Message}. Retaining authoritative database state and continuing pipeline.",
                        Timestamp = DateTime.UtcNow
                    }, cancellationToken);

                    if (cex.TaskId == task.Id)
                    {
                        // Reload authoritative database state for the executing task
                        var authoritativeDbTask = await _taskRepository.GetByIdAsync(task.Id, cancellationToken);
                        if (authoritativeDbTask != null)
                        {
                            task.Title = authoritativeDbTask.Title;
                            task.Description = authoritativeDbTask.Description;
                            task.Priority = authoritativeDbTask.Priority;
                            task.MergeMode = authoritativeDbTask.MergeMode;
                            task.BaseBranch = authoritativeDbTask.BaseBranch;
                            task.FeatureBranch = authoritativeDbTask.FeatureBranch;
                            task.Origin = authoritativeDbTask.Origin;
                            task.Version = authoritativeDbTask.Version;
                            task.UpdatedAt = authoritativeDbTask.UpdatedAt;
                            task.Dependencies = authoritativeDbTask.Dependencies;
                        }

                        // Sync the authoritative DB state back to hostRepoPath and ext4 mirror
                        await SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

                        // Overwrite targetSyncPath (worktree) if different from hostRepoPath so git checkpoints don't commit conflicted markdown
                        if (!string.IsNullOrWhiteSpace(targetSyncPath) &&
                            !string.Equals(targetSyncPath, hostRepoPath, StringComparison.OrdinalIgnoreCase) &&
                            Directory.Exists(targetSyncPath))
                        {
                            try
                            {
                                await _markdownSyncService.SyncTaskToDiskAsync(task, targetSyncPath, cancellationToken: cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed syncing authoritative task #{TaskId} markdown to worktree '{TargetSyncPath}'", task.Id, targetSyncPath);
                            }
                        }

                        if (conflictRetries >= maxConflictRetries)
                        {
                            _logger.LogWarning("Exceeded max retries ({MaxRetries}) resolving task #{TaskId} concurrency conflicts; continuing pipeline.", maxConflictRetries, task.Id);
                            break;
                        }
                    }
                    else
                    {
                        // The conflict occurred on another task in the workspace.
                        // Sync that other task's authoritative DB state to host and worktree so it no longer conflicts.
                        var otherConflictedTask = await _taskRepository.GetByIdAsync(cex.TaskId, cancellationToken);
                        if (otherConflictedTask != null)
                        {
                            await SyncTaskMarkdownAndMirrorAsync(otherConflictedTask, workspace, cancellationToken);
                            if (!string.IsNullOrWhiteSpace(targetSyncPath) &&
                                !string.Equals(targetSyncPath, hostRepoPath, StringComparison.OrdinalIgnoreCase) &&
                                Directory.Exists(targetSyncPath))
                            {
                                try
                                {
                                    await _markdownSyncService.SyncTaskToDiskAsync(otherConflictedTask, targetSyncPath, cancellationToken: cancellationToken);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed syncing authoritative task #{TaskId} markdown to worktree '{TargetSyncPath}'", otherConflictedTask.Id, targetSyncPath);
                                }
                            }
                        }

                        if (conflictRetries >= maxConflictRetries)
                        {
                            _logger.LogWarning("Exceeded max retries ({MaxRetries}) resolving foreign task concurrency conflicts; continuing pipeline.", maxConflictRetries);
                            break;
                        }
                    }
                }
                catch (TaskMarkdownSyncException)
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
            }

            var refreshedTask = refreshedTasks?.FirstOrDefault(t => t.Id == task.Id);
            if (refreshedTask != null)
            {
                task.Title = refreshedTask.Title;
                task.Description = refreshedTask.Description;
                task.Priority = refreshedTask.Priority;
                task.MergeMode = refreshedTask.MergeMode;
                task.BaseBranch = refreshedTask.BaseBranch;
                task.FeatureBranch = refreshedTask.FeatureBranch;
                task.Origin = refreshedTask.Origin;
                task.UpdatedAt = refreshedTask.UpdatedAt;
                task.Version = refreshedTask.Version;
                task.Dependencies = refreshedTask.Dependencies;
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
        if (result.IsCancelled || (cancellationToken.IsCancellationRequested && !result.IsSuccess))
        {
            _logger.LogInformation("Processing of task #{TaskId} at stage '{StageId}' was cancelled or paused.", task.Id, stage.StageId);

            using var timeoutCts = new CancellationTokenSource(PauseAbortTimeout);
            var ct = timeoutCts.Token;

            var isPause = _cancellationManager.IsPauseRequested(task.Id) || _operationsControlService.IsPaused;
            if (isPause)
            {
                _logger.LogInformation("Task #{TaskId} paused at stage '{StageId}' due to global operations suspension.", task.Id, stage.StageId);
                await CloseActiveConversationsAsync(task.Id, StageExecutionStatus.Interrupted, "Operations paused by user (GPU VRAM reclaimed).", ct);

                var currentTask = await _taskRepository.GetByIdAsync(task.Id, ct);
                if (currentTask != null && currentTask.Status != TaskOverallStatus.Closed && currentTask.Status != TaskOverallStatus.Rejected)
                {
                    currentTask.Status = TaskOverallStatus.Open;
                    currentTask.InterruptedStageId = stage.StageId;
                    currentTask.InterruptedAgentProfileId = profile?.Id ?? currentTask.InterruptedAgentProfileId;
                    currentTask.IsWaitingForHuman = false;
                    currentTask.IsDeferredByPause = true;

                    pipelineContext.SyncDataBagToTask();
                    await _taskRepository.UpdateWithHistoryAsync(
                        currentTask,
                        dependsOn: null,
                        changedBy: AuditActors.User,
                        reason: "Operations paused by user (GPU reclaimed).",
                        cancellationToken: ct);
                    await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Open, ct);
                }

                await _taskRepository.AddExecutionLogAsync(new ExecutionLog
                {
                    TaskId = task.Id,
                    Level = LogLevel.Information,
                    Message = $"Stage '{stage.StageId}' was paused.",
                    Timestamp = DateTime.UtcNow
                }, ct);

                cohortExitReason = CohortExitReason.Paused;
                return;
            }

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

            cohortExitReason = CohortExitReason.Cancelled;
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

            cohortExitReason = CohortExitReason.WaitingForHuman;
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
                cohortExitReason = CohortExitReason.Cancelled;
                return;
            }

            if (_cancellationManager.IsPauseRequested(task.Id) || _operationsControlService.IsPaused)
            {
                var persistedStatusAfterStage = await _taskRepository.GetStatusAsync(task.Id, CancellationToken.None);
                if (persistedStatusAfterStage != TaskOverallStatus.Closed && persistedStatusAfterStage != TaskOverallStatus.Rejected)
                {
                    task.Status = TaskOverallStatus.Open;
                    if (!result.IsSuccess)
                    {
                        task.InterruptedStageId = stage.StageId;
                        task.InterruptedAgentProfileId = profile?.Id;
                    }
                    else
                    {
                        var nextStage = !string.IsNullOrWhiteSpace(result.TargetRoutingStageId)
                            ? pipeline.GetStage(result.TargetRoutingStageId)
                            : pipeline.GetNextStage(stage.StageId);
                        task.InterruptedStageId = nextStage?.StageId;
                        task.InterruptedAgentProfileId = null;
                    }
                    task.IsWaitingForHuman = false;
                    task.IsDeferredByPause = true;
                    await _taskRepository.UpdateWithHistoryAsync(
                        task,
                        dependsOn: null,
                        changedBy: AuditActors.User,
                        reason: $"Execution paused after stage '{stage.StageId}'.",
                        cancellationToken: CancellationToken.None);
                    await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Open, CancellationToken.None);
                }
                cohortExitReason = CohortExitReason.Paused;
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
            cohortExitReason = CohortExitReason.Cancelled;
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

                        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
                        {
                            TaskId = task.Id,
                            Level = LogLevel.Warning,
                            Message = ConflictResolutionConstants.AuditConflictResolutionBudgetExhausted,
                            Timestamp = DateTime.UtcNow
                        }, cancellationToken);

                        cohortExitReason = CohortExitReason.WaitingForHuman;
                        return;
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
                    await SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

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
                    cohortExitReason = CohortExitReason.Failed;
                    return;
                }
                else
                {
                    // Verified successfully!
                    pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictResolutionAttempts, 0);
                    pipelineContext.DataBag.Remove(PipelineDataBagKeys.ConflictFiles);
                    pipelineContext.DataBag.Remove(PipelineDataBagKeys.ConflictBaseBranch);
                    pipelineContext.DataBag.Remove(PipelineDataBagKeys.ConflictDetectedAt);
                    pipelineContext.DataBag.Remove(PipelineDataBagKeys.CompilerErrors);
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
            if (stage.Kind == WorkflowStageKind.Deterministic)
            {
                SetSyntheticPrimingFromDeterministicStage(stage, pipelineContext, result, durationMs);
            }
            // Case B: Stage Completed
            pipelineContext.ResetStageRetry(stage.StageId);
            if (string.Equals(stage.StageId, StandardStageIds.PostImplementationGates, StringComparison.OrdinalIgnoreCase))
            {
                pipelineContext.DataBag.Remove(PipelineDataBagKeys.CompilerErrors);
                pipelineContext.DataBag.Remove(PipelineDataBagKeys.FailedTests);
            }

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
            if (string.IsNullOrWhiteSpace(activeInterruptedStageId))
            {
                pipelineContext.DataBag.Remove(PipelineDataBagKeys.LastHumanAnswer);
                pipelineContext.DataBag.Remove(PipelineDataBagKeys.LastHumanAnswerJson);
            }

            if (string.Equals(stage.StageId, StandardStageIds.Implementation, StringComparison.OrdinalIgnoreCase))
            {
                await PopulateReviewDiffsAsync(workspace, task, pipelineContext, cancellationToken);
            }

            var nextStageId = result.TargetRoutingStageId;
            IWorkflowStage? nextStage;
            if (!string.IsNullOrWhiteSpace(nextStageId))
            {
                nextStage = pipeline.GetStage(nextStageId)
                    ?? throw new WorkflowValidationException($"Task #{task.Id} stage '{stage.StageId}' routed dynamically to unknown target stage '{nextStageId}'.");
            }
            else
            {
                nextStage = pipeline.GetNextStage(stage.StageId);
            }

            if (nextStage != null)
            {
                task.CurrentStageId = nextStage.StageId;

                if (string.Equals(nextStage.StageId, StandardStageIds.CodeReviewAndFix, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nextStage.StageId, StandardStageIds.PostReviewVerification, StringComparison.OrdinalIgnoreCase))
                {
                    await PopulateReviewDiffsAsync(workspace, task, pipelineContext, cancellationToken);
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

                bool isSameCohort = IsSameCohort(stage.QueueTier, nextStage.QueueTier);
                if (isSameCohort)
                {
                    _logger.LogInformation("Chaining stage '{CurrentStage}' -> '{NextStage}' in-process for task #{TaskId} (QueueTier: {Tier})",
                        stage.StageId, nextStage.StageId, task.Id, stage.QueueTier);
                    stage = nextStage;
                    continue;
                }
                else
                {
                    // Cohort Boundary: Enqueue next stage and exit worker turn
                    _logger.LogInformation("Cohort boundary reached after '{CurrentStage}' for task #{TaskId}. Enqueuing next stage '{NextStage}' to {Tier}.",
                        stage.StageId, task.Id, nextStage.StageId, nextStage.QueueTier);
                    task.LastHangfireJobId = _jobQueue.EnqueueStage(task.Id, nextStage.QueueTier, task.Priority);
                    await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
                    cohortExitReason = CohortExitReason.CohortCompleted;
                    return;
                }
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
                    cohortExitReason = task.IsWaitingForHuman ? CohortExitReason.WaitingForHuman : CohortExitReason.Failed;
                    return;
                }

                // Mergeable: sync from base branch in worktree
                var syncResult = await _gitWorkspaceManager.SyncFromBaseBranchAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
                if (syncResult != null && syncResult.HasConflict)
                {
                    await InitiateConflictResolutionAsync(task, workspace, pipelineContext, syncResult.ConflictedFiles, cancellationToken);
                    cohortExitReason = task.IsWaitingForHuman ? CohortExitReason.WaitingForHuman : CohortExitReason.Failed;
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
                        var reverifyRetryKey = $"{stage.StageId}:gate2-reverify";
                        var reverifyRetries = pipelineContext.IncrementRetry(reverifyRetryKey);
                        var reverifyMaxRetries = pipelineContext.DataBag.Get<int?>(PipelineDataBagKeys.MaxRetries) ?? WorkflowDefaults.DefaultMaxStageRetries;
                        var reverifyDurationMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

                        if (!string.IsNullOrWhiteSpace(loopBackStageId) && reverifyRetries <= reverifyMaxRetries)
                        {
                            var targetStage = pipeline.GetStage(loopBackStageId);
                            if (targetStage != null && IsSameCohort(stage.QueueTier, targetStage.QueueTier))
                            {
                                await PrepareInProcessLoopBackAsync(task, workspace, pipelineContext, stage, targetStage, reverifyRetries, reverifyMaxRetries, reverifyResult, reverifyDurationMs, cancellationToken);
                                stage = targetStage;
                                continue;
                            }
                            else if (targetStage != null)
                            {
                                await HandleCrossCohortLoopBackAsync(task, workspace, pipelineContext, stage, targetStage, reverifyRetries, reverifyMaxRetries, reverifyResult, reverifyDurationMs, cancellationToken);
                                cohortExitReason = CohortExitReason.Failed;
                                return;
                            }
                        }

                        await HandleStageFatalFailureAsync(task, workspace, pipelineContext, stage, reverifyResult, cancellationToken);
                        cohortExitReason = CohortExitReason.Failed;
                        return;
                    }
                }

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

                if (task.MergeMode == MergeMode.Automatic)
                {
                    _logger.LogInformation("ForgeTask #{TaskId} completed all stages with MergeMode.Automatic; executing auto-merge.", task.Id);
                    pipelineContext.SyncDataBagToTask();
                    CompleteTaskResult mergeResult;
                    using (await _workspaceLockProvider.AcquireLockAsync(workspace.Id, cancellationToken))
                    {
                        mergeResult = await CompleteTaskAndCascadeInternalAsync(task, workspace, $"Auto-merge task #{task.Id}: {task.Title}", cancellationToken);
                    }

                    if (mergeResult.Status == CompleteTaskStatus.ConflictResolutionDispatched)
                    {
                        cohortExitReason = task.IsWaitingForHuman ? CohortExitReason.WaitingForHuman : CohortExitReason.Failed;
                        return;
                    }
                }
                else
                {
                    task.Status = TaskOverallStatus.Ready;
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

                    await SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);
                    await _taskRepository.AddExecutionLogAsync(new ExecutionLog
                    {
                        TaskId = task.Id,
                        Level = LogLevel.Information,
                        Message = $"Pipeline completed all stages for task #{task.Id}. Awaiting maintainer manual review to merge.",
                        Timestamp = DateTime.UtcNow
                    }, cancellationToken);
                }

                cohortExitReason = CohortExitReason.CohortCompleted;
                return;
            }
        }

        // Loopback / Retry Routing (e.g. deterministic build, test, or lint quality gate failed and routed back to implementation)
        var targetLoopBackId = result.TargetRoutingStageId ?? stage.LoopBackStageId;
        var retries = pipelineContext.IncrementRetry(stage.StageId);
        var effectiveMaxRetries = pipelineContext.DataBag.Get<int?>(PipelineDataBagKeys.MaxRetries) ?? WorkflowDefaults.DefaultMaxStageRetries;

        if (!string.IsNullOrWhiteSpace(targetLoopBackId) && retries <= effectiveMaxRetries)
        {
            var targetStage = pipeline.GetStage(targetLoopBackId);
            if (targetStage != null && IsSameCohort(stage.QueueTier, targetStage.QueueTier))
            {
                // IN-PROCESS LOOPBACK
                await PrepareInProcessLoopBackAsync(task, workspace, pipelineContext, stage, targetStage, retries, effectiveMaxRetries, result, durationMs, cancellationToken);
                stage = targetStage;
                continue;
            }
            else if (targetStage != null)
            {
                // CROSS-COHORT LOOPBACK
                await HandleCrossCohortLoopBackAsync(task, workspace, pipelineContext, stage, targetStage, retries, effectiveMaxRetries, result, durationMs, cancellationToken);
                cohortExitReason = CohortExitReason.Failed;
                return;
            }
        }

        // Case C: Permanent Failure
        await HandleStageFatalFailureAsync(task, workspace, pipelineContext, stage, result, cancellationToken);
        cohortExitReason = CohortExitReason.Failed;
        return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ocex) when (ocex is OperationCanceledException or NightCodeForge.Core.Exceptions.OperationsPausedException)
        {
            _logger.LogInformation(ocex, "ForgeTask #{TaskId} processing was cancelled or interrupted.", taskId);
            using var timeoutCts = new CancellationTokenSource(PauseAbortTimeout);
            var ct = timeoutCts.Token;
            try
            {
                if (_cancellationManager.IsCancelRequested(taskId))
                {
                    cohortExitReason = CohortExitReason.Cancelled;
                    _logger.LogInformation("Task #{TaskId} execution cancelled by user.", taskId);
                    await CloseActiveConversationsAsync(taskId, StageExecutionStatus.Cancelled, "Task cancelled by user.", ct);

                    var currentTask = await _taskRepository.GetByIdAsync(taskId, ct);
                    if (currentTask != null && currentTask.Status != TaskOverallStatus.Closed && currentTask.Status != TaskOverallStatus.Rejected)
                    {
                        currentTask.Status = TaskOverallStatus.Cancelled;
                        currentTask.IsWaitingForHuman = false;
                        await _taskRepository.UpdateWithHistoryAsync(
                            currentTask,
                            dependsOn: null,
                            changedBy: AuditActors.User,
                            reason: "Cancelled by user.",
                            cancellationToken: ct);
                        await _dependencyStore.UpdateTaskStatusAsync(taskId, TaskOverallStatus.Cancelled, ct);
                    }
                    return;
                }

                if (_cancellationManager.IsPauseRequested(taskId) || _operationsControlService.IsPaused)
                {
                    cohortExitReason = CohortExitReason.Paused;
                    _logger.LogInformation("Task #{TaskId} paused due to global operations suspension.", taskId);

                    await CloseActiveConversationsAsync(taskId, StageExecutionStatus.Interrupted, "Operations paused by user (GPU VRAM reclaimed).", ct);

                    var currentTask = await _taskRepository.GetByIdAsync(taskId, ct);
                    if (currentTask != null && currentTask.Status != TaskOverallStatus.Closed && currentTask.Status != TaskOverallStatus.Rejected)
                    {
                        currentTask.Status = TaskOverallStatus.Open;
                        currentTask.InterruptedStageId = currentExecutingStage?.StageId ?? currentTask.InterruptedStageId ?? currentTask.CurrentStageId;
                        currentTask.InterruptedAgentProfileId = currentExecutingProfile?.Id ?? currentTask.InterruptedAgentProfileId;
                        currentTask.IsWaitingForHuman = false;
                        currentTask.IsDeferredByPause = true;

                        await _taskRepository.UpdateWithHistoryAsync(
                            currentTask,
                            dependsOn: null,
                            changedBy: AuditActors.User,
                            reason: "Operations paused by user (GPU reclaimed).",
                            cancellationToken: ct);
                        await _dependencyStore.UpdateTaskStatusAsync(taskId, TaskOverallStatus.Open, ct);
                    }
                    return;
                }

                cohortExitReason = CohortExitReason.Cancelled;
                // Parent cancellation (e.g. shutdown or external token): close conversations as Cancelled and leave status untouched
                await CloseActiveConversationsAsync(taskId, StageExecutionStatus.Cancelled, "Execution interrupted by application shutdown or cancellation token.", ct);
            }
            catch (Exception cex)
            {
                _logger.LogWarning(cex, "Failed to update active conversation and status during cancellation/pause for task #{TaskId}", taskId);
            }
        }
        catch (Exception ex)
        {
            cohortExitReason = CohortExitReason.Failed;
            _logger.LogError(ex, "ForgeTask #{TaskId} execution failed with an unhandled exception.", taskId);
            try
            {
                await CloseActiveConversationsAsync(taskId, StageExecutionStatus.Failed, $"Pipeline failed unexpectedly: {ex.Message}", CancellationToken.None);

                var failedTask = await _taskRepository.GetByIdAsync(taskId, CancellationToken.None);
                if (failedTask != null && failedTask.Status != TaskOverallStatus.Closed && failedTask.Status != TaskOverallStatus.Rejected && failedTask.Status != TaskOverallStatus.Cancelled)
                {
                    failedTask.Status = TaskOverallStatus.Failed;
                    failedTask.IsWaitingForHuman = false;
                    await _taskRepository.UpdateWithHistoryAsync(
                        failedTask,
                        dependsOn: null,
                        changedBy: AuditActors.TaskOrchestrator,
                        reason: $"Unexpected pipeline failure: {ex.Message}",
                        cancellationToken: CancellationToken.None);
                    await _dependencyStore.UpdateTaskStatusAsync(taskId, TaskOverallStatus.Failed, CancellationToken.None);
                    await _dependencyStore.SetWaitingForHumanAsync(taskId, false, CancellationToken.None);
                }

                await _taskRepository.AddExecutionLogAsync(new ExecutionLog
                {
                    TaskId = taskId,
                    Level = LogLevel.Error,
                    Message = $"Pipeline failed unexpectedly: {ex.Message}",
                    Timestamp = DateTime.UtcNow
                }, CancellationToken.None);
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "Failed recording unhandled exception failure state for task #{TaskId}", taskId);
            }
            throw;
        }
        finally
        {
            _logger.LogInformation("Task #{TaskId} cohort execution completed in {DurationMs}ms with reason '{ExitReason}'.", taskId, cohortSw.ElapsedMilliseconds, cohortExitReason);
            _telemetryService.ClearActiveTask(taskId);
            if (taskCts != null)
            {
                _cancellationManager.Unregister(taskId, taskCts);
                taskCts.Dispose();
            }
        }
    }

    private async Task CloseActiveConversationsAsync(int taskId, StageExecutionStatus status, string summary, CancellationToken cancellationToken)
    {
        var convs = await _taskRepository.GetConversationsAsync(taskId, cancellationToken);
        var activeConvs = convs.Where(c => c.Status == StageExecutionStatus.Running || c.Status == StageExecutionStatus.WaitingForHuman).ToList();
        foreach (var activeConv in activeConvs)
        {
            await _taskRepository.CompleteConversationAsync(
                activeConv.Id,
                status,
                summary: summary,
                cancellationToken: cancellationToken);
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

        var pipeline = await _pipelineProvider.GetPipelineAsync(task, cancellationToken);
        var blockedStage = (!string.IsNullOrWhiteSpace(task.CurrentStageId) ? pipeline.GetStage(task.CurrentStageId) : null)
            ?? pipeline.InitialStage
            ?? throw new WorkflowValidationException($"No active or initial stage found for task #{task.Id}.");

        // Persist ToolResult turn immediately in the waiting conversation so Task Inspector and conversation history
        // reflect the user's answers without having to wait for the worker to schedule and execute the agent.
        var questionStageId = batchQuestions.FirstOrDefault()?.StageId;
        var waitingConv = !string.IsNullOrWhiteSpace(questionStageId)
            ? await _taskRepository.GetWaitingConversationAsync(task.Id, questionStageId, cancellationToken)
            : null;

        if (waitingConv == null)
        {
            waitingConv = await _taskRepository.GetWaitingConversationAsync(task.Id, blockedStage.StageId, cancellationToken);
        }
        if (waitingConv == null && !string.IsNullOrWhiteSpace(task.CurrentStageId))
        {
            waitingConv = await _taskRepository.GetWaitingConversationAsync(task.Id, task.CurrentStageId, cancellationToken);
        }
        if (waitingConv == null)
        {
            var convs = await _taskRepository.GetConversationsAsync(task.Id, cancellationToken);
            var candidate = convs.LastOrDefault(c => c.Status == StageExecutionStatus.WaitingForHuman)
                ?? convs.LastOrDefault(c => !string.IsNullOrWhiteSpace(questionStageId) && string.Equals(c.StageId, questionStageId, StringComparison.OrdinalIgnoreCase))
                ?? convs.LastOrDefault(c => !string.IsNullOrWhiteSpace(task.CurrentStageId) && string.Equals(c.StageId, task.CurrentStageId, StringComparison.OrdinalIgnoreCase))
                ?? convs.LastOrDefault();
            if (candidate != null)
            {
                waitingConv = await _taskRepository.GetConversationWithTurnsAsync(candidate.Id, cancellationToken);
            }
        }

        if (waitingConv != null)
        {
            var askTurn = waitingConv.Turns?.LastOrDefault(t => t.Role == StageTurnRole.Tool && string.Equals(t.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase));
            var callId = ConversationHistorySerializer.ExtractCallId(askTurn?.MetadataJson);
            if (string.IsNullOrEmpty(callId))
            {
                callId = Guid.NewGuid().ToString("N");
            }

            bool alreadyHasToolResult = waitingConv.Turns != null && waitingConv.Turns.Any(t =>
                t.Role == StageTurnRole.ToolResult &&
                string.Equals(t.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase) &&
                ((t.MetadataJson != null && t.MetadataJson.Contains(callId)) || (string.IsNullOrEmpty(t.MetadataJson) && t.ToolResult == formattedQna)));

            if (!alreadyHasToolResult)
            {
                var metaDict = new Dictionary<string, object?> { ["CallId"] = callId };
                if (!string.IsNullOrWhiteSpace(structuredJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(structuredJson);
                        metaDict["Answers"] = doc.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        // Ignore malformed JSON
                    }
                }
                var meta = JsonSerializer.Serialize(metaDict);

                await _taskRepository.AddConversationTurnAsync(
                    waitingConv.Id,
                    StageTurnRole.ToolResult,
                    toolName: CanonicalToolNames.Ask,
                    toolResult: formattedQna,
                    metadataJson: meta,
                    cancellationToken: cancellationToken);
            }
        }

        await _streamer.BroadcastAsync(new AgentActivityEvent(
            SessionId: Guid.NewGuid().ToString("N"),
            TaskId: task.Id,
            StageId: blockedStage.StageId,
            Kind: AgentActivityKind.ToolCompleted,
            Content: formattedQna,
            Timestamp: DateTime.UtcNow,
            Metadata: new Dictionary<string, object?>
            {
                [AgentActivityMetadataKeys.ToolName] = CanonicalToolNames.Ask
            }
        ), cancellationToken);

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Information,
            Message = $"Human answers submitted for task #{taskId} (batch {activeBatchId ?? "default"} completed); resumed and enqueued to stage queue {blockedStage.QueueTier}",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);

        task.LastHangfireJobId = _jobQueue.EnqueueStage(task.Id, blockedStage.QueueTier, task.Priority);
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

        var targetStage = pipeline.GetStage(targetStageId)
            ?? throw new WorkflowValidationException($"Workflow does not contain stage '{targetStageId}'.");

        // Re-route task back to dynamic target stage
        task.Status = TaskOverallStatus.Open;
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
        await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Open, cancellationToken);

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Information,
            Message = $"Reviewer requested changes for task #{task.Id} (routing back to '{targetStageId}'): {revisionNotes}",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);

        // Enqueue to target stage queue tier
        task.LastHangfireJobId = _jobQueue.EnqueueStage(task.Id, targetStage.QueueTier, task.Priority);
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
                var pipeline = await _pipelineProvider.GetPipelineAsync(task, cancellationToken);
                var initialStage = pipeline.InitialStage
                    ?? throw new WorkflowValidationException($"Workflow does not contain an initial stage for task #{task.Id}.");
                task.LastHangfireJobId = _jobQueue.EnqueueStage(task.Id, initialStage.QueueTier, task.Priority);

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
        bag.Remove(PipelineDataBagKeys.ConflictResolutionAttempts);
        bag.Remove(PipelineDataBagKeys.TotalConflictResolutionAttempts);
        bag.Remove(PipelineDataBagKeys.LifetimeConflictDetections);
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
            try
            {
                await _gitWorkspaceManager.RemoveWorktreeAsync(workspace, task.Id, cancellationToken);
            }
            catch (Exception wex) when (wex is not OperationCanceledException)
            {
                _logger.LogWarning(wex, "Failed to remove worktree for rejected task #{TaskId}", task.Id);
            }

            try
            {
                await _environmentProvider.CleanupTaskContainerAsync(workspace, task.Id, cancellationToken);
            }
            catch (Exception cex) when (cex is not OperationCanceledException)
            {
                _logger.LogWarning(cex, "Failed to clean up container for rejected task #{TaskId}", task.Id);
            }

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
                // Critical Section: SquashAndMerge must be atomic and non-cancellable by pause
                squashSha = await _gitWorkspaceManager.SquashAndMergeAsync(
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
            if (_gitMirrorSyncService != null && !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
            {
                var pushResult = await _gitMirrorSyncService.PushBaseBranchToHostAsync(
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
                            await _gitWorkspaceManager.ResetCanonicalBranchAsync(workspace, task.BaseBranch, preSquashSha, CancellationToken.None);
                        }
                        await _gitMirrorSyncService.PullFromHostAsync(workspace.Ext4MirrorPath, task.BaseBranch, CancellationToken.None);
                        continue;
                    }

                    _logger.LogError("Mirror push rejected as non-fast-forward after {MaxRetries} retries for task #{TaskId}.",
                        ConflictResolutionConstants.MaxPushRetries, task.Id);
                    if (!string.IsNullOrEmpty(preSquashSha))
                    {
                        await _gitWorkspaceManager.ResetCanonicalBranchAsync(workspace, task.BaseBranch, preSquashSha, CancellationToken.None);
                    }
                    throw new MirrorPushRejectedException(task.BaseBranch, workspace.Ext4MirrorPath, pushResult.WarningMessage ?? "Non-fast-forward push rejection.");
                }
                else if (!pushResult.Success)
                {
                    if (!string.IsNullOrEmpty(preSquashSha))
                    {
                        await _gitWorkspaceManager.ResetCanonicalBranchAsync(workspace, task.BaseBranch, preSquashSha, CancellationToken.None);
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
            try
            {
                await _gitWorkspaceManager.RemoveWorktreeAsync(workspace, task.Id, CancellationToken.None);
            }
            catch (Exception wex)
            {
                _logger.LogWarning(wex, "Failed to remove worktree for completed task #{TaskId}", task.Id);
            }

            break;
        }

        try
        {
            await _environmentProvider.CleanupTaskContainerAsync(workspace, task.Id, cancellationToken);
        }
        catch (Exception cex) when (cex is not OperationCanceledException)
        {
            _logger.LogWarning(cex, "Failed to clean up container for completed task #{TaskId}", task.Id);
        }

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

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Warning,
            Message = reason,
            Timestamp = DateTime.UtcNow
        }, cancellationToken);
    }

    private async Task PrepareInProcessLoopBackAsync(
        ForgeTask task,
        Workspace workspace,
        PipelineContext pipelineContext,
        IWorkflowStage currentStage,
        IWorkflowStage targetStage,
        int retries,
        int effectiveMaxRetries,
        StageExecutionResult result,
        int durationMs,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning("Stage '{StageId}' failed, routing backward in-process to '{TargetStage}' (Retry {Retry}/{Max}): {Reason}",
            currentStage.StageId, targetStage.StageId, retries, effectiveMaxRetries, result.ErrorMessage);

        PopulateDiagnosticsIntoDataBag(pipelineContext, currentStage, retries, result);
        if (currentStage.Kind == WorkflowStageKind.Deterministic)
        {
            SetSyntheticPrimingFromDeterministicStage(currentStage, pipelineContext, result, durationMs);
        }

        if (!string.Equals(targetStage.StageId, StandardStageIds.CodeReviewAndFix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(targetStage.StageId, StandardStageIds.PostReviewVerification, StringComparison.OrdinalIgnoreCase))
        {
            pipelineContext.DataBag.Remove(PipelineDataBagKeys.ReviewDiff);
            pipelineContext.DataBag.Remove(PipelineDataBagKeys.ReviewDiffStat);
        }

        task.CurrentStageId = targetStage.StageId;
        pipelineContext.SyncDataBagToTask();
        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.TaskOrchestrator,
            reason: $"Quality gate '{currentStage.StageId}' failed. Looping back in-process to '{targetStage.StageId}' (attempt {retries}/{effectiveMaxRetries}).",
            cancellationToken: cancellationToken);
        await _dependencyStore.UpdateTaskStatusAsync(task.Id, task.Status, cancellationToken);
        await SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Warning,
            Message = $"Quality gate '{currentStage.StageId}' failed: {result.ErrorMessage}. Looping back in-process to '{targetStage.StageId}' (attempt {retries}/{effectiveMaxRetries}).",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);
    }

    private async Task HandleCrossCohortLoopBackAsync(
        ForgeTask task,
        Workspace workspace,
        PipelineContext pipelineContext,
        IWorkflowStage currentStage,
        IWorkflowStage targetStage,
        int retries,
        int effectiveMaxRetries,
        StageExecutionResult result,
        int durationMs,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning("Stage '{StageId}' failed, routing backward cross-cohort to '{TargetStage}' (Retry {Retry}/{Max}): {Reason}",
            currentStage.StageId, targetStage.StageId, retries, effectiveMaxRetries, result.ErrorMessage);

        PopulateDiagnosticsIntoDataBag(pipelineContext, currentStage, retries, result);
        if (currentStage.Kind == WorkflowStageKind.Deterministic)
        {
            SetSyntheticPrimingFromDeterministicStage(currentStage, pipelineContext, result, durationMs);
        }

        if (!string.Equals(targetStage.StageId, StandardStageIds.CodeReviewAndFix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(targetStage.StageId, StandardStageIds.PostReviewVerification, StringComparison.OrdinalIgnoreCase))
        {
            pipelineContext.DataBag.Remove(PipelineDataBagKeys.ReviewDiff);
            pipelineContext.DataBag.Remove(PipelineDataBagKeys.ReviewDiffStat);
        }

        task.CurrentStageId = targetStage.StageId;
        pipelineContext.SyncDataBagToTask();
        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.TaskOrchestrator,
            reason: $"Quality gate '{currentStage.StageId}' failed. Looping back to '{targetStage.StageId}' (attempt {retries}/{effectiveMaxRetries}).",
            cancellationToken: cancellationToken);
        await _dependencyStore.UpdateTaskStatusAsync(task.Id, task.Status, cancellationToken);
        await SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Warning,
            Message = $"Quality gate '{currentStage.StageId}' failed: {result.ErrorMessage}. Looping back cross-cohort to '{targetStage.StageId}' (attempt {retries}/{effectiveMaxRetries}).",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);

        task.LastHangfireJobId = _jobQueue.EnqueueStage(task.Id, targetStage.QueueTier, task.Priority);
        await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
    }

    private async Task HandleStageFatalFailureAsync(
        ForgeTask task,
        Workspace workspace,
        PipelineContext pipelineContext,
        IWorkflowStage stage,
        StageExecutionResult result,
        CancellationToken cancellationToken)
    {
        var effectiveMaxRetries = pipelineContext.DataBag.Get<int?>(PipelineDataBagKeys.MaxRetries) ?? WorkflowDefaults.DefaultMaxStageRetries;
        task.Status = TaskOverallStatus.Failed;
        pipelineContext.SyncDataBagToTask();
        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.TaskOrchestrator,
            reason: $"Pipeline failed at stage '{stage.StageId}'. Exceeded max retry limit ({effectiveMaxRetries}). Error: {result.ErrorMessage ?? "Unknown failure"}",
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

    /// <summary>Separator between the stage id and a failed test entry inside CompilerErrors.</summary>
    private const string TestFailureMarker = " test failure] ";

    private static void PopulateDiagnosticsIntoDataBag(
        PipelineContext pipelineContext,
        IWorkflowStage stage,
        int retries,
        StageExecutionResult result)
    {
        var errors = pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.CompilerErrors) ?? new List<string>();
        var failedTests = pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.FailedTests);
        if (failedTests is { Count: > 0 })
        {
            errors.AddRange(failedTests.Select(ft => $"[{stage.StageId}{TestFailureMarker}{ft}"));
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
    }

    private static bool IsSameCohort(StageQueueTier current, StageQueueTier next) =>
        current == next && current != StageQueueTier.TaskReview;

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

    /// <inheritdoc />
    public async Task<bool> RestartTaskPipelineAsync(
        int taskId,
        TaskRestartGitMode gitMode = TaskRestartGitMode.PreserveWork,
        CancellationToken cancellationToken = default)
    {
        // 1. Initial snapshot check
        var initialSnapshot = await _taskRepository.GetByIdAsync(taskId, cancellationToken)
            ?? throw new KeyNotFoundException($"Task #{taskId} not found.");

        if (initialSnapshot.SquashCommitSha != null || initialSnapshot.Status == TaskOverallStatus.Closed)
        {
            throw new InvalidOperationException("Cannot restart a task that has already been merged.");
        }

        // 2. Active execution cancellation: signal if currently registered before awaiting release
        if (_cancellationManager.IsRegistered(taskId))
        {
            _cancellationManager.Cancel(taskId);
        }

        // 3. Workspace / Task Mutual Exclusion Lock: awaits release of any running worker
        using var lockReleaser = await _workspaceLockProvider.AcquireTaskLockAsync(initialSnapshot.WorkspaceId, taskId, cancellationToken);

        // 4. Reload task entity after acquiring lock to guarantee fresh state
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken)
            ?? throw new KeyNotFoundException($"Task #{taskId} not found.");

        if (task.SquashCommitSha != null || task.Status == TaskOverallStatus.Closed)
        {
            throw new InvalidOperationException("Cannot restart a task that has already been merged.");
        }

        // 5. Delete lingering Hangfire job if present
        if (!string.IsNullOrWhiteSpace(task.LastHangfireJobId))
        {
            _jobQueue.Delete(task.LastHangfireJobId);
        }

        var workspace = task.Workspace ?? await _workspaceRepository.GetByIdAsync(task.WorkspaceId, cancellationToken);
        if (workspace == null)
        {
            throw new InvalidOperationException($"Workspace #{task.WorkspaceId} not found for task #{taskId}.");
        }

        // 6. Hard Reset Path: Container teardown FIRST, then Git reset
        if (gitMode == TaskRestartGitMode.HardResetToBaseBranch)
        {
            try
            {
                await _environmentProvider.CleanupTaskContainerAsync(workspace, task.Id, cancellationToken);
                await _gitWorkspaceManager.ResetTaskBranchToBaseAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed resetting workspace and Git state during pipeline restart for Task #{TaskId}", task.Id);
                task.Status = TaskOverallStatus.Failed;
                await _taskRepository.UpdateWithHistoryAsync(
                    task,
                    dependsOn: null,
                    changedBy: AuditActors.TaskOrchestrator,
                    reason: $"Pipeline restart failed: {ex.Message}",
                    cancellationToken: cancellationToken);
                await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Failed, cancellationToken);
                await _taskRepository.AddExecutionLogAsync(new ExecutionLog
                {
                    TaskId = task.Id,
                    Level = LogLevel.Error,
                    Message = $"Pipeline restart failed during environment/Git reset: {ex.Message}",
                    Timestamp = DateTime.UtcNow
                }, cancellationToken);
                throw;
            }

            // Purge pipeline execution data bag and execution run flag only on HardReset
            task.PipelineDataJson = "{}";
            task.HasExecutionRun = false;
            task.MaxExecutedStageOrder = 0;
        }

        // 7. Reset Task State
        task.Status = TaskOverallStatus.Open;
        task.SquashCommitSha = null;
        task.IsWaitingForHuman = false;
        task.InterruptedStageId = null;
        task.InterruptedAgentProfileId = null;
        task.UpdatedAt = DateTime.UtcNow;

        // 8. Stage Conversations - mark active or pending as Superseded
        var convs = await _taskRepository.GetConversationsAsync(task.Id, cancellationToken);
        foreach (var c in convs.Where(c => c.Status == StageExecutionStatus.Running || c.Status == StageExecutionStatus.WaitingForHuman || c.Status == StageExecutionStatus.Pending))
        {
            await _taskRepository.CompleteConversationAsync(c.Id, StageExecutionStatus.Superseded, summary: "Pipeline restarted; previous conversation superseded.", cancellationToken: cancellationToken);
        }

        // 7. Resolve Initial Stage
        var pipeline = await _pipelineProvider.GetPipelineAsync(task, cancellationToken);
        var initialStage = pipeline.InitialStage ?? throw new InvalidOperationException($"Task #{task.Id} has no initial workflow stage.");
        task.CurrentStageId = initialStage.StageId;

        // 8. Update DB Record with History
        var changeReason = gitMode == TaskRestartGitMode.HardResetToBaseBranch
            ? $"Pipeline restarted from Stage 1 with Hard Reset to '{task.BaseBranch}' (discards previous work)"
            : "Pipeline restarted from Stage 1 with preserved worktree changes";

        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.User,
            reason: changeReason,
            cancellationToken: cancellationToken);

        await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Open, cancellationToken);

        // 9. Execution Log
        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Information,
            Message = changeReason,
            Timestamp = DateTime.UtcNow
        }, cancellationToken);

        // 10. SignalR Telemetry Event
        try
        {
            await _streamer.BroadcastAsync(new Streaming.AgentActivityEvent(
                Guid.NewGuid().ToString("N"),
                task.Id,
                initialStage.StageId,
                Streaming.AgentActivityKind.Notice,
                $"Pipeline restarted for task #{task.Id} from stage '{initialStage.StageId}' ({gitMode})",
                DateTime.UtcNow,
                new Dictionary<string, object?>
                {
                    ["IsRestart"] = true,
                    ["RestartMode"] = gitMode.ToString(),
                    [Streaming.AgentActivityMetadataKeys.TaskTitle] = task.Title,
                    [Streaming.AgentActivityMetadataKeys.Priority] = task.Priority,
                    [Streaming.AgentActivityMetadataKeys.StageKind] = initialStage.Kind.ToString(),
                    [Streaming.AgentActivityMetadataKeys.QueueType] = initialStage.Kind == WorkflowStageKind.Deterministic
                        ? DeterministicStageConstants.CpuDeterministicQueue
                        : DeterministicStageConstants.LlmInferenceQueue
                }), cancellationToken);
        }
        catch (Exception sex)
        {
            _logger.LogDebug(sex, "Failed broadcasting stage restarted event for task #{TaskId}", task.Id);
        }

        // 11. Re-enqueue Initial Stage
        task.IsDeferredByPause = false;
        var newJobId = _jobQueue.EnqueueStage(task.Id, initialStage.QueueTier, task.Priority);
        task.LastHangfireJobId = newJobId;
        await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);

        // 12. Markdown Sync
        await SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

        _logger.LogInformation("Successfully restarted pipeline for Task #{TaskId} (Mode: {GitMode}, InitialStage: {StageId}, JobId: {JobId})",
            taskId, gitMode, initialStage.StageId, newJobId);

        return true;
    }

    private async Task PopulateReviewDiffsAsync(Workspace workspace, ForgeTask task, PipelineContext pipelineContext, CancellationToken cancellationToken)
    {
        try
        {
            var diffStat = await _gitWorkspaceManager.GenerateDiffStatAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
            pipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewDiffStat, diffStat);
        }
        catch (Exception ex)
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed generating live diff for review stage for task #{TaskId}", task.Id);
        }
    }

    private static void SetSyntheticPrimingFromDeterministicStage(
        IWorkflowStage stage,
        PipelineContext pipelineContext,
        StageExecutionResult result,
        int stageDurationMs)
    {
        var commandExecutions = pipelineContext.DataBag.Get<List<StageCommandExecution>>(PipelineDataBagKeys.StageCommands(stage.StageId));
        if (result.IsSuccess)
        {
            StageCommandExecution? selected = null;
            if (commandExecutions is { Count: > 0 })
            {
                selected = commandExecutions.FirstOrDefault(c =>
                    string.Equals(c.StageName, DeterministicStageConstants.TestWithCoverageSubStage, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.StageName, DeterministicStageConstants.TestSubStage, StringComparison.OrdinalIgnoreCase))
                    ?? commandExecutions.Last();
            }

            var command = selected?.Command
                ?? pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.StageCommand(stage.StageId))
                ?? pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.TestCommand)
                ?? pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.BuildCommand)
                ?? DeterministicStageConstants.DefaultTestCommand;

            var rawOutput = selected?.Output
                ?? pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.StageOutput(stage.StageId))
                ?? "Verification completed successfully with exit code 0.";

            var formattedOutput = rawOutput.StartsWith("Exit Code:", StringComparison.OrdinalIgnoreCase)
                ? rawOutput
                : $"Exit Code: 0\n{rawOutput}".TrimEnd();

            formattedOutput = WorkflowDefaults.TruncateStageOutput(formattedOutput);

            var duration = selected?.DurationMs > 0 ? selected.DurationMs : (stageDurationMs > 0 ? stageDurationMs : (long?)null);

            pipelineContext.DataBag.Set(PipelineDataBagKeys.PendingSyntheticPriming, new SyntheticPrimingExecution(
                SourceStageId: stage.StageId,
                ToolName: CanonicalToolNames.Bash,
                Command: command,
                Output: formattedOutput,
                IsSuccess: true,
                DurationMs: duration));
        }
        else
        {
            StageCommandExecution? failed = null;
            if (commandExecutions is { Count: > 0 })
            {
                failed = commandExecutions.LastOrDefault(c => !c.IsSuccess) ?? commandExecutions.Last();
            }

            var command = failed?.Command
                ?? pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.StageCommand(stage.StageId))
                ?? pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.TestCommand)
                ?? pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.BuildCommand)
                ?? DeterministicStageConstants.DefaultTestCommand;

            var rawOutput = failed?.Output
                ?? pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.StageOutput(stage.StageId))
                ?? result.ErrorMessage
                ?? "Deterministic step failed.";

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage) && !rawOutput.Contains(result.ErrorMessage, StringComparison.OrdinalIgnoreCase))
            {
                rawOutput = $"{rawOutput.TrimEnd()}\n\n[Quality Gate Failure Reason]:\n{result.ErrorMessage}";
            }

            var compilerErrors = pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.CompilerErrors);
            var failedTests = pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.FailedTests);

            var diagSb = new StringBuilder();
            if (compilerErrors is { Count: > 0 } && !rawOutput.Contains("[Compiler Diagnostics]", StringComparison.OrdinalIgnoreCase))
            {
                // Filter out test failures populated into CompilerErrors by PopulateDiagnosticsIntoDataBag
                // to avoid duplicating them when [Failed Tests] is rendered below.
                var actualCompilerErrors = compilerErrors
                    .Where(err => !err.Contains(TestFailureMarker, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (actualCompilerErrors.Count > 0)
                {
                    diagSb.AppendLine().AppendLine("[Compiler Diagnostics]:");
                    foreach (var err in actualCompilerErrors)
                    {
                        diagSb.AppendLine(err);
                    }
                }
            }
            if (failedTests is { Count: > 0 } && !rawOutput.Contains("[Failed Tests]", StringComparison.OrdinalIgnoreCase))
            {
                diagSb.AppendLine().AppendLine("[Failed Tests]:");
                foreach (var ft in failedTests)
                {
                    diagSb.AppendLine(ft);
                }
            }

            if (diagSb.Length > 0)
            {
                rawOutput = $"{rawOutput.TrimEnd()}\n{diagSb.ToString().TrimEnd()}";
            }

            var formattedOutput = rawOutput.StartsWith("Exit Code:", StringComparison.OrdinalIgnoreCase)
                ? rawOutput
                : $"Exit Code: 1\n{rawOutput}".TrimEnd();

            formattedOutput = WorkflowDefaults.TruncateStageOutput(formattedOutput);

            var duration = failed?.DurationMs > 0 ? failed.DurationMs : (stageDurationMs > 0 ? stageDurationMs : (long?)null);

            pipelineContext.DataBag.Set(PipelineDataBagKeys.PendingSyntheticPriming, new SyntheticPrimingExecution(
                SourceStageId: stage.StageId,
                ToolName: CanonicalToolNames.Bash,
                Command: command,
                Output: formattedOutput,
                IsSuccess: false,
                DurationMs: duration));
        }
    }
}

