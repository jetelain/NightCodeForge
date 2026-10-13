using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Deterministic workflow stage evaluating coverage ratchet (Δ >= 0) and warning ratchet (Δ <= 0).
/// Loops back to 'implementation' if regressions are detected.
/// </summary>
public class DeterministicPostImplementationStage : IWorkflowStage
{
    public const string DefaultStageId = "deterministic-post-impl";
    public const string LoopBackStageId = "implementation";

    public string StageId => DefaultStageId;
    public string DisplayName => "Deterministic Post-Implementation Ratchet Gate";
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;

    private readonly DeterministicCoverageGate _coverageGate;
    private readonly DeterministicWarningRatchetStage _warningRatchet;

    public DeterministicPostImplementationStage(
        DeterministicCoverageGate? coverageGate = null,
        DeterministicWarningRatchetStage? warningRatchet = null)
    {
        _coverageGate = coverageGate ?? new DeterministicCoverageGate();
        _warningRatchet = warningRatchet ?? new DeterministicWarningRatchetStage();
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var covResult = await _coverageGate.ExecuteAsync(context, cancellationToken);
        if (!covResult.IsSuccess)
        {
            return StageExecutionResult.Failure(
                covResult.ErrorMessage ?? "Post-implementation coverage gate failed.",
                loopBackStageId: LoopBackStageId);
        }

        var warnResult = await _warningRatchet.ExecuteAsync(context, cancellationToken);
        if (!warnResult.IsSuccess)
        {
            return StageExecutionResult.Failure(
                warnResult.ErrorMessage ?? "Post-implementation warning ratchet failed.",
                loopBackStageId: LoopBackStageId);
        }

        return StageExecutionResult.Success();
    }
}
