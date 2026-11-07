using System.Text;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Central implementation of <see cref="ITaskOrchestrator"/> orchestrating stage and cohort pipeline execution,
/// delegating workspace/Git/markdown preparation to <see cref="ITaskWorkspacePreparer"/>,
/// merge gate and DAG cascade unblocking to <see cref="ITaskMergeAndCascadeCoordinator"/>,
/// and human interaction lifecycle mutations to <see cref="ITaskLifecycleMutationCoordinator"/>.
/// </summary>
public class TaskOrchestrator : ITaskOrchestrator
{
    private readonly ITaskRepository _taskRepository;
    private readonly ITaskWorkspacePreparer _workspacePreparer;
    private readonly ITaskMergeAndCascadeCoordinator _mergeAndCascadeCoordinator;
    private readonly ITaskLifecycleMutationCoordinator _lifecycleMutationCoordinator;
    private readonly IHarnessRegistry _harnessRegistry;
    private readonly IAgentActivityStreamer _streamer;
    private readonly IOperationsControlService _operationsControlService;
    private readonly IOperationsTelemetryService _telemetryService;
    private readonly ILogger<TaskOrchestrator> _logger;

    private static readonly TimeSpan PauseAbortTimeout = TimeSpan.FromSeconds(5);

    public TaskOrchestrator(
        ITaskRepository taskRepository,
        ITaskWorkspacePreparer workspacePreparer,
        ITaskMergeAndCascadeCoordinator mergeAndCascadeCoordinator,
        ITaskLifecycleMutationCoordinator lifecycleMutationCoordinator,
        IHarnessRegistry harnessRegistry,
        IAgentActivityStreamer streamer,
        IOperationsControlService operationsControlService,
        IOperationsTelemetryService telemetryService,
        ILogger<TaskOrchestrator> logger)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _workspacePreparer = workspacePreparer ?? throw new ArgumentNullException(nameof(workspacePreparer));
        _mergeAndCascadeCoordinator = mergeAndCascadeCoordinator ?? throw new ArgumentNullException(nameof(mergeAndCascadeCoordinator));
        _lifecycleMutationCoordinator = lifecycleMutationCoordinator ?? throw new ArgumentNullException(nameof(lifecycleMutationCoordinator));
        _harnessRegistry = harnessRegistry ?? throw new ArgumentNullException(nameof(harnessRegistry));
        _streamer = streamer ?? throw new ArgumentNullException(nameof(streamer));
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
        PipelineContext? pipelineContext = null;
        var cohortSw = System.Diagnostics.Stopwatch.StartNew();
        CohortExitReason cohortExitReason = CohortExitReason.Aborted;

        try
        {
            // 1. DAG Readiness Check
            if (!await _mergeAndCascadeCoordinator.EnsureReadyForExecutionAsync(task, cancellationToken))
            {
                return;
            }

            var initialJobId = task.LastHangfireJobId;

            // 2. ForgeTask Lock Acquisition (Enables concurrent execution of different tasks on the same workspace)
            using var lockReleaser = await _workspacePreparer.AcquireTaskLockAsync(task.WorkspaceId, task.Id, cancellationToken);

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

            taskCts = _lifecycleMutationCoordinator.RegisterExecutionCancellation(task.Id, cancellationToken);
            cancellationToken = taskCts.Token;

            var workspace = await _workspacePreparer.GetWorkspaceAsync(task, cancellationToken);
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
                await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, TaskOverallStatus.Failed, cancellationToken);
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
            try
            {
                await _workspacePreparer.PrepareTaskBranchAndMirrorAsync(
                    workspace,
                    task,
                    async (previousBaseBranch, resolvedBaseBranch, ct) =>
                    {
                        await _taskRepository.UpdateWithHistoryAsync(
                            task,
                            dependsOn: null,
                            changedBy: AuditActors.TaskOrchestrator,
                            reason: $"Resolved BaseBranch from \"{previousBaseBranch}\" to workspace default \"{resolvedBaseBranch}\"",
                            cancellationToken: ct);
                    },
                    cancellationToken);
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
                await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, TaskOverallStatus.Failed, cancellationToken);
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
            var pipeline = await _mergeAndCascadeCoordinator.GetPipelineAsync(task, cancellationToken);
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
                await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, TaskOverallStatus.Failed, cancellationToken);
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
                await _mergeAndCascadeCoordinator.SetWaitingForHumanInStoreAsync(task.Id, false, cancellationToken);
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

            var resolvedPaths = _workspacePreparer.ResolveWorkspacePaths(workspace, task.Id);
            var taskWorkingPath = resolvedPaths.TaskWorkingPath;
            var hostRepoPath = resolvedPaths.HostRepoPath;
            var targetSyncPath = resolvedPaths.TargetSyncPath;

            pipelineContext = new PipelineContext(task, workspace)
            {
                WorkingDirectory = taskWorkingPath,
                Pipeline = pipeline
            };

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
                if (!isFirstIteration && await _mergeAndCascadeCoordinator.HasUrgentJobsWaitingAsync(cancellationToken))
                {
                    _logger.LogInformation("Cooperatively yielding task #{TaskId} before stage '{StageId}' (not yet executed; CurrentStageId == StageId, resume re-enters here) for pending P1 Urgent job.", task.Id, stage.StageId);
                    task.CurrentStageId = stage.StageId;
                    task.InterruptedStageId = activeInterruptedStageId;
                    pipelineContext.SyncDataBagToTask();
                    task.LastHangfireJobId = _mergeAndCascadeCoordinator.EnqueueStage(task.Id, stage.QueueTier, task.Priority);
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
                var profile = await _workspacePreparer.ResolveAgentProfileAsync(workspace, task, stage.StageId, cancellationToken);
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
                        await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, TaskOverallStatus.Failed, cancellationToken);
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
                bool wasResumedStageRun = isResumedRun;
                bool modifiedDuringPreResumeSync = false;
                StageExecutionResult? result = null;
                StageExecutionRecord? record = null;
                var startedAt = DateTime.UtcNow;
                int durationMs = 0;

                if (stage.Kind == WorkflowStageKind.Deterministic)
                {
                    try
                    {
                        var existingStageConvs = await _taskRepository.GetConversationsAsync(task.Id, cancellationToken);
                        var maxExistingAttempt = (existingStageConvs ?? Array.Empty<Conversation>())
                            .Where(c => string.Equals(c.StageId, stage.StageId, StringComparison.OrdinalIgnoreCase))
                            .Select(c => c.AttemptIndex)
                            .DefaultIfEmpty(0)
                            .Max();
                        int attemptIndex = Math.Max(
                            (pipelineContext.StageRetries.GetValueOrDefault(stage.StageId, 0)) + 1,
                            maxExistingAttempt + 1);
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
                        await _streamer.BroadcastAsync(new AgentActivityEvent(
                            Guid.NewGuid().ToString("N"),
                            task.Id,
                            stage.StageId,
                            AgentActivityKind.StageStarted,
                            $"Starting stage '{stage.StageId}'",
                            DateTime.UtcNow,
                            new Dictionary<string, object?>
                            {
                                [AgentActivityMetadataKeys.StageKind] = stage.Kind.ToString(),
                                [AgentActivityMetadataKeys.TaskTitle] = task.Title,
                                [AgentActivityMetadataKeys.Priority] = task.Priority,
                                [AgentActivityMetadataKeys.QueueType] = stage.Kind == WorkflowStageKind.Deterministic
                                    ? DeterministicStageConstants.CpuDeterministicQueue
                                    : DeterministicStageConstants.LlmInferenceQueue
                            }), cancellationToken);
                    }
                    catch (Exception sex)
                    {
                        _logger.LogDebug(sex, "Failed broadcasting stage started event for task #{TaskId}", task.Id);
                    }

                    var preSyncOutcome = await _workspacePreparer.SyncPreStageMarkdownAsync(
                        workspace,
                        task,
                        pipelineContext,
                        stage,
                        targetSyncPath,
                        hostRepoPath,
                        isResumedRun,
                        cancellationToken);
                    baselineSnapshot = preSyncOutcome.BaselineSnapshot;
                    modifiedDuringPreResumeSync = preSyncOutcome.ModifiedDuringPreResumeSync;
                    isResumedRun = false;

                    // Gate 1: Pre-Implementation Baseline Synchronization
                    if (string.Equals(stage.StageId, StandardStageIds.PreImplementationBaseline, StringComparison.OrdinalIgnoreCase))
                    {
                        await ExecuteGate1BaselineSyncAsync(workspace, task, stage, deterministicConv, v => deterministicConvFinalized = v, cancellationToken);
                    }

                    // Marker Materialization at Implementation stage start
                    if (string.Equals(stage.StageId, StandardStageIds.Implementation, StringComparison.OrdinalIgnoreCase))
                    {
                        await _workspacePreparer.MaterializeConflictMarkersForImplementationAsync(workspace, task, pipelineContext, cancellationToken);
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
                                    if (string.IsNullOrEmpty(resumeMemory))
                                    {
                                        IEnumerable<Conversation>? latestStageConvs = await _taskRepository.GetConversationsAsync(task.Id, cancellationToken);
                                        latestStageConvs ??= task.Conversations;
                                        if (_lifecycleMutationCoordinator.CanRestoreCheckpointMemory(latestStageConvs, lastCheckpoint, stage.StageId))
                                        {
                                            resumeMemory = lastCheckpoint!.SerializedMemory;
                                        }
                                    }

                                    var reviewDiffStat = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.ReviewDiffStat);
                                    if (string.IsNullOrWhiteSpace(reviewDiffStat) &&
                                        (string.Equals(stage.StageId, StandardStageIds.CodeReviewAndFix, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(stage.StageId, StandardStageIds.PostReviewVerification, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        await _workspacePreparer.PopulateReviewDiffsAsync(workspace, task, pipelineContext, cancellationToken);
                                        reviewDiffStat = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.ReviewDiffStat);
                                    }

                                    var bootstrapPrompt = stage.BuildBootstrapPrompt(task, pipelineContext, stageDirective);

                                    SyntheticPrimingExecution? syntheticPriming = null;
                                    var pendingPriming = pipelineContext.DataBag.Get<SyntheticPrimingExecution>(PipelineDataBagKeys.PendingSyntheticPriming);
                                    var effectiveOptimizationFlags = pipelineContext.DataBag.Get<LocalOptimizationFlags>(PipelineDataBagKeys.OptimizationFlags)
                                        ?? profile.OptimizationFlags ?? new LocalOptimizationFlags();

                                    if (pendingPriming != null && effectiveOptimizationFlags.SyntheticConversationPriming)
                                    {
                                        syntheticPriming = pendingPriming;
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

                                    // Consume once after non-cancelled execution: a failed/completed run must not replay stale recovered history, answers, or priming later.
                                    pipelineContext.DataBag.Remove(PipelineDataBagKeys.RecoveredSerializedMemory);
                                    pipelineContext.DataBag.Remove(PipelineDataBagKeys.PendingSyntheticPriming);
                                    if (isInterruptedStage)
                                    {
                                        pipelineContext.DataBag.Remove(PipelineDataBagKeys.LastHumanAnswer);
                                        pipelineContext.DataBag.Remove(PipelineDataBagKeys.LastHumanAnswerJson);
                                        activeInterruptedStageId = null;
                                        task.InterruptedStageId = null;
                                    }

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
                                    await _streamer.BroadcastAsync(new AgentActivityEvent(
                                        Guid.NewGuid().ToString("N"),
                                        task.Id,
                                        stage.StageId,
                                        AgentActivityKind.ToolStarted,
                                        $"{stage.StageId}: Running {stage.DisplayName}...",
                                        DateTime.UtcNow,
                                        new Dictionary<string, object?>
                                        {
                                            [AgentActivityMetadataKeys.ToolName] = stage.StageId
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
                                    await _streamer.BroadcastAsync(new AgentActivityEvent(
                                        Guid.NewGuid().ToString("N"),
                                        task.Id,
                                        stage.StageId,
                                        AgentActivityKind.ToolCompleted,
                                        $"{stage.StageId}: {standaloneOut}",
                                        DateTime.UtcNow,
                                        new Dictionary<string, object?>
                                        {
                                            [AgentActivityMetadataKeys.ToolName] = stage.StageId,
                                            [AgentActivityMetadataKeys.DurationMs] = standaloneDuration
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
                    catch (OperationsPausedException opex)
                    {
                        _logger.LogInformation(opex, "Stage '{StageId}' for task #{TaskId} was paused by global operations pause.", stage.StageId, taskId);
                        result = StageExecutionResult.Cancelled("Operations paused by user (GPU VRAM reclaimed).");
                    }
                    catch (OperationCanceledException ocex)
                        when (cancellationToken.IsCancellationRequested ||
                              _lifecycleMutationCoordinator.IsCancelRequested(task.Id) ||
                              _lifecycleMutationCoordinator.IsPauseRequested(task.Id) ||
                              _operationsControlService.IsPaused)
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
                        await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, TaskOverallStatus.Failed, cancellationToken);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Stage '{StageId}' failed with unhandled exception for task #{TaskId}", stage.StageId, taskId);
                        result = StageExecutionResult.Failure(HttpErrorDetailHelper.FormatLlmInvocationError(ex));
                    }

                    if (result == null)
                    {
                        throw new InvalidOperationException($"Stage execution for '{stage.StageId}' did not produce a result.");
                    }

                    var completedAt = DateTime.UtcNow;
                    durationMs = (int)(completedAt - startedAt).TotalMilliseconds;

                    // Save execution metrics record
                    record = result.ExecutionRecord ?? new StageExecutionRecord();
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
                                var existingStageConvs = await _taskRepository.GetConversationsAsync(task.Id, finishToken);
                                var maxExistingAttempt = (existingStageConvs ?? Array.Empty<Conversation>())
                                    .Where(c => string.Equals(c.StageId, stage.StageId, StringComparison.OrdinalIgnoreCase))
                                    .Select(c => c.AttemptIndex)
                                    .DefaultIfEmpty(0)
                                    .Max();
                                int attemptIndex = Math.Max(
                                    (pipelineContext.StageRetries.GetValueOrDefault(stage.StageId, 0)) + 1,
                                    maxExistingAttempt + 1);
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
                                        await _streamer.BroadcastAsync(new AgentActivityEvent(
                                            Guid.NewGuid().ToString("N"),
                                            task.Id,
                                            stage.StageId,
                                            AgentActivityKind.StageCompleted,
                                            summary,
                                            DateTime.UtcNow,
                                            new Dictionary<string, object?> { [AgentActivityMetadataKeys.StageKind] = nameof(WorkflowStageKind.Deterministic) }), finishToken);
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

                var isStageCancelledOrPaused = result.IsCancelled || (cancellationToken.IsCancellationRequested && !result.IsSuccess);

                // Auto-persist any backlog proposals emitted by triage or discovery agents (propose_tasks)
                var pendingProposals = !isStageCancelledOrPaused
                    ? pipelineContext.DataBag.Get<List<TaskProposal>>(PipelineDataBagKeys.PendingProposals)
                    : null;
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
                        await _workspacePreparer.SyncMarkdownTaskSafeAsync(workspace, suggestedTask, cancellationToken);
                    }
                    pipelineContext.DataBag.Remove(PipelineDataBagKeys.PendingProposals);
                }

                // Read back all disk modifications to tasks using single authority (TaskMarkdownSyncService)
                if (!isStageCancelledOrPaused && !string.IsNullOrWhiteSpace(targetSyncPath) && Directory.Exists(targetSyncPath))
                {
                    var stageActor = AuditActors.GetActorForStage(stage.StageId);
                    var stageReason = result.NeedsClarification
                        ? $"Stage '{stage.StageId}' pausing for human clarification"
                        : $"Stage '{stage.StageId}' completed disk synchronization";

                    IReadOnlyList<ForgeTask>? refreshedTasks = null;
                    const int maxConflictRetries = 5;
                    int conflictRetries = 0;

                    while (true)
                    {
                        try
                        {
                            refreshedTasks = await _workspacePreparer.SyncAllTasksFromDiskAsync(
                                workspace,
                                targetSyncPath,
                                hostRepoPath,
                                baselineSnapshot,
                                stageActor,
                                stageReason,
                                currentTaskId: task.Id,
                                cancellationToken: cancellationToken);
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
                                var authoritativeDbTask = await _taskRepository.GetByIdAsync(task.Id, cancellationToken);
                                if (authoritativeDbTask != null)
                                {
                                    TaskWorkspacePreparer.CopyTaskMetadata(task, authoritativeDbTask);
                                }

                                await _workspacePreparer.SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

                                if (!string.IsNullOrWhiteSpace(targetSyncPath) &&
                                    !string.Equals(targetSyncPath, hostRepoPath, StringComparison.OrdinalIgnoreCase) &&
                                    Directory.Exists(targetSyncPath))
                                {
                                    await _workspacePreparer.SyncTaskToWorktreeSafeAsync(task, targetSyncPath, cancellationToken);
                                }

                                if (conflictRetries >= maxConflictRetries)
                                {
                                    _logger.LogWarning("Exceeded max retries ({MaxRetries}) resolving task #{TaskId} concurrency conflicts; continuing pipeline.", maxConflictRetries, task.Id);
                                    break;
                                }
                            }
                            else
                            {
                                var otherConflictedTask = await _taskRepository.GetByIdAsync(cex.TaskId, cancellationToken);
                                if (otherConflictedTask != null)
                                {
                                    await _workspacePreparer.SyncTaskMarkdownAndMirrorAsync(otherConflictedTask, workspace, cancellationToken);
                                    if (!string.IsNullOrWhiteSpace(targetSyncPath) &&
                                        !string.Equals(targetSyncPath, hostRepoPath, StringComparison.OrdinalIgnoreCase) &&
                                        Directory.Exists(targetSyncPath))
                                    {
                                        await _workspacePreparer.SyncTaskToWorktreeSafeAsync(otherConflictedTask, targetSyncPath, cancellationToken);
                                    }
                                }

                                if (conflictRetries >= maxConflictRetries)
                                {
                                    _logger.LogWarning("Exceeded max retries ({MaxRetries}) resolving foreign task concurrency conflicts; continuing pipeline.", maxConflictRetries);
                                    break;
                                }
                            }
                        }
                        catch (Exception syncEx) when (result.NeedsClarification && syncEx is not OperationCanceledException)
                        {
                            _logger.LogWarning(syncEx, "Failed syncing disk tasks to database upon interruption for task #{TaskId}", task.Id);
                            break;
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
                        TaskWorkspacePreparer.CopyTaskMetadata(task, refreshedTask);
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

                        // Only an earlier modification by this stage during the current pipeline cycle excuses an unchanged resumed run.
                        bool alreadyModifiedByEarlierRun = modifiedDuringPreResumeSync;
                        if (!modifiedInThisStage && !alreadyModifiedByEarlierRun && wasResumedStageRun)
                        {
                            var history = await _taskRepository.GetHistoryAsync(task.Id, cancellationToken);
                            if (history != null && history.Count > 0)
                            {
                                var lastRestartIndex = -1;
                                for (int i = history.Count - 1; i >= 0; i--)
                                {
                                    var reason = history[i].ChangeReason;
                                    if (reason != null &&
                                        reason.Contains("Pipeline restarted", StringComparison.OrdinalIgnoreCase))
                                    {
                                        lastRestartIndex = i;
                                        break;
                                    }
                                }

                                for (int i = lastRestartIndex + 1; i < history.Count; i++)
                                {
                                    if (string.Equals(history[i].ChangedBy, stageActor, StringComparison.OrdinalIgnoreCase))
                                    {
                                        alreadyModifiedByEarlierRun = true;
                                        break;
                                    }
                                }
                            }
                        }

                        var remediationRetryKey = $"{stage.StageId}:missing-doc";
                        if (modifiedInThisStage || alreadyModifiedByEarlierRun)
                        {
                            pipelineContext.ResetStageRetry(remediationRetryKey);
                        }
                        else
                        {
                            var remediationMaxRetries = pipelineContext.DataBag.Get<int?>(PipelineDataBagKeys.MaxRetries) ?? (stage.MaxRetries > 0 ? stage.MaxRetries : WorkflowDefaults.DefaultMaxStageRetries);
                            var outcome = await RemediateStageAsync(
                                task,
                                pipelineContext,
                                stage,
                                remediationRetryKey,
                                remediationMaxRetries,
                                StageRemediationMessages.MissingSpecificationPrompt,
                                "Awaiting task specification document update",
                                "prompted agent to update task specification document",
                                $"Stage '{stage.StageId}' completed without producing a modified task specification document after {remediationMaxRetries} conversational remediation attempts.",
                                "document remediation budget",
                                cancellationToken);

                            if (outcome.ShouldResume)
                            {
                                isResumedRun = true;
                                activeInterruptedStageId = stage.StageId;
                                continue;
                            }

                            var failureResult = StageExecutionResult.Failure(outcome.FailureMessage, record: record);
                            await HandleStageFatalFailureAsync(task, workspace, pipelineContext, stage, failureResult, cancellationToken, remediationRetryKey);
                            cohortExitReason = CohortExitReason.Failed;
                            return;
                        }
                    }
                }

                // Implementation artifact gate: a stage that "succeeds" without touching any production/test file is a false completion.
                if (string.Equals(stage.StageId, StandardStageIds.Implementation, StringComparison.OrdinalIgnoreCase) &&
                    result.IsSuccess && !isStageCancelledOrPaused && !result.NeedsClarification &&
                    pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.ConflictFiles) is not { Count: > 0 })
                {
                    var emptyDiffRetryKey = $"{stage.StageId}:empty-diff";
                    if (await _workspacePreparer.HasImplementationChangesAsync(workspace, task, cancellationToken))
                    {
                        pipelineContext.ResetStageRetry(emptyDiffRetryKey);
                    }
                    else
                    {
                        var emptyDiffMaxRetries = pipelineContext.DataBag.Get<int?>(PipelineDataBagKeys.MaxRetries) ?? (stage.MaxRetries > 0 ? stage.MaxRetries : WorkflowDefaults.DefaultMaxStageRetries);
                        var emptyDiffOutcome = await RemediateStageAsync(
                            task,
                            pipelineContext,
                            stage,
                            emptyDiffRetryKey,
                            emptyDiffMaxRetries,
                            StageRemediationMessages.EmptyImplementationPrompt,
                            "Awaiting code implementation",
                            "prompted agent to implement the required code changes",
                            $"Stage '{stage.StageId}' completed without modifying any repository source or test files after {emptyDiffMaxRetries} conversational remediation attempts.",
                            "implementation remediation budget",
                            cancellationToken);

                        if (emptyDiffOutcome.ShouldResume)
                        {
                            isResumedRun = true;
                            activeInterruptedStageId = stage.StageId;
                            continue;
                        }

                        var emptyDiffFailure = StageExecutionResult.Failure(emptyDiffOutcome.FailureMessage, record: record);
                        await HandleStageFatalFailureAsync(task, workspace, pipelineContext, stage, emptyDiffFailure, cancellationToken, emptyDiffRetryKey);
                        cohortExitReason = CohortExitReason.Failed;
                        return;
                    }
                }

                // 5. Result Evaluation
                if (result.IsCancelled || (cancellationToken.IsCancellationRequested && !result.IsSuccess))
                {
                    _logger.LogInformation("Processing of task #{TaskId} at stage '{StageId}' was cancelled or paused.", task.Id, stage.StageId);

                    using var timeoutCts = new CancellationTokenSource(PauseAbortTimeout);
                    var ct = timeoutCts.Token;

                    var isPause = _lifecycleMutationCoordinator.IsPauseRequested(task.Id) || _operationsControlService.IsPaused;
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
                            currentTask.PipelineDataJson = task.PipelineDataJson;
                            await _taskRepository.UpdateWithHistoryAsync(
                                currentTask,
                                dependsOn: null,
                                changedBy: AuditActors.User,
                                reason: "Operations paused by user (GPU reclaimed).",
                                cancellationToken: ct);
                            await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, TaskOverallStatus.Open, ct);
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
                        var activeConvs = convs.Where(c =>
                            c.Status == StageExecutionStatus.Running ||
                            c.Status == StageExecutionStatus.WaitingForHuman ||
                            c.Status == StageExecutionStatus.Interrupted).ToList();
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
                    if (_lifecycleMutationCoordinator.IsCancelRequested(task.Id))
                    {
                        var persistedTask = await _taskRepository.GetByIdAsync(task.Id, ct);
                        if (persistedTask?.Status != TaskOverallStatus.Closed && persistedTask?.Status != TaskOverallStatus.Rejected)
                        {
                            task.Status = TaskOverallStatus.Cancelled;
                            task.IsWaitingForHuman = false;
                            if (task.Questions != null)
                            {
                                foreach (var q in task.Questions.Where(q => q.Status == QuestionStatus.Pending))
                                {
                                    q.Status = QuestionStatus.Skipped;
                                }
                            }
                            pipelineContext.SyncDataBagToTask();
                            await _taskRepository.UpdateWithHistoryAsync(
                                task,
                                dependsOn: null,
                                changedBy: AuditActors.User,
                                reason: $"Stage '{stage.StageId}' was cancelled.",
                                cancellationToken: ct);
                            await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, task.Status, ct);
                            await TaskBadgeCounter.SynchronizeBadgeCountsAsync(_taskRepository, _telemetryService, ct);
                        }
                    }
                    else
                    {
                        pipelineContext.SyncDataBagToTask();
                        await _taskRepository.UpdateAsync(task, cancellationToken: ct);
                        await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, task.Status, ct);
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
                        commitHash = await _workspacePreparer.CreateWipCheckpointAsync(
                            workspace,
                            task.Id,
                            stage.StageId,
                            $"WIP checkpoint before human clarification ({result.ClarificationQuestions.Count} question(s)): {summaryPrompt}",
                            cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
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
                    task.Checkpoints ??= new List<StageCheckpoint>();
                    if (!task.Checkpoints.Contains(checkpoint))
                    {
                        task.Checkpoints.Add(checkpoint);
                    }

                    // Universal Interruption: human interruption NEVER mutates task.Status!
                    task.IsWaitingForHuman = true;
                    task.InterruptedStageId = stage.StageId;
                    task.InterruptedAgentProfileId = profile?.Id;

                    pipelineContext.SyncDataBagToTask();
                    await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
                    await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, task.Status, cancellationToken);
                    await _mergeAndCascadeCoordinator.SetWaitingForHumanInStoreAsync(task.Id, true, cancellationToken);
                    await _workspacePreparer.SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);
                    await TaskBadgeCounter.SynchronizeBadgeCountsAsync(_taskRepository, _telemetryService, cancellationToken);

                    await _taskRepository.AddExecutionLogAsync(new ExecutionLog
                    {
                        TaskId = task.Id,
                        Level = LogLevel.Information,
                        Message = $"ForgeTask #{task.Id} paused waiting for human input ({result.ClarificationQuestions.Count} question(s), batch {batchId}): {summaryPrompt}",
                        Timestamp = DateTime.UtcNow
                    }, cancellationToken);

                    cohortExitReason = CohortExitReason.WaitingForHuman;
                    return;
                }

                // Finding L4: Re-verify cancellation token and persisted (untracked) status to ensure we never overwrite cancellation
                if (cancellationToken.IsCancellationRequested)
                {
                    if (_lifecycleMutationCoordinator.IsCancelRequested(task.Id))
                    {
                        var persistedStatusAfterStage = await _taskRepository.GetStatusAsync(task.Id, CancellationToken.None);
                        if (persistedStatusAfterStage != TaskOverallStatus.Closed && persistedStatusAfterStage != TaskOverallStatus.Rejected)
                        {
                            task.Status = TaskOverallStatus.Cancelled;
                            task.IsWaitingForHuman = false;
                            if (task.Questions != null)
                            {
                                foreach (var q in task.Questions.Where(q => q.Status == QuestionStatus.Pending))
                                {
                                    q.Status = QuestionStatus.Skipped;
                                }
                            }
                            await _taskRepository.UpdateWithHistoryAsync(
                                task,
                                dependsOn: null,
                                changedBy: AuditActors.User,
                                reason: $"Execution cancelled after stage '{stage.StageId}'.",
                                cancellationToken: CancellationToken.None);
                            await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, TaskOverallStatus.Cancelled, CancellationToken.None);
                            await TaskBadgeCounter.SynchronizeBadgeCountsAsync(_taskRepository, _telemetryService, CancellationToken.None);
                        }
                        cohortExitReason = CohortExitReason.Cancelled;
                        return;
                    }

                    if (_lifecycleMutationCoordinator.IsPauseRequested(task.Id) || _operationsControlService.IsPaused)
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
                            await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, TaskOverallStatus.Open, CancellationToken.None);
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
                    await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, TaskOverallStatus.Cancelled, CancellationToken.None);
                    await TaskBadgeCounter.SynchronizeBadgeCountsAsync(_taskRepository, _telemetryService, CancellationToken.None);
                    cohortExitReason = CohortExitReason.Cancelled;
                    return;
                }

                if (result.IsSuccess && string.Equals(stage.StageId, StandardStageIds.Implementation, StringComparison.OrdinalIgnoreCase))
                {
                    var conflictOutcome = await _mergeAndCascadeCoordinator.VerifyAndHandleImplementationConflictsAsync(
                        task,
                        workspace,
                        pipelineContext,
                        pipeline,
                        cancellationToken);

                    if (conflictOutcome == ConflictVerificationOutcome.EscalatedToHuman)
                    {
                        cohortExitReason = CohortExitReason.WaitingForHuman;
                        return;
                    }

                    if (conflictOutcome == ConflictVerificationOutcome.LoopedBackToImplementation)
                    {
                        cohortExitReason = CohortExitReason.Failed;
                        return;
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
                            await _streamer.BroadcastAsync(new AgentActivityEvent(
                                Guid.NewGuid().ToString("N"),
                                task.Id,
                                stage.StageId,
                                AgentActivityKind.StageCompleted,
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
                        commitHash = await _workspacePreparer.CreateWipCheckpointAsync(
                            workspace,
                            task.Id,
                            stage.StageId,
                            $"Completed stage '{stage.StageId}'",
                            cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
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
                    task.Checkpoints ??= new List<StageCheckpoint>();
                    if (!task.Checkpoints.Contains(checkpoint))
                    {
                        task.Checkpoints.Add(checkpoint);
                    }
                    pipelineContext.DataBag.Remove(PipelineDataBagKeys.SerializedMemory);
                    if (string.IsNullOrWhiteSpace(activeInterruptedStageId))
                    {
                        pipelineContext.DataBag.Remove(PipelineDataBagKeys.LastHumanAnswer);
                        pipelineContext.DataBag.Remove(PipelineDataBagKeys.LastHumanAnswerJson);
                    }

                    if (string.Equals(stage.StageId, StandardStageIds.Implementation, StringComparison.OrdinalIgnoreCase))
                    {
                        await _workspacePreparer.PopulateReviewDiffsAsync(workspace, task, pipelineContext, cancellationToken);
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
                            await _workspacePreparer.PopulateReviewDiffsAsync(workspace, task, pipelineContext, cancellationToken);
                        }

                        pipelineContext.SyncDataBagToTask();
                        await _taskRepository.UpdateWithHistoryAsync(
                            task,
                            dependsOn: null,
                            changedBy: AuditActors.TaskOrchestrator,
                            reason: $"Stage '{stage.StageId}' completed. Advanced to '{nextStage.StageId}'. Status: {task.Status}",
                            cancellationToken: cancellationToken);
                        await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, task.Status, cancellationToken);
                        await _workspacePreparer.SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

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
                            task.LastHangfireJobId = _mergeAndCascadeCoordinator.EnqueueStage(task.Id, nextStage.QueueTier, task.Priority);
                            await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
                            cohortExitReason = CohortExitReason.CohortCompleted;
                            return;
                        }
                    }
                    else
                    {
                        // Gate 2: Pre-Review / Ready Gate Verification
                        var gate2Outcome = await _mergeAndCascadeCoordinator.CheckAndSyncGate2MergeabilityAsync(
                            task,
                            workspace,
                            pipelineContext,
                            stage.StageId,
                            cancellationToken);

                        if (gate2Outcome.DispatchedConflictResolution)
                        {
                            cohortExitReason = task.IsWaitingForHuman ? CohortExitReason.WaitingForHuman : CohortExitReason.Failed;
                            return;
                        }

                        if (gate2Outcome.BaseHadAdvanced)
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

                                await HandleStageFatalFailureAsync(task, workspace, pipelineContext, stage, reverifyResult, cancellationToken, reverifyRetryKey);
                                cohortExitReason = CohortExitReason.Failed;
                                return;
                            }
                        }

                        var diffStat = await _workspacePreparer.GenerateDiffStatSafeAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
                        pipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewDiffStat, diffStat);

                        if (task.MergeMode == MergeMode.Automatic)
                        {
                            _logger.LogInformation("ForgeTask #{TaskId} completed all stages with MergeMode.Automatic; executing auto-merge.", task.Id);
                            pipelineContext.SyncDataBagToTask();
                            CompleteTaskResult mergeResult;
                            using (await _workspacePreparer.AcquireWorkspaceLockAsync(workspace.Id, cancellationToken))
                            {
                                mergeResult = await _mergeAndCascadeCoordinator.CompleteTaskAndCascadeInternalAsync(task, workspace, $"Auto-merge task #{task.Id}: {task.Title}", cancellationToken);
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
                            await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, TaskOverallStatus.Ready, cancellationToken);

                            await _workspacePreparer.SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);
                            await TaskBadgeCounter.SynchronizeBadgeCountsAsync(_taskRepository, _telemetryService, cancellationToken);
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
                var effectiveMaxRetries = pipelineContext.DataBag.Get<int?>(PipelineDataBagKeys.MaxRetries) ?? (stage.MaxRetries > 0 ? stage.MaxRetries : WorkflowDefaults.DefaultMaxStageRetries);

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
        catch (Exception ocex) when (ocex is OperationCanceledException or OperationsPausedException)
        {
            _logger.LogInformation(ocex, "ForgeTask #{TaskId} processing was cancelled or interrupted.", taskId);
            using var timeoutCts = new CancellationTokenSource(PauseAbortTimeout);
            var ct = timeoutCts.Token;
            try
            {
                if (_lifecycleMutationCoordinator.IsCancelRequested(taskId))
                {
                    cohortExitReason = CohortExitReason.Cancelled;
                    _logger.LogInformation("Task #{TaskId} execution cancelled by user.", taskId);
                    await CloseActiveConversationsAsync(taskId, StageExecutionStatus.Cancelled, "Task cancelled by user.", ct);

                    var currentTask = await _taskRepository.GetByIdAsync(taskId, ct);
                    if (currentTask != null && currentTask.Status != TaskOverallStatus.Closed && currentTask.Status != TaskOverallStatus.Rejected)
                    {
                        currentTask.Status = TaskOverallStatus.Cancelled;
                        currentTask.IsWaitingForHuman = false;
                        if (currentTask.Questions != null)
                        {
                            foreach (var q in currentTask.Questions.Where(q => q.Status == QuestionStatus.Pending))
                            {
                                q.Status = QuestionStatus.Skipped;
                            }
                        }
                        await _taskRepository.UpdateWithHistoryAsync(
                            currentTask,
                            dependsOn: null,
                            changedBy: AuditActors.User,
                            reason: "Cancelled by user.",
                            cancellationToken: ct);
                        await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(taskId, TaskOverallStatus.Cancelled, ct);
                        await TaskBadgeCounter.SynchronizeBadgeCountsAsync(_taskRepository, _telemetryService, ct);
                    }
                    return;
                }

                if (_lifecycleMutationCoordinator.IsPauseRequested(taskId) || _operationsControlService.IsPaused)
                {
                    cohortExitReason = CohortExitReason.Paused;
                    _logger.LogInformation("Task #{TaskId} paused due to global operations suspension.", taskId);

                    await CloseActiveConversationsAsync(taskId, StageExecutionStatus.Interrupted, "Operations paused by user (GPU VRAM reclaimed).", ct);

                    var currentTask = await _taskRepository.GetByIdAsync(taskId, ct);
                    if (currentTask != null && currentTask.Status != TaskOverallStatus.Closed && currentTask.Status != TaskOverallStatus.Rejected)
                    {
                        if (pipelineContext != null)
                        {
                            pipelineContext.SyncDataBagToTask();
                            currentTask.PipelineDataJson = task.PipelineDataJson;
                        }
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
                        await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(taskId, TaskOverallStatus.Open, ct);
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
                    await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(taskId, TaskOverallStatus.Failed, CancellationToken.None);
                    await _mergeAndCascadeCoordinator.SetWaitingForHumanInStoreAsync(taskId, false, CancellationToken.None);
                    await TaskBadgeCounter.SynchronizeBadgeCountsAsync(_taskRepository, _telemetryService, CancellationToken.None);
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
                _lifecycleMutationCoordinator.UnregisterExecutionCancellation(taskId, taskCts);
                taskCts.Dispose();
            }
        }
    }

    public Task SubmitHumanAnswerAsync(int questionId, string answer, CancellationToken cancellationToken = default) =>
        _lifecycleMutationCoordinator.SubmitHumanAnswerAsync(questionId, answer, cancellationToken);

    public Task SubmitAnswersAsync(int taskId, IReadOnlyDictionary<int, string> answers, CancellationToken cancellationToken = default) =>
        _lifecycleMutationCoordinator.SubmitAnswersAsync(taskId, answers, cancellationToken);

    public Task RequestChangesAsync(int taskId, string revisionNotes, CancellationToken cancellationToken = default) =>
        _lifecycleMutationCoordinator.RequestChangesAsync(taskId, revisionNotes, cancellationToken);

    public Task PromoteSuggestedTaskAsync(
        int taskId,
        TaskOverallStatus targetStatus,
        int priority,
        MergeMode mergeMode,
        string? baseBranch = null,
        CancellationToken cancellationToken = default) =>
        _lifecycleMutationCoordinator.PromoteSuggestedTaskAsync(taskId, targetStatus, priority, mergeMode, baseBranch, cancellationToken);

    public Task RejectTaskAsync(int taskId, string? reason = null, CancellationToken cancellationToken = default) =>
        _lifecycleMutationCoordinator.RejectTaskAsync(taskId, reason, cancellationToken);

    public Task<CompleteTaskResult> CompleteTaskAndCascadeAsync(int taskId, string finalCommitMessage, CancellationToken cancellationToken = default) =>
        _mergeAndCascadeCoordinator.CompleteTaskAndCascadeAsync(taskId, finalCommitMessage, cancellationToken);

    public Task<bool> RestartTaskPipelineAsync(
        int taskId,
        TaskRestartGitMode gitMode = TaskRestartGitMode.PreserveWork,
        CancellationToken cancellationToken = default) =>
        _lifecycleMutationCoordinator.RestartTaskPipelineAsync(taskId, gitMode, cancellationToken);

    private async Task ExecuteGate1BaselineSyncAsync(
        Workspace workspace,
        ForgeTask task,
        IWorkflowStage stage,
        Conversation? deterministicConv,
        Action<bool> setDeterministicConvFinalized,
        CancellationToken cancellationToken)
    {
        var syncToolName = $"{stage.StageId}{DeterministicStageConstants.GitSyncToolSuffix}";
        var syncCmd = $"git merge {task.BaseBranch}";
        var syncSw = System.Diagnostics.Stopwatch.StartNew();

        if (deterministicConv != null)
        {
            try
            {
                await _streamer.BroadcastAsync(new AgentActivityEvent(
                    Guid.NewGuid().ToString("N"),
                    task.Id,
                    stage.StageId,
                    AgentActivityKind.ToolStarted,
                    $"{syncToolName}: Syncing from '{task.BaseBranch}'...",
                    DateTime.UtcNow,
                    new Dictionary<string, object?>
                    {
                        [AgentActivityMetadataKeys.ToolName] = syncToolName
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

        await _workspacePreparer.CreateWipCheckpointAsync(
            workspace,
            task.Id,
            StandardStageIds.TaskReview,
            "WIP checkpoint before Pre-Implementation Baseline sync",
            cancellationToken);

        var baselineSyncResult = await _workspacePreparer.SyncFromBaseBranchAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
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

                await _streamer.BroadcastAsync(new AgentActivityEvent(
                    Guid.NewGuid().ToString("N"),
                    task.Id,
                    stage.StageId,
                    AgentActivityKind.ToolCompleted,
                    $"{syncToolName}: {syncResultMsg}",
                    DateTime.UtcNow,
                    new Dictionary<string, object?>
                    {
                        [AgentActivityMetadataKeys.ToolName] = syncToolName,
                        [AgentActivityMetadataKeys.DurationMs] = syncDuration
                    }), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed broadcasting or recording Gate 1 sync tool completed turn for task #{TaskId}", task.Id);
            }
        }

        if (baselineSyncResult.HasConflict)
        {
            await _workspacePreparer.AbortMergeAsync(workspace, task.Id, cancellationToken);
            var conflictMsg = $"Merge conflict during Pre-Implementation Baseline synchronization with '{task.BaseBranch}' in {baselineSyncResult.ConflictedFiles.Count} files.";

            task.Status = TaskOverallStatus.Failed;
            await _taskRepository.UpdateWithHistoryAsync(
                task,
                dependsOn: null,
                changedBy: AuditActors.TaskOrchestrator,
                reason: conflictMsg,
                cancellationToken: cancellationToken);
            await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, TaskOverallStatus.Failed, cancellationToken);
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
                    setDeterministicConvFinalized(true);
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

    private async Task CloseActiveConversationsAsync(int taskId, StageExecutionStatus status, string summary, CancellationToken cancellationToken)
    {
        var convs = await _taskRepository.GetConversationsAsync(taskId, cancellationToken);
        var activeConvs = convs.Where(c =>
            c.Status == StageExecutionStatus.Running ||
            (status != StageExecutionStatus.Interrupted && (c.Status == StageExecutionStatus.WaitingForHuman || c.Status == StageExecutionStatus.Interrupted))).ToList();
        foreach (var activeConv in activeConvs)
        {
            await _taskRepository.CompleteConversationAsync(
                activeConv.Id,
                status,
                summary: summary,
                cancellationToken: cancellationToken);
        }
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
        await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, task.Status, cancellationToken);
        await _workspacePreparer.SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

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
        await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, task.Status, cancellationToken);
        await _workspacePreparer.SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Warning,
            Message = $"Quality gate '{currentStage.StageId}' failed: {result.ErrorMessage}. Looping back cross-cohort to '{targetStage.StageId}' (attempt {retries}/{effectiveMaxRetries}).",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);

        task.LastHangfireJobId = _mergeAndCascadeCoordinator.EnqueueStage(task.Id, targetStage.QueueTier, task.Priority);
        await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
    }

    private readonly record struct StageRemediationOutcome(bool ShouldResume, string FailureMessage);

    private async Task<StageRemediationOutcome> RemediateStageAsync(
        ForgeTask task,
        PipelineContext pipelineContext,
        IWorkflowStage stage,
        string remediationRetryKey,
        int remediationMaxRetries,
        string remediationPrompt,
        string interruptedSummary,
        string noticeAction,
        string failureMessage,
        string exhaustedBudgetName,
        CancellationToken cancellationToken)
    {
        var remediationAttempt = pipelineContext.IncrementRetry(remediationRetryKey);

        var stageConvs = await _taskRepository.GetConversationsAsync(task.Id, cancellationToken);
        var latestConv = stageConvs?
            .Where(c => string.Equals(c.StageId, stage.StageId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.AttemptIndex)
            .ThenByDescending(c => c.StartedAt)
            .ThenByDescending(c => c.Id)
            .FirstOrDefault();

        if (remediationAttempt > remediationMaxRetries)
        {
            _logger.LogWarning(
                "Task #{TaskId} stage '{StageId}' exhausted {Budget} ({MaxRetries}).",
                task.Id,
                stage.StageId,
                exhaustedBudgetName,
                remediationMaxRetries);

            pipelineContext.DataBag.Remove(PipelineDataBagKeys.ApprovedPlan);
            pipelineContext.DataBag.Remove(PipelineDataBagKeys.StageSummary(stage.StageId));

            if (latestConv != null)
            {
                await _taskRepository.CompleteConversationAsync(
                    latestConv.Id,
                    StageExecutionStatus.Failed,
                    summary: failureMessage,
                    cancellationToken: cancellationToken);
            }

            return new StageRemediationOutcome(false, failureMessage);
        }

        if (latestConv != null)
        {
            await _taskRepository.AddConversationTurnAsync(
                latestConv.Id,
                StageTurnRole.User,
                content: remediationPrompt,
                cancellationToken: cancellationToken);

            await _taskRepository.CompleteConversationAsync(
                latestConv.Id,
                StageExecutionStatus.Interrupted,
                summary: $"{interruptedSummary} (attempt {remediationAttempt}/{remediationMaxRetries}).",
                cancellationToken: cancellationToken);

            var convWithTurns = await _taskRepository.GetConversationWithTurnsAsync(latestConv.Id, cancellationToken);
            if (convWithTurns?.Turns != null)
            {
                pipelineContext.DataBag.Set(
                    PipelineDataBagKeys.RecoveredSerializedMemory,
                    ConversationHistorySerializer.Serialize(convWithTurns.Turns));
            }
        }
        else
        {
            pipelineContext.DataBag.Set(
                PipelineDataBagKeys.RecoveredSerializedMemory,
                ConversationHistorySerializer.Serialize(new[]
                {
                    new ConversationTurn
                    {
                        TurnIndex = 1,
                        Role = StageTurnRole.User,
                        Content = remediationPrompt
                    }
                }));
        }

        pipelineContext.DataBag.Remove(PipelineDataBagKeys.ApprovedPlan);
        pipelineContext.DataBag.Remove(PipelineDataBagKeys.StageSummary(stage.StageId));

        task.InterruptedStageId = stage.StageId;

        pipelineContext.SyncDataBagToTask();
        await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);

        try
        {
            await _streamer.BroadcastAsync(new AgentActivityEvent(
                Guid.NewGuid().ToString("N"),
                task.Id,
                stage.StageId,
                AgentActivityKind.Notice,
                $"Stage '{stage.StageId}' {noticeAction} (attempt {remediationAttempt}/{remediationMaxRetries}).",
                DateTime.UtcNow,
                new Dictionary<string, object?>
                {
                    [AgentActivityMetadataKeys.StageKind] = stage.Kind.ToString(),
                    [AgentActivityMetadataKeys.Attempt] = remediationAttempt,
                    [AgentActivityMetadataKeys.MaxRetries] = remediationMaxRetries
                }), cancellationToken);
        }
        catch (Exception sex)
        {
            _logger.LogDebug(sex, "Failed broadcasting stage notice event for task #{TaskId}", task.Id);
        }

        return new StageRemediationOutcome(true, string.Empty);
    }

    private async Task HandleStageFatalFailureAsync(
        ForgeTask task,
        Workspace workspace,
        PipelineContext pipelineContext,
        IWorkflowStage stage,
        StageExecutionResult result,
        CancellationToken cancellationToken,
        string? retryKey = null)
    {
        var effectiveMaxRetries = pipelineContext.DataBag.Get<int?>(PipelineDataBagKeys.MaxRetries) ?? (stage.MaxRetries > 0 ? stage.MaxRetries : WorkflowDefaults.DefaultMaxStageRetries);
        task.Status = TaskOverallStatus.Failed;
        pipelineContext.SyncDataBagToTask();

        var stageRetries = pipelineContext.StageRetries.GetValueOrDefault(retryKey ?? stage.StageId, 0);
        var attempt = Math.Max(1, stageRetries);
        var failureReason = stageRetries >= effectiveMaxRetries
            ? $"exceeded max retry limit ({effectiveMaxRetries})"
            : $"unrecoverable stage failure on attempt {attempt}";
        var reason = $"Pipeline failed at stage '{stage.StageId}'. {char.ToUpperInvariant(failureReason[0])}{failureReason[1..]}. Error: {result.ErrorMessage ?? "Unknown failure"}";

        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.TaskOrchestrator,
            reason: reason,
            cancellationToken: cancellationToken);
        await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, TaskOverallStatus.Failed, cancellationToken);
        await TaskBadgeCounter.SynchronizeBadgeCountsAsync(_taskRepository, _telemetryService, cancellationToken);
        await _workspacePreparer.SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);
        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Error,
            Message = $"Stage '{stage.StageId}' {failureReason}: {result.ErrorMessage}",
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
