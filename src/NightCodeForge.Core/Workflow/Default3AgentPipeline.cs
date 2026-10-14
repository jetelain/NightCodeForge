using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Factory creating the standard full pipeline:
/// Stage 1: TaskReviewStage
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
        TaskReviewStage? reviewStage = null,
        DeterministicPreImplementationStage? preImplStage = null,
        ImplementationStage? implStage = null,
        DeterministicPostImplementationStage? postImplStage = null,
        CodeReviewAndFixStage? fixStage = null,
        DeterministicPostReviewStage? postReviewStage = null,
        IExecutionEnvironmentProvider? envProvider = null,
        IExecutionEnvironment? executionEnv = null,
        IOllamaModelPreloadService? preloadService = null)
    {
        return new WorkflowPipeline(new IWorkflowStage[]
        {
            reviewStage ?? new TaskReviewStage(),
            preImplStage ?? new DeterministicPreImplementationStage(
                envProvider: envProvider,
                executionEnv: executionEnv,
                preloadService: preloadService),
            implStage ?? new ImplementationStage(),
            postImplStage ?? new DeterministicPostImplementationStage(
                envProvider: envProvider,
                executionEnv: executionEnv),
            fixStage ?? new CodeReviewAndFixStage(),
            postReviewStage ?? new DeterministicPostReviewStage(
                envProvider: envProvider,
                executionEnv: executionEnv,
                preloadService: preloadService)
        });
    }
}
