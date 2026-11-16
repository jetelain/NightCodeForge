using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.ExternalProcess;

/// <summary>
/// Broadcasts live agent activity, records SQLite conversation turns/usage,
/// and persists session markdown transcripts for external-process harnesses.
/// </summary>
public interface IHarnessActivityRecorder
{
    Task BroadcastAsync(
        string sessionId,
        int taskId,
        string stageId,
        AgentActivityKind kind,
        string content,
        CancellationToken cancellationToken);

    Task BroadcastAsync(
        string sessionId,
        int taskId,
        string stageId,
        AgentActivityKind kind,
        string content,
        IReadOnlyDictionary<string, object?>? metadata,
        CancellationToken cancellationToken);

    Task BeginConversationAsync(
        string sessionId,
        string harnessId,
        AgentSessionContext context,
        CancellationToken cancellationToken);

    Task RecordUserTurnAsync(
        string sessionId,
        string harnessId,
        AgentSessionContext context,
        string promptText,
        CancellationToken cancellationToken);

    Task RecordAssistantTurnAsync(
        string sessionId,
        string? content,
        string? thinkingTrace,
        CancellationToken cancellationToken);

    Task RecordToolCallTurnAsync(
        string sessionId,
        string toolName,
        string? toolArguments,
        string? callId,
        CancellationToken cancellationToken);

    Task RecordToolResultTurnAsync(
        string sessionId,
        string toolName,
        string? toolResult,
        bool isError,
        string? callId,
        long? durationMs,
        CancellationToken cancellationToken);

    Task SaveTranscriptAsync(
        string harnessId,
        AgentSessionContext context,
        string workspacePath,
        string promptText,
        string? summaryText,
        long elapsedMs,
        long promptTokens,
        long completionTokens,
        AgentRunStatus status,
        CancellationToken cancellationToken);

    Task SaveTranscriptAsync(
        string harnessId,
        AgentSessionContext context,
        string workspacePath,
        string promptText,
        string? summaryText,
        long elapsedMs,
        long promptTokens,
        long completionTokens,
        AgentRunStatus status,
        CancellationToken cancellationToken,
        string? sessionId,
        int? cachedInputTokens);
}
