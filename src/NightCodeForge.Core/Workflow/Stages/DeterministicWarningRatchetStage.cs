using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Deterministic quality gate enforcing that no new compiler or linter warnings are introduced compared to baseline.
/// </summary>
public class DeterministicWarningRatchetStage : IWorkflowStage
{
    public const string DefaultStageId = "warning-ratchet";
    public const string FallbackLoopBackStageId = "implementation";

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public string LoopBackStageId { get; set; } = FallbackLoopBackStageId;

    public DeterministicWarningRatchetStage(string stageId = DefaultStageId, string displayName = "Compiler & Linter Warning Ratchet")
    {
        StageId = stageId;
        DisplayName = displayName;
    }

    public Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var bag = context.DataBag;

        if (bag.TryGet<int>("warnings.baseline.total", out var baselineWarnings) &&
            bag.TryGet<int>("warnings.current.total", out var currentWarnings))
        {
            if (currentWarnings > baselineWarnings)
            {
                var delta = currentWarnings - baselineWarnings;
                var newWarnings = bag.Get<List<string>>("warnings.new") ?? new List<string>();
                var details = newWarnings.Count > 0 ? string.Join("; ", newWarnings) : $"{delta} new warning(s) found.";
                var error = $"Warning ratchet violation: warning count increased from {baselineWarnings} to {currentWarnings} (Delta: +{delta}). Details: {details}";
                bag.Set("ratchet.failure.reason", error);
                var targetStage = bag.Get<string>("Stage.LoopBackId") ?? LoopBackStageId;
                return Task.FromResult(StageExecutionResult.Failure(error, targetStage));
            }
        }

        return Task.FromResult(StageExecutionResult.Success());
    }
}
