namespace NightCodeForge.Core.Workflow.Stages;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow.Configuration;

/// <summary>
/// Executes a sequence of deterministic sub-stages (e.g. build, test, coverage, sast, ratchet)
/// resolved via IWorkflowStageRegistry and verified to be deterministic.
/// </summary>
public class CompositeDeterministicStage : IWorkflowStage
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public DeterministicStageConfig Config { get; }
    public string? LoopBackStageId => Config.LoopBackStageId;

    private readonly IServiceProvider _serviceProvider;
    private readonly IWorkflowStageRegistry _registry;

    public CompositeDeterministicStage(
        StageDefinition definition,
        IServiceProvider serviceProvider,
        IWorkflowStageRegistry? registry = null)
    {
        StageId = definition.StageId;
        DisplayName = definition.DisplayName;
        _serviceProvider = serviceProvider;
        _registry = registry ?? serviceProvider.GetService<IWorkflowStageRegistry>() ?? new WorkflowStageRegistry();

        Config = !string.IsNullOrWhiteSpace(definition.ConfigurationJson)
            ? JsonSerializer.Deserialize<DeterministicStageConfig>(definition.ConfigurationJson, JsonOptions) ?? new DeterministicStageConfig()
            : new DeterministicStageConfig();
    }

    public CompositeDeterministicStage(
        string stageId,
        string displayName,
        DeterministicStageConfig config,
        IServiceProvider serviceProvider,
        IWorkflowStageRegistry? registry = null)
    {
        StageId = stageId;
        DisplayName = displayName;
        Config = config;
        _serviceProvider = serviceProvider;
        _registry = registry ?? serviceProvider.GetService<IWorkflowStageRegistry>() ?? new WorkflowStageRegistry();
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var stagesCsv = Config.ExtraParameters.TryGetValue("Stages", out var s) ? s : "build,test";
        var stageKeys = stagesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var key in stageKeys)
        {
            var subStage = ResolveSubStage(key);

            var result = await subStage.ExecuteAsync(context, cancellationToken);
            if (!result.IsSuccess && Config.FailOnError)
            {
                return StageExecutionResult.Failure(
                    result.ErrorMessage ?? $"Composite sub-stage '{key}' failed.",
                    loopBackStageId: Config.LoopBackStageId,
                    record: result.ExecutionRecord);
            }
        }

        return StageExecutionResult.Success();
    }

    private IWorkflowStage ResolveSubStage(string stageName)
    {
        var normalized = stageName.Trim();
        var implementationType = normalized.StartsWith("deterministic-", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"deterministic-{normalized}";

        if (!_registry.TryGetFactory(implementationType, out var factory) || factory == null)
        {
            // Also try resolving directly by name if custom non-prefixed key was registered
            if (!_registry.TryGetFactory(normalized, out factory) || factory == null)
            {
                throw new InvalidOperationException(
                    $"Unknown deterministic sub-stage '{stageName}' in composite stage '{StageId}'. Available types: [{string.Join(", ", _registry.RegisteredTypes)}]");
            }
            implementationType = normalized;
        }

        var subDef = new StageDefinition
        {
            StageId = $"{StageId}-{stageName}",
            DisplayName = $"{DisplayName} ({stageName})",
            Kind = WorkflowStageKind.Deterministic,
            CsharpImplementationType = implementationType
        };

        var stage = factory(_serviceProvider, subDef);

        if (stage.Kind != WorkflowStageKind.Deterministic)
        {
            throw new InvalidOperationException(
                $"Composite stage '{StageId}' can only execute deterministic sub-stages, but '{stageName}' resolved to a stage of kind '{stage.Kind}'.");
        }

        return stage;
    }
}
