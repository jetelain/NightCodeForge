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
    Error,
    OptimizationAction,
    Notice
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

/// <summary>
/// Canonical metadata key constants used in <see cref="AgentActivityEvent.Metadata"/>.
/// </summary>
public static class AgentActivityMetadataKeys
{
    public const string TaskTitle = "TaskTitle";
    public const string Priority = "Priority";
    public const string QueueType = "QueueType";
    public const string StageKind = "StageKind";
    public const string ToolName = "ToolName";
    public const string DurationMs = "DurationMs";
    public const string ActionType = "ActionType";
    public const string TokensSaved = "TokensSaved";
    public const string TokensOrBytesSaved = "TokensOrBytesSaved";
    public const string BranchName = "BranchName";
}
