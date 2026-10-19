using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.ScenarioRunner.Common;

namespace NightCodeForge.ScenarioRunner.Scenarios;

/// <summary>
/// Scenario 6: Full Autonomous Multi-Stage Pipeline Execution.
/// Exercises the complete standard workflow lifecycle:
/// ForgeTask Review -> Pre-Implementation Baseline -> Implementation -> Post-Implementation Ratchet -> Code Review & Fix -> Post-Review Gate -> Merge Gate.
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

        // Step 1: Create ForgeTask with Automatic MergeMode
        ConsoleFormatting.PrintStep("6.1", "Seeding end-to-end feature task with MergeMode.Automatic...");
        ForgeTask task;
        using (var scope = context.Environment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            task = new ForgeTask
            {
                WorkspaceId = context.Environment.Workspace.Id,
                Title = "Implement Multiply Operation in Calculator",
                Description = "Add a Multiply(int a, int b) method to Calculator with full XML documentation.",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.TaskReview,
                BaseBranch = "main",
                Priority = 1,
                MergeMode = MergeMode.Automatic
            };
            db.Tasks.Add(task);
            await db.SaveChangesAsync(context.CancellationToken);

            task.FeatureBranch = $"feature/task-{task.Id}";
            await db.SaveChangesAsync(context.CancellationToken);
            ConsoleFormatting.PrintSuccess($"Seeded ForgeTask #{task.Id}: '{task.Title}'");
        }

        // Step 2: Configure custom stage handlers for deterministic end-to-end progression
        using var scopeServices = context.Environment.Services.CreateScope();
        var registry = scopeServices.ServiceProvider.GetRequiredService<IWorkflowStageRegistry>();
        var renderer = scopeServices.ServiceProvider.GetRequiredService<IPromptTemplateRenderer>();
        var timeProvider = scopeServices.ServiceProvider.GetRequiredService<TimeProvider>();

        var reviewStage = new GenericAgenticStage(StandardStageIds.TaskReview, "ForgeTask Review", new AgenticStageConfig { RequiredMode = AgentMode.Full }, renderer, timeProvider)
        {
            Handler = (pipelineCtx, ct) =>
            {
                pipelineCtx.DataBag.Set(PipelineDataBagKeys.ApprovedPlan, "Technical Plan: Add Multiply(int a, int b) returning a * b to Calculator.cs.");
                return Task.FromResult(StageExecutionResult.Success());
            }
        };

        var implStage = new GenericAgenticStage(StandardStageIds.Implementation, "Implementation", new AgenticStageConfig { RequiredMode = AgentMode.Full }, renderer, timeProvider)
        {
            Handler = (pipelineCtx, ct) =>
            {
                // Implement Multiply in Calculator.cs in the active task worktree
                context.Environment.GitRepo.FixCompilerError(pipelineCtx.WorkingDirectory); // includes Multiply method
                return Task.FromResult(StageExecutionResult.Success());
            }
        };

        var reviewFixStage = new GenericAgenticStage(StandardStageIds.CodeReviewAndFix, "Code Review & Fix", new AgenticStageConfig { RequiredMode = AgentMode.Review, LoopBackStageId = StandardStageIds.Implementation }, renderer, timeProvider)
        {
            Handler = (pipelineCtx, ct) =>
            {
                // Verify diff
                return Task.FromResult(StageExecutionResult.Success());
            }
        };

        var preImplDef = new StageDefinition
        {
            StageId = StandardStageIds.PreImplementationBaseline,
            DisplayName = "Pre-Implementation Baseline",
            Kind = WorkflowStageKind.Deterministic,
            CsharpImplementationType = StageImplementationTypes.DeterministicComposite,
            ConfigurationJson = "{\"Action\": \"CompositeBaseline\", \"FailOnError\": false, \"ExtraParameters\": {\"Stages\": \"build,test\"}}"
        };

        var postImplDef = new StageDefinition
        {
            StageId = StandardStageIds.PostImplementationGates,
            DisplayName = "Post-Implementation Gates",
            Kind = WorkflowStageKind.Deterministic,
            CsharpImplementationType = StageImplementationTypes.DeterministicComposite,
            ConfigurationJson = $"{{\"Action\": \"CompositeVerification\", \"LoopBackStageId\": \"{StandardStageIds.Implementation}\", \"FailOnError\": true, \"ExtraParameters\": {{\"Stages\": \"build,test\"}}}}"
        };

        var postReviewDef = new StageDefinition
        {
            StageId = StandardStageIds.PostReviewVerification,
            DisplayName = "Post-Review Verification",
            Kind = WorkflowStageKind.Deterministic,
            CsharpImplementationType = StageImplementationTypes.DeterministicComposite,
            ConfigurationJson = $"{{\"Action\": \"CompositeVerification\", \"LoopBackStageId\": \"{StandardStageIds.CodeReviewAndFix}\", \"FailOnError\": true, \"ExtraParameters\": {{\"Stages\": \"build,test\"}}}}"
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            reviewStage,
            new CompositeDeterministicStage(preImplDef, scopeServices.ServiceProvider, registry),
            implStage,
            new CompositeDeterministicStage(postImplDef, scopeServices.ServiceProvider, registry),
            reviewFixStage,
            new CompositeDeterministicStage(postReviewDef, scopeServices.ServiceProvider, registry)
        });

        context.Environment.SetCustomPipeline(pipeline);

        try
        {
            var pipelineProvider = scopeServices.ServiceProvider.GetRequiredService<IWorkflowPipelineProvider>();
            var orchestrator = scopeServices.ServiceProvider.GetRequiredService<ITaskOrchestrator>();

            // Step 3: Execute all stages sequentially
            var stages = pipeline.Stages.ToList();
            ConsoleFormatting.PrintStep("6.2", $"Executing pipeline across {stages.Count} configured stages...");

        for (int i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];
            ConsoleFormatting.PrintStep($"6.2.{i + 1}", $"Processing stage '{stage.DisplayName}' ({stage.StageId})...");

            await orchestrator.ProcessTaskAsync(task.Id, context.CancellationToken);

            // Reload task to inspect advancement
            using var inspectScope = context.Environment.Services.CreateScope();
            var db = inspectScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var reloaded = await db.Tasks.FindAsync([task.Id], context.CancellationToken);

            if (reloaded?.Status == TaskOverallStatus.Failed)
            {
                return ScenarioResult.Fail(sw.Elapsed, $"Pipeline failed during stage '{stage.StageId}'.");
            }

            ConsoleFormatting.PrintSuccess($"Stage '{stage.StageId}' completed. ForgeTask Status={reloaded?.Status}, Next Stage='{reloaded?.CurrentStageId}'");
            details.Add($"Stage Completed: {stage.StageId}");
        }

        // Final Orchestration pass to trigger auto-merge gate
        using (var finalScope = context.Environment.Services.CreateScope())
        {
            var orch = finalScope.ServiceProvider.GetRequiredService<ITaskOrchestrator>();
            await orch.ProcessTaskAsync(task.Id, context.CancellationToken);
        }

        // Step 4: Verify Final State (Squash-Merged to main & ForgeTask Closed)
        ConsoleFormatting.PrintStep("6.3", "Verifying auto-merge and final task status...");
        using (var verifyScope = context.Environment.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var finalTask = await db.Tasks
                .Include(i => i.Checkpoints)
                .FirstOrDefaultAsync(i => i.Id == task.Id, context.CancellationToken);

            var records = await db.StageExecutionRecords
                .Where(r => r.TaskId == task.Id)
                .OrderBy(r => r.StartedAt)
                .ToListAsync(context.CancellationToken);

            ConsoleFormatting.PrintSuccess($"Final ForgeTask Status: {finalTask?.Status}");
            ConsoleFormatting.PrintSuccess($"Recorded {records.Count} stage execution telemetry records in database.");
            ConsoleFormatting.PrintSuccess($"Recorded {finalTask?.Checkpoints.Count} Git checkpoints.");

            if (finalTask?.Status != TaskOverallStatus.Closed)
            {
                var logs = await db.ExecutionLogs.Where(l => l.TaskId == task.Id).ToListAsync(context.CancellationToken);
                var lastErr = logs.LastOrDefault(l => l.Level == LogLevel.Warning || l.Level == LogLevel.Error);
                return ScenarioResult.Fail(sw.Elapsed, $"Expected task to be Closed after auto-merge, found {finalTask?.Status}. Last log: {lastErr?.Message}");
            }

            // Verify Calculator.cs on main branch contains Multiply
            await context.Environment.GitRepo.CheckoutBranchAsync("main", create: false, context.CancellationToken);
            var content = context.Environment.GitRepo.ReadFile("Calculator.cs");

            if (!content.Contains("Multiply"))
            {
                return ScenarioResult.Fail(sw.Elapsed, "Calculator.cs on main branch does not contain Multiply after auto-merge.");
            }

            ConsoleFormatting.PrintSuccess("Verified 'main' branch contains merged Multiply method implementation!");
            details.Add($"Checkpoints Created: {finalTask?.Checkpoints.Count}");
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
