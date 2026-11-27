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
using NightCodeForge.Core.Inference;
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
    private readonly IConversationTurnSink _turnSink;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, bool> _writtenToolCallIds = new(StringComparer.Ordinal);
    private readonly List<ConversationTurn> _trackedTurns = new();
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    public CrucibleConversationState(Conversation conversation, int attemptIndex, IConversationTurnSink turnSink, ILogger logger)
    {
        Conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        AttemptIndex = attemptIndex;
        _turnSink = turnSink ?? throw new ArgumentNullException(nameof(turnSink));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (conversation.Turns is { Count: > 0 })
        {
            _trackedTurns.AddRange(conversation.Turns);
        }
    }

    internal CrucibleConversationState(Conversation conversation, int attemptIndex, ITaskRepository repository, ILogger logger)
        : this(conversation, attemptIndex, new TaskRepositoryConversationTurnSink(repository ?? throw new ArgumentNullException(nameof(repository))), logger)
    {
    }

    public Conversation Conversation { get; }

    public int AttemptIndex { get; }

    /// <summary>Stable router session identifier for sticky routing affinity and KV-cache reuse.</summary>
    public string? SessionId { get; set; }

    /// <summary>Baseline LLM session usage captured at conversation start for delta accounting.</summary>
    public RouterSessionUsage? BaselineUsage { get; set; }

    /// <summary>Computed LLM session delta usage from the most recent save/completion.</summary>
    public SessionUsageDelta? LastUsageDelta { get; set; }

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
                var turn = await _turnSink.AddTurnAsync(
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
            () => _turnSink.UpdateTaskAsync(task, cancellationToken),
            $"Failed to sync pipeline data bag for task #{task.Id}");

    /// <summary>Completes once every queued write has run.</summary>
    public Task DrainAsync()
    {
        lock (_gate)
        {
            return _tail;
        }
    }

    public async Task SanitizeTrailingInterruptedTurnsAsync(CancellationToken cancellationToken)
    {
        await _turnSink.SanitizeTrailingInterruptedTurnsAsync(Conversation.Id, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_trackedTurns.Count > 0)
            {
                var toRemove = ConversationHistorySerializer.GetTrailingInterruptedTurns(_trackedTurns);
                foreach (var turn in toRemove)
                {
                    _trackedTurns.Remove(turn);
                    if (turn.Role == StageTurnRole.Tool)
                    {
                        var callId = CrucibleConversationCoordinator.ExtractCallId(turn);
                        if (!string.IsNullOrEmpty(callId))
                        {
                            _writtenToolCallIds.TryRemove(callId, out _);
                        }
                    }
                }
            }
        }
    }

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

    /// <summary>Releases sticky router session affinity for the specified session (e.g. on stage completion or after emergency context compaction).</summary>
    Task ReleaseSessionAffinityAsync(string? sessionId, CancellationToken cancellationToken = default);
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
    private const string UserTranscriptRole = "USER";
    private const string ThinkingTranscriptRole = "THINKING";
    private const string AssistantTranscriptRole = "ASSISTANT";
    private const string ToolTranscriptRole = "TOOL";
    private const string ToolResultTranscriptRole = "TOOL_RESULT";
    private const string ToolErrorTranscriptRole = "TOOL_ERROR";
    private const string OptimizationTranscriptRole = "OPTIMIZATION";
    private const string CompactionRole = "COMPACTION";
    private const string LegacyCompactionMarker = ConversationHistorySerializer.CompactionCheckpointHeader;
    private static readonly TimeSpan SessionTelemetryTimeout = TimeSpan.FromSeconds(5);

    internal static long ComputeTotalInferenceDurationMs(
        ThinkingAndActivityTracingChatClient? tracingClient,
        HarnessAgentOptions? harnessOptions)
    {
        long primaryInferenceMs = tracingClient?.TotalInferenceDurationMs ?? 0;
        long summarizerInferenceMs = harnessOptions?.CompactionStrategy is PiStyleCrucibleCompactionStrategy piCompaction
            ? piCompaction.TotalSummarizerInferenceDurationMs
            : 0;
        return primaryInferenceMs + summarizerInferenceMs;
    }

    private readonly ITaskRepository _taskRepository;
    private readonly SessionTranscriptService _transcriptService;
    private readonly ILlmGatewayService _llmGatewayService;
    private readonly ILogger<CrucibleConversationCoordinator> _logger;

    public CrucibleConversationCoordinator(
        ITaskRepository taskRepository,
        SessionTranscriptService transcriptService,
        ILlmGatewayService llmGatewayService,
        ILogger<CrucibleConversationCoordinator> logger)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _transcriptService = transcriptService ?? throw new ArgumentNullException(nameof(transcriptService));
        _llmGatewayService = llmGatewayService ?? throw new ArgumentNullException(nameof(llmGatewayService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CrucibleConversationState> BeginAsync(AgentSessionContext context, string harnessId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        int attemptIndex = (context.PipelineContext?.StageRetries.GetValueOrDefault(context.StageId, 0) ?? 0) + 1;
        int? persistedTaskId = context.ForgeTask.Id > 0 ? context.ForgeTask.Id : null;
        Conversation? conversation = null;
        Conversation? waitingConversation = null;
        ConversationTurn? initialBootstrapTurn = null;
        bool hadPersistedTurnsBeforeResume = false;

        var resumableConversation = persistedTaskId.HasValue
            ? await _taskRepository.GetResumableConversationAsync(persistedTaskId.Value, context.StageId, cancellationToken)
            : null;
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
            if (persistedTaskId.HasValue)
            {
                var existingConvs = await _taskRepository.GetConversationsAsync(persistedTaskId.Value, cancellationToken);
                if (existingConvs != null)
                {
                    var maxExistingAttempt = existingConvs
                        .Where(c => string.Equals(c.StageId, context.StageId, StringComparison.OrdinalIgnoreCase))
                        .Select(c => c.AttemptIndex)
                        .DefaultIfEmpty(0)
                        .Max();
                    attemptIndex = Math.Max(attemptIndex, maxExistingAttempt + 1);
                }
            }

            conversation = await _taskRepository.StartConversationAsync(
                persistedTaskId,
                context.StageId,
                WorkflowStageKind.Agentic,
                attemptIndex,
                context.Profile.Id,
                harnessId,
                context.Profile.ModelName,
                workspaceId: context.Workspace?.Id,
                title: context.ForgeTask.Title,
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
        state.ChatHistory = RestoreChatHistory(context, effectiveSerializedStateJson, _logger);
        SeedRecordedToolCallsFromHistory(state);

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
            ApplyFilteredSyntheticPriming(
                state,
                context.SyntheticPriming,
                context.OptimizationFlags ?? context.Profile?.OptimizationFlags,
                context.WorkspacePath);
        }

        if (!string.IsNullOrWhiteSpace(context.PendingHumanAnswer))
        {
            var resumedWaitingConversation = ReferenceEquals(conversation, waitingConversation);
            ApplyPendingHumanAnswer(state, context, resumedWaitingConversation, _logger);
        }

        var sessionId = persistedTaskId.HasValue
            ? LlmSessionIds.ForStage(persistedTaskId.Value, context.StageId)
            : $"task-transient-{Guid.NewGuid():N}-stage-{context.StageId}";
        RouterSessionUsage? baselineUsage = null;
        try
        {
            baselineUsage = await _llmGatewayService.GetSessionUsageAsync(sessionId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture baseline LLM session usage for session '{SessionId}'", sessionId);
        }

        state.SessionId = sessionId;
        state.BaselineUsage = baselineUsage;

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

    internal static List<ChatMessage> RestoreChatHistory(AgentSessionContext context, string? effectiveSerializedStateJson, ILogger logger)
    {
        // The system prompt is NOT part of the history: it is supplied to HarnessAgent as HarnessInstructions
        // (ChatOptions.Instructions), so adding it as a System message too would send it to the model twice.
        var initialPrompt = !string.IsNullOrWhiteSpace(context.BootstrapPrompt) ? context.BootstrapPrompt : DefaultBootstrapPrompt;

        List<ChatMessage> chatHistory;
        if (!string.IsNullOrEmpty(effectiveSerializedStateJson))
        {
            var resolveHostPath = BuildRestoreHostPathResolver(context);
            chatHistory = CrucibleAgentHarness.DeserializeSession(
                effectiveSerializedStateJson,
                resolveHostPath,
                trackUnresolvedRefs: true,
                logger);
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

        CrucibleAgentHarness.StripAttribution(chatHistory);
        return chatHistory;
    }

    internal static Func<string, string?> BuildRestoreHostPathResolver(AgentSessionContext context)
    {
        var sandboxResolver = context.SandboxScope != null ? new SandboxPathResolver(context.SandboxScope) : null;
        return sourcePath =>
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return null;
            }

            if (sandboxResolver != null)
            {
                try
                {
                    var resolved = sandboxResolver.ResolveHostPath(sourcePath, forWrite: false);
                    return !string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved) ? resolved : null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
            }

            if (!string.IsNullOrWhiteSpace(context.WorkspacePath))
            {
                var trimmed = sourcePath.Trim();
                if (Path.IsPathRooted(trimmed) ||
                    trimmed.StartsWith('/') ||
                    trimmed.StartsWith('\\'))
                {
                    return null;
                }

                var fullWorkspace = Path.GetFullPath(context.WorkspacePath);
                var combined = Path.GetFullPath(Path.Combine(fullWorkspace, trimmed.Replace('/', Path.DirectorySeparatorChar)));
                if (SandboxPathResolver.IsPathInsideDirectory(combined, fullWorkspace) && File.Exists(combined))
                {
                    return combined;
                }
            }

            return null;
        };
    }

    internal static void ApplyFilteredSyntheticPriming(
        CrucibleConversationState state,
        SyntheticPrimingExecution priming,
        LocalOptimizationFlags? optimizationFlags,
        string? workspaceRoot)
    {
        var effectivePriming = FilterSyntheticPriming(priming, optimizationFlags, workspaceRoot);
        ApplySyntheticPriming(state, effectivePriming);
    }

    private static SyntheticPrimingExecution FilterSyntheticPriming(
        SyntheticPrimingExecution priming,
        LocalOptimizationFlags? optimizationFlags,
        string? workspaceRoot)
    {
        var output = priming.Output ?? string.Empty;
        bool skipSmartFilter = priming.IsPreFiltered ||
            output.Contains(DeterministicStageConstants.PrimingSectionHeaderPrefix, StringComparison.Ordinal);

        var filtered = NightCodeForge.Core.Workflow.Filtering.CliOutputFilterHelpers.FilterPrimingOutput(
            NightCodeForge.Core.Workflow.Filtering.CliOutputFilterRegistry.Default,
            priming.Command,
            output,
            priming.IsSuccess,
            workspaceRoot ?? string.Empty,
            smartFilteringEnabled: !skipSmartFilter && (optimizationFlags?.SmartCliOutputFiltering ?? true),
            WorkflowDefaults.MaxToolInlineChars);

        return string.Equals(filtered, output, StringComparison.Ordinal)
            ? priming
            : priming with { Output = filtered };
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

    internal static void SeedRecordedToolCallsFromHistory(CrucibleConversationState state)
    {
        foreach (var msg in state.ChatHistory)
        {
            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fcc && !string.IsNullOrEmpty(fcc.CallId))
                {
                    state.TryRecordToolCall(fcc.CallId);
                }
            }
        }
    }

    /// <summary>Completes the pending <c>ask</c> tool call with the human answers as its tool result.</summary>
    internal static void ApplyPendingHumanAnswer(
        CrucibleConversationState state,
        AgentSessionContext context,
        bool resumedWaitingConversation,
        ILogger logger)
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
                    metadataJson: BuildToolResultMetadata(callId, context.PendingHumanAnswerJson, logger));
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

        SessionUsageDelta? usageDelta = null;
        if (!string.IsNullOrWhiteSpace(state.SessionId))
        {
            try
            {
                using var costCts = new CancellationTokenSource(SessionTelemetryTimeout);
                var finalUsage = await _llmGatewayService.GetSessionUsageAsync(state.SessionId, costCts.Token);
                usageDelta = SessionUsageDelta.Compute(finalUsage, state.BaselineUsage);
                state.LastUsageDelta = usageDelta;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to capture final LLM session usage for session '{SessionId}'", state.SessionId);
            }

            if (usageDelta == null && (request.PromptTokens > 0 || request.CompletionTokens > 0))
            {
                usageDelta = new SessionUsageDelta(
                    InputTokens: (int)Math.Min(int.MaxValue, request.PromptTokens),
                    OutputTokens: (int)Math.Min(int.MaxValue, request.CompletionTokens),
                    CachedInputTokens: 0,
                    Cost: null,
                    ActualCloudCost: null,
                    ElectricityCost: null,
                    CloudSavings: null,
                    Currency: null);
            }
            else if (usageDelta != null && (request.PromptTokens > usageDelta.InputTokens || request.CompletionTokens > usageDelta.OutputTokens))
            {
                usageDelta = usageDelta with
                {
                    InputTokens = (int)Math.Min(int.MaxValue, Math.Max(request.PromptTokens, usageDelta.InputTokens)),
                    OutputTokens = (int)Math.Min(int.MaxValue, Math.Max(request.CompletionTokens, usageDelta.OutputTokens))
                };
            }

            if (usageDelta != null)
            {
                try
                {
                    await _taskRepository.UpdateConversationUsageAsync(state.Conversation.Id, usageDelta, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to accumulate conversation usage delta for conversation #{ConversationId}", state.Conversation.Id);
                }
            }
        }

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
            var entries = BuildTranscriptEntries(
                request.ChatHistory,
                trackedTurns: state.GetTrackedTurnsSnapshot(),
                tracedEntries: tracingClient?.CapturedEntries,
                stageStartedAtUtc: state.Conversation.StartedAt != default ? state.Conversation.StartedAt : null);

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

            long effectivePrompt = Math.Max(request.PromptTokens, usageDelta?.InputTokens ?? 0);
            long effectiveCompletion = Math.Max(request.CompletionTokens, usageDelta?.OutputTokens ?? 0);
            var transcriptMetrics = new AgentRunMetrics(
                DurationMs: request.ElapsedMs,
                PromptTokens: effectivePrompt,
                CompletionTokens: effectiveCompletion,
                TotalTokens: effectivePrompt + effectiveCompletion,
                CachedInputTokens: usageDelta?.CachedInputTokens,
                Cost: usageDelta?.Cost,
                ActualCloudCost: usageDelta?.ActualCloudCost,
                ElectricityCost: usageDelta?.ElectricityCost,
                CloudSavings: usageDelta?.CloudSavings,
                Currency: usageDelta?.Currency,
                InferenceDurationMs: ComputeTotalInferenceDurationMs(tracingClient, harnessOptions));

            var meta = new TranscriptMetadata(
                context.ForgeTask.Id,
                context.StageId,
                context.Profile.Name,
                context.Profile.ModelName,
                request.ElapsedMs,
                transcriptMetrics,
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

        if (!string.IsNullOrWhiteSpace(state.SessionId))
        {
            await ReleaseSessionAffinityAsync(state.SessionId, CancellationToken.None);
        }

        return transcriptFileName;
    }

    /// <summary>
    /// Releases sticky router session affinity for the specified session (e.g. on stage completion or after emergency context compaction).
    /// </summary>
    public async Task ReleaseSessionAffinityAsync(string? sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(SessionTelemetryTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await _llmGatewayService.ReleaseSessionAffinityAsync(sessionId, linkedCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release LLM session affinity for session '{SessionId}'", sessionId);
        }
    }

    public static List<TranscriptEntry> BuildTranscriptEntries(
        IReadOnlyList<ChatMessage> chatHistory,
        IReadOnlyList<ConversationTurn>? trackedTurns = null,
        IReadOnlyList<TranscriptEntry>? tracedEntries = null,
        DateTime? stageStartedAtUtc = null)
    {
        var entries = new List<TranscriptEntry>();
        var timeline = TranscriptTimelineResolver.Create(chatHistory, trackedTurns, tracedEntries, stageStartedAtUtc);
        // Tool result messages only carry the call id: resolve it back to the name of the tool that was called.
        var toolNamesByCallId = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int msgIndex = 0; msgIndex < chatHistory.Count; msgIndex++)
        {
            var msg = chatHistory[msgIndex];
            var role = msg.Role.Value;
            var text = msg.Text ?? string.Empty;
            var contents = msg.Contents ?? Array.Empty<AIContent>();
            var messageCreatedAtUtc = ExtractValidMessageCreatedAtUtc(msg);

            if (IsCompactionMessageText(text))
            {
                role = CompactionRole;
            }

            // Structured reasoning (TextReasoningContent) is not part of msg.Text; emit it as its own step.
            if (msg.Role == ChatRole.Assistant)
            {
                var reasoning = string.Concat(contents.OfType<TextReasoningContent>().Select(r => r.Text));
                if (!string.IsNullOrWhiteSpace(reasoning))
                {
                    var thinkingTimestamp = timeline.NextTimestamp(
                        TranscriptRoleKind.Thinking,
                        callId: null,
                        content: reasoning,
                        messageCreatedAtUtc,
                        chatHistory,
                        msgIndex);
                    entries.Add(new TranscriptEntry(thinkingTimestamp, ThinkingTranscriptRole, reasoning));
                }
            }

            var functionCalls = contents.OfType<FunctionCallContent>().ToList();
            var functionResults = contents.OfType<FunctionResultContent>().ToList();

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

                    var toolTimestamp = timeline.NextTimestamp(
                        TranscriptRoleKind.ToolCall,
                        callId: fc.CallId,
                        content: i == 0 ? text : null,
                        messageCreatedAtUtc,
                        chatHistory,
                        msgIndex);

                    if (i == 0 && msg.Role == ChatRole.Assistant && !string.IsNullOrEmpty(text))
                    {
                        timeline.ConsumeOptionalUpTo(TranscriptRoleKind.Assistant, toolTimestamp, text);
                    }

                    entries.Add(new TranscriptEntry(
                        toolTimestamp,
                        role,
                        Content: i == 0 ? text : string.Empty,
                        ToolName: fc.Name,
                        ToolArguments: argsJson,
                        ToolResult: null,
                        CallId: fc.CallId));
                }
            }
            else if (functionResults.Count > 0)
            {
                foreach (var fr in functionResults)
                {
                    var resultStr = fr.Result?.ToString() ?? string.Empty;
                    var resultTimestamp = timeline.NextTimestamp(
                        TranscriptRoleKind.ToolResult,
                        callId: fr.CallId,
                        content: resultStr,
                        messageCreatedAtUtc,
                        chatHistory,
                        msgIndex);

                    entries.Add(new TranscriptEntry(
                        resultTimestamp,
                        role,
                        Content: string.Empty,
                        ToolName: fr.CallId != null && toolNamesByCallId.TryGetValue(fr.CallId, out var calledTool) ? calledTool : fr.CallId,
                        ToolArguments: null,
                        ToolResult: resultStr,
                        CallId: fr.CallId));
                }
            }
            else
            {
                bool hasImages = contents.Any(c => c is DataContent dc && dc.HasTopLevelMediaType(VisionConstants.TopLevelImageMediaType));
                var effectiveContent = hasImages ? ThinkingAndActivityTracingChatClient.DescribeMessage(msg) : text;
                var roleKind = string.Equals(role, CompactionRole, StringComparison.Ordinal)
                    ? TranscriptRoleKind.Compaction
                    : (msg.Role == ChatRole.Assistant
                        ? TranscriptRoleKind.Assistant
                        : (msg.Role == ChatRole.User ? TranscriptRoleKind.User : TranscriptRoleKind.Other));
                var entryTimestamp = timeline.NextTimestamp(
                    roleKind,
                    callId: null,
                    content: effectiveContent,
                    messageCreatedAtUtc,
                    chatHistory,
                    msgIndex);

                entries.Add(new TranscriptEntry(
                    entryTimestamp,
                    role,
                    Content: effectiveContent,
                    ToolName: null,
                    ToolArguments: null,
                    ToolResult: null));
            }
        }

        return entries;
    }

    private static bool IsCompactionMessageText(string? text) =>
        !string.IsNullOrEmpty(text) &&
        (text.StartsWith(PiStyleCrucibleCompactionStrategy.CheckpointPreamble, StringComparison.Ordinal) ||
         text.StartsWith(LegacyCompactionMarker, StringComparison.Ordinal));

    private static DateTime? ExtractValidMessageCreatedAtUtc(ChatMessage msg) =>
        msg.CreatedAt.HasValue && msg.CreatedAt.Value > DateTimeOffset.UnixEpoch
            ? msg.CreatedAt.Value.UtcDateTime
            : null;

    private enum TranscriptRoleKind
    {
        User,
        Thinking,
        Assistant,
        ToolCall,
        ToolResult,
        Compaction,
        Other
    }

    private enum TimelineSource
    {
        TrackedTurns,
        TracedEntries
    }

    private readonly record struct RecordedTurnTimestamp(
        TimelineSource Source,
        int SequenceOrder,
        DateTime TimestampUtc,
        string? Content,
        string? CallId);

    private sealed class RoleTimestampStream(List<RecordedTurnTimestamp> items)
    {
        public List<RecordedTurnTimestamp> Items { get; } = items;
        public int Cursor { get; set; }
    }

    private sealed class TranscriptTimelineResolver
    {
        private readonly Dictionary<string, RecordedTurnTimestamp> _toolCallTimestampsByCallId;
        private readonly Dictionary<string, RecordedTurnTimestamp> _toolResultTimestampsByCallId;
        private readonly RoleTimestampStream _userStream;
        private readonly RoleTimestampStream _thinkingStream;
        private readonly RoleTimestampStream _assistantStream;
        private readonly RoleTimestampStream _toolCallStream;
        private readonly RoleTimestampStream _toolResultStream;
        private readonly RoleTimestampStream _compactionStream;
        private DateTime _lastTimestamp;
        private int _minTrackedSeq = -1;
        private int _minTracedSeq = -1;

        private TranscriptTimelineResolver(
            DateTime baselineUtc,
            Dictionary<string, RecordedTurnTimestamp> toolCallTimestampsByCallId,
            Dictionary<string, RecordedTurnTimestamp> toolResultTimestampsByCallId,
            RoleTimestampStream userStream,
            RoleTimestampStream thinkingStream,
            RoleTimestampStream assistantStream,
            RoleTimestampStream toolCallStream,
            RoleTimestampStream toolResultStream,
            RoleTimestampStream compactionStream)
        {
            _lastTimestamp = baselineUtc;
            _toolCallTimestampsByCallId = toolCallTimestampsByCallId;
            _toolResultTimestampsByCallId = toolResultTimestampsByCallId;
            _userStream = userStream;
            _thinkingStream = thinkingStream;
            _assistantStream = assistantStream;
            _toolCallStream = toolCallStream;
            _toolResultStream = toolResultStream;
            _compactionStream = compactionStream;
        }

        public static TranscriptTimelineResolver Create(
            IReadOnlyList<ChatMessage> chatHistory,
            IReadOnlyList<ConversationTurn>? trackedTurns,
            IReadOnlyList<TranscriptEntry>? tracedEntries,
            DateTime? stageStartedAtUtc)
        {
            var orderedTurns = trackedTurns is { Count: > 0 }
                ? trackedTurns.OrderBy(t => t.TurnIndex).ToList()
                : new List<ConversationTurn>();

            var toolCallByCallId = new Dictionary<string, RecordedTurnTimestamp>(StringComparer.Ordinal);
            var toolResultByCallId = new Dictionary<string, RecordedTurnTimestamp>(StringComparer.Ordinal);
            var userList = new List<RecordedTurnTimestamp>();
            var thinkingList = new List<RecordedTurnTimestamp>();
            var assistantList = new List<RecordedTurnTimestamp>();
            var toolCallList = new List<RecordedTurnTimestamp>();
            var toolResultList = new List<RecordedTurnTimestamp>();
            var compactionList = new List<RecordedTurnTimestamp>();

            int trackedSeq = 0;
            foreach (var turn in orderedTurns)
            {
                if (turn.Timestamp == default)
                {
                    continue;
                }

                var ts = NormalizeUtc(turn.Timestamp);
                int seq = trackedSeq++;
                switch (turn.Role)
                {
                    case StageTurnRole.User:
                        userList.Add(new RecordedTurnTimestamp(TimelineSource.TrackedTurns, seq, ts, turn.Content, null));
                        break;
                    case StageTurnRole.Thinking:
                        thinkingList.Add(new RecordedTurnTimestamp(TimelineSource.TrackedTurns, seq, ts, turn.ThinkingTrace ?? turn.Content, null));
                        break;
                    case StageTurnRole.Assistant:
                        assistantList.Add(new RecordedTurnTimestamp(TimelineSource.TrackedTurns, seq, ts, turn.Content, null));
                        break;
                    case StageTurnRole.Optimization:
                        compactionList.Add(new RecordedTurnTimestamp(TimelineSource.TrackedTurns, seq, ts, turn.Content, null));
                        break;
                    case StageTurnRole.Tool:
                        var toolCallId = ExtractCallId(turn);
                        var toolRecord = new RecordedTurnTimestamp(TimelineSource.TrackedTurns, seq, ts, turn.Content, toolCallId);
                        toolCallList.Add(toolRecord);
                        if (!string.IsNullOrEmpty(toolCallId))
                        {
                            toolCallByCallId.TryAdd(toolCallId, toolRecord);
                        }
                        break;
                    case StageTurnRole.ToolResult:
                        var resultCallId = ExtractCallId(turn);
                        var resultRecord = new RecordedTurnTimestamp(TimelineSource.TrackedTurns, seq, ts, turn.ToolResult ?? turn.Content, resultCallId);
                        toolResultList.Add(resultRecord);
                        if (!string.IsNullOrEmpty(resultCallId))
                        {
                            toolResultByCallId.TryAdd(resultCallId, resultRecord);
                        }
                        break;
                }
            }

            if (tracedEntries is { Count: > 0 })
            {
                var tracedUsers = new List<RecordedTurnTimestamp>();
                var tracedThinking = new List<RecordedTurnTimestamp>();
                var tracedAssistant = new List<RecordedTurnTimestamp>();
                var tracedToolCalls = new List<RecordedTurnTimestamp>();
                var tracedToolResults = new List<RecordedTurnTimestamp>();
                var tracedCompactions = new List<RecordedTurnTimestamp>();

                int tracedSeq = 0;
                foreach (var entry in tracedEntries)
                {
                    if (entry.Timestamp == default)
                    {
                        continue;
                    }

                    var ts = NormalizeUtc(entry.Timestamp);
                    int seq = tracedSeq++;
                    if (string.Equals(entry.Role, UserTranscriptRole, StringComparison.OrdinalIgnoreCase))
                    {
                        tracedUsers.Add(new RecordedTurnTimestamp(TimelineSource.TracedEntries, seq, ts, entry.Content, null));
                    }
                    else if (string.Equals(entry.Role, ThinkingTranscriptRole, StringComparison.OrdinalIgnoreCase))
                    {
                        tracedThinking.Add(new RecordedTurnTimestamp(TimelineSource.TracedEntries, seq, ts, entry.Content, null));
                    }
                    else if (string.Equals(entry.Role, AssistantTranscriptRole, StringComparison.OrdinalIgnoreCase))
                    {
                        tracedAssistant.Add(new RecordedTurnTimestamp(TimelineSource.TracedEntries, seq, ts, entry.Content, null));
                    }
                    else if (string.Equals(entry.Role, OptimizationTranscriptRole, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(entry.Role, CompactionRole, StringComparison.OrdinalIgnoreCase))
                    {
                        tracedCompactions.Add(new RecordedTurnTimestamp(TimelineSource.TracedEntries, seq, ts, entry.Content, null));
                    }
                    else if (string.Equals(entry.Role, ToolTranscriptRole, StringComparison.OrdinalIgnoreCase))
                    {
                        var record = new RecordedTurnTimestamp(TimelineSource.TracedEntries, seq, ts, entry.Content, entry.CallId);
                        tracedToolCalls.Add(record);
                        if (!string.IsNullOrEmpty(entry.CallId))
                        {
                            toolCallByCallId.TryAdd(entry.CallId, record);
                        }
                    }
                    else if (string.Equals(entry.Role, ToolResultTranscriptRole, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(entry.Role, ToolErrorTranscriptRole, StringComparison.OrdinalIgnoreCase))
                    {
                        var record = new RecordedTurnTimestamp(TimelineSource.TracedEntries, seq, ts, entry.ToolResult ?? entry.Content, entry.CallId);
                        tracedToolResults.Add(record);
                        if (!string.IsNullOrEmpty(entry.CallId))
                        {
                            toolResultByCallId.TryAdd(entry.CallId, record);
                        }
                    }
                }

                if (userList.Count == 0 && tracedUsers.Count > 0)
                {
                    userList = tracedUsers;
                }
                if (thinkingList.Count == 0 && tracedThinking.Count > 0)
                {
                    thinkingList = tracedThinking;
                }
                if (assistantList.Count == 0 && tracedAssistant.Count > 0)
                {
                    assistantList = tracedAssistant;
                }
                if (toolCallList.Count == 0 && tracedToolCalls.Count > 0)
                {
                    toolCallList = tracedToolCalls;
                }
                if (toolResultList.Count == 0 && tracedToolResults.Count > 0)
                {
                    toolResultList = tracedToolResults;
                }
                if (compactionList.Count == 0 && tracedCompactions.Count > 0)
                {
                    compactionList = tracedCompactions;
                }
            }

            var baseline = ResolveBaselineUtc(stageStartedAtUtc, chatHistory, orderedTurns, tracedEntries);
            return new TranscriptTimelineResolver(
                baseline,
                toolCallByCallId,
                toolResultByCallId,
                new RoleTimestampStream(userList),
                new RoleTimestampStream(thinkingList),
                new RoleTimestampStream(assistantList),
                new RoleTimestampStream(toolCallList),
                new RoleTimestampStream(toolResultList),
                new RoleTimestampStream(compactionList));
        }

        public void ConsumeOptionalUpTo(TranscriptRoleKind kind, DateTime maxTimestampUtc, string? expectedContent = null)
        {
            var stream = GetStream(kind);
            if (stream == null)
            {
                return;
            }

            AdvancePastPruned(stream);
            if (stream.Cursor < stream.Items.Count)
            {
                var head = stream.Items[stream.Cursor];
                if (head.TimestampUtc <= maxTimestampUtc &&
                    (string.IsNullOrWhiteSpace(head.Content) ||
                     string.IsNullOrWhiteSpace(expectedContent) ||
                     ContentMatches(head.Content, expectedContent)))
                {
                    stream.Cursor++;
                }
            }
        }

        public DateTime NextTimestamp(
            TranscriptRoleKind kind,
            string? callId,
            string? content,
            DateTime? messageCreatedAtUtc,
            IReadOnlyList<ChatMessage> chatHistory,
            int msgIndex)
        {
            DateTime? resolved = null;
            var stream = GetStream(kind);

            // 1. Host-recorded CallId lookup takes strict precedence over remote ChatMessage.CreatedAt.
            if (!string.IsNullOrEmpty(callId))
            {
                if (kind == TranscriptRoleKind.ToolCall && _toolCallTimestampsByCallId.TryGetValue(callId, out var callRecord))
                {
                    resolved = callRecord.TimestampUtc;
                    PruneBefore(callRecord.Source, callRecord.SequenceOrder);
                    ConsumeStreamThroughRecord(stream, callRecord);
                }
                else if (kind == TranscriptRoleKind.ToolResult && _toolResultTimestampsByCallId.TryGetValue(callId, out var resRecord))
                {
                    resolved = resRecord.TimestampUtc;
                    PruneBefore(resRecord.Source, resRecord.SequenceOrder);
                    ConsumeStreamThroughRecord(stream, resRecord);
                }
            }

            // 2. Host-recorded role stream (with content-aware alignment across compacted or unpersisted turns).
            if (!resolved.HasValue && stream != null)
            {
                AdvancePastPruned(stream);
                if (stream.Cursor < stream.Items.Count)
                {
                    var head = stream.Items[stream.Cursor];
                    if (kind == TranscriptRoleKind.User &&
                        !string.IsNullOrWhiteSpace(content) &&
                        !string.IsNullOrWhiteSpace(head.Content) &&
                        !ContentMatches(head.Content, content) &&
                        LaterUserMessageMatches(chatHistory, msgIndex + 1, head.Content))
                    {
                        // Current message is an unpersisted earlier prompt (e.g. DefaultBootstrapPrompt);
                        // keep head reserved for the matching later User turn so we do not steal its timestamp.
                    }
                    else
                    {
                        int matchIndex = stream.Cursor;
                        if (kind is TranscriptRoleKind.Thinking or TranscriptRoleKind.Assistant or TranscriptRoleKind.User or TranscriptRoleKind.Compaction &&
                            !string.IsNullOrWhiteSpace(content) &&
                            !string.IsNullOrWhiteSpace(head.Content) &&
                            !ContentMatches(head.Content, content))
                        {
                            for (int i = stream.Cursor + 1; i < stream.Items.Count; i++)
                            {
                                if (ContentMatches(stream.Items[i].Content, content))
                                {
                                    matchIndex = i;
                                    break;
                                }
                            }
                        }

                        var chosen = stream.Items[matchIndex];
                        stream.Cursor = matchIndex + 1;
                        resolved = chosen.TimestampUtc;
                        if (kind != TranscriptRoleKind.Compaction)
                        {
                            PruneBefore(chosen.Source, chosen.SequenceOrder);
                        }
                    }
                }
            }

            // 3. Fallback to ChatMessage.CreatedAt when no host turn/trace timestamp was recorded.
            resolved ??= messageCreatedAtUtc;

            // 4. Compaction checkpoints sit synthetically before kept-recent messages in chatHistory even though
            //    StageTurnRole.Optimization was persisted after those kept-recent turns executed. Cap the checkpoint's
            //    positional timestamp at the earliest kept turn following it so kept turns retain real timestamps.
            if (kind == TranscriptRoleKind.Compaction && resolved.HasValue && msgIndex + 1 < chatHistory.Count)
            {
                var nextFollowingTs = PeekEarliestFollowingTimestamp(chatHistory, msgIndex + 1);
                if (nextFollowingTs.HasValue && resolved.Value > nextFollowingTs.Value)
                {
                    resolved = nextFollowingTs.Value;
                }
            }

            var effective = resolved ?? _lastTimestamp;
            if (effective < _lastTimestamp)
            {
                effective = _lastTimestamp;
            }

            _lastTimestamp = effective;
            return effective;
        }

        private DateTime? PeekEarliestFollowingTimestamp(IReadOnlyList<ChatMessage> chatHistory, int startIndex)
        {
            for (int idx = startIndex; idx < chatHistory.Count; idx++)
            {
                var msg = chatHistory[idx];
                var contents = msg.Contents ?? Array.Empty<AIContent>();

                if (msg.Role == ChatRole.Assistant)
                {
                    var reasoning = string.Concat(contents.OfType<TextReasoningContent>().Select(r => r.Text));
                    if (!string.IsNullOrWhiteSpace(reasoning))
                    {
                        var thinkingTs = PeekStreamTimestamp(_thinkingStream, reasoning);
                        if (thinkingTs.HasValue)
                        {
                            return thinkingTs.Value;
                        }
                    }
                }

                foreach (var fc in contents.OfType<FunctionCallContent>())
                {
                    if (!string.IsNullOrEmpty(fc.CallId) && _toolCallTimestampsByCallId.TryGetValue(fc.CallId, out var rec))
                    {
                        return rec.TimestampUtc;
                    }
                }

                foreach (var fr in contents.OfType<FunctionResultContent>())
                {
                    if (!string.IsNullOrEmpty(fr.CallId) && _toolResultTimestampsByCallId.TryGetValue(fr.CallId, out var rec))
                    {
                        return rec.TimestampUtc;
                    }
                }

                var text = msg.Text ?? string.Empty;
                if (!IsCompactionMessageText(text))
                {
                    var stream = msg.Role == ChatRole.Assistant
                        ? _assistantStream
                        : (msg.Role == ChatRole.User ? _userStream : null);
                    if (stream != null)
                    {
                        var streamTs = PeekStreamTimestamp(stream, text);
                        if (streamTs.HasValue)
                        {
                            return streamTs.Value;
                        }
                    }
                }

                var createdAt = ExtractValidMessageCreatedAtUtc(msg);
                if (createdAt.HasValue)
                {
                    return createdAt.Value;
                }
            }

            return null;
        }

        private DateTime? PeekStreamTimestamp(RoleTimestampStream stream, string? content)
        {
            int idx = stream.Cursor;
            while (idx < stream.Items.Count && IsPruned(stream.Items[idx]))
            {
                idx++;
            }

            if (idx >= stream.Items.Count)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(content) &&
                !string.IsNullOrWhiteSpace(stream.Items[idx].Content) &&
                !ContentMatches(stream.Items[idx].Content, content))
            {
                for (int i = idx + 1; i < stream.Items.Count; i++)
                {
                    if (!IsPruned(stream.Items[i]) && ContentMatches(stream.Items[i].Content, content))
                    {
                        return stream.Items[i].TimestampUtc;
                    }
                }
            }

            return stream.Items[idx].TimestampUtc;
        }

        private static bool LaterUserMessageMatches(IReadOnlyList<ChatMessage> chatHistory, int startIndex, string expectedContent)
        {
            for (int i = startIndex; i < chatHistory.Count; i++)
            {
                var candidate = chatHistory[i];
                if (candidate.Role == ChatRole.User &&
                    !IsCompactionMessageText(candidate.Text) &&
                    ContentMatches(candidate.Text, expectedContent))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContentMatches(string? a, string? b) =>
            !string.IsNullOrWhiteSpace(a) &&
            !string.IsNullOrWhiteSpace(b) &&
            string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal);

        private void PruneBefore(TimelineSource source, int sequenceOrder)
        {
            if (source == TimelineSource.TrackedTurns)
            {
                if (sequenceOrder > _minTrackedSeq)
                {
                    _minTrackedSeq = sequenceOrder;
                }
            }
            else if (sequenceOrder > _minTracedSeq)
            {
                _minTracedSeq = sequenceOrder;
            }
        }

        private bool IsPruned(RecordedTurnTimestamp item) =>
            item.Source == TimelineSource.TrackedTurns
                ? item.SequenceOrder < _minTrackedSeq
                : item.SequenceOrder < _minTracedSeq;

        private void AdvancePastPruned(RoleTimestampStream stream)
        {
            while (stream.Cursor < stream.Items.Count && IsPruned(stream.Items[stream.Cursor]))
            {
                stream.Cursor++;
            }
        }

        private void ConsumeStreamThroughRecord(RoleTimestampStream? stream, RecordedTurnTimestamp matched)
        {
            if (stream == null)
            {
                return;
            }

            AdvancePastPruned(stream);
            while (stream.Cursor < stream.Items.Count)
            {
                var current = stream.Items[stream.Cursor];
                if (current.Source == matched.Source && current.SequenceOrder <= matched.SequenceOrder)
                {
                    stream.Cursor++;
                    continue;
                }

                if (!string.IsNullOrEmpty(matched.CallId) &&
                    string.Equals(current.CallId, matched.CallId, StringComparison.Ordinal))
                {
                    stream.Cursor++;
                    break;
                }

                if (current.TimestampUtc <= matched.TimestampUtc && string.IsNullOrEmpty(current.CallId))
                {
                    stream.Cursor++;
                    continue;
                }

                break;
            }
        }

        private RoleTimestampStream? GetStream(TranscriptRoleKind kind) => kind switch
        {
            TranscriptRoleKind.User => _userStream,
            TranscriptRoleKind.Thinking => _thinkingStream,
            TranscriptRoleKind.Assistant => _assistantStream,
            TranscriptRoleKind.ToolCall => _toolCallStream,
            TranscriptRoleKind.ToolResult => _toolResultStream,
            TranscriptRoleKind.Compaction => _compactionStream,
            _ => null
        };

        private static DateTime ResolveBaselineUtc(
            DateTime? stageStartedAtUtc,
            IReadOnlyList<ChatMessage> chatHistory,
            IReadOnlyList<ConversationTurn> orderedTurns,
            IReadOnlyList<TranscriptEntry>? tracedEntries)
        {
            if (stageStartedAtUtc.HasValue && stageStartedAtUtc.Value != default)
            {
                return NormalizeUtc(stageStartedAtUtc.Value);
            }

            DateTime? earliest = null;
            foreach (var turn in orderedTurns)
            {
                if (turn.Timestamp != default)
                {
                    var ts = NormalizeUtc(turn.Timestamp);
                    if (!earliest.HasValue || ts < earliest.Value)
                    {
                        earliest = ts;
                    }
                }
            }

            if (tracedEntries != null)
            {
                foreach (var entry in tracedEntries)
                {
                    if (entry.Timestamp != default)
                    {
                        var ts = NormalizeUtc(entry.Timestamp);
                        if (!earliest.HasValue || ts < earliest.Value)
                        {
                            earliest = ts;
                        }
                    }
                }
            }

            foreach (var msg in chatHistory)
            {
                var createdAt = ExtractValidMessageCreatedAtUtc(msg);
                if (createdAt.HasValue && (!earliest.HasValue || createdAt.Value < earliest.Value))
                {
                    earliest = createdAt.Value;
                }
            }

            return earliest ?? DateTime.UtcNow;
        }

        private static DateTime NormalizeUtc(DateTime value) =>
            value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
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

    private static string BuildToolResultMetadata(string callId, string? structuredAnswerJson, ILogger logger)
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
                logger.LogWarning(ex, "Ignoring malformed structured human answer JSON for call {CallId}", callId);
            }
        }
        return JsonSerializer.Serialize(metaDict);
    }
}
