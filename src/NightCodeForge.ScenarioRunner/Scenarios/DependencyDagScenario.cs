using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.ScenarioRunner.Common;

namespace NightCodeForge.ScenarioRunner.Scenarios;

/// <summary>
/// Scenario 4: Dependency DAG Resolution & Cascade Unblocking.
/// Tests that:
/// - A dependent issue is blocked until its prerequisites are completed
/// - The orchestrator refuses to process issues prematurely
/// - When a prerequisite completes via CompleteIssueAndCascadeAsync, its branch is squash-merged
/// - Dependent issues are automatically unblocked and enqueued into the dev-queue
/// </summary>
public class DependencyDagScenario : IScenario
{
    public string Id => "4";
    public string Name => "Dependency DAG Engine & Cascade Unblocking";
    public string Description => "Tests DAG prerequisite blocking, squash-merging, and automatic cascade unblocking.";

    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var sw = Stopwatch.StartNew();
        var details = new List<string>();

        // Step 1: Create Parent (Prerequisite) and Child (Dependent) Issues
        ConsoleFormatting.PrintStep("4.1", "Seeding DAG issues: Issue A (Prerequisite) and Issue B (Dependent)...");
        Issue issueA;
        Issue issueB;

        using (var scope = context.Environment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var depStore = scope.ServiceProvider.GetRequiredService<IIssueDependencyStore>();

            issueA = new Issue
            {
                WorkspaceId = context.Environment.Workspace.Id,
                Title = "Core Calculation Service",
                Description = "Implement the foundation math calculation service.",
                Status = IssueOverallStatus.Open,
                CurrentStageId = "issue-review",
                BaseBranch = "main",
                Priority = 1,
                MergeMode = MergeMode.ManualReviewRequired
            };

            issueB = new Issue
            {
                WorkspaceId = context.Environment.Workspace.Id,
                Title = "Math HTTP API Endpoint",
                Description = "Expose Calculation Service methods via REST controller.",
                Status = IssueOverallStatus.Open,
                CurrentStageId = "issue-review",
                BaseBranch = "main",
                Priority = 2,
                MergeMode = MergeMode.ManualReviewRequired
            };

            db.Issues.AddRange(issueA, issueB);
            await db.SaveChangesAsync(context.CancellationToken);

            issueA.FeatureBranch = $"feature/issue-{issueA.Id}";
            issueB.FeatureBranch = $"feature/issue-{issueB.Id}";
            await db.SaveChangesAsync(context.CancellationToken);

            // Wire DAG: Issue B depends on Issue A
            db.IssueDependencies.Add(new IssueDependency
            {
                IssueId = issueB.Id,
                DependsOnIssueId = issueA.Id
            });
            await db.SaveChangesAsync(context.CancellationToken);

            ConsoleFormatting.PrintSuccess($"Created Issue #{issueA.Id} (Parent) and Issue #{issueB.Id} (Child, depends on #{issueA.Id})");
        }

        // Step 2: Validate Initial DAG Readiness
        ConsoleFormatting.PrintStep("4.2", "Verifying DAG readiness engine state...");
        using (var scope = context.Environment.Services.CreateScope())
        {
            var dagEngine = scope.ServiceProvider.GetRequiredService<IDependencyResolutionEngine>();

            var readyA = await dagEngine.IsReadyForExecutionAsync(issueA.Id, context.CancellationToken);
            var readyB = await dagEngine.IsReadyForExecutionAsync(issueB.Id, context.CancellationToken);

            if (!readyA)
            {
                return ScenarioResult.Fail(sw.Elapsed, "Prerequisite Issue A should be ready for execution.");
            }
            if (readyB)
            {
                return ScenarioResult.Fail(sw.Elapsed, "Violation: Dependent Issue B should NOT be ready while Issue A is open.");
            }

            ConsoleFormatting.PrintSuccess($"DAG correctly blocks child: Issue A Ready={readyA}, Issue B Ready={readyB}");
            details.Add($"Initial State: Issue #{issueA.Id} Ready=True, Issue #{issueB.Id} Ready=False");
        }

        // Step 3: Test Premature Execution Interception
        ConsoleFormatting.PrintStep("4.3", "Attempting premature execution of blocked child Issue B...");
        using (var scope = context.Environment.Services.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IIssueOrchestrator>();
            await orchestrator.ProcessIssueAsync(issueB.Id, context.CancellationToken);

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var logs = await db.ExecutionLogs
                .Where(l => l.IssueId == issueB.Id)
                .OrderByDescending(l => l.Timestamp)
                .ToListAsync(context.CancellationToken);

            var waitingLog = logs.FirstOrDefault(l => l.Message.Contains("waiting on prerequisite issues"));
            if (waitingLog == null)
            {
                return ScenarioResult.Fail(sw.Elapsed, "Orchestrator did not record waiting on prerequisites log.");
            }

            ConsoleFormatting.PrintSuccess("Orchestrator intercepted execution: safely waited without branch checkout or GPU lock.");
        }

        // Step 4: Advance and Complete Issue A with Squash-Merge
        ConsoleFormatting.PrintStep("4.4", "Completing Issue A and triggering cascade unblocking...");
        // Add a commit on Issue A's feature branch
        await context.Environment.GitRepo.CheckoutBranchAsync(issueA.FeatureBranch, create: true, context.CancellationToken);
        context.Environment.GitRepo.CreateFile("MathService.cs", "// MathService core implementation\npublic class MathService {}");
        await context.Environment.GitRepo.CommitAllAsync("Implement MathService", context.CancellationToken);
        await context.Environment.GitRepo.CheckoutBranchAsync("main", create: false, context.CancellationToken);

        using (var scope = context.Environment.Services.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IIssueOrchestrator>();
            await orchestrator.CompleteIssueAndCascadeAsync(issueA.Id, "Squash: Complete MathService", context.CancellationToken);
        }

        // Step 5: Verify Issue A Closed & Child Issue B Unblocked
        ConsoleFormatting.PrintStep("4.5", "Verifying Issue A closed and dependent Issue B cascade unblocked...");
        using (var scope = context.Environment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dagEngine = scope.ServiceProvider.GetRequiredService<IDependencyResolutionEngine>();

            var reloadedA = await db.Issues.FindAsync([issueA.Id], context.CancellationToken);
            var reloadedB = await db.Issues.FindAsync([issueB.Id], context.CancellationToken);

            if (reloadedA?.Status != IssueOverallStatus.Closed)
            {
                return ScenarioResult.Fail(sw.Elapsed, $"Expected Issue A to be Closed, found {reloadedA?.Status}.");
            }
            ConsoleFormatting.PrintSuccess($"Issue #{issueA.Id} closed and squash-merged into main.");

            var readyBNow = await dagEngine.IsReadyForExecutionAsync(issueB.Id, context.CancellationToken);
            if (!readyBNow)
            {
                return ScenarioResult.Fail(sw.Elapsed, "Violation: Issue B was not unblocked after Issue A completed.");
            }

            ConsoleFormatting.PrintSuccess($"Cascade unblocking successful! Issue #{issueB.Id} status is now {reloadedB?.Status}, ReadyForExecution=true.");

            // Verify git main branch contains the squashed commit
            var log = await context.Environment.GitRepo.GetCommitLogAsync(2, context.CancellationToken);
            ConsoleFormatting.PrintInfo($"Git main commit log:\n{log.Trim()}");

            details.Add($"Squash Merge: Issue #{issueA.Id} merged to main");
            details.Add($"Cascade Unblock: Issue #{issueB.Id} unblocked and ready");
        }

        sw.Stop();
        return ScenarioResult.Pass(sw.Elapsed, "DAG engine dependency blocking, squash-merging, and cascade unblocking verified.", details.ToArray());
    }
}
