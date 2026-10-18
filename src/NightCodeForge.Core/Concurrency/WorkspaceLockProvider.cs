using System.Collections.Concurrent;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Concurrency;

/// <summary>
/// Hierarchical keyed asynchronous lock provider ensuring:
/// 1. Workspace-level locks (AcquireLockAsync) are strictly exclusive across the workspace,
///    blocking all task locks and waiting for active tasks on that workspace to finish.
/// 2. Task-level locks (AcquireTaskLockAsync) are shared at the workspace tier (allowing concurrent
///    execution of different tasks on the same workspace) but exclusive per taskId.
/// Includes lock inspection telemetry and emergency release recovery mechanisms.
/// </summary>
public class WorkspaceLockProvider : IWorkspaceLockProvider
{
    private sealed class ActiveLockEntry
    {
        public required string LockId { get; init; }
        public required string Key { get; init; }
        public required int WorkspaceId { get; init; }
        public required int? TaskId { get; init; }
        public required string LockType { get; init; }
        public required DateTime AcquiredAt { get; init; }
        public required WorkspaceLockState State { get; init; }
        public SemaphoreSlim? TaskSemaphore { get; init; }
        public bool IsEmergencyReleased { get; set; }
    }

    private sealed class WorkspaceLockState
    {
        public readonly int WorkspaceId;
        public readonly object Sync = new();

        public ActiveLockEntry? ActiveWorkspaceLock;
        public readonly Dictionary<int, ActiveLockEntry> ActiveTaskLocks = new();
        public int ActiveReaders;
        public bool WriterActive;
        public int WaitingWritersCount;

        public readonly ConcurrentDictionary<int, SemaphoreSlim> TaskSemaphores = new();
        public readonly Queue<TaskCompletionSource<bool>> WaitingWriters = new();
        public readonly Queue<TaskCompletionSource<bool>> WaitingReaders = new();

        public WorkspaceLockState(int workspaceId)
        {
            WorkspaceId = workspaceId;
        }

        public SemaphoreSlim GetTaskSemaphore(int taskId)
        {
            return TaskSemaphores.GetOrAdd(taskId, _ => new SemaphoreSlim(1, 1));
        }
    }

    private readonly ConcurrentDictionary<int, WorkspaceLockState> _workspaceStates = new();

    public async Task<IDisposable> AcquireLockAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var state = GetOrCreateState(workspaceId);
        TaskCompletionSource<bool>? waiter = null;

        lock (state.Sync)
        {
            if (!state.WriterActive && state.ActiveReaders == 0 && state.WaitingWritersCount <= 0)
            {
                state.WriterActive = true;
            }
            else
            {
                state.WaitingWritersCount++;
                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                state.WaitingWriters.Enqueue(waiter);
            }
        }

        try
        {
            if (waiter != null)
            {
                await using var reg = cancellationToken.Register(() =>
                {
                    lock (state.Sync)
                    {
                        if (waiter.TrySetCanceled(cancellationToken))
                        {
                            state.WaitingWritersCount--;
                            if (state.WaitingWritersCount < 0) state.WaitingWritersCount = 0;
                            Drain(state);
                        }
                    }
                });

                await waiter.Task;
            }

            cancellationToken.ThrowIfCancellationRequested();

            lock (state.Sync)
            {
                var lockId = Guid.NewGuid().ToString("N");
                var entry = new ActiveLockEntry
                {
                    LockId = lockId,
                    Key = GetWorkspaceKey(workspaceId),
                    WorkspaceId = workspaceId,
                    TaskId = null,
                    LockType = WorkspaceLockTypes.WorkspaceExclusive,
                    AcquiredAt = DateTime.UtcNow,
                    State = state
                };
                state.ActiveWorkspaceLock = entry;
                return new WorkspaceReleaser(this, entry);
            }
        }
        catch
        {
            lock (state.Sync)
            {
                if (waiter != null && waiter.Task.IsCompletedSuccessfully)
                {
                    state.WriterActive = false;
                    Drain(state);
                }
                else if (waiter == null)
                {
                    state.WriterActive = false;
                    Drain(state);
                }
            }
            throw;
        }
    }

    public bool TryAcquireLock(int workspaceId, out IDisposable? lockReleaser)
    {
        var state = GetOrCreateState(workspaceId);
        lock (state.Sync)
        {
            if (!state.WriterActive && state.ActiveReaders == 0 && state.WaitingWritersCount <= 0)
            {
                state.WriterActive = true;
                var lockId = Guid.NewGuid().ToString("N");
                var entry = new ActiveLockEntry
                {
                    LockId = lockId,
                    Key = GetWorkspaceKey(workspaceId),
                    WorkspaceId = workspaceId,
                    TaskId = null,
                    LockType = WorkspaceLockTypes.WorkspaceExclusive,
                    AcquiredAt = DateTime.UtcNow,
                    State = state
                };
                state.ActiveWorkspaceLock = entry;
                lockReleaser = new WorkspaceReleaser(this, entry);
                return true;
            }

            lockReleaser = null;
            return false;
        }
    }

    public async Task<(bool Acquired, IDisposable? LockReleaser)> TryAcquireLockAsync(
        int workspaceId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var releaser = await AcquireLockAsync(workspaceId, timeoutCts.Token);
            return (true, releaser);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, null);
        }
    }

    public async Task<IDisposable> AcquireTaskLockAsync(int workspaceId, int taskId, CancellationToken cancellationToken = default)
    {
        var state = GetOrCreateState(workspaceId);
        var taskSem = state.GetTaskSemaphore(taskId);

        await taskSem.WaitAsync(cancellationToken);

        TaskCompletionSource<bool>? waiter = null;
        try
        {
            lock (state.Sync)
            {
                if (!state.WriterActive && state.WaitingWritersCount <= 0)
                {
                    state.ActiveReaders++;
                }
                else
                {
                    waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    state.WaitingReaders.Enqueue(waiter);
                }
            }

            if (waiter != null)
            {
                await using var reg = cancellationToken.Register(() =>
                {
                    lock (state.Sync)
                    {
                        waiter.TrySetCanceled(cancellationToken);
                    }
                });

                await waiter.Task;
            }

            cancellationToken.ThrowIfCancellationRequested();

            lock (state.Sync)
            {
                var lockId = Guid.NewGuid().ToString("N");
                var entry = new ActiveLockEntry
                {
                    LockId = lockId,
                    Key = GetTaskKey(workspaceId, taskId),
                    WorkspaceId = workspaceId,
                    TaskId = taskId,
                    LockType = WorkspaceLockTypes.TaskExclusive,
                    AcquiredAt = DateTime.UtcNow,
                    State = state,
                    TaskSemaphore = taskSem
                };
                state.ActiveTaskLocks[taskId] = entry;
                return new TaskReleaser(this, entry);
            }
        }
        catch
        {
            lock (state.Sync)
            {
                if (waiter != null && waiter.Task.IsCompletedSuccessfully)
                {
                    state.ActiveReaders--;
                    Drain(state);
                }
                else if (waiter == null)
                {
                    state.ActiveReaders--;
                    Drain(state);
                }
            }
            taskSem.Release();
            throw;
        }
    }

    public bool TryAcquireTaskLock(int workspaceId, int taskId, out IDisposable? lockReleaser)
    {
        var state = GetOrCreateState(workspaceId);
        var taskSem = state.GetTaskSemaphore(taskId);
        if (!taskSem.Wait(0))
        {
            lockReleaser = null;
            return false;
        }

        lock (state.Sync)
        {
            if (!state.WriterActive && state.WaitingWritersCount <= 0)
            {
                state.ActiveReaders++;
                var lockId = Guid.NewGuid().ToString("N");
                var entry = new ActiveLockEntry
                {
                    LockId = lockId,
                    Key = GetTaskKey(workspaceId, taskId),
                    WorkspaceId = workspaceId,
                    TaskId = taskId,
                    LockType = WorkspaceLockTypes.TaskExclusive,
                    AcquiredAt = DateTime.UtcNow,
                    State = state,
                    TaskSemaphore = taskSem
                };
                state.ActiveTaskLocks[taskId] = entry;
                lockReleaser = new TaskReleaser(this, entry);
                return true;
            }

            taskSem.Release();
            lockReleaser = null;
            return false;
        }
    }

    public async Task<(bool Acquired, IDisposable? LockReleaser)> TryAcquireTaskLockAsync(
        int workspaceId,
        int taskId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var releaser = await AcquireTaskLockAsync(workspaceId, taskId, timeoutCts.Token);
            return (true, releaser);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, null);
        }
    }

    public IReadOnlyList<ActiveLockSnapshot> GetActiveLocks()
    {
        var now = DateTime.UtcNow;
        var list = new List<ActiveLockSnapshot>();

        foreach (var state in _workspaceStates.Values)
        {
            lock (state.Sync)
            {
                if (state.ActiveWorkspaceLock != null)
                {
                    var entry = state.ActiveWorkspaceLock;
                    list.Add(new ActiveLockSnapshot(
                        entry.WorkspaceId,
                        null,
                        entry.LockType,
                        entry.AcquiredAt,
                        now - entry.AcquiredAt));
                }

                foreach (var entry in state.ActiveTaskLocks.Values)
                {
                    list.Add(new ActiveLockSnapshot(
                        entry.WorkspaceId,
                        entry.TaskId,
                        entry.LockType,
                        entry.AcquiredAt,
                        now - entry.AcquiredAt));
                }
            }
        }

        return list
            .OrderBy(s => s.WorkspaceId)
            .ThenBy(s => s.TaskId ?? 0)
            .ToList();
    }

    public bool EmergencyRelease(int workspaceId, int? taskId = null)
    {
        if (!_workspaceStates.TryGetValue(workspaceId, out var state))
        {
            return false;
        }

        lock (state.Sync)
        {
            if (!taskId.HasValue)
            {
                if (state.ActiveWorkspaceLock != null)
                {
                    var entry = state.ActiveWorkspaceLock;
                    entry.IsEmergencyReleased = true;
                    state.ActiveWorkspaceLock = null;
                    state.WriterActive = false;
                    Drain(state);
                    return true;
                }
                return false;
            }
            else
            {
                if (state.ActiveTaskLocks.TryGetValue(taskId.Value, out var entry))
                {
                    entry.IsEmergencyReleased = true;
                    state.ActiveTaskLocks.Remove(taskId.Value);
                    state.ActiveReaders--;
                    Drain(state);
                    if (entry.TaskSemaphore != null && entry.TaskSemaphore.CurrentCount == 0)
                    {
                        entry.TaskSemaphore.Release();
                    }
                    return true;
                }
                return false;
            }
        }
    }

    public void EmergencyReleaseAll()
    {
        foreach (var state in _workspaceStates.Values)
        {
            lock (state.Sync)
            {
                if (state.ActiveWorkspaceLock != null)
                {
                    state.ActiveWorkspaceLock.IsEmergencyReleased = true;
                    state.ActiveWorkspaceLock = null;
                    state.WriterActive = false;
                }

                foreach (var entry in state.ActiveTaskLocks.Values)
                {
                    entry.IsEmergencyReleased = true;
                    if (entry.TaskSemaphore != null && entry.TaskSemaphore.CurrentCount == 0)
                    {
                        entry.TaskSemaphore.Release();
                    }
                }
                state.ActiveTaskLocks.Clear();
                state.ActiveReaders = 0;

                Drain(state);
            }
        }
    }

    private WorkspaceLockState GetOrCreateState(int workspaceId)
    {
        return _workspaceStates.GetOrAdd(workspaceId, id => new WorkspaceLockState(id));
    }

    private static void Drain(WorkspaceLockState state)
    {
        if (state.WriterActive)
        {
            return;
        }

        while (state.WaitingWriters.Count > 0)
        {
            var peek = state.WaitingWriters.Peek();
            if (peek.Task.IsCompleted)
            {
                state.WaitingWriters.Dequeue();
                continue;
            }

            if (state.ActiveReaders == 0)
            {
                var nextWriter = state.WaitingWriters.Dequeue();
                state.WaitingWritersCount--;
                if (state.WaitingWritersCount < 0) state.WaitingWritersCount = 0;
                if (nextWriter.TrySetResult(true))
                {
                    state.WriterActive = true;
                    return;
                }
            }
            else
            {
                return;
            }
        }

        while (state.WaitingReaders.Count > 0)
        {
            var nextReader = state.WaitingReaders.Dequeue();
            if (!nextReader.Task.IsCompleted && nextReader.TrySetResult(true))
            {
                state.ActiveReaders++;
            }
        }
    }

    private void ReleaseWorkspace(ActiveLockEntry entry)
    {
        var state = entry.State;
        lock (state.Sync)
        {
            if (entry.IsEmergencyReleased)
            {
                return;
            }

            if (state.ActiveWorkspaceLock == entry)
            {
                state.ActiveWorkspaceLock = null;
                state.WriterActive = false;
                Drain(state);
            }
        }
    }

    private void ReleaseTask(ActiveLockEntry entry)
    {
        var state = entry.State;
        lock (state.Sync)
        {
            if (entry.IsEmergencyReleased)
            {
                return;
            }

            state.ActiveTaskLocks.Remove(entry.TaskId!.Value);
            state.ActiveReaders--;
            Drain(state);
        }

        entry.TaskSemaphore?.Release();
    }

    private static string GetWorkspaceKey(int workspaceId) => $"wks:{workspaceId}";
    private static string GetTaskKey(int workspaceId, int taskId) => $"wks:{workspaceId}:task:{taskId}";

    private sealed class WorkspaceReleaser : IDisposable
    {
        private WorkspaceLockProvider? _provider;
        private readonly ActiveLockEntry _entry;
        private int _disposed;

        public WorkspaceReleaser(WorkspaceLockProvider provider, ActiveLockEntry entry)
        {
            _provider = provider;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && _provider != null)
            {
                _provider.ReleaseWorkspace(_entry);
                _provider = null;
            }
        }
    }

    private sealed class TaskReleaser : IDisposable
    {
        private WorkspaceLockProvider? _provider;
        private readonly ActiveLockEntry _entry;
        private int _disposed;

        public TaskReleaser(WorkspaceLockProvider provider, ActiveLockEntry entry)
        {
            _provider = provider;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && _provider != null)
            {
                _provider.ReleaseTask(_entry);
                _provider = null;
            }
        }
    }
}
