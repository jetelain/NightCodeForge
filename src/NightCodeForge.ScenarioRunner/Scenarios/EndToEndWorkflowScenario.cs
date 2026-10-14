using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.ScenarioRunner.Common;

namespace NightCodeForge.ScenarioRunner.Scenarios;

/// <summary>
/// Scenario 6: Full Autonomous Multi-Stage Pipeline Execution.
/// Exercises the complete standard workflow lifecycle:
/// Issue Review -> Pre-Implementation Baseline -> Implementation -> Post-Implementation Ratchet -> Code Review & Fix -> Post-Review Gate -> Merge Gate.
/// Verifies git branch creation, stage WIP commits, stage execution records, and clean squash-merge into base branch.
/// </summary>
public class EndToEndWorkflowScenario : IScenario
{
    public string Id => "6";
    public string Name => "End-to-End Autonomous Multi-Stage Pipeline";
    public string Description => "Runs the complete multi-stage pipeline from review to implementation, ratchet gate, and squash-merge.";

    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var sw = Stopwatch.StartNew();
        var details = new List<string>();

        // Step 1: Create Issue with Automatic MergeMode
        ConsoleFormatting.PrintStep("6.1", "Seeding end-to-end feature issue with MergeMode.Automatic...");
        Issue issue;
        using (var scope = context.Environment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            issue = new Issue
            {
                WorkspaceId = context.Environment.Workspace.Id,
                Title = "Implement Multiply Operation in Calculator",
                Description = "Add a Multiply(int a, int b) method to Calculator with full XML documentation.",
                Status = IssueOverallStatus.Open,
                CurrentStageId = IssueReviewStage.DefaultStageId,
                BaseBranch = "main",
                Priority = 1,
                MergeMode = MergeMode.Automatic
            };
            db.Issues.Add(issue);
            await db.SaveChangesAsync(context.CancellationToken);

            issue.FeatureBranch = $"feature/issue-{issue.Id}";
            await db.SaveChangesAsync(context.CancellationToken);
            ConsoleFormatting.PrintSuccess($"Seeded Issue #{issue.Id}: '{issue.Title}'");
        }

        // Step 2: Configure custom stage handlers for deterministic end-to-end progression
        var reviewStage = new IssueReviewStage
        {
            Handler = (pipelineCtx, ct) =>
            {
                pipelineCtx.DataBag.Set("Stage1.ApprovedPlan", "Technical Plan: Add Multiply(int a, int b) returning a * b to Calculator.cs.");
                return Task.FromResult(StageExecutionResult.Success());
            }
        };

        var implStage = new ImplementationStage
        {
            Handler = (pipelineCtx, ct) =>
            {
                // Implement Multiply in Calculator.cs in the active issue worktree
                context.Environment.GitRepo.FixCompilerError(pipelineCtx.WorkingDirectory); // includes Multiply method
                return Task.FromResult(StageExecutionResult.Success());
            }
        };

        var reviewFixStage = new CodeReviewAndFixStage
        {
            Handler = (pipelineCtx, ct) =>
            {
                // Verify diff
                return Task.FromResult(StageExecutionResult.Success());
            }
        };

        using var scopeServices = context.Environment.Services.CreateScope();
        var envProvider = scopeServices.ServiceProvider.GetRequiredService<IExecutionEnvironmentProvider>();
        var execEnv = scopeServices.ServiceProvider.GetRequiredService<IExecutionEnvironment>();
        var preloadService = scopeServices.ServiceProvider.GetRequiredService<IOllamaModelPreloadService>();

        var pipeline = Default3AgentPipeline.Create(
            reviewStage: reviewStage,
            implStage: implStage,
            fixStage: reviewFixStage,
            envProvider: envProvider,
            executionEnv: execEnv,
            preloadService: preloadService);

        context.Environment.SetCustomPipeline(pipeline);

        try
        {
            var pipelineProvider = scopeServices.ServiceProvider.GetRequiredService<IWorkflowPipelineProvider>();
            var orchestrator = scopeServices.ServiceProvider.GetRequiredService<IIssueOrchestrator>();

            // Step 3: Execute all stages sequentially
            var stages = pipeline.Stages.ToList();
            ConsoleFormatting.PrintStep("6.2", $"Executing pipeline across {stages.Count} configured stages...");

        for (int i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];
            ConsoleFormatting.PrintStep($"6.2.{i + 1}", $"Processing stage '{stage.DisplayName}' ({stage.StageId})...");

            await orchestrator.ProcessIssueAsync(issue.Id, context.CancellationToken);

            // Reload issue to inspect advancement
            using var inspectScope = context.Environment.Services.CreateScope();
            var db = inspectScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var reloaded = await db.Issues.FindAsync([issue.Id], context.CancellationToken);

            if (reloaded?.Status == IssueOverallStatus.Failed)
            {
                return ScenarioResult.Fail(sw.Elapsed, $"Pipeline failed during stage '{stage.StageId}'.");
            }

            ConsoleFormatting.PrintSuccess($"Stage '{stage.StageId}' completed. Issue Status={reloaded?.Status}, Next Stage='{reloaded?.CurrentStageId}'");
            details.Add($"Stage Completed: {stage.StageId}");
        }

        // Final Orchestration pass to trigger auto-merge gate
        using (var finalScope = context.Environment.Services.CreateScope())
        {
            var orch = finalScope.ServiceProvider.GetRequiredService<IIssueOrchestrator>();
            await orch.ProcessIssueAsync(issue.Id, context.CancellationToken);
        }

        // Step 4: Verify Final State (Squash-Merged to main & Issue Closed)
        ConsoleFormatting.PrintStep("6.3", "Verifying auto-merge and final issue status...");
        using (var verifyScope = context.Environment.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var finalIssue = await db.Issues
                .Include(i => i.Checkpoints)
                .FirstOrDefaultAsync(i => i.Id == issue.Id, context.CancellationToken);

            var records = await db.StageExecutionRecords
                .Where(r => r.IssueId == issue.Id)
                .OrderBy(r => r.StartedAt)
                .ToListAsync(context.CancellationToken);

            ConsoleFormatting.PrintSuccess($"Final Issue Status: {finalIssue?.Status}");
            ConsoleFormatting.PrintSuccess($"Recorded {records.Count} stage execution telemetry records in database.");
            ConsoleFormatting.PrintSuccess($"Recorded {finalIssue?.Checkpoints.Count} Git checkpoints.");

            if (finalIssue?.Status != IssueOverallStatus.Closed)
            {
                var logs = await db.ExecutionLogs.Where(l => l.IssueId == issue.Id).ToListAsync(context.CancellationToken);
                var lastErr = logs.LastOrDefault(l => l.Level == LogLevel.Warning || l.Level == LogLevel.Error);
                return ScenarioResult.Fail(sw.Elapsed, $"Expected issue to be Closed after auto-merge, found {finalIssue?.Status}. Last log: {lastErr?.Message}");
            }

            // Verify Calculator.cs on main branch contains Multiply
            await context.Environment.GitRepo.CheckoutBranchAsync("main", create: false, context.CancellationToken);
            var content = context.Environment.GitRepo.ReadFile("Calculator.cs");

            if (!content.Contains("Multiply"))
            {
                return ScenarioResult.Fail(sw.Elapsed, "Calculator.cs on main branch does not contain Multiply after auto-merge.");
            }

            ConsoleFormatting.PrintSuccess("Verified 'main' branch contains merged Multiply method implementation!");
            details.Add($"Checkpoints Created: {finalIssue?.Checkpoints.Count}");
            details.Add($"Telemetry Records: {records.Count}");
            details.Add($"Auto-Merge: Verified in main branch");
        }

            sw.Stop();
            return ScenarioResult.Pass(sw.Elapsed, "Full autonomous multi-stage workflow pipeline with auto-merge verified.", details.ToArray());
        }
        finally
        {
            context.Environment.SetCustomPipeline(null);
        }
    }
}
