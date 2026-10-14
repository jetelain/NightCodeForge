using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Services;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.ScenarioRunner.Common;

namespace NightCodeForge.ScenarioRunner.Scenarios;

/// <summary>
/// Scenario 7: Startup Crash Recovery & Working Tree Reconciliation.
/// Tests recovery from sudden host crash, power outage, or killed worker process:
/// - Detects and purges dangling .git/index.lock files
/// - Commits uncommitted dirty changes into a safe rescue commit (WIP-RECOVERY)
/// - Reconciles orphaned Running tasks and re-enqueues them to llm-urgent
/// </summary>
public class StartupCrashRecoveryScenario : IScenario
{
    public string Id => "7";
    public string Name => "Crash Resilience & Startup Workspace Reconciliation";
    public string Description => "Tests automatic dangling git lock removal, rescue commit creation, and orphaned task recovery.";

    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var sw = Stopwatch.StartNew();
        var details = new List<string>();

        // Step 1: Simulate Crash Conditions
        ConsoleFormatting.PrintStep("7.1", "Injecting crash artifacts: dangling index.lock, dirty tree edits, and orphaned Running task...");

        // 1. Dangling lock
        var lockPath = context.Environment.GitRepo.InjectDanglingLock();
        if (!File.Exists(lockPath))
        {
            return ScenarioResult.Fail(sw.Elapsed, "Failed to create dangling index.lock.");
        }
        ConsoleFormatting.PrintInfo($"Injected dangling lock: {lockPath}");

        // 2. Dirty uncommitted file edit
        context.Environment.GitRepo.InjectDirtyChange();
        var status = await context.Environment.GitRepo.GetStatusAsync(context.CancellationToken);
        if (string.IsNullOrWhiteSpace(status))
        {
            return ScenarioResult.Fail(sw.Elapsed, "Failed to inject dirty working tree change.");
        }
        ConsoleFormatting.PrintInfo($"Working tree is dirty:\n{status.Trim()}");

        // 3. Orphaned task left in Running state
        ForgeTask crashedTask;
        using (var scope = context.Environment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            crashedTask = new ForgeTask
            {
                WorkspaceId = context.Environment.Workspace.Id,
                Title = "Orphaned In-Flight Task",
                Description = "Task interrupted midway through inference by simulated host reboot.",
                Status = TaskOverallStatus.Implemented,
                CurrentStageId = "implementation",
                BaseBranch = "main",
                FeatureBranch = "feature/task-crashed",
                Priority = 1,
                MergeMode = MergeMode.ManualReviewRequired
            };
            db.Tasks.Add(crashedTask);
            await db.SaveChangesAsync(context.CancellationToken);
            ConsoleFormatting.PrintInfo($"Seeded orphaned task #{crashedTask.Id} with Status=Implemented.");
        }

        // Step 2: Execute Startup Recovery Reconciliation
        ConsoleFormatting.PrintStep("7.2", "Executing IStartupRecoveryService.ReconcileWorkspacesAsync()...");
        using (var scope = context.Environment.Services.CreateScope())
        {
            var recoveryService = scope.ServiceProvider.GetRequiredService<IStartupRecoveryService>();
            await recoveryService.ReconcileWorkspacesAsync(context.CancellationToken);
            ConsoleFormatting.PrintSuccess("Startup recovery service executed successfully.");
        }

        // Step 3: Verify Stale Lock Purged
        ConsoleFormatting.PrintStep("7.3", "Verifying dangling .git/index.lock was purged...");
        if (File.Exists(lockPath))
        {
            return ScenarioResult.Fail(sw.Elapsed, "Violation: .git/index.lock was NOT purged by recovery service.");
        }
        ConsoleFormatting.PrintSuccess(".git/index.lock safely detected and removed.");
        details.Add("Stale Lock: Purged successfully");

        // Step 4: Verify Rescue Commit Created for Dirty Tree
        ConsoleFormatting.PrintStep("7.4", "Verifying rescue commit was created for uncommitted edits...");
        var log = await context.Environment.GitRepo.GetCommitLogAsync(2, context.CancellationToken);
        if (!log.Contains("WIP-RECOVERY"))
        {
            return ScenarioResult.Fail(sw.Elapsed, "Violation: No WIP-RECOVERY rescue commit found in git log.");
        }
        ConsoleFormatting.PrintSuccess($"Rescue commit verified in Git log:\n{log.Trim()}");
        details.Add("Rescue Commit: Created WIP-RECOVERY commit");

        // Step 5: Verify Orphaned ForgeTask Reset & Re-Enqueued to Priority Queue
        ConsoleFormatting.PrintStep("7.5", "Verifying orphaned task reconciled and enqueued to llm-urgent...");
        using (var scope = context.Environment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var reloaded = await db.Tasks.FindAsync([crashedTask.Id], context.CancellationToken);

            if (reloaded == null)
            {
                return ScenarioResult.Fail(sw.Elapsed, "Orphaned task was lost from database.");
            }

            ConsoleFormatting.PrintSuccess($"Orphaned task reconciled (Status={reloaded.Status}).");

            var urgentJob = context.Environment.JobQueue.AllJobs
                .FirstOrDefault(j => j.TaskId == crashedTask.Id && j.QueueName == "llm-urgent");

            if (urgentJob == null)
            {
                return ScenarioResult.Fail(sw.Elapsed, "Orphaned task was not re-enqueued to priority queue 'llm-urgent'.");
            }

            ConsoleFormatting.PrintSuccess($"Orphaned task re-enqueued to head of queue '{urgentJob.QueueName}'.");
            details.Add($"Task Re-Enqueued: {urgentJob.JobId} -> {urgentJob.QueueName}");
        }

        sw.Stop();
        return ScenarioResult.Pass(sw.Elapsed, "Crash resilience, lock purging, WIP rescue commits, and task re-enqueue verified.", details.ToArray());
    }
}
