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

        // 1. DAG Readiness Check
        var isReady = await _dependencyEngine.IsReadyForExecutionAsync(taskId, cancellationToken);
        if (!isReady)
        {
            _logger.LogInformation("ForgeTask #{TaskId} prerequisites not yet completed or not open; waiting.", taskId);
            await _taskRepository.AddExecutionLogAsync(new ExecutionLog
            {
                TaskId = task.Id,
                Level = LogLevel.Information,
                Message = $"ForgeTask #{task.Id} is waiting on prerequisite tasks before execution.",
                Timestamp = DateTime.UtcNow
            }, cancellationToken);
            return;
        }

        // 2. ForgeTask Lock Acquisition (Enables concurrent execution of different tasks on the same workspace)
        using var lockReleaser = await _workspaceLockProvider.AcquireTaskLockAsync(task.WorkspaceId, task.Id, cancellationToken);

        var workspace = task.Workspace ?? await _workspaceRepository.GetByIdAsync(task.WorkspaceId, cancellationToken);
        if (workspace == null)
        {
            var err = $"Workspace #{task.WorkspaceId} not found for task #{taskId}.";
            _logger.LogError(err);
            task.Status = TaskOverallStatus.Failed;
            await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed checking out feature branch for task #{TaskId}", taskId);
            task.Status = TaskOverallStatus.Failed;
            await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
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
            await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
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
                (p.StageDirectives != null && p.StageDirectives.ContainsKey(stage.StageId)))
                ?? workspace.AgentProfiles?.FirstOrDefault();
        }

        if (stage.Kind == WorkflowStageKind.Agentic && !HasCustomHandler(stage))
        {
            if (profile == null)
            {
                var notFoundEx = new AgentProfileNotFoundException(stage.StageId, workspace.Id);
                _logger.LogError(notFoundEx, "Agent profile not found for stage '{StageId}' in workspace #{WorkspaceId}", stage.StageId, workspace.Id);
                task.Status = TaskOverallStatus.Failed;
                await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
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

        var targetSyncPath = (!string.IsNullOrWhiteSpace(taskWorkingPath) && Directory.Exists(taskWorkingPath))
            ? taskWorkingPath
            : workspace.RepositoryPath;

        if (!string.IsNullOrWhiteSpace(targetSyncPath) && Directory.Exists(targetSyncPath))
        {
            try
            {
                var syncedFile = await _markdownSyncService.SyncTaskToDiskAsync(task, targetSyncPath, cancellationToken: cancellationToken);
                var relPath = Path.GetRelativePath(targetSyncPath, syncedFile).Replace('\\', '/');
                pipelineContext.DataBag.Set(PipelineDataBagKeys.TaskMarkdownPath, relPath);

                if (!string.IsNullOrWhiteSpace(workspace.RepositoryPath) &&
                    Directory.Exists(workspace.RepositoryPath) &&
                    !string.Equals(workspace.RepositoryPath, targetSyncPath, StringComparison.OrdinalIgnoreCase))
                {
                    await _markdownSyncService.SyncTaskToDiskAsync(task, workspace.RepositoryPath, cancellationToken: cancellationToken);
                }
            }
            catch (Exception mex)
            {
                _logger.LogWarning(mex, "Failed pre-stage mirroring of task #{TaskId} to docs/tasks/*.md", task.Id);
            }
        }

        var startedAt = DateTime.UtcNow;
        StageExecutionResult result;

        try
        {
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
                        var resumeMemory = (lastCheckpoint != null && string.Equals(lastCheckpoint.StageId, stage.StageId, StringComparison.OrdinalIgnoreCase))
                            ? lastCheckpoint.SerializedMemory
                            : null;

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
                            BootstrapPrompt = stage.BuildBootstrapPrompt(task, pipelineContext, stageDirective),
                            OptimizationFlags = profile.OptimizationFlags ?? new LocalOptimizationFlags(),
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
            _logger.LogInformation("Stage '{StageId}' triggered HumanInterruptionException: {Prompt}", stage.StageId, hex.Question.Prompt);
            result = StageExecutionResult.WaitingForHuman(hex.Question);
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
            : (result.IsSuccess ? StageExecutionStatus.Completed : StageExecutionStatus.Failed);

        await _taskRepository.AddExecutionRecordAsync(record, cancellationToken);

        // Record Conversation for deterministic stages
        if (stage.Kind == WorkflowStageKind.Deterministic)
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

                // Turn 2: Tool execution
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
                    BaseBranch = workspace.DefaultBaseBranch ?? "main",
                    Priority = 1,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                await _taskRepository.CreateAsync(suggestedTask, cancellationToken: cancellationToken);
                _logger.LogInformation("Triage/Discovery created suggested task #{NewTaskId}: {Title}", suggestedTask.Id, suggestedTask.Title);
                await SyncMarkdownTaskSafeAsync(workspace, suggestedTask, cancellationToken);
            }
            pipelineContext.DataBag.Remove(PipelineDataBagKeys.PendingProposals);
        }

        // Read back any disk modifications to the task specification document
        await ReadBackTaskMarkdownFromDiskAsync(stage, workspace, task, pipelineContext, targetSyncPath, cancellationToken);

        // 5. Result Evaluation
        if (result.NeedsClarification)
        {
            // Case A: WaitingForHuman - clean job success, freeing single GPU worker
            var question = result.ClarificationQuestion ?? new Question
            {
                Prompt = "Human input required to proceed.",
                Status = QuestionStatus.Pending
            };
            question.TaskId = task.Id;
            question.StageId = stage.StageId;
            question.Status = QuestionStatus.Pending;
            question.AgentProfileId = profile?.Id;
            question.AskedAt = DateTime.UtcNow;

            task.Questions.Add(question);

            string commitHash = string.Empty;
            try
            {
                commitHash = await _gitWorkspaceManager.CreateWipCheckpointAsync(
                    workspace,
                    task.Id,
                    stage.StageId,
                    $"WIP checkpoint before human clarification: {question.Prompt}",
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
                Message = $"ForgeTask #{task.Id} paused waiting for human input to: {question.Prompt}",
                Timestamp = DateTime.UtcNow
            }, cancellationToken);

            // Clean return: Hangfire marks job as Succeeded, GPU worker freed
            return;
        }

        if (result.IsSuccess)
        {
            // Case B: Stage Completed
            pipelineContext.ResetRetries();

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

            // Status stamps per stage
            if (string.Equals(stage.StageId, StandardStageIds.TaskReview, StringComparison.OrdinalIgnoreCase))
            {
                task.Status = TaskOverallStatus.Reviewed;
            }
            else if (string.Equals(stage.StageId, StandardStageIds.Implementation, StringComparison.OrdinalIgnoreCase))
            {
                task.Status = TaskOverallStatus.Implemented;
            }
            else if (string.Equals(stage.StageId, StandardStageIds.PostReviewVerification, StringComparison.OrdinalIgnoreCase))
            {
                task.Status = TaskOverallStatus.Ready;
            }

            var nextStageId = result.TargetRoutingStageId;
            IWorkflowStage? nextStage = !string.IsNullOrWhiteSpace(nextStageId)
                ? pipeline.GetStage(nextStageId)
                : pipeline.GetNextStage(stage.StageId);

            if (nextStage != null)
            {
                task.CurrentStageId = nextStage.StageId;
                pipelineContext.SyncDataBagToTask();
                await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
                await _dependencyStore.UpdateTaskStatusAsync(task.Id, task.Status, cancellationToken);
                await SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

                // Enqueue next stage to standard inference queue
                _jobQueue.EnqueueInference(task.Id);
            }
            else
            {
                // Final stage reached: Transition to Ready and generate review diff
                task.Status = TaskOverallStatus.Ready;

                string diff = string.Empty;
                try
                {
                    diff = await _gitWorkspaceManager.GenerateDiffAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed generating review diff for task #{TaskId}", task.Id);
                }

                pipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewDiff, diff);
                pipelineContext.SyncDataBagToTask();
                await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
                await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Ready, cancellationToken);

                if (task.MergeMode == MergeMode.Automatic)
                {
                    _logger.LogInformation("ForgeTask #{TaskId} reached Ready with MergeMode.Automatic; executing auto-merge.", task.Id);
                    await CompleteTaskAndCascadeInternalAsync(task, workspace, $"Auto-merge task #{task.Id}: {task.Title}", cancellationToken);
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
            var retries = pipelineContext.IncrementRetry(stage.StageId);
            var effectiveMaxRetries = pipelineContext.DataBag.Get<int?>(PipelineDataBagKeys.MaxRetries) ?? WorkflowDefaults.DefaultMaxStageRetries;
            if (retries <= effectiveMaxRetries)
            {
                _logger.LogWarning("Stage '{StageId}' failed, routing backward to '{TargetStage}' (Retry {Retry}/{Max}): {Reason}",
                    stage.StageId, targetLoopBackId, retries, effectiveMaxRetries, result.ErrorMessage);

                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    var errors = pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.CompilerErrors) ?? new List<string>();
                    errors.Add($"[{stage.StageId} attempt {retries}] {result.ErrorMessage}");
                    pipelineContext.DataBag.Set(PipelineDataBagKeys.CompilerErrors, errors);
                }

                task.CurrentStageId = targetLoopBackId;
                pipelineContext.SyncDataBagToTask();
                await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
                await _dependencyStore.UpdateTaskStatusAsync(task.Id, task.Status, cancellationToken);
                await SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

                await _taskRepository.AddExecutionLogAsync(new ExecutionLog
                {
                    TaskId = task.Id,
                    Level = LogLevel.Warning,
                    Message = $"Quality gate '{stage.StageId}' failed: {result.ErrorMessage}. Looping back to '{targetLoopBackId}' (attempt {retries}/{effectiveMaxRetries}).",
                    Timestamp = DateTime.UtcNow
                }, cancellationToken);

                _jobQueue.EnqueueUrgent(task.Id);
                return;
            }
        }

        // Case C: Permanent Failure
        task.Status = TaskOverallStatus.Failed;
        pipelineContext.SyncDataBagToTask();
        await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
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

    public async Task SubmitHumanAnswerAsync(int questionId, string answer, CancellationToken cancellationToken = default)
    {
        var question = await _taskRepository.GetQuestionByIdAsync(questionId, cancellationToken);
        if (question == null)
        {
            throw new KeyNotFoundException($"Question #{questionId} not found.");
        }

        question.Answer = answer;
        question.Status = QuestionStatus.Answered;
        question.AnsweredAt = DateTime.UtcNow;

        var task = question.ForgeTask ?? await _taskRepository.GetByIdAsync(question.TaskId, cancellationToken);
        if (task == null)
        {
            throw new KeyNotFoundException($"Associated ForgeTask #{question.TaskId} not found.");
        }

        // Universal Interruption: answering never mutates task.Status!
        task.IsWaitingForHuman = false;

        var bag = PipelineDataBag.FromJson(task.PipelineDataJson);
        bag.Set(PipelineDataBagKeys.LastHumanAnswer, answer);
        bag.Set(PipelineDataBagKeys.QuestionAnswer(question.Id), answer);
        task.PipelineDataJson = bag.ToJson();

        await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
        await _dependencyStore.UpdateTaskStatusAsync(task.Id, task.Status, cancellationToken);
        await _dependencyStore.SetWaitingForHumanAsync(task.Id, false, cancellationToken);

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Information,
            Message = $"Human answer submitted for question #{questionId}: {answer}",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);

        // Preemptive top-priority resumption on llm-urgent
        _jobQueue.EnqueueUrgent(task.Id);
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

        await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
        await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Reviewed, cancellationToken);

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Information,
            Message = $"Reviewer requested changes for task #{task.Id} (routing back to '{targetStageId}'): {revisionNotes}",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);

        // Enqueue to llm-urgent for rapid rework
        _jobQueue.EnqueueUrgent(task.Id);
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

            await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
            await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Open, cancellationToken);

            var isReady = await _dependencyEngine.IsReadyForExecutionAsync(task.Id, cancellationToken);
            if (isReady)
            {
                _jobQueue.EnqueueInference(task.Id);
            }
        }
        else if (targetStatus == TaskOverallStatus.Rejected)
        {
            task.Status = TaskOverallStatus.Rejected;
            task.UpdatedAt = DateTime.UtcNow;

            await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
            await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Rejected, cancellationToken);
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

    public async Task CompleteTaskAndCascadeAsync(int taskId, string finalCommitMessage, CancellationToken cancellationToken = default)
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

        await CompleteTaskAndCascadeInternalAsync(task, workspace, finalCommitMessage, cancellationToken);
    }

    private async Task CompleteTaskAndCascadeInternalAsync(
        ForgeTask task,
        Workspace workspace,
        string finalCommitMessage,
        CancellationToken cancellationToken)
    {
        // 1. Squash & Merge feature branch into base branch
        var squashSha = await _gitWorkspaceManager.SquashAndMergeAsync(
            workspace,
            task.Id,
            task.BaseBranch,
            finalCommitMessage,
            cancellationToken);

        task.SquashCommitSha = squashSha;

        // If ext4 mirror is active, push the merged base branch back to host-origin so developer host repository is updated!
        if (_gitMirrorSyncService != null && !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            try
            {
                await _gitMirrorSyncService.PushBaseBranchToHostAsync(
                    workspace.Ext4MirrorPath,
                    task.BaseBranch,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed pushing merged base branch '{BaseBranch}' to host-origin for task #{TaskId}",
                    task.BaseBranch, task.Id);
            }
        }

        // 2. Mark task as completed (Closed)
        task.Status = TaskOverallStatus.Closed;
        task.CurrentStageId = string.Empty;
        await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
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
        foreach (var unblockedId in unblocked)
        {
            _logger.LogInformation("ForgeTask #{PrereqId} completed; unblocked dependent task #{UnblockedId}.", task.Id, unblockedId);
            _jobQueue.EnqueueInference(unblockedId);
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
            Status = runResult.Status == AgentRunStatus.Failed ? StageExecutionStatus.Failed : StageExecutionStatus.Completed
        };

        if (runResult.Status == AgentRunStatus.WaitingForHuman)
        {
            var question = new Question
            {
                StageId = stage.StageId,
                Prompt = runResult.PendingQuestion ?? "Human clarification required.",
                Rationale = runResult.Summary ?? string.Empty,
                QuestionType = QuestionType.FreeText,
                Status = QuestionStatus.Pending
            };
            return StageExecutionResult.WaitingForHuman(question, record);
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

    private async Task ReadBackTaskMarkdownFromDiskAsync(
        IWorkflowStage stage,
        Workspace workspace,
        ForgeTask task,
        PipelineContext pipelineContext,
        string targetSyncPath,
        CancellationToken cancellationToken)
    {
        if (_markdownSyncService == null || string.IsNullOrWhiteSpace(targetSyncPath) || !Directory.Exists(targetSyncPath))
        {
            return;
        }

        try
        {
            var relPath = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath);
            string? targetFile = null;

            if (!string.IsNullOrWhiteSpace(relPath))
            {
                var candidate = Path.Combine(targetSyncPath, relPath);
                if (File.Exists(candidate))
                {
                    targetFile = candidate;
                }
            }

            if (targetFile == null)
            {
                var tasksDir = Path.Combine(targetSyncPath, "docs", "tasks");
                if (Directory.Exists(tasksDir))
                {
                    var files = Directory.GetFiles(tasksDir, "*.md");
                    foreach (var file in files)
                    {
                        try
                        {
                            var content = await File.ReadAllTextAsync(file, cancellationToken);
                            var (fm, _) = _markdownSyncService.ParseMarkdown(content);
                            if (fm.Id == task.Id)
                            {
                                targetFile = file;
                                break;
                            }
                        }
                        catch
                        {
                            // Ignore unreadable files during scan
                        }
                    }
                }
            }

            if (targetFile == null || !File.Exists(targetFile))
            {
                return;
            }

            // Sync modified file from disk into repository and database
            await _markdownSyncService.ProcessFileChangedAsync(targetFile, workspace.Id, expectedTaskId: task.Id, cancellationToken: cancellationToken);

            // Directly read and apply disk description and title to ensure in-memory task reflects agent edits
            if (File.Exists(targetFile))
            {
                try
                {
                    var fileContent = await File.ReadAllTextAsync(targetFile, cancellationToken);
                    var (fm, body) = _markdownSyncService.ParseMarkdown(fileContent);
                    var diskDesc = Services.TaskMarkdownSyncService.ExtractDescriptionFromBody(body);
                    if (!string.IsNullOrWhiteSpace(diskDesc))
                    {
                        task.Description = diskDesc;
                    }
                    if (!string.IsNullOrWhiteSpace(fm.Title) &&
                        !string.Equals(fm.Title.Trim(), "Task", StringComparison.OrdinalIgnoreCase))
                    {
                        task.Title = fm.Title.Trim();
                    }
                }
                catch (Exception pex)
                {
                    _logger.LogDebug(pex, "Failed direct parse of task markdown file {Path}", targetFile);
                }
            }

            // Refresh task from repository to capture agent's updated description, title, and dependencies
            var refreshedTask = await _taskRepository.GetByIdAsync(task.Id, cancellationToken);
            if (refreshedTask != null)
            {
                if (!string.IsNullOrWhiteSpace(refreshedTask.Title) && !string.Equals(refreshedTask.Title.Trim(), "Task", StringComparison.OrdinalIgnoreCase)) task.Title = refreshedTask.Title;
                if (!string.IsNullOrWhiteSpace(refreshedTask.Description)) task.Description = refreshedTask.Description;
                task.Priority = refreshedTask.Priority;
                task.BaseBranch = refreshedTask.BaseBranch;
                task.FeatureBranch = refreshedTask.FeatureBranch;
                task.UpdatedAt = refreshedTask.UpdatedAt;
            }

            // If this is the task-review stage, prioritize the disk description as the approved technical plan
            if (string.Equals(stage.StageId, StandardStageIds.TaskReview, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(task.Description))
            {
                pipelineContext.DataBag.Set(PipelineDataBagKeys.ApprovedPlan, task.Description);
                if (pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.StageSummary(StandardStageIds.TaskReview)) == null)
                {
                    pipelineContext.DataBag.Set(PipelineDataBagKeys.StageSummary(StandardStageIds.TaskReview), task.Description);
                }
            }

            // Mirror the exact file to workspace RepositoryPath if different
            if (!string.IsNullOrWhiteSpace(workspace.RepositoryPath) &&
                Directory.Exists(workspace.RepositoryPath) &&
                !string.Equals(workspace.RepositoryPath, targetSyncPath, StringComparison.OrdinalIgnoreCase))
            {
                var destRelPath = !string.IsNullOrWhiteSpace(relPath) ? relPath : Path.GetRelativePath(targetSyncPath, targetFile);
                var destFile = Path.Combine(workspace.RepositoryPath, destRelPath);
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrWhiteSpace(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                var diskContent = await File.ReadAllTextAsync(targetFile, cancellationToken);
                _markdownSyncService.SuppressContentHash(diskContent);
                await File.WriteAllTextAsync(destFile, diskContent, Encoding.UTF8, cancellationToken);
            }

            _logger.LogInformation("Read back modified task specification for task #{TaskId} from disk ({Path})", task.Id, targetFile);
            await _taskRepository.AddExecutionLogAsync(new ExecutionLog
            {
                TaskId = task.Id,
                Level = LogLevel.Information,
                Message = $"Synchronized agent modifications to task specification from {Path.GetFileName(targetFile)} into task #{task.Id}.",
                Timestamp = DateTime.UtcNow
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed reading back modified task specification for task #{TaskId} from disk", task.Id);
        }
    }

    private async Task SyncMarkdownTaskSafeAsync(Workspace workspace, ForgeTask task, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(workspace.RepositoryPath))
        {
            try
            {
                await _markdownSyncService.SyncTaskToDiskAsync(task, workspace.RepositoryPath, cancellationToken: cancellationToken);
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
