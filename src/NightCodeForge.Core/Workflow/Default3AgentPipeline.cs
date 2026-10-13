using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Factory creating the standard full pipeline:
/// Stage 1: IssueReviewStage
/// -> Stage 2: DeterministicPreImplementationStage
/// -> Stage 3: ImplementationStage
/// -> Stage 4: DeterministicPostImplementationStage
/// -> Stage 5: CodeReviewAndFixStage
/// -> Stage 6: DeterministicPostReviewStage
/// (Stage 7: Merge Gate handled by orchestrator upon completion).
/// </summary>
public static class Default3AgentPipeline
{
    public static IWorkflowPipeline Create(
        IssueReviewStage? reviewStage = null,
        DeterministicPreImplementationStage? preImplStage = null,
        ImplementationStage? implStage = null,
        DeterministicPostImplementationStage? postImplStage = null,
        CodeReviewAndFixStage? fixStage = null,
        DeterministicPostReviewStage? postReviewStage = null)
    {
        return new WorkflowPipeline(new IWorkflowStage[]
        {
            reviewStage ?? new IssueReviewStage(),
            preImplStage ?? new DeterministicPreImplementationStage(),
            implStage ?? new ImplementationStage(),
            postImplStage ?? new DeterministicPostImplementationStage(),
            fixStage ?? new CodeReviewAndFixStage(),
            postReviewStage ?? new DeterministicPostReviewStage()
        });
    }
}
