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
    public async Task SameIssue_SameWorkspace_SerializesConcurrentExecutions()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 1;
        const int issueId = 101;
        var executionLog = new List<string>();

        Task task2;
        var task2StartedWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var lock1 = await provider.AcquireIssueLockAsync(workspaceId, issueId);
        try
        {
            task2 = Task.Run(async () =>
            {
                task2StartedWaiting.SetResult();
                using (await provider.AcquireIssueLockAsync(workspaceId, issueId))
                {
                    lock (executionLog) executionLog.Add("IssueTask2");
                }
            });

            await task2StartedWaiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(50);
            lock (executionLog) executionLog.Add("IssueTask1");
        }
        finally
        {
            lock1.Dispose();
        }

        await task2.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, executionLog.Count);
        Assert.Equal("IssueTask1", executionLog[0]);
        Assert.Equal("IssueTask2", executionLog[1]);
    }

    [Fact]
    public async Task DifferentIssues_SameWorkspace_ExecuteConcurrently()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 1;
        var issue1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var issue2Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task1 = Task.Run(async () =>
        {
            using (await provider.AcquireIssueLockAsync(workspaceId, 101))
            {
                issue1Started.SetResult();
                await issue2Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
        });

        var task2 = Task.Run(async () =>
        {
            using (await provider.AcquireIssueLockAsync(workspaceId, 102))
            {
                issue2Started.SetResult();
                await issue1Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
        });

        await Task.WhenAll(task1, task2).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TryAcquireIssueLock_ReturnsFalseWhenLocked()
    {
        var provider = new WorkspaceLockProvider();
        const int workspaceId = 10;
        const int issueId = 99;

        using (await provider.AcquireIssueLockAsync(workspaceId, issueId))
        {
            var acquired = provider.TryAcquireIssueLock(workspaceId, issueId, out var releaser);
            Assert.False(acquired);
            Assert.Null(releaser);

            var (asyncAcquired, asyncReleaser) = await provider.TryAcquireIssueLockAsync(workspaceId, issueId, TimeSpan.FromMilliseconds(50));
            Assert.False(asyncAcquired);
            Assert.Null(asyncReleaser);
        }

        var success = provider.TryAcquireIssueLock(workspaceId, issueId, out var secondReleaser);
        Assert.True(success);
        Assert.NotNull(secondReleaser);
        secondReleaser?.Dispose();
    }
}
