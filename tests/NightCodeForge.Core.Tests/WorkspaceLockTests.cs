using NightCodeForge.Core.Concurrency;

namespace NightCodeForge.Core.Tests;

public class WorkspaceLockTests
{
    [Fact]
    public async Task SameWorkspace_SerializesConcurrentExecutions()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 1;
        var executionLog = new List<string>();

        Task task2;
        var task2StartedWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var lock1 = await provider.AcquireLockAsync(workspaceId);
        try
        {
            task2 = Task.Run(async () =>
            {
                task2StartedWaiting.SetResult();
                using (await provider.AcquireLockAsync(workspaceId))
                {
                    lock (executionLog) executionLog.Add("Task2");
                }
            });

            await task2StartedWaiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(50);
            lock (executionLog) executionLog.Add("Task1");
        }
        finally
        {
            lock1.Dispose();
        }

        await task2.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, executionLog.Count);
        Assert.Equal("Task1", executionLog[0]);
        Assert.Equal("Task2", executionLog[1]);
    }

    [Fact]
    public async Task DifferentWorkspaces_ExecuteConcurrently()
    {
        var provider = new WorkspaceLockProvider();
        var workspace1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspace2Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task1 = Task.Run(async () =>
        {
            using (await provider.AcquireLockAsync(1))
            {
                workspace1Started.SetResult();
                await workspace2Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
        });

        var task2 = Task.Run(async () =>
        {
            using (await provider.AcquireLockAsync(2))
            {
                workspace2Started.SetResult();
                await workspace1Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
        });

        await Task.WhenAll(task1, task2).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TryAcquireLock_ReturnsFalseWhenLocked()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 42;

        using (await provider.AcquireLockAsync(workspaceId))
        {
            var acquired = provider.TryAcquireLock(workspaceId, out var releaser);
            Assert.False(acquired);
            Assert.Null(releaser);

            var (asyncAcquired, asyncReleaser) = await provider.TryAcquireLockAsync(workspaceId, TimeSpan.FromMilliseconds(50));
            Assert.False(asyncAcquired);
            Assert.Null(asyncReleaser);
        }

        // After release, TryAcquireLock succeeds
        var success = provider.TryAcquireLock(workspaceId, out var secondReleaser);
        Assert.True(success);
        Assert.NotNull(secondReleaser);
        secondReleaser?.Dispose();
    }

    [Fact]
    public async Task SameTask_SameWorkspace_SerializesConcurrentExecutions()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 1;
        const int taskId = 101;
        var executionLog = new List<string>();

        Task task2;
        var task2StartedWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var lock1 = await provider.AcquireTaskLockAsync(workspaceId, taskId);
        try
        {
            task2 = Task.Run(async () =>
            {
                task2StartedWaiting.SetResult();
                using (await provider.AcquireTaskLockAsync(workspaceId, taskId))
                {
                    lock (executionLog) executionLog.Add("TaskTask2");
                }
            });

            await task2StartedWaiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(50);
            lock (executionLog) executionLog.Add("TaskTask1");
        }
        finally
        {
            lock1.Dispose();
        }

        await task2.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, executionLog.Count);
        Assert.Equal("TaskTask1", executionLog[0]);
        Assert.Equal("TaskTask2", executionLog[1]);
    }

    [Fact]
    public async Task DifferentTasks_SameWorkspace_ExecuteConcurrently()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 1;
        var task1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task2Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task1 = Task.Run(async () =>
        {
            using (await provider.AcquireTaskLockAsync(workspaceId, 101))
            {
                task1Started.SetResult();
                await task2Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
        });

        var task2 = Task.Run(async () =>
        {
            using (await provider.AcquireTaskLockAsync(workspaceId, 102))
            {
                task2Started.SetResult();
                await task1Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
        });

        await Task.WhenAll(task1, task2).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TryAcquireTaskLock_ReturnsFalseWhenLocked()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 10;
        const int taskId = 99;

        using (await provider.AcquireTaskLockAsync(workspaceId, taskId))
        {
            var acquired = provider.TryAcquireTaskLock(workspaceId, taskId, out var releaser);
            Assert.False(acquired);
            Assert.Null(releaser);

            var (asyncAcquired, asyncReleaser) = await provider.TryAcquireTaskLockAsync(workspaceId, taskId, TimeSpan.FromMilliseconds(50));
            Assert.False(asyncAcquired);
            Assert.Null(asyncReleaser);
        }

        var success = provider.TryAcquireTaskLock(workspaceId, taskId, out var secondReleaser);
        Assert.True(success);
        Assert.NotNull(secondReleaser);
        secondReleaser?.Dispose();
    }

    [Fact]
    public async Task GetActiveLocks_ReflectsCurrentlyHeldLocks_AndClearsOnDispose()
    {
        var provider = new WorkspaceLockProvider();
        Assert.Empty(provider.GetActiveLocks());

        var lock1 = await provider.AcquireLockAsync(1);
        var lock2 = await provider.AcquireTaskLockAsync(2, 42);

        var active = provider.GetActiveLocks();
        Assert.Equal(2, active.Count);

        var wsLock = Assert.Single(active, l => l.WorkspaceId == 1);
        Assert.Null(wsLock.TaskId);
        Assert.Equal("WorkspaceExclusive", wsLock.LockType);
        Assert.True(wsLock.DurationHeld >= TimeSpan.Zero);

        var taskLock = Assert.Single(active, l => l.WorkspaceId == 2);
        Assert.Equal(42, taskLock.TaskId);
        Assert.Equal("TaskExclusive", taskLock.LockType);
        Assert.True(taskLock.DurationHeld >= TimeSpan.Zero);

        lock1.Dispose();
        active = provider.GetActiveLocks();
        Assert.Single(active);
        Assert.Equal(2, active[0].WorkspaceId);

        lock2.Dispose();
        Assert.Empty(provider.GetActiveLocks());
    }

    [Fact]
    public async Task EmergencyRelease_WorkspaceLock_ReleasesLockAndAllowsNewAcquisition()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 10;

        var originalReleaser = await provider.AcquireLockAsync(workspaceId);
        Assert.Single(provider.GetActiveLocks());

        // Attempting to acquire should fail
        Assert.False(provider.TryAcquireLock(workspaceId, out var blockedReleaser));
        Assert.Null(blockedReleaser);

        // Emergency release
        var released = provider.EmergencyRelease(workspaceId);
        Assert.True(released);
        Assert.Empty(provider.GetActiveLocks());

        // Now acquire should succeed immediately
        var acquired = provider.TryAcquireLock(workspaceId, out var newReleaser);
        Assert.True(acquired);
        Assert.NotNull(newReleaser);

        // Disposing original releaser after emergency release should not throw
        var exception = Record.Exception(() => originalReleaser.Dispose());
        Assert.Null(exception);

        newReleaser?.Dispose();
        Assert.Empty(provider.GetActiveLocks());
    }

    [Fact]
    public async Task EmergencyRelease_TaskLock_ReleasesLockAndAllowsNewAcquisition()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 15;
        const int taskId = 99;

        var originalReleaser = await provider.AcquireTaskLockAsync(workspaceId, taskId);
        Assert.Single(provider.GetActiveLocks());

        var released = provider.EmergencyRelease(workspaceId, taskId);
        Assert.True(released);
        Assert.Empty(provider.GetActiveLocks());

        var acquired = provider.TryAcquireTaskLock(workspaceId, taskId, out var newReleaser);
        Assert.True(acquired);
        Assert.NotNull(newReleaser);

        originalReleaser.Dispose();
        newReleaser?.Dispose();
    }

    [Fact]
    public async Task EmergencyReleaseAll_ClearsAllActiveLocks()
    {
        var provider = new WorkspaceLockProvider();
        var l1 = await provider.AcquireLockAsync(1);
        var l2 = await provider.AcquireLockAsync(2);
        var l3 = await provider.AcquireTaskLockAsync(3, 100);

        Assert.Equal(3, provider.GetActiveLocks().Count);

        provider.EmergencyReleaseAll();
        Assert.Empty(provider.GetActiveLocks());

        // Disposing original releasers does not throw
        l1.Dispose();
        l2.Dispose();
        l3.Dispose();
    }

    [Fact]
    public async Task ActiveTaskLock_DoesNotBlockExclusiveWorkspaceLockOnSameWorkspace()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 1;
        const int taskId = 101;

        // An active task lock (e.g. running LLM agent loop in isolated worktree)
        // must NOT block an exclusive workspace lock (e.g. SquashAndMergeAsync on canonical clone)
        using var taskLock = await provider.AcquireTaskLockAsync(workspaceId, taskId);

        // TryAcquireLock succeeds immediately without waiting
        var acquired = provider.TryAcquireLock(workspaceId, out var workspaceLock);
        Assert.True(acquired);
        Assert.NotNull(workspaceLock);

        workspaceLock?.Dispose();

        // Async acquisition also succeeds immediately
        using var asyncLock = await provider.AcquireLockAsync(workspaceId);
        Assert.NotNull(asyncLock);
    }

    [Fact]
    public async Task MultipleConcurrentTasks_DoNotBlockExclusiveWorkspaceLock()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 1;

        // Multiple tasks executing concurrently in isolated worktrees
        using var task1Lock = await provider.AcquireTaskLockAsync(workspaceId, 101);
        using var task2Lock = await provider.AcquireTaskLockAsync(workspaceId, 102);

        // Workspace-level lock (e.g. maintainer squash-merge in Review Room) acquires immediately
        using var exclusiveLock = await provider.AcquireLockAsync(workspaceId);
        Assert.NotNull(exclusiveLock);
    }

    [Fact]
    public async Task ExclusiveWorkspaceLock_DoesNotBlockTaskLocksOnSameWorkspace()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 1;
        const int taskId = 101;

        using var exclusiveLock = await provider.AcquireLockAsync(workspaceId);

        // Task in isolated worktree can still acquire task execution lock
        var acquired = provider.TryAcquireTaskLock(workspaceId, taskId, out var taskLock);
        Assert.True(acquired);
        Assert.NotNull(taskLock);
        taskLock?.Dispose();
    }

    [Fact]
    public async Task ExclusiveWorkspaceLock_DoesNotBlockTasksOnDifferentWorkspaces()
    {
        var provider = new WorkspaceLockProvider();

        using var ws1Lock = await provider.AcquireLockAsync(1);

        // Task on workspace 2 should acquire immediately
        var acquired = provider.TryAcquireTaskLock(2, 201, out var ws2TaskLock);
        Assert.True(acquired);
        Assert.NotNull(ws2TaskLock);
        ws2TaskLock?.Dispose();
    }

    [Fact]
    public async Task CancelledWorkspaceLockWaiters_DoNotLeakOrStarveRemainingWaiters()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 1;

        var holder = await provider.AcquireLockAsync(workspaceId);

        // Many waiters time out / cancel while the lock is held
        for (var i = 0; i < 20; i++)
        {
            var (acquired, releaser) = await provider.TryAcquireLockAsync(workspaceId, TimeSpan.FromMilliseconds(5));
            Assert.False(acquired);
            Assert.Null(releaser);
        }

        // One live waiter remains queued behind the holder
        var liveWaiter = provider.AcquireLockAsync(workspaceId);
        Assert.False(liveWaiter.IsCompleted);

        // A new caller must not barge ahead of the queued waiter
        Assert.False(provider.TryAcquireLock(workspaceId, out _));

        holder.Dispose();

        using var granted = await liveWaiter.WaitAsync(TimeSpan.FromSeconds(2));

        // While the live waiter holds it, nobody else can acquire
        Assert.False(provider.TryAcquireLock(workspaceId, out _));

        granted.Dispose();

        // Fully released: no phantom waiters remain
        Assert.True(provider.TryAcquireLock(workspaceId, out var final));
        final?.Dispose();
    }

    [Fact]
    public async Task AcquireLockAsync_WithPreCancelledToken_ThrowsAndLeavesLockAvailable()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 1;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.AcquireLockAsync(workspaceId, cts.Token));

        // Lock must remain free and immediately acquirable
        var acquired = provider.TryAcquireLock(workspaceId, out var releaser);
        Assert.True(acquired);
        Assert.NotNull(releaser);
        releaser?.Dispose();
    }

    [Fact]
    public async Task AcquireTaskLockAsync_WithPreCancelledToken_ThrowsAndLeavesTaskLockAvailable()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 1;
        const int taskId = 101;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.AcquireTaskLockAsync(workspaceId, taskId, cts.Token));

        var acquired = provider.TryAcquireTaskLock(workspaceId, taskId, out var releaser);
        Assert.True(acquired);
        Assert.NotNull(releaser);
        releaser?.Dispose();
    }

    [Fact]
    public async Task AcquireLockAsync_CancelledWhileWaiting_RemovesWaiterAndAllowsSubsequentAcquisitions()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 1;

        var holder = await provider.AcquireLockAsync(workspaceId);

        using var cts = new CancellationTokenSource();
        var waitingTask = provider.AcquireLockAsync(workspaceId, cts.Token);

        // Cancel while queued
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingTask);

        // Release holder
        holder.Dispose();

        // New acquisition succeeds immediately
        using var newLock = await provider.AcquireLockAsync(workspaceId);
        Assert.NotNull(newLock);
    }

    [Fact]
    public async Task TryAcquireTaskLockAsync_WithCancelledToken_ThrowsOperationCanceledException()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 1;
        const int taskId = 101;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.TryAcquireTaskLockAsync(workspaceId, taskId, TimeSpan.FromSeconds(1), cts.Token));

        var (acquired, releaser) = await provider.TryAcquireTaskLockAsync(workspaceId, taskId, TimeSpan.FromSeconds(1));
        Assert.True(acquired);
        Assert.NotNull(releaser);
        releaser?.Dispose();
    }

    [Fact]
    public async Task EmergencyRelease_WhenWriterActiveWithNullActiveLock_ReleasesLockAndAllowsNewAcquisition()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 1;

        var holder = await provider.AcquireLockAsync(workspaceId);
        Assert.Single(provider.GetActiveLocks());

        var released = provider.EmergencyRelease(workspaceId);
        Assert.True(released);
        Assert.Empty(provider.GetActiveLocks());

        // Calling EmergencyRelease again when not held returns false
        Assert.False(provider.EmergencyRelease(workspaceId));

        using var reacquired = await provider.AcquireLockAsync(workspaceId);
        Assert.NotNull(reacquired);
    }

    [Fact]
    public async Task EmergencyRelease_GrantsQueuedWaiter_AndStaleHolderDisposeDoesNotReleaseNewOwner()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 1;

        var staleHolder = await provider.AcquireLockAsync(workspaceId);
        var waiter = provider.AcquireLockAsync(workspaceId);
        Assert.False(waiter.IsCompleted);

        Assert.True(provider.EmergencyRelease(workspaceId));
        using var newOwner = await waiter.WaitAsync(TimeSpan.FromSeconds(2));

        // New owner is visible in telemetry as soon as it is granted
        Assert.Single(provider.GetActiveLocks());

        // Stale holder disposing must not free the new owner's lock
        staleHolder.Dispose();
        Assert.Single(provider.GetActiveLocks());
        Assert.False(provider.TryAcquireLock(workspaceId, out _));
    }
}

