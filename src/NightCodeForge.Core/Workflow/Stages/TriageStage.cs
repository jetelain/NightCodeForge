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
    public string StageDirective { get; set; } = "Analyze the reported task against the repository base branch. Check <existing_workspace_tasks> and inspect candidate items via get_task first. If an unstarted triage-created task (Origin: UntrustedIntake or GitHubIssue) already covers this issue, refine it via edit_task (or remove obsolete proposals via remove_task) instead of creating duplicates; never duplicate or alter Manual, Ideation, Import, or already-started tasks. Otherwise determine affected files and acceptance criteria and call propose_tasks to create structured Suggested backlog items (and use edit_task/remove_task with returned #IDs if refining them in-session). You have read-only codebase access and cannot modify code or access external networks.";

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
