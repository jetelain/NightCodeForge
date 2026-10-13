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

        const int workspace1 = 1;
        const int workspace2 = 2;

        // Test 1: Strict Mutual Exclusion on the Same Workspace
        ConsoleFormatting.PrintStep("8.1", $"Testing mutual exclusion with 5 concurrent tasks on Workspace #{workspace1}...");

        int activeLockHolders = 0;
        int maxSimultaneousHolders = 0;
        int completedTasks = 0;

        var tasks = Enumerable.Range(0, 5).Select(async i =>
        {
            using var releaser = await lockProvider.AcquireLockAsync(workspace1, context.CancellationToken);

            var current = Interlocked.Increment(ref activeLockHolders);
            lock (details)
            {
                if (current > maxSimultaneousHolders)
                {
                    maxSimultaneousHolders = current;
                }
            }

            // Simulate working tree work
            await Task.Delay(30, context.CancellationToken);

            Interlocked.Decrement(ref activeLockHolders);
            Interlocked.Increment(ref completedTasks);
        }).ToArray();

        await Task.WhenAll(tasks);

        if (maxSimultaneousHolders > 1)
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Violation: Workspace #{workspace1} had {maxSimultaneousHolders} simultaneous lock holders!");
        }

        ConsoleFormatting.PrintSuccess($"Mutual exclusion verified! Max simultaneous holders = {maxSimultaneousHolders} (100% serialized), Completed = {completedTasks}");
        details.Add($"Same Workspace: Serialized (Max holders = {maxSimultaneousHolders})");

        // Test 2: Concurrency Across Independent Workspaces
        ConsoleFormatting.PrintStep("8.2", $"Testing non-blocking concurrent locks across Workspace #{workspace1} and #{workspace2}...");

        bool ws1Holding = false;
        bool overlapped = false;

        using var ws1Lock = await lockProvider.AcquireLockAsync(workspace1, context.CancellationToken);
        ws1Holding = true;

        // In parallel, acquire lock on Workspace #2 (should succeed immediately without waiting)
        var ws2Task = Task.Run(async () =>
        {
            using var ws2Lock = await lockProvider.AcquireLockAsync(workspace2, context.CancellationToken);
            if (ws1Holding)
            {
                overlapped = true;
            }
            await Task.Delay(20, context.CancellationToken);
        }, context.CancellationToken);

        await ws2Task;

        if (!overlapped)
        {
            return ScenarioResult.Fail(sw.Elapsed, "Workspace #2 lock was unexpectedly blocked by Workspace #1.");
        }

        ConsoleFormatting.PrintSuccess($"Concurrent independent workspaces verified! Workspace #{workspace1} and #{workspace2} held locks simultaneously without contention.");
        details.Add("Multi-Workspace: Fully concurrent (No cross-workspace contention)");

        sw.Stop();
        return ScenarioResult.Pass(sw.Elapsed, "Per-workspace mutual exclusion and multi-workspace concurrency isolation verified.", details.ToArray());
    }
}
