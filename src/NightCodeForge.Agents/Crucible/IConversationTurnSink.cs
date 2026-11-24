using NightCodeForge.Agents.Compaction;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Crucible;

/// <summary>
/// Decoupled persistence sink for Crucible conversation turns and optional task state synchronization,
/// allowing <see cref="CrucibleConversationState"/> to operate against either EF/SQLite or an in-memory store.
/// </summary>
public interface IConversationTurnSink
{
    Task<ConversationTurn> AddTurnAsync(
        int conversationId,
        StageTurnRole role,
        string? content = null,
        string? thinkingTrace = null,
        string? toolName = null,
        string? toolArguments = null,
        string? toolResult = null,
        long? durationMs = null,
        bool isError = false,
        string? metadataJson = null,
        CancellationToken cancellationToken = default);

    Task UpdateTaskAsync(ForgeTask task, CancellationToken cancellationToken = default);

    Task SanitizeTrailingInterruptedTurnsAsync(int conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Persists <see cref="CrucibleConversationState"/> turns and task updates into an <see cref="ITaskRepository"/>.
/// </summary>
public sealed class TaskRepositoryConversationTurnSink : IConversationTurnSink
{
    private readonly ITaskRepository _repository;

    public TaskRepositoryConversationTurnSink(ITaskRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public Task<ConversationTurn> AddTurnAsync(
        int conversationId,
        StageTurnRole role,
        string? content = null,
        string? thinkingTrace = null,
        string? toolName = null,
        string? toolArguments = null,
        string? toolResult = null,
        long? durationMs = null,
        bool isError = false,
        string? metadataJson = null,
        CancellationToken cancellationToken = default) =>
        _repository.AddConversationTurnAsync(
            conversationId,
            role,
            content,
            thinkingTrace,
            toolName,
            toolArguments,
            toolResult,
            durationMs,
            isError,
            metadataJson,
            cancellationToken);

    public Task UpdateTaskAsync(ForgeTask task, CancellationToken cancellationToken = default) =>
        _repository.UpdateAsync(task, cancellationToken: cancellationToken);

    public Task SanitizeTrailingInterruptedTurnsAsync(int conversationId, CancellationToken cancellationToken = default) =>
        _repository.SanitizeTrailingInterruptedTurnsAsync(conversationId, cancellationToken);
}

/// <summary>
/// Thread-safe in-memory <see cref="IConversationTurnSink"/> used by stateless runs without database writes.
/// </summary>
public sealed class InMemoryConversationTurnSink : IConversationTurnSink
{
    private readonly object _lock = new();
    private readonly List<ConversationTurn> _turns = new();
    private int _nextId = 1;
    private int _nextTurnIndex;

    public IReadOnlyList<ConversationTurn> Turns
    {
        get
        {
            lock (_lock)
            {
                return _turns.ToList();
            }
        }
    }

    public Task<ConversationTurn> AddTurnAsync(
        int conversationId,
        StageTurnRole role,
        string? content = null,
        string? thinkingTrace = null,
        string? toolName = null,
        string? toolArguments = null,
        string? toolResult = null,
        long? durationMs = null,
        bool isError = false,
        string? metadataJson = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var turn = new ConversationTurn
            {
                Id = _nextId++,
                ConversationId = conversationId,
                TurnIndex = _nextTurnIndex++,
                Role = role,
                Content = content,
                ThinkingTrace = thinkingTrace,
                ToolName = toolName,
                ToolArguments = toolArguments,
                ToolResult = toolResult,
                DurationMs = durationMs,
                IsError = isError,
                MetadataJson = metadataJson,
                Timestamp = DateTime.UtcNow
            };
            _turns.Add(turn);
            return Task.FromResult(turn);
        }
    }

    public Task UpdateTaskAsync(ForgeTask task, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SanitizeTrailingInterruptedTurnsAsync(int conversationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_turns.Count == 0)
            {
                return Task.CompletedTask;
            }

            var toRemove = ConversationHistorySerializer.GetTrailingInterruptedTurns(_turns);
            foreach (var turn in toRemove)
            {
                _turns.Remove(turn);
            }

            return Task.CompletedTask;
        }
    }
}
