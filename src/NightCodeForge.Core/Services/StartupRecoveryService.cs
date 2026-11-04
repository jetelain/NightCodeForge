using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Implements startup crash recovery: delegates Git lock/mirror/worktree recovery to <see cref="IWorkspaceGitRecoveryService"/>,
/// reaps orphaned task containers, and re-enqueues interrupted active tasks to their respective stage queue tiers.
/// </summary>
public class StartupRecoveryService : IStartupRecoveryService
{
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly ITaskRepository _taskRepository;
    private readonly IWorkspaceGitRecoveryService _gitRecoveryService;
    private readonly IWorkflowPipelineProvider _pipelineProvider;
    private readonly ITaskJobQueue _jobQueue;
    private readonly IExecutionEnvironmentProvider _environmentProvider;
    private readonly IOperationsControlService _operationsControlService;
    private readonly ILogger<StartupRecoveryService> _logger;

    private const string RecoveryNoticeContent = "[System Notice: Host restarted unexpectedly; workspace restored; resuming from previous conversation turn]";
    private const string InterruptedAgenticConversationSummary = "Interrupted by host restart; session will resume in this conversation.";
    private const string InterruptedDeterministicConversationSummary = "Process terminated unexpectedly mid-execution. Stage reset for clean re-run.";
    private const string SupersededRunningConversationSummary = "Process terminated unexpectedly mid-execution. Superseded by startup recovery.";

    public StartupRecoveryService(
        IWorkspaceRepository workspaceRepository,
        ITaskRepository taskRepository,
        IWorkspaceGitRecoveryService gitRecoveryService,
        IWorkflowPipelineProvider pipelineProvider,
        ITaskJobQueue jobQueue,
        IExecutionEnvironmentProvider environmentProvider,
        IOperationsControlService operationsControlService,
        ILogger<StartupRecoveryService> logger)
    {
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _gitRecoveryService = gitRecoveryService ?? throw new ArgumentNullException(nameof(gitRecoveryService));
        _pipelineProvider = pipelineProvider ?? throw new ArgumentNullException(nameof(pipelineProvider));
        _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
        _environmentProvider = environmentProvider ?? throw new ArgumentNullException(nameof(environmentProvider));
        _operationsControlService = operationsControlService ?? throw new ArgumentNullException(nameof(operationsControlService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ReconcileWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Beginning startup recovery and workspace reconciliation...");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _gitRecoveryService.VerifyWslAvailability();
        }

        var workspaces = await _workspaceRepository.GetAllAsync(cancellationToken);

        foreach (var workspace in workspaces)
        {
            await _gitRecoveryService.RecoverWorkspaceAsync(workspace, cancellationToken);
        }

        // Reap sandbox containers of closed / rejected / deleted tasks
        foreach (var workspace in workspaces.Where(w => w.Id > 0))
        {
            try
            {
                await TaskContainerReaper.ReapAsync(_environmentProvider, _taskRepository, workspace, _logger, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to reap orphaned task containers for workspace #{WorkspaceId}", workspace.Id);
            }
        }

        // 4. Status normalization & crash recovery for active tasks left interrupted by restart
        var activeStatuses = new[] { TaskOverallStatus.Open, TaskOverallStatus.Implemented, TaskOverallStatus.Reviewed };
        var candidateTasks = new List<ForgeTask>();
        var seenTaskIds = new HashSet<int>();

        foreach (var status in activeStatuses)
        {
            var tasks = await _taskRepository.GetByStatusAsync(status, cancellationToken);
            foreach (var t in tasks.Where(task => !task.IsWaitingForHuman && seenTaskIds.Add(task.Id)))
            {
                candidateTasks.Add(t);
            }
        }

        var resolvedCandidates = new List<(ForgeTask Task, IWorkflowPipeline Pipeline, IWorkflowStage Stage, int StageIndex)>();

        foreach (var task in candidateTasks)
        {
            var pipeline = await _pipelineProvider.GetPipelineAsync(task, cancellationToken);

            // Normalize legacy status Implemented / Reviewed
            if (task.Status is TaskOverallStatus.Implemented or TaskOverallStatus.Reviewed)
            {
                var targetStageId = task.Status == TaskOverallStatus.Implemented
                    ? StandardStageIds.PostImplementationGates
                    : StandardStageIds.PostReviewVerification;

                var targetStage = pipeline.GetStage(targetStageId)
                    ?? throw new WorkflowValidationException($"Cannot migrate legacy task #{task.Id} with status '{task.Status}': stage '{targetStageId}' is not defined in workflow.");

                task.CurrentStageId = targetStage.StageId;
                task.Status = TaskOverallStatus.Open;
                task.HasExecutionRun = true;

                var logMsg = $"Legacy status migration: Normalized task #{task.Id} to Open at stage '{task.CurrentStageId}'.";
                _logger.LogInformation(logMsg);

                await _taskRepository.UpdateWithHistoryAsync(
                    task,
                    dependsOn: null,
                    changedBy: AuditActors.System,
                    reason: logMsg,
                    cancellationToken: cancellationToken);
            }

            IWorkflowStage stage;
            if (!string.IsNullOrWhiteSpace(task.CurrentStageId))
            {
                stage = pipeline.GetStage(task.CurrentStageId)
                    ?? throw new WorkflowValidationException($"No workflow stage '{task.CurrentStageId}' found in workflow for task #{task.Id}.");
            }
            else
            {
                stage = pipeline.InitialStage
                    ?? throw new WorkflowValidationException($"No initial workflow stage resolved for task #{task.Id}.");
                task.CurrentStageId = stage.StageId;
            }

            var stageIndex = pipeline.Stages.ToList().FindIndex(s => string.Equals(s.StageId, stage.StageId, StringComparison.OrdinalIgnoreCase));
            resolvedCandidates.Add((task, pipeline, stage, stageIndex));
        }

        // Order candidates so tasks already furthest along in the pipeline are enqueued first:
        // 1. Priority ascending (P1 Urgent first, P2, P3...)
        // 2. QueueTier descending (CodeReview = 3 first, Implementation = 2, TaskReview = 1)
        // 3. StageIndex descending (later stages first within same queue tier, ensuring a task is completed before earlier ones)
        // 4. UpdatedAt ascending (earlier modified first)
        var orderedCandidates = resolvedCandidates
            .OrderBy(c => c.Task.Priority)
            .ThenByDescending(c => c.Stage.QueueTier)
            .ThenByDescending(c => c.StageIndex)
            .ThenBy(c => c.Task.UpdatedAt)
            .ToList();

        var reconciledCount = 0;
        foreach (var (task, pipeline, stage, _) in orderedCandidates)
        {
            var bag = PipelineDataBag.FromJson(task.PipelineDataJson);

            if (stage.Kind == WorkflowStageKind.Agentic)
            {
                // Agentic Stage: Rehydrate memory from active database conversation turns
                var convs = await _taskRepository.GetConversationsAsync(task.Id, cancellationToken);
                var activeConv = convs
                    .Where(c => string.Equals(c.StageId, stage.StageId, StringComparison.OrdinalIgnoreCase) &&
                                (c.Status == StageExecutionStatus.Running ||
                                 c.Status == StageExecutionStatus.WaitingForHuman ||
                                 c.Status == StageExecutionStatus.Interrupted))
                    .OrderByDescending(c => c.AttemptIndex)
                    .ThenByDescending(c => c.StartedAt)
                    .ThenByDescending(c => c.Id)
                    .FirstOrDefault();

                // Supersede any older running/waiting/interrupted conversations left dangling by previous crashes
                foreach (var other in convs.Where(c =>
                    (c.Status == StageExecutionStatus.Running ||
                     c.Status == StageExecutionStatus.WaitingForHuman ||
                     c.Status == StageExecutionStatus.Interrupted) &&
                    (activeConv == null || c.Id != activeConv.Id)))
                {
                    await _taskRepository.CompleteConversationAsync(
                        other.Id,
                        StageExecutionStatus.Superseded,
                        summary: SupersededRunningConversationSummary,
                        cancellationToken: cancellationToken);
                }

                if (activeConv != null)
                {
                    var convWithTurns = await _taskRepository.GetConversationWithTurnsAsync(activeConv.Id, cancellationToken)
                        ?? throw new InvalidOperationException($"Conversation #{activeConv.Id} of task #{task.Id} disappeared during startup recovery.");

                    var wasAlreadyInterrupted = activeConv.Status == StageExecutionStatus.Interrupted;
                    var pendingHumanAnswer = bag.Get<string>(PipelineDataBagKeys.LastHumanAnswer);
                    var isWaitingWithAnswer = activeConv.Status == StageExecutionStatus.WaitingForHuman && !string.IsNullOrWhiteSpace(pendingHumanAnswer);

                    if (convWithTurns.Turns.Count > 0)
                    {
                        var orderedExisting = convWithTurns.Turns.OrderBy(t => t.TurnIndex).ToList();
                        IReadOnlyList<ConversationTurn> allTurns;

                        if (isWaitingWithAnswer)
                        {
                            allTurns = orderedExisting;
                        }
                        else if (!wasAlreadyInterrupted)
                        {
                            var lastTurn = orderedExisting.LastOrDefault();
                            if (lastTurn == null || lastTurn.Role != StageTurnRole.System || !string.Equals(lastTurn.Content, RecoveryNoticeContent, StringComparison.Ordinal))
                            {
                                var noticeTurn = await _taskRepository.AddConversationTurnAsync(
                                    activeConv.Id,
                                    StageTurnRole.System,
                                    content: RecoveryNoticeContent,
                                    cancellationToken: cancellationToken);

                                allTurns = orderedExisting
                                    .Where(t => t.Id != noticeTurn.Id)
                                    .Append(noticeTurn)
                                    .OrderBy(t => t.TurnIndex)
                                    .ToList();
                            }
                            else
                            {
                                allTurns = orderedExisting;
                            }

                            // Transition the crashed conversation to Interrupted so CrucibleAgentHarness resumes it in-place.
                            await _taskRepository.CompleteConversationAsync(
                                activeConv.Id,
                                StageExecutionStatus.Interrupted,
                                summary: InterruptedAgenticConversationSummary,
                                cancellationToken: cancellationToken);
                        }
                        else
                        {
                            allTurns = orderedExisting;
                        }

                        var reconstructedHistory = ConversationHistorySerializer.Serialize(allTurns);
                        if (isWaitingWithAnswer)
                        {
                            reconstructedHistory = ConversationHistorySerializer.CompletePendingAskToolCall(reconstructedHistory, pendingHumanAnswer!);
                        }
                        bag.Set(PipelineDataBagKeys.RecoveredSerializedMemory, reconstructedHistory);
                        task.InterruptedStageId = stage.StageId;

                        _logger.LogWarning("Rehydrated agentic session memory from {Count} turns for orphaned task #{TaskId}.", allTurns.Count, task.Id);
                    }
                    else if (wasAlreadyInterrupted || isWaitingWithAnswer)
                    {
                        bag.Remove(PipelineDataBagKeys.RecoveredSerializedMemory);
                        task.InterruptedStageId = stage.StageId;
                    }
                    else
                    {
                        // Interrupted by crash before first turn was recorded; close conversation and resume cleanly from scratch/checkpoint
                        await _taskRepository.CompleteConversationAsync(
                            activeConv.Id,
                            StageExecutionStatus.Failed,
                            summary: InterruptedAgenticConversationSummary,
                            cancellationToken: cancellationToken);

                        bag.Remove(PipelineDataBagKeys.RecoveredSerializedMemory);
                        _logger.LogWarning("Running conversation #{ConvId} for task #{TaskId} had 0 turns; resuming cleanly from last checkpoint or bootstrap prompt.", activeConv.Id, task.Id);
                    }
                }
                else
                {
                    // No interrupted conversation was persisted: nothing to rehydrate. Any stale in-flight memory is
                    // dropped so the orchestrator resumes from the last stage checkpoint (or from scratch).
                    bag.Remove(PipelineDataBagKeys.RecoveredSerializedMemory);
                    _logger.LogWarning("No running conversation found for orphaned agentic task #{TaskId} at stage '{StageId}'; resuming from last checkpoint.", task.Id, stage.StageId);
                }
            }
            else
            {
                // Deterministic Stage: Reset all partial metrics and stage command/output keys
                var deterministicKeys = new[]
                {
                    PipelineDataBagKeys.TestsCurrentTotal,
                    PipelineDataBagKeys.TestsCurrentPassed,
                    PipelineDataBagKeys.TestsCurrentFailed,
                    PipelineDataBagKeys.TestsCurrentDurationMs,
                    PipelineDataBagKeys.CoverageCurrentLineRate,
                    PipelineDataBagKeys.WarningsCurrentTotal,
                    PipelineDataBagKeys.WarningsNew,
                    PipelineDataBagKeys.FailedTests,
                    PipelineDataBagKeys.BuildFailureReason,
                    PipelineDataBagKeys.TestFailureReason,
                    PipelineDataBagKeys.CoverageFailureReason,
                    PipelineDataBagKeys.SastFailureReason,
                    PipelineDataBagKeys.RatchetFailureReason,
                    PipelineDataBagKeys.StageCommand(stage.StageId),
                    PipelineDataBagKeys.StageOutput(stage.StageId)
                };

                foreach (var key in deterministicKeys)
                {
                    if (!string.IsNullOrEmpty(key))
                    {
                        bag.Remove(key);
                    }
                }

                // Supersede incomplete conversation runs if any exist
                var convs = await _taskRepository.GetConversationsAsync(task.Id, cancellationToken);
                foreach (var runningConv in convs.Where(c => c.Status == StageExecutionStatus.Running || c.Status == StageExecutionStatus.WaitingForHuman))
                {
                    await _taskRepository.CompleteConversationAsync(
                        runningConv.Id,
                        StageExecutionStatus.Failed,
                        summary: InterruptedDeterministicConversationSummary,
                        cancellationToken: cancellationToken);
                }

                _logger.LogWarning("Reset metrics for deterministic stage on orphaned task #{TaskId}.", task.Id);
            }

            if (string.IsNullOrWhiteSpace(task.CurrentStageId))
            {
                task.CurrentStageId = stage.StageId;
            }

            task.PipelineDataJson = bag.ToJson();

            if (!string.IsNullOrWhiteSpace(task.LastHangfireJobId))
            {
                _jobQueue.Delete(task.LastHangfireJobId);
                task.LastHangfireJobId = null;
            }

            if (_operationsControlService.IsPaused)
            {
                task.InterruptedStageId = stage.StageId;
                task.IsDeferredByPause = true;
                await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
                await _taskRepository.AddExecutionLogAsync(new ExecutionLog
                {
                    TaskId = task.Id,
                    Level = LogLevel.Information,
                    Message = $"Host reboot recovery: Operations are paused. Held task #{task.Id} at stage '{stage.StageId}' without enqueuing.",
                    Timestamp = DateTime.UtcNow
                }, cancellationToken);
                reconciledCount++;
                continue;
            }

            task.IsDeferredByPause = false;
            await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
            await _taskRepository.AddExecutionLogAsync(new ExecutionLog
            {
                TaskId = task.Id,
                Level = LogLevel.Warning,
                Message = $"Host reboot recovery: Re-enqueued task #{task.Id} to stage queue {stage.QueueTier} (Priority: {task.Priority}).",
                Timestamp = DateTime.UtcNow
            }, cancellationToken);

            // Stage-tier priority re-enqueue (P1 routes to urgent, others to their stage tier)
            task.LastHangfireJobId = _jobQueue.EnqueueStage(task.Id, stage.QueueTier, task.Priority);
            await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
            reconciledCount++;
        }

        _logger.LogInformation("Startup workspace reconciliation completed. Reconciled {Count} active tasks.", reconciledCount);
    }
}
