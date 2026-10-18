using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class PipelineEngineTests
{
    [Fact]
    public async Task Pipeline_AdvancesSequentiallyToCompletion()
    {
        var stage1 = TestWorkflowPipelines.CreateReviewStage();
        var stage2 = TestWorkflowPipelines.CreateImplementationStage();
        var stage3 = TestWorkflowPipelines.CreateCodeReviewStage();

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage1, stage2, stage3 });
        var engine = new WorkflowPipelineEngine();

        var task = new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Sequential Test" };
        var workspace = new Workspace { Id = 1, Name = "WS1" };
        var context = new PipelineContext(task, workspace);

        // Step 1: TaskReview
        var res1 = await engine.ExecuteStepAsync(pipeline, context);
        Assert.True(res1.IsSuccess);
        Assert.Equal("implementation", task.CurrentStageId);
        Assert.Equal(TaskOverallStatus.Reviewed, task.Status);

        // Step 2: Implementation
        var res2 = await engine.ExecuteStepAsync(pipeline, context);
        Assert.True(res2.IsSuccess);
        Assert.Equal("code-review-fix", task.CurrentStageId);
        Assert.Equal(TaskOverallStatus.Implemented, task.Status);

        // Step 3: CodeReviewAndFix (End of pipeline)
        var res3 = await engine.ExecuteStepAsync(pipeline, context);
        Assert.True(res3.IsSuccess);
        Assert.Equal(TaskOverallStatus.Ready, task.Status);
    }

    [Fact]
    public async Task Pipeline_LoopsBackOnStageFailure()
    {
        var stage1 = TestWorkflowPipelines.CreateReviewStage();
        var stage2 = TestWorkflowPipelines.CreateImplementationStage();
        var stage3 = TestWorkflowPipelines.CreateCodeReviewStage(
            (_, _) => Task.FromResult(StageExecutionResult.Failure("Missing test cases", "implementation")));

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage1, stage2, stage3 });
        var engine = new WorkflowPipelineEngine();

        var task = new ForgeTask
        {
            Id = 1,
            WorkspaceId = 1,
            CurrentStageId = "code-review-fix",
            Status = TaskOverallStatus.Implemented
        };
        var workspace = new Workspace { Id = 1, Name = "WS1" };
        var context = new PipelineContext(task, workspace);

        var result = await engine.ExecuteStepAsync(pipeline, context);

        Assert.False(result.IsSuccess);
        Assert.Equal("implementation", task.CurrentStageId);
        Assert.Equal(1, context.RetryCount);
    }

    [Fact]
    public async Task Pipeline_TripsCircuitBreakerAfterMaxRetries()
    {
        var failingStage = TestWorkflowPipelines.CreateImplementationStage(
            (_, _) => Task.FromResult(StageExecutionResult.Failure("Build error CS1002", "implementation")));

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { failingStage });
        var engine = new WorkflowPipelineEngine { MaxRetries = 3 };

        var task = new ForgeTask { Id = 1, WorkspaceId = 1, CurrentStageId = "implementation" };
        var workspace = new Workspace { Id = 1, Name = "WS1" };
        var context = new PipelineContext(task, workspace);

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
        Assert.Equal(TaskOverallStatus.Failed, task.Status);
        Assert.Contains("exceeded maximum retry count", fatalResult.ErrorMessage);
    }

    [Fact]
    public async Task Pipeline_TransitionsToWaitingForHumanWhenClarificationNeeded()
    {
        var reviewStage = TestWorkflowPipelines.CreateReviewStage(
            (_, _) =>
            {
                var q = new Question
                {
                    Prompt = "Which authentication strategy?",
                    Rationale = "OIDC vs Local DB"
                };
                return Task.FromResult(StageExecutionResult.QuestionRequired(new[] { q }));
            });

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { reviewStage });
        var engine = new WorkflowPipelineEngine();

        var task = new ForgeTask { Id = 5, WorkspaceId = 1, Status = TaskOverallStatus.Open, CurrentStageId = "task-review" };
        var workspace = new Workspace { Id = 1, Name = "WS1" };
        var context = new PipelineContext(task, workspace);

        var result = await engine.ExecuteStepAsync(pipeline, context);

        Assert.False(result.IsSuccess);
        Assert.True(result.NeedsClarification);
        Assert.True(task.IsWaitingForHuman);
        Assert.Equal(TaskOverallStatus.Open, task.Status);
        Assert.Single(task.Questions);
        Assert.Equal("Which authentication strategy?", task.Questions.First().Prompt);
    }
}
