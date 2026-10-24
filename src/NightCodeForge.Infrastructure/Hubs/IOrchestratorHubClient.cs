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

    /// <summary>
    /// Context or memory optimization telemetry (e.g. tool compaction, thinking pruning, token eviction).
    /// </summary>
    Task ReceiveOptimizationAction(int taskId, string stageId, string actionType, string description, int tokensSaved, DateTime timestamp);

    /// <summary>
    /// Live operations telemetry: an LLM inference call has started.
    /// </summary>
    Task ReceiveLlmCallStarted(NightCodeForge.Core.Workflow.LlmCallRecord call);

    /// <summary>
    /// Live operations telemetry: streaming progress of an active LLM inference call.
    /// </summary>
    Task ReceiveLlmCallProgress(string callId, int completionTokens, double durationMs, double tokensPerSecond, int reasoningTokens);

    /// <summary>
    /// Live operations telemetry: an LLM inference call has completed or failed.
    /// </summary>
    Task ReceiveLlmCallCompleted(NightCodeForge.Core.Workflow.LlmCallRecord call);

    /// <summary>
    /// Live operations telemetry: a shell command execution has started.
    /// </summary>
    Task ReceiveShellCommandStarted(NightCodeForge.Core.Workflow.ShellCommandRecord command);

    /// <summary>
    /// Live operations telemetry: real-time output text chunk for an active shell command.
    /// </summary>
    Task ReceiveShellCommandOutput(string commandId, string outputChunk, bool isError);

    /// <summary>
    /// Live operations telemetry: a shell command execution has completed or failed.
    /// </summary>
    Task ReceiveShellCommandCompleted(NightCodeForge.Core.Workflow.ShellCommandRecord command);

    /// <summary>
    /// Global operations pause / resume status change or GPU VRAM reclaim update.
    /// </summary>
    Task ReceiveOperationsStatusChanged(bool isPaused, string? reclaimStatus, string? lastReclaimError);
}
