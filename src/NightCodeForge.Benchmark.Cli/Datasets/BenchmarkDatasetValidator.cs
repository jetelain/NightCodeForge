using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Benchmark.Cli.Verification;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Benchmark.Cli.Datasets;

/// <summary>
/// Performs deterministic pre-flight verification of benchmark datasets across Implementation,
/// Code Review, and ForgeTask Review categories using ephemeral workspaces and containerized execution.
/// </summary>
public sealed class BenchmarkDatasetValidator : IBenchmarkDatasetValidator
{
    private readonly IBenchmarkDatasetLoader _datasetLoader;
    private readonly IBenchmarkWorkspaceManager _workspaceManager;
    private readonly IBenchmarkVerifier _verifier;
    private readonly IBenchmarkToolchainPreflightValidator _toolchainPreflightValidator;
    private readonly ILogger<BenchmarkDatasetValidator> _logger;

    public BenchmarkDatasetValidator(
        IBenchmarkDatasetLoader datasetLoader,
        IBenchmarkWorkspaceManager workspaceManager,
        IBenchmarkVerifier verifier,
        IBenchmarkToolchainPreflightValidator toolchainPreflightValidator,
        ILogger<BenchmarkDatasetValidator> logger)
    {
        _datasetLoader = datasetLoader ?? throw new ArgumentNullException(nameof(datasetLoader));
        _workspaceManager = workspaceManager ?? throw new ArgumentNullException(nameof(workspaceManager));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _toolchainPreflightValidator = toolchainPreflightValidator ?? throw new ArgumentNullException(nameof(toolchainPreflightValidator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BenchmarkDatasetValidationReport> ValidateAsync(
        string datasetPath,
        BenchmarkProblemCategory? categoryOverride,
        IReadOnlyList<string>? instanceIdFilter,
        int? limit,
        bool verifyWithTests,
        Action<string>? progressCallback,
        string? manifestPath = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is <= 0)
        {
            throw new BenchmarkDatasetException("Validation limit must be greater than zero when specified.", datasetPath);
        }

        var allInstances = await _datasetLoader.LoadAsync(
            datasetPath,
            categoryOverride,
            manifestPath,
            msg =>
            {
                _logger.LogInformation("{Diagnostic}", msg);
                progressCallback?.Invoke(msg);
            },
            cancellationToken).ConfigureAwait(false);
        var selected = FilterInstances(allInstances, datasetPath, instanceIdFilter, limit);

        var results = new List<BenchmarkInstanceValidationResult>(selected.Count);
        foreach (var instance in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = instance.ResolveEffectiveCategory(categoryOverride);
            var result = await ValidateInstanceAsync(instance, category, verifyWithTests, cancellationToken).ConfigureAwait(false);
            results.Add(result);

            var statusTag = result.Passed ? BenchmarkDatasetConstants.StatusPass : BenchmarkDatasetConstants.StatusFail;
            var detail = result.Passed ? result.Summary : result.ErrorMessage;
            progressCallback?.Invoke($"[{statusTag}] {result.InstanceId} ({result.Category}, {result.Language}, {result.DurationMs} ms): {detail}");
        }

        return new BenchmarkDatasetValidationReport
        {
            DatasetPath = Path.GetFullPath(datasetPath),
            Results = results
        };
    }

    private static IReadOnlyList<BenchmarkInstance> FilterInstances(
        IReadOnlyList<BenchmarkInstance> allInstances,
        string datasetPath,
        IReadOnlyList<string>? instanceIdFilter,
        int? limit)
    {
        IEnumerable<BenchmarkInstance> query = allInstances;
        if (instanceIdFilter is { Count: > 0 })
        {
            var requestedIds = instanceIdFilter
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var availableIds = allInstances.Select(i => i.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = requestedIds.Where(id => !availableIds.Contains(id)).ToList();
            if (missing.Count > 0)
            {
                throw new BenchmarkDatasetException(
                    $"Requested --filter instance_id(s) not found in '{datasetPath}': {string.Join(", ", missing)}.",
                    datasetPath,
                    missing[0]);
            }

            var requestedSet = requestedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            query = query.Where(i => requestedSet.Contains(i.InstanceId));
        }

        if (limit is > 0)
        {
            query = query.Take(limit.Value);
        }

        var selected = query.ToList();
        if (selected.Count == 0)
        {
            throw new BenchmarkDatasetException("No dataset instances matched the requested filters.", datasetPath);
        }

        return selected;
    }

    private async Task<BenchmarkInstanceValidationResult> ValidateInstanceAsync(
        BenchmarkInstance instance,
        BenchmarkProblemCategory category,
        bool verifyWithTests,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        BenchmarkWorkspaceHandle? handle = null;

        try
        {
            var preflightError = await _toolchainPreflightValidator
                .ValidateAsync(instance, category, ExecutionMode.ContainerSandbox, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(preflightError))
            {
                sw.Stop();
                return new BenchmarkInstanceValidationResult
                {
                    InstanceId = instance.InstanceId,
                    Category = category,
                    Language = instance.Language,
                    Passed = false,
                    ErrorMessage = preflightError,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            handle = await _workspaceManager.ProvisionAsync(instance, category, cancellationToken: cancellationToken).ConfigureAwait(false);

            var (passed, summary, error) = category switch
            {
                BenchmarkProblemCategory.Implementation => await ValidateImplementationAsync(instance, handle, verifyWithTests, cancellationToken).ConfigureAwait(false),
                BenchmarkProblemCategory.CodeReview => await ValidateCodeReviewAsync(instance, handle, verifyWithTests, cancellationToken).ConfigureAwait(false),
                BenchmarkProblemCategory.TaskReview => await ValidateTaskReviewAsync(instance, handle, cancellationToken).ConfigureAwait(false),
                _ => (false, null, $"Unsupported category '{category}'.")
            };

            sw.Stop();
            return new BenchmarkInstanceValidationResult
            {
                InstanceId = instance.InstanceId,
                Category = category,
                Language = instance.Language,
                Passed = passed,
                Summary = summary,
                ErrorMessage = error,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is BenchmarkException)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Pre-flight validation failed for benchmark instance '{InstanceId}'.", instance.InstanceId);
            return new BenchmarkInstanceValidationResult
            {
                InstanceId = instance.InstanceId,
                Category = category,
                Language = instance.Language,
                Passed = false,
                ErrorMessage = ex.Message,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        finally
        {
            if (handle != null)
            {
                try
                {
                    await _workspaceManager.CleanupAsync(handle, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to clean up ephemeral validation workspace '{WorkspacePath}' for '{InstanceId}'.",
                        handle.WorkspacePath, instance.InstanceId);
                }
            }
        }
    }

    private async Task<(bool Passed, string? Summary, string? Error)> ValidateImplementationAsync(
        BenchmarkInstance instance,
        BenchmarkWorkspaceHandle workspace,
        bool verifyWithTests,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instance.GoldPatch))
        {
            return (false, null, $"Implementation instance '{instance.InstanceId}' is missing required 'patch' (GoldPatch).");
        }

        if (!verifyWithTests)
        {
            if (!string.IsNullOrWhiteSpace(instance.TestPatch))
            {
                var testOnlyCheck = await TryApplyPatchSequenceAsync(
                    workspace.WorkspacePath,
                    workspace.BaseCommit,
                    [instance.TestPatch],
                    $"Implementation TestPatch failed to apply cleanly to BaseCommit for '{instance.InstanceId}'",
                    cancellationToken).ConfigureAwait(false);
                if (!testOnlyCheck.Passed)
                {
                    return (false, null, testOnlyCheck.Error);
                }
            }

            var combinedCheck = await TryApplyPatchSequenceAsync(
                workspace.WorkspacePath,
                workspace.BaseCommit,
                [instance.GoldPatch, instance.TestPatch],
                $"Implementation GoldPatch/TestPatch failed to apply cleanly to BaseCommit for '{instance.InstanceId}'",
                cancellationToken).ConfigureAwait(false);
            if (!combinedCheck.Passed)
            {
                return (false, null, combinedCheck.Error);
            }

            return (true, "GoldPatch and TestPatch applied cleanly to BaseCommit (--no-verify).", null);
        }

        var baseResult = await _verifier.RunImplementationTestsAsync(
            instance,
            workspace,
            patchToApply: null,
            ExecutionMode.ContainerSandbox,
            cancellationToken).ConfigureAwait(false);

        if (instance.FailToPass.Count > 0)
        {
            if (baseResult.FailToPassPassed > 0)
            {
                var failedSet = baseResult.FailedTests.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var passedOnBase = instance.FailToPass
                    .Where(t => !failedSet.Contains(t))
                    .ToList();
                var passedDetail = passedOnBase.Count > 0
                    ? $" Passing test(s): {string.Join(", ", passedOnBase)}"
                    : string.Empty;
                return (false, null,
                    $"Pre-flight FailToPass invariant violated: {baseResult.FailToPassPassed}/{baseResult.FailToPassTotal} FailToPass test(s) already pass on BaseCommit without GoldPatch.{passedDetail}");
            }
        }
        else if (baseResult.Passed)
        {
            return (false, null, "Pre-flight invariant violated: test suite already passed on BaseCommit without GoldPatch.");
        }

        var goldResult = await _verifier.RunImplementationTestsAsync(
            instance,
            workspace,
            patchToApply: instance.GoldPatch,
            ExecutionMode.ContainerSandbox,
            cancellationToken).ConfigureAwait(false);

        if (!goldResult.Passed)
        {
            return (false, null, $"GoldPatch failed verification on BaseCommit + TestPatch: {FormatFailedTestReason(goldResult)}");
        }

        return (
            true,
            $"Base failed FailToPass; GoldPatch passed {goldResult.FailToPassPassed}/{goldResult.FailToPassTotal} FailToPass and {goldResult.PassToPassPassed}/{goldResult.PassToPassTotal} PassToPass.",
            null);
    }

    private async Task<(bool Passed, string? Summary, string? Error)> ValidateCodeReviewAsync(
        BenchmarkInstance instance,
        BenchmarkWorkspaceHandle workspace,
        bool verifyWithTests,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspace.ReviewDiff))
        {
            return (false, null, $"CodeReview instance '{instance.InstanceId}' produced an empty ReviewDiff after applying CandidatePatch.");
        }

        if (!BenchmarkDatasetLoader.TryParseCodeReviewVerdict(instance.ExpectedVerdict, out var expectedVerdict))
        {
            return (false, null, $"Invalid ExpectedVerdict '{instance.ExpectedVerdict}' for CodeReview instance '{instance.InstanceId}'.");
        }

        bool hasTestPatch = !string.IsNullOrWhiteSpace(instance.TestPatch);
        bool hasTestAssertions = hasTestPatch || instance.FailToPass.Count > 0 || instance.PassToPass.Count > 0;

        if (!verifyWithTests)
        {
            if (hasTestPatch)
            {
                var candidateTestPatchCheck = await TryApplyPatchSequenceAsync(
                    workspace.WorkspacePath,
                    workspace.BaseCommit,
                    [instance.CandidatePatch, instance.TestPatch],
                    $"CodeReview TestPatch failed to apply cleanly on CandidatePatch for '{instance.InstanceId}'",
                    cancellationToken).ConfigureAwait(false);
                if (!candidateTestPatchCheck.Passed)
                {
                    return (false, null, candidateTestPatchCheck.Error);
                }
            }

            if (expectedVerdict == CodeReviewVerdict.MinorFix && !string.IsNullOrWhiteSpace(instance.GoldPatch))
            {
                var goldTestPatchCheck = await TryApplyPatchSequenceAsync(
                    workspace.WorkspacePath,
                    workspace.BaseCommit,
                    [instance.GoldPatch, instance.TestPatch],
                    $"CodeReview MinorFix reference GoldPatch/TestPatch failed to apply cleanly for '{instance.InstanceId}'",
                    cancellationToken).ConfigureAwait(false);
                if (!goldTestPatchCheck.Passed)
                {
                    return (false, null, goldTestPatchCheck.Error);
                }
            }

            return (true, "CandidatePatch applied cleanly to BaseCommit with valid diff.", null);
        }

        if (!hasTestAssertions)
        {
            return (true, "CandidatePatch applied cleanly to BaseCommit with valid diff.", null);
        }

        if (expectedVerdict == CodeReviewVerdict.Approve)
        {
            var candidateTestResult = await _verifier.RunImplementationTestsAsync(
                instance,
                workspace,
                patchToApply: instance.CandidatePatch,
                ExecutionMode.ContainerSandbox,
                cancellationToken).ConfigureAwait(false);

            if (!candidateTestResult.Passed)
            {
                return (false, null, $"CodeReview Approve fixture tests failed: {FormatFailedTestReason(candidateTestResult)}");
            }

            return (true, "CandidatePatch applied cleanly and Approve tests passed.", null);
        }

        if (expectedVerdict == CodeReviewVerdict.MinorFix)
        {
            if (!string.IsNullOrWhiteSpace(instance.GoldPatch))
            {
                var goldTestResult = await _verifier.RunImplementationTestsAsync(
                    instance,
                    workspace,
                    patchToApply: instance.GoldPatch,
                    ExecutionMode.ContainerSandbox,
                    cancellationToken).ConfigureAwait(false);

                if (!goldTestResult.Passed)
                {
                    return (false, null, $"CodeReview MinorFix reference GoldPatch tests failed: {FormatFailedTestReason(goldTestResult)}");
                }

                return (true, "CandidatePatch applied cleanly and MinorFix reference GoldPatch tests passed.", null);
            }

            if (hasTestPatch)
            {
                var candidateTestPatchCheck = await TryApplyPatchSequenceAsync(
                    workspace.WorkspacePath,
                    workspace.BaseCommit,
                    [instance.CandidatePatch, instance.TestPatch],
                    $"CodeReview TestPatch failed to apply cleanly on CandidatePatch for '{instance.InstanceId}'",
                    cancellationToken).ConfigureAwait(false);
                if (!candidateTestPatchCheck.Passed)
                {
                    return (false, null, candidateTestPatchCheck.Error);
                }

                return (true, "CandidatePatch and TestPatch applied cleanly to BaseCommit.", null);
            }
        }

        return (true, "CandidatePatch applied cleanly to BaseCommit with valid diff.", null);
    }

    private async Task<(bool Passed, string? Error)> TryApplyPatchSequenceAsync(
        string workspacePath,
        string baseCommit,
        IReadOnlyList<string?> patches,
        string failureContext,
        CancellationToken cancellationToken)
    {
        await _workspaceManager.ResetToBaseAsync(workspacePath, baseCommit, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var patch in patches)
            {
                if (!string.IsNullOrWhiteSpace(patch))
                {
                    await _workspaceManager.ApplyPatchAsync(workspacePath, patch, cancellationToken).ConfigureAwait(false);
                }
            }

            return (true, null);
        }
        catch (BenchmarkWorkspaceException ex)
        {
            return (false, $"{failureContext}: {ex.Message}");
        }
    }

    private static string FormatFailedTestReason(BenchmarkVerificationResult result) =>
        string.IsNullOrWhiteSpace(result.ErrorMessage)
            ? $"Failed tests: {string.Join(", ", result.FailedTests)}"
            : result.ErrorMessage;

    private async Task<(bool Passed, string? Summary, string? Error)> ValidateTaskReviewAsync(
        BenchmarkInstance instance,
        BenchmarkWorkspaceHandle workspace,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspace.TaskMarkdownPath))
        {
            return (false, null, $"TaskReview instance '{instance.InstanceId}' did not materialize TaskMarkdownPath.");
        }

        var normalizedTaskPath = workspace.TaskMarkdownPath.Replace('\\', '/');
        if (!normalizedTaskPath.StartsWith(BenchmarkPaths.DocsTasksPrefix, StringComparison.OrdinalIgnoreCase) ||
            normalizedTaskPath.Split('/').Any(seg => seg == ".."))
        {
            return (false, null, $"TaskReview markdown path '{workspace.TaskMarkdownPath}' must reside under '{BenchmarkPaths.DocsTasksPrefix}'.");
        }

        var fullTaskPath = Path.Combine(
            workspace.WorkspacePath,
            normalizedTaskPath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(fullTaskPath))
        {
            return (false, null, $"TaskReview markdown file '{workspace.TaskMarkdownPath}' was not created in workspace.");
        }

        var content = await File.ReadAllTextAsync(fullTaskPath, cancellationToken).ConfigureAwait(false);
        var (frontmatter, body) = BenchmarkVerifier.ParseTaskMarkdown(content, _logger);

        if (frontmatter == null)
        {
            return (false, null, $"TaskReview markdown '{workspace.TaskMarkdownPath}' has missing or invalid YAML frontmatter.");
        }

        if (!frontmatter.Id.HasValue || frontmatter.Id.Value <= 0)
        {
            return (false, null, $"TaskReview markdown '{workspace.TaskMarkdownPath}' frontmatter is missing a valid positive 'id'.");
        }

        if (string.IsNullOrWhiteSpace(frontmatter.Title))
        {
            return (false, null, $"TaskReview markdown '{workspace.TaskMarkdownPath}' frontmatter is missing 'title'.");
        }

        if (string.IsNullOrWhiteSpace(frontmatter.Status))
        {
            return (false, null, $"TaskReview markdown '{workspace.TaskMarkdownPath}' frontmatter is missing 'status'.");
        }

        if (!string.IsNullOrWhiteSpace(frontmatter.Difficulty) &&
            !Enum.TryParse<TaskDifficulty>(frontmatter.Difficulty.Trim(), ignoreCase: true, out _))
        {
            return (false, null, $"TaskReview markdown '{workspace.TaskMarkdownPath}' has invalid frontmatter difficulty '{frontmatter.Difficulty}'.");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return (false, null, $"TaskReview markdown '{workspace.TaskMarkdownPath}' has an empty specification body.");
        }

        return (true, $"Valid BaseCommit checkout and task markdown frontmatter at '{workspace.TaskMarkdownPath}'.", null);
    }
}
