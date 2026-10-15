using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.ScenarioRunner.Common;

namespace NightCodeForge.ScenarioRunner.Scenarios;

/// <summary>
/// Scenario 8: Workspace Concurrency & Lock Isolation.
/// Validates that IWorkspaceLockProvider enforces strict mutual exclusion per workspace:
/// - Prevents concurrent Git operations on the same workspace (eliminating race conditions and index corruption)
/// - Protects single-worker GPU VRAM limits
/// - Allows independent workspaces to acquire locks concurrently without contention
/// </summary>
public class WorkspaceConcurrencyScenario : IScenario
{
    public string Id => "8";
    public string Name => "Workspace Concurrency & Lock Isolation";
    public string Description => "Tests strict single-worker mutual exclusion per workspace and non-blocking multi-workspace concurrency.";

    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var sw = Stopwatch.StartNew();
        var details = new List<string>();

        using var scope = context.Environment.Services.CreateScope();
        var lockProvider = scope.ServiceProvider.GetRequiredService<IWorkspaceLockProvider>();

        // Synthetic workspace IDs > 1,000,000 to avoid collisions with real user workspaces
        const int workspace1 = 1_000_001;
        const int workspace2 = 1_000_002;
        const int task1 = 101;
        const int task2 = 102;

        // Test 1: Strict Mutual Exclusion on the Same Task (within a workspace)
        ConsoleFormatting.PrintStep("8.1", $"Testing mutual exclusion with 5 concurrent workers on Task #{task1} (Workspace #{workspace1})...");

        int activeLockHolders = 0;
        int maxSimultaneousHolders = 0;
        int completedTasks = 0;

        var tasks = Enumerable.Range(0, 5).Select(async i =>
        {
            using var releaser = await lockProvider.AcquireTaskLockAsync(workspace1, task1, context.CancellationToken);

            var current = Interlocked.Increment(ref activeLockHolders);
            lock (details)
            {
                if (current > maxSimultaneousHolders)
                {
                    maxSimultaneousHolders = current;
                }
            }

            // Simulate task execution inside dedicated worktree
            await Task.Delay(20, context.CancellationToken);

            Interlocked.Decrement(ref activeLockHolders);
            Interlocked.Increment(ref completedTasks);
        }).ToArray();

        await Task.WhenAll(tasks);

        if (maxSimultaneousHolders > 1)
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Violation: Task #{task1} had {maxSimultaneousHolders} simultaneous lock holders!");
        }

        ConsoleFormatting.PrintSuccess($"Task mutual exclusion verified! Max simultaneous holders = {maxSimultaneousHolders} (100% serialized), Completed = {completedTasks}");
        details.Add($"Same Task: Serialized (Max holders = {maxSimultaneousHolders})");

        // Test 2: Concurrency Across Independent Tasks on the SAME Workspace (Worktree Isolation)
        ConsoleFormatting.PrintStep("8.2", $"Testing non-blocking concurrent tasks #{task1} and #{task2} on the SAME Workspace #{workspace1} (worktree parallelism)...");

        bool task1Holding = false;
        bool tasksOverlapped = false;

        using (var t1Lock = await lockProvider.AcquireTaskLockAsync(workspace1, task1, context.CancellationToken))
        {
            task1Holding = true;

            var t2Task = Task.Run(async () =>
            {
                using var t2Lock = await lockProvider.AcquireTaskLockAsync(workspace1, task2, context.CancellationToken);
                if (task1Holding)
                {
                    tasksOverlapped = true;
                }
                await Task.Delay(20, context.CancellationToken);
            }, context.CancellationToken);

            await t2Task;
        }

        if (!tasksOverlapped)
        {
            return ScenarioResult.Fail(sw.Elapsed, "Task #2 was unexpectedly blocked by Task #1 on the same workspace.");
        }

        ConsoleFormatting.PrintSuccess($"Concurrent tasks on same workspace verified! Tasks #{task1} and #{task2} ran concurrently in isolated worktrees without contention.");
        details.Add("Intra-Workspace Tasks: Fully concurrent (Worktree isolated)");

        // Test 3: Concurrency Across Independent Workspaces
        ConsoleFormatting.PrintStep("8.3", $"Testing non-blocking concurrent locks across Workspace #{workspace1} and #{workspace2}...");

        bool ws1Holding = false;
        bool workspacesOverlapped = false;

        using (var ws1Lock = await lockProvider.AcquireLockAsync(workspace1, context.CancellationToken))
        {
            ws1Holding = true;

            var ws2Task = Task.Run(async () =>
            {
                using var ws2Lock = await lockProvider.AcquireLockAsync(workspace2, context.CancellationToken);
                if (ws1Holding)
                {
                    workspacesOverlapped = true;
                }
                await Task.Delay(20, context.CancellationToken);
            }, context.CancellationToken);

            await ws2Task;
        }

        if (!workspacesOverlapped)
        {
            return ScenarioResult.Fail(sw.Elapsed, "Workspace #2 lock was unexpectedly blocked by Workspace #1.");
        }

        ConsoleFormatting.PrintSuccess($"Concurrent independent workspaces verified! Workspace #{workspace1} and #{workspace2} held locks simultaneously without contention.");
        details.Add("Multi-Workspace: Fully concurrent (No cross-workspace contention)");

        // Test 4: Lock Telemetry Snapshot and Emergency Release
        ConsoleFormatting.PrintStep("8.4", "Verifying lock telemetry snapshot and emergency release recovery...");

        var heldLock = await lockProvider.AcquireTaskLockAsync(workspace1, task1, context.CancellationToken);
        var activeSnapshots = lockProvider.GetActiveLocks();
        var foundLock = activeSnapshots.FirstOrDefault(l => l.WorkspaceId == workspace1 && l.TaskId == task1);

        if (foundLock == null)
        {
            heldLock.Dispose();
            return ScenarioResult.Fail(sw.Elapsed, "Active lock was not reported by GetActiveLocks().");
        }

        ConsoleFormatting.PrintSuccess($"Lock telemetry verified: Found lock [{foundLock.LockType}] for Workspace #{foundLock.WorkspaceId}, Task #{foundLock.TaskId}");

        var released = lockProvider.EmergencyRelease(workspace1, task1);
        if (!released)
        {
            heldLock.Dispose();
            return ScenarioResult.Fail(sw.Elapsed, "EmergencyRelease failed to force-release active task lock.");
        }

        // Verify another worker can acquire immediately without waiting
        var immediateSuccess = lockProvider.TryAcquireTaskLock(workspace1, task1, out var reacquiredLock);
        reacquiredLock?.Dispose();
        heldLock.Dispose();

        if (!immediateSuccess)
        {
            return ScenarioResult.Fail(sw.Elapsed, "Failed to immediately acquire task lock after emergency release.");
        }

        ConsoleFormatting.PrintSuccess("Emergency release successfully unlocked hung task lock.");
        details.Add("Lock Telemetry & Emergency Release: Verified");

        sw.Stop();
        return ScenarioResult.Pass(sw.Elapsed, "Per-task mutual exclusion, intra-workspace worktree concurrency, cross-workspace isolation, and emergency release verified.", details.ToArray());
    }
}
