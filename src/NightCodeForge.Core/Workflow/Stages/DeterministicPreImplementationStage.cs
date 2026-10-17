using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Deterministic workflow stage capturing baseline compilation cleanliness, test suite passing status,
/// and baseline code coverage before the implementation agent begins edits.
/// </summary>
public class DeterministicPreImplementationStage : IWorkflowStage
{
    public const string DefaultStageId = StandardStageIds.PreImplementationBaseline;
    public string StageId => DefaultStageId;
    public string DisplayName => "Deterministic Pre-Implementation Baseline";
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;

    private readonly DeterministicBuildStage _buildStage;
    private readonly DeterministicTestStage _testStage;

    public DeterministicPreImplementationStage(
        DeterministicBuildStage? buildStage = null,
        DeterministicTestStage? testStage = null,
        IExecutionEnvironment? executionEnv = null,
        IExecutionEnvironmentProvider? envProvider = null,
        IOllamaModelPreloadService? preloadService = null)
    {
        _buildStage = buildStage ?? new DeterministicBuildStage(
            executionEnv: executionEnv,
            preloadService: preloadService,
            stageId: "pre-impl-build",
            displayName: "Pre-Implementation Baseline Build",
            envProvider: envProvider);

        _testStage = testStage ?? new DeterministicTestStage(
            executionEnv: executionEnv,
            stageId: "pre-impl-test",
            displayName: "Pre-Implementation Baseline Test",
            envProvider: envProvider);
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        // Capture baseline build/warning metrics
        await _buildStage.ExecuteAsync(context, cancellationToken);

        // Capture baseline test execution metrics
        await _testStage.ExecuteAsync(context, cancellationToken);

        // Baseline capture always succeeds so implementation stage can proceed
        return StageExecutionResult.Success();
    }
}
