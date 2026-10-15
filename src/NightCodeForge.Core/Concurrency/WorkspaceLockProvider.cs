using System.Collections.Concurrent;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Concurrency;

/// <summary>
/// Keyed asynchronous lock provider ensuring exclusive workspace access while allowing concurrent work across different workspaces.
/// Includes lock inspection telemetry and emergency release recovery mechanisms.
/// </summary>
public class WorkspaceLockProvider : IWorkspaceLockProvider
{
    private sealed class SemaphoreItem
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }

    private sealed class ActiveLockEntry
    {
        public required string LockId { get; init; }
        public required string Key { get; init; }
        public required int WorkspaceId { get; init; }
        public required int? TaskId { get; init; }
        public required string LockType { get; init; }
        public required DateTime AcquiredAt { get; init; }
        public required SemaphoreItem Item { get; init; }
        public bool IsEmergencyReleased { get; set; }
    }

    private readonly ConcurrentDictionary<string, SemaphoreItem> _locks = new();
    private readonly ConcurrentDictionary<string, ActiveLockEntry> _activeLocks = new();

    public Task<IDisposable> AcquireLockAsync(int workspaceId, CancellationToken cancellationToken = default)
        => AcquireInternalAsync(GetWorkspaceKey(workspaceId), workspaceId, null, "WorkspaceExclusive", cancellationToken);

    public bool TryAcquireLock(int workspaceId, out IDisposable? lockReleaser)
        => TryAcquireInternal(GetWorkspaceKey(workspaceId), workspaceId, null, "WorkspaceExclusive", out lockReleaser);

    public Task<(bool Acquired, IDisposable? LockReleaser)> TryAcquireLockAsync(
        int workspaceId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => TryAcquireInternalAsync(GetWorkspaceKey(workspaceId), workspaceId, null, "WorkspaceExclusive", timeout, cancellationToken);

    public Task<IDisposable> AcquireTaskLockAsync(int workspaceId, int taskId, CancellationToken cancellationToken = default)
        => AcquireInternalAsync(GetTaskKey(workspaceId, taskId), workspaceId, taskId, "TaskExclusive", cancellationToken);

    public bool TryAcquireTaskLock(int workspaceId, int taskId, out IDisposable? lockReleaser)
        => TryAcquireInternal(GetTaskKey(workspaceId, taskId), workspaceId, taskId, "TaskExclusive", out lockReleaser);

    public Task<(bool Acquired, IDisposable? LockReleaser)> TryAcquireTaskLockAsync(
        int workspaceId,
        int taskId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => TryAcquireInternalAsync(GetTaskKey(workspaceId, taskId), workspaceId, taskId, "TaskExclusive", timeout, cancellationToken);

    public IReadOnlyList<ActiveLockSnapshot> GetActiveLocks()
    {
        var now = DateTime.UtcNow;
        lock (_locks)
        {
            return _activeLocks.Values
                .Select(e => new ActiveLockSnapshot(
                    e.WorkspaceId,
                    e.TaskId,
                    e.LockType,
                    e.AcquiredAt,
                    now - e.AcquiredAt))
                .OrderBy(s => s.WorkspaceId)
                .ThenBy(s => s.TaskId ?? 0)
                .ToList();
        }
    }

    public bool EmergencyRelease(int workspaceId, int? taskId = null)
    {
        var key = taskId.HasValue ? GetTaskKey(workspaceId, taskId.Value) : GetWorkspaceKey(workspaceId);
        lock (_locks)
        {
            if (_activeLocks.TryRemove(key, out var entry))
            {
                entry.IsEmergencyReleased = true;
                if (entry.Item.Semaphore.CurrentCount == 0)
                {
                    entry.Item.Semaphore.Release();
                }
                DecrementRef(key, entry.Item);
                return true;
            }
            return false;
        }
    }

    public void EmergencyReleaseAll()
    {
        lock (_locks)
        {
            foreach (var key in _activeLocks.Keys.ToList())
            {
                if (_activeLocks.TryRemove(key, out var entry))
                {
                    entry.IsEmergencyReleased = true;
                    if (entry.Item.Semaphore.CurrentCount == 0)
                    {
                        entry.Item.Semaphore.Release();
                    }
                    DecrementRef(key, entry.Item);
                }
            }
        }
    }

    private static string GetWorkspaceKey(int workspaceId) => $"wks:{workspaceId}";
    private static string GetTaskKey(int workspaceId, int taskId) => $"wks:{workspaceId}:task:{taskId}";

    private async Task<IDisposable> AcquireInternalAsync(
        string key,
        int workspaceId,
        int? taskId,
        string lockType,
        CancellationToken cancellationToken)
    {
        var item = GetOrCreate(key);
        try
        {
            await item.Semaphore.WaitAsync(cancellationToken);
            return RegisterActiveLock(key, item, workspaceId, taskId, lockType);
        }
        catch
        {
            DecrementRef(key, item);
            throw;
        }
    }

    private bool TryAcquireInternal(
        string key,
        int workspaceId,
        int? taskId,
        string lockType,
        out IDisposable? lockReleaser)
    {
        var item = GetOrCreate(key);
        if (item.Semaphore.Wait(0))
        {
            lockReleaser = RegisterActiveLock(key, item, workspaceId, taskId, lockType);
            return true;
        }

        DecrementRef(key, item);
        lockReleaser = null;
        return false;
    }

    private async Task<(bool Acquired, IDisposable? LockReleaser)> TryAcquireInternalAsync(
        string key,
        int workspaceId,
        int? taskId,
        string lockType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var item = GetOrCreate(key);
        try
        {
            var acquired = await item.Semaphore.WaitAsync(timeout, cancellationToken);
            if (acquired)
            {
                return (true, RegisterActiveLock(key, item, workspaceId, taskId, lockType));
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

    private IDisposable RegisterActiveLock(
        string key,
        SemaphoreItem item,
        int workspaceId,
        int? taskId,
        string lockType)
    {
        var lockId = Guid.NewGuid().ToString("N");
        var entry = new ActiveLockEntry
        {
            LockId = lockId,
            Key = key,
            WorkspaceId = workspaceId,
            TaskId = taskId,
            LockType = lockType,
            AcquiredAt = DateTime.UtcNow,
            Item = item
        };

        lock (_locks)
        {
            _activeLocks[key] = entry;
        }

        return new Releaser(this, key, item, lockId);
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

    private void Release(string key, SemaphoreItem item, string lockId)
    {
        lock (_locks)
        {
            if (_activeLocks.TryGetValue(key, out var entry) && entry.LockId == lockId)
            {
                _activeLocks.TryRemove(key, out _);
                if (!entry.IsEmergencyReleased && item.Semaphore.CurrentCount == 0)
                {
                    item.Semaphore.Release();
                }
                DecrementRef(key, item);
            }
        }
    }

    private sealed class Releaser : IDisposable
    {
        private WorkspaceLockProvider? _provider;
        private readonly string _key;
        private readonly SemaphoreItem _item;
        private readonly string _lockId;
        private int _disposed;

        public Releaser(WorkspaceLockProvider provider, string key, SemaphoreItem item, string lockId)
        {
            _provider = provider;
            _key = key;
            _item = item;
            _lockId = lockId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && _provider != null)
            {
                _provider.Release(_key, _item, _lockId);
                _provider = null;
            }
        }
    }
}
