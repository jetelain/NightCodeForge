using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Deterministic quality gate verifying that code coverage does not degrade and test count does not decrease.
/// Automatically executes the test suite if test results have not yet been populated in the pipeline.
/// </summary>
public class DeterministicCoverageGate : IWorkflowStage
{
    public const string DefaultStageId = "coverage-gate";
    public const string FallbackLoopBackStageId = "implementation";

    private readonly IExecutionEnvironment? _executionEnv;
    private readonly IExecutionEnvironmentProvider? _envProvider;
    private readonly string? _testCommand;

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public string LoopBackStageId { get; set; } = FallbackLoopBackStageId;

    public DeterministicCoverageGate(
        string stageId = DefaultStageId,
        string displayName = "Deterministic Test & Coverage Gate",
        IExecutionEnvironment? executionEnv = null,
        IExecutionEnvironmentProvider? envProvider = null,
        string? testCommand = null)
    {
        StageId = stageId;
        DisplayName = displayName;
        _executionEnv = executionEnv;
        _envProvider = envProvider;
        _testCommand = testCommand;
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var bag = context.DataBag;

        // If tests have not been executed yet in this pipeline run, execute them now
        if (!bag.TryGet<int>("tests.current.total", out _) && (_executionEnv != null || _envProvider != null))
        {
            var testStage = new DeterministicTestStage(
                _executionEnv,
                _testCommand,
                stageId: StageId,
                displayName: DisplayName,
                envProvider: _envProvider);

            var testResult = await testStage.ExecuteAsync(context, cancellationToken);
            if (!testResult.IsSuccess)
            {
                return testResult;
            }
        }

        // 1. Anti-cheating check: Test count preservation
        if (bag.TryGet<int>("tests.baseline.total", out var baselineTests) &&
            bag.TryGet<int>("tests.current.total", out var currentTests))
        {
            if (currentTests < baselineTests)
            {
                var error = $"Anti-cheating violation: total test count decreased from {baselineTests} to {currentTests}. Tests must not be deleted or disabled.";
                bag.Set("gate.failure.reason", error);
                var targetStage = bag.Get<string>("Stage.LoopBackId") ?? LoopBackStageId;
                return StageExecutionResult.Failure(error, targetStage);
            }
        }

        // 2. Code coverage regression check
        if (bag.TryGet<double>("coverage.baseline.line_rate", out var baselineLineRate) &&
            bag.TryGet<double>("coverage.current.line_rate", out var currentLineRate))
        {
            if (currentLineRate < baselineLineRate)
            {
                var delta = currentLineRate - baselineLineRate;
                var error = $"Coverage regression detected: line coverage decreased from {baselineLineRate:P2} to {currentLineRate:P2} (Delta: {delta:P2}). Quality gate requires Delta >= 0.0%.";
                bag.Set("gate.failure.reason", error);
                var targetStage = bag.Get<string>("Stage.LoopBackId") ?? LoopBackStageId;
                return StageExecutionResult.Failure(error, targetStage);
            }
        }

        return StageExecutionResult.Success();
    }
}
