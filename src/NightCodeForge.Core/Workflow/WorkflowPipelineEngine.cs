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
    public int MaxRetries { get; set; } = WorkflowDefaults.DefaultMaxStageRetries;

    public async Task<StageExecutionResult> ExecuteStepAsync(
        IWorkflowPipeline pipeline,
        PipelineContext context,
        CancellationToken cancellationToken = default)
    {
        if (pipeline == null) throw new ArgumentNullException(nameof(pipeline));
        if (context == null) throw new ArgumentNullException(nameof(context));

        // Determine stage to execute
        var currentStageId = context.ForgeTask.CurrentStageId;
        IWorkflowStage? stage;

        if (string.IsNullOrWhiteSpace(currentStageId))
        {
            stage = pipeline.Stages.FirstOrDefault();
            if (stage != null)
            {
                context.ForgeTask.CurrentStageId = stage.StageId;
            }
        }
        else
        {
            stage = pipeline.GetStage(currentStageId);
        }

        if (stage == null)
        {
            var err = $"Stage '{currentStageId}' not found in pipeline.";
            context.ForgeTask.Status = TaskOverallStatus.Failed;
            return StageExecutionResult.Failure(err);
        }

        var result = await stage.ExecuteAsync(context, cancellationToken);

        if (result.NeedsClarification)
        {
            context.ForgeTask.IsWaitingForHuman = true;
            context.ForgeTask.InterruptedStageId = stage.StageId;
            var batchId = Guid.NewGuid().ToString("N");
            if (result.ClarificationQuestions != null)
            {
                foreach (var q in result.ClarificationQuestions)
                {
                    q.TaskId = context.ForgeTask.Id;
                    q.StageId = stage.StageId;
                    q.BatchId = batchId;
                    context.ForgeTask.Questions.Add(q);
                }
            }
            context.SyncDataBagToTask();
            return result;
        }

        if (result.IsSuccess)
        {
            // Reset retry count on forward progression
            context.ResetRetries();

            if (string.Equals(stage.StageId, StandardStageIds.PostReviewVerification, StringComparison.OrdinalIgnoreCase))
            {
                context.ForgeTask.Status = TaskOverallStatus.Ready;
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
                context.ForgeTask.CurrentStageId = nextStage.StageId;
            }
            else
            {
                // Reached end of pipeline
                context.ForgeTask.Status = TaskOverallStatus.Ready;
            }

            context.SyncDataBagToTask();
            return result;
        }

        // Failure handling & loop circuit breaker
        var retries = context.IncrementRetry(stage.StageId);
        var effectiveMaxRetries = context.DataBag.Get<int?>(PipelineDataBagKeys.MaxRetries) ?? MaxRetries;
        if (retries > effectiveMaxRetries)
        {
            context.ForgeTask.Status = TaskOverallStatus.Failed;
            context.SyncDataBagToTask();
            return StageExecutionResult.Failure($"Stage '{stage.StageId}' failed and exceeded maximum retry count of {effectiveMaxRetries}: {result.ErrorMessage}");
        }

        // Backward loop or retry routing
        if (!string.IsNullOrWhiteSpace(result.TargetRoutingStageId))
        {
            context.ForgeTask.CurrentStageId = result.TargetRoutingStageId;
        }

        context.SyncDataBagToTask();
        return result;
    }
}
