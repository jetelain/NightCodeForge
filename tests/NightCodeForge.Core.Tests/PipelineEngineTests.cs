using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class PipelineEngineTests
{
    [Fact]
    public async Task Pipeline_AdvancesSequentiallyToCompletion()
    {
        var stage1 = new IssueReviewStage();
        var stage2 = new ImplementationStage();
        var stage3 = new CodeReviewAndFixStage();

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage1, stage2, stage3 });
        var engine = new WorkflowPipelineEngine();

        var issue = new Issue { Id = 1, WorkspaceId = 1, Title = "Sequential Test" };
        var workspace = new Workspace { Id = 1, Name = "WS1" };
        var context = new PipelineContext(issue, workspace);

        // Step 1: IssueReview
        var res1 = await engine.ExecuteStepAsync(pipeline, context);
        Assert.True(res1.IsSuccess);
        Assert.Equal("implementation", issue.CurrentStageId);
        Assert.Equal(IssueOverallStatus.Running, issue.Status);

        // Step 2: Implementation
        var res2 = await engine.ExecuteStepAsync(pipeline, context);
        Assert.True(res2.IsSuccess);
        Assert.Equal("code-review-fix", issue.CurrentStageId);

        // Step 3: CodeReviewAndFix (End of pipeline)
        var res3 = await engine.ExecuteStepAsync(pipeline, context);
        Assert.True(res3.IsSuccess);
        Assert.Equal(IssueOverallStatus.Completed, issue.Status);
    }

    [Fact]
    public async Task Pipeline_LoopsBackOnStageFailure()
    {
        var stage1 = new IssueReviewStage();
        var stage2 = new ImplementationStage();
        var stage3 = new CodeReviewAndFixStage
        {
            Handler = (_, _) => Task.FromResult(StageExecutionResult.Failure("Missing test cases", "implementation"))
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage1, stage2, stage3 });
        var engine = new WorkflowPipelineEngine();

        var issue = new Issue
        {
            Id = 1,
            WorkspaceId = 1,
            CurrentStageId = "code-review-fix",
            Status = IssueOverallStatus.Running
        };
        var workspace = new Workspace { Id = 1, Name = "WS1" };
        var context = new PipelineContext(issue, workspace);

        var result = await engine.ExecuteStepAsync(pipeline, context);

        Assert.False(result.IsSuccess);
        Assert.Equal("implementation", issue.CurrentStageId);
        Assert.Equal(1, context.RetryCount);
    }

    [Fact]
    public async Task Pipeline_TripsCircuitBreakerAfterMaxRetries()
    {
        var failingStage = new ImplementationStage
        {
            Handler = (_, _) => Task.FromResult(StageExecutionResult.Failure("Build error CS1002", "implementation"))
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { failingStage });
        var engine = new WorkflowPipelineEngine { MaxRetries = 3 };

        var issue = new Issue { Id = 1, WorkspaceId = 1, CurrentStageId = "implementation" };
        var workspace = new Workspace { Id = 1, Name = "WS1" };
        var context = new PipelineContext(issue, workspace);

        // Retries 1, 2, 3
        await engine.ExecuteStepAsync(pipeline, context);
        Assert.Equal(1, context.RetryCount);

        await engine.ExecuteStepAsync(pipeline, context);
        Assert.Equal(2, context.RetryCount);

        await engine.ExecuteStepAsync(pipeline, context);
        Assert.Equal(3, context.RetryCount);

        // 4th failure exceeds MaxRetries (3)
        var fatalResult = await engine.ExecuteStepAsync(pipeline, context);

        Assert.False(fatalResult.IsSuccess);
        Assert.Equal(IssueOverallStatus.Failed, issue.Status);
        Assert.Contains("exceeded maximum retry count", fatalResult.ErrorMessage);
    }

    [Fact]
    public async Task Pipeline_TransitionsToBlockedQuestionWhenClarificationNeeded()
    {
        var reviewStage = new IssueReviewStage
        {
            Handler = (_, _) =>
            {
                var q = new Question
                {
                    Prompt = "Which authentication strategy?",
                    Rationale = "OIDC vs Local DB"
                };
                return Task.FromResult(StageExecutionResult.QuestionRequired(q));
            }
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { reviewStage });
        var engine = new WorkflowPipelineEngine();

        var issue = new Issue { Id = 5, WorkspaceId = 1, CurrentStageId = "issue-review" };
        var workspace = new Workspace { Id = 1, Name = "WS1" };
        var context = new PipelineContext(issue, workspace);

        var result = await engine.ExecuteStepAsync(pipeline, context);

        Assert.False(result.IsSuccess);
        Assert.True(result.NeedsClarification);
        Assert.Equal(IssueOverallStatus.BlockedQuestion, issue.Status);
        Assert.Single(issue.Questions);
        Assert.Equal("Which authentication strategy?", issue.Questions.First().Prompt);
    }
}
