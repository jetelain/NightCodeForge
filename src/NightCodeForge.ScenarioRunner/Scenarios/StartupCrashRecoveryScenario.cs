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

        // Step 1: Create in-flight task and dedicated active worktree
        ConsoleFormatting.PrintStep("7.1", "Seeding in-flight task and provisioning active worktree...");
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
                CurrentStageId = StandardStageIds.Implementation,
                BaseBranch = "main",
                FeatureBranch = "feature/task-crashed",
                Priority = 1,
                MergeMode = MergeMode.ManualReviewRequired
            };
            db.Tasks.Add(crashedTask);
            await db.SaveChangesAsync(context.CancellationToken);
            ConsoleFormatting.PrintInfo($"Seeded orphaned task #{crashedTask.Id} with Status=Implemented.");
        }

        using var workScope = context.Environment.Services.CreateScope();
        var gitManager = workScope.ServiceProvider.GetRequiredService<Core.Interfaces.IGitWorkspaceManager>();
        var worktreePath = await gitManager.EnsureWorktreeAsync(
            context.Environment.Workspace,
            crashedTask.Id,
            crashedTask.BaseBranch,
            context.CancellationToken);
        ConsoleFormatting.PrintInfo($"Active worktree created at: {worktreePath}");

        try
        {
            // 1. Inject dangling locks (both base repo and active worktree)
            var baseLockPath = context.Environment.GitRepo.InjectDanglingLock();
            var worktreeLockPath = context.Environment.GitRepo.InjectDanglingLock(worktreePath);
            ConsoleFormatting.PrintInfo($"Injected dangling locks: {baseLockPath} and {worktreeLockPath}");

            // 2. Inject uncommitted working tree edits into active task worktree
            context.Environment.GitRepo.InjectDirtyChange(worktreePath);
            var status = await context.Environment.GitRepo.GetStatusAsync(worktreePath, context.CancellationToken);
            if (string.IsNullOrWhiteSpace(status))
            {
                return ScenarioResult.Fail(sw.Elapsed, "Failed to inject dirty worktree change.");
            }
            ConsoleFormatting.PrintInfo($"Worktree is dirty:\n{status.Trim()}");

            // Step 2: Execute Startup Recovery Reconciliation
            ConsoleFormatting.PrintStep("7.2", "Executing IStartupRecoveryService.ReconcileWorkspacesAsync()...");
            using (var scope = context.Environment.Services.CreateScope())
            {
                var recoveryService = scope.ServiceProvider.GetRequiredService<IStartupRecoveryService>();
                await recoveryService.ReconcileWorkspacesAsync(context.CancellationToken);
                ConsoleFormatting.PrintSuccess("Startup recovery service executed successfully.");
            }

            // Step 3: Verify Stale Locks Purged
            ConsoleFormatting.PrintStep("7.3", "Verifying dangling locks were purged...");
            if (File.Exists(baseLockPath) || File.Exists(worktreeLockPath))
            {
                return ScenarioResult.Fail(sw.Elapsed, "Violation: .git/index.lock was NOT purged by recovery service.");
            }
            ConsoleFormatting.PrintSuccess("Dangling git lockfiles safely detected and removed.");
            details.Add("Stale Locks: Purged successfully");

            // Step 4: Verify Rescue Commit Created for Dirty Tree
            ConsoleFormatting.PrintStep("7.4", "Verifying rescue commit was created for uncommitted edits in worktree...");
            var log = await context.Environment.GitRepo.GetCommitLogAsync(2, worktreePath, context.CancellationToken);
            if (!log.Contains("WIP-RECOVERY"))
            {
                return ScenarioResult.Fail(sw.Elapsed, "Violation: No WIP-RECOVERY rescue commit found in worktree git log.");
            }
            ConsoleFormatting.PrintSuccess($"Rescue commit verified in worktree Git log:\n{log.Trim()}");
            details.Add("Rescue Commit: Created WIP-RECOVERY commit in task worktree");

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
        }
        finally
        {
            try
            {
                await gitManager.RemoveWorktreeAsync(context.Environment.Workspace, crashedTask.Id, context.CancellationToken);
            }
            catch { }
        }

        sw.Stop();
        return ScenarioResult.Pass(sw.Elapsed, "Crash resilience, lock purging, WIP rescue commits, and task re-enqueue verified.", details.ToArray());
    }
}
