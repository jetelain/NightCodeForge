using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Orchestrates the execution of stages in a workflow pipeline with routing and circuit breakers.
/// </summary>
public interface IWorkflowPipelineEngine
{
    int MaxRetries { get; set; }
    Task<StageExecutionResult> ExecuteStepAsync(IWorkflowPipeline pipeline, PipelineContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Engine executing pipeline stages with forward advancement, backward failure loops, and circuit breakers.
/// </summary>
public class WorkflowPipelineEngine : IWorkflowPipelineEngine
{
    public int MaxRetries { get; set; } = 3;

    public async Task<StageExecutionResult> ExecuteStepAsync(
        IWorkflowPipeline pipeline,
        PipelineContext context,
        CancellationToken cancellationToken = default)
    {
        if (pipeline == null) throw new ArgumentNullException(nameof(pipeline));
        if (context == null) throw new ArgumentNullException(nameof(context));

        // Determine stage to execute
        var currentStageId = context.Issue.CurrentStageId;
        IWorkflowStage? stage;

        if (string.IsNullOrWhiteSpace(currentStageId))
        {
            stage = pipeline.Stages.FirstOrDefault();
            if (stage != null)
            {
                context.Issue.CurrentStageId = stage.StageId;
            }
        }
        else
        {
            stage = pipeline.GetStage(currentStageId);
        }

        if (stage == null)
        {
            var err = $"Stage '{currentStageId}' not found in pipeline.";
            context.Issue.Status = IssueOverallStatus.Failed;
            return StageExecutionResult.Failure(err);
        }

        var result = await stage.ExecuteAsync(context, cancellationToken);

        if (result.NeedsClarification)
        {
            context.Issue.IsWaitingForHuman = true;
            context.Issue.InterruptedStageId = stage.StageId;
            if (result.ClarificationQuestion != null)
            {
                result.ClarificationQuestion.IssueId = context.Issue.Id;
                result.ClarificationQuestion.StageId = stage.StageId;
                context.Issue.Questions.Add(result.ClarificationQuestion);
            }
            context.SyncDataBagToIssue();
            return result;
        }

        if (result.IsSuccess)
        {
            // Reset retry count on forward progression
            context.ResetRetries();

            // Status stamps per stage
            if (string.Equals(stage.StageId, "issue-review", StringComparison.OrdinalIgnoreCase))
            {
                context.Issue.Status = IssueOverallStatus.Reviewed;
            }
            else if (string.Equals(stage.StageId, "implementation", StringComparison.OrdinalIgnoreCase))
            {
                context.Issue.Status = IssueOverallStatus.Implemented;
            }
            else if (string.Equals(stage.StageId, "deterministic-post-review", StringComparison.OrdinalIgnoreCase))
            {
                context.Issue.Status = IssueOverallStatus.Ready;
            }

            var nextStageId = result.TargetRoutingStageId;
            IWorkflowStage? nextStage;

            if (!string.IsNullOrWhiteSpace(nextStageId))
            {
                nextStage = pipeline.GetStage(nextStageId);
            }
            else
            {
                nextStage = pipeline.GetNextStage(stage.StageId);
            }

            if (nextStage != null)
            {
                context.Issue.CurrentStageId = nextStage.StageId;
            }
            else
            {
                // Reached end of pipeline
                context.Issue.Status = IssueOverallStatus.Ready;
            }

            context.SyncDataBagToIssue();
            return result;
        }

        // Failure handling & loop circuit breaker
        var retries = context.IncrementRetry(stage.StageId);
        var effectiveMaxRetries = context.DataBag.Get<int?>("Workflow.MaxRetries") ?? MaxRetries;
        if (retries > effectiveMaxRetries)
        {
            context.Issue.Status = IssueOverallStatus.Failed;
            context.SyncDataBagToIssue();
            return StageExecutionResult.Failure($"Stage '{stage.StageId}' failed and exceeded maximum retry count of {effectiveMaxRetries}: {result.ErrorMessage}");
        }

        // Backward loop or retry routing
        if (!string.IsNullOrWhiteSpace(result.TargetRoutingStageId))
        {
            context.Issue.CurrentStageId = result.TargetRoutingStageId;
        }

        context.SyncDataBagToIssue();
        return result;
    }
}
