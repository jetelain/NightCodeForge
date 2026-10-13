using System.Text.RegularExpressions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Deterministic workflow stage executing project build (e.g. dotnet build) purely on CPU with 0 GPU tokens.
/// Validates compilation cleanliness, records warning counts into PipelineDataBag, and triggers JIT model preloading.
/// </summary>
public partial class DeterministicBuildStage : IWorkflowStage
{
    public const string DefaultStageId = "deterministic-build";
    public const string FallbackLoopBackStageId = "implementation";

    [GeneratedRegex(@"(?:warning\s+([A-Za-z0-9_-]+):\s*(.+?)\s*\[|\((\d+),(\d+)\):\s*warning\s*([A-Za-z0-9_-]+):)")]
    private static partial Regex WarningRegex();

    [GeneratedRegex(@"(?:error\s+([A-Za-z0-9_-]+):\s*(.+?)\s*\[|\((\d+),(\d+)\):\s*error\s*([A-Za-z0-9_-]+):)")]
    private static partial Regex ErrorRegex();

    private readonly IExecutionEnvironment? _executionEnv;
    private readonly IExecutionEnvironmentProvider? _envProvider;
    private readonly IOllamaModelPreloadService? _preloadService;
    private readonly string _buildCommand;

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public string LoopBackStageId { get; set; } = FallbackLoopBackStageId;

    public DeterministicBuildStage(
        IExecutionEnvironment? executionEnv = null,
        IOllamaModelPreloadService? preloadService = null,
        string? buildCommand = null,
        string stageId = DefaultStageId,
        string displayName = "Deterministic Build & Compilation Gate",
        IExecutionEnvironmentProvider? envProvider = null)
    {
        _executionEnv = executionEnv;
        _envProvider = envProvider;
        _preloadService = preloadService;
        _buildCommand = buildCommand ?? "dotnet build";
        StageId = stageId;
        DisplayName = displayName;
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var bag = context.DataBag;
        var config = CodeStackPresets.ResolveConfig(context.Workspace.RepositoryPath, bag);
        var command = config.BuildCommand ?? _buildCommand;
        var workDir = context.Workspace.RepositoryPath;

        // Trigger JIT model preloading asynchronously if next stage model is known
        if (_preloadService != null && context.CurrentProfile != null && !string.IsNullOrWhiteSpace(context.CurrentProfile.ModelName))
        {
            _ = _preloadService.PreloadModelAsync(context.CurrentProfile.ModelName, "15m", CancellationToken.None);
        }

        var effectiveEnv = (context.Workspace != null && _envProvider != null)
            ? _envProvider.GetEnvironment(context.Workspace)
            : _executionEnv;

        if (effectiveEnv != null)
        {
            var timeout = context.Workspace?.BuildTimeoutSeconds ?? config.BuildTimeoutSeconds ?? 120;
            bag.Set("build.timeout_seconds", timeout);

            var res = await effectiveEnv.ExecuteAsync(command, workDir, timeoutSeconds: timeout, cancellationToken: cancellationToken);

            var fullOutput = string.IsNullOrWhiteSpace(res.StandardError)
                ? res.StandardOutput
                : $"{res.StandardOutput}\n{res.StandardError}";

            // Count warnings
            var warnings = WarningRegex().Matches(fullOutput);
            var warningCount = warnings.Count;
            bag.Set("warnings.current.total", warningCount);

            if (!bag.TryGet<int>("warnings.baseline.total", out _))
            {
                bag.Set("warnings.baseline.total", warningCount);
            }

            if (!res.Success)
            {
                bool isTimeout = res.ExitCode == 124 ||
                                 res.Truncated && (res.StandardError.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                                                   res.StandardOutput.Contains("timed out", StringComparison.OrdinalIgnoreCase));

                if (isTimeout)
                {
                    var timeoutMsg = $"Deterministic build timed out after {timeout} seconds. Build failed to terminate within the configured limit.";
                    bag.Set("diagnostics.compiler_errors", new List<string> { timeoutMsg });
                    bag.Set("build.failure.reason", timeoutMsg);
                    var target = bag.Get<string>("Stage.LoopBackId") ?? LoopBackStageId;
                    return StageExecutionResult.Failure(timeoutMsg, target);
                }

                var errors = ErrorRegex().Matches(fullOutput);
                var errorList = errors.Select(m => m.Value.Trim()).Take(10).ToList();
                bag.Set("diagnostics.compiler_errors", errorList);

                var errorMsg = $"Deterministic build failed with exit code {res.ExitCode}. {errorList.Count} compiler error(s) detected.";
                bag.Set("build.failure.reason", errorMsg);
                var targetStage = bag.Get<string>("Stage.LoopBackId") ?? LoopBackStageId;
                return StageExecutionResult.Failure(errorMsg, targetStage);
            }
        }

        return StageExecutionResult.Success();
    }
}
