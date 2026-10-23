using System.Collections.Concurrent;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Concurrency;

/// <summary>
/// Hierarchical keyed asynchronous lock provider ensuring:
/// 1. Workspace-level locks (AcquireLockAsync) are strictly exclusive across the canonical workspace,
///    serializing canonical Git and mirror synchronization operations to eliminate .git/index.lock races.
/// 2. Task-level locks (AcquireTaskLockAsync) are exclusive per taskId, serializing executions of the same
///    task without blocking independent tasks or workspace-level Git merge operations.
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
        public bool WriterActive;

        public readonly ConcurrentDictionary<int, SemaphoreSlim> TaskSemaphores = new();
        public readonly LinkedList<TaskCompletionSource<ActiveLockEntry>> WaitingWriters = new();

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
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetOrCreateState(workspaceId);
        TaskCompletionSource<ActiveLockEntry> waiter;

        lock (state.Sync)
        {
            if (!state.WriterActive && state.WaitingWriters.Count == 0)
            {
                state.WriterActive = true;
                return CreateWorkspaceReleaser(state, workspaceId);
            }

            waiter = new TaskCompletionSource<ActiveLockEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
            state.WaitingWriters.AddLast(waiter);
        }

        try
        {
            await using var reg = cancellationToken.Register(() =>
            {
                lock (state.Sync)
                {
                    if (waiter.TrySetCanceled(cancellationToken))
                    {
                        state.WaitingWriters.Remove(waiter);
                        Drain(state);
                    }
                }
            });

            var entry = await waiter.Task;

            cancellationToken.ThrowIfCancellationRequested();

            return new WorkspaceReleaser(this, entry);
        }
        catch
        {
            if (waiter.Task.IsCompletedSuccessfully)
            {
                // Granted but caller cancelled: give the lock back (no-op if emergency released).
                ReleaseWorkspace(waiter.Task.Result);
            }
            throw;
        }
    }

    public bool TryAcquireLock(int workspaceId, out IDisposable? lockReleaser)
    {
        var state = GetOrCreateState(workspaceId);
        lock (state.Sync)
        {
            if (!state.WriterActive && state.WaitingWriters.Count == 0)
            {
                state.WriterActive = true;
                lockReleaser = CreateWorkspaceReleaser(state, workspaceId);
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
        cancellationToken.ThrowIfCancellationRequested();

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
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetOrCreateState(workspaceId);
        var taskSem = state.GetTaskSemaphore(taskId);

        await taskSem.WaitAsync(cancellationToken);

        lock (state.Sync)
        {
            return CreateTaskReleaser(state, workspaceId, taskId, taskSem);
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
            lockReleaser = CreateTaskReleaser(state, workspaceId, taskId, taskSem);
            return true;
        }
    }

    public async Task<(bool Acquired, IDisposable? LockReleaser)> TryAcquireTaskLockAsync(
        int workspaceId,
        int taskId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetOrCreateState(workspaceId);
        var taskSem = state.GetTaskSemaphore(taskId);

        var acquired = await taskSem.WaitAsync(timeout, cancellationToken);
        if (!acquired)
        {
            return (false, null);
        }

        lock (state.Sync)
        {
            return (true, CreateTaskReleaser(state, workspaceId, taskId, taskSem));
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
                if (state.ActiveWorkspaceLock != null || state.WriterActive)
                {
                    if (state.ActiveWorkspaceLock != null)
                    {
                        state.ActiveWorkspaceLock.IsEmergencyReleased = true;
                        state.ActiveWorkspaceLock = null;
                    }
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
                if (state.ActiveWorkspaceLock != null || state.WriterActive)
                {
                    if (state.ActiveWorkspaceLock != null)
                    {
                        state.ActiveWorkspaceLock.IsEmergencyReleased = true;
                        state.ActiveWorkspaceLock = null;
                    }
                    state.WriterActive = false;
                    Drain(state);
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
            }
        }
    }

    private WorkspaceReleaser CreateWorkspaceReleaser(WorkspaceLockState state, int workspaceId)
    {
        var entry = CreateWorkspaceEntry(state, workspaceId);
        state.ActiveWorkspaceLock = entry;
        return new WorkspaceReleaser(this, entry);
    }

    private static ActiveLockEntry CreateWorkspaceEntry(WorkspaceLockState state, int workspaceId)
    {
        return new ActiveLockEntry
        {
            LockId = Guid.NewGuid().ToString("N"),
            Key = GetWorkspaceKey(workspaceId),
            WorkspaceId = workspaceId,
            TaskId = null,
            LockType = WorkspaceLockTypes.WorkspaceExclusive,
            AcquiredAt = DateTime.UtcNow,
            State = state
        };
    }

    private TaskReleaser CreateTaskReleaser(WorkspaceLockState state, int workspaceId, int taskId, SemaphoreSlim taskSem)
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
            var nextWriter = state.WaitingWriters.First!.Value;
            state.WaitingWriters.RemoveFirst();
            var entry = CreateWorkspaceEntry(state, state.WorkspaceId);
            if (nextWriter.TrySetResult(entry))
            {
                state.WriterActive = true;
                state.ActiveWorkspaceLock = entry;
                return;
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
