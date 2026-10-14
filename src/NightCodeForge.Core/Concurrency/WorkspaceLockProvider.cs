using System.Collections.Concurrent;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Concurrency;

/// <summary>
/// Keyed asynchronous lock provider ensuring exclusive workspace access while allowing concurrent work across different workspaces.
/// </summary>
public class WorkspaceLockProvider : IWorkspaceLockProvider
{
    private sealed class SemaphoreItem
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }

    private readonly ConcurrentDictionary<string, SemaphoreItem> _locks = new();

    public Task<IDisposable> AcquireLockAsync(int workspaceId, CancellationToken cancellationToken = default)
        => AcquireInternalAsync(GetWorkspaceKey(workspaceId), cancellationToken);

    public bool TryAcquireLock(int workspaceId, out IDisposable? lockReleaser)
        => TryAcquireInternal(GetWorkspaceKey(workspaceId), out lockReleaser);

    public Task<(bool Acquired, IDisposable? LockReleaser)> TryAcquireLockAsync(
        int workspaceId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => TryAcquireInternalAsync(GetWorkspaceKey(workspaceId), timeout, cancellationToken);

    public Task<IDisposable> AcquireTaskLockAsync(int workspaceId, int taskId, CancellationToken cancellationToken = default)
        => AcquireInternalAsync(GetTaskKey(workspaceId, taskId), cancellationToken);

    public bool TryAcquireTaskLock(int workspaceId, int taskId, out IDisposable? lockReleaser)
        => TryAcquireInternal(GetTaskKey(workspaceId, taskId), out lockReleaser);

    public Task<(bool Acquired, IDisposable? LockReleaser)> TryAcquireTaskLockAsync(
        int workspaceId,
        int taskId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => TryAcquireInternalAsync(GetTaskKey(workspaceId, taskId), timeout, cancellationToken);

    private static string GetWorkspaceKey(int workspaceId) => $"wks:{workspaceId}";
    private static string GetTaskKey(int workspaceId, int taskId) => $"wks:{workspaceId}:task:{taskId}";

    private async Task<IDisposable> AcquireInternalAsync(string key, CancellationToken cancellationToken)
    {
        var item = GetOrCreate(key);
        try
        {
            await item.Semaphore.WaitAsync(cancellationToken);
            return new Releaser(this, key, item);
        }
        catch
        {
            DecrementRef(key, item);
            throw;
        }
    }

    private bool TryAcquireInternal(string key, out IDisposable? lockReleaser)
    {
        var item = GetOrCreate(key);
        if (item.Semaphore.Wait(0))
        {
            lockReleaser = new Releaser(this, key, item);
            return true;
        }

        DecrementRef(key, item);
        lockReleaser = null;
        return false;
    }

    private async Task<(bool Acquired, IDisposable? LockReleaser)> TryAcquireInternalAsync(
        string key,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var item = GetOrCreate(key);
        try
        {
            var acquired = await item.Semaphore.WaitAsync(timeout, cancellationToken);
            if (acquired)
            {
                return (true, new Releaser(this, key, item));
            }

            DecrementRef(key, item);
            return (false, null);
        }
        catch
        {
            DecrementRef(key, item);
            throw;
        }
    }

    private SemaphoreItem GetOrCreate(string key)
    {
        lock (_locks)
        {
            if (!_locks.TryGetValue(key, out var item))
            {
                item = new SemaphoreItem();
                _locks[key] = item;
            }
            item.RefCount++;
            return item;
        }
    }

    private void DecrementRef(string key, SemaphoreItem item)
    {
        lock (_locks)
        {
            item.RefCount--;
            if (item.RefCount <= 0)
            {
                _locks.TryRemove(key, out _);
                item.Semaphore.Dispose();
            }
        }
    }

    private void Release(string key, SemaphoreItem item)
    {
        item.Semaphore.Release();
        DecrementRef(key, item);
    }

    private sealed class Releaser : IDisposable
    {
        private WorkspaceLockProvider? _provider;
        private readonly string _key;
        private readonly SemaphoreItem _item;
        private int _disposed;

        public Releaser(WorkspaceLockProvider provider, string key, SemaphoreItem item)
        {
            _provider = provider;
            _key = key;
            _item = item;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && _provider != null)
            {
                _provider.Release(_key, _item);
                _provider = null;
            }
        }
    }
}
