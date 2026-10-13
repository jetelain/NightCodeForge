using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Deterministic workflow stage executing static analysis and security scanning (e.g. Roslyn Analyzers, Semgrep, Trivy).
/// Produces finding reports without consuming GPU tokens.
/// </summary>
public class DeterministicSastStage : IWorkflowStage
{
    public const string DefaultStageId = "deterministic-sast";
    public const string FallbackLoopBackStageId = "implementation";

    private readonly IExecutionEnvironment? _executionEnv;
    private readonly string _sastCommand;

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public string LoopBackStageId { get; set; } = FallbackLoopBackStageId;

    public DeterministicSastStage(
        IExecutionEnvironment? executionEnv = null,
        string? sastCommand = null,
        string stageId = DefaultStageId,
        string displayName = "Deterministic SAST & Security Scan Gate")
    {
        _executionEnv = executionEnv;
        _sastCommand = sastCommand ?? "echo 'SAST check completed.'";
        StageId = stageId;
        DisplayName = displayName;
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var bag = context.DataBag;
        var config = CodeStackPresets.ResolveConfig(context.Workspace.RepositoryPath, bag);
        var command = config.SastCommand ?? _sastCommand;
        var workDir = context.Workspace.RepositoryPath;

        if (_executionEnv != null)
        {
            var res = await _executionEnv.ExecuteAsync(command, workDir, timeoutSeconds: 180, cancellationToken: cancellationToken);
            if (!res.Success)
            {
                var errorMsg = $"SAST analyzer failed with exit code {res.ExitCode}: {res.StandardError}";
                bag.Set("sast.failure.reason", errorMsg);
                var targetStage = bag.Get<string>("Stage.LoopBackId") ?? LoopBackStageId;
                return StageExecutionResult.Failure(errorMsg, targetStage);
            }
        }

        return StageExecutionResult.Success();
    }
}
