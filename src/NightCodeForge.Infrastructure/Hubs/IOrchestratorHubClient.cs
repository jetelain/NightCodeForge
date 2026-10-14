namespace NightCodeForge.Infrastructure.Hubs;

/// <summary>
/// Strongly-typed client contract for real-time orchestrator SignalR telemetry.
/// </summary>
public interface IOrchestratorHubClient
{
    /// <summary>
    /// Live streaming of model reasoning thoughts (&lt;think&gt;) or answer text.
    /// </summary>
    Task ReceiveTokenDelta(int taskId, string token, bool isThinking);

    /// <summary>
    /// Tool execution notification (e.g. read, write, edit, bash, ask).
    /// </summary>
    Task ReceiveToolExecution(int taskId, string toolName, string arguments, string status, int? durationMs);

    /// <summary>
    /// Git operation telemetry (e.g. branch creation, WIP commit, fetch, push).
    /// </summary>
    Task ReceiveGitProgress(int taskId, string operation, string message);

    /// <summary>
    /// Global worker state change (Idle, Running, WaitingForHuman, Blocked).
    /// </summary>
    Task ReceiveWorkerStatus(string status, int? activeTaskId, string? stageId, string hardwareTarget);

    /// <summary>
    /// Pipeline advancement across agentic and deterministic stages.
    /// </summary>
    Task ReceiveStageTransition(int taskId, string stageId, string stageKind, string status);

    /// <summary>
    /// Differential quality gate outcomes (Coverage delta, Warning ratchet delta, Test assertions).
    /// </summary>
    Task ReceiveDeterministicGateResult(int taskId, string gateName, bool passed, string metricsSummary);

    /// <summary>
    /// Developer host tree dirty alert preventing push/merge.
    /// </summary>
    Task ReceiveHostDirtyAlert(int taskId, string branchName);

    /// <summary>
    /// Execution log line for real-time terminal streaming.
    /// </summary>
    Task ReceiveExecutionLog(int taskId, string stageId, string level, string message, DateTime timestamp);
}
