using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.ExternalProcess;

/// <summary>
/// Broadcasts live agent activity and persists session markdown transcripts for external-process harnesses.
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
}

