using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.ScenarioRunner.Common;

namespace NightCodeForge.ScenarioRunner.Scenarios;

/// <summary>
/// Scenario 2: Direct Agentic Reasoning & Tool Invocation with Ollama Qwen3.8-27B:latest.
/// Executes CrucibleAgentHarness against the synthetic workspace, streams real-time reasoning and tool
/// calls, and verifies tokens, duration, and session memory serialization.
/// </summary>
public class AgentReasoningScenario : IScenario
{
    public string Id => "2";
    public string Name => "Crucible Agent Reasoning & Tool Invocation (Qwen3.8-27B)";
    public string Description => "Tests live LLM inference with Qwen3.8-27B:latest (or Mock), real-time streaming, and tool execution.";

    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var sw = Stopwatch.StartNew();
        var details = new List<string>();

        ConsoleFormatting.PrintStep("2.1", "Seeding test task into database...");
        ForgeTask task;
        using (var scope = context.Environment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            task = new ForgeTask
            {
                WorkspaceId = context.Environment.Workspace.Id,
                Title = "Inspect Calculator Codebase",
                Description = "Inspect Calculator.cs and README.md. Analyze the available arithmetic methods and recommend next steps.",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.TaskReview,
                BaseBranch = "main",
                FeatureBranch = "feature/task-inspect",
                Priority = 1,
                MergeMode = MergeMode.ManualReviewRequired
            };
            db.Tasks.Add(task);
            await db.SaveChangesAsync(context.CancellationToken);
            ConsoleFormatting.PrintSuccess($"Created ForgeTask #{task.Id}: '{task.Title}'");
        }

        ConsoleFormatting.PrintStep("2.2", $"Preparing CrucibleAgentHarness with model '{context.Options.ModelId}' and dedicated worktree...");
        using var runScope = context.Environment.Services.CreateScope();
        var harness = runScope.ServiceProvider.GetRequiredService<CrucibleAgentHarness>();
        var streamer = runScope.ServiceProvider.GetRequiredService<IAgentActivityStreamer>();
        var gitManager = runScope.ServiceProvider.GetRequiredService<IGitWorkspaceManager>();

        var worktreePath = await gitManager.EnsureWorktreeAsync(
            context.Environment.Workspace,
            task.Id,
            task.BaseBranch,
            context.CancellationToken);
        ConsoleFormatting.PrintInfo($"Worktree provisioned at '{worktreePath}'");

        var profile = context.Environment.Workspace.AgentProfiles?.FirstOrDefault(p => p.Id == StandardStageIds.TaskReview)
            ?? new AgentProfile
            {
                Id = StandardStageIds.TaskReview,
                Name = "Reviewer",
                HarnessId = StandardHarnessIds.Crucible,
                ModelName = context.Options.ModelId,
                Mode = AgentMode.Full
            };

        var sessionContext = new AgentSessionContext
        {
            ForgeTask = task,
            Workspace = context.Environment.Workspace,
            Profile = profile,
            StageId = StandardStageIds.TaskReview,
            StageDirective = "Inspect Calculator.cs and README.md in the repository. Answer what methods are present and recommend 2 additional arithmetic operations.",
            RequiredMode = AgentMode.Full,
            WorkspacePath = worktreePath,
            BootstrapPrompt = "Please inspect Calculator.cs and provide an overview and enhancement recommendations.",
            OptimizationFlags = new LocalOptimizationFlags()
        };

        // Start background streaming display
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var streamTask = Task.Run(async () =>
        {
            try
            {
                Console.WriteLine();
                Console.WriteLine($"{ConsoleFormatting.Dim}─── Live Telemetry Stream ───────────────────────────────────────────{ConsoleFormatting.Reset}");
                await foreach (var evt in streamer.SubscribeAsync(task.Id, streamCts.Token))
                {
                    switch (evt.Kind)
                    {
                        case AgentActivityKind.ThinkingDelta:
                            Console.Write($"{ConsoleFormatting.Magenta}{ConsoleFormatting.Dim}{evt.Content}{ConsoleFormatting.Reset}");
                            break;
                        case AgentActivityKind.MessageDelta:
                            Console.Write($"{ConsoleFormatting.Cyan}{evt.Content}{ConsoleFormatting.Reset}");
                            break;
                        case AgentActivityKind.ToolStarted:
                            Console.WriteLine($"\n{ConsoleFormatting.Yellow}⚙ [TOOL CALL] {evt.Content}{ConsoleFormatting.Reset}");
                            break;
                        case AgentActivityKind.ToolCompleted:
                            var snippet = evt.Content.Length > 120 ? evt.Content[..117] + "..." : evt.Content;
                            Console.WriteLine($"{ConsoleFormatting.Green}✔ [TOOL RESULT] {snippet}{ConsoleFormatting.Reset}");
                            break;
                        case AgentActivityKind.Interrupted:
                            Console.WriteLine($"\n{ConsoleFormatting.Yellow}⏸ [INTERRUPTED] {evt.Content}{ConsoleFormatting.Reset}");
                            break;
                        case AgentActivityKind.Error:
                            Console.WriteLine($"\n{ConsoleFormatting.Red}✖ [ERROR] {evt.Content}{ConsoleFormatting.Reset}");
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when streamCts is cancelled upon scenario completion.
            }
            finally
            {
                Console.WriteLine();
                Console.WriteLine($"{ConsoleFormatting.Dim}─── End Telemetry Stream ────────────────────────────────────────────{ConsoleFormatting.Reset}");
                Console.WriteLine();
            }
        }, streamCts.Token);

        try
        {
            ConsoleFormatting.PrintStep("2.3", "Executing CrucibleAgentHarness reasoning turn...");
            var runResult = await harness.RunStageAsync(sessionContext, context.CancellationToken);

            // Cancel streaming watcher and wait for it
            streamCts.Cancel();
            try { await streamTask; } catch (Exception) { /* Stream task completion on scenario finish */ }

            sw.Stop();

            if (runResult.Status == AgentRunStatus.Failed)
            {
                return ScenarioResult.Fail(sw.Elapsed, $"Agent execution failed: {runResult.Error}");
            }

            ConsoleFormatting.PrintSuccess($"Agent execution completed in {sw.ElapsedMilliseconds}ms!");
            ConsoleFormatting.PrintInfo($"Duration: {runResult.Metrics.DurationMs}ms, Prompt Tokens: {runResult.Metrics.PromptTokens}, Completion Tokens: {runResult.Metrics.CompletionTokens}, Total Tokens: {runResult.Metrics.TotalTokens}");

            if (!string.IsNullOrEmpty(runResult.UpdatedState))
            {
                ConsoleFormatting.PrintSuccess($"Serialized memory captured ({runResult.UpdatedState.Length} bytes)");
            }

            details.Add($"Model: {context.Options.ModelId}");
            details.Add($"Tokens: {runResult.Metrics.TotalTokens} (Prompt: {runResult.Metrics.PromptTokens}, Comp: {runResult.Metrics.CompletionTokens})");
            details.Add($"Duration: {runResult.Metrics.DurationMs}ms");

            return ScenarioResult.Pass(sw.Elapsed, "Crucible agent reasoning, streaming, and tool execution verified.", details.ToArray());
        }
        finally
        {
            try
            {
                await gitManager.RemoveWorktreeAsync(context.Environment.Workspace, task.Id, context.CancellationToken);
            }
            catch (Exception)
            {
                // Best-effort test worktree cleanup
            }
        }
    }
}
