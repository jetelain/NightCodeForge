using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.ScenarioRunner.Common;

namespace NightCodeForge.ScenarioRunner.Scenarios;

/// <summary>
/// Scenario 10: Git & Ext4 Mirror Lifecycle, Host Sync & Reconcile.
/// Validates the high-performance Ext4 repository mirroring subsystem:
/// - Configures 'receive.denyCurrentBranch updateInstead' on the host repository
/// - Clones host repository into isolated mirror directory and configures 'host-origin' remote
/// - Creates feature branch and commits on mirror
/// - Performs primary push to host-origin
/// - Tests dirty host working tree conflict fallback (automatic push to '-ncf' branch)
/// - Performs squash-merge and verifies base branch host push synchronization (PushBaseBranchToHostAsync)
/// </summary>
public class GitMirrorSyncScenario : IScenario
{
    public string Id => "10";
    public string Name => "Ext4 Mirror Lifecycle, Host Sync & Dirty Tree Reconcile";
    public string Description => "Validates clone mirroring, host configuration, feature branch push, dirty host tree fallback (-ncf), and base branch push reconciliation.";

    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var sw = Stopwatch.StartNew();
        var details = new List<string>();

        using var scope = context.Environment.Services.CreateScope();
        var syncService = scope.ServiceProvider.GetRequiredService<IGitMirrorSyncService>();
        var gitRunner = scope.ServiceProvider.GetRequiredService<IGitCliRunner>();

        var hostRepo = context.Environment.RepositoryPath;
        var mirrorRepo = Path.Combine(context.Environment.RootDirectory, "ext4_mirror");

        // Step 1: Initialize Mirror from Host
        ConsoleFormatting.PrintStep("10.1", "Configuring host repository and initializing clone mirror...");
        var initializedPath = await syncService.InitializeMirrorAsync(
            context.Environment.Workspace,
            mirrorRepo,
            context.CancellationToken);

        if (!Directory.Exists(initializedPath))
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Mirror directory was not created: {initializedPath}");
        }

        // Configure git identity on mirror repository for deterministic commits
        await gitRunner.ExecuteAsync(mirrorRepo, "config user.name \"NightCodeForge Mirror\"", cancellationToken: context.CancellationToken);
        await gitRunner.ExecuteAsync(mirrorRepo, "config user.email \"mirror@nightcodeforge.test\"", cancellationToken: context.CancellationToken);

        // Verify receive.denyCurrentBranch on host
        var hostConfig = await gitRunner.ExecuteAsync(hostRepo, "config receive.denyCurrentBranch", cancellationToken: context.CancellationToken);
        if (!hostConfig.StandardOutput.Trim().Equals("updateInstead", StringComparison.OrdinalIgnoreCase))
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Expected 'receive.denyCurrentBranch' to be 'updateInstead' on host, found '{hostConfig.StandardOutput.Trim()}'.");
        }

        // Verify host-origin remote in mirror
        var remotes = await gitRunner.ExecuteAsync(mirrorRepo, "remote -v", cancellationToken: context.CancellationToken);
        if (!remotes.StandardOutput.Contains("host-origin"))
        {
            return ScenarioResult.Fail(sw.Elapsed, "Local remote 'host-origin' missing from mirror repository.");
        }

        ConsoleFormatting.PrintSuccess($"Mirror initialized at '{mirrorRepo}' with 'host-origin' remote and 'updateInstead' host config.");
        details.Add($"Mirror Initialized: {Path.GetFileName(mirrorRepo)}");

        // Step 2: Feature Branch & Commit on Mirror
        const int issueId = 10;
        var branchName = $"feature/issue-{issueId}";
        ConsoleFormatting.PrintStep("10.2", $"Creating branch '{branchName}' in mirror and committing feature file...");

        await gitRunner.ExecuteAsync(mirrorRepo, $"checkout -b {branchName}", cancellationToken: context.CancellationToken);

        var featureFilePath = Path.Combine(mirrorRepo, "FeatureSyncService.cs");
        await File.WriteAllTextAsync(featureFilePath, "// Initial Feature Code\npublic class FeatureSyncService { }\n", context.CancellationToken);

        await gitRunner.ExecuteAsync(mirrorRepo, "add FeatureSyncService.cs", cancellationToken: context.CancellationToken);
        await gitRunner.ExecuteAsync(mirrorRepo, "commit -m \"feat: add FeatureSyncService implementation\"", cancellationToken: context.CancellationToken);

        ConsoleFormatting.PrintSuccess($"Committed feature implementation on '{branchName}' in mirror.");

        // Step 3: Primary Clean Push to Host
        ConsoleFormatting.PrintStep("10.3", "Executing primary sync push to host-origin...");
        var primaryPush = await syncService.PushToHostAsync(mirrorRepo, issueId, context.CancellationToken);

        if (!primaryPush.Success || primaryPush.FallbackUsed || primaryPush.BranchPushed != branchName)
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Primary push failed or unexpectedly used fallback: Success={primaryPush.Success}, Fallback={primaryPush.FallbackUsed}");
        }

        // Verify host repository has branch
        var hostBranches = await gitRunner.ExecuteAsync(hostRepo, "branch --list", cancellationToken: context.CancellationToken);
        if (!hostBranches.StandardOutput.Contains(branchName))
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Branch '{branchName}' not found in host repository after primary push.");
        }

        ConsoleFormatting.PrintSuccess($"Primary push succeeded: '{branchName}' pushed cleanly to host repository!");
        details.Add($"Primary Push: {branchName} (Clean)");

        // Step 4: Dirty Host Working Tree Fallback (-ncf)
        ConsoleFormatting.PrintStep("10.4", "Simulating dirty working tree on host to test '-ncf' fallback push...");

        // Checkout feature branch on host and make an uncommitted modification to simulate a developer editing locally
        await gitRunner.ExecuteAsync(hostRepo, $"checkout {branchName}", cancellationToken: context.CancellationToken);
        var hostFeaturePath = Path.Combine(hostRepo, "FeatureSyncService.cs");
        await File.AppendAllTextAsync(hostFeaturePath, "// Host developer uncommitted local edit\n", context.CancellationToken);

        // Make another commit in mirror
        await File.AppendAllTextAsync(featureFilePath, "// Agent secondary edit in mirror\n", context.CancellationToken);
        await gitRunner.ExecuteAsync(mirrorRepo, "add FeatureSyncService.cs", cancellationToken: context.CancellationToken);
        await gitRunner.ExecuteAsync(mirrorRepo, "commit -m \"feat: agent secondary update\"", cancellationToken: context.CancellationToken);

        // Attempt push while host worktree is dirty on the same checked-out branch
        var dirtyPush = await syncService.PushToHostAsync(mirrorRepo, issueId, context.CancellationToken);

        if (!dirtyPush.Success)
        {
            return ScenarioResult.Fail(sw.Elapsed, "Push failed entirely instead of falling back to '-ncf'.");
        }

        if (!dirtyPush.FallbackUsed)
        {
            return ScenarioResult.Fail(sw.Elapsed, "Expected dirty push to trigger FallbackUsed = true.");
        }

        var expectedFallbackBranch = $"{branchName}-ncf";
        if (dirtyPush.BranchPushed != expectedFallbackBranch)
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Expected fallback branch '{expectedFallbackBranch}', got '{dirtyPush.BranchPushed}'.");
        }

        // Verify fallback branch exists in host
        var hostBranchesAfterFallback = await gitRunner.ExecuteAsync(hostRepo, "branch --list", cancellationToken: context.CancellationToken);
        if (!hostBranchesAfterFallback.StandardOutput.Contains(expectedFallbackBranch))
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Fallback branch '{expectedFallbackBranch}' not found on host repository.");
        }

        ConsoleFormatting.PrintSuccess($"Dirty host working tree handled gracefully: agent changes pushed to '{expectedFallbackBranch}'!");
        details.Add($"Dirty Fallback Push: {expectedFallbackBranch} (Conflict averted)");

        // Step 5: Base Branch Host Synchronization (Squash-Merge & PushBaseBranchToHostAsync)
        ConsoleFormatting.PrintStep("10.5", "Testing squash-merge and base branch host push synchronization...");

        // Reset host working tree and checkout main
        await gitRunner.ExecuteAsync(hostRepo, "checkout -f main", cancellationToken: context.CancellationToken);

        // In mirror: checkout main and squash-merge feature branch
        await gitRunner.ExecuteAsync(mirrorRepo, "checkout main", cancellationToken: context.CancellationToken);
        await gitRunner.ExecuteAsync(mirrorRepo, $"merge --squash {branchName}", cancellationToken: context.CancellationToken);
        await gitRunner.ExecuteAsync(mirrorRepo, "commit -m \"Squash: Issue #10 Feature Complete\"", cancellationToken: context.CancellationToken);

        // Push base branch to host
        var basePush = await syncService.PushBaseBranchToHostAsync(mirrorRepo, "main", context.CancellationToken);
        if (!basePush.Success || basePush.FallbackUsed)
        {
            return ScenarioResult.Fail(sw.Elapsed, $"PushBaseBranchToHostAsync failed or unexpectedly used fallback: Success={basePush.Success}, Fallback={basePush.FallbackUsed}");
        }

        // Inspect git log on host repository main branch
        var hostLog = await gitRunner.ExecuteAsync(hostRepo, "log -1 --pretty=%B", cancellationToken: context.CancellationToken);
        if (!hostLog.StandardOutput.Contains("Squash: Issue #10 Feature Complete"))
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Host repository 'main' branch did not receive the squash commit. Host log: {hostLog.StandardOutput}");
        }

        // Verify the file exists on host main
        if (!File.Exists(hostFeaturePath))
        {
            return ScenarioResult.Fail(sw.Elapsed, "FeatureSyncService.cs does not exist in host working directory on main branch.");
        }

        ConsoleFormatting.PrintSuccess("Host repository 'main' successfully updated with squash merge commit and working tree synchronized!");
        details.Add("Base Branch Sync: Verified host-origin main updated with squash commit");

        sw.Stop();
        return ScenarioResult.Pass(sw.Elapsed, "Ext4 mirror initialization, primary push, dirty host fallback, and base branch push synchronization verified.", details.ToArray());
    }
}
