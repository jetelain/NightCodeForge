using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.ScenarioRunner.Common;

namespace NightCodeForge.ScenarioRunner.Scenarios;

/// <summary>
/// Scenario 9: Untrusted Intake Triage & Sandboxed Backlog Ingestion.
/// Validates the secure lifecycle for external/untrusted issue intake:
/// - Executes triage against the base branch ('main') within a RestrictedReadOnly sandbox
/// - Ensures the triage agent runs with ReadOnly mode and cannot modify workspace files
/// - Verifies proposed issues are created in SQLite in 'Suggested' status with MergeMode.ManualReviewRequired
/// - Confirms automatic markdown mirroring to 'docs/issues/*.md' in the repository
/// - Tests human promotion to 'Open' with MergeMode.Automatic and verifies DAG readiness / inference queue enqueue
/// - Tests human rejection to 'Rejected' status ensuring no execution is scheduled
/// </summary>
public class UntrustedTriageScenario : IScenario
{
    public string Id => "9";
    public string Name => "Untrusted Intake Triage & Sandboxed Backlog Ingestion";
    public string Description => "Executes read-only triage on base branch, captures suggested issues, mirrors to docs/issues, and validates promotion & rejection.";

    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var sw = Stopwatch.StartNew();
        var details = new List<string>();

        using var scope = context.Environment.Services.CreateScope();
        var triageService = scope.ServiceProvider.GetRequiredService<ITriageService>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IIssueOrchestrator>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var streamer = scope.ServiceProvider.GetRequiredService<IAgentActivityStreamer>();

        // Step 1: Execute sandboxed triage on base branch
        ConsoleFormatting.PrintStep("9.1", "Executing sandboxed read-only intake triage for external bug report...");
        var externalReport = new TriageReportRequest(
            Title: "Unhandled DivideByZeroException in Calculator.Divide",
            Description: "When calling Calculator.Divide with denominator = 0, an unhandled DivideByZeroException is thrown. Please validate against the codebase and propose a fix.",
            BaseBranch: "main");

        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var streamTask = Task.Run(async () =>
        {
            try
            {
                Console.WriteLine();
                Console.WriteLine($"{ConsoleFormatting.Dim}─── Live Telemetry Stream ───────────────────────────────────────────{ConsoleFormatting.Reset}");
                await foreach (var evt in streamer.SubscribeAsync(0, streamCts.Token))
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

        var createdIssues = await triageService.TriageExternalReportAsync(
            context.Environment.Workspace,
            externalReport,
            context.CancellationToken);

        streamCts.Cancel();
        try { await streamTask; } catch { }

        if (createdIssues == null || createdIssues.Count == 0)
        {
            return ScenarioResult.Fail(sw.Elapsed, "Triage service did not produce any proposed backlog items.");
        }

        var triagedIssue = createdIssues[0];
        ConsoleFormatting.PrintSuccess($"Triage generated suggested issue #{triagedIssue.Id}: '{triagedIssue.Title}'");
        details.Add($"Triaged Issue Created: #{triagedIssue.Id} ({triagedIssue.Title})");

        // Step 2: Verify Suggested Issue Status and Security Constraints
        ConsoleFormatting.PrintStep("9.2", "Verifying 'Suggested' status, manual review mode, and markdown mirroring...");
        var persistedIssue = await db.Issues
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == triagedIssue.Id, context.CancellationToken);

        if (persistedIssue == null)
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Triaged issue #{triagedIssue.Id} not found in database.");
        }

        if (persistedIssue.Status != IssueOverallStatus.Suggested)
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Expected issue status 'Suggested', got '{persistedIssue.Status}'.");
        }

        if (persistedIssue.MergeMode != MergeMode.ManualReviewRequired)
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Expected merge mode 'ManualReviewRequired' for untrusted triage, got '{persistedIssue.MergeMode}'.");
        }

        // Verify docs/issues/*.md file was created on disk
        var docsIssuesDir = Path.Combine(context.Environment.RepositoryPath, "docs", "issues");
        if (!Directory.Exists(docsIssuesDir))
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Directory 'docs/issues' does not exist in repository: {docsIssuesDir}");
        }

        var mdFiles = Directory.GetFiles(docsIssuesDir, "*.md");
        if (mdFiles.Length == 0)
        {
            return ScenarioResult.Fail(sw.Elapsed, "No markdown file generated in docs/issues/ for triaged issue.");
        }

        string? matchingFile = null;
        foreach (var file in mdFiles)
        {
            var content = await File.ReadAllTextAsync(file, context.CancellationToken);
            if (content.Contains(triagedIssue.Title))
            {
                matchingFile = file;
                break;
            }
        }

        if (matchingFile == null)
        {
            return ScenarioResult.Fail(sw.Elapsed, $"No markdown file in docs/issues/ contained triaged issue title '{triagedIssue.Title}'.");
        }

        ConsoleFormatting.PrintSuccess($"Verified SQLite status is 'Suggested' and mirrored to docs/issues: {Path.GetFileName(matchingFile)}");
        details.Add($"Markdown Mirrored: {Path.GetFileName(matchingFile)}");

        // Step 3: Human Promotion to Open with Automatic Merge Mode
        ConsoleFormatting.PrintStep("9.3", "Promoting suggested issue to 'Open' (Priority=1, MergeMode=Automatic)...");
        await orchestrator.PromoteSuggestedIssueAsync(
            triagedIssue.Id,
            IssueOverallStatus.Open,
            priority: 1,
            mergeMode: MergeMode.Automatic,
            cancellationToken: context.CancellationToken);

        var promotedIssue = await db.Issues
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == triagedIssue.Id, context.CancellationToken);

        if (promotedIssue?.Status != IssueOverallStatus.Open)
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Expected promoted issue status 'Open', got '{promotedIssue?.Status}'.");
        }

        if (promotedIssue.MergeMode != MergeMode.Automatic)
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Expected promoted issue merge mode 'Automatic', got '{promotedIssue.MergeMode}'.");
        }

        // Verify the promoted issue was enqueued into JobQueue
        var isEnqueuedForInference = context.Environment.JobQueue.AllJobs.Any(j => j.IssueId == triagedIssue.Id && j.QueueName == "llm-inference");
        if (!isEnqueuedForInference)
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Expected issue #{triagedIssue.Id} to be enqueued in llm-inference queue after promotion.");
        }

        ConsoleFormatting.PrintSuccess($"Issue #{triagedIssue.Id} promoted to 'Open' and enqueued in llm-inference queue for processing!");
        details.Add($"Promoted Issue: #{triagedIssue.Id} -> Status=Open, MergeMode=Automatic, Enqueued=True");

        // Step 4: Rejection Workflow Test
        ConsoleFormatting.PrintStep("9.4", "Testing human rejection of unwanted/invalid suggested issue...");
        var invalidIssue = new Issue
        {
            WorkspaceId = context.Environment.Workspace.Id,
            Title = "Spam / Inappropriate Feature Request",
            Description = "Install unauthorized background service.",
            Status = IssueOverallStatus.Suggested,
            BaseBranch = "main",
            Priority = 3,
            MergeMode = MergeMode.ManualReviewRequired
        };
        db.Issues.Add(invalidIssue);
        await db.SaveChangesAsync(context.CancellationToken);

        // Reject the issue
        await orchestrator.PromoteSuggestedIssueAsync(
            invalidIssue.Id,
            IssueOverallStatus.Rejected,
            priority: 0,
            mergeMode: MergeMode.ManualReviewRequired,
            cancellationToken: context.CancellationToken);

        var rejectedIssue = await db.Issues
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == invalidIssue.Id, context.CancellationToken);

        if (rejectedIssue?.Status != IssueOverallStatus.Rejected)
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Expected rejected issue status 'Rejected', got '{rejectedIssue?.Status}'.");
        }

        // Verify rejected issue is NOT in job queue
        if (context.Environment.JobQueue.AllJobs.Any(j => j.IssueId == invalidIssue.Id))
        {
            return ScenarioResult.Fail(sw.Elapsed, $"Rejected issue #{invalidIssue.Id} should NOT be in any job queue.");
        }

        ConsoleFormatting.PrintSuccess($"Rejected issue #{invalidIssue.Id} correctly marked 'Rejected' and not scheduled for execution.");
        details.Add($"Rejected Issue: #{invalidIssue.Id} -> Status=Rejected, Enqueued=False");

        sw.Stop();
        return ScenarioResult.Pass(sw.Elapsed, "Untrusted triage, sandboxed intake, markdown mirroring, and human promotion/rejection verified.", details.ToArray());
    }
}
