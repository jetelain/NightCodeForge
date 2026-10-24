namespace NightCodeForge.Core.Domain;

/// <summary>
/// Canonical machine status strings for background orchestrator worker states
/// communicated via SignalR (<c>ReceiveWorkerStatus</c>) and local UI state services (<c>HandleWorkerStatus</c>).
/// </summary>
public static class WorkerStatusConstants
{
    public const string Idle = "Idle";
    public const string Running = "Running";
    public const string WaitingForHuman = "WaitingForHuman";
    public const string Blocked = "Blocked";

    /// <summary>
    /// Checks if the status matches Idle (case-insensitive or whitespace/null).
    /// </summary>
    public static bool IsIdle(string? status) =>
        string.IsNullOrWhiteSpace(status) || string.Equals(status, Idle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if the status matches Running (case-insensitive).
    /// </summary>
    public static bool IsRunning(string? status) =>
        string.Equals(status, Running, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if the status matches WaitingForHuman (case-insensitive, including legacy aliases).
    /// </summary>
    public static bool IsWaitingForHuman(string? status) =>
        string.Equals(status, WaitingForHuman, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "waiting_for_human", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "waiting", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if the status matches Blocked (case-insensitive).
    /// </summary>
    public static bool IsBlocked(string? status) =>
        string.Equals(status, Blocked, StringComparison.OrdinalIgnoreCase);
}
