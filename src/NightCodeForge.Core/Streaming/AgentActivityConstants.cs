namespace NightCodeForge.Core.Streaming;

/// <summary>
/// Canonical constants used in agent activity events and streaming telemetry.
/// </summary>
public static class AgentActivityConstants
{
    /// <summary>
    /// Prefix content indicating operations were paused (e.g. for GPU VRAM release/reclaim).
    /// </summary>
    public const string OperationsPausedPrefix = "Operations paused";

    /// <summary>
    /// Action type metadata value indicating a task was dynamically suspended due to unclosed prerequisites.
    /// </summary>
    public const string ActionTypeTaskSuspended = "TaskSuspended";

    /// <summary>
    /// SignalR stage transition status indicating a task stage run was suspended.
    /// </summary>
    public const string StageStatusSuspended = "Suspended";

    /// <summary>
    /// Canonical audit reason prefix recorded when a task is suspended on unclosed prerequisites.
    /// </summary>
    public const string SuspendedAuditReasonPrefix = "Task suspended: waiting for unclosed prerequisites";
}
