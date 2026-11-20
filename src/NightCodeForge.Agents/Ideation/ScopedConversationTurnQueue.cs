using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Agents.Ideation;

/// <summary>
/// Thread-safe, scope-hygienic turn persistence queue for one warm Ideation conversation.
/// Every database write opens and disposes a fresh DI scope so the singleton coordinator
/// never retains a scoped <see cref="ITaskRepository"/> or <c>AppDbContext</c>.
/// </summary>
internal sealed class ScopedConversationTurnQueue
{
    private readonly int _conversationId;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly List<ConversationTurn> _trackedTurns = new();
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    public ScopedConversationTurnQueue(
        int conversationId,
        IServiceScopeFactory scopeFactory,
        ILogger logger)
    {
        if (conversationId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(conversationId), "ConversationId must be positive.");
        }

        _conversationId = conversationId;
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void SeedTrackedTurns(IEnumerable<ConversationTurn>? turns)
    {
        if (turns == null)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var turn in turns)
            {
                TrackPersistedTurnLocked(turn);
            }
        }
    }

    public IReadOnlyList<ConversationTurn> GetTrackedTurnsSnapshot()
    {
        lock (_gate)
        {
            return _trackedTurns.OrderBy(t => t.TurnIndex).ToList();
        }
    }

    public Task<ConversationTurn> EnqueueAndAwaitTurnAsync(
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
        Task<ConversationTurn> writeTask;
        lock (_gate)
        {
            writeTask = PersistAfterAsync(
                _tail,
                role,
                content,
                thinkingTrace,
                toolName,
                toolArguments,
                toolResult,
                durationMs,
                isError,
                metadataJson);
            _tail = ObserveSafeAsync(writeTask, failureMessage);
        }

        return writeTask;
    }

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
        lock (_gate)
        {
            var writeTask = PersistAfterAsync(
                _tail,
                role,
                content,
                thinkingTrace,
                toolName,
                toolArguments,
                toolResult,
                durationMs,
                isError,
                metadataJson);
            _tail = ObserveSafeAsync(writeTask, failureMessage);
        }
    }

    public void EnqueueSanitizeTrailingInterruptedTurns(string failureMessage, int? removeTrailingUserTurnIndex = null)
    {
        lock (_gate)
        {
            _tail = RunSanitizeAfterAsync(_tail, failureMessage, removeTrailingUserTurnIndex);
        }
    }

    public Task DrainAsync()
    {
        lock (_gate)
        {
            return _tail;
        }
    }

    private async Task<ConversationTurn> PersistAfterAsync(
        Task previous,
        StageTurnRole role,
        string? content,
        string? thinkingTrace,
        string? toolName,
        string? toolArguments,
        string? toolResult,
        long? durationMs,
        bool isError,
        string? metadataJson)
    {
        await previous.ConfigureAwait(false);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var turn = await repository.AddConversationTurnAsync(
            _conversationId,
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

        lock (_gate)
        {
            TrackPersistedTurnLocked(turn);
        }

        return turn;
    }

    private async Task RunSanitizeAfterAsync(Task previous, string failureMessage, int? removeTrailingUserTurnIndex)
    {
        await previous.ConfigureAwait(false);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
            await repository.SanitizeTrailingInterruptedTurnsAsync(
                _conversationId,
                includeUnansweredAsk: true,
                removeTrailingUserTurnIndex: removeTrailingUserTurnIndex,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            lock (_gate)
            {
                if (_trackedTurns.Count > 0)
                {
                    var toRemove = NightCodeForge.Core.Workflow.ConversationHistorySerializer.GetTrailingInterruptedTurns(
                        _trackedTurns,
                        includeUnansweredAsk: true);
                    foreach (var turn in toRemove)
                    {
                        _trackedTurns.Remove(turn);
                    }

                    if (removeTrailingUserTurnIndex.HasValue && _trackedTurns.Count > 0)
                    {
                        var orphanUsers = NightCodeForge.Core.Workflow.ConversationHistorySerializer.GetTrailingOrphanUserTurn(
                            _trackedTurns,
                            removeTrailingUserTurnIndex.Value);
                        foreach (var turn in orphanUsers)
                        {
                            _trackedTurns.Remove(turn);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Message}", failureMessage);
        }
    }

    private async Task ObserveSafeAsync(Task writeTask, string failureMessage)
    {
        try
        {
            await writeTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Message}", failureMessage);
        }
    }

    private void TrackPersistedTurnLocked(ConversationTurn? turn)
    {
        if (turn == null)
        {
            return;
        }

        if (!_trackedTurns.Any(t =>
                ReferenceEquals(t, turn) ||
                (turn.Id > 0 && t.Id == turn.Id) ||
                t.TurnIndex == turn.TurnIndex))
        {
            _trackedTurns.Add(turn);
        }
    }
}
