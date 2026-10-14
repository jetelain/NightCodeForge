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
    /// Asynchronously acquires an exclusive lock for a specific issue within a workspace, allowing concurrent execution of different issues.
    /// </summary>
    Task<IDisposable> AcquireIssueLockAsync(int workspaceId, int issueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously attempts to acquire an exclusive lock for a specific issue within a workspace.
    /// </summary>
    bool TryAcquireIssueLock(int workspaceId, int issueId, out IDisposable? lockReleaser);

    /// <summary>
    /// Asynchronously attempts to acquire an exclusive lock for a specific issue within the specified timeout.
    /// </summary>
    Task<(bool Acquired, IDisposable? LockReleaser)> TryAcquireIssueLockAsync(int workspaceId, int issueId, TimeSpan timeout, CancellationToken cancellationToken = default);
}
