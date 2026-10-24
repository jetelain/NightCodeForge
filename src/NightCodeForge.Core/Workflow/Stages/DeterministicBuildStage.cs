using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Deterministic workflow stage executing project build (e.g. dotnet build) purely on CPU with 0 GPU tokens.
/// Validates compilation cleanliness, records warning counts into PipelineDataBag, and triggers JIT model preloading.
/// </summary>
public partial class DeterministicBuildStage : IWorkflowStage
{
    public const string DefaultStageId = StageImplementationTypes.DeterministicBuild;
    public const string FallbackLoopBackStageId = StandardStageIds.Implementation;

    [GeneratedRegex(@"(?:warning\s+([A-Za-z0-9_-]+):\s*(.+?)\s*\[|\((\d+),(\d+)\):\s*warning\s*([A-Za-z0-9_-]+):)")]
    private static partial Regex WarningRegex();

    [GeneratedRegex(@"(?:error\s+([A-Za-z0-9_-]+):\s*(.+?)\s*\[|\((\d+),(\d+)\):\s*error\s*([A-Za-z0-9_-]+):)")]
    private static partial Regex ErrorRegex();

    private readonly IExecutionEnvironmentProvider _envProvider;
    private readonly IOllamaModelPreloadService? _preloadService;
    private readonly IBackgroundTaskRunner _backgroundTaskRunner;
    private readonly ILogger<DeterministicBuildStage> _logger;
    private readonly string _buildCommand;

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public StageQueueTier QueueTier { get; set; } = StageQueueTier.Implementation;
    public string LoopBackStageId { get; set; } = FallbackLoopBackStageId;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public DeterministicBuildStage(
        IExecutionEnvironmentProvider envProvider,
        IOllamaModelPreloadService? preloadService,
        IBackgroundTaskRunner backgroundTaskRunner,
        ILogger<DeterministicBuildStage> logger)
        : this(envProvider, preloadService, backgroundTaskRunner, logger, "dotnet build", DefaultStageId, "Deterministic Build & Compilation Gate")
    {
    }

    public DeterministicBuildStage(
        IExecutionEnvironmentProvider envProvider,
        IOllamaModelPreloadService? preloadService,
        IBackgroundTaskRunner backgroundTaskRunner,
        ILogger<DeterministicBuildStage> logger,
        string buildCommand,
        string stageId,
        string displayName)
    {
        _envProvider = envProvider ?? throw new ArgumentNullException(nameof(envProvider));
        _preloadService = preloadService;
        _backgroundTaskRunner = backgroundTaskRunner ?? throw new ArgumentNullException(nameof(backgroundTaskRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _buildCommand = !string.IsNullOrWhiteSpace(buildCommand) ? buildCommand : "dotnet build";
        StageId = !string.IsNullOrWhiteSpace(stageId) ? stageId : DefaultStageId;
        DisplayName = !string.IsNullOrWhiteSpace(displayName) ? displayName : "Deterministic Build & Compilation Gate";
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var workspace = context.Workspace ?? throw new InvalidOperationException("Workspace is required to resolve execution environment.");

        var bag = context.DataBag;

        // Reset previous build failure metrics so stale errors do not carry over
        bag.Remove(PipelineDataBagKeys.CompilerErrors);
        bag.Remove(PipelineDataBagKeys.BuildFailureReason);
        bag.Remove(PipelineDataBagKeys.WarningsCurrentTotal);
        bag.Remove(PipelineDataBagKeys.WarningsNew);

        var repoPath = workspace.RepositoryPath;
        var workDir = !string.IsNullOrWhiteSpace(context.WorkingDirectory)
            ? context.WorkingDirectory
            : (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)
                ? workspace.Ext4MirrorPath
                : repoPath);

        var config = CodeStackPresets.ResolveConfig(repoPath, bag);
        var command = config.BuildCommand ?? _buildCommand;

        // Trigger JIT model preloading asynchronously for the next agentic stage's model
        if (_preloadService != null)
        {
            string? nextModelName = null;
            if (context.Pipeline != null)
            {
                // Find matching pipeline stage: exact match or prefix of composite sub-stage
                var currentPipelineStage = context.Pipeline.Stages.FirstOrDefault(s =>
                    string.Equals(s.StageId, StageId, StringComparison.OrdinalIgnoreCase) ||
                    StageId.StartsWith($"{s.StageId}-", StringComparison.OrdinalIgnoreCase));

                var nextStage = currentPipelineStage != null ? context.Pipeline.GetNextStage(currentPipelineStage.StageId) : null;
                while (nextStage != null && nextStage.Kind != WorkflowStageKind.Agentic)
                {
                    nextStage = context.Pipeline.GetNextStage(nextStage.StageId);
                }

                if (nextStage != null)
                {
                    var nextProfile = context.Workspace?.AgentProfiles?.FirstOrDefault(p =>
                        string.Equals(p.Id, nextStage.StageId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.Name, nextStage.StageId, StringComparison.OrdinalIgnoreCase) ||
                        (p.StageDirectives != null && p.StageDirectives.ContainsKey(nextStage.StageId)));

                    if (nextProfile != null)
                    {
                        nextModelName = nextProfile.ModelName;
                    }
                    else
                    {
                        _logger.LogInformation("No matching agent profile found for next stage '{StageId}'. Skipping JIT model preload.", nextStage.StageId);
                    }
                }
                else
                {
                    _logger.LogInformation("No subsequent agentic stage found after '{StageId}'. Skipping JIT model preload.", StageId);
                }
            }

            if (string.IsNullOrWhiteSpace(nextModelName) && context.CurrentProfile != null)
            {
                nextModelName = context.CurrentProfile.ModelName;
            }

            if (!string.IsNullOrWhiteSpace(nextModelName))
            {
                var modelToPreload = nextModelName;
                _backgroundTaskRunner.RunTracked(
                    async ct => await _preloadService.PreloadModelAsync(modelToPreload, WorkflowDefaults.ModelPreloadKeepAlive, ct).ConfigureAwait(false),
                    $"PreloadModel.{modelToPreload}",
                    cancellationToken);
            }
        }

        var effectiveEnv = _envProvider.GetEnvironment(workspace, SandboxSecurityTier.Standard, context.ForgeTask?.Id)
            ?? throw new InvalidOperationException($"No execution environment could be resolved for workspace #{workspace.Id}.");

        var timeout = workspace.BuildTimeoutSeconds ?? config.BuildTimeoutSeconds ?? WorkflowDefaults.DefaultBuildTimeoutSeconds;
        bag.Set(PipelineDataBagKeys.BuildTimeoutSeconds, timeout);

        var res = await effectiveEnv.ExecuteAsync(command, workDir, timeoutSeconds: timeout, cancellationToken: cancellationToken);

        var fullOutput = string.IsNullOrWhiteSpace(res.StandardError)
            ? res.StandardOutput
            : $"{res.StandardOutput}\n{res.StandardError}";

        bag.Set(PipelineDataBagKeys.StageCommand(StageId), command);
        bag.Set(PipelineDataBagKeys.StageOutput(StageId), WorkflowDefaults.TruncateStageOutput(fullOutput));

        // Count warnings
        var warnings = WarningRegex().Matches(fullOutput);
        var warningCount = warnings.Count;
        bag.Set(PipelineDataBagKeys.WarningsCurrentTotal, warningCount);

        var warningLines = warnings.Select(w => w.Value.Trim()).Distinct().Take(20).ToList();
        bag.Set(PipelineDataBagKeys.WarningsNew, warningLines);

        if (res.Success && !bag.TryGet<int>(PipelineDataBagKeys.WarningsBaselineTotal, out _))
        {
            bag.Set(PipelineDataBagKeys.WarningsBaselineTotal, warningCount);
        }

        if (!res.Success)
        {
            bool isTimeout = res.ExitCode == WorkflowDefaults.ProcessTimeoutExitCode ||
                             res.Truncated && (res.StandardError.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                                               res.StandardOutput.Contains("timed out", StringComparison.OrdinalIgnoreCase));

            if (isTimeout)
            {
                var timeoutMsg = $"Deterministic build timed out after {timeout} seconds. Build failed to terminate within the configured limit.";
                bag.Set(PipelineDataBagKeys.CompilerErrors, new List<string> { timeoutMsg });
                bag.Set(PipelineDataBagKeys.BuildFailureReason, timeoutMsg);
                var target = bag.Get<string>(PipelineDataBagKeys.LoopBackId) ?? LoopBackStageId;
                return StageExecutionResult.Failure(timeoutMsg, target);
            }

            var errors = ErrorRegex().Matches(fullOutput);
            var errorList = errors.Select(m => m.Value.Trim()).Take(10).ToList();
            bag.Set(PipelineDataBagKeys.CompilerErrors, errorList);

            var errorMsg = errorList.Count > 0
                ? $"Deterministic build failed with exit code {res.ExitCode}. {errorList.Count} compiler error(s) detected."
                : $"Deterministic build failed with exit code {res.ExitCode}. Output: {fullOutput.Trim()}";
            bag.Set(PipelineDataBagKeys.BuildFailureReason, errorMsg);
            var targetStage = bag.Get<string>(PipelineDataBagKeys.LoopBackId) ?? LoopBackStageId;
            return StageExecutionResult.Failure(errorMsg, targetStage);
        }

        bag.Remove(PipelineDataBagKeys.CompilerErrors);
        bag.Remove(PipelineDataBagKeys.BuildFailureReason);
        return StageExecutionResult.Success();
    }
}
