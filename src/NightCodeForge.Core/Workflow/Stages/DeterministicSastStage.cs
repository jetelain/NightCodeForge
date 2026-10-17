using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Deterministic workflow stage executing static analysis and security scanning (e.g. Roslyn Analyzers, Semgrep, Trivy).
/// Produces finding reports without consuming GPU tokens.
/// </summary>
public class DeterministicSastStage : IWorkflowStage
{
    public const string DefaultStageId = StageImplementationTypes.DeterministicSast;
    public const string FallbackLoopBackStageId = StandardStageIds.Implementation;

    private readonly IExecutionEnvironmentProvider _envProvider;
    private readonly string _sastCommand;

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public string LoopBackStageId { get; set; } = FallbackLoopBackStageId;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public DeterministicSastStage(IExecutionEnvironmentProvider envProvider)
        : this(envProvider, "echo 'SAST check completed.'", DefaultStageId, "Deterministic SAST & Security Scan Gate")
    {
    }

    public DeterministicSastStage(
        IExecutionEnvironmentProvider envProvider,
        string sastCommand,
        string stageId,
        string displayName)
    {
        _envProvider = envProvider ?? throw new ArgumentNullException(nameof(envProvider));
        _sastCommand = !string.IsNullOrWhiteSpace(sastCommand) ? sastCommand : "echo 'SAST check completed.'";
        StageId = !string.IsNullOrWhiteSpace(stageId) ? stageId : DefaultStageId;
        DisplayName = !string.IsNullOrWhiteSpace(displayName) ? displayName : "Deterministic SAST & Security Scan Gate";
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var workspace = context.Workspace ?? throw new InvalidOperationException("Workspace is required to resolve execution environment.");

        var bag = context.DataBag;
        var repoPath = workspace.RepositoryPath;
        var workDir = !string.IsNullOrWhiteSpace(context.WorkingDirectory)
            ? context.WorkingDirectory
            : (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)
                ? workspace.Ext4MirrorPath
                : repoPath);

        var config = CodeStackPresets.ResolveConfig(repoPath, bag);
        var command = config.SastCommand ?? _sastCommand;

        var effectiveEnv = _envProvider.GetEnvironment(workspace, SandboxSecurityTier.Standard, context.ForgeTask?.Id)
            ?? throw new InvalidOperationException($"No execution environment could be resolved for workspace #{workspace.Id}.");

        var res = await effectiveEnv.ExecuteAsync(command, workDir, timeoutSeconds: 180, cancellationToken: cancellationToken);
        var fullOutput = string.IsNullOrWhiteSpace(res.StandardError)
            ? res.StandardOutput
            : $"{res.StandardOutput}\n{res.StandardError}";

        bag.Set(PipelineDataBagKeys.StageCommand(StageId), command);
        bag.Set(PipelineDataBagKeys.StageOutput(StageId), fullOutput);

        if (!res.Success)
        {
            var errorMsg = $"SAST analyzer failed with exit code {res.ExitCode}: {res.StandardError}";
            bag.Set(PipelineDataBagKeys.SastFailureReason, errorMsg);
            var targetStage = bag.Get<string>(PipelineDataBagKeys.LoopBackId) ?? LoopBackStageId;
            return StageExecutionResult.Failure(errorMsg, targetStage);
        }

        return StageExecutionResult.Success();
    }
}
