using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Agentic stage performing untrusted intake triage on main/master, recommending backlog proposals.
/// </summary>
public class TriageStage : IWorkflowStage
{
    public const string DefaultStageId = StandardStageIds.Triage;
    public string StageId => DefaultStageId;
    public string DisplayName => "Untrusted Intake Triage";
    public WorkflowStageKind Kind => WorkflowStageKind.Agentic;
    public StageQueueTier QueueTier { get; set; } = StageQueueTier.TaskReview;
    public AgentMode RequiredMode => AgentMode.ReadOnly;
    public string StageDirective { get; set; } = "Analyze the reported task against the repository base branch. Determine affected files and acceptance criteria. Call propose_tasks to recommend structured backlog items. You have read-only access and cannot modify code or access external networks.";

    public Func<PipelineContext, CancellationToken, Task<StageExecutionResult>>? Handler { get; set; }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        if (Handler != null)
        {
            return await Handler(context, cancellationToken);
        }
        return StageExecutionResult.Success();
    }
}
