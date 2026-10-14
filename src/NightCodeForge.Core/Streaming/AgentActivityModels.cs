namespace NightCodeForge.Core.Streaming;

/// <summary>
/// Categories of live agent events streamed to the UI and listeners.
/// </summary>
public enum AgentActivityKind
{
    ThinkingDelta,
    MessageDelta,
    ToolStarted,
    ToolCompleted,
    TurnCompleted,
    StageStarted,
    StageCompleted,
    Interrupted,
    Error
}

/// <summary>
/// A real-time telemetry event emitted during agent reasoning and tool execution.
/// </summary>
public record AgentActivityEvent(
    string SessionId,
    int TaskId,
    string StageId,
    AgentActivityKind Kind,
    string Content,
    DateTime Timestamp,
    IReadOnlyDictionary<string, object?>? Metadata = null);

/// <summary>
/// Contract for broadcasting and subscribing to live agent activity streams.
/// </summary>
public interface IAgentActivityStreamer
{
    Task BroadcastAsync(AgentActivityEvent activityEvent, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentActivityEvent> SubscribeAsync(int? taskId = null, CancellationToken cancellationToken = default);
}
