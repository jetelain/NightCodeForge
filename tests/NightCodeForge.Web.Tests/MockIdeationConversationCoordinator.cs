using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Web.Tests;

public class MockIdeationConversationCoordinator : IIdeationConversationCoordinator, IDisposable
{
    private readonly ITaskRepository _taskRepository;
    private readonly object _gate = new();
    private readonly Dictionary<int, ActiveTurnState> _activeTurns = new();
    private readonly Dictionary<int, List<Action<IdeationStreamUpdate>>> _subscribers = new();
    private readonly Dictionary<int, IdeationSessionOptions> _sessionOptions = new();
    private readonly Dictionary<int, (string? Path, string? Markdown)> _transcripts = new();
    private int _activeTurnCount;

    public MockIdeationConversationCoordinator(ITaskRepository? taskRepository = null)
    {
        _taskRepository = taskRepository ?? new InMemoryTaskRepository();
    }

    public List<IdeationStreamUpdate> StreamUpdatesToYield { get; set; } = new();
    public Func<IdeationStreamUpdate, Task>? OnBeforeYieldUpdateAsync { get; set; }
    public Exception? ExceptionToThrow { get; set; }
    public Exception? SynchronousStartException { get; set; }
    public Exception? UpdateOptionsException { get; set; }
    public Func<int, int, IdeationSessionOptions?, CancellationToken, Task>? OnBeforeLoadConversationAsync { get; set; }
    public bool PersistStreamedTurnsToRepository { get; set; } = true;
    public IdeationResult? ResultToReturn { get; set; }

    public List<(int WorkspaceId, int ConversationId)> ReleasedSessions { get; } = new();
    public List<(int WorkspaceId, int ConversationId, int SubscriberCountAtRelease)> ReleaseEvents { get; } = new();
    public List<(int WorkspaceId, int ConversationId, IdeationSessionOptions Options)> UpdatedOptions { get; } = new();
    public List<(int WorkspaceId, int ConversationId)> WarmupCalls { get; } = new();
    public List<int> CancelledConversationIds { get; } = new();
    public List<int> DirtyWorkspaceIds { get; } = new();
    public IdeationSessionOptions? LastOptions { get; private set; }
    public int? LastConversationId { get; private set; }
    public string? LastPrompt { get; private set; }
    public string? LastCustomSystemPrompt => LastOptions?.CustomSystemPrompt;
    public OllamaRoutingTarget? LastRoutingTarget => LastOptions?.RoutingTarget;
    public string? LastModelOverride => LastOptions?.ModelOverride;

    public bool HasActiveTurn => Volatile.Read(ref _activeTurnCount) > 0;
    public event Action<bool>? OnTurnActivityChanged;

    private sealed class ActiveTurnState
    {
        public int WorkspaceId { get; init; }
        public int ConversationId { get; init; }
        public int ActiveUserTurnIndex { get; init; }
        public IdeationSessionOptions Options { get; set; } = new();
        public CancellationTokenSource Cts { get; } = new();
        public StringBuilder ThinkingBuffer { get; } = new();
        public StringBuilder MessageBuffer { get; } = new();
        public List<IdeationInFlightToolCall> ToolCalls { get; } = new();
        public List<IdeationInFlightPart> Parts { get; } = new();
        public List<IdeationProposal> Proposals { get; } = new();
        public Dictionary<Guid, IReadOnlyList<IdeationQuestion>> PendingQuestionGroups { get; } = new();
        public Queue<string?> SubmittedQuestionMetadata { get; } = new();
        public Task? RunningTask { get; set; }
    }

    public Task<IReadOnlyList<Conversation>> GetWorkspaceConversationsAsync(
        int workspaceId,
        CancellationToken cancellationToken = default) =>
        _taskRepository.GetIdeationConversationsAsync(workspaceId, cancellationToken);

    public async Task<IdeationSessionHandle> GetOrCreateActiveConversationAsync(
        int workspaceId,
        IdeationSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var conv = await _taskRepository.GetActiveIdeationConversationAsync(workspaceId, cancellationToken).ConfigureAwait(false)
            ?? await _taskRepository.StartIdeationConversationAsync(workspaceId, title: "Discovery Chat", cancellationToken: cancellationToken).ConfigureAwait(false);
        return CreateSessionHandle(conv, options);
    }

    public async Task<IdeationSessionHandle?> LoadConversationAsync(
        int workspaceId,
        int conversationId,
        IdeationSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (OnBeforeLoadConversationAsync != null)
        {
            await OnBeforeLoadConversationAsync(workspaceId, conversationId, options, cancellationToken).ConfigureAwait(false);
        }

        var conv = await _taskRepository.GetConversationWithTurnsAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (conv == null || conv.WorkspaceId != workspaceId)
        {
            return null;
        }

        return CreateSessionHandle(conv, options);
    }

    public async Task<IdeationSessionHandle> StartConversationAsync(
        int workspaceId,
        string? title = null,
        int? archivePreviousConversationId = null,
        string? archiveSummary = null,
        IdeationSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (archivePreviousConversationId is int prevId && prevId > 0)
        {
            await ReleaseConversationSessionAsync(workspaceId, prevId, cancelIfActive: true, cancellationToken).ConfigureAwait(false);
            await _taskRepository.CompleteConversationAsync(prevId, StageExecutionStatus.Completed, summary: archiveSummary, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var conv = await _taskRepository.StartIdeationConversationAsync(workspaceId, title: title ?? "Discovery Chat", cancellationToken: cancellationToken).ConfigureAwait(false);
        return CreateSessionHandle(conv, options);
    }

    public Task WarmupSessionAsync(
        int workspaceId,
        int conversationId,
        IdeationSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            WarmupCalls.Add((workspaceId, conversationId));
            if (options != null)
            {
                _sessionOptions[conversationId] = options;
            }
        }
        return Task.CompletedTask;
    }

    public Task UpdateSessionOptionsAsync(
        int workspaceId,
        int conversationId,
        IdeationSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (UpdateOptionsException != null)
        {
            return Task.FromException(UpdateOptionsException);
        }

        lock (_gate)
        {
            LastOptions = options;
            UpdatedOptions.Add((workspaceId, conversationId, options));
            _sessionOptions[conversationId] = options;
            if (_activeTurns.TryGetValue(conversationId, out var state))
            {
                state.Options = options;
            }
        }
        return Task.CompletedTask;
    }

    public IdeationStreamSubscription Subscribe(
        int conversationId,
        Action<IdeationStreamUpdate> onUpdate)
    {
        ArgumentNullException.ThrowIfNull(onUpdate);
        IdeationInFlightTurnSnapshot? snapshot;
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(conversationId, out var list))
            {
                list = new List<Action<IdeationStreamUpdate>>();
                _subscribers[conversationId] = list;
            }
            list.Add(onUpdate);
            snapshot = CaptureSnapshotLocked(conversationId);
        }

        return new IdeationStreamSubscription(snapshot, () =>
        {
            lock (_gate)
            {
                if (_subscribers.TryGetValue(conversationId, out var list))
                {
                    list.Remove(onUpdate);
                }
            }
        });
    }

    public IdeationInFlightTurnSnapshot? GetInFlightSnapshot(int conversationId)
    {
        lock (_gate)
        {
            return CaptureSnapshotLocked(conversationId);
        }
    }

    public bool IsConversationGenerating(int conversationId)
    {
        lock (_gate)
        {
            return _activeTurns.ContainsKey(conversationId);
        }
    }

    public async Task<IdeationResult> SendMessageAsync(
        IdeationSendRequest request,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<IdeationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = Subscribe(request.ConversationId, upd =>
        {
            if (upd.Kind == IdeationStreamUpdateKind.Completed && upd.FinalResult != null)
            {
                tcs.TrySetResult(upd.FinalResult);
            }
            else if (upd.Kind == IdeationStreamUpdateKind.Error)
            {
                tcs.TrySetException(new InvalidOperationException(upd.ErrorMessage ?? "Mock error"));
            }
        });

        if (!await StartTurnAsync(request, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"A turn is already active on conversation #{request.ConversationId}.");
        }
        return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> StartTurnAsync(
        IdeationSendRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (SynchronousStartException != null)
        {
            throw SynchronousStartException;
        }

        lock (_gate)
        {
            if (_activeTurns.ContainsKey(request.ConversationId))
            {
                return false;
            }
        }

        var userTurn = await _taskRepository.AddConversationTurnAsync(
            request.ConversationId,
            StageTurnRole.User,
            content: request.Prompt,
            metadataJson: request.UserTurnMetadataJson,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var state = new ActiveTurnState
        {
            WorkspaceId = request.WorkspaceId,
            ConversationId = request.ConversationId,
            ActiveUserTurnIndex = userTurn.TurnIndex,
            Options = request.Options ?? _sessionOptions.GetValueOrDefault(request.ConversationId) ?? new IdeationSessionOptions()
        };

        lock (_gate)
        {
            if (_activeTurns.ContainsKey(request.ConversationId))
            {
                return false;
            }
            LastConversationId = request.ConversationId;
            LastPrompt = request.Prompt;
            LastOptions = request.Options;
            if (request.Options != null)
            {
                _sessionOptions[request.ConversationId] = request.Options;
            }
            _activeTurns[request.ConversationId] = state;
        }

        if (Interlocked.Increment(ref _activeTurnCount) == 1)
        {
            OnTurnActivityChanged?.Invoke(true);
        }

        var snapshotUpdates = StreamUpdatesToYield.ToList();
        state.RunningTask = Task.Run(() => RunTurnAsync(state, snapshotUpdates));
        return true;
    }

    public Task<bool> SubmitQuestionAnswersAsync(
        int conversationId,
        Guid questionGroupId,
        IReadOnlyDictionary<int, string> answersByIndex,
        string? formattedAnswer = null,
        string? metadataJson = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IdeationQuestion>? questions = null;
        var resolvedAnswer = !string.IsNullOrWhiteSpace(formattedAnswer)
            ? formattedAnswer
            : string.Join("\n", answersByIndex.OrderBy(kv => kv.Key).Select(kv => kv.Value));

        lock (_gate)
        {
            if (!_activeTurns.TryGetValue(conversationId, out var state))
            {
                return Task.FromResult(false);
            }

            if (questionGroupId != Guid.Empty && state.PendingQuestionGroups.TryGetValue(questionGroupId, out var exact))
            {
                questions = exact;
            }
            else
            {
                questions = state.PendingQuestionGroups.Values.LastOrDefault(g => g.Any(q => !q.IsAnswered));
            }

            if (questions == null)
            {
                return Task.FromResult(false);
            }

            var partIdx = state.Parts.FindLastIndex(p => p.Kind == IdeationInFlightPartKind.Question &&
                (questionGroupId == Guid.Empty || p.QuestionGroupId == questionGroupId));
            if (partIdx >= 0)
            {
                state.Parts[partIdx] = state.Parts[partIdx] with
                {
                    QuestionAnswer = resolvedAnswer,
                    QuestionMetadataJson = metadataJson
                };
            }

            state.SubmittedQuestionMetadata.Enqueue(metadataJson);
        }

        for (int i = 0; i < questions.Count; i++)
        {
            var ans = answersByIndex.TryGetValue(i, out var val) ? val : string.Empty;
            questions[i].Submit(ans);
        }

        return Task.FromResult(true);
    }

    public async Task CancelActiveTurnAsync(
        int conversationId,
        CancellationToken cancellationToken = default)
    {
        ActiveTurnState? state;
        lock (_gate)
        {
            CancelledConversationIds.Add(conversationId);
            _activeTurns.TryGetValue(conversationId, out state);
        }

        if (state != null)
        {
            await state.Cts.CancelAsync().ConfigureAwait(false);
            foreach (var q in state.PendingQuestionGroups.Values.SelectMany(g => g))
            {
                q.Cancel();
            }
        }
    }

    public Task<bool> ReleaseConversationSessionAsync(
        int workspaceId,
        int conversationId,
        bool cancelIfActive = false,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ReleasedSessions.Add((workspaceId, conversationId));
            var subCount = _subscribers.TryGetValue(conversationId, out var subs) ? subs.Count : 0;
            ReleaseEvents.Add((workspaceId, conversationId, subCount));
        }
        return Task.FromResult(true);
    }

    public void MarkWorkspaceContextDirty(int workspaceId)
    {
        lock (_gate)
        {
            DirtyWorkspaceIds.Add(workspaceId);
        }
    }

    public Task WaitForActiveTurnAsync(int conversationId)
    {
        Task? t;
        lock (_gate)
        {
            t = _activeTurns.TryGetValue(conversationId, out var s) ? s.RunningTask : null;
        }
        return t ?? Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var state in _activeTurns.Values)
            {
                state.Cts.Cancel();
            }
            _activeTurns.Clear();
        }
    }

    private async Task RunTurnAsync(ActiveTurnState state, List<IdeationStreamUpdate> updates)
    {
        var ct = state.Cts.Token;
        bool finalized = false;

        try
        {
            if (ExceptionToThrow != null)
            {
                throw ExceptionToThrow;
            }

            foreach (var rawUpdate in updates)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (OnBeforeYieldUpdateAsync != null)
                {
                    await OnBeforeYieldUpdateAsync(rawUpdate).ConfigureAwait(false);
                }

                var update = rawUpdate with { ConversationId = state.ConversationId };

                if (update.Kind == IdeationStreamUpdateKind.QuestionAsked)
                {
                    var groupId = update.QuestionGroupId ?? Guid.NewGuid();
                    update = update with { QuestionGroupId = groupId };
                    var questions = update.Questions ?? (update.Question != null ? new[] { update.Question } : Array.Empty<IdeationQuestion>());

                    MutateAndBroadcast(state, () =>
                    {
                        if (questions.Count > 0)
                        {
                            state.PendingQuestionGroups[groupId] = questions;
                            state.Parts.Add(new IdeationInFlightPart(
                                IdeationInFlightPartKind.Question,
                                QuestionGroupId: groupId,
                                Questions: questions));
                        }
                    }, update);

                    if (questions.Count > 0)
                    {
                        using var reg = ct.Register(() =>
                        {
                            foreach (var q in questions)
                            {
                                q.Cancel();
                            }
                        });
                        try
                        {
                            await Task.WhenAll(questions.Select(q => q.AnswerTask)).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }

                    continue;
                }

                if (update.Kind == IdeationStreamUpdateKind.Completed && update.FinalResult != null)
                {
                    await PersistAssistantTurnIfEnabledAsync(state, update.FinalResult).ConfigureAwait(false);
                    CompleteTurnAndBroadcast(state, update);
                    finalized = true;
                    break;
                }

                if (update.Kind == IdeationStreamUpdateKind.Error)
                {
                    CompleteTurnAndBroadcast(state, update);
                    finalized = true;
                    break;
                }

                MutateAndBroadcast(state, () => ApplyUpdateToStateLocked(state, update), update);
                await Task.Yield();
            }

            if (!finalized)
            {
                if (ct.IsCancellationRequested)
                {
                    var cancelled = new IdeationResult(
                        state.MessageBuffer.ToString(),
                        state.Proposals.ToList(),
                        state.ToolCalls.Select(t => t.Info).ToList(),
                        IsCancelled: true);
                    CompleteTurnAndBroadcast(state, new IdeationStreamUpdate(
                        IdeationStreamUpdateKind.Completed,
                        FinalResult: cancelled,
                        ConversationId: state.ConversationId));
                }
                else
                {
                    var finalResult = new IdeationResult(
                        state.MessageBuffer.Length > 0
                            ? state.MessageBuffer.ToString()
                            : (ResultToReturn?.AssistantMessage ?? string.Empty),
                        state.Proposals.ToList(),
                        state.ToolCalls.Select(t => t.Info).ToList());
                    await PersistAssistantTurnIfEnabledAsync(state, finalResult).ConfigureAwait(false);
                    CompleteTurnAndBroadcast(state, new IdeationStreamUpdate(
                        IdeationStreamUpdateKind.Completed,
                        FinalResult: finalResult,
                        ConversationId: state.ConversationId));
                }
            }
        }
        catch (OperationCanceledException)
        {
            var cancelled = new IdeationResult(state.MessageBuffer.ToString(), state.Proposals.ToList(), IsCancelled: true);
            CompleteTurnAndBroadcast(state, new IdeationStreamUpdate(
                IdeationStreamUpdateKind.Completed,
                FinalResult: cancelled,
                ConversationId: state.ConversationId));
        }
        catch (Exception ex)
        {
            CompleteTurnAndBroadcast(state, new IdeationStreamUpdate(
                IdeationStreamUpdateKind.Error,
                ErrorMessage: ex.Message,
                ConversationId: state.ConversationId));
        }
        finally
        {
            state.Cts.Dispose();
            if (Interlocked.Decrement(ref _activeTurnCount) == 0)
            {
                OnTurnActivityChanged?.Invoke(false);
            }
        }
    }

    private async Task PersistAssistantTurnIfEnabledAsync(ActiveTurnState state, IdeationResult finalResult)
    {
        if (!PersistStreamedTurnsToRepository || finalResult.IsCancelled)
        {
            return;
        }

        foreach (var tc in state.ToolCalls)
        {
            var toolCallMeta = string.IsNullOrEmpty(tc.Info.ToolCallId)
                ? null
                : JsonSerializer.Serialize(new { CallId = tc.Info.ToolCallId });
            await _taskRepository.AddConversationTurnAsync(
                state.ConversationId,
                StageTurnRole.Tool,
                toolName: tc.Info.ToolName,
                toolArguments: tc.Info.Arguments,
                metadataJson: toolCallMeta).ConfigureAwait(false);
            if (tc.IsCompleted)
            {
                string? resultMeta = toolCallMeta;
                if (string.Equals(tc.Info.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase))
                {
                    string? answerMetaJson = null;
                    lock (_gate)
                    {
                        if (state.SubmittedQuestionMetadata.Count > 0)
                        {
                            answerMetaJson = state.SubmittedQuestionMetadata.Dequeue();
                        }
                    }

                    resultMeta = MergeAskMetadata(tc.Info.ToolCallId, answerMetaJson);
                }

                await _taskRepository.AddConversationTurnAsync(
                    state.ConversationId,
                    StageTurnRole.ToolResult,
                    content: tc.Info.Result,
                    toolName: tc.Info.ToolName,
                    toolArguments: tc.Info.Arguments,
                    toolResult: tc.Info.Result,
                    durationMs: tc.Info.DurationMs,
                    isError: tc.Info.IsError,
                    metadataJson: resultMeta).ConfigureAwait(false);
            }
        }

        var proposals = state.Proposals.Count > 0
            ? state.Proposals
            : (finalResult.ProposedTasks?.ToList() ?? new List<IdeationProposal>());
        var metaJson = proposals.Count > 0 ? JsonSerializer.Serialize(proposals) : null;
        var text = state.MessageBuffer.Length > 0 ? state.MessageBuffer.ToString() : finalResult.AssistantMessage;
        var thinking = state.ThinkingBuffer.Length > 0 ? state.ThinkingBuffer.ToString() : null;

        await _taskRepository.AddConversationTurnAsync(
            state.ConversationId,
            StageTurnRole.Assistant,
            content: text,
            thinkingTrace: thinking,
            metadataJson: metaJson).ConfigureAwait(false);
    }

    private static string? MergeAskMetadata(string? callId, string? answerMetaJson)
    {
        if (!string.IsNullOrWhiteSpace(answerMetaJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(answerMetaJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    (doc.RootElement.TryGetProperty("Answers", out var answersEl) ||
                     doc.RootElement.TryGetProperty("answers", out answersEl)))
                {
                    return JsonSerializer.Serialize(new { CallId = callId, Answers = answersEl });
                }

                return answerMetaJson;
            }
            catch (JsonException)
            {
                return answerMetaJson;
            }
        }

        return string.IsNullOrEmpty(callId) ? null : JsonSerializer.Serialize(new { CallId = callId });
    }

    private static void ApplyUpdateToStateLocked(ActiveTurnState state, IdeationStreamUpdate update)
    {
        switch (update.Kind)
        {
            case IdeationStreamUpdateKind.ThinkingDelta when !string.IsNullOrEmpty(update.TextDelta):
                state.ThinkingBuffer.Append(update.TextDelta);
                if (state.Parts.LastOrDefault() is { Kind: IdeationInFlightPartKind.Thinking } lastThink)
                {
                    state.Parts[^1] = lastThink with { Text = (lastThink.Text ?? string.Empty) + update.TextDelta };
                }
                else
                {
                    state.Parts.Add(new IdeationInFlightPart(IdeationInFlightPartKind.Thinking, Text: update.TextDelta));
                }
                break;

            case IdeationStreamUpdateKind.MessageDelta when !string.IsNullOrEmpty(update.TextDelta):
                state.MessageBuffer.Append(update.TextDelta);
                if (state.Parts.LastOrDefault() is { Kind: IdeationInFlightPartKind.Text } lastText)
                {
                    state.Parts[^1] = lastText with { Text = (lastText.Text ?? string.Empty) + update.TextDelta };
                }
                else
                {
                    state.Parts.Add(new IdeationInFlightPart(IdeationInFlightPartKind.Text, Text: update.TextDelta));
                }
                break;

            case IdeationStreamUpdateKind.ToolCallStarted when update.ToolCall != null:
                state.ToolCalls.Add(new IdeationInFlightToolCall(update.ToolCall, IsCompleted: false));
                if (!string.Equals(update.ToolCall.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase))
                {
                    state.Parts.Add(new IdeationInFlightPart(IdeationInFlightPartKind.ToolCall, ToolCall: update.ToolCall, IsToolCompleted: false));
                }
                break;

            case IdeationStreamUpdateKind.ToolCallCompleted when update.ToolCall != null:
                var idx = state.ToolCalls.FindIndex(t => t.Info.ToolCallId == update.ToolCall.ToolCallId);
                if (idx >= 0) state.ToolCalls[idx] = new IdeationInFlightToolCall(update.ToolCall, IsCompleted: true);
                var partIdx = state.Parts.FindIndex(p => p.Kind == IdeationInFlightPartKind.ToolCall && p.ToolCall?.ToolCallId == update.ToolCall.ToolCallId);
                if (partIdx >= 0) state.Parts[partIdx] = state.Parts[partIdx] with { ToolCall = update.ToolCall, IsToolCompleted = true };
                break;

            case IdeationStreamUpdateKind.OptimizationNotice:
                state.Parts.Add(new IdeationInFlightPart(
                    IdeationInFlightPartKind.Optimization,
                    Text: update.TextDelta,
                    PreCompactionTokens: update.PreCompactionTokens,
                    PostCompactionTokens: update.PostCompactionTokens,
                    DurationMs: update.DurationMs));
                break;

            case IdeationStreamUpdateKind.ProposalDiscovered when update.Proposal != null:
                state.Proposals.Add(update.Proposal);
                break;
        }
    }

    private void MutateAndBroadcast(ActiveTurnState state, Action mutateLocked, IdeationStreamUpdate update)
    {
        Action<IdeationStreamUpdate>[] callbacks;
        lock (_gate)
        {
            mutateLocked();
            callbacks = _subscribers.TryGetValue(state.ConversationId, out var list) ? list.ToArray() : Array.Empty<Action<IdeationStreamUpdate>>();
        }

        foreach (var cb in callbacks)
        {
            cb(update);
        }
    }

    private void CompleteTurnAndBroadcast(ActiveTurnState state, IdeationStreamUpdate update)
    {
        Action<IdeationStreamUpdate>[] callbacks;
        lock (_gate)
        {
            if (update.Kind == IdeationStreamUpdateKind.Completed && update.FinalResult != null &&
                (!string.IsNullOrWhiteSpace(update.FinalResult.TranscriptPath) ||
                 !string.IsNullOrWhiteSpace(update.FinalResult.FullTranscriptMarkdown)))
            {
                _transcripts[state.ConversationId] = (update.FinalResult.TranscriptPath, update.FinalResult.FullTranscriptMarkdown);
            }
            _activeTurns.Remove(state.ConversationId);
            callbacks = _subscribers.TryGetValue(state.ConversationId, out var list) ? list.ToArray() : Array.Empty<Action<IdeationStreamUpdate>>();
        }

        foreach (var cb in callbacks)
        {
            cb(update);
        }
    }

    private IdeationSessionHandle CreateSessionHandle(Conversation conv, IdeationSessionOptions? options)
    {
        IdeationSessionOptions resolvedOptions;
        string? transcriptPath = null;
        string? transcriptMarkdown = null;
        IdeationInFlightTurnSnapshot? snapshot;
        lock (_gate)
        {
            if (options != null)
            {
                _sessionOptions[conv.Id] = options;
            }
            resolvedOptions = _sessionOptions.GetValueOrDefault(conv.Id) ?? new IdeationSessionOptions();
            if (_transcripts.TryGetValue(conv.Id, out var t))
            {
                transcriptPath = t.Path;
                transcriptMarkdown = t.Markdown;
            }
            snapshot = CaptureSnapshotLocked(conv.Id);
        }

        return new IdeationSessionHandle(conv, snapshot, resolvedOptions, transcriptPath, transcriptMarkdown);
    }

    private IdeationInFlightTurnSnapshot? CaptureSnapshotLocked(int conversationId)
    {
        if (!_activeTurns.TryGetValue(conversationId, out var state))
        {
            return null;
        }

        _transcripts.TryGetValue(conversationId, out var t);
        return new IdeationInFlightTurnSnapshot(
            state.WorkspaceId,
            state.ConversationId,
            IsGenerating: true,
            state.ActiveUserTurnIndex,
            state.ThinkingBuffer.ToString(),
            state.MessageBuffer.ToString(),
            state.ToolCalls.ToList(),
            state.Parts.ToList(),
            state.Proposals.ToList(),
            LatestTranscriptPath: t.Path,
            LatestFullTranscriptMarkdown: t.Markdown,
            state.Options);
    }
}
