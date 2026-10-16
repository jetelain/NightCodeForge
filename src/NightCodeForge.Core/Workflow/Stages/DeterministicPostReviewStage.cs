using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Deterministic workflow stage verifying project compilation cleanliness and unit test execution following Code Review edits.
/// If any compilation error or test failure occurs, loops backward to 'code-review-fix' with diagnostics.
/// On pass, transitions status to Ready.
/// </summary>
public class DeterministicPostReviewStage : IWorkflowStage
{
    public const string DefaultStageId = "deterministic-post-review";
    public const string LoopBackStageId = "code-review-fix";

    private readonly DeterministicBuildStage _buildStage;
    private readonly DeterministicTestStage _testStage;

    public string StageId => DefaultStageId;
    public string DisplayName => "Deterministic Post-Review Verification Gate";
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    string? IWorkflowStage.LoopBackStageId => LoopBackStageId;

    public DeterministicPostReviewStage(
        DeterministicBuildStage? buildStage = null,
        DeterministicTestStage? testStage = null,
        IExecutionEnvironment? executionEnv = null,
        IExecutionEnvironmentProvider? envProvider = null,
        IOllamaModelPreloadService? preloadService = null)
    {
        _buildStage = buildStage ?? new DeterministicBuildStage(
            executionEnv: executionEnv,
            preloadService: preloadService,
            stageId: "post-review-build",
            displayName: "Post-Review Build Verification",
            envProvider: envProvider)
        {
            LoopBackStageId = LoopBackStageId
        };

        _testStage = testStage ?? new DeterministicTestStage(
            executionEnv: executionEnv,
            stageId: "post-review-test",
            displayName: "Post-Review Test Verification",
            envProvider: envProvider)
        {
            LoopBackStageId = LoopBackStageId
        };
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        // 1. Verify build
        var buildResult = await _buildStage.ExecuteAsync(context, cancellationToken);
        if (!buildResult.IsSuccess)
        {
            return StageExecutionResult.Failure(
                buildResult.ErrorMessage ?? "Post-review build verification failed.",
                loopBackStageId: LoopBackStageId);
        }

        // 2. Verify tests
        var testResult = await _testStage.ExecuteAsync(context, cancellationToken);
        if (!testResult.IsSuccess)
        {
            return StageExecutionResult.Failure(
                testResult.ErrorMessage ?? "Post-review test verification failed.",
                loopBackStageId: LoopBackStageId);
        }

        return StageExecutionResult.Success();
    }
}
