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

    private readonly IExecutionEnvironment? _executionEnv;
    private readonly IExecutionEnvironmentProvider? _envProvider;

    public DeterministicScriptStage(
        StageDefinition definition,
        IServiceProvider serviceProvider)
    {
        StageId = definition.StageId;
        DisplayName = definition.DisplayName;
        _executionEnv = serviceProvider.GetService<IExecutionEnvironment>();
        _envProvider = serviceProvider.GetService<IExecutionEnvironmentProvider>();

        Config = !string.IsNullOrWhiteSpace(definition.ConfigurationJson)
            ? JsonSerializer.Deserialize<DeterministicStageConfig>(definition.ConfigurationJson, JsonOptions) ?? new DeterministicStageConfig()
            : new DeterministicStageConfig();
    }

    public DeterministicScriptStage(
        string stageId,
        string displayName,
        DeterministicStageConfig config,
        IExecutionEnvironment? executionEnv = null,
        IExecutionEnvironmentProvider? envProvider = null)
    {
        StageId = stageId;
        DisplayName = displayName;
        Config = config;
        _executionEnv = executionEnv;
        _envProvider = envProvider;
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var command = Config.Command;
        if (string.IsNullOrWhiteSpace(command))
        {
            return StageExecutionResult.Success();
        }

        var workDir = !string.IsNullOrWhiteSpace(context.WorkingDirectory)
            ? context.WorkingDirectory
            : context.Workspace?.RepositoryPath ?? Directory.GetCurrentDirectory();

        var effectiveEnv = (context.Workspace != null && _envProvider != null)
            ? _envProvider.GetEnvironment(context.Workspace, SandboxSecurityTier.Standard, context.ForgeTask?.Id)
            : _executionEnv;

        if (effectiveEnv != null)
        {
            var timeout = Config.TimeoutSeconds > 0 ? Config.TimeoutSeconds : 300;
            var res = await effectiveEnv.ExecuteAsync(command, workDir, timeoutSeconds: timeout, cancellationToken: cancellationToken);
            if (!res.Success && Config.FailOnError)
            {
                var errorOutput = !string.IsNullOrWhiteSpace(res.StandardError)
                    ? res.StandardError
                    : res.StandardOutput;
                return StageExecutionResult.Failure(
                    $"Deterministic script stage '{StageId}' failed with exit code {res.ExitCode}: {errorOutput}".Trim(),
                    loopBackStageId: Config.LoopBackStageId);
            }
        }

        return StageExecutionResult.Success();
    }
}
