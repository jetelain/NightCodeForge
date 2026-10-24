namespace NightCodeForge.Core.Workflow.Stages;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Streaming;
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
    public StageQueueTier QueueTier { get; set; } = StageQueueTier.Implementation;
    public DeterministicStageConfig Config { get; }
    public string? LoopBackStageId => Config.LoopBackStageId;

    private readonly IServiceProvider _serviceProvider;
    private readonly IWorkflowStageRegistry _registry;
    private readonly IAgentActivityStreamer _streamer;
    private readonly ITaskRepository _taskRepository;
    private readonly ILogger<CompositeDeterministicStage> _logger;

    public CompositeDeterministicStage(
        StageDefinition definition,
        IServiceProvider serviceProvider,
        IWorkflowStageRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(definition);
        StageId = definition.StageId;
        DisplayName = definition.DisplayName;
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _streamer = serviceProvider.GetRequiredService<IAgentActivityStreamer>();
        _taskRepository = serviceProvider.GetRequiredService<ITaskRepository>();
        _logger = serviceProvider.GetRequiredService<ILogger<CompositeDeterministicStage>>();

        Config = !string.IsNullOrWhiteSpace(definition.ConfigurationJson)
            ? JsonSerializer.Deserialize<DeterministicStageConfig>(definition.ConfigurationJson, JsonOptions) ?? new DeterministicStageConfig()
            : new DeterministicStageConfig();
    }

    public CompositeDeterministicStage(
        string stageId,
        string displayName,
        DeterministicStageConfig config,
        IServiceProvider serviceProvider,
        IWorkflowStageRegistry registry)
    {
        StageId = stageId;
        DisplayName = displayName;
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _streamer = serviceProvider.GetRequiredService<IAgentActivityStreamer>();
        _taskRepository = serviceProvider.GetRequiredService<ITaskRepository>();
        _logger = serviceProvider.GetRequiredService<ILogger<CompositeDeterministicStage>>();
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var stagesCsv = Config.ExtraParameters.TryGetValue("Stages", out var s) ? s : "build,test";
        var stageKeys = stagesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var commandList = new List<StageCommandExecution>();

        context.DataBag.Remove(PipelineDataBagKeys.StageCommands(StageId));

        var convId = context.CurrentConversation?.Id;

        foreach (var key in stageKeys)
        {
            var subStage = ResolveSubStage(key);
            var toolStageName = !string.IsNullOrWhiteSpace(key) ? $"{StageId}-{key}" : StageId;
            var initialCmd = ResolveInitialCommand(subStage, key, context);

            try
            {
                await _streamer.BroadcastAsync(new AgentActivityEvent(
                    Guid.NewGuid().ToString("N"),
                    context.ForgeTask.Id,
                    StageId,
                    AgentActivityKind.ToolStarted,
                    $"{toolStageName}: {initialCmd}",
                    DateTime.UtcNow,
                    new Dictionary<string, object?>
                    {
                        [AgentActivityMetadataKeys.ToolName] = toolStageName
                    }), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed broadcasting ToolStarted for {ToolName} in task #{TaskId}", toolStageName, context.ForgeTask.Id);
            }

            if (convId.HasValue)
            {
                try
                {
                    await _taskRepository.AddConversationTurnAsync(
                        convId.Value,
                        StageTurnRole.Tool,
                        content: $"Execute {initialCmd}",
                        toolName: toolStageName,
                        toolArguments: initialCmd,
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed recording Tool turn for {ToolName} in conversation #{ConvId}", toolStageName, convId.Value);
                }
            }

            try
            {
                await _taskRepository.AddExecutionLogAsync(new ExecutionLog
                {
                    TaskId = context.ForgeTask.Id,
                    Level = LogLevel.Information,
                    Message = $"[Deterministic] Running sub-stage '{key}' ({initialCmd})",
                    Timestamp = DateTime.UtcNow
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed recording execution log for {SubStage} in task #{TaskId}", key, context.ForgeTask.Id);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await subStage.ExecuteAsync(context, cancellationToken);
            sw.Stop();

            var actualCmd = context.DataBag.Get<string>(PipelineDataBagKeys.StageCommand(subStage.StageId)) ?? initialCmd;
            var subOut = context.DataBag.Get<string>(PipelineDataBagKeys.StageOutput(subStage.StageId))
                         ?? (result.IsSuccess ? "Verification completed successfully." : result.ErrorMessage ?? "Deterministic step failed.");

            commandList.Add(new StageCommandExecution(actualCmd, subOut, key, result.IsSuccess, sw.ElapsedMilliseconds));

            if (convId.HasValue)
            {
                try
                {
                    await _taskRepository.AddConversationTurnAsync(
                        convId.Value,
                        StageTurnRole.ToolResult,
                        content: subOut,
                        toolName: toolStageName,
                        toolArguments: actualCmd,
                        toolResult: subOut,
                        durationMs: (int)sw.ElapsedMilliseconds,
                        isError: !result.IsSuccess,
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed recording ToolResult turn for {ToolName} in conversation #{ConvId}", toolStageName, convId.Value);
                }
            }

            try
            {
                await _streamer.BroadcastAsync(new AgentActivityEvent(
                    Guid.NewGuid().ToString("N"),
                    context.ForgeTask.Id,
                    StageId,
                    AgentActivityKind.ToolCompleted,
                    $"{toolStageName}: {subOut}",
                    DateTime.UtcNow,
                    new Dictionary<string, object?>
                    {
                        [AgentActivityMetadataKeys.ToolName] = toolStageName,
                        [AgentActivityMetadataKeys.DurationMs] = (int)sw.ElapsedMilliseconds
                    }), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed broadcasting ToolCompleted for {ToolName} in task #{TaskId}", toolStageName, context.ForgeTask.Id);
            }

            if (!result.IsSuccess && Config.FailOnError)
            {
                context.DataBag.Set(PipelineDataBagKeys.StageCommands(StageId), commandList);
                var failCmds = string.Join(" && ", commandList.Select(c => c.Command));
                var failOutputs = string.Join("\n---\n", commandList.Select(c => $"[{c.StageName}] {c.Output}"));
                context.DataBag.Set(PipelineDataBagKeys.StageCommand(StageId), failCmds);
                context.DataBag.Set(PipelineDataBagKeys.StageOutput(StageId), WorkflowDefaults.TruncateStageOutput(failOutputs));

                return StageExecutionResult.Failure(
                    result.ErrorMessage ?? $"Composite sub-stage '{key}' failed.",
                    loopBackStageId: Config.LoopBackStageId,
                    record: result.ExecutionRecord);
            }
        }

        context.DataBag.Set(PipelineDataBagKeys.StageCommands(StageId), commandList);
        var combinedCmds = string.Join(" && ", commandList.Select(c => c.Command));
        var combinedOutputs = string.Join("\n---\n", commandList.Select(c => $"[{c.StageName}] {c.Output}"));
        context.DataBag.Set(PipelineDataBagKeys.StageCommand(StageId), combinedCmds);
        context.DataBag.Set(PipelineDataBagKeys.StageOutput(StageId), WorkflowDefaults.TruncateStageOutput(combinedOutputs));

        return StageExecutionResult.Success();
    }

    private static string ResolveInitialCommand(IWorkflowStage subStage, string key, PipelineContext context)
    {
        var repoPath = context.Workspace?.RepositoryPath;
        var bag = context.DataBag;
        var config = !string.IsNullOrWhiteSpace(repoPath) ? CodeStackPresets.ResolveConfig(repoPath, bag) : null;

        return key.Trim().ToLowerInvariant() switch
        {
            DeterministicStageConstants.BuildSubStage => config?.BuildCommand ?? DeterministicStageConstants.DefaultBuildCommand,
            DeterministicStageConstants.TestSubStage => config?.TestCommand ?? DeterministicStageConstants.DefaultTestCommand,
            DeterministicStageConstants.TestWithCoverageSubStage => bag.Get<string>(PipelineDataBagKeys.CoverageCommand) ?? config?.CoverageCommand ?? WorkflowDefaults.DefaultCoverageTestCommand,
            DeterministicStageConstants.FormatSubStage => config?.FormatCommand ?? DeterministicStageConstants.DefaultFormatCommand,
            DeterministicStageConstants.SastSubStage => config?.SastCommand ?? DeterministicStageConstants.DefaultSastCommand,
            _ => bag.Get<string>(PipelineDataBagKeys.StageCommand(subStage.StageId)) ?? subStage.DisplayName
        };
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
