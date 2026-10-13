using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Deterministic quality gate verifying that code coverage does not degrade and test count does not decrease.
/// </summary>
public class DeterministicCoverageGate : IWorkflowStage
{
    public const string DefaultStageId = "coverage-gate";
    public const string FallbackLoopBackStageId = "implementation";

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public string LoopBackStageId { get; set; } = FallbackLoopBackStageId;

    public DeterministicCoverageGate(string stageId = DefaultStageId, string displayName = "Deterministic Test & Coverage Gate")
    {
        StageId = stageId;
        DisplayName = displayName;
    }

    public Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var bag = context.DataBag;

        // 1. Anti-cheating check: Test count preservation
        if (bag.TryGet<int>("tests.baseline.total", out var baselineTests) &&
            bag.TryGet<int>("tests.current.total", out var currentTests))
        {
            if (currentTests < baselineTests)
            {
                var error = $"Anti-cheating violation: total test count decreased from {baselineTests} to {currentTests}. Tests must not be deleted or disabled.";
                bag.Set("gate.failure.reason", error);
                var targetStage = bag.Get<string>("Stage.LoopBackId") ?? LoopBackStageId;
                return Task.FromResult(StageExecutionResult.Failure(error, targetStage));
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
                return Task.FromResult(StageExecutionResult.Failure(error, targetStage));
            }
        }

        return Task.FromResult(StageExecutionResult.Success());
    }
}
