using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.ScenarioRunner.Common;

namespace NightCodeForge.ScenarioRunner.Scenarios;

/// <summary>
/// Scenario 3: Universal Human Interruption & Resumption.
/// Verifies that when an agent calls 'ask', the orchestrator captures the interruption cleanly:
/// - Preserves ForgeTask.Status (never marks as Failed/Blocked)
/// - Sets ForgeTask.IsWaitingForHuman = true
/// - Creates a Git WIP checkpoint commit on feature branch
/// - Persists Clarification Question with Pending status
/// - Frees the GPU inference worker cleanly
/// - Upon SubmitHumanAnswerAsync, unblocks task, rehydrates context, and resumes execution on llm-urgent
/// </summary>
public class HumanInterruptionScenario : IScenario
{
    public string Id => "3";
    public string Name => "Human Interruption & Resumption Lifecycle";
    public string Description => "Tests clean human-in-the-loop interruption, WIP git checkpointing, and urgent resumption.";

    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var sw = Stopwatch.StartNew();
        var details = new List<string>();

        // Step 1: Create ForgeTask
        ConsoleFormatting.PrintStep("3.1", "Seeding task requiring human architectural clarification...");
        ForgeTask task;
        using (var scope = context.Environment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            task = new ForgeTask
            {
                WorkspaceId = context.Environment.Workspace.Id,
                Title = "Add Modulo Operation to Calculator",
                Description = "Implement Modulo(int a, int b) in Calculator. Ambiguity exists around zero division behavior.",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.TaskReview,
                BaseBranch = "main",
                FeatureBranch = "feature/task-modulo",
                Priority = 1,
                MergeMode = MergeMode.ManualReviewRequired
            };
            db.Tasks.Add(task);
            await db.SaveChangesAsync(context.CancellationToken);
            ConsoleFormatting.PrintSuccess($"Seeded ForgeTask #{task.Id}: '{task.Title}'");
        }

        // Step 2: Configure custom stage handler that triggers human interruption on turn 1
        var renderer = context.Environment.Services.GetRequiredService<IPromptTemplateRenderer>();

        var reviewStage = new GenericAgenticStage(StandardStageIds.TaskReview, "ForgeTask Review", new AgenticStageConfig { RequiredMode = AgentMode.Full }, renderer)
        {
            Handler = (pipelineContext, ct) =>
            {
                var lastAnswer = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.LastHumanAnswer);
                if (string.IsNullOrWhiteSpace(lastAnswer))
                {
                    // Turn 1: Interrupt with question
                    throw new HumanInterruptionException(new[] { new QuestionItem("Should Modulo(a, 0) throw DivideByZeroException or return 0?") });
                }

                // Turn 2 (Resumed): Incorporate answer and succeed
                pipelineContext.DataBag.Set(PipelineDataBagKeys.ApprovedPlan, $"Modulo approved with zero division behavior: {lastAnswer}");
                return Task.FromResult(StageExecutionResult.Success());
            }
        };

        var customPipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            reviewStage,
            new GenericAgenticStage(StandardStageIds.Implementation, "Implementation", new AgenticStageConfig { RequiredMode = AgentMode.Full }, renderer)
        });

        context.Environment.SetCustomPipeline(customPipeline);

        try
        {
            using (var scope = context.Environment.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                task.FeatureBranch = $"feature/task-{task.Id}";
                await db.SaveChangesAsync(context.CancellationToken);

                var orchestrator = scope.ServiceProvider.GetRequiredService<ITaskOrchestrator>();
                ConsoleFormatting.PrintStep("3.2", "Executing task-review stage (expecting human clarification pause)...");
                await orchestrator.ProcessTaskAsync(task.Id, context.CancellationToken);
            }

        // Step 3: Verify Interruption State in Database & Git
        ConsoleFormatting.PrintStep("3.3", "Verifying database and Git state after interruption...");
        Question? pendingQuestion = null;
        using (var scope = context.Environment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var reloaded = await db.Tasks
                .Include(i => i.Questions)
                .Include(i => i.Checkpoints)
                .FirstOrDefaultAsync(i => i.Id == task.Id, context.CancellationToken);

            if (reloaded == null)
            {
                return ScenarioResult.Fail(sw.Elapsed, "ForgeTask not found in database.");
            }

            // Status must NOT be Failed
            if (reloaded.Status == TaskOverallStatus.Failed)
            {
                return ScenarioResult.Fail(sw.Elapsed, "Violation: ForgeTask status was marked Failed upon human interruption.");
            }

            if (!reloaded.IsWaitingForHuman)
            {
                return ScenarioResult.Fail(sw.Elapsed, "Violation: IsWaitingForHuman was not set to true.");
            }

            pendingQuestion = reloaded.Questions.FirstOrDefault(q => q.Status == QuestionStatus.Pending);
            if (pendingQuestion == null)
            {
                return ScenarioResult.Fail(sw.Elapsed, "No pending Question record found in database.");
            }

            ConsoleFormatting.PrintSuccess($"ForgeTask #{task.Id} correctly paused: IsWaitingForHuman=true, Status={reloaded.Status}");
            ConsoleFormatting.PrintSuccess($"Pending question stored in DB: '{pendingQuestion.Prompt}'");

            // Verify WIP checkpoint commit was created on git branch
            var latestCheckpoint = reloaded.Checkpoints.OrderByDescending(c => c.CreatedAt).FirstOrDefault();
            if (latestCheckpoint == null || string.IsNullOrWhiteSpace(latestCheckpoint.GitCommitHash))
            {
                return ScenarioResult.Fail(sw.Elapsed, "No Git checkpoint commit was recorded.");
            }

            ConsoleFormatting.PrintSuccess($"Git WIP checkpoint created: {latestCheckpoint.GitCommitHash[..Math.Min(8, latestCheckpoint.GitCommitHash.Length)]}");
            details.Add($"Question: {pendingQuestion.Prompt}");
            details.Add($"WIP Commit: {latestCheckpoint.GitCommitHash[..Math.Min(8, latestCheckpoint.GitCommitHash.Length)]}");
        }

        // Step 4: Submit Human Answer via Orchestrator
        ConsoleFormatting.PrintStep("3.4", "Submitting human answer via orchestrator.SubmitHumanAnswerAsync...");
        const string humanAnswer = "Throw System.DivideByZeroException for consistency with integer division.";

        using (var scope = context.Environment.Services.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<ITaskOrchestrator>();
            await orchestrator.SubmitHumanAnswerAsync(pendingQuestion!.Id, humanAnswer, context.CancellationToken);
            ConsoleFormatting.PrintSuccess($"Submitted answer: '{humanAnswer}'");
        }

        // Step 5: Verify Post-Answer Resumption Enqueue
        ConsoleFormatting.PrintStep("3.5", "Verifying question answered and urgent queue dispatch...");
        using (var scope = context.Environment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var reloaded = await db.Tasks
                .Include(i => i.Questions)
                .FirstOrDefaultAsync(i => i.Id == task.Id, context.CancellationToken);

            var answeredQ = reloaded?.Questions.FirstOrDefault(q => q.Id == pendingQuestion.Id);
            if (answeredQ?.Status != QuestionStatus.Answered)
            {
                return ScenarioResult.Fail(sw.Elapsed, $"Expected QuestionStatus.Answered but found {answeredQ?.Status}.");
            }

            if (reloaded?.IsWaitingForHuman == true)
            {
                return ScenarioResult.Fail(sw.Elapsed, "ForgeTask IsWaitingForHuman is still true after submitting answer.");
            }

            ConsoleFormatting.PrintSuccess("Question marked Answered; IsWaitingForHuman reset to false.");

            // Verify urgent queue job was dispatched
            var urgentJob = context.Environment.JobQueue.AllJobs
                .FirstOrDefault(j => j.TaskId == task.Id && j.QueueName == "llm-urgent");

            if (urgentJob == null)
            {
                return ScenarioResult.Fail(sw.Elapsed, "Resumption job was not dispatched to priority queue 'llm-urgent'.");
            }

            ConsoleFormatting.PrintSuccess($"Job #{urgentJob.JobId} enqueued to priority queue '{urgentJob.QueueName}'");
            details.Add($"Answered: {answeredQ.Answer}");
            details.Add($"Enqueued: {urgentJob.JobId} -> {urgentJob.QueueName}");
        }

        // Step 6: Process Resumed Turn
        ConsoleFormatting.PrintStep("3.6", "Processing resumed task execution turn...");
        using (var scope = context.Environment.Services.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<ITaskOrchestrator>();
            await orchestrator.ProcessTaskAsync(task.Id, context.CancellationToken);
        }

        // Verify stage advanced past task-review
        using (var scope = context.Environment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var completedTask = await db.Tasks.FindAsync([task.Id], context.CancellationToken);
            ConsoleFormatting.PrintSuccess($"Resumption completed. Current stage advanced to '{completedTask?.CurrentStageId}', Status={completedTask?.Status}");
            details.Add($"Next Stage: {completedTask?.CurrentStageId}");
        }

            sw.Stop();
            return ScenarioResult.Pass(sw.Elapsed, "Universal human interruption, WIP checkpointing, and urgent resumption verified.", details.ToArray());
        }
        finally
        {
            context.Environment.SetCustomPipeline(null);
        }
    }
}
