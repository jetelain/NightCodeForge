namespace NightCodeForge.Web.Lifecycle;

using NightCodeForge.Infrastructure.Configuration;

/// <summary>
/// Persisted runtime phase of a NightCodeForge web host instance.
/// </summary>
public enum InstanceLifecycleStatus
{
    Running = 0,
    Stopping = 1,
    StoppedCleanly = 2,
    Crashed = 3,
    ExitedWithoutGracefulShutdown = 4
}

/// <summary>
/// Classification of the previous instance's persisted lifecycle state inspected at startup.
/// </summary>
public enum PreviousInstanceAssessmentKind
{
    None = 0,
    StoppedCleanly = 1,
    Crashed = 2,
    TerminatedDuringShutdown = 3,
    ExitedWithoutGracefulShutdown = 4,
    UncleanTerminationDetected = 5,
    AnotherInstanceStillRunning = 6,
    CorruptedStateFile = 7
}

/// <summary>
/// JSON-serializable marker representing the current or previous host run's lifecycle state.
/// </summary>
public sealed record InstanceLifecycleState
{
    public required int ProcessId { get; init; }
    public required string SessionId { get; init; }
    public required string Version { get; init; }
    public required string EnvironmentName { get; init; }
    public required string ContentRootPath { get; init; }
    public required DateTimeOffset ProcessStartedAtUtc { get; init; }
    public required DateTimeOffset RecordedStartedAtUtc { get; init; }
    public DateTimeOffset? StoppingAtUtc { get; init; }
    public DateTimeOffset? StoppedAtUtc { get; init; }
    public required InstanceLifecycleStatus Status { get; init; }
    public string? ShutdownReason { get; init; }
    public int? ExitCode { get; init; }
    public string? ExceptionType { get; init; }
    public string? ExceptionMessage { get; init; }
}

/// <summary>
/// Result of assessing the persisted lifecycle state file from an earlier run.
/// </summary>
public sealed record PreviousInstanceAssessment(
    PreviousInstanceAssessmentKind Kind,
    InstanceLifecycleState? PreviousState,
    string? DiagnosticError);

/// <summary>
/// Immutable metadata describing the running host instance for lifecycle logging.
/// </summary>
public sealed record HostLifecycleInfo(
    int ProcessId,
    string SessionId,
    string Version,
    string EnvironmentName,
    string ContentRootPath,
    string LogsDirectory,
    UserConfigLoadStatus UserConfigStatus,
    DateTimeOffset ProcessStartedAtUtc,
    string OsDescription,
    string FrameworkDescription);
