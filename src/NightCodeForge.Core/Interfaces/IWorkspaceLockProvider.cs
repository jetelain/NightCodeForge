namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Provides workspace-scoped asynchronous locking to prevent concurrent Git operations and index race conditions.
/// </summary>
public interface IWorkspaceLockProvider
{
    /// <summary>
    /// Asynchronously acquires an exclusive lock for the specified workspace.
    /// </summary>
    Task<IDisposable> AcquireLockAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously attempts to acquire an exclusive lock for the specified workspace without waiting.
    /// </summary>
    bool TryAcquireLock(int workspaceId, out IDisposable? lockReleaser);

    /// <summary>
    /// Asynchronously attempts to acquire an exclusive lock within the specified timeout.
    /// </summary>
    Task<(bool Acquired, IDisposable? LockReleaser)> TryAcquireLockAsync(int workspaceId, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously acquires an exclusive lock for a specific task within a workspace, allowing concurrent execution of different tasks.
    /// </summary>
    Task<IDisposable> AcquireTaskLockAsync(int workspaceId, int taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously attempts to acquire an exclusive lock for a specific task within a workspace.
    /// </summary>
    bool TryAcquireTaskLock(int workspaceId, int taskId, out IDisposable? lockReleaser);

    /// <summary>
    /// Asynchronously attempts to acquire an exclusive lock for a specific task within the specified timeout.
    /// </summary>
    Task<(bool Acquired, IDisposable? LockReleaser)> TryAcquireTaskLockAsync(int workspaceId, int taskId, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a real-time snapshot of all currently acquired workspace and task locks.
    /// </summary>
    IReadOnlyList<ActiveLockSnapshot> GetActiveLocks();

    /// <summary>
    /// Forcibly releases an active lock held for the given workspace and optional task, recovering from hung or orphaned states.
    /// </summary>
    bool EmergencyRelease(int workspaceId, int? taskId = null);

    /// <summary>
    /// Forcibly releases all active workspace and task locks.
    /// </summary>
    void EmergencyReleaseAll();
}

/// <summary>
/// Snapshot representing an active keyed concurrency lock.
/// </summary>
/// <param name="WorkspaceId">The target workspace identifier.</param>
/// <param name="TaskId">The optional task identifier for task-scoped locks.</param>
/// <param name="LockType">Lock tier ("WorkspaceExclusive" or "TaskExclusive").</param>
/// <param name="AcquiredAt">Timestamp when the lock was acquired (UTC).</param>
/// <param name="DurationHeld">Duration the lock has been held so far.</param>
public record ActiveLockSnapshot(
    int WorkspaceId,
    int? TaskId,
    string LockType,
    DateTime AcquiredAt,
    TimeSpan DurationHeld);

