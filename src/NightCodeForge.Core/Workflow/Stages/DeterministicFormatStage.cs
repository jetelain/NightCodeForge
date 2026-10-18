namespace NightCodeForge.Core.Workflow.Stages;

using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

/// <summary>
/// Deterministic workflow stage applying code formatting (e.g. dotnet format, ruff format, cargo fmt).
/// Directly fixes code style and formatting so that the agent and reviewer do not need to perform mechanical reformatting.
/// </summary>
public class DeterministicFormatStage : IWorkflowStage
{
    public const string DefaultStageId = StageImplementationTypes.DeterministicFormat;
    public const string FallbackLoopBackStageId = StandardStageIds.Implementation;

    private readonly IExecutionEnvironmentProvider _envProvider;
    private readonly ILogger<DeterministicFormatStage> _logger;
    private readonly string? _formatCommand;

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public string LoopBackStageId { get; set; } = FallbackLoopBackStageId;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public DeterministicFormatStage(
        IExecutionEnvironmentProvider envProvider,
        ILogger<DeterministicFormatStage> logger)
        : this(envProvider, logger, null, DefaultStageId, "Deterministic Code Formatting")
    {
    }

    public DeterministicFormatStage(IExecutionEnvironmentProvider envProvider)
        : this(envProvider, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeterministicFormatStage>.Instance, null, DefaultStageId, "Deterministic Code Formatting")
    {
    }

    public DeterministicFormatStage(
        IExecutionEnvironmentProvider envProvider,
        string? formatCommand,
        string stageId,
        string displayName)
        : this(envProvider, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeterministicFormatStage>.Instance, formatCommand, stageId, displayName)
    {
    }

    public DeterministicFormatStage(
        IExecutionEnvironmentProvider envProvider,
        ILogger<DeterministicFormatStage> logger,
        string? formatCommand,
        string stageId,
        string displayName)
    {
        _envProvider = envProvider ?? throw new ArgumentNullException(nameof(envProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _formatCommand = formatCommand;
        StageId = !string.IsNullOrWhiteSpace(stageId) ? stageId : DefaultStageId;
        DisplayName = !string.IsNullOrWhiteSpace(displayName) ? displayName : "Deterministic Code Formatting";
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var workspace = context.Workspace ?? throw new InvalidOperationException("Workspace is required to resolve execution environment.");

        var bag = context.DataBag;
        bag.Remove(PipelineDataBagKeys.FormatFailureReason);

        var repoPath = workspace.RepositoryPath;
        var workDir = !string.IsNullOrWhiteSpace(context.WorkingDirectory)
            ? context.WorkingDirectory
            : (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)
                ? workspace.Ext4MirrorPath
                : repoPath);

        var config = CodeStackPresets.ResolveConfig(repoPath, bag);
        var command = _formatCommand ?? config.FormatCommand;

        if (string.IsNullOrWhiteSpace(command))
        {
            var skipMsg = "Deterministic code formatting step skipped: no format command configured for workspace stack.";
            _logger.LogInformation("{Message}", skipMsg);
            bag.Set(PipelineDataBagKeys.StageCommand(StageId), "format (skipped)");
            bag.Set(PipelineDataBagKeys.StageOutput(StageId), skipMsg);
            return StageExecutionResult.Success();
        }

        var effectiveEnv = _envProvider.GetEnvironment(workspace, SandboxSecurityTier.Standard, context.ForgeTask?.Id)
            ?? throw new InvalidOperationException($"No execution environment could be resolved for workspace #{workspace.Id}.");

        var timeout = WorkflowDefaults.DefaultFormatTimeoutSeconds;
        var res = await effectiveEnv.ExecuteAsync(command, workDir, timeoutSeconds: timeout, cancellationToken: cancellationToken);
        var fullOutput = string.IsNullOrWhiteSpace(res.StandardError)
            ? res.StandardOutput
            : $"{res.StandardOutput}\n{res.StandardError}";

        bag.Set(PipelineDataBagKeys.StageCommand(StageId), command);
        bag.Set(PipelineDataBagKeys.StageOutput(StageId), WorkflowDefaults.TruncateStageOutput(fullOutput));

        if (!res.Success)
        {
            var errorDetail = !string.IsNullOrWhiteSpace(res.StandardError) ? res.StandardError : res.StandardOutput;

            // If the formatting command fails due to missing runner tool or script, treat as non-blocking warning
            if (errorDetail.Contains("Missing script", StringComparison.OrdinalIgnoreCase) ||
                errorDetail.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
                errorDetail.Contains("not recognized as an internal or external command", StringComparison.OrdinalIgnoreCase))
            {
                var skipMsg = $"Deterministic code formatting command '{command}' is not available in workspace environment; skipped: {errorDetail}".Trim();
                _logger.LogWarning("{Message}", skipMsg);
                bag.Set(PipelineDataBagKeys.StageCommand(StageId), $"{command} (unavailable)");
                bag.Set(PipelineDataBagKeys.StageOutput(StageId), skipMsg);
                return StageExecutionResult.Success();
            }

            var errorMsg = $"Code formatter failed with exit code {res.ExitCode}: {errorDetail}".Trim();
            bag.Set(PipelineDataBagKeys.FormatFailureReason, errorMsg);
            var targetStage = bag.Get<string>(PipelineDataBagKeys.LoopBackId) ?? LoopBackStageId;
            return StageExecutionResult.Failure(errorMsg, targetStage);
        }

        bag.Remove(PipelineDataBagKeys.FormatFailureReason);
        return StageExecutionResult.Success();
    }
}
