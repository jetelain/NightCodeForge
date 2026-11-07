using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.AI;
using NightCodeForge.Agents.Compaction;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Crucible;

/// <summary>Everything needed to persist the transcript and close the conversation of a Crucible stage run.</summary>
public sealed record CrucibleTranscriptRequest(
    AgentSessionContext Context,
    string WorkspacePath,
    IReadOnlyList<ChatMessage> ChatHistory,
    long ElapsedMs,
    long PromptTokens,
    long CompletionTokens,
    AgentRunStatus Status,
    string? Summary,
    ThinkingAndActivityTracingChatClient? TracingClient,
    HarnessAgentOptions? HarnessOptions,
    IReadOnlyList<string>? ActiveSkills);

/// <summary>
/// Live state of the database conversation of one Crucible stage run: the restored chat history plus ordered,
/// non-concurrent turn persistence.
/// </summary>
public sealed class CrucibleConversationState
{
    private readonly ITaskRepository _repository;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, bool> _writtenToolCallIds = new(StringComparer.Ordinal);
    private readonly List<ConversationTurn> _trackedTurns = new();
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    internal CrucibleConversationState(Conversation conversation, int attemptIndex, ITaskRepository repository, ILogger logger)
    {
        Conversation = conversation;
        AttemptIndex = attemptIndex;
        _repository = repository;
        _logger = logger;
        if (conversation.Turns is { Count: > 0 })
        {
            _trackedTurns.AddRange(conversation.Turns);
        }
    }

    public Conversation Conversation { get; }

    public int AttemptIndex { get; }

    /// <summary>True when this stage execution resumed an existing conversation or restored serialized session state.</summary>
    public bool IsResumedConversation { get; internal set; }

    /// <summary>Restored (or freshly seeded) chat history of the session; mutated in place by the harness.</summary>
    public List<ChatMessage> ChatHistory { get; internal set; } = new();

    /// <summary>Marks a tool call as recorded in the database; returns false when it already was.</summary>
    public bool TryRecordToolCall(string callId) => _writtenToolCallIds.TryAdd(callId, true);

    public bool IsToolCallRecorded(string callId) => _writtenToolCallIds.ContainsKey(callId);

    internal void TrackPersistedTurn(ConversationTurn? turn)
    {
        if (turn == null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_trackedTurns.Any(t => ReferenceEquals(t, turn)
                || (turn.Id > 0 && t.Id == turn.Id)
                || t.TurnIndex == turn.TurnIndex))
            {
                _trackedTurns.Add(turn);
            }
        }
    }

    /// <summary>Returns an ordered snapshot of all conversation turns tracked in this session.</summary>
    public IReadOnlyList<ConversationTurn> GetTrackedTurnsSnapshot()
    {
        lock (_gate)
        {
            return _trackedTurns.OrderBy(t => t.TurnIndex).ToList();
        }
    }

    /// <summary>Queues a conversation turn write, executed in submission order.</summary>
    public void EnqueueTurn(
        StageTurnRole role,
        string failureMessage,
        string? content = null,
        string? thinkingTrace = null,
        string? toolName = null,
        string? toolArguments = null,
        string? toolResult = null,
        long? durationMs = null,
        bool isError = false,
        string? metadataJson = null)
    {
        var conversationId = Conversation.Id;
        EnqueueWrite(
            async () =>
            {
                var turn = await _repository.AddConversationTurnAsync(
                    conversationId,
                    role,
                    content: content,
                    thinkingTrace: thinkingTrace,
                    toolName: toolName,
                    toolArguments: toolArguments,
                    toolResult: toolResult,
                    durationMs: durationMs,
                    isError: isError,
                    metadataJson: metadataJson,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                TrackPersistedTurn(turn);
            },
            failureMessage);
    }

    /// <summary>Queues arbitrary persistence work, executed in submission order; failures are logged, never thrown.</summary>
    public void EnqueueWrite(Func<Task> write, string failureMessage)
    {
        lock (_gate)
        {
            _tail = RunAfterAsync(_tail, write, failureMessage);
        }
    }

    /// <summary>Queues the sync of the task (pipeline data bag) with the conversation turn writes, preserving order.</summary>
    public void EnqueueTaskUpdate(ForgeTask task, CancellationToken cancellationToken) =>
        EnqueueWrite(
            () => _repository.UpdateAsync(task, cancellationToken: cancellationToken),
            $"Failed to sync pipeline data bag for task #{task.Id}");

    /// <summary>Completes once every queued write has run.</summary>
    public Task DrainAsync()
    {
        lock (_gate)
        {
            return _tail;
        }
    }

    public Task SanitizeTrailingInterruptedTurnsAsync(CancellationToken cancellationToken) =>
        _repository.SanitizeTrailingInterruptedTurnsAsync(Conversation.Id, cancellationToken);

    /// <summary>Call id of the most recent persisted <c>ask</c> tool turn, if any.</summary>
    public string? FindLastAskCallId() =>
        CrucibleConversationCoordinator.ExtractCallId(GetTrackedTurnsSnapshot().LastOrDefault(IsAskToolTurn));

    internal static bool IsAskToolTurn(ConversationTurn t) =>
        t.Role == StageTurnRole.Tool && string.Equals(t.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase);

    private async Task RunAfterAsync(Task previous, Func<Task> write, string failureMessage)
    {
        await previous.ConfigureAwait(false);
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Message}", failureMessage);
        }
    }
}

/// <summary>
/// Crucible stage conversation lifecycle: resolving or starting the database conversation, restoring the chat
/// history, synthetic priming, human answer resumption and transcript persistence.
/// </summary>
public interface ICrucibleConversationCoordinator
{
    /// <summary>
    /// Resolves or starts the conversation of the stage and returns it with its restored chat history, after applying
    /// synthetic priming and the pending human answer; every resulting turn write is persisted before returning.
    /// </summary>
    Task<CrucibleConversationState> BeginAsync(AgentSessionContext context, string harnessId, CancellationToken cancellationToken);

    /// <summary>Persists the session transcript and completes the database conversation. Returns the transcript file name.</summary>
    Task<string?> SaveTranscriptAsync(CrucibleConversationState state, CrucibleTranscriptRequest request, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class CrucibleConversationCoordinator : ICrucibleConversationCoordinator
{
    public const string DefaultBootstrapPrompt = "Please inspect the requirements and begin execution.";
    public const string FixDirectivePrompt = "Please fix the compilation/test errors shown above.";
    public const string HumanAnswerFollowUpPrompt = "Please proceed with your plan based on the clarified answers.";
    public const string CancelledSupersededSummary = "Cancelled superseded paused conversation";
    private const string SystemNoticeMarker = "[System Notice";
    private const string CallIdMetadataKey = "CallId";
    private const string AnswersMetadataKey = "Answers";
    private const string TranscriptsFolder = "transcripts";
    private const string DocsFolder = "docs";
    private const string ForwardPassPrimingType = "ForwardPass";
    private const string LoopbackDiagnosticPrimingType = "LoopbackDiagnostic";
    private const string CompactionRole = "COMPACTION";
    private const string LegacyCompactionMarker = ConversationHistorySerializer.CompactionCheckpointHeader;

    private readonly ITaskRepository _taskRepository;
    private readonly SessionTranscriptService _transcriptService;
    private readonly ILogger<CrucibleConversationCoordinator> _logger;

    public CrucibleConversationCoordinator(
        ITaskRepository taskRepository,
        SessionTranscriptService transcriptService,
        ILogger<CrucibleConversationCoordinator> logger)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _transcriptService = transcriptService ?? throw new ArgumentNullException(nameof(transcriptService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CrucibleConversationState> BeginAsync(AgentSessionContext context, string harnessId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        int attemptIndex = (context.PipelineContext?.StageRetries.GetValueOrDefault(context.StageId, 0) ?? 0) + 1;
        Conversation? conversation = null;
        Conversation? waitingConversation = null;
        ConversationTurn? initialBootstrapTurn = null;
        bool hadPersistedTurnsBeforeResume = false;

        var resumableConversation = await _taskRepository.GetResumableConversationAsync(context.ForgeTask.Id, context.StageId, cancellationToken);
        if (resumableConversation != null)
        {
            if (resumableConversation.Status == StageExecutionStatus.WaitingForHuman)
            {
                waitingConversation = resumableConversation;
                if (!string.IsNullOrWhiteSpace(context.PendingHumanAnswer))
                {
                    conversation = resumableConversation;
                    hadPersistedTurnsBeforeResume = conversation.Turns is { Count: > 0 };
                    await ReopenAsync(conversation, cancellationToken);
                }
                else
                {
                    await _taskRepository.CompleteConversationAsync(
                        resumableConversation.Id,
                        StageExecutionStatus.Cancelled,
                        summary: CancelledSupersededSummary,
                        cancellationToken: cancellationToken);
                }
            }
            else if (resumableConversation.Status == StageExecutionStatus.Interrupted)
            {
                conversation = resumableConversation;
                hadPersistedTurnsBeforeResume = conversation.Turns is { Count: > 0 };
                await ReopenAsync(conversation, cancellationToken);

                if (!hadPersistedTurnsBeforeResume && !string.IsNullOrWhiteSpace(context.BootstrapPrompt))
                {
                    initialBootstrapTurn = await _taskRepository.AddConversationTurnAsync(
                        conversation.Id,
                        StageTurnRole.User,
                        content: context.BootstrapPrompt,
                        cancellationToken: cancellationToken);
                    conversation.Turns ??= new List<ConversationTurn>();
                    if (!conversation.Turns.Contains(initialBootstrapTurn))
                    {
                        conversation.Turns.Add(initialBootstrapTurn);
                    }
                }
            }
        }

        if (conversation == null)
        {
            var existingConvs = await _taskRepository.GetConversationsAsync(context.ForgeTask.Id, cancellationToken);
            if (existingConvs != null)
            {
                var maxExistingAttempt = existingConvs
                    .Where(c => string.Equals(c.StageId, context.StageId, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.AttemptIndex)
                    .DefaultIfEmpty(0)
                    .Max();
                attemptIndex = Math.Max(attemptIndex, maxExistingAttempt + 1);
            }

            conversation = await _taskRepository.StartConversationAsync(
                context.ForgeTask.Id,
                context.StageId,
                WorkflowStageKind.Agentic,
                attemptIndex,
                context.Profile.Id,
                harnessId,
                context.Profile.ModelName,
                cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(context.BootstrapPrompt))
            {
                initialBootstrapTurn = await _taskRepository.AddConversationTurnAsync(
                    conversation.Id,
                    StageTurnRole.User,
                    content: context.BootstrapPrompt,
                    cancellationToken: cancellationToken);
            }
        }

        if (conversation.AttemptIndex > 0)
        {
            attemptIndex = conversation.AttemptIndex;
        }

        var state = new CrucibleConversationState(conversation, attemptIndex, _taskRepository, _logger);
        state.TrackPersistedTurn(initialBootstrapTurn);
        var effectiveSerializedStateJson = ResolveSerializedState(context, conversation, hadPersistedTurnsBeforeResume);
        state.IsResumedConversation = hadPersistedTurnsBeforeResume
            || !string.IsNullOrWhiteSpace(context.SerializedStateJson);
        state.ChatHistory = RestoreChatHistory(context, effectiveSerializedStateJson);

        if (conversation.Turns != null)
        {
            foreach (var t in conversation.Turns.Where(t => t.Role == StageTurnRole.Tool))
            {
                var cid = ExtractCallId(t);
                if (!string.IsNullOrEmpty(cid))
                {
                    state.TryRecordToolCall(cid);
                }
            }
        }

        if (string.IsNullOrEmpty(effectiveSerializedStateJson) && !hadPersistedTurnsBeforeResume && context.SyntheticPriming != null)
        {
            ApplySyntheticPriming(state, context.SyntheticPriming);
        }

        if (!string.IsNullOrWhiteSpace(context.PendingHumanAnswer))
        {
            var resumedWaitingConversation = ReferenceEquals(conversation, waitingConversation);
            ApplyPendingHumanAnswer(state, context, resumedWaitingConversation);
        }

        await state.DrainAsync();
        return state;
    }

    private async Task ReopenAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        await _taskRepository.ResumeConversationAsync(conversation.Id, cancellationToken);
        conversation.Status = StageExecutionStatus.Running;
        conversation.CompletedAt = null;
        conversation.Summary = null;
    }

    private string? ResolveSerializedState(AgentSessionContext context, Conversation conversation, bool hadPersistedTurnsBeforeResume)
    {
        string? effectiveSerializedStateJson = context.SerializedStateJson;
        if (hadPersistedTurnsBeforeResume && conversation.Turns is { Count: > 0 })
        {
            var turnsStateJson = ConversationHistorySerializer.Serialize(conversation.Turns);
            if (string.IsNullOrWhiteSpace(effectiveSerializedStateJson))
            {
                effectiveSerializedStateJson = turnsStateJson;
            }
            else
            {
                var turnsHistory = CrucibleAgentHarness.DeserializeSession(turnsStateJson, _logger);
                var stateHistory = CrucibleAgentHarness.DeserializeSession(effectiveSerializedStateJson, _logger);

                var turnsCheckpointMsg = turnsHistory.LastOrDefault(PiStyleCrucibleCompactionStrategy.IsCompactionCheckpointMessage);
                var stateCheckpointMsg = stateHistory.LastOrDefault(PiStyleCrucibleCompactionStrategy.IsCompactionCheckpointMessage);
                bool turnsHasCompactionCheckpoint = turnsCheckpointMsg != null;
                bool stateHasCompactionCheckpoint = stateCheckpointMsg != null;

                if (turnsHasCompactionCheckpoint && !stateHasCompactionCheckpoint)
                {
                    // DB turns contain a persisted compaction checkpoint while stateHistory is stale/uncompacted.
                    return turnsStateJson;
                }

                // Never overwrite a compacted stateHistory with an uncompacted turnsHistory simply because
                // turnsHistory has more items (compaction intentionally reduces message count).
                if (stateHasCompactionCheckpoint && !turnsHasCompactionCheckpoint)
                {
                    return effectiveSerializedStateJson;
                }

                if (turnsHasCompactionCheckpoint && stateHasCompactionCheckpoint)
                {
                    var turnsCheckpointText = turnsCheckpointMsg!.Text?.Trim() ?? string.Empty;
                    var stateCheckpointText = stateCheckpointMsg!.Text?.Trim() ?? string.Empty;
                    if (!string.Equals(turnsCheckpointText, stateCheckpointText, StringComparison.Ordinal))
                    {
                        return turnsStateJson;
                    }
                }

                if (turnsHistory.Count >= stateHistory.Count)
                {
                    effectiveSerializedStateJson = turnsStateJson;
                }
            }
        }

        return effectiveSerializedStateJson;
    }

    private List<ChatMessage> RestoreChatHistory(AgentSessionContext context, string? effectiveSerializedStateJson)
    {
        // The system prompt is NOT part of the history: it is supplied to HarnessAgent as HarnessInstructions
        // (ChatOptions.Instructions), so adding it as a System message too would send it to the model twice.
        var initialPrompt = !string.IsNullOrWhiteSpace(context.BootstrapPrompt) ? context.BootstrapPrompt : DefaultBootstrapPrompt;

        List<ChatMessage> chatHistory;
        if (!string.IsNullOrEmpty(effectiveSerializedStateJson))
        {
            chatHistory = CrucibleAgentHarness.DeserializeSession(effectiveSerializedStateJson, _logger);
            // Legacy checkpoints stored the system prompt as the first message; strip it while preserving system notices
            chatHistory.RemoveAll(m => m.Role == ChatRole.System && (m.Text == null || !m.Text.Contains(SystemNoticeMarker, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            chatHistory = new List<ChatMessage> { new(ChatRole.User, initialPrompt) };
        }

        // A checkpoint that cannot be restored (corrupt JSON) or only contained system notices would leave the
        // agent with no prompt at all: ensure the bootstrap prompt is present.
        if (chatHistory.Count == 0 || chatHistory.All(m => m.Role == ChatRole.System || (m.Text != null && m.Text.Contains(SystemNoticeMarker, StringComparison.OrdinalIgnoreCase))))
        {
            if (chatHistory.Count == 0)
            {
                chatHistory.Add(new ChatMessage(ChatRole.User, initialPrompt));
            }
            else
            {
                chatHistory.Insert(0, new ChatMessage(ChatRole.User, initialPrompt));
            }
        }

        return chatHistory;
    }

    private static void ApplySyntheticPriming(CrucibleConversationState state, SyntheticPrimingExecution priming)
    {
        var chatHistory = state.ChatHistory;
        var primeCallId = $"prime_{Guid.NewGuid():N}";
        state.TryRecordToolCall(primeCallId);

        chatHistory.Add(new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent(primeCallId, CanonicalToolNames.Bash, new Dictionary<string, object?> { ["command"] = priming.Command })
        }));
        chatHistory.Add(new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent(primeCallId, priming.Output)
        }));

        var primingMeta = JsonSerializer.Serialize(new PrimingTurnMetadata(
            CallId: primeCallId,
            IsPrimed: true,
            SourceStageId: priming.SourceStageId,
            PrimingType: priming.IsSuccess ? ForwardPassPrimingType : LoopbackDiagnosticPrimingType));
        var conversationId = state.Conversation.Id;

        state.EnqueueTurn(
            StageTurnRole.Tool,
            $"Failed to record primed Tool turn for conversation #{conversationId}",
            content: $"Execute {priming.Command}",
            toolName: CanonicalToolNames.Bash,
            toolArguments: JsonSerializer.Serialize(new { command = priming.Command }),
            metadataJson: primingMeta,
            durationMs: priming.DurationMs);

        state.EnqueueTurn(
            StageTurnRole.ToolResult,
            $"Failed to record primed ToolResult turn for conversation #{conversationId}",
            content: priming.Output,
            toolName: CanonicalToolNames.Bash,
            toolResult: priming.Output,
            isError: !priming.IsSuccess,
            metadataJson: primingMeta,
            durationMs: priming.DurationMs);

        if (!priming.IsSuccess)
        {
            chatHistory.Add(new ChatMessage(ChatRole.User, FixDirectivePrompt));
            state.EnqueueTurn(
                StageTurnRole.User,
                $"Failed to record fix directive User turn for conversation #{conversationId}",
                content: FixDirectivePrompt);
        }
    }

    /// <summary>Completes the pending <c>ask</c> tool call with the human answers as its tool result.</summary>
    private void ApplyPendingHumanAnswer(CrucibleConversationState state, AgentSessionContext context, bool resumedWaitingConversation)
    {
        var chatHistory = state.ChatHistory;
        var conversation = state.Conversation;
        var pendingAnswer = context.PendingHumanAnswer!;

        var askCallIds = new List<string>();
        var pendingCallIds = new HashSet<string>(StringComparer.Ordinal);
        var interruptedAskCallIds = new HashSet<string>(StringComparer.Ordinal);
        var answeredAskCallIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var msg in chatHistory)
        {
            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fcc && !string.IsNullOrEmpty(fcc.CallId) && string.Equals(fcc.Name, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase))
                {
                    if (!askCallIds.Contains(fcc.CallId, StringComparer.Ordinal))
                    {
                        askCallIds.Add(fcc.CallId);
                    }
                    pendingCallIds.Add(fcc.CallId);
                }
                else if (content is FunctionResultContent frc && !string.IsNullOrEmpty(frc.CallId) && askCallIds.Contains(frc.CallId, StringComparer.Ordinal))
                {
                    pendingCallIds.Remove(frc.CallId);
                    var resStr = frc.Result?.ToString();
                    if (string.Equals(resStr, ConversationHistorySerializer.InterruptedToolResult, StringComparison.Ordinal))
                    {
                        interruptedAskCallIds.Add(frc.CallId);
                    }
                    else
                    {
                        interruptedAskCallIds.Remove(frc.CallId);
                        answeredAskCallIds.Add(frc.CallId);
                    }
                }
            }
        }

        if (interruptedAskCallIds.Count > 0)
        {
            foreach (var msg in chatHistory)
            {
                for (int i = 0; i < msg.Contents.Count; i++)
                {
                    if (msg.Contents[i] is FunctionResultContent frc &&
                        !string.IsNullOrEmpty(frc.CallId) &&
                        interruptedAskCallIds.Contains(frc.CallId))
                    {
                        msg.Contents[i] = new FunctionResultContent(frc.CallId, pendingAnswer);
                    }
                }
            }
        }

        foreach (var pendingId in pendingCallIds)
        {
            chatHistory.Add(new ChatMessage(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent(pendingId, pendingAnswer)
            }));
        }

        bool endsWithAnsweredAskToolResult = answeredAskCallIds.Count > 0 &&
            chatHistory.LastOrDefault() is { } lastToolMsg &&
            lastToolMsg.Role == ChatRole.Tool &&
            lastToolMsg.Contents.OfType<FunctionResultContent>().Any(frc => !string.IsNullOrEmpty(frc.CallId) && answeredAskCallIds.Contains(frc.CallId));

        if (pendingCallIds.Count > 0 || interruptedAskCallIds.Count > 0 || endsWithAnsweredAskToolResult)
        {
            var lastMsg = chatHistory.LastOrDefault();
            if (lastMsg == null || lastMsg.Role != ChatRole.User || !string.Equals(lastMsg.Text, HumanAnswerFollowUpPrompt, StringComparison.Ordinal))
            {
                chatHistory.Add(new ChatMessage(ChatRole.User, HumanAnswerFollowUpPrompt));
            }
        }
        else if (resumedWaitingConversation &&
                 !chatHistory.Any(m => (m.Text != null && m.Text.Contains(pendingAnswer, StringComparison.Ordinal))
                     || m.Contents.OfType<FunctionResultContent>().Any(frc => frc.Result?.ToString()?.Contains(pendingAnswer, StringComparison.Ordinal) == true)))
        {
            // Resuming a paused conversation that was interrupted/waiting for human without pending call IDs in memory
            chatHistory.Add(new ChatMessage(ChatRole.User, $"[Human Clarification]\n{pendingAnswer}\n\n{HumanAnswerFollowUpPrompt}"));
        }

        // Persist ToolResult and follow-up turns in the resumed database conversation
        var targetCallIds = pendingCallIds.Concat(interruptedAskCallIds).ToList();
        if (targetCallIds.Count == 0 && askCallIds.Count > 0)
        {
            targetCallIds.Add(askCallIds[^1]);
        }
        if (targetCallIds.Count == 0 && resumedWaitingConversation)
        {
            var askTurn = conversation.Turns?.LastOrDefault(CrucibleConversationState.IsAskToolTurn);
            if (askTurn != null)
            {
                var cid = ExtractCallId(askTurn);
                targetCallIds.Add(!string.IsNullOrEmpty(cid) ? cid : $"call_{Guid.NewGuid():N}");
            }
        }

        // Strictly guard: only record ToolResult and followUp if an ask call was actually present.
        // Never record orphan ask ToolResult turns in un-interrupted or fresh conversations!
        if (targetCallIds.Count == 0)
        {
            return;
        }

        var convId = conversation.Id;
        foreach (var callId in targetCallIds)
        {
            bool alreadyHasToolResult = conversation.Turns != null && conversation.Turns.Any(t =>
                t.Role == StageTurnRole.ToolResult &&
                string.Equals(t.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase) &&
                ((t.MetadataJson != null && t.MetadataJson.Contains(callId)) || (string.IsNullOrEmpty(t.MetadataJson) && t.ToolResult == pendingAnswer)));

            if (!alreadyHasToolResult)
            {
                state.EnqueueTurn(
                    StageTurnRole.ToolResult,
                    $"Failed to record ToolResult turn for conversation #{convId}",
                    toolName: CanonicalToolNames.Ask,
                    toolResult: pendingAnswer,
                    metadataJson: BuildToolResultMetadata(callId, context.PendingHumanAnswerJson));
            }
        }

        var lastAskTurnIndex = conversation.Turns?.LastOrDefault(CrucibleConversationState.IsAskToolTurn)?.TurnIndex ?? -1;
        bool alreadyHasFollowUp = conversation.Turns != null && conversation.Turns.Any(t =>
            t.TurnIndex > lastAskTurnIndex && t.Role == StageTurnRole.User && t.Content == HumanAnswerFollowUpPrompt);

        if (!alreadyHasFollowUp)
        {
            state.EnqueueTurn(
                StageTurnRole.User,
                $"Failed to record follow-up turn for conversation #{convId}",
                content: HumanAnswerFollowUpPrompt);
        }
    }

    public async Task<string?> SaveTranscriptAsync(CrucibleConversationState state, CrucibleTranscriptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);

        // Every queued conversation turn must be persisted before the conversation is closed.
        await state.DrainAsync();

        var context = request.Context;
        var tracingClient = request.TracingClient;
        var harnessOptions = request.HarnessOptions;
        string? transcriptFileName = null;
        try
        {
            var systemPrompt = tracingClient?.LastCapturedSystemPrompt
                ?? request.ChatHistory.FirstOrDefault(m => m.Role == ChatRole.System)?.Text
                ?? context.Profile.SystemPrompt;
            IReadOnlyList<string>? availableTools = tracingClient is { LastCapturedTools.Count: > 0 }
                ? tracingClient.LastCapturedTools
                : context.Profile.PermittedTools;
            var mafConfig = harnessOptions != null
                ? MafConfigurationSummary.Build(harnessOptions, tracingClient?.DetectedMafConfiguration)
                : null;
            var entries = BuildTranscriptEntries(request.ChatHistory);

            TranscriptCompactionMetadata? compactionMeta = null;
            if (harnessOptions?.CompactionStrategy is PiStyleCrucibleCompactionStrategy piCompaction)
            {
                var opts = piCompaction.Options;
                compactionMeta = new TranscriptCompactionMetadata(
                    piCompaction.GetType().Name,
                    opts.ContextWindowTokens,
                    opts.ReserveTokens,
                    opts.TriggerThresholdTokens,
                    opts.KeepRecentTokens,
                    opts.ResolutionSource ?? "Unknown");
            }

            var meta = new TranscriptMetadata(
                context.ForgeTask.Id,
                context.StageId,
                context.Profile.Name,
                context.Profile.ModelName,
                request.ElapsedMs,
                new AgentRunMetrics(request.ElapsedMs, request.PromptTokens, request.CompletionTokens, request.PromptTokens + request.CompletionTokens),
                context.OptimizationFlags ?? new LocalOptimizationFlags(),
                request.Status,
                KvCacheHitRate: null,
                SystemPrompt: systemPrompt,
                AvailableTools: availableTools,
                ActiveSkills: request.ActiveSkills,
                MafConfiguration: mafConfig,
                ModelCalls: tracingClient?.ModelCalls,
                AttemptIndex: state.AttemptIndex,
                Compaction: compactionMeta);

            var repoRoot = context.Workspace?.RepositoryPath ?? request.WorkspacePath;
            var transcriptDir = Path.Combine(repoRoot, DocsFolder, TranscriptsFolder);
            var savedPath = await _transcriptService.SaveTranscriptAsync(meta, entries, transcriptDir, cancellationToken);
            transcriptFileName = Path.GetFileName(savedPath);
        }
        catch (Exception tex)
        {
            _logger.LogWarning(tex, "Failed to persist session transcript for task #{TaskId} with status {Status}", context.ForgeTask.Id, request.Status);
        }

        try
        {
            var stageStatus = request.Status switch
            {
                AgentRunStatus.Completed => StageExecutionStatus.Completed,
                AgentRunStatus.WaitingForHuman => StageExecutionStatus.WaitingForHuman,
                AgentRunStatus.Cancelled => StageExecutionStatus.Cancelled,
                AgentRunStatus.Interrupted => StageExecutionStatus.Interrupted,
                _ => StageExecutionStatus.Failed
            };

            await _taskRepository.CompleteConversationAsync(
                state.Conversation.Id,
                stageStatus,
                summary: request.Summary,
                transcriptFileName: transcriptFileName,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to complete Conversation #{ConversationId}", state.Conversation.Id);
        }

        return transcriptFileName;
    }

    public static List<TranscriptEntry> BuildTranscriptEntries(IReadOnlyList<ChatMessage> chatHistory)
    {
        var entries = new List<TranscriptEntry>();
        var now = DateTime.UtcNow;
        // Tool result messages only carry the call id: resolve it back to the name of the tool that was called.
        var toolNamesByCallId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var msg in chatHistory)
        {
            var role = msg.Role.Value;
            var text = msg.Text ?? string.Empty;

            if (text.StartsWith(PiStyleCrucibleCompactionStrategy.CheckpointPreamble, StringComparison.Ordinal)
                || text.StartsWith(LegacyCompactionMarker, StringComparison.Ordinal))
            {
                role = CompactionRole;
            }

            // Structured reasoning (TextReasoningContent) is not part of msg.Text; emit it as its own step.
            if (msg.Role == ChatRole.Assistant)
            {
                var reasoning = string.Concat(msg.Contents.OfType<TextReasoningContent>().Select(r => r.Text));
                if (!string.IsNullOrWhiteSpace(reasoning))
                {
                    entries.Add(new TranscriptEntry(now, "THINKING", reasoning));
                }
            }

            var functionCalls = msg.Contents.OfType<FunctionCallContent>().ToList();
            var functionResults = msg.Contents.OfType<FunctionResultContent>().ToList();

            if (functionCalls.Count > 0)
            {
                for (int i = 0; i < functionCalls.Count; i++)
                {
                    var fc = functionCalls[i];
                    if (!string.IsNullOrEmpty(fc.CallId))
                    {
                        toolNamesByCallId[fc.CallId] = fc.Name;
                    }
                    var argsJson = fc.Arguments != null
                        ? JsonSerializer.Serialize(fc.Arguments, new JsonSerializerOptions { WriteIndented = true })
                        : null;

                    entries.Add(new TranscriptEntry(
                        now,
                        role,
                        Content: i == 0 ? text : string.Empty,
                        ToolName: fc.Name,
                        ToolArguments: argsJson,
                        ToolResult: null));
                }
            }
            else if (functionResults.Count > 0)
            {
                foreach (var fr in functionResults)
                {
                    var resultStr = fr.Result?.ToString() ?? string.Empty;
                    entries.Add(new TranscriptEntry(
                        now,
                        role,
                        Content: string.Empty,
                        ToolName: fr.CallId != null && toolNamesByCallId.TryGetValue(fr.CallId, out var calledTool) ? calledTool : fr.CallId,
                        ToolArguments: null,
                        ToolResult: resultStr));
                }
            }
            else
            {
                entries.Add(new TranscriptEntry(
                    now,
                    role,
                    Content: text,
                    ToolName: null,
                    ToolArguments: null,
                    ToolResult: null));
            }
        }

        return entries;
    }

    public static string? ExtractCallId(ConversationTurn? turn)
    {
        if (turn == null || string.IsNullOrWhiteSpace(turn.MetadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(turn.MetadataJson);
            if (doc.RootElement.TryGetProperty(CallIdMetadataKey, out var cProp))
            {
                return cProp.GetString();
            }
        }
        catch (JsonException)
        {
            // Justification: turn metadata is best-effort diagnostics; a malformed payload simply carries no call id.
        }
        return null;
    }

    private string BuildToolResultMetadata(string callId, string? structuredAnswerJson)
    {
        var metaDict = new Dictionary<string, object?> { [CallIdMetadataKey] = callId };
        if (!string.IsNullOrWhiteSpace(structuredAnswerJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(structuredAnswerJson);
                metaDict[AnswersMetadataKey] = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Ignoring malformed structured human answer JSON for call {CallId}", callId);
            }
        }
        return JsonSerializer.Serialize(metaDict);
    }
}
