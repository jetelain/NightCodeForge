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
}
