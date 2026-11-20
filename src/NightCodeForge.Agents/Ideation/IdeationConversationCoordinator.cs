using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Compaction;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Crucible.ChatClients;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Streaming;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Ideation;

/// <summary>
/// Singleton coordinator managing interactive Ideation conversations, warm execution contexts,
/// proactive 48k model preload, structured turn persistence, and circuit-independent turn execution.
/// </summary>
public sealed class IdeationConversationCoordinator : IIdeationConversationCoordinator, IDisposable
{
    public const int DefaultMaxTurns = AgentProfile.DefaultMaxTurns;
    public const int IdeationDefaultContextIntentTokens = IIdeationConversationCoordinator.DefaultContextIntentTokens;
    public static readonly TimeSpan DefaultSessionIdleTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultIdleSweepInterval = TimeSpan.FromMinutes(1);

    private const string IdeationAgentId = "ideation-agent";
    private const string IdeationAgentName = "IdeationAgent";
    private const float DefaultTemperature = 0.3f;
    private static readonly TimeSpan SessionTelemetryTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposeStopWaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StreamCancellationGraceTimeout = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentActivityStreamer _activityStreamer;
    private readonly SessionTranscriptService _transcriptService;
    private readonly IOperationsControlService _operationsControlService;
    private readonly IBackgroundTaskRunner _backgroundTaskRunner;
    private readonly ILogger<IdeationConversationCoordinator> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly object _registryGate = new();
    private readonly ConcurrentDictionary<int, WarmIdeationSession> _sessions = new();
    private readonly ConcurrentDictionary<int, List<WarmIdeationSession.SubscriberEntry>> _pendingSubscribers = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _idleSweepTask;
    private readonly Task _activityWatcherTask;
    private int _activeTurnCount;
    private int _disposedFlag;

    private bool IsDisposed => Volatile.Read(ref _disposedFlag) != 0;

    public bool HasActiveTurn => Volatile.Read(ref _activeTurnCount) > 0;

    public event Action<bool>? OnTurnActivityChanged;

    public IdeationConversationCoordinator(
        IServiceScopeFactory scopeFactory,
        IAgentActivityStreamer activityStreamer,
        SessionTranscriptService transcriptService,
        IOperationsControlService operationsControlService,
        IBackgroundTaskRunner backgroundTaskRunner,
        ILogger<IdeationConversationCoordinator> logger,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _activityStreamer = activityStreamer ?? throw new ArgumentNullException(nameof(activityStreamer));
        _transcriptService = transcriptService ?? throw new ArgumentNullException(nameof(transcriptService));
        _operationsControlService = operationsControlService ?? throw new ArgumentNullException(nameof(operationsControlService));
        _backgroundTaskRunner = backgroundTaskRunner ?? throw new ArgumentNullException(nameof(backgroundTaskRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _operationsControlService.OnOperationsStateChanged += HandleOperationsStateChanged;
        _idleSweepTask = RunIdleSweepLoopAsync(_disposeCts.Token);
        _activityWatcherTask = RunWorkspaceActivityWatcherAsync(_disposeCts.Token);
    }

    /// <summary>
    /// Resolves the effective AgentProfile for ideation, prioritizing explicit profile,
    /// then workspace profile named/id 'ideation', or throws AgentProfileNotFoundException if missing.
    /// </summary>
    public static AgentProfile ResolveIdeationProfile(Workspace? workspace, AgentProfile? explicitProfile = null)
    {
        if (explicitProfile != null)
        {
            return explicitProfile;
        }

        var profileFromWorkspace = workspace?.AgentProfiles?.FirstOrDefault(p =>
            string.Equals(p.Id, StandardStageIds.Ideation, StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains(StandardStageIds.Ideation, StringComparison.OrdinalIgnoreCase));

        if (profileFromWorkspace != null)
        {
            return profileFromWorkspace;
        }

        throw new AgentProfileNotFoundException(StandardStageIds.Ideation, workspace?.Id);
    }

    public async Task<IReadOnlyList<Conversation>> GetWorkspaceConversationsAsync(
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        return await taskRepo.GetIdeationConversationsAsync(workspaceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IdeationSessionHandle> GetOrCreateActiveConversationAsync(
        int workspaceId,
        IdeationSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        Conversation? activeConv;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
            activeConv = await taskRepo.GetActiveIdeationConversationAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            if (activeConv == null)
            {
                activeConv = await taskRepo.StartIdeationConversationAsync(
                    workspaceId,
                    title: $"Discovery Chat #{_timeProvider.GetUtcNow():MMdd-HHmm}",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        var session = GetOrCreateSession(
            workspaceId,
            activeConv.Id,
            options,
            reserveTurnSlot: false,
            applyOptionsToExisting: options != null);
        TriggerBackgroundWarmup(session, options);
        return CaptureSessionHandle(session, activeConv);
    }

    public async Task<IdeationSessionHandle?> LoadConversationAsync(
        int workspaceId,
        int conversationId,
        IdeationSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        Conversation? conversation;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
            conversation = await taskRepo.GetConversationWithTurnsAsync(conversationId, cancellationToken).ConfigureAwait(false);
        }

        if (conversation == null || conversation.WorkspaceId != workspaceId)
        {
            return null;
        }

        var session = GetOrCreateSession(
            workspaceId,
            conversation.Id,
            options,
            reserveTurnSlot: false,
            applyOptionsToExisting: options != null);
        if (conversation.Status == StageExecutionStatus.Running)
        {
            TriggerBackgroundWarmup(session, options);
        }

        return CaptureSessionHandle(session, conversation);
    }

    public async Task<IdeationSessionHandle> StartConversationAsync(
        int workspaceId,
        string? title = null,
        int? archivePreviousConversationId = null,
        string? archiveSummary = null,
        IdeationSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (archivePreviousConversationId.HasValue && archivePreviousConversationId.Value > 0)
        {
            await ReleaseConversationSessionAsync(
                workspaceId,
                archivePreviousConversationId.Value,
                cancelIfActive: true,
                cancellationToken).ConfigureAwait(false);

            await using var archiveScope = _scopeFactory.CreateAsyncScope();
            var taskRepo = archiveScope.ServiceProvider.GetRequiredService<ITaskRepository>();
            await taskRepo.CompleteConversationAsync(
                archivePreviousConversationId.Value,
                StageExecutionStatus.Completed,
                summary: archiveSummary,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        Conversation created;
        await using (var createScope = _scopeFactory.CreateAsyncScope())
        {
            var taskRepo = createScope.ServiceProvider.GetRequiredService<ITaskRepository>();
            created = await taskRepo.StartIdeationConversationAsync(
                workspaceId,
                title: string.IsNullOrWhiteSpace(title) ? $"Discovery Chat #{_timeProvider.GetUtcNow():MMdd-HHmm}" : title,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var session = GetOrCreateSession(workspaceId, created.Id, options);
        TriggerBackgroundWarmup(session, options);
        return CaptureSessionHandle(session, created);
    }

    public async Task WarmupSessionAsync(
        int workspaceId,
        int conversationId,
        IdeationSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_operationsControlService.IsPaused)
        {
            return;
        }

        var session = GetOrCreateSession(workspaceId, conversationId, options);
        var warmupTask = EnsureWarmupStarted(session, options);
        await warmupTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateSessionOptionsAsync(
        int workspaceId,
        int conversationId,
        IdeationSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(options);

        var session = GetOrCreateSession(workspaceId, conversationId, options: null);
        bool changed;
        bool isGenerating;
        lock (session.StateGate)
        {
            session.LastAccessedAt = _timeProvider.GetUtcNow();
            changed = !WarmIdeationSession.AreOptionsEquivalent(session.Options, options);
            if (changed)
            {
                session.Options = options;
                session.ConfigGeneration++;
                session.OptionsDirty = true;
            }
            isGenerating = session.IsGenerating;
        }

        if (changed && !isGenerating && !_operationsControlService.IsPaused)
        {
            await EnsureWarmupStarted(session, options).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public IdeationStreamSubscription Subscribe(
        int conversationId,
        Action<IdeationStreamUpdate> onUpdate)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(onUpdate);

        var entry = new WarmIdeationSession.SubscriberEntry(onUpdate);
        IdeationInFlightTurnSnapshot? snapshot;

        lock (_registryGate)
        {
            if (_sessions.TryGetValue(conversationId, out var session))
            {
                lock (session.StateGate)
                {
                    if (!session.IsEvicted)
                    {
                        session.LastAccessedAt = _timeProvider.GetUtcNow();
                        session.PendingEviction = false;
                        session.Subscribers.Add(entry);
                        snapshot = session.CaptureInFlightSnapshotLocked();
                        return new IdeationStreamSubscription(snapshot, () => UnsubscribeEntry(conversationId, entry, session));
                    }
                }
            }

            var pendingList = _pendingSubscribers.GetOrAdd(conversationId, _ => new List<WarmIdeationSession.SubscriberEntry>());
            pendingList.Add(entry);
        }

        return new IdeationStreamSubscription(null, () => UnsubscribeEntry(conversationId, entry, null));
    }

    private void UnsubscribeEntry(
        int conversationId,
        WarmIdeationSession.SubscriberEntry entry,
        WarmIdeationSession? knownSession)
    {
        lock (_registryGate)
        {
            if (knownSession != null)
            {
                lock (knownSession.StateGate)
                {
                    knownSession.Subscribers.Remove(entry);
                }
            }

            if (_sessions.TryGetValue(conversationId, out var activeSession) &&
                !ReferenceEquals(activeSession, knownSession))
            {
                lock (activeSession.StateGate)
                {
                    activeSession.Subscribers.Remove(entry);
                }
            }

            if (_pendingSubscribers.TryGetValue(conversationId, out var pendingList))
            {
                pendingList.Remove(entry);
                if (pendingList.Count == 0)
                {
                    _pendingSubscribers.TryRemove(conversationId, out _);
                }
            }
        }
    }

    public IdeationInFlightTurnSnapshot? GetInFlightSnapshot(int conversationId)
    {
        return _sessions.TryGetValue(conversationId, out var session)
            ? CaptureInFlightSnapshot(session)
            : null;
    }

    public bool IsConversationGenerating(int conversationId)
    {
        if (!_sessions.TryGetValue(conversationId, out var session))
        {
            return false;
        }

        lock (session.StateGate)
        {
            return session.IsGenerating;
        }
    }

    public async Task<IdeationResult> SendMessageAsync(
        IdeationSendRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);

        var session = GetOrCreateSession(request.WorkspaceId, request.ConversationId, request.Options, reserveTurnSlot: true);
        bool lockAcquired = false;
        try
        {
            await session.TurnLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockAcquired = true;

            bool cancelledOrEvicted;
            lock (session.StateGate)
            {
                cancelledOrEvicted = session.CancelRequested || session.IsEvicted;
            }
            if (cancelledOrEvicted)
            {
                throw new OperationCanceledException("Ideation turn was cancelled before starting.");
            }

            if (_operationsControlService.IsPaused)
            {
                throw new OperationsPausedException("Cannot run ideation turn while global operations are paused.");
            }

            ApplyRequestOptionsLocked(session, request.Options);
            var userTurn = await PersistUserTurnAsync(session, request, cancellationToken).ConfigureAwait(false);
            return await ExecuteTurnCoreAsync(
                session,
                request,
                userTurn,
                linkCallerCancellation: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseTurnSlotLocked(session);
            if (lockAcquired)
            {
                ReleaseSemaphoreSafe(session.TurnLock);
            }
            await FinalizePendingEvictionIfIdleAsync(session).ConfigureAwait(false);
        }
    }

    public async Task<bool> StartTurnAsync(
        IdeationSendRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);

        if (_operationsControlService.IsPaused)
        {
            throw new OperationsPausedException("Cannot start ideation turn while global operations are paused.");
        }

        var session = GetOrCreateSession(request.WorkspaceId, request.ConversationId, request.Options, reserveTurnSlot: true);
        bool lockAcquired = false;
        bool inFlightStarted = false;
        bool backgroundStarted = false;
        try
        {
            if (!await session.TurnLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogDebug("Ignoring concurrent StartTurnAsync for conversation #{ConversationId} because a turn is already active.", request.ConversationId);
                return false;
            }
            lockAcquired = true;

            bool cancelledOrEvicted;
            lock (session.StateGate)
            {
                cancelledOrEvicted = session.CancelRequested || session.IsEvicted;
            }
            if (cancelledOrEvicted)
            {
                return false;
            }

            ApplyRequestOptionsLocked(session, request.Options);
            var userTurn = await PersistUserTurnAsync(session, request, cancellationToken).ConfigureAwait(false);

            lock (session.StateGate)
            {
                session.BeginInFlightTurnLocked(userTurn.TurnIndex);
                inFlightStarted = true;
                session.LastAccessedAt = _timeProvider.GetUtcNow();
            }

            _backgroundTaskRunner.RunTracked(
                async _ =>
                {
                    try
                    {
                        await ExecuteTurnCoreAsync(
                            session,
                            request,
                            userTurn,
                            linkCallerCancellation: false,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        ReleaseTurnSlotLocked(session);
                        ReleaseSemaphoreSafe(session.TurnLock);
                        await FinalizePendingEvictionIfIdleAsync(session).ConfigureAwait(false);
                    }
                },
                $"IdeationTurn.Conv{request.ConversationId}");
            backgroundStarted = true;
            return true;
        }
        finally
        {
            if (!backgroundStarted)
            {
                if (inFlightStarted)
                {
                    lock (session.StateGate)
                    {
                        session.EndInFlightTurnLocked();
                    }
                }
                ReleaseTurnSlotLocked(session);
                if (lockAcquired)
                {
                    ReleaseSemaphoreSafe(session.TurnLock);
                }
                await FinalizePendingEvictionIfIdleAsync(session).ConfigureAwait(false);
            }
        }
    }

    public async Task<bool> SubmitQuestionAnswersAsync(
        int conversationId,
        Guid questionGroupId,
        IReadOnlyDictionary<int, string> answersByIndex,
        string? formattedAnswer = null,
        string? metadataJson = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(answersByIndex);

        if (!_sessions.TryGetValue(conversationId, out var session))
        {
            return false;
        }

        IReadOnlyList<IdeationQuestion>? questionsToComplete = null;
        var resolvedFormatted = !string.IsNullOrWhiteSpace(formattedAnswer)
            ? formattedAnswer
            : FormatSingleSubmissionAnswer(answersByIndex);

        lock (session.StateGate)
        {
            session.LastAccessedAt = _timeProvider.GetUtcNow();

            WarmIdeationSession.PendingQuestionGroup? group = null;
            if (questionGroupId != Guid.Empty && session.PendingQuestionGroups.TryGetValue(questionGroupId, out var exact))
            {
                group = exact;
            }
            else
            {
                group = session.PendingQuestionGroups.Values
                    .LastOrDefault(g => !g.IsSubmitted && g.Questions.Any(q => !q.IsAnswered));
            }

            if (group == null || group.IsSubmitted || group.Questions.All(q => q.IsAnswered))
            {
                return false;
            }

            group.IsSubmitted = true;
            group.SubmittedFormattedAnswer = resolvedFormatted;
            group.SubmittedMetadataJson = metadataJson;

            var part = session.CurrentParts.LastOrDefault(p =>
                p.Kind == IdeationInFlightPartKind.Question &&
                p.QuestionGroupId == group.GroupId);
            if (part != null)
            {
                part.QuestionAnswer = resolvedFormatted;
                part.QuestionMetadataJson = metadataJson;
            }

            questionsToComplete = group.Questions;
        }

        for (int i = 0; i < questionsToComplete.Count; i++)
        {
            var answer = answersByIndex.TryGetValue(i, out var val) && !string.IsNullOrWhiteSpace(val)
                ? val.Trim()
                : "(No answer provided)";
            questionsToComplete[i].Submit(answer);
        }

        return true;
    }

    public async Task CancelActiveTurnAsync(
        int conversationId,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(conversationId, out var session))
        {
            return;
        }

        CancellationTokenSource? turnCts;
        List<IdeationQuestion> pendingQuestions;
        lock (session.StateGate)
        {
            if (session.IsGenerating || session.WaitingOrActiveTurns > 0)
            {
                session.CancelRequested = true;
            }
            turnCts = session.ActiveTurnCts;
            pendingQuestions = session.PendingQuestionGroups.Values
                .SelectMany(g => g.Questions)
                .ToList();
        }

        if (turnCts != null)
        {
            try
            {
                await turnCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Turn already completed and disposed its CTS.
            }
        }

        foreach (var q in pendingQuestions)
        {
            q.Cancel();
        }

        await ReleaseRouterAffinityBestEffortAsync(session.RouterSessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ReleaseConversationSessionAsync(
        int workspaceId,
        int conversationId,
        bool cancelIfActive = false,
        CancellationToken cancellationToken = default)
    {
        var routerSessionId = LlmSessionIds.ForIdeation(workspaceId, conversationId);
        if (!_sessions.TryGetValue(conversationId, out var session))
        {
            return await ReleaseRouterAffinityBestEffortAsync(routerSessionId, cancellationToken).ConfigureAwait(false);
        }

        bool isGenerating;
        bool hasSubscribers;
        bool hasWaitingOrActive;
        lock (session.StateGate)
        {
            isGenerating = session.IsGenerating;
            hasSubscribers = session.Subscribers.Count > 0;
            hasWaitingOrActive = session.WaitingOrActiveTurns > 0;

            if (!cancelIfActive && (isGenerating || hasSubscribers || hasWaitingOrActive))
            {
                if ((isGenerating || hasWaitingOrActive) && !hasSubscribers)
                {
                    session.PendingEviction = true;
                }
                return false;
            }
        }

        if (cancelIfActive && (isGenerating || hasWaitingOrActive))
        {
            await CancelActiveTurnAsync(conversationId, cancellationToken).ConfigureAwait(false);
            await session.TurnLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            ReleaseSemaphoreSafe(session.TurnLock);
        }

        return await EvictAndDisposeSessionAsync(
            session,
            cancellationToken,
            force: cancelIfActive,
            preserveSubscribers: false,
            allowWhileActive: cancelIfActive).ConfigureAwait(false);
    }

    public void MarkWorkspaceContextDirty(int workspaceId)
    {
        foreach (var session in _sessions.Values)
        {
            if (session.WorkspaceId == workspaceId)
            {
                lock (session.StateGate)
                {
                    session.ContextDirty = true;
                }
            }
        }
    }

    internal async Task EvictIdleSessionsAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var session in _sessions.Values.ToList())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            bool shouldEvict;
            lock (session.StateGate)
            {
                shouldEvict = !session.IsGenerating
                    && session.WaitingOrActiveTurns == 0
                    && session.Subscribers.Count == 0
                    && (now - session.LastAccessedAt) >= DefaultSessionIdleTimeout;
            }

            if (shouldEvict)
            {
                await EvictAndDisposeSessionAsync(session, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0)
        {
            return;
        }

        _operationsControlService.OnOperationsStateChanged -= HandleOperationsStateChanged;

        try
        {
            await _disposeCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        foreach (var session in _sessions.Values.ToList())
        {
            CancellationTokenSource? cts;
            List<IdeationQuestion> pendingQuestions;
            lock (session.StateGate)
            {
                if (session.IsGenerating || session.WaitingOrActiveTurns > 0)
                {
                    session.CancelRequested = true;
                }
                cts = session.ActiveTurnCts;
                pendingQuestions = session.PendingQuestionGroups.Values.SelectMany(g => g.Questions).ToList();
            }

            foreach (var q in pendingQuestions)
            {
                q.Cancel();
            }

            if (cts != null)
            {
                try
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // Turn already completed
                }
            }
        }

        try
        {
            await Task.WhenAll(_idleSweepTask, _activityWatcherTask)
                .WaitAsync(DisposeStopWaitTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Background loops terminated
        }

        foreach (var session in _sessions.Values.ToList())
        {
            await TryWaitAndReleaseBeforeDisposeAsync(session.TurnLock).ConfigureAwait(false);
            await TryWaitAndReleaseBeforeDisposeAsync(session.SessionInitLock).ConfigureAwait(false);
            await EvictAndDisposeSessionAsync(
                session,
                CancellationToken.None,
                force: true,
                preserveSubscribers: false,
                allowWhileActive: true).ConfigureAwait(false);
            session.DisposeLocks();
        }

        _disposeCts.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private WarmIdeationSession GetOrCreateSession(
        int workspaceId,
        int conversationId,
        IdeationSessionOptions? options,
        bool reserveTurnSlot = false,
        bool applyOptionsToExisting = true)
    {
        WarmIdeationSession session;
        lock (_registryGate)
        {
            if (!_sessions.TryGetValue(conversationId, out session!) || session.IsEvicted)
            {
                session = new WarmIdeationSession(
                    workspaceId,
                    conversationId,
                    new ScopedConversationTurnQueue(conversationId, _scopeFactory, _logger),
                    _timeProvider.GetUtcNow())
                {
                    Options = options ?? new IdeationSessionOptions()
                };
                _sessions[conversationId] = session;
            }

            lock (session.StateGate)
            {
                session.LastAccessedAt = _timeProvider.GetUtcNow();
                session.PendingEviction = false;
                session.PendingForceEviction = false;
                if (reserveTurnSlot)
                {
                    session.WaitingOrActiveTurns++;
                }

                if (_pendingSubscribers.TryRemove(conversationId, out var pendingList))
                {
                    foreach (var sub in pendingList)
                    {
                        if (!session.Subscribers.Contains(sub))
                        {
                            session.Subscribers.Add(sub);
                        }
                    }
                    pendingList.Clear();
                }

                if (applyOptionsToExisting && options != null && !WarmIdeationSession.AreOptionsEquivalent(session.Options, options))
                {
                    session.Options = options;
                    session.ConfigGeneration++;
                    session.OptionsDirty = true;
                }
            }
        }

        return session;
    }

    private static void ReleaseTurnSlotLocked(WarmIdeationSession session)
    {
        lock (session.StateGate)
        {
            if (session.WaitingOrActiveTurns > 0)
            {
                session.WaitingOrActiveTurns--;
            }

            if (session.WaitingOrActiveTurns == 0 && !session.IsGenerating)
            {
                session.CancelRequested = false;
            }
        }
    }

    private void ApplyRequestOptionsLocked(WarmIdeationSession session, IdeationSessionOptions? options)
    {
        if (options == null)
        {
            return;
        }

        lock (session.StateGate)
        {
            session.LastAccessedAt = _timeProvider.GetUtcNow();
            if (!WarmIdeationSession.AreOptionsEquivalent(session.Options, options))
            {
                session.Options = options;
                session.ConfigGeneration++;
                session.OptionsDirty = true;
            }
        }
    }

    private void TriggerBackgroundWarmup(WarmIdeationSession session, IdeationSessionOptions? options)
    {
        if (_operationsControlService.IsPaused || IsDisposed)
        {
            return;
        }

        var warmupTask = EnsureWarmupStarted(session, options);
        if (!warmupTask.IsCompleted || warmupTask.IsFaulted)
        {
            _backgroundTaskRunner.RunTracked(
                async _ => await warmupTask.ConfigureAwait(false),
                $"IdeationWarmup.Conv{session.ConversationId}",
                _disposeCts.Token);
        }
    }

    private Task EnsureWarmupStarted(WarmIdeationSession session, IdeationSessionOptions? options)
    {
        bool hasHistory;
        lock (session.HistoryGate)
        {
            hasHistory = session.ChatHistory.Count > 0;
        }

        TaskCompletionSource tcs;
        int targetGeneration;
        IdeationSessionOptions targetOptions;

        lock (session.StateGate)
        {
            session.LastAccessedAt = _timeProvider.GetUtcNow();
            if (options != null && !WarmIdeationSession.AreOptionsEquivalent(session.Options, options))
            {
                session.Options = options;
                session.ConfigGeneration++;
                session.OptionsDirty = true;
            }

            if (session.HarnessSetup != null && !session.OptionsDirty && (!session.ContextDirty || hasHistory))
            {
                return Task.CompletedTask;
            }

            if (session.IsGenerating || session.IsExecutingInference)
            {
                return session.InFlightWarmupTask ?? Task.CompletedTask;
            }

            if (session.InFlightWarmupTask != null
                && !session.InFlightWarmupTask.IsCompleted
                && session.InFlightWarmupGeneration == session.ConfigGeneration
                && WarmIdeationSession.AreOptionsEquivalent(session.InFlightWarmupOptions, session.Options))
            {
                return session.InFlightWarmupTask;
            }

            targetGeneration = session.ConfigGeneration;
            targetOptions = session.Options;
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Prevent UnobservedTaskException if callers cancel WaitAsync early before warmup finishes.
            _ = tcs.Task.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            session.InFlightWarmupTask = tcs.Task;
            session.InFlightWarmupGeneration = targetGeneration;
            session.InFlightWarmupOptions = targetOptions;
        }

        _ = RunWarmupIntoTcsAsync(tcs, session, targetGeneration, targetOptions, _disposeCts.Token);
        return tcs.Task;
    }

    private async Task RunWarmupIntoTcsAsync(
        TaskCompletionSource tcs,
        WarmIdeationSession session,
        int targetGeneration,
        IdeationSessionOptions targetOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            await WarmupCoreAsync(session, targetGeneration, targetOptions, cancellationToken).ConfigureAwait(false);
            tcs.TrySetResult();
        }
        catch (OperationCanceledException oce)
        {
            tcs.TrySetCanceled(oce.CancellationToken);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    private async Task WarmupCoreAsync(
        WarmIdeationSession session,
        int targetGeneration,
        IdeationSessionOptions targetOptions,
        CancellationToken cancellationToken)
    {
        if (IsWarmupStale(session, targetGeneration))
        {
            return;
        }

        var workspace = await LoadWorkspaceOrThrowAsync(session.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var (effectiveProfile, effectiveFlags) = await ResolveProfileAndFlagsAsync(workspace, targetOptions, cancellationToken).ConfigureAwait(false);

        if (IsWarmupStale(session, targetGeneration))
        {
            return;
        }

        await TriggerModelPreloadIfEligibleAsync(session, effectiveProfile, effectiveFlags).ConfigureAwait(false);

        await session.SessionInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsWarmupStale(session, targetGeneration))
            {
                return;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var workspaceContext = scope.ServiceProvider.GetRequiredService<IAgentWorkspaceContextService>();
            var locations = workspaceContext.ResolveLocations(workspace, taskId: null, workspacePathOverride: null);

            TriggerNonBlockingInitialBacklogSync(session, workspace, locations);

            var permittedTools = ResolvePermittedTools(effectiveProfile);
            bool toolsetMatches = session.ToolsetSession != null
                && session.Locations != null
                && string.Equals(session.Locations.RepoPath, locations.RepoPath, StringComparison.Ordinal)
                && string.Equals(session.Locations.AgentWorkingDir, locations.AgentWorkingDir, StringComparison.Ordinal)
                && session.EffectiveProfile != null
                && session.EffectiveProfile.Mode == effectiveProfile.Mode
                && session.EffectiveProfile.SecurityTier == effectiveProfile.SecurityTier
                && session.PermittedTools.SequenceEqual(permittedTools, StringComparer.OrdinalIgnoreCase);

            var toolsetSession = session.ToolsetSession;
            if (!toolsetMatches)
            {
                var toolsetFactory = scope.ServiceProvider.GetRequiredService<ICanonicalToolsetFactory>();
                toolsetSession = await toolsetFactory.CreateAsync(
                    new CanonicalToolsetRequest(
                        workspace,
                        effectiveProfile,
                        new CanonicalToolScope(
                            locations.RepoPath,
                            WorkspaceId: workspace.Id,
                            IsReadOnly: effectiveProfile.Mode == AgentMode.ReadOnly || effectiveProfile.SecurityTier == SandboxSecurityTier.RestrictedReadOnly,
                            OptimizationFlags: effectiveFlags),
                        permittedTools,
                        Array.Empty<CustomToolConfig>(),
                        new Dictionary<string, string>(),
                        AttachBacklogMutationService: true),
                    cancellationToken).ConfigureAwait(false);
            }

            var promptContext = await workspaceContext.BuildPromptContextAsync(
                workspace,
                locations.RepoPath,
                permittedTools,
                toolsetSession!.SkillsService,
                cancellationToken).ConfigureAwait(false);

            var topLevelSummary = workspaceContext.DescribeTopLevelEntries(workspace, locations.RepoPath);
            var backlogDigest = await workspaceContext.BuildBacklogDigestAsync(workspace.Id, null, cancellationToken).ConfigureAwait(false);
            var attachmentsDigest = await workspaceContext.BuildWorkspaceAttachmentsDigestAsync(workspace.Id, cancellationToken).ConfigureAwait(false);
            var hotspotsDigest = await workspaceContext.BuildFrictionHotspotsDigestAsync(locations.RepoPath, cancellationToken).ConfigureAwait(false);

            var systemPrompt = IdeationSystemPromptBuilder.BuildSystemPrompt(
                workspace,
                effectiveProfile,
                locations,
                topLevelSummary,
                promptContext,
                backlogDigest,
                attachmentsDigest,
                hotspotsDigest,
                targetOptions.CustomSystemPrompt);

            if (!session.HistoryHydrated)
            {
                await HydrateHistoryFromDatabaseLockedAsync(session, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            }

            bool wasColdResumed;
            lock (session.HistoryGate)
            {
                wasColdResumed = session.ChatHistory.Count > 0;
            }

            var agentFactory = scope.ServiceProvider.GetRequiredService<IHarnessAgentFactory>();
            int maxTurns = effectiveProfile.MaxTurns is > 0 ? effectiveProfile.MaxTurns.Value : DefaultMaxTurns;
            var newSetup = await agentFactory.CreateAsync(
                new HarnessAgentRequest(
                    effectiveProfile,
                    session.RouterSessionId,
                    AgentId: IdeationAgentId,
                    AgentName: IdeationAgentName,
                    Instructions: systemPrompt,
                    Tools: toolsetSession.AITools,
                    MaxTurns: maxTurns,
                    CreateTracingClient: baseClient => new ThinkingAndActivityTracingChatClient(baseClient),
                    DecorateTracingClient: tracing => new PerServiceCallChatHistoryPersistingChatClient(
                        tracing,
                        updatedHistory =>
                        {
                            lock (session.HistoryGate)
                            {
                                session.ChatHistory.Clear();
                                session.ChatHistory.AddRange(updatedHistory);
                                CrucibleAgentHarness.StripAttribution(session.ChatHistory);
                            }
                            return Task.CompletedTask;
                        },
                        _logger),
                    DefaultTemperature: DefaultTemperature,
                    Dispatcher: toolsetSession.Dispatcher),
                cancellationToken).ConfigureAwait(false);

            WireCompactionCallbacks(session, newSetup, wasColdResumed);

            if (IsWarmupStale(session, targetGeneration))
            {
                DisposeHarnessSetup(newSetup);
                return;
            }

            HarnessAgentSetup? previousSetup;
            lock (session.StateGate)
            {
                if (session.ConfigGeneration != targetGeneration || session.IsExecutingInference || _operationsControlService.IsPaused || IsDisposed)
                {
                    DisposeHarnessSetup(newSetup);
                    return;
                }

                previousSetup = session.HarnessSetup;
                session.HarnessSetup = newSetup;
                session.ToolsetSession = toolsetSession;
                session.Locations = locations;
                session.EffectiveProfile = effectiveProfile;
                session.OptimizationFlags = effectiveFlags;
                session.SystemPrompt = systemPrompt;
                session.PermittedTools = permittedTools;
                session.ActiveSkills = promptContext.ActiveSkills;
                session.LastBacklogDigest = backlogDigest;
                session.LastAttachmentsDigest = attachmentsDigest;
                session.LastFrictionHotspotsDigest = hotspotsDigest;
                session.OptionsDirty = false;
                session.ContextDirty = false;
            }

            DisposeHarnessSetup(previousSetup);
        }
        finally
        {
            ReleaseSemaphoreSafe(session.SessionInitLock);
        }
    }

    private bool IsWarmupStale(WarmIdeationSession session, int targetGeneration)
    {
        if (IsDisposed || _operationsControlService.IsPaused || !_sessions.ContainsKey(session.ConversationId))
        {
            return true;
        }

        lock (session.StateGate)
        {
            return session.ConfigGeneration != targetGeneration;
        }
    }

    private async Task HydrateHistoryFromDatabaseLockedAsync(
        WarmIdeationSession session,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        int? activeUserTurnIndex;
        lock (session.StateGate)
        {
            activeUserTurnIndex = session.ActiveUserTurnIndex;
        }

        var taskRepo = serviceProvider.GetRequiredService<ITaskRepository>();
        var conv = await taskRepo.GetConversationWithTurnsAsync(session.ConversationId, cancellationToken).ConfigureAwait(false);
        if (conv?.Turns is { Count: > 0 })
        {
            session.TurnQueue.SeedTrackedTurns(conv.Turns);
            var turnsToRestore = activeUserTurnIndex.HasValue
                ? conv.Turns.Where(t => t.TurnIndex < activeUserTurnIndex.Value).ToList()
                : conv.Turns.ToList();
            int? maxRestored = turnsToRestore.Count > 0
                ? turnsToRestore.Max(t => t.TurnIndex)
                : null;

            if (turnsToRestore.Count > 0)
            {
                var serialized = ConversationHistorySerializer.Serialize(turnsToRestore);
                var restored = CrucibleAgentHarness.DeserializeSession(serialized, _logger);
                restored.RemoveAll(m => m.Role == ChatRole.System);
                CrucibleAgentHarness.SanitizeInFlightTurns(restored);

                lock (session.HistoryGate)
                {
                    session.ChatHistory.Clear();
                    session.ChatHistory.AddRange(restored);
                    session.HydratedMaxTurnIndex = maxRestored;
                }
            }
            else
            {
                lock (session.HistoryGate)
                {
                    session.ChatHistory.Clear();
                    session.HydratedMaxTurnIndex = null;
                }
            }
        }

        session.HistoryHydrated = true;
    }

    private void WireCompactionCallbacks(
        WarmIdeationSession session,
        HarnessAgentSetup setup,
        bool wasColdResumed)
    {
        if (setup.Options.CompactionStrategy is not PiStyleCrucibleCompactionStrategy piStrategy)
        {
            return;
        }

        piStrategy.OnCompacting = async (preTokens, msgCount, ct) =>
        {
            await BroadcastActivityAsync(
                session.RouterSessionId,
                0,
                StandardStageIds.Ideation,
                AgentActivityKind.Notice,
                $"Compacting conversation context ({preTokens:N0} estimated tokens, summarizing {msgCount} messages)...",
                ct).ConfigureAwait(false);
        };

        piStrategy.OnCompacted = async (notification, ct) =>
        {
            lock (session.HistoryGate)
            {
                session.ChatHistory.Clear();
                session.ChatHistory.AddRange(notification.IncludedMessages);
                CrucibleAgentHarness.StripAttribution(session.ChatHistory);
            }

            await session.TurnQueue.DrainAsync().ConfigureAwait(false);
            int? cutTurnIndex = CrucibleAgentHarness.ResolveCompactionCutTurnIndex(
                session.TurnQueue.GetTrackedTurnsSnapshot(),
                notification.FirstKeptCallId,
                notification.KeptRecentMessageCount);

            var checkpointMeta = new ConversationHistorySerializer.CompactionCheckpointMetadata(
                IsCompactionCheckpoint: true,
                CutTurnIndex: cutTurnIndex,
                FirstKeptTurnIndex: cutTurnIndex,
                FirstKeptCallId: notification.FirstKeptCallId,
                KeptRecentMessageCount: notification.KeptRecentMessageCount,
                KeptRecentGroupCount: notification.KeptRecentGroupCount,
                SummarizedGroupCount: notification.SummarizedGroupCount,
                SummarizedMessageCount: notification.SummarizedMessageCount,
                PreCompactionTokens: notification.PreCompactionTokens,
                PostCompactionTokens: notification.PostCompactionTokens,
                DurationMs: Math.Round(notification.DurationMs, 1));

            session.TurnQueue.EnqueueTurn(
                StageTurnRole.Optimization,
                $"Failed to record compaction checkpoint turn for ideation conversation #{session.ConversationId}",
                content: notification.SummaryMessage.Text,
                durationMs: (long)Math.Round(notification.DurationMs),
                metadataJson: JsonSerializer.Serialize(checkpointMeta));

            var durationMs = Math.Round(notification.DurationMs, 1);
            MutateInFlightAndBroadcast(
                session,
                () =>
                {
                    if (session.IsGenerating)
                    {
                        session.CurrentParts.Add(new WarmIdeationSession.MutableInFlightPart
                        {
                            Kind = IdeationInFlightPartKind.Optimization,
                            Text = notification.SummaryMessage.Text,
                            PreCompactionTokens = notification.PreCompactionTokens,
                            PostCompactionTokens = notification.PostCompactionTokens,
                            DurationMs = durationMs
                        });
                    }
                },
                new IdeationStreamUpdate(
                    IdeationStreamUpdateKind.OptimizationNotice,
                    TextDelta: notification.SummaryMessage.Text,
                    ConversationId: session.ConversationId,
                    PreCompactionTokens: notification.PreCompactionTokens,
                    PostCompactionTokens: notification.PostCompactionTokens,
                    DurationMs: durationMs));

            await BroadcastActivityAsync(
                session.RouterSessionId,
                0,
                StandardStageIds.Ideation,
                AgentActivityKind.Notice,
                $"Context compaction complete in {notification.DurationMs:F0}ms: {notification.PreCompactionTokens:N0} -> {notification.PostCompactionTokens:N0} tokens (summarized {notification.SummarizedMessageCount} messages across {notification.SummarizedGroupCount} groups).",
                ct).ConfigureAwait(false);
        };

        if (wasColdResumed)
        {
            List<ChatMessage> snapshot;
            lock (session.HistoryGate)
            {
                snapshot = session.ChatHistory.ToList();
            }

            int restoredTokens = ModelCalibratedTokenEstimator.EstimateMessagesTokens(snapshot, setup.EffectiveModelId);
            int coldResumeThreshold = PiStyleCrucibleCompactionStrategy.GetColdResumeThresholdTokens(piStrategy.Options);
            if (restoredTokens >= coldResumeThreshold)
            {
                _logger.LogInformation(
                    "Armed cold-resume context compaction for ideation conversation #{ConversationId} (restored {RestoredTokens} estimated tokens >= {ColdThreshold} ceiling).",
                    session.ConversationId,
                    restoredTokens,
                    coldResumeThreshold);
                piStrategy.ArmColdResumeCompaction();
            }
        }
    }

    private void TriggerNonBlockingInitialBacklogSync(
        WarmIdeationSession session,
        Workspace workspace,
        AgentWorkspaceLocations locations)
    {
        lock (session.StateGate)
        {
            if (session.BackgroundBacklogSyncTriggered)
            {
                return;
            }
            session.BackgroundBacklogSyncTriggered = true;
        }

        _backgroundTaskRunner.RunTracked(
            async ct =>
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var workspaceContext = scope.ServiceProvider.GetRequiredService<IAgentWorkspaceContextService>();
                await workspaceContext.SyncBacklogToDiskAsync(workspace, locations, ct).ConfigureAwait(false);
            },
            $"IdeationInitialBacklogSync.Ws{workspace.Id}.Conv{session.ConversationId}",
            _disposeCts.Token);
    }

    private async Task TriggerModelPreloadIfEligibleAsync(
        WarmIdeationSession session,
        AgentProfile effectiveProfile,
        LocalOptimizationFlags effectiveFlags)
    {
        if (!effectiveFlags.JitModelPreload || _operationsControlService.IsPaused)
        {
            return;
        }

        string? modelName = effectiveProfile.ModelName;
        if (string.IsNullOrWhiteSpace(modelName))
        {
            await using var optScope = _scopeFactory.CreateAsyncScope();
            var llmOptions = optScope.ServiceProvider.GetService<IOptions<LlmProviderOptions>>()?.Value;
            modelName = llmOptions?.DefaultModelId;
        }

        if (string.IsNullOrWhiteSpace(modelName))
        {
            return;
        }

        int contextIntentTokens = effectiveProfile.ContextWindowTokens is > 0
            ? effectiveProfile.ContextWindowTokens.Value
            : IdeationDefaultContextIntentTokens;
        string? target = effectiveProfile.RoutingTarget != OllamaRoutingTarget.Auto
            ? effectiveProfile.RoutingTarget.ToString()
            : null;

        var preloadSignature = $"{modelName.Trim()}|{target ?? "Auto"}|{contextIntentTokens}";
        lock (session.StateGate)
        {
            if (string.Equals(session.LastPreloadedSignature, preloadSignature, StringComparison.Ordinal))
            {
                return;
            }
            session.LastPreloadedSignature = preloadSignature;
        }

        var capturedModel = modelName.Trim();
        var capturedSignature = preloadSignature;
        var preloadOptions = new ModelPreloadOptions(
            KeepAliveSeconds: null,
            ContextIntentTokens: contextIntentTokens,
            Target: target,
            TaskId: null,
            StageId: StandardStageIds.Ideation);

        _backgroundTaskRunner.RunTracked(
            async ct =>
            {
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var preloadService = scope.ServiceProvider.GetRequiredService<ILlmModelPreloadService>();
                    await preloadService.PreloadModelAsync(capturedModel, preloadOptions, ct).ConfigureAwait(false);
                }
                catch (OperationsPausedException ex)
                {
                    _logger.LogDebug(ex, "Skipping ideation model preload for '{Model}' because operations are paused.", capturedModel);
                    ClearLastPreloadedSignatureIfMatch(session, capturedSignature);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ClearLastPreloadedSignatureIfMatch(session, capturedSignature);
                    throw;
                }
            },
            $"IdeationModelPreload.{capturedModel}",
            _disposeCts.Token);
    }

    private static void ClearLastPreloadedSignatureIfMatch(WarmIdeationSession session, string signature)
    {
        lock (session.StateGate)
        {
            if (string.Equals(session.LastPreloadedSignature, signature, StringComparison.Ordinal))
            {
                session.LastPreloadedSignature = null;
            }
        }
    }

    private async Task<Workspace> LoadWorkspaceOrThrowAsync(int workspaceId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var workspaceRepo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        return await workspaceRepo.GetByIdAsync(workspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Workspace #{workspaceId} not found.");
    }

    private async Task<(AgentProfile Profile, LocalOptimizationFlags Flags)> ResolveProfileAndFlagsAsync(
        Workspace workspace,
        IdeationSessionOptions options,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var workspaceRepo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var agentFactory = scope.ServiceProvider.GetRequiredService<IHarnessAgentFactory>();

        AgentProfile? baseProfile = options.ExplicitProfile;
        if (baseProfile == null)
        {
            baseProfile = await workspaceRepo.GetEffectiveAgentProfileAsync(workspace.Id, StandardStageIds.Ideation, cancellationToken).ConfigureAwait(false);
            baseProfile ??= ResolveIdeationProfile(workspace);
        }

        AgentProfile effectiveProfile = baseProfile;
        bool hasTargetOverride = options.RoutingTarget != OllamaRoutingTarget.Auto && options.RoutingTarget != baseProfile.RoutingTarget;
        bool hasModelOverride = !string.IsNullOrWhiteSpace(options.ModelOverride)
            && !string.Equals(options.ModelOverride.Trim(), baseProfile.ModelName, StringComparison.OrdinalIgnoreCase);

        if (hasTargetOverride || hasModelOverride)
        {
            effectiveProfile = new AgentProfile
            {
                Id = baseProfile.Id,
                Name = baseProfile.Name,
                WorkspaceId = baseProfile.WorkspaceId,
                HarnessId = baseProfile.HarnessId,
                ModelName = !string.IsNullOrWhiteSpace(options.ModelOverride) ? options.ModelOverride.Trim() : baseProfile.ModelName,
                Mode = baseProfile.Mode,
                SecurityTier = baseProfile.SecurityTier,
                PermittedTools = baseProfile.PermittedTools,
                OptimizationFlags = baseProfile.OptimizationFlags,
                Temperature = baseProfile.Temperature,
                ThinkingEffort = baseProfile.ThinkingEffort,
                ThinkingTokenBudget = baseProfile.ThinkingTokenBudget,
                MaxTurns = baseProfile.MaxTurns,
                ContextWindowTokens = baseProfile.ContextWindowTokens,
                MaxOutputTokens = baseProfile.MaxOutputTokens,
                SystemPrompt = baseProfile.SystemPrompt,
                RoutingTarget = options.RoutingTarget != OllamaRoutingTarget.Auto ? options.RoutingTarget : baseProfile.RoutingTarget
            };
        }

        var flags = agentFactory.ResolveOptimizationFlags(effectiveProfile, effectiveProfile.OptimizationFlags);
        return (effectiveProfile, flags);
    }

    private async Task<ConversationTurn> PersistUserTurnAsync(
        WarmIdeationSession session,
        IdeationSendRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("Prompt cannot be empty.", nameof(request));
        }

        List<ChatMessage> historySnapshot;
        lock (session.HistoryGate)
        {
            historySnapshot = session.ChatHistory.ToList();
        }

        if (!session.HistoryHydrated && historySnapshot.Count == 0)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
            var conv = await taskRepo.GetConversationWithTurnsAsync(session.ConversationId, cancellationToken).ConfigureAwait(false);
            if (conv?.Turns is { Count: > 0 })
            {
                historySnapshot = conv.Turns
                    .OrderBy(t => t.TurnIndex)
                    .Where(t => t.Role == StageTurnRole.User && !string.IsNullOrEmpty(t.Content))
                    .Select(t => new ChatMessage(ChatRole.User, t.Content!))
                    .ToList();
            }
        }

        var effectiveUserPrompt = IdeationSystemPromptBuilder.FormatUserPromptWithCurrentDate(
            request.Prompt.Trim(),
            historySnapshot,
            _timeProvider);

        return await session.TurnQueue.EnqueueAndAwaitTurnAsync(
            StageTurnRole.User,
            $"Failed to record user turn for ideation conversation #{session.ConversationId}",
            content: effectiveUserPrompt,
            metadataJson: request.UserTurnMetadataJson).ConfigureAwait(false);
    }

    private async Task<IdeationResult> ExecuteTurnCoreAsync(
        WarmIdeationSession session,
        IdeationSendRequest request,
        ConversationTurn prePersistedUserTurn,
        bool linkCallerCancellation,
        CancellationToken callerToken)
    {
        using var turnCts = linkCallerCancellation
            ? CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, callerToken)
            : CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        var turnCt = turnCts.Token;

        bool cancelAlreadyRequested;
        lock (session.StateGate)
        {
            if (!session.IsGenerating)
            {
                session.BeginInFlightTurnLocked(prePersistedUserTurn.TurnIndex);
            }
            session.ActiveTurnCts = turnCts;
            session.LastAccessedAt = _timeProvider.GetUtcNow();
            cancelAlreadyRequested = session.CancelRequested;
        }

        if (cancelAlreadyRequested)
        {
            await turnCts.CancelAsync().ConfigureAwait(false);
        }

        NotifyTurnActivityStarted();

        RouterSessionUsage? baselineUsage = null;
        SessionUsageDelta? usageDelta = null;
        bool deltaCaptured = false;
        ThinkingAndActivityTracingChatClient? tracingClient = null;
        long baselineInputTokens = 0;
        long baselineOutputTokens = 0;
        long baselineSummarizerInputTokens = 0;
        long baselineSummarizerOutputTokens = 0;
        PiStyleCrucibleCompactionStrategy? piStrategy = null;
        DirtyContextRefreshResult contextRefresh = DirtyContextRefreshResult.None;
        bool userMsgAddedToChatHistory = false;
        bool replacedPrePersistedUserMessage = false;
        int historyCountBeforeTurn = 0;
        Stopwatch? activeThinkingStopwatch = null;
        StringBuilder? activeThinkingBlock = null;

        void FlushActiveThinkingBlock()
        {
            if (activeThinkingStopwatch == null || activeThinkingBlock == null)
            {
                return;
            }

            activeThinkingStopwatch.Stop();
            var text = activeThinkingBlock.ToString();
            var durationMs = activeThinkingStopwatch.ElapsedMilliseconds;
            activeThinkingStopwatch = null;
            activeThinkingBlock = null;

            if (!string.IsNullOrEmpty(text))
            {
                session.TurnQueue.EnqueueTurn(
                    StageTurnRole.Thinking,
                    $"Failed to record thinking turn for ideation conversation #{session.ConversationId}",
                    content: text,
                    thinkingTrace: text,
                    durationMs: durationMs);
            }
        }

        try
        {
            turnCt.ThrowIfCancellationRequested();

            if (_operationsControlService.IsPaused)
            {
                throw new OperationsPausedException("Cannot execute ideation turn while global operations are paused.");
            }

            await EnsureWarmSetupReadyForTurnAsync(session, turnCt).ConfigureAwait(false);
            contextRefresh = await RefreshDirtyWorkspaceContextAsync(session, turnCt).ConfigureAwait(false);

            var setup = session.HarnessSetup
                ?? throw new InvalidOperationException($"Warm harness setup missing for ideation conversation #{session.ConversationId}.");
            var toolset = session.ToolsetSession
                ?? throw new InvalidOperationException($"Warm toolset missing for ideation conversation #{session.ConversationId}.");
            var effectiveProfile = session.EffectiveProfile
                ?? throw new InvalidOperationException($"Effective profile missing for ideation conversation #{session.ConversationId}.");
            var locations = session.Locations
                ?? throw new InvalidOperationException($"Workspace locations missing for ideation conversation #{session.ConversationId}.");

            lock (session.StateGate)
            {
                session.IsExecutingInference = true;
            }

            var canonicalTools = toolset.Tools;
            canonicalTools.PendingProposals.Clear();
            canonicalTools.CreatedTaskIds.Clear();
            canonicalTools.ModifiedTaskIds.Clear();
            canonicalTools.RemovedTaskIds.Clear();

            tracingClient = setup.TracingClient;
            piStrategy = setup.Options.CompactionStrategy as PiStyleCrucibleCompactionStrategy;
            int baselineTotalModelCalls = tracingClient.TotalModelCalls;
            baselineInputTokens = tracingClient.TotalMeasuredInputTokens;
            baselineOutputTokens = tracingClient.TotalMeasuredOutputTokens;
            baselineSummarizerInputTokens = piStrategy?.TotalSummarizerInputTokens ?? 0;
            baselineSummarizerOutputTokens = piStrategy?.TotalSummarizerOutputTokens ?? 0;

            var metadata = new Dictionary<string, object?>
            {
                [AgentActivityMetadataKeys.WorkspaceId] = session.WorkspaceId,
                [AgentActivityMetadataKeys.ConversationId] = session.ConversationId,
                [AgentActivityMetadataKeys.AgentProfile] = effectiveProfile.Id,
                [AgentActivityMetadataKeys.ModelKey] = setup.EffectiveModelId ?? effectiveProfile.ModelName
            };

            baselineUsage = await TryGetBaselineUsageAsync(session.RouterSessionId, turnCt).ConfigureAwait(false);
            await BroadcastActivityAsync(
                session.RouterSessionId,
                0,
                StandardStageIds.Ideation,
                AgentActivityKind.StageStarted,
                "Starting ideation discovery",
                turnCt,
                metadata).ConfigureAwait(false);

            var effectiveUserPrompt = prePersistedUserTurn.Content ?? request.Prompt.Trim();
            var llmUserPrompt = string.IsNullOrWhiteSpace(contextRefresh.Delta)
                ? effectiveUserPrompt
                : $"{contextRefresh.Delta}\n\n{effectiveUserPrompt}";

            List<ChatMessage> messagesCopy;
            lock (session.HistoryGate)
            {
                historyCountBeforeTurn = session.ChatHistory.Count;
                var userMsg = new ChatMessage(ChatRole.User, llmUserPrompt);
                if (session.HydratedMaxTurnIndex.HasValue
                    && session.HydratedMaxTurnIndex.Value >= prePersistedUserTurn.TurnIndex
                    && session.ChatHistory.Count > 0
                    && session.ChatHistory[^1].Role == ChatRole.User)
                {
                    session.ChatHistory[^1] = userMsg;
                    replacedPrePersistedUserMessage = true;
                }
                else
                {
                    session.ChatHistory.Add(userMsg);
                }

                session.HydratedMaxTurnIndex = null;
                userMsgAddedToChatHistory = true;
                CrucibleAgentHarness.StripAttribution(session.ChatHistory);
                messagesCopy = session.ChatHistory.ToList();
            }

            var turnStopwatch = Stopwatch.StartNew();
            var transcriptEntries = new List<TranscriptEntry>
            {
                new(_timeProvider.GetUtcNow().UtcDateTime, "USER", effectiveUserPrompt)
            };
            var transcriptBuffer = new TranscriptStreamBuffer(transcriptEntries);
            var sessionProposals = new List<IdeationProposal>();

            var questionChannel = Channel.CreateUnbounded<(Guid GroupId, IReadOnlyList<IdeationQuestion> Questions)>();
            canonicalTools.AskHandler = CreateAskHandler(session, questionChannel);

            var agent = setup.Agent;
            var agentSession = await agent.CreateSessionAsync(turnCt).ConfigureAwait(false);

            var projector = new AgentStreamProjector();
            var fullTextBuilder = new StringBuilder();
            var fullReasoningBuilder = new StringBuilder();
            var allToolCalls = new List<IdeationToolCallInfo>();
            HumanInterruptionException? interruption = null;

            async Task ProcessProjectedEventsAsync(IEnumerable<IdeationStreamUpdate> events)
            {
                foreach (var domainEvt in events)
                {
                    if (domainEvt.Kind == IdeationStreamUpdateKind.ThinkingDelta && !string.IsNullOrEmpty(domainEvt.TextDelta))
                    {
                        if (activeThinkingStopwatch == null)
                        {
                            activeThinkingStopwatch = Stopwatch.StartNew();
                            activeThinkingBlock = new StringBuilder();
                        }

                        activeThinkingBlock!.Append(domainEvt.TextDelta);
                        fullReasoningBuilder.Append(domainEvt.TextDelta);
                        transcriptBuffer.Append("THINKING", domainEvt.TextDelta);

                        var evt = domainEvt with { ConversationId = session.ConversationId };
                        MutateInFlightAndBroadcast(session, () => AppendThinkingDeltaLocked(session, domainEvt.TextDelta), evt);
                        await BroadcastActivityAsync(session.RouterSessionId, 0, StandardStageIds.Ideation, AgentActivityKind.ThinkingDelta, domainEvt.TextDelta, turnCt, metadata).ConfigureAwait(false);
                    }
                    else if (domainEvt.Kind == IdeationStreamUpdateKind.MessageDelta && !string.IsNullOrEmpty(domainEvt.TextDelta))
                    {
                        FlushActiveThinkingBlock();
                        fullTextBuilder.Append(domainEvt.TextDelta);
                        transcriptBuffer.Append("ASSISTANT", domainEvt.TextDelta);

                        var evt = domainEvt with { ConversationId = session.ConversationId };
                        MutateInFlightAndBroadcast(session, () => AppendMessageDeltaLocked(session, domainEvt.TextDelta), evt);
                        await BroadcastActivityAsync(session.RouterSessionId, 0, StandardStageIds.Ideation, AgentActivityKind.MessageDelta, domainEvt.TextDelta, turnCt, metadata).ConfigureAwait(false);
                    }
                    else if (domainEvt.Kind == IdeationStreamUpdateKind.ToolCallStarted && domainEvt.ToolCall != null)
                    {
                        FlushActiveThinkingBlock();
                        allToolCalls.Add(domainEvt.ToolCall);
                        transcriptBuffer.Flush();
                        transcriptEntries.Add(new TranscriptEntry(_timeProvider.GetUtcNow().UtcDateTime, "TOOL", string.Empty, domainEvt.ToolCall.ToolName, domainEvt.ToolCall.Arguments, CallId: domainEvt.ToolCall.ToolCallId));

                        var evt = domainEvt with { ConversationId = session.ConversationId };
                        MutateInFlightAndBroadcast(session, () => RecordToolStartedLocked(session, domainEvt.ToolCall), evt);
                        session.TurnQueue.EnqueueTurn(
                            StageTurnRole.Tool,
                            $"Failed to record Tool turn for ideation conversation #{session.ConversationId}",
                            toolName: domainEvt.ToolCall.ToolName,
                            toolArguments: domainEvt.ToolCall.Arguments,
                            metadataJson: JsonSerializer.Serialize(new { CallId = domainEvt.ToolCall.ToolCallId }));

                        await BroadcastActivityAsync(session.RouterSessionId, 0, StandardStageIds.Ideation, AgentActivityKind.ToolStarted, $"{domainEvt.ToolCall.ToolName}: {domainEvt.ToolCall.Arguments}", turnCt, metadata).ConfigureAwait(false);
                    }
                    else if (domainEvt.Kind == IdeationStreamUpdateKind.ToolCallCompleted && domainEvt.ToolCall != null)
                    {
                        FlushActiveThinkingBlock();
                        var comp = domainEvt.ToolCall;
                        transcriptEntries.Add(new TranscriptEntry(
                            _timeProvider.GetUtcNow().UtcDateTime,
                            comp.IsError ? "TOOL_ERROR" : "TOOL_RESULT",
                            comp.Result ?? string.Empty,
                            comp.ToolName,
                            CallId: comp.ToolCallId));

                        string? turnMeta = null;
                        IdeationStreamUpdate? evt = null;
                        Action<IdeationStreamUpdate>[] callbacks;
                        lock (session.StateGate)
                        {
                            turnMeta = RecordToolCompletedLocked(session, comp);
                            evt = domainEvt with
                            {
                                ConversationId = session.ConversationId,
                                ToolMetadataJson = turnMeta
                            };
                            callbacks = session.GetSubscribersSnapshotLocked();
                        }
                        BroadcastToCallbacks(session.ConversationId, callbacks, evt);
                        session.TurnQueue.EnqueueTurn(
                            StageTurnRole.ToolResult,
                            $"Failed to record ToolResult turn for ideation conversation #{session.ConversationId}",
                            content: comp.Result,
                            toolName: comp.ToolName,
                            toolArguments: comp.Arguments,
                            toolResult: comp.Result,
                            durationMs: comp.DurationMs,
                            isError: comp.IsError,
                            metadataJson: turnMeta);

                        await BroadcastActivityAsync(session.RouterSessionId, 0, StandardStageIds.Ideation, AgentActivityKind.ToolCompleted, comp.Result ?? string.Empty, turnCt, metadata).ConfigureAwait(false);

                        if (comp.IsError)
                        {
                            _logger.LogWarning("Ideation tool '{ToolName}' failed or returned error: {ToolResult}", comp.ToolName, comp.Result);
                        }

                        await SyncPendingProposalsAsync(session, canonicalTools, sessionProposals, metadata, turnCt).ConfigureAwait(false);
                    }
                }
            }

            bool streamingFailed = false;
            IAsyncEnumerator<AgentResponseUpdate>? enumerator = null;
            try
            {
                var stream = agent.RunStreamingAsync(messagesCopy, agentSession, cancellationToken: turnCt);
                enumerator = stream.GetAsyncEnumerator(turnCt);
            }
            catch (Exception ex) when (ex is NotImplementedException or NotSupportedException)
            {
                streamingFailed = true;
            }

            if (!streamingFailed && enumerator != null)
            {
                Task<bool>? pendingMoveTask = null;
                bool timedOutWaitingForMove = false;
                try
                {
                    while (true)
                    {
                        bool hasMore;
                        var moveTask = enumerator.MoveNextAsync().AsTask();
                        pendingMoveTask = moveTask;
                        while (!moveTask.IsCompleted && !turnCt.IsCancellationRequested)
                        {
                            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(turnCt);
                            var waitQuestion = questionChannel.Reader.WaitToReadAsync(waitCts.Token).AsTask();
                            await Task.WhenAny(moveTask, waitQuestion).ConfigureAwait(false);
                            waitCts.Cancel();
                            try
                            {
                                await waitQuestion.ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // Expected when moveTask completes first or turnCt is cancelled.
                            }

                            while (questionChannel.Reader.TryRead(out var pendingBatch))
                            {
                                FlushActiveThinkingBlock();
                                await EmitQuestionAskedAsync(session, pendingBatch.GroupId, pendingBatch.Questions, metadata, turnCt).ConfigureAwait(false);
                            }
                        }

                        try
                        {
                            if (!moveTask.IsCompleted && turnCt.IsCancellationRequested)
                            {
                                try
                                {
                                    hasMore = await moveTask.WaitAsync(StreamCancellationGraceTimeout).ConfigureAwait(false);
                                }
                                catch (TimeoutException)
                                {
                                    timedOutWaitingForMove = true;
                                    turnCt.ThrowIfCancellationRequested();
                                    throw;
                                }
                            }
                            else
                            {
                                hasMore = await moveTask.ConfigureAwait(false);
                            }

                            turnCt.ThrowIfCancellationRequested();
                        }
                        catch (Exception ex) when (ex is NotImplementedException or NotSupportedException)
                        {
                            streamingFailed = true;
                            break;
                        }
                        catch (Exception ex) when (FindInterruption(ex) != null)
                        {
                            interruption = FindInterruption(ex);
                            break;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            await BroadcastActivityAsync(session.RouterSessionId, 0, StandardStageIds.Ideation, AgentActivityKind.Error, HttpErrorDetailHelper.FormatLlmInvocationError(ex), turnCt, metadata).ConfigureAwait(false);
                            throw;
                        }

                        while (questionChannel.Reader.TryRead(out var remainingBatch))
                        {
                            FlushActiveThinkingBlock();
                            await EmitQuestionAskedAsync(session, remainingBatch.GroupId, remainingBatch.Questions, metadata, turnCt).ConfigureAwait(false);
                        }

                        if (!hasMore)
                        {
                            break;
                        }

                        await ProcessProjectedEventsAsync(projector.ProjectUpdate(enumerator.Current)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (!timedOutWaitingForMove && pendingMoveTask is { IsCompleted: false })
                    {
                        try
                        {
                            await pendingMoveTask.WaitAsync(StreamCancellationGraceTimeout).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
                        {
                            // Expected when the stream cancelled or did not unwind within the grace period.
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "In-flight ideation stream MoveNextAsync faulted during cleanup for conversation #{ConversationId}.", session.ConversationId);
                        }
                    }

                    if (pendingMoveTask is { IsCompleted: false })
                    {
                        var orphanedEnumerator = enumerator;
                        var conversationId = session.ConversationId;
                        _ = pendingMoveTask.ContinueWith(
                            async t =>
                            {
                                if (t.IsFaulted)
                                {
                                    _logger.LogDebug(t.Exception, "Abandoned ideation stream MoveNextAsync faulted after timeout for conversation #{ConversationId}.", conversationId);
                                }

                                try
                                {
                                    await orphanedEnumerator.DisposeAsync().ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "Deferred disposal of abandoned ideation stream enumerator failed for conversation #{ConversationId}.", conversationId);
                                }
                            },
                            CancellationToken.None,
                            TaskContinuationOptions.None,
                            TaskScheduler.Default).Unwrap();
                    }
                    else
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }

            if (streamingFailed)
            {
                canonicalTools.AskHandler = null;
                AgentResponse? response = null;
                try
                {
                    response = await agent.RunAsync(messagesCopy, agentSession, cancellationToken: turnCt).ConfigureAwait(false);
                }
                catch (Exception ex) when (FindInterruption(ex) != null)
                {
                    interruption = FindInterruption(ex);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await BroadcastActivityAsync(session.RouterSessionId, 0, StandardStageIds.Ideation, AgentActivityKind.Error, HttpErrorDetailHelper.FormatLlmInvocationError(ex), turnCt, metadata).ConfigureAwait(false);
                    throw;
                }

                if (response != null)
                {
                    foreach (var update in response.ToAgentResponseUpdates())
                    {
                        await ProcessProjectedEventsAsync(projector.ProjectUpdate(update)).ConfigureAwait(false);
                    }
                }
            }

            await ProcessProjectedEventsAsync(projector.Flush()).ConfigureAwait(false);
            FlushActiveThinkingBlock();

            if (interruption != null)
            {
                var prompts = string.Join("\n- ", interruption.Questions.Select(q => q.Prompt));
                var interruptionText = $"\n\n**I need your input before continuing:**\n- {prompts}\n";
                fullTextBuilder.Append(interruptionText);
                transcriptBuffer.Append("ASSISTANT", interruptionText);
                _logger.LogWarning("Ideation ask escaped the live handler; surfaced as chat text: {Prompts}", prompts);
                MutateInFlightAndBroadcast(
                    session,
                    () => AppendMessageDeltaLocked(session, interruptionText),
                    new IdeationStreamUpdate(
                        IdeationStreamUpdateKind.MessageDelta,
                        TextDelta: interruptionText,
                        ConversationId: session.ConversationId));
            }

            await SyncPendingProposalsAsync(session, canonicalTools, sessionProposals, metadata, turnCt).ConfigureAwait(false);

            var fullText = fullTextBuilder.ToString();
            foreach (var p in IdeationSystemPromptBuilder.ParseFallbackProposals(fullText, _logger))
            {
                if (!sessionProposals.Any(existing => string.Equals(existing.Title, p.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    sessionProposals.Add(p);
                    MutateInFlightAndBroadcast(
                        session,
                        () => RecordProposalLocked(session, p),
                        new IdeationStreamUpdate(
                            IdeationStreamUpdateKind.ProposalDiscovered,
                            Proposal: p,
                            ConversationId: session.ConversationId));
                }
            }

            transcriptBuffer.Flush();
            turnStopwatch.Stop();

            long turnMeasuredInput = Math.Max(0, tracingClient.TotalMeasuredInputTokens - baselineInputTokens)
                + Math.Max(0, (piStrategy?.TotalSummarizerInputTokens ?? 0) - baselineSummarizerInputTokens);
            long turnMeasuredOutput = Math.Max(0, tracingClient.TotalMeasuredOutputTokens - baselineOutputTokens)
                + Math.Max(0, (piStrategy?.TotalSummarizerOutputTokens ?? 0) - baselineSummarizerOutputTokens);

            usageDelta = await CaptureAndPersistSessionUsageDeltaAsync(
                session.RouterSessionId,
                baselineUsage,
                session.ConversationId,
                turnMeasuredInput,
                turnMeasuredOutput).ConfigureAwait(false);
            deltaCaptured = true;

            var turnModelCalls = tracingClient.ModelCalls
                .Where(c => c.Index > baselineTotalModelCalls)
                .ToList();

            var (transcriptPath, fullTranscriptMarkdown) = await SaveTurnTranscriptAsync(
                session,
                locations.RepoPath,
                effectiveProfile,
                setup,
                session.ActiveSkills,
                session.PermittedTools,
                turnStopwatch.ElapsedMilliseconds,
                turnMeasuredInput,
                turnMeasuredOutput,
                usageDelta,
                transcriptEntries,
                turnModelCalls,
                AgentRunStatus.Completed,
                turnCt).ConfigureAwait(false);

            var assistantMetadataJson = sessionProposals.Count > 0
                ? JsonSerializer.Serialize(sessionProposals)
                : null;
            var fullReasoning = fullReasoningBuilder.ToString();
            session.TurnQueue.EnqueueTurn(
                StageTurnRole.Assistant,
                $"Failed to record Assistant turn for ideation conversation #{session.ConversationId}",
                content: fullText,
                thinkingTrace: string.IsNullOrEmpty(fullReasoning) ? null : fullReasoning,
                metadataJson: assistantMetadataJson);

            await session.TurnQueue.DrainAsync().ConfigureAwait(false);

            if (usageDelta?.Cost.HasValue == true || usageDelta?.ActualCloudCost.HasValue == true || usageDelta?.ElectricityCost.HasValue == true)
            {
                var totalCost = usageDelta.Cost ?? ((usageDelta.ActualCloudCost ?? 0) + (usageDelta.ElectricityCost ?? 0));
                metadata[AgentActivityMetadataKeys.Cost] = totalCost;
                metadata[AgentActivityMetadataKeys.Currency] = usageDelta.Currency ?? TaskCostSummary.DefaultCurrency;
                metadata[AgentActivityMetadataKeys.ElectricityCost] = usageDelta.ElectricityCost;
                metadata[AgentActivityMetadataKeys.ActualCloudCost] = usageDelta.ActualCloudCost;
                metadata[AgentActivityMetadataKeys.CloudSavings] = usageDelta.CloudSavings;
                metadata[AgentActivityMetadataKeys.CachedInputTokens] = usageDelta.CachedInputTokens;
            }

            var finalResult = new IdeationResult(
                fullText,
                sessionProposals,
                allToolCalls,
                transcriptPath,
                fullTranscriptMarkdown,
                usageDelta,
                IsCancelled: false);

            await BroadcastActivityAsync(
                session.RouterSessionId,
                0,
                StandardStageIds.Ideation,
                AgentActivityKind.StageCompleted,
                $"Ideation completed with {sessionProposals.Count} proposal(s).",
                turnCt,
                metadata).ConfigureAwait(false);

            if (canonicalTools.CreatedTaskIds.Count > 0 ||
                canonicalTools.ModifiedTaskIds.Count > 0 ||
                canonicalTools.RemovedTaskIds.Count > 0 ||
                allToolCalls.Any(t => !t.IsError &&
                    (string.Equals(t.ToolName, CanonicalToolNames.SetTaskPriority, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(t.ToolName, CanonicalToolNames.ReworkPriorities, StringComparison.OrdinalIgnoreCase))))
            {
                await RefreshLastBacklogDigestBestEffortAsync(session, turnCt).ConfigureAwait(false);
            }

            MutateInFlightAndBroadcast(
                session,
                () =>
                {
                    session.LatestTranscriptPath = transcriptPath;
                    session.LatestFullTranscriptMarkdown = fullTranscriptMarkdown;
                    session.IsExecutingInference = false;
                    session.EndInFlightTurnLocked();
                    session.LastAccessedAt = _timeProvider.GetUtcNow();
                },
                new IdeationStreamUpdate(
                    IdeationStreamUpdateKind.Completed,
                    FinalResult: finalResult,
                    ConversationId: session.ConversationId));

            return finalResult;
        }
        catch (OperationCanceledException)
        {
            CancelPendingQuestionsAndSanitizeHistory(session);
            session.TurnQueue.EnqueueSanitizeTrailingInterruptedTurns(
                $"Failed to sanitize trailing interrupted turns for ideation conversation #{session.ConversationId}");
            FlushActiveThinkingBlock();

            string partialMessage;
            string partialThinking;
            List<IdeationToolCallInfo> partialTools;
            List<IdeationProposal> partialProposals;
            lock (session.StateGate)
            {
                partialMessage = session.CurrentMessageBuffer.ToString();
                partialThinking = session.CurrentThinkingBuffer.ToString();
                partialTools = session.CurrentToolCalls.Select(t => t.Info).ToList();
                partialProposals = session.DiscoveredProposals.ToList();
            }

            var stoppedContent = string.IsNullOrEmpty(partialMessage)
                ? IIdeationConversationCoordinator.CancelledAssistantNotice.TrimStart()
                : partialMessage + IIdeationConversationCoordinator.CancelledAssistantNotice;
            var cancelledMetadataJson = partialProposals.Count > 0
                ? JsonSerializer.Serialize(partialProposals)
                : null;

            session.TurnQueue.EnqueueTurn(
                StageTurnRole.Assistant,
                $"Failed to record cancelled Assistant turn for ideation conversation #{session.ConversationId}",
                content: stoppedContent,
                thinkingTrace: string.IsNullOrEmpty(partialThinking) ? null : partialThinking,
                metadataJson: cancelledMetadataJson);
            partialMessage = stoppedContent;

            lock (session.HistoryGate)
            {
                if (!userMsgAddedToChatHistory)
                {
                    session.ChatHistory.Add(new ChatMessage(ChatRole.User, prePersistedUserTurn.Content ?? request.Prompt.Trim()));
                }
                session.ChatHistory.Add(new ChatMessage(ChatRole.Assistant, stoppedContent));
                CrucibleAgentHarness.StripAttribution(session.ChatHistory);
            }

            await session.TurnQueue.DrainAsync().ConfigureAwait(false);

            if (!deltaCaptured)
            {
                long turnMeasuredInput = tracingClient != null
                    ? Math.Max(0, tracingClient.TotalMeasuredInputTokens - baselineInputTokens)
                      + Math.Max(0, (piStrategy?.TotalSummarizerInputTokens ?? 0) - baselineSummarizerInputTokens)
                    : 0;
                long turnMeasuredOutput = tracingClient != null
                    ? Math.Max(0, tracingClient.TotalMeasuredOutputTokens - baselineOutputTokens)
                      + Math.Max(0, (piStrategy?.TotalSummarizerOutputTokens ?? 0) - baselineSummarizerOutputTokens)
                    : 0;

                usageDelta = await CaptureAndPersistSessionUsageDeltaAsync(
                    session.RouterSessionId,
                    baselineUsage,
                    session.ConversationId,
                    turnMeasuredInput,
                    turnMeasuredOutput).ConfigureAwait(false);
                deltaCaptured = true;
            }

            var cancelledResult = new IdeationResult(
                partialMessage,
                partialProposals,
                partialTools,
                UsageDelta: usageDelta,
                IsCancelled: true);

            MutateInFlightAndBroadcast(
                session,
                () =>
                {
                    session.IsExecutingInference = false;
                    session.EndInFlightTurnLocked();
                    session.LastAccessedAt = _timeProvider.GetUtcNow();
                },
                new IdeationStreamUpdate(
                    IdeationStreamUpdateKind.Completed,
                    FinalResult: cancelledResult,
                    ConversationId: session.ConversationId));

            if (linkCallerCancellation)
            {
                throw;
            }

            return cancelledResult;
        }
        catch (Exception ex)
        {
            CancelPendingQuestionsAndSanitizeHistory(session);

            bool anyCompletedTools;
            lock (session.StateGate)
            {
                anyCompletedTools = session.CurrentToolCalls.Any(t => t.IsCompleted);
                if (!anyCompletedTools && contextRefresh.RestorableDigestsCaptured)
                {
                    session.LastBacklogDigest = contextRefresh.PreviousBacklogDigest;
                    session.LastAttachmentsDigest = contextRefresh.PreviousAttachmentsDigest;
                    session.LastFrictionHotspotsDigest = contextRefresh.PreviousHotspotsDigest;
                    session.ContextDirty = true;
                }
            }

            if (!anyCompletedTools)
            {
                if (userMsgAddedToChatHistory)
                {
                    lock (session.HistoryGate)
                    {
                        if (replacedPrePersistedUserMessage && historyCountBeforeTurn > 0 && session.ChatHistory.Count >= historyCountBeforeTurn)
                        {
                            session.ChatHistory.RemoveRange(historyCountBeforeTurn - 1, session.ChatHistory.Count - (historyCountBeforeTurn - 1));
                            session.HydratedMaxTurnIndex = null;
                        }
                        else if (session.ChatHistory.Count > historyCountBeforeTurn)
                        {
                            session.ChatHistory.RemoveRange(historyCountBeforeTurn, session.ChatHistory.Count - historyCountBeforeTurn);
                        }
                    }
                }

                session.TurnQueue.EnqueueSanitizeTrailingInterruptedTurns(
                    $"Failed to sanitize trailing interrupted turns after error for ideation conversation #{session.ConversationId}",
                    removeTrailingUserTurnIndex: prePersistedUserTurn.TurnIndex);
            }
            else
            {
                string partialMessage;
                string partialThinking;
                List<IdeationProposal> partialProposals;
                lock (session.StateGate)
                {
                    partialMessage = session.CurrentMessageBuffer.ToString();
                    partialThinking = session.CurrentThinkingBuffer.ToString();
                    partialProposals = session.DiscoveredProposals.ToList();
                }

                var errorAssistantContent = string.IsNullOrEmpty(partialMessage)
                    ? IIdeationConversationCoordinator.ErrorAfterToolsAssistantNotice
                    : $"{partialMessage}\n\n{IIdeationConversationCoordinator.ErrorAfterToolsAssistantNotice}";
                var errorMetadataJson = partialProposals.Count > 0
                    ? JsonSerializer.Serialize(partialProposals)
                    : null;

                session.TurnQueue.EnqueueSanitizeTrailingInterruptedTurns(
                    $"Failed to sanitize trailing interrupted turns after error for ideation conversation #{session.ConversationId}");
                session.TurnQueue.EnqueueTurn(
                    StageTurnRole.Assistant,
                    $"Failed to record error Assistant turn for ideation conversation #{session.ConversationId}",
                    content: errorAssistantContent,
                    thinkingTrace: string.IsNullOrEmpty(partialThinking) ? null : partialThinking,
                    metadataJson: errorMetadataJson);
                lock (session.HistoryGate)
                {
                    if (!userMsgAddedToChatHistory)
                    {
                        session.ChatHistory.Add(new ChatMessage(ChatRole.User, prePersistedUserTurn.Content ?? request.Prompt.Trim()));
                    }
                    session.ChatHistory.Add(new ChatMessage(ChatRole.Assistant, errorAssistantContent));
                    CrucibleAgentHarness.StripAttribution(session.ChatHistory);
                }
            }

            await session.TurnQueue.DrainAsync().ConfigureAwait(false);

            var formattedError = HttpErrorDetailHelper.FormatLlmInvocationError(ex);
            _logger.LogWarning(ex, "Ideation turn failed for conversation #{ConversationId}: {Error}", session.ConversationId, formattedError);

            MutateInFlightAndBroadcast(
                session,
                () =>
                {
                    session.IsExecutingInference = false;
                    session.EndInFlightTurnLocked();
                    session.LastAccessedAt = _timeProvider.GetUtcNow();
                },
                new IdeationStreamUpdate(
                    IdeationStreamUpdateKind.Error,
                    ErrorMessage: formattedError,
                    ConversationId: session.ConversationId));

            if (linkCallerCancellation)
            {
                throw;
            }

            return new IdeationResult(string.Empty, Array.Empty<IdeationProposal>());
        }
        finally
        {
            if (session.ToolsetSession != null)
            {
                session.ToolsetSession.Tools.AskHandler = null;
            }

            if (!deltaCaptured)
            {
                long turnMeasuredInput = tracingClient != null
                    ? Math.Max(0, tracingClient.TotalMeasuredInputTokens - baselineInputTokens)
                      + Math.Max(0, (piStrategy?.TotalSummarizerInputTokens ?? 0) - baselineSummarizerInputTokens)
                    : 0;
                long turnMeasuredOutput = tracingClient != null
                    ? Math.Max(0, tracingClient.TotalMeasuredOutputTokens - baselineOutputTokens)
                      + Math.Max(0, (piStrategy?.TotalSummarizerOutputTokens ?? 0) - baselineSummarizerOutputTokens)
                    : 0;

                await CaptureAndPersistSessionUsageDeltaAsync(
                    session.RouterSessionId,
                    baselineUsage,
                    session.ConversationId,
                    turnMeasuredInput,
                    turnMeasuredOutput).ConfigureAwait(false);
            }

            lock (session.StateGate)
            {
                session.IsExecutingInference = false;
                if (ReferenceEquals(session.ActiveTurnCts, turnCts))
                {
                    session.ActiveTurnCts = null;
                }
                if (session.IsGenerating)
                {
                    session.EndInFlightTurnLocked();
                }
            }

            NotifyTurnActivityCompleted();
        }
    }

    private async Task EnsureWarmSetupReadyForTurnAsync(WarmIdeationSession session, CancellationToken cancellationToken)
    {
        Task? pendingWarmup = null;
        bool needsBuild = false;
        int targetGeneration;
        IdeationSessionOptions targetOptions;

        lock (session.StateGate)
        {
            if (session.HarnessSetup == null || session.OptionsDirty)
            {
                needsBuild = true;
                if (session.InFlightWarmupTask != null
                    && !session.InFlightWarmupTask.IsCompleted
                    && session.InFlightWarmupGeneration == session.ConfigGeneration
                    && WarmIdeationSession.AreOptionsEquivalent(session.InFlightWarmupOptions, session.Options))
                {
                    pendingWarmup = session.InFlightWarmupTask;
                }
            }
            targetGeneration = session.ConfigGeneration;
            targetOptions = session.Options;
        }

        if (pendingWarmup != null && !pendingWarmup.IsCompleted)
        {
            try
            {
                await pendingWarmup.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Background warmup was cancelled (e.g. pause or disposal); fall through and re-evaluate state.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "In-flight background warmup failed for conversation #{ConversationId}; re-evaluating setup.", session.ConversationId);
            }
        }

        lock (session.StateGate)
        {
            needsBuild = session.HarnessSetup == null || session.OptionsDirty;
            targetGeneration = session.ConfigGeneration;
            targetOptions = session.Options;
        }

        if (needsBuild)
        {
            await WarmupCoreAsync(session, targetGeneration, targetOptions, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record DirtyContextRefreshResult(
        string? Delta,
        bool RestorableDigestsCaptured,
        string? PreviousBacklogDigest,
        string? PreviousAttachmentsDigest,
        string? PreviousHotspotsDigest)
    {
        public static readonly DirtyContextRefreshResult None = new(null, false, null, null, null);
    }

    private async Task<DirtyContextRefreshResult> RefreshDirtyWorkspaceContextAsync(WarmIdeationSession session, CancellationToken cancellationToken)
    {
        bool isDirty;
        lock (session.StateGate)
        {
            isDirty = session.ContextDirty;
        }

        if (!isDirty || session.Locations == null)
        {
            return DirtyContextRefreshResult.None;
        }

        string currentBacklog;
        string currentAttachments;
        string currentHotspots;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var workspaceContext = scope.ServiceProvider.GetRequiredService<IAgentWorkspaceContextService>();
            currentBacklog = await workspaceContext.BuildBacklogDigestAsync(session.WorkspaceId, null, cancellationToken).ConfigureAwait(false);
            currentAttachments = await workspaceContext.BuildWorkspaceAttachmentsDigestAsync(session.WorkspaceId, cancellationToken).ConfigureAwait(false);
            currentHotspots = await workspaceContext.BuildFrictionHotspotsDigestAsync(session.Locations.RepoPath, cancellationToken).ConfigureAwait(false);
        }

        bool hasHistory;
        lock (session.HistoryGate)
        {
            hasHistory = session.ChatHistory.Count > 0;
        }

        if (!hasHistory)
        {
            bool changed = !string.Equals(session.LastBacklogDigest ?? string.Empty, currentBacklog, StringComparison.Ordinal)
                || !string.Equals(session.LastAttachmentsDigest ?? string.Empty, currentAttachments, StringComparison.Ordinal)
                || !string.Equals(session.LastFrictionHotspotsDigest ?? string.Empty, currentHotspots, StringComparison.Ordinal);

            if (changed)
            {
                int generation;
                IdeationSessionOptions options;
                lock (session.StateGate)
                {
                    generation = session.ConfigGeneration;
                    options = session.Options;
                }

                await WarmupCoreAsync(session, generation, options, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                lock (session.StateGate)
                {
                    session.ContextDirty = false;
                }
            }

            return DirtyContextRefreshResult.None;
        }

        string? previousBacklog;
        string? previousAttachments;
        string? previousHotspots;
        lock (session.StateGate)
        {
            previousBacklog = session.LastBacklogDigest;
            previousAttachments = session.LastAttachmentsDigest;
            previousHotspots = session.LastFrictionHotspotsDigest;
        }

        string? delta = IdeationSystemPromptBuilder.BuildWorkspaceContextUpdateDelta(
            previousBacklog,
            currentBacklog,
            previousAttachments,
            currentAttachments,
            previousHotspots,
            currentHotspots);

        lock (session.StateGate)
        {
            session.LastBacklogDigest = currentBacklog;
            session.LastAttachmentsDigest = currentAttachments;
            session.LastFrictionHotspotsDigest = currentHotspots;
            session.ContextDirty = false;
        }

        return new DirtyContextRefreshResult(
            delta,
            !string.IsNullOrWhiteSpace(delta),
            previousBacklog,
            previousAttachments,
            previousHotspots);
    }

    private async Task RefreshLastBacklogDigestBestEffortAsync(WarmIdeationSession session, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var workspaceContext = scope.ServiceProvider.GetRequiredService<IAgentWorkspaceContextService>();
            var latestBacklog = await workspaceContext.BuildBacklogDigestAsync(session.WorkspaceId, null, cancellationToken).ConfigureAwait(false);
            lock (session.StateGate)
            {
                session.LastBacklogDigest = latestBacklog;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed refreshing backlog digest cache after in-session mutation for conversation #{ConversationId}.", session.ConversationId);
        }
    }

    private AskHandler CreateAskHandler(
        WarmIdeationSession session,
        Channel<(Guid GroupId, IReadOnlyList<IdeationQuestion> Questions)> questionChannel)
    {
        return async (questions, ct) =>
        {
            var registrations = new List<CancellationTokenRegistration>();
            var groupId = Guid.NewGuid();
            var questionList = new List<IdeationQuestion>(questions.Count);
            var tasks = new List<Task<string>>(questions.Count);

            try
            {
                foreach (var q in questions)
                {
                    var question = new IdeationQuestion(q.Prompt, q.Rationale, q.Options);
                    questionList.Add(question);
                    registrations.Add(ct.Register(() => question.Cancel()));
                    tasks.Add(question.AnswerTask);
                }

                lock (session.StateGate)
                {
                    session.PendingQuestionGroups[groupId] = new WarmIdeationSession.PendingQuestionGroup(groupId, questionList);
                }

                questionChannel.Writer.TryWrite((groupId, questionList));

                string[] answers;
                try
                {
                    answers = await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                    throw;
                }

                var sb = new StringBuilder();
                for (int i = 0; i < questionList.Count; i++)
                {
                    if (i > 0) sb.AppendLine();
                    sb.AppendLine($"{i + 1}. Question: {questionList[i].Prompt}");
                    sb.AppendLine($"   Answer: {answers[i]}");
                }
                return sb.ToString().TrimEnd();
            }
            finally
            {
                foreach (var reg in registrations)
                {
                    reg.Dispose();
                }
            }
        };
    }

    private async Task EmitQuestionAskedAsync(
        WarmIdeationSession session,
        Guid groupId,
        IReadOnlyList<IdeationQuestion> questions,
        Dictionary<string, object?> metadata,
        CancellationToken cancellationToken)
    {
        var firstQ = questions.FirstOrDefault();
        var promptSummary = questions.Count > 1
            ? $"Batch ({questions.Count} questions): {firstQ?.Prompt}"
            : (firstQ?.Prompt ?? "Clarification requested");

        await BroadcastActivityAsync(
            session.RouterSessionId,
            0,
            StandardStageIds.Ideation,
            AgentActivityKind.ToolStarted,
            $"ask: {promptSummary}",
            cancellationToken,
            metadata).ConfigureAwait(false);

        MutateInFlightAndBroadcast(
            session,
            () =>
            {
                session.PendingQuestionGroups.TryGetValue(groupId, out var group);
                session.CurrentParts.Add(new WarmIdeationSession.MutableInFlightPart
                {
                    Kind = IdeationInFlightPartKind.Question,
                    QuestionGroupId = groupId,
                    Questions = questions,
                    QuestionAnswer = group is { IsSubmitted: true } ? group.SubmittedFormattedAnswer : null,
                    QuestionMetadataJson = group is { IsSubmitted: true } ? group.SubmittedMetadataJson : null
                });
            },
            new IdeationStreamUpdate(
                IdeationStreamUpdateKind.QuestionAsked,
                Question: firstQ,
                Questions: questions,
                ConversationId: session.ConversationId,
                QuestionGroupId: groupId));
    }

    private async Task SyncPendingProposalsAsync(
        WarmIdeationSession session,
        CanonicalTools canonicalTools,
        List<IdeationProposal> sessionProposals,
        Dictionary<string, object?> metadata,
        CancellationToken cancellationToken)
    {
        if (canonicalTools.RemovedTaskIds.Count > 0)
        {
            var removedSet = canonicalTools.RemovedTaskIds.ToHashSet();
            sessionProposals.RemoveAll(p => p.TaskId.HasValue && removedSet.Contains(p.TaskId.Value));
            lock (session.StateGate)
            {
                session.DiscoveredProposals.RemoveAll(p => p.TaskId.HasValue && removedSet.Contains(p.TaskId.Value));
            }
        }

        foreach (var p in canonicalTools.PendingProposals)
        {
            if (!sessionProposals.Any(existing =>
                (p.TaskId.HasValue && existing.TaskId.HasValue)
                    ? existing.TaskId == p.TaskId
                    : string.Equals(existing.Title, p.Title, StringComparison.OrdinalIgnoreCase)))
            {
                var ideationProp = IdeationSystemPromptBuilder.ToIdeationProposal(p);
                sessionProposals.Add(ideationProp);

                MutateInFlightAndBroadcast(
                    session,
                    () => RecordProposalLocked(session, ideationProp),
                    new IdeationStreamUpdate(
                        IdeationStreamUpdateKind.ProposalDiscovered,
                        Proposal: ideationProp,
                        ConversationId: session.ConversationId));

                await BroadcastActivityAsync(
                    session.RouterSessionId,
                    0,
                    StandardStageIds.Ideation,
                    AgentActivityKind.ToolCompleted,
                    $"Proposed task: {ideationProp.Title}",
                    cancellationToken,
                    metadata).ConfigureAwait(false);
            }
        }
    }

    private void MutateInFlightAndBroadcast(
        WarmIdeationSession session,
        Action mutateLocked,
        IdeationStreamUpdate update)
    {
        Action<IdeationStreamUpdate>[] callbacks;
        lock (session.StateGate)
        {
            mutateLocked();
            callbacks = session.GetSubscribersSnapshotLocked();
        }

        BroadcastToCallbacks(session.ConversationId, callbacks, update);
    }

    private void BroadcastToCallbacks(
        int conversationId,
        Action<IdeationStreamUpdate>[] callbacks,
        IdeationStreamUpdate update)
    {
        foreach (var callback in callbacks)
        {
            try
            {
                callback(update);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ideation stream subscriber callback threw an exception for conversation #{ConversationId}", conversationId);
            }
        }
    }

    private static void AppendThinkingDeltaLocked(WarmIdeationSession session, string delta)
    {
        session.CurrentThinkingBuffer.Append(delta);
        var last = session.CurrentParts.LastOrDefault();
        if (last is { Kind: IdeationInFlightPartKind.Thinking })
        {
            last.Text = (last.Text ?? string.Empty) + delta;
        }
        else
        {
            session.CurrentParts.Add(new WarmIdeationSession.MutableInFlightPart
            {
                Kind = IdeationInFlightPartKind.Thinking,
                Text = delta
            });
        }
    }

    private static void AppendMessageDeltaLocked(WarmIdeationSession session, string delta)
    {
        session.CurrentMessageBuffer.Append(delta);
        var last = session.CurrentParts.LastOrDefault();
        if (last is { Kind: IdeationInFlightPartKind.Text })
        {
            last.Text = (last.Text ?? string.Empty) + delta;
        }
        else
        {
            session.CurrentParts.Add(new WarmIdeationSession.MutableInFlightPart
            {
                Kind = IdeationInFlightPartKind.Text,
                Text = delta
            });
        }
    }

    private static void RecordToolStartedLocked(WarmIdeationSession session, IdeationToolCallInfo call)
    {
        if (!session.CurrentToolCalls.Any(t => string.Equals(t.Info.ToolCallId, call.ToolCallId, StringComparison.Ordinal)))
        {
            session.CurrentToolCalls.Add(new IdeationInFlightToolCall(call, IsCompleted: false));
            if (!string.Equals(call.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase))
            {
                session.CurrentParts.Add(new WarmIdeationSession.MutableInFlightPart
                {
                    Kind = IdeationInFlightPartKind.ToolCall,
                    ToolCall = call,
                    IsToolCompleted = false
                });
            }
        }
    }

    private static string? RecordToolCompletedLocked(WarmIdeationSession session, IdeationToolCallInfo comp)
    {
        int callIdx = session.CurrentToolCalls.FindIndex(t => string.Equals(t.Info.ToolCallId, comp.ToolCallId, StringComparison.Ordinal));
        if (callIdx >= 0)
        {
            session.CurrentToolCalls[callIdx] = new IdeationInFlightToolCall(comp, IsCompleted: true);
        }
        else
        {
            session.CurrentToolCalls.Add(new IdeationInFlightToolCall(comp, IsCompleted: true));
        }

        var part = session.CurrentParts.FirstOrDefault(p =>
            p.Kind == IdeationInFlightPartKind.ToolCall &&
            string.Equals(p.ToolCall?.ToolCallId, comp.ToolCallId, StringComparison.Ordinal));
        if (part != null)
        {
            part.ToolCall = comp;
            part.IsToolCompleted = true;
        }

        if (string.Equals(comp.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase))
        {
            var qPart = session.CurrentParts.LastOrDefault(p =>
                p.Kind == IdeationInFlightPartKind.Question &&
                p.QuestionGroupId.HasValue &&
                session.PendingQuestionGroups.ContainsKey(p.QuestionGroupId.Value));

            qPart ??= session.CurrentParts.LastOrDefault(p =>
                p.Kind == IdeationInFlightPartKind.Question &&
                string.IsNullOrWhiteSpace(p.QuestionAnswer));

            string? submittedFormattedAnswer = null;
            string? submittedMetadataJson = null;
            if (qPart?.QuestionGroupId is Guid gid &&
                session.PendingQuestionGroups.Remove(gid, out var group))
            {
                submittedFormattedAnswer = group.SubmittedFormattedAnswer;
                submittedMetadataJson = group.SubmittedMetadataJson;
            }
            else
            {
                var fallbackEntry = session.PendingQuestionGroups
                    .LastOrDefault(kv => !string.IsNullOrWhiteSpace(kv.Value.SubmittedMetadataJson) ||
                                         !string.IsNullOrWhiteSpace(kv.Value.SubmittedFormattedAnswer));
                if (fallbackEntry.Value != null)
                {
                    submittedFormattedAnswer = fallbackEntry.Value.SubmittedFormattedAnswer;
                    submittedMetadataJson = fallbackEntry.Value.SubmittedMetadataJson;
                    session.PendingQuestionGroups.Remove(fallbackEntry.Key);
                }
            }

            if (qPart != null)
            {
                if (string.IsNullOrWhiteSpace(qPart.QuestionMetadataJson) && !string.IsNullOrWhiteSpace(submittedMetadataJson))
                {
                    qPart.QuestionMetadataJson = submittedMetadataJson;
                }

                if (string.IsNullOrWhiteSpace(qPart.QuestionAnswer))
                {
                    if (!string.IsNullOrWhiteSpace(submittedFormattedAnswer))
                    {
                        qPart.QuestionAnswer = submittedFormattedAnswer;
                    }
                    else if (!string.IsNullOrWhiteSpace(comp.Result))
                    {
                        var answerText = comp.Result;
                        const string prefix = "User answered: ";
                        if (answerText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            answerText = answerText[prefix.Length..].Trim();
                        }
                        qPart.QuestionAnswer = answerText;
                    }
                }
            }

            return MergeCallIdAndAnswersMetadata(comp.ToolCallId, submittedMetadataJson);
        }

        return !string.IsNullOrEmpty(comp.ToolCallId)
            ? JsonSerializer.Serialize(new { CallId = comp.ToolCallId })
            : null;
    }

    private static void RecordProposalLocked(WarmIdeationSession session, IdeationProposal proposal)
    {
        if (!session.DiscoveredProposals.Any(existing =>
            (proposal.TaskId.HasValue && existing.TaskId.HasValue)
                ? existing.TaskId == proposal.TaskId
                : string.Equals(existing.Title, proposal.Title, StringComparison.OrdinalIgnoreCase)))
        {
            session.DiscoveredProposals.Add(proposal);
        }
    }

    private static string MergeCallIdAndAnswersMetadata(string? callId, string? answersMetadataJson)
    {
        if (!string.IsNullOrWhiteSpace(answersMetadataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(answersMetadataJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    (doc.RootElement.TryGetProperty("Answers", out var answersEl) ||
                     doc.RootElement.TryGetProperty("answers", out answersEl)))
                {
                    return JsonSerializer.Serialize(new
                    {
                        CallId = callId,
                        Answers = answersEl
                    });
                }
            }
            catch (JsonException)
            {
                // Fall back to CallId-only metadata when structured answers JSON is malformed.
            }
        }

        return JsonSerializer.Serialize(new { CallId = callId });
    }

    private static void CancelPendingQuestionsAndSanitizeHistory(WarmIdeationSession session)
    {
        List<IdeationQuestion> pending;
        lock (session.StateGate)
        {
            pending = session.PendingQuestionGroups.Values
                .SelectMany(g => g.Questions)
                .ToList();
        }

        foreach (var q in pending)
        {
            q.Cancel();
        }

        lock (session.HistoryGate)
        {
            CrucibleAgentHarness.SanitizeInFlightTurns(session.ChatHistory);
        }
    }

    private async Task<RouterSessionUsage?> TryGetBaselineUsageAsync(string routerSessionId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var gateway = scope.ServiceProvider.GetRequiredService<ILlmGatewayService>();
            return await gateway.GetSessionUsageAsync(routerSessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to capture baseline LLM session usage for ideation session '{SessionId}'", routerSessionId);
            return null;
        }
    }

    private async Task<SessionUsageDelta?> CaptureAndPersistSessionUsageDeltaAsync(
        string routerSessionId,
        RouterSessionUsage? baselineUsage,
        int conversationId,
        long measuredInputTokens,
        long measuredOutputTokens)
    {
        SessionUsageDelta? usageDelta = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var gateway = scope.ServiceProvider.GetRequiredService<ILlmGatewayService>();
            using var costCts = new CancellationTokenSource(SessionTelemetryTimeout);
            var finalUsage = await gateway.GetSessionUsageAsync(routerSessionId, costCts.Token).ConfigureAwait(false);
            usageDelta = SessionUsageDelta.Compute(finalUsage, baselineUsage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture final LLM session usage for ideation session '{SessionId}'", routerSessionId);
        }

        if (usageDelta == null && (measuredInputTokens > 0 || measuredOutputTokens > 0))
        {
            usageDelta = new SessionUsageDelta(
                InputTokens: (int)Math.Min(int.MaxValue, measuredInputTokens),
                OutputTokens: (int)Math.Min(int.MaxValue, measuredOutputTokens),
                CachedInputTokens: 0,
                Cost: null,
                ActualCloudCost: null,
                ElectricityCost: null,
                CloudSavings: null,
                Currency: null);
        }
        else if (usageDelta != null && (measuredInputTokens > usageDelta.InputTokens || measuredOutputTokens > usageDelta.OutputTokens))
        {
            usageDelta = usageDelta with
            {
                InputTokens = (int)Math.Min(int.MaxValue, Math.Max(measuredInputTokens, usageDelta.InputTokens)),
                OutputTokens = (int)Math.Min(int.MaxValue, Math.Max(measuredOutputTokens, usageDelta.OutputTokens))
            };
        }

        if (usageDelta != null && conversationId > 0)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
                await taskRepo.UpdateConversationUsageAsync(conversationId, usageDelta, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to accumulate conversation usage delta for ideation conversation #{ConversationId}", conversationId);
            }
        }

        return usageDelta;
    }

    private async Task<(string? TranscriptPath, string? FullTranscriptMarkdown)> SaveTurnTranscriptAsync(
        WarmIdeationSession session,
        string repoPath,
        AgentProfile profile,
        HarnessAgentSetup setup,
        IReadOnlyList<string> activeSkills,
        IReadOnlyList<string> permittedTools,
        long elapsedMs,
        long turnPromptTokens,
        long turnCompletionTokens,
        SessionUsageDelta? usageDelta,
        IReadOnlyList<TranscriptEntry> transcriptEntries,
        IReadOnlyList<ModelCallSnapshot> turnModelCalls,
        AgentRunStatus status,
        CancellationToken cancellationToken)
    {
        try
        {
            var targetDir = Path.Combine(repoPath, "docs", "transcripts");
            long promptTokens = Math.Max(turnPromptTokens, usageDelta?.InputTokens ?? 0);
            long completionTokens = Math.Max(turnCompletionTokens, usageDelta?.OutputTokens ?? 0);
            var transcriptMetrics = new AgentRunMetrics(
                DurationMs: elapsedMs,
                PromptTokens: promptTokens,
                CompletionTokens: completionTokens,
                TotalTokens: promptTokens + completionTokens,
                CachedInputTokens: usageDelta?.CachedInputTokens,
                Cost: usageDelta?.Cost,
                ActualCloudCost: usageDelta?.ActualCloudCost,
                ElectricityCost: usageDelta?.ElectricityCost,
                CloudSavings: usageDelta?.CloudSavings,
                Currency: usageDelta?.Currency);

            var metadataRecord = new TranscriptMetadata(
                TaskId: session.WorkspaceId,
                StageId: StandardStageIds.Ideation,
                AgentProfile: profile.Id,
                ModelName: setup.EffectiveModelId ?? profile.ModelName ?? "default",
                DurationMs: elapsedMs,
                Metrics: transcriptMetrics,
                Optimizations: session.OptimizationFlags ?? profile.OptimizationFlags ?? new LocalOptimizationFlags(),
                Status: status,
                KvCacheHitRate: null,
                SystemPrompt: setup.TracingClient.LastCapturedSystemPrompt ?? session.SystemPrompt,
                AvailableTools: setup.TracingClient.LastCapturedTools.Count > 0 ? setup.TracingClient.LastCapturedTools : permittedTools,
                ActiveSkills: activeSkills,
                MafConfiguration: MafConfigurationSummary.Build(setup.Options, setup.TracingClient.DetectedMafConfiguration),
                ModelCalls: turnModelCalls,
                ConversationId: session.ConversationId);

            var transcriptPath = await _transcriptService.SaveTranscriptAsync(metadataRecord, transcriptEntries, targetDir, cancellationToken).ConfigureAwait(false);
            var fullMarkdown = File.Exists(transcriptPath)
                ? await File.ReadAllTextAsync(transcriptPath, cancellationToken).ConfigureAwait(false)
                : null;
            return (transcriptPath, fullMarkdown);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to save ideation session transcript for conversation #{ConversationId}.", session.ConversationId);
            return (null, null);
        }
    }

    private async Task BroadcastActivityAsync(
        string sessionId,
        int taskId,
        string stageId,
        AgentActivityKind kind,
        string content,
        CancellationToken ct,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        try
        {
            var evt = new AgentActivityEvent(sessionId, taskId, stageId, kind, content, _timeProvider.GetUtcNow().UtcDateTime, metadata);
            await _activityStreamer.BroadcastAsync(evt, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed broadcasting ideation activity event {Kind} for session '{SessionId}'.", kind, sessionId);
        }
    }

    private async Task<bool> ReleaseRouterAffinityBestEffortAsync(string routerSessionId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var gateway = scope.ServiceProvider.GetRequiredService<ILlmGatewayService>();
            return await gateway.ReleaseSessionAffinityAsync(routerSessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed releasing router affinity for ideation session '{SessionId}'.", routerSessionId);
            return false;
        }
    }

    private async Task<bool> EvictAndDisposeSessionAsync(
        WarmIdeationSession session,
        CancellationToken cancellationToken,
        bool force = false,
        bool preserveSubscribers = false,
        bool allowWhileActive = false)
    {
        HarnessAgentSetup? setupToDispose;
        lock (_registryGate)
        {
            lock (session.StateGate)
            {
                if (session.IsEvicted)
                {
                    return false;
                }

                if (!allowWhileActive && (session.IsGenerating || session.WaitingOrActiveTurns > 0))
                {
                    if (force)
                    {
                        session.PendingEviction = true;
                        session.PendingForceEviction = true;
                        session.CancelRequested = true;
                    }
                    return false;
                }

                if (!force && session.Subscribers.Count > 0)
                {
                    return false;
                }

                if (preserveSubscribers && session.Subscribers.Count > 0)
                {
                    var pendingList = _pendingSubscribers.GetOrAdd(
                        session.ConversationId,
                        _ => new List<WarmIdeationSession.SubscriberEntry>());
                    foreach (var sub in session.Subscribers)
                    {
                        if (!pendingList.Contains(sub))
                        {
                            pendingList.Add(sub);
                        }
                    }
                    session.Subscribers.Clear();
                }

                session.IsEvicted = true;
                session.ConfigGeneration++;
                session.PendingEviction = false;
                session.PendingForceEviction = false;
                setupToDispose = session.HarnessSetup;
                session.HarnessSetup = null;
                session.ToolsetSession = null;
            }

            _sessions.TryRemove(new KeyValuePair<int, WarmIdeationSession>(session.ConversationId, session));
        }

        DisposeHarnessSetup(setupToDispose);
        return await ReleaseRouterAffinityBestEffortAsync(session.RouterSessionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinalizePendingEvictionIfIdleAsync(WarmIdeationSession session)
    {
        bool forceEvict;
        bool normalEvict;
        lock (session.StateGate)
        {
            bool idle = !session.IsGenerating && session.WaitingOrActiveTurns == 0;
            forceEvict = idle && session.PendingForceEviction;
            normalEvict = idle && !forceEvict && session.PendingEviction && session.Subscribers.Count == 0;
        }

        if (forceEvict)
        {
            await EvictAndDisposeSessionAsync(
                session,
                CancellationToken.None,
                force: true,
                preserveSubscribers: true).ConfigureAwait(false);
        }
        else if (normalEvict)
        {
            await EvictAndDisposeSessionAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void DisposeHarnessSetup(HarnessAgentSetup? setup)
    {
        if (setup == null)
        {
            return;
        }

        // Disposing ActiveClient cascades through DelegatingChatClient.Dispose down to BaseClient.
        // Never touch IExecutionEnvironment, which is owned by singleton IExecutionEnvironmentProvider.
        setup.ActiveClient.Dispose();
        if (!ReferenceEquals(setup.ActiveClient, setup.BaseClient) && setup.ActiveClient is not DelegatingChatClient)
        {
            setup.BaseClient.Dispose();
        }
    }

    private void HandleOperationsStateChanged(bool isPaused)
    {
        if (!isPaused || IsDisposed)
        {
            return;
        }

        _backgroundTaskRunner.RunTracked(
            async ct =>
            {
                foreach (var session in _sessions.Values.ToList())
                {
                    bool hasActiveOrStartingTurn;
                    lock (session.StateGate)
                    {
                        session.ConfigGeneration++;
                        hasActiveOrStartingTurn = session.IsGenerating || session.WaitingOrActiveTurns > 0;
                        if (hasActiveOrStartingTurn)
                        {
                            session.PendingEviction = true;
                            session.PendingForceEviction = true;
                            session.CancelRequested = true;
                        }
                    }

                    if (hasActiveOrStartingTurn)
                    {
                        await CancelActiveTurnAsync(session.ConversationId, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var evicted = await EvictAndDisposeSessionAsync(
                            session,
                            ct,
                            force: true,
                            preserveSubscribers: true).ConfigureAwait(false);
                        if (!evicted && !session.IsEvicted)
                        {
                            await CancelActiveTurnAsync(session.ConversationId, ct).ConfigureAwait(false);
                        }
                    }
                }
            },
            "IdeationCoordinator.PauseEviction",
            CancellationToken.None);
    }

    private async Task RunIdleSweepLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(DefaultIdleSweepInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await EvictIdleSessionsAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Coordinator disposed
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ideation idle session sweep loop stopped unexpectedly.");
        }
    }

    internal static bool IsWorkspaceMutatingActivity(AgentActivityEvent evt)
    {
        if (evt.Kind is AgentActivityKind.StageCompleted or AgentActivityKind.Error or AgentActivityKind.Interrupted)
        {
            return !string.Equals(evt.StageId, StandardStageIds.Ideation, StringComparison.OrdinalIgnoreCase)
                || evt.TaskId > 0;
        }

        if (evt.Kind == AgentActivityKind.Notice && evt.Metadata != null)
        {
            if (evt.Metadata.TryGetValue(AgentActivityMetadataKeys.ActionType, out var actionObj) &&
                string.Equals(actionObj?.ToString(), AgentActivityConstants.ActionTypeTaskSuspended, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (evt.Metadata.TryGetValue(AgentActivityMetadataKeys.ToolName, out var toolObj) &&
                toolObj?.ToString() is { } toolName &&
                (CanonicalToolNames.BacklogMutationTools.Contains(toolName, StringComparer.OrdinalIgnoreCase) ||
                 string.Equals(toolName, CanonicalToolNames.ProposeTasks, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task RunWorkspaceActivityWatcherAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in _activityStreamer.SubscribeAsync(taskId: null, cancellationToken).ConfigureAwait(false))
            {
                if (_sessions.IsEmpty || !IsWorkspaceMutatingActivity(evt))
                {
                    continue;
                }

                int? workspaceId = null;
                if (evt.Metadata != null &&
                    evt.Metadata.TryGetValue(AgentActivityMetadataKeys.WorkspaceId, out var rawWsId) &&
                    rawWsId is int wsId &&
                    wsId > 0)
                {
                    workspaceId = wsId;
                }
                else if (evt.TaskId > 0)
                {
                    try
                    {
                        await using var scope = _scopeFactory.CreateAsyncScope();
                        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
                        var task = await taskRepo.GetByIdAsync(evt.TaskId, cancellationToken).ConfigureAwait(false);
                        workspaceId = task?.WorkspaceId;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "Could not resolve workspace for activity event on task #{TaskId}.", evt.TaskId);
                    }
                }

                if (workspaceId.HasValue && workspaceId.Value > 0)
                {
                    MarkWorkspaceContextDirty(workspaceId.Value);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Coordinator disposed
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ideation workspace activity watcher exited.");
        }
    }

    private IdeationSessionHandle CaptureSessionHandle(WarmIdeationSession session, Conversation conversation)
    {
        lock (session.StateGate)
        {
            return session.CaptureSessionHandleLocked(conversation);
        }
    }

    private IdeationInFlightTurnSnapshot? CaptureInFlightSnapshot(WarmIdeationSession session)
    {
        lock (session.StateGate)
        {
            return session.CaptureInFlightSnapshotLocked();
        }
    }

    private static async Task TryWaitAndReleaseBeforeDisposeAsync(SemaphoreSlim semaphore)
    {
        bool acquired = false;
        try
        {
            acquired = await semaphore.WaitAsync(DisposeStopWaitTimeout).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        finally
        {
            if (acquired)
            {
                ReleaseSemaphoreSafe(semaphore);
            }
        }
    }

    private static void ReleaseSemaphoreSafe(SemaphoreSlim semaphore)
    {
        try
        {
            semaphore.Release();
        }
        catch (ObjectDisposedException)
        {
            // Coordinator shut down and disposed session locks after timeout.
        }
    }

    private void NotifyTurnActivityStarted()
    {
        if (Interlocked.Increment(ref _activeTurnCount) == 1)
        {
            OnTurnActivityChanged?.Invoke(true);
        }
    }

    private void NotifyTurnActivityCompleted()
    {
        if (Interlocked.Decrement(ref _activeTurnCount) == 0)
        {
            OnTurnActivityChanged?.Invoke(false);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
    }

    private static IReadOnlyList<string> ResolvePermittedTools(AgentProfile profile) =>
        profile.PermittedTools is { Count: > 0 }
            ? profile.PermittedTools
            : DefaultAgentTools.Ideation;

    private static HumanInterruptionException? FindInterruption(Exception? ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is HumanInterruptionException hie)
            {
                return hie;
            }
        }
        return null;
    }

    private static string FormatSingleSubmissionAnswer(IReadOnlyDictionary<int, string> answeredDict)
    {
        if (answeredDict.Count == 0)
        {
            return string.Empty;
        }

        if (answeredDict.Count == 1 && answeredDict.TryGetValue(0, out var firstAns))
        {
            return firstAns;
        }

        return string.Join("\n\n", answeredDict.OrderBy(kv => kv.Key).Select(kv => $"Q{kv.Key + 1}: {kv.Value}"));
    }
}
