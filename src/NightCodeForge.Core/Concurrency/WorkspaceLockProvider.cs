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

    private readonly ConcurrentDictionary<int, SemaphoreItem> _locks = new();

    public async Task<IDisposable> AcquireLockAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var item = GetOrCreate(workspaceId);
        try
        {
            await item.Semaphore.WaitAsync(cancellationToken);
            return new Releaser(this, workspaceId, item);
        }
        catch
        {
            DecrementRef(workspaceId, item);
            throw;
        }
    }

    public bool TryAcquireLock(int workspaceId, out IDisposable? lockReleaser)
    {
        var item = GetOrCreate(workspaceId);
        if (item.Semaphore.Wait(0))
        {
            lockReleaser = new Releaser(this, workspaceId, item);
            return true;
        }

        DecrementRef(workspaceId, item);
        lockReleaser = null;
        return false;
    }

    public async Task<(bool Acquired, IDisposable? LockReleaser)> TryAcquireLockAsync(
        int workspaceId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var item = GetOrCreate(workspaceId);
        try
        {
            var acquired = await item.Semaphore.WaitAsync(timeout, cancellationToken);
            if (acquired)
            {
                return (true, new Releaser(this, workspaceId, item));
            }

            DecrementRef(workspaceId, item);
            return (false, null);
        }
        catch
        {
            DecrementRef(workspaceId, item);
            throw;
        }
    }

    private SemaphoreItem GetOrCreate(int workspaceId)
    {
        lock (_locks)
        {
            if (!_locks.TryGetValue(workspaceId, out var item))
            {
                item = new SemaphoreItem();
                _locks[workspaceId] = item;
            }
            item.RefCount++;
            return item;
        }
    }

    private void DecrementRef(int workspaceId, SemaphoreItem item)
    {
        lock (_locks)
        {
            item.RefCount--;
            if (item.RefCount <= 0)
            {
                _locks.TryRemove(workspaceId, out _);
                item.Semaphore.Dispose();
            }
        }
    }

    private void Release(int workspaceId, SemaphoreItem item)
    {
        item.Semaphore.Release();
        DecrementRef(workspaceId, item);
    }

    private sealed class Releaser : IDisposable
    {
        private WorkspaceLockProvider? _provider;
        private readonly int _workspaceId;
        private readonly SemaphoreItem _item;
        private int _disposed;

        public Releaser(WorkspaceLockProvider provider, int workspaceId, SemaphoreItem item)
        {
            _provider = provider;
            _workspaceId = workspaceId;
            _item = item;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && _provider != null)
            {
                _provider.Release(_workspaceId, _item);
                _provider = null;
            }
        }
    }
}
