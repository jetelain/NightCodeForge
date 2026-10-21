using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Thread-safe ring buffer and real-time dispatcher for system-wide LLM inference calls
/// and operating system shell / CLI executions.
/// </summary>
public interface IOperationsTelemetryService
{
    /// <summary>
    /// Records the start of an in-flight LLM inference call.
    /// </summary>
    void RecordLlmCallStarted(LlmCallRecord record);

    /// <summary>
    /// Updates real-time streaming progress of an active LLM inference call.
    /// </summary>
    void UpdateLlmCallProgress(string callId, int completionTokens, double durationMs, double tokensPerSecond, int reasoningTokens = 0);

    /// <summary>
    /// Records the completion, failure, or cancellation of an LLM inference call.
    /// </summary>
    void RecordLlmCallCompleted(LlmCallRecord record);

    /// <summary>
    /// Records the start of a system shell or CLI process execution.
    /// </summary>
    void RecordShellCommandStarted(ShellCommandRecord record);

    /// <summary>
    /// Appends real-time output text to an active shell command preview.
    /// </summary>
    void UpdateShellCommandOutput(string commandId, string outputChunk, bool isError);

    /// <summary>
    /// Records the exit or failure of a shell command execution.
    /// </summary>
    void RecordShellCommandCompleted(ShellCommandRecord record);

    /// <summary>
    /// Retrieves a chronological snapshot of the most recent LLM inference calls (newest first).
    /// </summary>
    IReadOnlyList<LlmCallRecord> GetRecentLlmCalls(int count = 25);

    /// <summary>
    /// Retrieves the currently active in-flight LLM call, if any.
    /// </summary>
    LlmCallRecord? GetActiveLlmCall();

    /// <summary>
    /// Retrieves a chronological snapshot of the most recent shell commands (newest first).
    /// </summary>
    IReadOnlyList<ShellCommandRecord> GetRecentShellCommands(int count = 25);

    /// <summary>
    /// Retrieves the currently active in-flight shell command, if any.
    /// </summary>
    ShellCommandRecord? GetActiveShellCommand();
}
