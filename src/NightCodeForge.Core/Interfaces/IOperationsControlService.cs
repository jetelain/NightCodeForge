using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Controls global execution state across all background inference and deterministic queues,
/// coordinating graceful task suspension, GPU VRAM eviction, and resumption reconciliation.
/// </summary>
public interface IOperationsControlService
{
    /// <summary>
    /// Indicates whether background operations are currently paused by the user.
    /// </summary>
    bool IsPaused { get; }

    /// <summary>
    /// Current status of host GPU VRAM eviction.
    /// </summary>
    OperationsReclaimStatus ReclaimStatus { get; }

    /// <summary>
    /// Last error message if VRAM reclaim failed.
    /// </summary>
    string? LastReclaimError { get; }

    /// <summary>
    /// Suspends background operations, checkpointing in-flight tasks and unloading GPU VRAM.
    /// </summary>
    Task<bool> PauseOperationsAsync(string reason = "User initiated pause (Gamer / Dedicated Workload Mode)", CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes background operations, clears pause tracking, and re-enqueues waiting and interrupted tasks.
    /// </summary>
    Task<bool> ResumeOperationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the number of open tasks currently deferred by pause.
    /// </summary>
    Task<int> GetDeferredTaskCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Event triggered whenever the global operations pause or reclaim status changes.
    /// </summary>
    event Action<bool>? OnOperationsStateChanged;
}
