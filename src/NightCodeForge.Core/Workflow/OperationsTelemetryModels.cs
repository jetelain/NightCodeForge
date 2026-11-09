namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Status of an LLM inference call intercepted by telemetry.
/// </summary>
public enum LlmCallStatus
{
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Telemetry record capturing an LLM inference request and completion.
/// </summary>
public record LlmCallRecord(
    string CallId,
    string Model,
    string TargetBackend,       // "Local", "Remote", "Cloud", "Auto"
    DateTime StartTime,
    double DurationMs,
    int PromptTokens,
    int CompletionTokens,
    double TokensPerSecond,
    string PromptPreview,
    string? CompletionPreview,
    LlmCallStatus Status,
    string? ErrorMessage,
    int ReasoningTokens = 0,
    string? Endpoint = null,
    string? ThinkingEffort = null,
    int RetryAttempt = 0,
    int MaxRetries = 0,
    string? LastRetryReason = null,
    string? SessionId = null,
    int? TaskId = null,
    string? StageId = null);

/// <summary>
/// Status of an operating system shell command intercepted by telemetry.
/// </summary>
public enum ShellCommandStatus
{
    Running,
    Completed,
    Failed
}

/// <summary>
/// Stage of self-healing escalation achieved during command timeout recovery.
/// </summary>
public enum ContainerRecoveryStage
{
    None = 0,
    ProcessSweepSucceeded = 1,
    ContainerRecreated = 2,
    WslShutdownRecovered = 3,
    RecoveryFailed = 4
}

/// <summary>
/// Telemetry record capturing a shell / CLI command execution.
/// </summary>
public record ShellCommandRecord(
    string CommandId,
    string CommandLine,
    string WorkingDirectory,
    DateTime StartTime,
    double DurationMs,
    int? ExitCode,
    ShellCommandStatus Status,
    string? OutputPreview,
    string? ErrorPreview,
    ContainerRecoveryStage RecoveryStage = ContainerRecoveryStage.None);
