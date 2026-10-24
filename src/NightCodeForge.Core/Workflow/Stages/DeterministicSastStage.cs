using Microsoft.Extensions.Logging;
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
    private readonly ILogger<DeterministicSastStage> _logger;
    private readonly string? _sastCommand;

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public StageQueueTier QueueTier { get; set; } = StageQueueTier.Implementation;
    public string LoopBackStageId { get; set; } = FallbackLoopBackStageId;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public DeterministicSastStage(
        IExecutionEnvironmentProvider envProvider,
        ILogger<DeterministicSastStage> logger)
        : this(envProvider, logger, null, DefaultStageId, "Deterministic SAST & Security Scan Gate")
    {
    }

    public DeterministicSastStage(IExecutionEnvironmentProvider envProvider)
        : this(envProvider, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeterministicSastStage>.Instance, null, DefaultStageId, "Deterministic SAST & Security Scan Gate")
    {
    }

    public DeterministicSastStage(
        IExecutionEnvironmentProvider envProvider,
        string? sastCommand,
        string stageId,
        string displayName)
        : this(envProvider, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeterministicSastStage>.Instance, sastCommand, stageId, displayName)
    {
    }

    public DeterministicSastStage(
        IExecutionEnvironmentProvider envProvider,
        ILogger<DeterministicSastStage> logger,
        string? sastCommand,
        string stageId,
        string displayName)
    {
        _envProvider = envProvider ?? throw new ArgumentNullException(nameof(envProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sastCommand = sastCommand;
        StageId = !string.IsNullOrWhiteSpace(stageId) ? stageId : DefaultStageId;
        DisplayName = !string.IsNullOrWhiteSpace(displayName) ? displayName : "Deterministic SAST & Security Scan Gate";
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var workspace = context.Workspace ?? throw new InvalidOperationException("Workspace is required to resolve execution environment.");

        var bag = context.DataBag;
        bag.Remove(PipelineDataBagKeys.SastFailureReason);

        var repoPath = workspace.RepositoryPath;
        var workDir = !string.IsNullOrWhiteSpace(context.WorkingDirectory)
            ? context.WorkingDirectory
            : (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)
                ? workspace.Ext4MirrorPath
                : repoPath);

        var config = CodeStackPresets.ResolveConfig(repoPath, bag);
        var command = _sastCommand ?? config.SastCommand;

        if (string.IsNullOrWhiteSpace(command))
        {
            var infoMsg = "Deterministic SAST gate skipped: no SAST analyzer command configured (extension point for SonarQube, Semgrep, Trivy, etc.).";
            _logger.LogInformation("{Message}", infoMsg);
            bag.Set(PipelineDataBagKeys.StageCommand(StageId), "sast (skipped)");
            bag.Set(PipelineDataBagKeys.StageOutput(StageId), infoMsg);
            return StageExecutionResult.Success();
        }

        var effectiveEnv = _envProvider.GetEnvironment(workspace, SandboxSecurityTier.Standard, context.ForgeTask?.Id)
            ?? throw new InvalidOperationException($"No execution environment could be resolved for workspace #{workspace.Id}.");

        var timeout = WorkflowDefaults.DefaultSastTimeoutSeconds;
        var res = await effectiveEnv.ExecuteAsync(command, workDir, timeoutSeconds: timeout, cancellationToken: cancellationToken);
        var fullOutput = string.IsNullOrWhiteSpace(res.StandardError)
            ? res.StandardOutput
            : $"{res.StandardOutput}\n{res.StandardError}";

        bag.Set(PipelineDataBagKeys.StageCommand(StageId), command);
        bag.Set(PipelineDataBagKeys.StageOutput(StageId), WorkflowDefaults.TruncateStageOutput(fullOutput));

        if (!res.Success)
        {
            var errorDetail = !string.IsNullOrWhiteSpace(res.StandardError) ? res.StandardError : res.StandardOutput;
            var errorMsg = $"SAST analyzer failed with exit code {res.ExitCode}: {errorDetail}".Trim();
            bag.Set(PipelineDataBagKeys.SastFailureReason, errorMsg);
            var targetStage = bag.Get<string>(PipelineDataBagKeys.LoopBackId) ?? LoopBackStageId;
            return StageExecutionResult.Failure(errorMsg, targetStage);
        }

        bag.Remove(PipelineDataBagKeys.SastFailureReason);
        return StageExecutionResult.Success();
    }
}
