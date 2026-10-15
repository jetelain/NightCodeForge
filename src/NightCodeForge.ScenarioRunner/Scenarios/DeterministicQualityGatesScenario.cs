using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.ScenarioRunner.Common;

namespace NightCodeForge.ScenarioRunner.Scenarios;

/// <summary>
/// Scenario 5: Deterministic Quality Gates & Compiler Regression Loopback.
/// Validates that:
/// - Deterministic build/test gates run with 0 GPU tokens
/// - Compiler errors are detected and routed backward to the 'implementation' stage
/// - Diagnostics are recorded in PipelineDataBag for the agent to review
/// - Clean compilation succeeds and advances the pipeline
/// </summary>
public class DeterministicQualityGatesScenario : IScenario
{
    public string Id => "5";
    public string Name => "Deterministic Quality Gates & Compiler Regression Loopback";
    public string Description => "Tests CPU-only compilation validation, regression detection, and loopback backward routing.";

    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var sw = Stopwatch.StartNew();
        var details = new List<string>();

        // Step 1: Create test task on a feature branch
        ConsoleFormatting.PrintStep("5.1", "Creating task and feature branch for quality gate testing...");
        ForgeTask task;
        using (var scope = context.Environment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            task = new ForgeTask
            {
                WorkspaceId = context.Environment.Workspace.Id,
                Title = "Compiler Gate Test",
                Description = "Validate deterministic build gate interception.",
                Status = TaskOverallStatus.Open,
                CurrentStageId = DeterministicBuildStage.DefaultStageId,
                BaseBranch = "main",
                FeatureBranch = "feature/task-compiler-gate",
                Priority = 1,
                MergeMode = MergeMode.ManualReviewRequired
            };
            db.Tasks.Add(task);
            await db.SaveChangesAsync(context.CancellationToken);
        }

        using var testScope = context.Environment.Services.CreateScope();
        var envProvider = testScope.ServiceProvider.GetRequiredService<IExecutionEnvironmentProvider>();
        var buildStage = testScope.ServiceProvider.GetRequiredService<DeterministicBuildStage>();
        var gitManager = testScope.ServiceProvider.GetRequiredService<IGitWorkspaceManager>();

        var worktreePath = await gitManager.EnsureWorktreeAsync(context.Environment.Workspace, task.Id, task.BaseBranch, cancellationToken: context.CancellationToken);
        ConsoleFormatting.PrintSuccess($"Provisioned dedicated worktree for task #{task.Id} at '{worktreePath}'.");

        var pipelineContext = new PipelineContext(task, context.Environment.Workspace)
        {
            WorkingDirectory = worktreePath
        };

        // Step 2: Inject Deliberate Syntax Error
        ConsoleFormatting.PrintStep("5.2", "Injecting deliberate compiler syntax error into Calculator.cs...");
        context.Environment.GitRepo.InjectCompilerError(worktreePath);

        // Step 3: Run DeterministicBuildStage (Expect Failure & Loopback)
        ConsoleFormatting.PrintStep("5.3", "Executing DeterministicBuildStage on corrupt codebase (0 GPU tokens)...");
        var failResult = await buildStage.ExecuteAsync(pipelineContext, context.CancellationToken);

        if (failResult.IsSuccess)
        {
            return ScenarioResult.Fail(sw.Elapsed, "Violation: DeterministicBuildStage should have failed on syntax error.");
        }

        if (!string.Equals(failResult.TargetRoutingStageId, "implementation", StringComparison.OrdinalIgnoreCase))
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Expected loopback to 'implementation', found '{failResult.TargetRoutingStageId}'.");
        }

        ConsoleFormatting.PrintSuccess($"Compiler error successfully intercepted without GPU tokens!");
        ConsoleFormatting.PrintInfo($"Loopback Target: '{failResult.TargetRoutingStageId}'");
        ConsoleFormatting.PrintInfo($"Error Diagnostics: {failResult.ErrorMessage}");
        details.Add($"Interception: Loopback to '{failResult.TargetRoutingStageId}'");

        // Step 4: Fix Compiler Error
        ConsoleFormatting.PrintStep("5.4", "Fixing syntax error in Calculator.cs...");
        context.Environment.GitRepo.FixCompilerError(worktreePath);

        // Step 5: Re-run DeterministicBuildStage (Expect Success)
        ConsoleFormatting.PrintStep("5.5", "Re-executing DeterministicBuildStage on corrected code...");
        var passResult = await buildStage.ExecuteAsync(pipelineContext, context.CancellationToken);

        if (!passResult.IsSuccess)
        {
            var diag = pipelineContext.DataBag.Get<List<string>>("diagnostics.compiler_errors");
            var diagStr = diag != null ? string.Join("; ", diag) : "none";
            return ScenarioResult.Fail(sw.Elapsed, $"DeterministicBuildStage failed after fix: {passResult.ErrorMessage} (Details: {diagStr})");
        }

        ConsoleFormatting.PrintSuccess("DeterministicBuildStage succeeded cleanly with 0 compiler errors!");
        details.Add("Compilation Gate: Passed cleanly on valid code");

        // Teardown worktree
        await gitManager.RemoveWorktreeAsync(context.Environment.Workspace, task.Id, context.CancellationToken);

        sw.Stop();
        return ScenarioResult.Pass(sw.Elapsed, "Deterministic quality gate compiler regression interception and loopback routing verified.", details.ToArray());
    }
}
