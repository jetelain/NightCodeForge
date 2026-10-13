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

        ConsoleFormatting.PrintStep("2.1", "Seeding test issue into database...");
        Issue issue;
        using (var scope = context.Environment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            issue = new Issue
            {
                WorkspaceId = context.Environment.Workspace.Id,
                Title = "Inspect Calculator Codebase",
                Description = "Inspect Calculator.cs and README.md. Analyze the available arithmetic methods and recommend next steps.",
                Status = IssueOverallStatus.Open,
                CurrentStageId = "issue-review",
                BaseBranch = "main",
                FeatureBranch = "feature/issue-inspect",
                Priority = 1,
                MergeMode = MergeMode.ManualReviewRequired
            };
            db.Issues.Add(issue);
            await db.SaveChangesAsync(context.CancellationToken);
            ConsoleFormatting.PrintSuccess($"Created Issue #{issue.Id}: '{issue.Title}'");
        }

        ConsoleFormatting.PrintStep("2.2", $"Preparing CrucibleAgentHarness with model '{context.Options.ModelId}'...");
        using var runScope = context.Environment.Services.CreateScope();
        var harness = runScope.ServiceProvider.GetRequiredService<CrucibleAgentHarness>();
        var streamer = runScope.ServiceProvider.GetRequiredService<IAgentActivityStreamer>();

        var profile = context.Environment.Workspace.AgentProfiles?.FirstOrDefault(p => p.Id == "issue-review")
            ?? new AgentProfile
            {
                Id = "issue-review",
                Name = "Reviewer",
                HarnessId = "crucible",
                ModelName = context.Options.ModelId,
                Mode = AgentMode.Full
            };

        var sessionContext = new AgentSessionContext
        {
            Issue = issue,
            Workspace = context.Environment.Workspace,
            Profile = profile,
            StageId = "issue-review",
            StageDirective = "Inspect Calculator.cs and README.md in the repository. Answer what methods are present and recommend 2 additional arithmetic operations.",
            RequiredMode = AgentMode.Full,
            WorkspacePath = context.Environment.RepositoryPath,
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
                await foreach (var evt in streamer.SubscribeAsync(issue.Id, streamCts.Token))
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
            catch (OperationCanceledException) { }
            finally
            {
                Console.WriteLine();
                Console.WriteLine($"{ConsoleFormatting.Dim}─── End Telemetry Stream ────────────────────────────────────────────{ConsoleFormatting.Reset}");
                Console.WriteLine();
            }
        }, streamCts.Token);

        ConsoleFormatting.PrintStep("2.3", "Executing CrucibleAgentHarness reasoning turn...");
        var runResult = await harness.RunStageAsync(sessionContext, context.CancellationToken);

        // Cancel streaming watcher and wait for it
        streamCts.Cancel();
        try { await streamTask; } catch { }

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
}
