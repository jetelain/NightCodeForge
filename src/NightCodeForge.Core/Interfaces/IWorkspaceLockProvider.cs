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
}
