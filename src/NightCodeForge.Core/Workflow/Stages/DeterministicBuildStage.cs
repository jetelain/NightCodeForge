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
    public const string DefaultStageId = StageImplementationTypes.DeterministicBuild;
    public const string FallbackLoopBackStageId = StandardStageIds.Implementation;

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
        var repoPath = context.Workspace.RepositoryPath;
        var workDir = !string.IsNullOrWhiteSpace(context.WorkingDirectory)
            ? context.WorkingDirectory
            : (!string.IsNullOrWhiteSpace(context.Workspace.Ext4MirrorPath)
                ? context.Workspace.Ext4MirrorPath
                : repoPath);

        var config = CodeStackPresets.ResolveConfig(repoPath, bag);
        var command = config.BuildCommand ?? _buildCommand;

        // Trigger JIT model preloading asynchronously for the next agentic stage's model
        if (_preloadService != null)
        {
            string? nextModelName = null;
            if (context.Pipeline != null)
            {
                var nextStage = context.Pipeline.GetNextStage(StageId);
                if (nextStage == null)
                {
                    // Fallback for compound stages (e.g. pre-impl-build inside deterministic-pre-impl)
                    nextStage = context.Pipeline.Stages.FirstOrDefault(s => s.Kind == WorkflowStageKind.Agentic);
                }
                else
                {
                    while (nextStage != null && nextStage.Kind != WorkflowStageKind.Agentic)
                    {
                        nextStage = context.Pipeline.GetNextStage(nextStage.StageId);
                    }
                }

                if (nextStage != null)
                {
                    var nextProfile = context.Workspace?.AgentProfiles?.FirstOrDefault(p =>
                        string.Equals(p.Id, nextStage.StageId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.Name, nextStage.StageId, StringComparison.OrdinalIgnoreCase) ||
                        (p.StageDirectives != null && p.StageDirectives.ContainsKey(nextStage.StageId)))
                        ?? context.Workspace?.AgentProfiles?.FirstOrDefault();
                    nextModelName = nextProfile?.ModelName;
                }
            }

            if (string.IsNullOrWhiteSpace(nextModelName) && context.CurrentProfile != null)
            {
                nextModelName = context.CurrentProfile.ModelName;
            }

            if (!string.IsNullOrWhiteSpace(nextModelName))
            {
                _ = _preloadService.PreloadModelAsync(nextModelName, "15m", CancellationToken.None);
            }
        }

        var effectiveEnv = (context.Workspace != null && _envProvider != null)
            ? _envProvider.GetEnvironment(context.Workspace, SandboxSecurityTier.Standard, context.ForgeTask?.Id)
            : _executionEnv;

        if (effectiveEnv != null)
        {
            var timeout = context.Workspace?.BuildTimeoutSeconds ?? config.BuildTimeoutSeconds ?? WorkflowDefaults.DefaultBuildTimeoutSeconds;
            bag.Set(PipelineDataBagKeys.BuildTimeoutSeconds, timeout);

            var res = await effectiveEnv.ExecuteAsync(command, workDir, timeoutSeconds: timeout, cancellationToken: cancellationToken);

            var fullOutput = string.IsNullOrWhiteSpace(res.StandardError)
                ? res.StandardOutput
                : $"{res.StandardOutput}\n{res.StandardError}";

            // Count warnings
            var warnings = WarningRegex().Matches(fullOutput);
            var warningCount = warnings.Count;
            bag.Set(PipelineDataBagKeys.WarningsCurrentTotal, warningCount);

            if (!bag.TryGet<int>(PipelineDataBagKeys.WarningsBaselineTotal, out _))
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
        }

        return StageExecutionResult.Success();
    }
}
