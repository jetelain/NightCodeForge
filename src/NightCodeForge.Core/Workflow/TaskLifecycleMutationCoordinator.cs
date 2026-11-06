using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Default implementation of <see cref="ITaskLifecycleMutationCoordinator"/> handling human answers,
/// change requests, suggested task promotions, rejections, pipeline restarts, and execution cancellation tracking.
/// </summary>
public class TaskLifecycleMutationCoordinator : ITaskLifecycleMutationCoordinator
{
    private readonly ITaskRepository _taskRepository;
    private readonly ITaskWorkspacePreparer _workspacePreparer;
    private readonly ITaskMergeAndCascadeCoordinator _mergeAndCascadeCoordinator;
    private readonly IWorkflowPipelineProvider _pipelineProvider;
    private readonly ITaskJobQueue _jobQueue;
    private readonly ITaskCancellationManager _cancellationManager;
    private readonly IAgentActivityStreamer _streamer;
    private readonly ILogger<TaskLifecycleMutationCoordinator> _logger;

    public TaskLifecycleMutationCoordinator(
        ITaskRepository taskRepository,
        ITaskWorkspacePreparer workspacePreparer,
        ITaskMergeAndCascadeCoordinator mergeAndCascadeCoordinator,
        IWorkflowPipelineProvider pipelineProvider,
        ITaskJobQueue jobQueue,
        ITaskCancellationManager cancellationManager,
        IAgentActivityStreamer streamer,
        ILogger<TaskLifecycleMutationCoordinator> logger)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _workspacePreparer = workspacePreparer ?? throw new ArgumentNullException(nameof(workspacePreparer));
        _mergeAndCascadeCoordinator = mergeAndCascadeCoordinator ?? throw new ArgumentNullException(nameof(mergeAndCascadeCoordinator));
        _pipelineProvider = pipelineProvider ?? throw new ArgumentNullException(nameof(pipelineProvider));
        _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
        _cancellationManager = cancellationManager ?? throw new ArgumentNullException(nameof(cancellationManager));
        _streamer = streamer ?? throw new ArgumentNullException(nameof(streamer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public CancellationTokenSource RegisterExecutionCancellation(int taskId, CancellationToken externalToken = default) =>
        _cancellationManager.Register(taskId, externalToken);

    public void UnregisterExecutionCancellation(int taskId, CancellationTokenSource cts) =>
        _cancellationManager.Unregister(taskId, cts);

    public bool IsCancelRequested(int taskId) =>
        _cancellationManager.IsCancelRequested(taskId);

    public bool IsPauseRequested(int taskId) =>
        _cancellationManager.IsPauseRequested(taskId);

    public bool CanRestoreCheckpointMemory(IEnumerable<Conversation>? conversations, StageCheckpoint? checkpoint, string? stageId) =>
        CanRestoreCheckpointMemoryInternal(conversations, checkpoint, stageId);

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

        using var lockReleaser = await _workspacePreparer.AcquireTaskLockAsync(task.WorkspaceId, task.Id, cancellationToken);

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
        bool hasPendingQuestionsInBatch = !string.IsNullOrEmpty(activeBatchId)
            ? task.Questions.Any(q =>
                string.Equals(q.BatchId, activeBatchId, StringComparison.OrdinalIgnoreCase) &&
                q.Status == QuestionStatus.Pending)
            : task.Questions.Any(q => q.Status == QuestionStatus.Pending);

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
            var targetStageId = batchQuestions.FirstOrDefault()?.StageId ?? task.CurrentStageId;
            IEnumerable<Conversation>? latestConvs = await _taskRepository.GetConversationsAsync(task.Id, cancellationToken);
            latestConvs ??= task.Conversations;
            if (CanRestoreCheckpointMemoryInternal(latestConvs, lastCheckpoint, targetStageId))
            {
                currentMemory = lastCheckpoint!.SerializedMemory;
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
        await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, task.Status, cancellationToken);
        await _mergeAndCascadeCoordinator.SetWaitingForHumanInStoreAsync(task.Id, false, cancellationToken);

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
        task.IsWaitingForHuman = false;
        task.InterruptedStageId = null;
        task.InterruptedAgentProfileId = null;

        var bag = PipelineDataBag.FromJson(task.PipelineDataJson);
        bag.Set(PipelineDataBagKeys.ReviewerFeedback, revisionNotes);
        task.PipelineDataJson = bag.ToJson();

        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.User,
            reason: $"Reviewer requested changes (routing back to '{targetStageId}'): {revisionNotes}",
            cancellationToken: cancellationToken);
        await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, TaskOverallStatus.Open, cancellationToken);
        await _mergeAndCascadeCoordinator.SetWaitingForHumanInStoreAsync(task.Id, false, cancellationToken);

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
        string? baseBranch = null,
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
            if (!string.IsNullOrWhiteSpace(baseBranch))
            {
                task.BaseBranch = baseBranch.Trim();
            }
            task.UpdatedAt = DateTime.UtcNow;

            await _taskRepository.UpdateWithHistoryAsync(
                task,
                dependsOn: null,
                changedBy: AuditActors.User,
                reason: $"Promoted task from Suggested to Open with Priority P{priority}, {mergeMode}, and BaseBranch '{task.BaseBranch}'.",
                cancellationToken: cancellationToken);
            await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, TaskOverallStatus.Open, cancellationToken);

            var isReady = await _mergeAndCascadeCoordinator.IsReadyForExecutionAsync(task.Id, cancellationToken);
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

        var workspace = await _workspacePreparer.GetWorkspaceAsync(task, cancellationToken);
        if (workspace != null)
        {
            await _workspacePreparer.SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);
        }

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Information,
            Message = $"ForgeTask #{task.Id} promoted to status {targetStatus} (Priority: {priority}, MergeMode: {mergeMode}, BaseBranch: {task.BaseBranch}).",
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
        task.InterruptedStageId = null;
        task.InterruptedAgentProfileId = null;
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

        var convs = await _taskRepository.GetConversationsAsync(task.Id, cancellationToken);
        foreach (var c in convs.Where(c =>
            c.Status == StageExecutionStatus.Running ||
            c.Status == StageExecutionStatus.WaitingForHuman ||
            c.Status == StageExecutionStatus.Interrupted))
        {
            await _taskRepository.CompleteConversationAsync(
                c.Id,
                StageExecutionStatus.Cancelled,
                summary: rejectionReason,
                cancellationToken: cancellationToken);
        }

        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.User,
            reason: rejectionReason,
            cancellationToken: cancellationToken);

        await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, TaskOverallStatus.Rejected, cancellationToken);
        await _mergeAndCascadeCoordinator.SetWaitingForHumanInStoreAsync(task.Id, false, cancellationToken);

        var workspace = await _workspacePreparer.GetWorkspaceAsync(task, cancellationToken);
        if (workspace != null)
        {
            await _workspacePreparer.RemoveWorktreeSafeAsync(workspace, task.Id, "rejected", cancellationToken);
            await _workspacePreparer.CleanupTaskContainerSafeAsync(workspace, task.Id, "rejected", cancellationToken);
            await _workspacePreparer.SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);
            await _workspacePreparer.ArchiveTaskMarkdownSafeAsync(workspace, task, cancellationToken);
        }

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Warning,
            Message = $"ForgeTask #{task.Id} was rejected by maintainer. Reason: {rejectionReason}",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);
    }

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
        using var lockReleaser = await _workspacePreparer.AcquireTaskLockAsync(initialSnapshot.WorkspaceId, taskId, cancellationToken);

        // 4. Reload task entity after acquiring lock to guarantee fresh state
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken)
            ?? throw new KeyNotFoundException($"Task #{taskId} not found.");

        if (task.SquashCommitSha != null || task.Status == TaskOverallStatus.Closed)
        {
            throw new InvalidOperationException("Cannot restart a task that has already been merged.");
        }

        // 5. Delete lingering Hangfire job if present and clear LastHangfireJobId before saving
        // so a fast worker picking up the newly enqueued job does not read the old ID and self-abort.
        if (!string.IsNullOrWhiteSpace(task.LastHangfireJobId))
        {
            _jobQueue.Delete(task.LastHangfireJobId);
            task.LastHangfireJobId = null;
        }

        var workspace = await _workspacePreparer.GetWorkspaceAsync(task, cancellationToken);
        if (workspace == null)
        {
            throw new InvalidOperationException($"Workspace #{task.WorkspaceId} not found for task #{taskId}.");
        }

        // 6. Hard Reset Path: Container teardown FIRST, then Git reset
        if (gitMode == TaskRestartGitMode.HardResetToBaseBranch)
        {
            try
            {
                await _workspacePreparer.ResetTaskWorkspaceToBaseAsync(workspace, task.Id, task.BaseBranch, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed resetting workspace and Git state during pipeline restart for Task #{TaskId}", task.Id);
                task.Status = TaskOverallStatus.Failed;
                await _taskRepository.UpdateWithHistoryAsync(
                    task,
                    dependsOn: null,
                    changedBy: AuditActors.TaskOrchestrator,
                    reason: $"Pipeline restart failed: {ex.Message}",
                    cancellationToken: cancellationToken);
                await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, TaskOverallStatus.Failed, cancellationToken);
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
        else if (!string.IsNullOrWhiteSpace(task.PipelineDataJson) && task.PipelineDataJson != "{}")
        {
            var bag = PipelineDataBag.FromJson(task.PipelineDataJson);
            bool modified = false;
            modified |= bag.Remove(PipelineDataBagKeys.RecoveredSerializedMemory);
            modified |= bag.Remove(PipelineDataBagKeys.SerializedMemory);
            modified |= bag.Remove(PipelineDataBagKeys.LastHumanAnswer);
            modified |= bag.Remove(PipelineDataBagKeys.LastHumanAnswerJson);
            modified |= bag.Remove(PipelineDataBagKeys.PendingSyntheticPriming);
            modified |= bag.Remove(PipelineDataBagKeys.RetryCount);
            modified |= bag.Remove(PipelineDataBagKeys.StageRetries);
            modified |= bag.Remove(PipelineDataBagKeys.CompilerErrors);
            modified |= bag.Remove(PipelineDataBagKeys.FailedTests);
            modified |= bag.Remove(PipelineDataBagKeys.ReviewerFeedback);
            modified |= bag.Remove(PipelineDataBagKeys.ReviewDiff);
            modified |= bag.Remove(PipelineDataBagKeys.ReviewDiffStat);
            if (modified)
            {
                task.PipelineDataJson = bag.ToJson();
            }
        }

        // 7. Reset Task State
        task.Status = TaskOverallStatus.Open;
        task.SquashCommitSha = null;
        task.IsWaitingForHuman = false;
        task.InterruptedStageId = null;
        task.InterruptedAgentProfileId = null;
        task.UpdatedAt = DateTime.UtcNow;

        if (task.Questions != null)
        {
            foreach (var question in task.Questions.Where(q => q.Status == QuestionStatus.Pending))
            {
                question.Status = QuestionStatus.Skipped;
            }
        }

        if (task.Checkpoints != null)
        {
            foreach (var checkpoint in task.Checkpoints.Where(c => !string.IsNullOrEmpty(c.SerializedMemory)))
            {
                checkpoint.SerializedMemory = string.Empty;
            }
        }

        // 8. Stage Conversations - mark active, interrupted, or pending as Superseded
        var convs = await _taskRepository.GetConversationsAsync(task.Id, cancellationToken) ?? Array.Empty<Conversation>();
        foreach (var c in convs.Where(c =>
            c.Status == StageExecutionStatus.Running ||
            c.Status == StageExecutionStatus.WaitingForHuman ||
            c.Status == StageExecutionStatus.Interrupted ||
            c.Status == StageExecutionStatus.Pending))
        {
            await _taskRepository.CompleteConversationAsync(c.Id, StageExecutionStatus.Superseded, summary: "Pipeline restarted; previous conversation superseded.", cancellationToken: cancellationToken);
        }

        // 9. Resolve Initial Stage
        var pipeline = await _pipelineProvider.GetPipelineAsync(task, cancellationToken);
        var initialStage = pipeline.InitialStage ?? throw new InvalidOperationException($"Task #{task.Id} has no initial workflow stage.");
        task.CurrentStageId = initialStage.StageId;

        // 10. Update DB Record with History
        var changeReason = gitMode == TaskRestartGitMode.HardResetToBaseBranch
            ? $"Pipeline restarted from Stage 1 with Hard Reset to '{task.BaseBranch}' (discards previous work)"
            : "Pipeline restarted from Stage 1 with preserved worktree changes";

        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.User,
            reason: changeReason,
            cancellationToken: cancellationToken);

        await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, TaskOverallStatus.Open, cancellationToken);
        await _mergeAndCascadeCoordinator.SetWaitingForHumanInStoreAsync(task.Id, false, cancellationToken);

        // 11. Execution Log
        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Information,
            Message = changeReason,
            Timestamp = DateTime.UtcNow
        }, cancellationToken);

        // 12. SignalR Telemetry Event
        try
        {
            await _streamer.BroadcastAsync(new AgentActivityEvent(
                Guid.NewGuid().ToString("N"),
                task.Id,
                initialStage.StageId,
                AgentActivityKind.Notice,
                $"Pipeline restarted for task #{task.Id} from stage '{initialStage.StageId}' ({gitMode})",
                DateTime.UtcNow,
                new Dictionary<string, object?>
                {
                    ["IsRestart"] = true,
                    ["RestartMode"] = gitMode.ToString(),
                    [AgentActivityMetadataKeys.TaskTitle] = task.Title,
                    [AgentActivityMetadataKeys.Priority] = task.Priority,
                    [AgentActivityMetadataKeys.StageKind] = initialStage.Kind.ToString(),
                    [AgentActivityMetadataKeys.QueueType] = initialStage.Kind == WorkflowStageKind.Deterministic
                        ? DeterministicStageConstants.CpuDeterministicQueue
                        : DeterministicStageConstants.LlmInferenceQueue
                }), cancellationToken);
        }
        catch (Exception sex)
        {
            _logger.LogDebug(sex, "Failed broadcasting stage restarted event for task #{TaskId}", task.Id);
        }

        // 13. Re-enqueue Initial Stage
        task.IsDeferredByPause = false;
        var newJobId = _jobQueue.EnqueueStage(task.Id, initialStage.QueueTier, task.Priority);
        task.LastHangfireJobId = newJobId;
        await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);

        // 14. Markdown Sync
        await _workspacePreparer.SyncTaskMarkdownAndMirrorAsync(task, workspace, cancellationToken);

        _logger.LogInformation("Successfully restarted pipeline for Task #{TaskId} (Mode: {GitMode}, InitialStage: {StageId}, JobId: {JobId})",
            taskId, gitMode, initialStage.StageId, newJobId);

        return true;
    }

    internal static bool CanRestoreCheckpointMemoryInternal(IEnumerable<Conversation>? conversations, StageCheckpoint? checkpoint, string? stageId)
    {
        if (checkpoint == null || string.IsNullOrEmpty(checkpoint.SerializedMemory))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(stageId) &&
            !string.Equals(checkpoint.StageId, stageId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var targetStageId = !string.IsNullOrWhiteSpace(stageId) ? stageId : checkpoint.StageId;
        var stageConvs = conversations?
            .Where(c => string.Equals(c.StageId, targetStageId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (stageConvs == null || stageConvs.Count == 0)
        {
            return true;
        }

        var latestStageConv = stageConvs
            .OrderByDescending(c => c.AttemptIndex)
            .ThenByDescending(c => c.StartedAt)
            .ThenByDescending(c => c.Id)
            .First();

        bool isLatestResumable = latestStageConv.Status == StageExecutionStatus.WaitingForHuman ||
                                 latestStageConv.Status == StageExecutionStatus.Interrupted ||
                                 latestStageConv.Status == StageExecutionStatus.Running;
        if (!isLatestResumable)
        {
            return false;
        }

        var lastSupersededConv = stageConvs
            .Where(c => c.Status == StageExecutionStatus.Superseded)
            .OrderByDescending(c => c.CompletedAt ?? c.StartedAt)
            .ThenByDescending(c => c.Id)
            .FirstOrDefault();

        if (lastSupersededConv != null &&
            checkpoint.CreatedAt <= (lastSupersededConv.CompletedAt ?? lastSupersededConv.StartedAt))
        {
            return false;
        }

        return true;
    }
}

