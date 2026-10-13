using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Factory creating the standard 3-agent pipeline with deterministic quality gate:
/// IssueReview -> Implementation -> DeterministicCoverageGate -> CodeReviewAndFix.
/// </summary>
public static class Default3AgentPipeline
{
    public static IWorkflowPipeline Create(
        IssueReviewStage? reviewStage = null,
        ImplementationStage? implStage = null,
        DeterministicCoverageGate? coverageGate = null,
        CodeReviewAndFixStage? fixStage = null)
    {
        return new WorkflowPipeline(new IWorkflowStage[]
        {
            reviewStage ?? new IssueReviewStage(),
            implStage ?? new ImplementationStage(),
            coverageGate ?? new DeterministicCoverageGate(),
            fixStage ?? new CodeReviewAndFixStage()
        });
    }
}
