using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Deterministic quality gate enforcing that no new compiler or linter warnings are introduced compared to baseline.
/// </summary>
public class DeterministicWarningRatchetStage : IWorkflowStage
{
    public const string DefaultStageId = StageImplementationTypes.DeterministicRatchet;
    public const string FallbackLoopBackStageId = StandardStageIds.Implementation;

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public string LoopBackStageId { get; set; } = FallbackLoopBackStageId;

    public const string DefaultDisplayName = "Compiler & Linter Warning Ratchet";

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public DeterministicWarningRatchetStage()
        : this(DefaultStageId, DefaultDisplayName)
    {
    }

    public DeterministicWarningRatchetStage(string stageId, string displayName)
    {
        StageId = stageId;
        DisplayName = displayName;
    }

    public Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var bag = context.DataBag;

        if (bag.TryGet<int>(PipelineDataBagKeys.WarningsBaselineTotal, out var baselineWarnings) &&
            bag.TryGet<int>(PipelineDataBagKeys.WarningsCurrentTotal, out var currentWarnings))
        {
            if (currentWarnings > baselineWarnings)
            {
                var delta = currentWarnings - baselineWarnings;
                var newWarnings = bag.Get<List<string>>(PipelineDataBagKeys.WarningsNew) ?? new List<string>();
                var details = newWarnings.Count > 0 ? string.Join("; ", newWarnings) : $"{delta} new warning(s) found.";
                var error = $"Warning ratchet violation: warning count increased from {baselineWarnings} to {currentWarnings} (Delta: +{delta}). Details: {details}";
                bag.Set(PipelineDataBagKeys.RatchetFailureReason, error);
                var targetStage = bag.Get<string>(PipelineDataBagKeys.LoopBackId) ?? LoopBackStageId;
                return Task.FromResult(StageExecutionResult.Failure(error, targetStage));
            }
        }

        return Task.FromResult(StageExecutionResult.Success());
    }
}
