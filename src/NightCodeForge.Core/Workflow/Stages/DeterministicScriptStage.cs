namespace NightCodeForge.Core.Workflow.Stages;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow.Configuration;

/// <summary>
/// Deterministic stage executing an external script or shell command.
/// </summary>
public class DeterministicScriptStage : IWorkflowStage
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public DeterministicStageConfig Config { get; }
    public string? LoopBackStageId => Config.LoopBackStageId;

    private readonly IExecutionEnvironmentProvider _envProvider;

    public DeterministicScriptStage(
        StageDefinition definition,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        StageId = definition.StageId;
        DisplayName = definition.DisplayName;
        _envProvider = serviceProvider.GetRequiredService<IExecutionEnvironmentProvider>();

        Config = !string.IsNullOrWhiteSpace(definition.ConfigurationJson)
            ? JsonSerializer.Deserialize<DeterministicStageConfig>(definition.ConfigurationJson, JsonOptions) ?? new DeterministicStageConfig()
            : new DeterministicStageConfig();
    }

    public DeterministicScriptStage(
        string stageId,
        string displayName,
        DeterministicStageConfig config,
        IExecutionEnvironmentProvider envProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(config);
        _envProvider = envProvider ?? throw new ArgumentNullException(nameof(envProvider));

        StageId = stageId;
        DisplayName = displayName;
        Config = config;
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var command = Config.Command;
        if (string.IsNullOrWhiteSpace(command))
        {
            return StageExecutionResult.Success();
        }

        var workspace = context.Workspace ?? throw new InvalidOperationException("Workspace is required to resolve execution environment.");

        var workDir = !string.IsNullOrWhiteSpace(context.WorkingDirectory)
            ? context.WorkingDirectory
            : workspace.RepositoryPath ?? Directory.GetCurrentDirectory();

        var effectiveEnv = _envProvider.GetEnvironment(workspace, SandboxSecurityTier.Standard, context.ForgeTask?.Id)
            ?? throw new InvalidOperationException($"No execution environment could be resolved for workspace #{workspace.Id}.");

        var timeout = Config.TimeoutSeconds > 0 ? Config.TimeoutSeconds : 300;
        var res = await effectiveEnv.ExecuteAsync(command, workDir, timeoutSeconds: timeout, cancellationToken: cancellationToken);
        var fullOutput = string.IsNullOrWhiteSpace(res.StandardError)
            ? res.StandardOutput
            : $"{res.StandardOutput}\n{res.StandardError}";

        context.DataBag.Set(PipelineDataBagKeys.StageCommand(StageId), command);
        context.DataBag.Set(PipelineDataBagKeys.StageOutput(StageId), WorkflowDefaults.TruncateStageOutput(fullOutput));

        if (!res.Success && Config.FailOnError)
        {
            var errorOutput = !string.IsNullOrWhiteSpace(res.StandardError)
                ? res.StandardError
                : res.StandardOutput;
            return StageExecutionResult.Failure(
                $"Deterministic script stage '{StageId}' failed with exit code {res.ExitCode}: {errorOutput}".Trim(),
                loopBackStageId: Config.LoopBackStageId);
        }

        return StageExecutionResult.Success();
    }
}
