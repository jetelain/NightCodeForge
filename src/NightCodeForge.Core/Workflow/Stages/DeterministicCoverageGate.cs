using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Deterministic quality gate verifying that code coverage does not degrade and test count does not decrease.
/// Automatically executes the test suite if test results have not yet been populated in the pipeline.
/// </summary>
public class DeterministicCoverageGate : IWorkflowStage
{
    public const string DefaultStageId = StageImplementationTypes.DeterministicCoverage;
    public const string FallbackLoopBackStageId = StandardStageIds.Implementation;

    private readonly IExecutionEnvironmentProvider _envProvider;
    private readonly string? _testCommand;

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public string LoopBackStageId { get; set; } = FallbackLoopBackStageId;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public DeterministicCoverageGate(IExecutionEnvironmentProvider envProvider)
        : this(envProvider, DefaultStageId, "Deterministic Test & Coverage Gate", null)
    {
    }

    public DeterministicCoverageGate(
        IExecutionEnvironmentProvider envProvider,
        string stageId,
        string displayName,
        string? testCommand)
    {
        _envProvider = envProvider ?? throw new ArgumentNullException(nameof(envProvider));
        StageId = !string.IsNullOrWhiteSpace(stageId) ? stageId : DefaultStageId;
        DisplayName = !string.IsNullOrWhiteSpace(displayName) ? displayName : "Deterministic Test & Coverage Gate";
        _testCommand = testCommand;
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bag = context.DataBag;

        // If tests have not been executed yet in this pipeline run, execute them now
        if (!bag.TryGet<int>(PipelineDataBagKeys.TestsCurrentTotal, out _))
        {
            var testStage = new DeterministicTestStage(
                _envProvider,
                _testCommand ?? "dotnet test --no-build",
                stageId: StageId,
                displayName: DisplayName);

            var testResult = await testStage.ExecuteAsync(context, cancellationToken);
            if (!testResult.IsSuccess)
            {
                return testResult;
            }
        }

        // 1. Anti-cheating check: Test count preservation
        if (bag.TryGet<int>(PipelineDataBagKeys.TestsBaselineTotal, out var baselineTests) &&
            bag.TryGet<int>(PipelineDataBagKeys.TestsCurrentTotal, out var currentTests))
        {
            if (currentTests < baselineTests)
            {
                var error = $"Anti-cheating violation: total test count decreased from {baselineTests} to {currentTests}. Tests must not be deleted or disabled.";
                bag.Set(PipelineDataBagKeys.CoverageFailureReason, error);
                var targetStage = bag.Get<string>(PipelineDataBagKeys.LoopBackId) ?? LoopBackStageId;
                return StageExecutionResult.Failure(error, targetStage);
            }
        }

        // 2. Code coverage regression check
        if (bag.TryGet<double>(PipelineDataBagKeys.CoverageBaselineLineRate, out var baselineLineRate) &&
            bag.TryGet<double>(PipelineDataBagKeys.CoverageCurrentLineRate, out var currentLineRate))
        {
            if (currentLineRate < baselineLineRate)
            {
                var delta = currentLineRate - baselineLineRate;
                var error = $"Coverage regression detected: line coverage decreased from {baselineLineRate:P2} to {currentLineRate:P2} (Delta: {delta:P2}). Quality gate requires Delta >= 0.0%.";
                bag.Set(PipelineDataBagKeys.CoverageFailureReason, error);
                var targetStage = bag.Get<string>(PipelineDataBagKeys.LoopBackId) ?? LoopBackStageId;
                return StageExecutionResult.Failure(error, targetStage);
            }
        }

        return StageExecutionResult.Success();
    }
}
