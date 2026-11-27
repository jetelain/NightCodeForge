using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Extensions.Logging;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Git;
using YamlDotNet.Core;

namespace NightCodeForge.Benchmark.Cli.Verification;

/// <summary>
/// Verifies benchmark outcomes across Implementation (containerized multi-stack test verification),
/// Code Review, and ForgeTask Review (deterministic scoring + optional LLM-as-judge).
/// </summary>
public sealed partial class BenchmarkVerifier : IBenchmarkVerifier
{
    private const double ReviewVerdictWeightWithFindings = 0.6;
    private const double ReviewFindingsWeight = 0.4;
    private const double WrongVerdictAnchorWeight = 0.5;
    private const double MinimumAnchorCoverageForPass = 0.5;
    private const double AdjacentEscalatedDifficultyCredit = 0.75;
    private const int MinimumFileStemCorroborationLength = 5;
    private const int MaxZeroResultOutputTailChars = 400;
    private const int MaxZeroResultOutputTailLines = 5;

    private static readonly HashSet<string> GenericFileStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "index",
        "utils",
        "helpers",
        "common",
        "types",
        "config",
        "tests",
        "program",
        "__init__",
        "default",
        "error",
        "errors",
        "client",
        "server",
        "model",
        "models"
    };

    [GeneratedRegex(@"[A-Za-z0-9_]{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex SummaryTokenRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceCollapseRegex();

    private readonly IBenchmarkWorkspaceManager _workspaceManager;
    private readonly IContainerEnvironmentFactory _containerFactory;
    private readonly IExecutionEnvironmentProvider _envProvider;
    private readonly IBenchmarkLlmJudge _llmJudge;
    private readonly ILogger<BenchmarkVerifier> _logger;

    public BenchmarkVerifier(
        IBenchmarkWorkspaceManager workspaceManager,
        IContainerEnvironmentFactory containerFactory,
        IExecutionEnvironmentProvider envProvider,
        IBenchmarkLlmJudge llmJudge,
        ILogger<BenchmarkVerifier> logger)
    {
        _workspaceManager = workspaceManager ?? throw new ArgumentNullException(nameof(workspaceManager));
        _containerFactory = containerFactory ?? throw new ArgumentNullException(nameof(containerFactory));
        _envProvider = envProvider ?? throw new ArgumentNullException(nameof(envProvider));
        _llmJudge = llmJudge ?? throw new ArgumentNullException(nameof(llmJudge));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BenchmarkVerificationResult> VerifyAsync(
        BenchmarkInstance instance,
        BenchmarkWorkspaceHandle workspace,
        BenchmarkAgentOutcome outcome,
        ExecutionMode executionMode,
        bool verifyWithTests,
        BenchmarkJudgeConfig? judgeConfig,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(outcome);

        return workspace.Category switch
        {
            BenchmarkProblemCategory.Implementation => await VerifyImplementationAsync(
                instance, workspace, outcome, executionMode, verifyWithTests, cancellationToken).ConfigureAwait(false),
            BenchmarkProblemCategory.CodeReview => await VerifyCodeReviewAsync(
                instance, workspace, outcome, executionMode, verifyWithTests, judgeConfig, cancellationToken).ConfigureAwait(false),
            BenchmarkProblemCategory.TaskReview => await VerifyTaskReviewAsync(
                instance, outcome, judgeConfig, cancellationToken).ConfigureAwait(false),
            _ => throw new BenchmarkVerificationException($"Unsupported category '{workspace.Category}'.", instance.InstanceId)
        };
    }

    private async Task<BenchmarkVerificationResult> VerifyImplementationAsync(
        BenchmarkInstance instance,
        BenchmarkWorkspaceHandle workspace,
        BenchmarkAgentOutcome outcome,
        ExecutionMode executionMode,
        bool verifyWithTests,
        CancellationToken cancellationToken)
    {
        if (!verifyWithTests)
        {
            bool hasPatch = !string.IsNullOrWhiteSpace(outcome.ModelPatch);
            return new BenchmarkVerificationResult
            {
                Passed = hasPatch,
                DeterministicScore = hasPatch ? 1.0 : 0.0,
                FailToPassTotal = instance.FailToPass.Count,
                PassToPassTotal = instance.PassToPass.Count,
                ErrorMessage = hasPatch ? null : "Model produced an empty patch."
            };
        }

        if (string.IsNullOrWhiteSpace(outcome.ModelPatch) && instance.FailToPass.Count > 0)
        {
            return new BenchmarkVerificationResult
            {
                Passed = false,
                DeterministicScore = 0.0,
                FailToPassPassed = 0,
                FailToPassTotal = instance.FailToPass.Count,
                PassToPassPassed = 0,
                PassToPassTotal = instance.PassToPass.Count,
                FailedTests = instance.FailToPass,
                ErrorMessage = "Model produced an empty patch."
            };
        }

        return await RunImplementationTestsAsync(
            instance,
            workspace,
            outcome.ModelPatch,
            executionMode,
            failToPassOnly: false,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BenchmarkVerificationResult> RunImplementationTestsAsync(
        BenchmarkInstance instance,
        BenchmarkWorkspaceHandle workspace,
        string? patchToApply,
        ExecutionMode executionMode,
        bool failToPassOnly = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(workspace);

        await _workspaceManager.ResetToBaseAsync(workspace.WorkspacePath, workspace.BaseCommit, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(patchToApply))
        {
            try
            {
                await _workspaceManager.ApplyPatchAsync(workspace.WorkspacePath, patchToApply, cancellationToken).ConfigureAwait(false);
            }
            catch (BenchmarkWorkspaceException ex)
            {
                return new BenchmarkVerificationResult
                {
                    Passed = false,
                    IsPatchApplicationError = true,
                    DeterministicScore = 0.0,
                    FailToPassTotal = instance.FailToPass.Count,
                    PassToPassTotal = instance.PassToPass.Count,
                    FailedTests = instance.FailToPass.Concat(instance.PassToPass).ToList(),
                    ErrorMessage = $"Patch failed to apply cleanly: {ex.Message}"
                };
            }
        }

        var testPatchError = await ResetAndApplyTestPatchAsync(
            workspace.WorkspacePath,
            workspace.BaseCommit,
            instance.TestPatch,
            cancellationToken).ConfigureAwait(false);
        if (testPatchError != null)
        {
            return new BenchmarkVerificationResult
            {
                Passed = false,
                IsPatchApplicationError = true,
                DeterministicScore = 0.0,
                FailToPassTotal = instance.FailToPass.Count,
                PassToPassTotal = instance.PassToPass.Count,
                FailedTests = instance.FailToPass.Concat(instance.PassToPass).ToList(),
                ErrorMessage = testPatchError
            };
        }

        return await ExecuteTestSuiteAsync(instance, workspace.WorkspacePath, executionMode, failToPassOnly, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ResetAndApplyTestPatchAsync(
        string workspacePath,
        string baseCommit,
        string? testPatch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(testPatch))
        {
            return null;
        }

        var targetPaths = BenchmarkWorkspaceManager.ExtractPatchTargetPaths(testPatch);
        try
        {
            if (targetPaths.Count > 0)
            {
                await _workspaceManager.ResetPathsToBaseAsync(workspacePath, baseCommit, targetPaths, cancellationToken).ConfigureAwait(false);
            }

            await _workspaceManager.ApplyPatchAsync(workspacePath, testPatch, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (BenchmarkWorkspaceException ex)
        {
            return $"Evaluation TestPatch failed to apply cleanly: {ex.Message}";
        }
    }

    private static void PrepareResultsDirectory(string resultsHostDir)
    {
        if (Directory.Exists(resultsHostDir))
        {
            Directory.Delete(resultsHostDir, recursive: true);
        }
        Directory.CreateDirectory(resultsHostDir);
    }

    private async Task<BenchmarkVerificationResult> ExecuteTestSuiteAsync(
        BenchmarkInstance instance,
        string workspacePath,
        ExecutionMode executionMode,
        bool failToPassOnly,
        CancellationToken cancellationToken)
    {
        var resultsHostDir = Path.Combine(workspacePath, BenchmarkPaths.BenchmarkResultDirRelative);
        PrepareResultsDirectory(resultsHostDir);

        var containerName = $"ncf-bench-verify-{Guid.NewGuid():N}"[..32];
        var scope = CreateVerificationScope(workspacePath, instance.CustomContainerImage, executionMode, containerName);
        var testCommand = ResolveTestCommand(instance, workspacePath, executionMode, failToPassOnly);
        var effectiveCommand = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(testCommand, instance.Language, executionMode, instance.PipConstraints);
        var format = instance.ResolveEffectiveTestResultFormat();
        var isCSharp = string.Equals(BenchmarkLanguages.Normalize(instance.Language), BenchmarkLanguages.CSharp, StringComparison.OrdinalIgnoreCase);
        var testRunStartedUtc = DateTime.UtcNow;

        CommandResult cmdResult;
        string combinedOutput;
        string finalAttemptOutput;
        IReadOnlyList<ParsedTestOutcome> parsedOutcomes;
        try
        {
            using var globalJsonScope = DotNetGlobalJsonResilienceScope.Apply(workspacePath, instance.Language, _logger);
            IExecutionEnvironment env;
            try
            {
                env = executionMode == ExecutionMode.ContainerSandbox
                    ? await _containerFactory.CreateContainerEnvironmentAsync(scope, cancellationToken).ConfigureAwait(false)
                    : await _envProvider.GetEnvironmentAsync(scope, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new BenchmarkVerificationException(
                    $"Failed to initialize verification execution environment (image '{instance.CustomContainerImage ?? "default"}'): {ex.Message}",
                    ex,
                    instance.InstanceId);
            }

            try
            {
                testRunStartedUtc = DateTime.UtcNow;
                IReadOnlyDictionary<string, string>? environmentVariables = isCSharp
                    ? new Dictionary<string, string> { [BenchmarkDefaults.DotNetRollForwardEnvVar] = BenchmarkDefaults.DotNetRollForwardLatestMajor }
                    : null;
                cmdResult = await env.ExecuteAsync(
                    effectiveCommand,
                    workingDirectory: executionMode == ExecutionMode.ContainerSandbox ? null : workspacePath,
                    timeoutSeconds: BenchmarkDefaults.DefaultVerificationTimeoutSeconds,
                    environmentVariables: environmentVariables,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var attempt1Output = BuildCombinedCommandOutput(cmdResult);
                combinedOutput = attempt1Output;
                finalAttemptOutput = attempt1Output;
                parsedOutcomes = ParseStructuredOutcomes(format, resultsHostDir, workspacePath, attempt1Output, testRunStartedUtc, instance.InstanceId);

                if (isCSharp &&
                    cmdResult.ExitCode != 0 &&
                    parsedOutcomes.Count == 0 &&
                    BenchmarkTestCommandResolver.HasMsBuildNodeCrashSignature(attempt1Output))
                {
                    _logger.LogWarning(
                        "MSBuild child-node crash detected during verification of '{InstanceId}'; retrying once with single-node (-m:1) concurrency and node reuse disabled.",
                        instance.InstanceId);

                    PrepareResultsDirectory(resultsHostDir);
                    var retryCommand = BenchmarkTestCommandResolver.ApplyMsBuildSingleNodeFlag(effectiveCommand);
                    var retryEnvVars = new Dictionary<string, string>
                    {
                        [BenchmarkDefaults.DotNetRollForwardEnvVar] = BenchmarkDefaults.DotNetRollForwardLatestMajor,
                        [BenchmarkDefaults.DotNetCliDoNotUseMsBuildServerEnvVar] = BenchmarkDefaults.EnvFlagEnabled,
                        [BenchmarkDefaults.MsBuildDisableNodeReuseEnvVar] = BenchmarkDefaults.EnvFlagEnabled
                    };

                    testRunStartedUtc = DateTime.UtcNow;
                    cmdResult = await env.ExecuteAsync(
                        retryCommand,
                        workingDirectory: executionMode == ExecutionMode.ContainerSandbox ? null : workspacePath,
                        timeoutSeconds: BenchmarkDefaults.DefaultVerificationTimeoutSeconds,
                        environmentVariables: retryEnvVars,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    var attempt2Output = BuildCombinedCommandOutput(cmdResult);
                    finalAttemptOutput = attempt2Output;
                    combinedOutput =
                        $"[Attempt 1 - MSBuild node crash detected, retrying with -m:1]\n{attempt1Output}\n\n" +
                        $"[Attempt 2 - Single-node retry (-m:1)]\n{attempt2Output}";
                    parsedOutcomes = ParseStructuredOutcomes(format, resultsHostDir, workspacePath, attempt2Output, testRunStartedUtc, instance.InstanceId);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new BenchmarkVerificationException(
                    $"Test verification command execution failed for '{instance.InstanceId}' (image '{instance.CustomContainerImage ?? "default"}'): {ex.Message}",
                    ex,
                    instance.InstanceId);
            }
            finally
            {
                if (executionMode == ExecutionMode.ContainerSandbox && env is IDisposable disposableEnv)
                {
                    disposableEnv.Dispose();
                }
            }
        }
        finally
        {
            if (executionMode == ExecutionMode.ContainerSandbox)
            {
                try
                {
                    await _containerFactory.RemoveContainerAsync(scope, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove verification container '{ContainerName}' for '{InstanceId}'.",
                        containerName, instance.InstanceId);
                }
            }
            else if (executionMode == ExecutionMode.WslDirect)
            {
                try
                {
                    await _envProvider.CleanupScopeEnvironmentAsync(scope, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up WSL verification scope for '{InstanceId}'.",
                        instance.InstanceId);
                }
            }
        }

        if (isCSharp &&
            cmdResult.ExitCode != 0 &&
            parsedOutcomes.Count == 0 &&
            BenchmarkTestCommandResolver.HasMsBuildNodeCrashSignature(finalAttemptOutput))
        {
            var tail = FormatCommandOutputTailForError(finalAttemptOutput);
            var infraErrorMessage = string.IsNullOrEmpty(tail)
                ? $"Verification infrastructure error: MSBuild child-node crash persisted after single-node (-m:1) retry (exit code {cmdResult.ExitCode})."
                : $"Verification infrastructure error: MSBuild child-node crash persisted after single-node (-m:1) retry (exit code {cmdResult.ExitCode}). Output tail: {tail}";
            return new BenchmarkVerificationResult
            {
                Passed = false,
                IsInfrastructureError = true,
                DeterministicScore = 0.0,
                FailToPassTotal = instance.FailToPass.Count,
                PassToPassTotal = instance.PassToPass.Count,
                FailedTests = instance.FailToPass.Concat(instance.PassToPass).ToList(),
                VerificationLog = combinedOutput,
                ErrorMessage = infraErrorMessage
            };
        }

        if (instance.FailToPass.Count == 0 && instance.PassToPass.Count == 0)
        {
            bool passed = cmdResult.ExitCode == 0 &&
                          (parsedOutcomes.Count == 0 ||
                           (parsedOutcomes.Any(o => o.Passed) && parsedOutcomes.All(o => o.Passed || o.Skipped)));
            var failed = parsedOutcomes.Where(o => !o.Passed && !o.Skipped).Select(o => o.Name).ToList();
            var errorMessage = passed
                ? null
                : (cmdResult.ExitCode != 0
                    ? $"Verification test command exited with code {cmdResult.ExitCode}."
                    : "Verification produced zero passing tests (all discovered tests were skipped).");
            return new BenchmarkVerificationResult
            {
                Passed = passed,
                DeterministicScore = passed ? 1.0 : 0.0,
                FailedTests = failed,
                VerificationLog = combinedOutput,
                ErrorMessage = errorMessage
            };
        }

        if (format == TestResultFormat.ExitCode)
        {
            bool exitOk = cmdResult.ExitCode == 0;
            return new BenchmarkVerificationResult
            {
                Passed = exitOk,
                DeterministicScore = exitOk ? 1.0 : 0.0,
                FailToPassPassed = exitOk ? instance.FailToPass.Count : 0,
                FailToPassTotal = instance.FailToPass.Count,
                PassToPassPassed = exitOk ? instance.PassToPass.Count : 0,
                PassToPassTotal = instance.PassToPass.Count,
                FailedTests = exitOk ? Array.Empty<string>() : instance.FailToPass.Concat(instance.PassToPass).ToList(),
                VerificationLog = combinedOutput,
                ErrorMessage = exitOk ? null : $"Test command exited with code {cmdResult.ExitCode}."
            };
        }

        var eval = TestAssertionEvaluator.Evaluate(instance.FailToPass, instance.PassToPass, parsedOutcomes);
        double score = ComputeImplementationDeterministicScore(
            eval.FailToPassPassed,
            eval.FailToPassTotal,
            eval.PassToPassPassed,
            eval.PassToPassTotal,
            cmdResult.ExitCode);

        return new BenchmarkVerificationResult
        {
            Passed = eval.AllPassed,
            DeterministicScore = score,
            FailToPassPassed = eval.FailToPassPassed,
            FailToPassTotal = eval.FailToPassTotal,
            PassToPassPassed = eval.PassToPassPassed,
            PassToPassTotal = eval.PassToPassTotal,
            FailedTests = eval.FailedTests,
            VerificationLog = combinedOutput,
            ErrorMessage = eval.AllPassed
                ? null
                : BuildAssertionFailureMessage(cmdResult.ExitCode, parsedOutcomes.Count, combinedOutput, eval.FailedTests)
        };
    }

    private static string BuildAssertionFailureMessage(
        int exitCode,
        int parsedOutcomeCount,
        string combinedOutput,
        IReadOnlyList<string> failedTests)
    {
        var failedList = string.Join(", ", failedTests);
        if (parsedOutcomeCount == 0 && exitCode != 0)
        {
            var outputTail = FormatCommandOutputTailForError(combinedOutput);
            return string.IsNullOrEmpty(outputTail)
                ? $"Test command exited with code {exitCode} and produced no parsed test results. Failed tests: {failedList}"
                : $"Test command exited with code {exitCode} and produced no parsed test results. Output tail: {outputTail} | Failed tests: {failedList}";
        }

        return $"Failed tests: {failedList}";
    }

    private static string FormatCommandOutputTailForError(string? combinedOutput, int maxChars = MaxZeroResultOutputTailChars)
    {
        if (string.IsNullOrWhiteSpace(combinedOutput) || maxChars <= 0)
        {
            return string.Empty;
        }

        var tailLines = combinedOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !string.Equals(l, "[STDERR]", StringComparison.Ordinal))
            .TakeLast(MaxZeroResultOutputTailLines);

        var collapsed = WhitespaceCollapseRegex().Replace(string.Join(" ", tailLines), " ").Trim();
        if (collapsed.Length <= maxChars)
        {
            return collapsed;
        }

        const string ellipsis = "...";
        return maxChars > ellipsis.Length
            ? ellipsis + collapsed[^((maxChars - ellipsis.Length))..]
            : collapsed[^maxChars..];
    }

    private async Task<BenchmarkVerificationResult> VerifyCodeReviewAsync(
        BenchmarkInstance instance,
        BenchmarkWorkspaceHandle workspace,
        BenchmarkAgentOutcome outcome,
        ExecutionMode executionMode,
        bool verifyWithTests,
        BenchmarkJudgeConfig? judgeConfig,
        CancellationToken cancellationToken)
    {
        if (!BenchmarkDatasetLoader.TryParseCodeReviewVerdict(instance.ExpectedVerdict, out var expectedVerdict))
        {
            throw new BenchmarkVerificationException(
                $"Invalid ExpectedVerdict '{instance.ExpectedVerdict}' for CodeReview instance '{instance.InstanceId}'.",
                instance.InstanceId);
        }

        var actualVerdict = ClassifyCodeReviewVerdict(outcome);
        bool verdictMatched = actualVerdict == expectedVerdict;

        var combinedCorpus = string.Join(
            "\n",
            outcome.ReviewerFeedback ?? string.Empty,
            outcome.ReviewerRejectionReason ?? string.Empty,
            outcome.Summary ?? string.Empty,
            outcome.ModelPatch ?? string.Empty);

        var (anchorScore, missingAnchors) = EvaluateExpectedFindings(instance.ExpectedFindings, combinedCorpus);

        bool minorFixTestsPassed = true;
        bool isInfrastructureError = false;
        int f2pPassed = 0, f2pTotal = 0;
        int p2pPassed = 0, p2pTotal = 0;
        IReadOnlyList<string> failedTests = Array.Empty<string>();
        string? testLog = null;
        string? testError = null;

        if (actualVerdict == CodeReviewVerdict.MinorFix &&
            verifyWithTests &&
            (!string.IsNullOrWhiteSpace(instance.TestPatch) || instance.FailToPass.Count > 0 || instance.PassToPass.Count > 0))
        {
            f2pTotal = instance.FailToPass.Count;
            p2pTotal = instance.PassToPass.Count;
            await _workspaceManager.ResetToBaseAsync(workspace.WorkspacePath, workspace.BaseCommit, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(instance.CandidatePatch))
            {
                await _workspaceManager.ApplyPatchAsync(workspace.WorkspacePath, instance.CandidatePatch, cancellationToken).ConfigureAwait(false);
            }

            bool minorFixPatchApplied = true;
            if (!string.IsNullOrWhiteSpace(outcome.ModelPatch))
            {
                try
                {
                    await _workspaceManager.ApplyPatchAsync(workspace.WorkspacePath, outcome.ModelPatch, cancellationToken).ConfigureAwait(false);
                }
                catch (BenchmarkWorkspaceException ex)
                {
                    minorFixPatchApplied = false;
                    minorFixTestsPassed = false;
                    failedTests = instance.FailToPass.Concat(instance.PassToPass).ToList();
                    testError = $"Generated minor-fix patch failed to apply cleanly: {ex.Message}";
                }
            }

            if (minorFixPatchApplied)
            {
                var testPatchError = await ResetAndApplyTestPatchAsync(
                    workspace.WorkspacePath,
                    workspace.BaseCommit,
                    instance.TestPatch,
                    cancellationToken).ConfigureAwait(false);
                if (testPatchError != null)
                {
                    minorFixTestsPassed = false;
                    failedTests = instance.FailToPass.Concat(instance.PassToPass).ToList();
                    testError = testPatchError;
                }
                else
                {
                    var testRes = await ExecuteTestSuiteAsync(instance, workspace.WorkspacePath, executionMode, failToPassOnly: false, cancellationToken).ConfigureAwait(false);
                    minorFixTestsPassed = testRes.Passed;
                    isInfrastructureError = testRes.IsInfrastructureError;
                    f2pPassed = testRes.FailToPassPassed;
                    f2pTotal = testRes.FailToPassTotal;
                    p2pPassed = testRes.PassToPassPassed;
                    p2pTotal = testRes.PassToPassTotal;
                    failedTests = testRes.FailedTests;
                    testLog = testRes.VerificationLog;
                    testError = testRes.ErrorMessage;
                }
            }
        }

        double verdictComponent = verdictMatched ? 1.0 : 0.0;
        double deterministicScore = isInfrastructureError
            ? 0.0
            : (instance.ExpectedFindings.Count > 0
                ? (ReviewVerdictWeightWithFindings * verdictComponent) + (ReviewFindingsWeight * anchorScore)
                : verdictComponent);
        if (!isInfrastructureError && !minorFixTestsPassed)
        {
            deterministicScore *= 0.5;
        }

        bool passed = !isInfrastructureError &&
                      verdictMatched &&
                      (instance.ExpectedFindings.Count == 0 || anchorScore >= MinimumAnchorCoverageForPass) &&
                      minorFixTestsPassed;

        BenchmarkJudgeResult? judgeResult = null;
        if (!isInfrastructureError && judgeConfig != null)
        {
            judgeResult = await _llmJudge.EvaluateCodeReviewAsync(instance, outcome, judgeConfig, cancellationToken).ConfigureAwait(false);
        }

        string? errorMsg = testError;
        if (!passed && errorMsg == null)
        {
            errorMsg = !verdictMatched
                ? $"Expected verdict '{expectedVerdict}' but got '{actualVerdict}'."
                : $"Missing expected finding anchors: {string.Join("; ", missingAnchors)}";
        }

        return new BenchmarkVerificationResult
        {
            Passed = passed,
            IsInfrastructureError = isInfrastructureError,
            ActualVerdict = actualVerdict.ToString(),
            DeterministicScore = deterministicScore,
            FailToPassPassed = f2pPassed,
            FailToPassTotal = f2pTotal,
            PassToPassPassed = p2pPassed,
            PassToPassTotal = p2pTotal,
            FailedTests = failedTests,
            JudgeScore = judgeResult?.Score,
            JudgeRationale = judgeResult?.Rationale,
            VerificationLog = testLog,
            ErrorMessage = errorMsg
        };
    }

    private async Task<BenchmarkVerificationResult> VerifyTaskReviewAsync(
        BenchmarkInstance instance,
        BenchmarkAgentOutcome outcome,
        BenchmarkJudgeConfig? judgeConfig,
        CancellationToken cancellationToken)
    {
        if (!BenchmarkDatasetLoader.TryParseTaskReviewVerdict(instance.ExpectedVerdict, out var expectedVerdict))
        {
            throw new BenchmarkVerificationException(
                $"Invalid ExpectedVerdict '{instance.ExpectedVerdict}' for TaskReview instance '{instance.InstanceId}'.",
                instance.InstanceId);
        }

        var actualVerdict = outcome.RunStatus == AgentRunStatus.WaitingForHuman
            ? TaskReviewVerdict.NeedsClarification
            : TaskReviewVerdict.Ready;

        bool scopeValid = outcome.ModifiedFilesOutsideDocsTasks.Count == 0;
        bool frontmatterPreserved = true;
        bool nonTrivialEnrichment = true;
        bool difficultyPassed = true;
        double difficultyScore = 1.0;
        string? detectedDifficulty = null;
        var failureReasons = new List<string>();

        if (!scopeValid)
        {
            failureReasons.Add($"Modified files outside docs/tasks/: {string.Join(", ", outcome.ModifiedFilesOutsideDocsTasks)}");
        }

        if (actualVerdict == TaskReviewVerdict.Ready)
        {
            var (beforeFm, beforeBody) = ParseTaskMarkdownSafe(outcome.TaskMarkdownBefore);
            var (afterFm, afterBody) = ParseTaskMarkdownSafe(outcome.UpdatedTaskMarkdown);

            if (afterFm == null || string.IsNullOrWhiteSpace(outcome.UpdatedTaskMarkdown))
            {
                frontmatterPreserved = false;
                nonTrivialEnrichment = false;
                failureReasons.Add("Task markdown specification was missing or empty on completion.");
            }
            else
            {
                if (beforeFm?.Id != null && afterFm.Id != beforeFm.Id)
                {
                    frontmatterPreserved = false;
                    failureReasons.Add($"YAML frontmatter 'id' changed from {beforeFm.Id} to {afterFm.Id}.");
                }

                if (!string.IsNullOrWhiteSpace(beforeFm?.Status) &&
                    !string.Equals(beforeFm.Status.Trim(), afterFm.Status?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    frontmatterPreserved = false;
                    failureReasons.Add($"YAML frontmatter 'status' changed from '{beforeFm.Status}' to '{afterFm.Status}'.");
                }

                var trimmedBefore = (beforeBody ?? string.Empty).Trim();
                var trimmedAfter = (afterBody ?? string.Empty).Trim();
                bool bodyChanged = !string.Equals(trimmedBefore, trimmedAfter, StringComparison.Ordinal);
                bool hasSufficientLength = trimmedAfter.Length >= trimmedBefore.Length + BenchmarkDefaults.MinimumEnrichedBodyDeltaChars &&
                                           trimmedAfter.Length >= BenchmarkDefaults.MinimumReadyBodyChars;
                nonTrivialEnrichment = bodyChanged && hasSufficientLength;
                if (!nonTrivialEnrichment)
                {
                    failureReasons.Add("Task specification lacked non-trivial architectural/plan enrichment.");
                }

                detectedDifficulty = afterFm.Difficulty?.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(instance.ExpectedDifficulty))
                {
                    (difficultyPassed, difficultyScore) = EvaluateTaskDifficulty(instance.ExpectedDifficulty, detectedDifficulty);
                    if (!difficultyPassed)
                    {
                        failureReasons.Add($"Expected difficulty '{instance.ExpectedDifficulty}' but got '{detectedDifficulty ?? "<none>"}'.");
                    }
                }
            }
        }

        var questionText = string.Join(
            "\n",
            outcome.PendingQuestions.Select(q =>
            {
                var optionsText = ExtractQuestionOptionsText(q.OptionsJson);
                return string.IsNullOrWhiteSpace(optionsText)
                    ? $"{q.Prompt} {q.Rationale}"
                    : $"{q.Prompt} {q.Rationale} {optionsText}";
            }));
        var combinedCorpus = string.Join(
            "\n",
            outcome.UpdatedTaskMarkdown ?? string.Empty,
            questionText,
            outcome.Summary ?? string.Empty);

        var (anchorScore, missingAnchors) = EvaluateExpectedFindings(instance.ExpectedFindings, combinedCorpus);
        if (instance.ExpectedFindings.Count > 0 && anchorScore < MinimumAnchorCoverageForPass)
        {
            failureReasons.Add($"Missing expected finding anchors: {string.Join("; ", missingAnchors)}");
        }

        bool verdictMatched = actualVerdict == expectedVerdict;
        if (!verdictMatched)
        {
            failureReasons.Insert(0, $"Expected verdict '{expectedVerdict}' but got '{actualVerdict}'.");
        }

        bool passed = verdictMatched &&
                      scopeValid &&
                      frontmatterPreserved &&
                      nonTrivialEnrichment &&
                      difficultyPassed &&
                      (instance.ExpectedFindings.Count == 0 || anchorScore >= MinimumAnchorCoverageForPass);

        double deterministicScore;
        if (!scopeValid)
        {
            deterministicScore = 0.0;
        }
        else if (!verdictMatched)
        {
            deterministicScore = instance.ExpectedFindings.Count > 0
                ? anchorScore * WrongVerdictAnchorWeight
                : 0.0;
        }
        else
        {
            var checks = new List<double>
            {
                1.0
            };

            if (actualVerdict == TaskReviewVerdict.Ready)
            {
                checks.Add(frontmatterPreserved ? 1.0 : 0.0);
                checks.Add(nonTrivialEnrichment ? 1.0 : 0.0);
                if (!string.IsNullOrWhiteSpace(instance.ExpectedDifficulty))
                {
                    checks.Add(difficultyScore);
                }
            }

            if (instance.ExpectedFindings.Count > 0)
            {
                checks.Add(anchorScore);
            }

            deterministicScore = checks.Average();
        }

        BenchmarkJudgeResult? judgeResult = null;
        if (judgeConfig != null)
        {
            judgeResult = await _llmJudge.EvaluateTaskReviewAsync(instance, outcome, judgeConfig, cancellationToken).ConfigureAwait(false);
        }

        return new BenchmarkVerificationResult
        {
            Passed = passed,
            ActualVerdict = actualVerdict.ToString(),
            DeterministicScore = deterministicScore,
            TaskDifficultyResult = detectedDifficulty,
            JudgeScore = judgeResult?.Score,
            JudgeRationale = judgeResult?.Rationale,
            ErrorMessage = passed ? null : string.Join(" ", failureReasons)
        };
    }

    internal static double ComputeImplementationDeterministicScore(
        int failToPassPassed,
        int failToPassTotal,
        int passToPassPassed,
        int passToPassTotal,
        int exitCode)
    {
        if (failToPassTotal > 0 && passToPassTotal > 0)
        {
            return ((double)failToPassPassed / failToPassTotal) * ((double)passToPassPassed / passToPassTotal);
        }

        if (failToPassTotal > 0)
        {
            return (double)failToPassPassed / failToPassTotal;
        }

        if (passToPassTotal > 0)
        {
            return (double)passToPassPassed / passToPassTotal;
        }

        return exitCode == 0 ? 1.0 : 0.0;
    }

    internal static CodeReviewVerdict ClassifyCodeReviewVerdict(BenchmarkAgentOutcome outcome)
    {
        if (!string.IsNullOrWhiteSpace(outcome.ReviewerFeedback) ||
            !string.IsNullOrWhiteSpace(outcome.ReviewerRejectionReason) ||
            string.Equals(outcome.TargetRouting, StandardStageIds.Implementation, StringComparison.OrdinalIgnoreCase))
        {
            return CodeReviewVerdict.RequestChanges;
        }

        if (!string.IsNullOrWhiteSpace(outcome.ModelPatch) &&
            !IsWhitespaceOnlyPatch(outcome.ModelPatch))
        {
            return CodeReviewVerdict.MinorFix;
        }

        return CodeReviewVerdict.Approve;
    }

    internal static (bool Passed, double Score) EvaluateTaskDifficulty(string? expectedDifficulty, string? actualDifficulty)
    {
        if (string.IsNullOrWhiteSpace(expectedDifficulty))
        {
            return (true, 1.0);
        }

        if (!TryParseTaskDifficulty(expectedDifficulty, out var expectedTier))
        {
            bool exactText = string.Equals(actualDifficulty?.Trim(), expectedDifficulty.Trim(), StringComparison.OrdinalIgnoreCase);
            return (exactText, exactText ? 1.0 : 0.0);
        }

        if (!TryParseTaskDifficulty(actualDifficulty, out var actualTier))
        {
            return (false, 0.0);
        }

        if (expectedTier == actualTier)
        {
            return (true, 1.0);
        }

        if ((expectedTier == TaskDifficulty.Expert && actualTier == TaskDifficulty.Difficult) ||
            (expectedTier == TaskDifficulty.Difficult && actualTier == TaskDifficulty.Expert))
        {
            return (true, AdjacentEscalatedDifficultyCredit);
        }

        return (false, 0.0);
    }

    private static bool TryParseTaskDifficulty(string? value, out TaskDifficulty tier)
    {
        tier = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.Length > 0 &&
               char.IsLetter(trimmed[0]) &&
               Enum.TryParse(trimmed, ignoreCase: true, out tier) &&
               Enum.IsDefined(tier);
    }

    private static readonly HashSet<string> IndentationSensitiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py",
        ".pyi",
        ".yaml",
        ".yml",
        ".coffee",
        ".haml",
        ".pug",
        ".sass"
    };

    private sealed class NormalizedCodeAccumulator
    {
        public StringBuilder Buffer { get; } = new();
        public bool PendingSpace { get; set; }
        public char LastChar { get; set; }

        public void AppendCodeChar(char ch)
        {
            if (PendingSpace && Buffer.Length > 0 && IsWordChar(LastChar) && IsWordChar(ch))
            {
                Buffer.Append(' ');
            }

            PendingSpace = false;
            Buffer.Append(ch);
            LastChar = ch;
        }

        public void AppendVerbatim(ReadOnlySpan<char> span)
        {
            PendingSpace = false;
            Buffer.Append(span);
            if (!span.IsEmpty)
            {
                LastChar = span[^1];
            }
        }

        public void ResetTokenBoundary()
        {
            PendingSpace = false;
            LastChar = '\0';
        }

        public void Clear()
        {
            Buffer.Clear();
            ResetTokenBoundary();
        }

        private static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
    }

    internal static bool IsWhitespaceOnlyPatch(string? patch)
    {
        if (string.IsNullOrWhiteSpace(patch))
        {
            return true;
        }

        var normalized = patch.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var removedAcc = new NormalizedCodeAccumulator();
        var addedAcc = new NormalizedCodeAccumulator();
        bool sawAnyDiffLine = false;
        bool inHunk = false;
        string? currentFilePath = null;
        bool isIndentationSensitive = false;
        bool isRustFile = false;

        foreach (var line in normalized.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (!FlushHunkComparison(removedAcc, addedAcc))
                {
                    return false;
                }

                inHunk = false;
                currentFilePath = TryExtractDiffGitPath(line);
                isIndentationSensitive = IsIndentationSensitivePath(currentFilePath);
                isRustFile = IsRustPath(currentFilePath);
                continue;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                if (!FlushHunkComparison(removedAcc, addedAcc))
                {
                    return false;
                }

                inHunk = true;
                continue;
            }

            if (!inHunk)
            {
                if (line.StartsWith("new file mode ", StringComparison.Ordinal) ||
                    line.StartsWith("deleted file mode ", StringComparison.Ordinal) ||
                    line.StartsWith("old mode ", StringComparison.Ordinal) ||
                    line.StartsWith("new mode ", StringComparison.Ordinal) ||
                    line.StartsWith("rename from ", StringComparison.Ordinal) ||
                    line.StartsWith("rename to ", StringComparison.Ordinal) ||
                    line.StartsWith("copy from ", StringComparison.Ordinal) ||
                    line.StartsWith("copy to ", StringComparison.Ordinal) ||
                    line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                    line.StartsWith("GIT binary patch", StringComparison.Ordinal))
                {
                    return false;
                }

                if (line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    var extracted = TryExtractDiffHeaderPath(line.AsSpan(4), "b/");
                    if (!string.IsNullOrEmpty(extracted))
                    {
                        currentFilePath = extracted;
                        isIndentationSensitive = IsIndentationSensitivePath(currentFilePath);
                        isRustFile = IsRustPath(currentFilePath);
                    }
                    continue;
                }

                if (line.StartsWith("--- ", StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(currentFilePath))
                    {
                        var extracted = TryExtractDiffHeaderPath(line.AsSpan(4), "a/");
                        if (!string.IsNullOrEmpty(extracted))
                        {
                            currentFilePath = extracted;
                            isIndentationSensitive = IsIndentationSensitivePath(currentFilePath);
                            isRustFile = IsRustPath(currentFilePath);
                        }
                    }
                    continue;
                }

                if (line.StartsWith("index ", StringComparison.Ordinal) ||
                    line.StartsWith("\\ No newline at end of file", StringComparison.Ordinal) ||
                    line.StartsWith("similarity index ", StringComparison.Ordinal) ||
                    line.StartsWith("dissimilarity index ", StringComparison.Ordinal) ||
                    string.IsNullOrEmpty(line))
                {
                    continue;
                }

                return false;
            }

            if (line.StartsWith('\\'))
            {
                continue;
            }

            if (line.Length == 0 || line[0] == ' ')
            {
                if (!FlushHunkComparison(removedAcc, addedAcc))
                {
                    return false;
                }
                continue;
            }

            if (line[0] == '-')
            {
                AppendNormalizedLine(line.AsSpan(1), removedAcc, isIndentationSensitive, isRustFile);
                sawAnyDiffLine = true;
            }
            else if (line[0] == '+')
            {
                AppendNormalizedLine(line.AsSpan(1), addedAcc, isIndentationSensitive, isRustFile);
                sawAnyDiffLine = true;
            }
            else
            {
                if (!FlushHunkComparison(removedAcc, addedAcc))
                {
                    return false;
                }
                return false;
            }
        }

        if (!FlushHunkComparison(removedAcc, addedAcc))
        {
            return false;
        }

        return sawAnyDiffLine;
    }

    private static bool FlushHunkComparison(NormalizedCodeAccumulator removedAcc, NormalizedCodeAccumulator addedAcc)
    {
        if (removedAcc.Buffer.Length != addedAcc.Buffer.Length ||
            !string.Equals(removedAcc.Buffer.ToString(), addedAcc.Buffer.ToString(), StringComparison.Ordinal))
        {
            return false;
        }

        removedAcc.Clear();
        addedAcc.Clear();
        return true;
    }

    private static void AppendNormalizedLine(
        ReadOnlySpan<char> line,
        NormalizedCodeAccumulator target,
        bool isIndentationSensitive,
        bool isRustFile)
    {
        if (isIndentationSensitive)
        {
            var trimmedEnd = line.TrimEnd();
            if (trimmedEnd.IsEmpty)
            {
                return;
            }

            int indentLen = 0;
            while (indentLen < trimmedEnd.Length && char.IsWhiteSpace(trimmedEnd[indentLen]))
            {
                indentLen++;
            }

            target.ResetTokenBoundary();
            target.Buffer.Append(trimmedEnd[..indentLen]);
            AppendNormalizedContent(trimmedEnd[indentLen..], target, isRustFile);
            target.Buffer.Append('\n');
            target.ResetTokenBoundary();
            return;
        }

        AppendNormalizedContent(line, target, isRustFile);
        target.PendingSpace = true;
    }

    private static void AppendNormalizedContent(ReadOnlySpan<char> chars, NormalizedCodeAccumulator target, bool isRustFile)
    {
        for (int i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            if (ch == '"')
            {
                if (TryFindClosingDelimiter(chars, i + 1, '"', out int closeIdx))
                {
                    target.AppendVerbatim(chars.Slice(i, closeIdx - i + 1));
                    i = closeIdx;
                }
                else
                {
                    target.AppendVerbatim(chars[i..]);
                    break;
                }
                continue;
            }

            if (ch == '`')
            {
                if (TryFindClosingDelimiter(chars, i + 1, '`', out int closeIdx))
                {
                    target.AppendVerbatim(chars.Slice(i, closeIdx - i + 1));
                    i = closeIdx;
                    continue;
                }

                target.AppendCodeChar(ch);
                continue;
            }

            if (ch == '\'')
            {
                bool matchedQuote = isRustFile
                    ? TryFindRustCharLiteralEnd(chars, i, out int closeIdx)
                    : TryFindClosingDelimiter(chars, i + 1, '\'', out closeIdx);

                if (matchedQuote)
                {
                    target.AppendVerbatim(chars.Slice(i, closeIdx - i + 1));
                    i = closeIdx;
                    continue;
                }

                target.AppendCodeChar(ch);
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                target.PendingSpace = true;
                continue;
            }

            target.AppendCodeChar(ch);
        }
    }

    private static bool TryFindRustCharLiteralEnd(ReadOnlySpan<char> chars, int quoteIndex, out int closingIndex)
    {
        if (quoteIndex + 2 < chars.Length &&
            chars[quoteIndex + 1] != '\\' &&
            chars[quoteIndex + 1] != '\'' &&
            chars[quoteIndex + 2] == '\'')
        {
            closingIndex = quoteIndex + 2;
            return true;
        }

        if (quoteIndex + 1 < chars.Length && chars[quoteIndex + 1] == '\\' &&
            TryFindClosingDelimiter(chars, quoteIndex + 1, '\'', out closingIndex))
        {
            var inner = chars.Slice(quoteIndex + 2, closingIndex - quoteIndex - 2);
            if (!inner.IsEmpty && !inner.ToString().Any(char.IsWhiteSpace))
            {
                return true;
            }
        }

        closingIndex = -1;
        return false;
    }

    private static bool TryFindClosingDelimiter(ReadOnlySpan<char> chars, int startIndex, char delimiter, out int closingIndex)
    {
        bool escaped = false;
        for (int i = startIndex; i < chars.Length; i++)
        {
            var ch = chars[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == delimiter)
            {
                closingIndex = i;
                return true;
            }
        }

        closingIndex = -1;
        return false;
    }

    private static string? TryExtractDiffHeaderPath(ReadOnlySpan<char> raw, string stripPrefix)
    {
        var tabIdx = raw.IndexOf('\t');
        if (tabIdx >= 0)
        {
            raw = raw[..tabIdx];
        }

        var path = raw.Trim().ToString();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
        {
            path = path[1..^1];
        }

        if (string.Equals(path, "/dev/null", StringComparison.Ordinal))
        {
            return null;
        }

        if (path.StartsWith(stripPrefix, StringComparison.Ordinal))
        {
            path = path[stripPrefix.Length..];
        }

        return path;
    }

    private static string? TryExtractDiffGitPath(string line)
    {
        const string prefix = "diff --git ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var bIdx = line.LastIndexOf(" b/", StringComparison.Ordinal);
        if (bIdx >= 0)
        {
            var path = line[(bIdx + 3)..].Trim();
            if (path.EndsWith('"') && path.StartsWith('"') && path.Length >= 2)
            {
                path = path[1..^1];
            }
            return path;
        }

        return null;
    }

    private static bool IsIndentationSensitivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && IndentationSensitiveExtensions.Contains(ext);
    }

    private static bool IsRustPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(Path.GetExtension(path), ".rs", StringComparison.OrdinalIgnoreCase);

    internal static (double Score, IReadOnlyList<string> MissingDescriptions) EvaluateExpectedFindings(
        IReadOnlyList<BenchmarkExpectedFinding> findings,
        string corpus)
    {
        if (findings == null || findings.Count == 0)
        {
            return (1.0, Array.Empty<string>());
        }

        var normalizedCorpus = (corpus ?? string.Empty).Replace('\\', '/');
        int matchedCount = 0;
        var missing = new List<string>();

        foreach (var finding in findings)
        {
            bool hasFile = !string.IsNullOrWhiteSpace(finding.FilePath);
            bool hasSymbol = !string.IsNullOrWhiteSpace(finding.SymbolOrLineHint);
            var nonEmptyKeywords = finding.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
            bool hasKeywords = nonEmptyKeywords.Count > 0;

            bool fileMatched = true;
            if (hasFile)
            {
                var normFile = finding.FilePath!.Replace('\\', '/').TrimStart('/');
                var fileName = Path.GetFileName(normFile);
                fileMatched = normalizedCorpus.Contains(normFile, StringComparison.OrdinalIgnoreCase) ||
                              (!string.IsNullOrWhiteSpace(fileName) && normalizedCorpus.Contains(fileName, StringComparison.OrdinalIgnoreCase));
            }

            bool symbolMatched = !hasSymbol ||
                                 normalizedCorpus.Contains(finding.SymbolOrLineHint!.Trim(), StringComparison.OrdinalIgnoreCase);

            bool keywordsMatched = !hasKeywords ||
                                   nonEmptyKeywords.Any(k => normalizedCorpus.Contains(k.Trim(), StringComparison.OrdinalIgnoreCase));

            bool summaryMatched = true;
            if (!hasFile && !hasSymbol && !hasKeywords)
            {
                summaryMatched = MatchesSummaryAnchor(finding.Summary, normalizedCorpus);
            }

            bool hasStrongKeywordCoverage = false;
            if (hasFile && hasKeywords)
            {
                var (independentTotal, independentMatched) = CountIndependentKeywords(nonEmptyKeywords, normalizedCorpus);
                hasStrongKeywordCoverage = independentTotal >= 2 &&
                                           independentMatched >= Math.Max(2, (independentTotal + 1) / 2);
            }

            bool fileStemMatched = hasFile && !fileMatched && MatchesIdentifierBoundedFileStem(finding.FilePath, normalizedCorpus);
            bool strongCorroboratedMatch = hasFile && hasStrongKeywordCoverage &&
                                           (hasSymbol
                                               ? (symbolMatched || fileMatched || fileStemMatched)
                                               : (fileMatched || fileStemMatched));

            if (((fileMatched && symbolMatched && keywordsMatched) || strongCorroboratedMatch) && summaryMatched)
            {
                matchedCount++;
            }
            else
            {
                missing.Add(finding.FilePath ?? finding.SymbolOrLineHint ?? finding.Summary ?? "finding");
            }
        }

        return ((double)matchedCount / findings.Count, missing);
    }

    internal static string ExtractQuestionOptionsText(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
        {
            return string.Empty;
        }

        var trimmed = optionsJson.Trim();
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var parts = new List<string>();
            CollectOptionText(doc.RootElement, parts);
            return parts.Count > 0 ? string.Join(", ", parts) : trimmed;
        }
        catch (JsonException)
        {
            return trimmed;
        }
    }

    private static void CollectOptionText(JsonElement element, List<string> parts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
            {
                var text = element.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(text);
                }
                break;
            }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectOptionText(item, parts);
                }
                break;
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var val = prop.Value.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            parts.Add(val);
                        }
                    }
                    else if (prop.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    {
                        CollectOptionText(prop.Value, parts);
                    }
                }
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                parts.Add(element.ToString());
                break;
        }
    }

    private static bool MatchesIdentifierBoundedFileStem(string? filePath, string normalizedCorpus)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(normalizedCorpus))
        {
            return false;
        }

        var normFile = filePath.Replace('\\', '/').TrimStart('/');
        var fileName = Path.GetFileName(normFile);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (IsEligibleStemForCorroboration(stem) && ContainsIdentifierBoundedToken(normalizedCorpus, stem))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(stem) && (stem.Contains('_', StringComparison.Ordinal) || stem.Contains('-', StringComparison.Ordinal)))
        {
            var compactStem = stem.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
            if (IsEligibleStemForCorroboration(compactStem) && ContainsIdentifierBoundedToken(normalizedCorpus, compactStem))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEligibleStemForCorroboration(string? stem) =>
        !string.IsNullOrWhiteSpace(stem) &&
        stem.Length >= MinimumFileStemCorroborationLength &&
        !GenericFileStems.Contains(stem);

    private static bool ContainsIdentifierBoundedToken(string normalizedCorpus, string token)
    {
        int searchIndex = 0;
        while (searchIndex <= normalizedCorpus.Length - token.Length)
        {
            int idx = normalizedCorpus.IndexOf(token, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            bool startBounded = idx == 0 || !IsIdentifierChar(normalizedCorpus[idx - 1]);
            int endIdx = idx + token.Length;
            bool endBounded = endIdx == normalizedCorpus.Length || !IsIdentifierChar(normalizedCorpus[endIdx]);
            if (startBounded && endBounded)
            {
                return true;
            }

            searchIndex = idx + 1;
        }

        return false;
    }

    private static bool IsIdentifierChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

    private static (int Total, int Matched) CountIndependentKeywords(
        IReadOnlyList<string> keywords,
        string normalizedCorpus)
    {
        var uniqueKeywords = keywords
            .Select(k => k.Trim())
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Drop superstring variants (e.g., "tests" when "test" is present) so one word cannot satisfy multiple slots.
        var independent = uniqueKeywords
            .Where(candidate => !uniqueKeywords.Any(other =>
                other.Length < candidate.Length &&
                candidate.Contains(other, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        int matched = independent.Count(k => normalizedCorpus.Contains(k, StringComparison.OrdinalIgnoreCase));
        return (independent.Count, matched);
    }

    private static bool MatchesSummaryAnchor(string? summary, string normalizedCorpus)
    {
        if (string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(normalizedCorpus))
        {
            return false;
        }

        var trimmedSummary = summary.Trim();
        if (normalizedCorpus.Contains(trimmedSummary, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var tokens = SummaryTokenRegex().Matches(trimmedSummary)
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tokens.Count == 0)
        {
            return false;
        }

        int requiredMatches = (tokens.Count + 1) / 2;
        int matchedTokens = tokens.Count(t => normalizedCorpus.Contains(t, StringComparison.OrdinalIgnoreCase));
        return matchedTokens >= requiredMatches;
    }

    internal static string ResolveTestCommand(
        BenchmarkInstance instance,
        string workspacePath,
        ExecutionMode executionMode = ExecutionMode.ContainerSandbox,
        bool failToPassOnly = false) =>
        BenchmarkTestCommandResolver.ResolveTestCommand(instance, workspacePath, executionMode, failToPassOnly);

    private IReadOnlyList<ParsedTestOutcome> ParseStructuredOutcomes(
        TestResultFormat format,
        string resultsHostDir,
        string workspacePath,
        string combinedOutput,
        DateTime testRunStartedUtc,
        string instanceId)
    {
        switch (format)
        {
            case TestResultFormat.Trx:
            {
                var trxFiles = FindResultFiles(resultsHostDir, workspacePath, "*.trx", testRunStartedUtc);
                return ParseXmlFilesSafely(trxFiles, TrxTestResultParser.ParseFile, "TRX", instanceId);
            }
            case TestResultFormat.JUnitXml:
            {
                var xmlFiles = FindResultFiles(resultsHostDir, workspacePath, "*.xml", testRunStartedUtc);
                return ParseXmlFilesSafely(xmlFiles, JUnitXmlTestResultParser.ParseFile, "JUnit XML", instanceId);
            }
            case TestResultFormat.CargoTest:
                return CargoTestResultParser.Parse(combinedOutput);
            default:
                return Array.Empty<ParsedTestOutcome>();
        }
    }

    private IReadOnlyList<ParsedTestOutcome> ParseXmlFilesSafely(
        IReadOnlyList<string> files,
        Func<string, IReadOnlyList<ParsedTestOutcome>> parseFunc,
        string formatLabel,
        string instanceId)
    {
        var outcomes = new List<ParsedTestOutcome>();
        foreach (var file in files)
        {
            try
            {
                outcomes.AddRange(parseFunc(file));
            }
            catch (XmlException ex)
            {
                _logger.LogWarning(ex, "Skipping corrupt or truncated {Format} result file '{File}' for instance '{InstanceId}'.",
                    formatLabel, file, instanceId);
            }
        }

        return outcomes;
    }

    private static readonly HashSet<string> SkippedResultScanDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "node_modules",
        ".venv",
        "__pycache__",
        ".pytest_cache",
        "bin",
        "obj"
    };

    private IReadOnlyList<string> FindResultFiles(
        string resultsHostDir,
        string workspacePath,
        string searchPattern,
        DateTime testRunStartedUtc)
    {
        if (Directory.Exists(resultsHostDir))
        {
            var direct = Directory.GetFiles(resultsHostDir, searchPattern, SearchOption.AllDirectories);
            if (direct.Length > 0)
            {
                return direct;
            }
        }

        var cutoffUtc = testRunStartedUtc.AddSeconds(-2);
        var testResultsDir = Path.Combine(workspacePath, "TestResults");
        if (Directory.Exists(testResultsDir))
        {
            var testResultsFiles = Directory.GetFiles(testResultsDir, searchPattern, SearchOption.AllDirectories)
                .Where(f => File.GetLastWriteTimeUtc(f) >= cutoffUtc)
                .ToArray();
            if (testResultsFiles.Length > 0)
            {
                return testResultsFiles;
            }
        }

        if (Directory.Exists(workspacePath))
        {
            return EnumerateFilesSkippingBuildDirs(workspacePath, searchPattern)
                .Where(f => File.GetLastWriteTimeUtc(f) >= cutoffUtc)
                .ToArray();
        }

        return Array.Empty<string>();
    }

    private IEnumerable<string> EnumerateFilesSkippingBuildDirs(string rootDir, string searchPattern)
    {
        var pending = new Stack<string>();
        pending.Push(rootDir);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current, searchPattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Skipping inaccessible directory '{Directory}' while scanning for test result files.", current);
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Skipping inaccessible subdirectories under '{Directory}' while scanning for test result files.", current);
                continue;
            }

            foreach (var subDir in subDirs)
            {
                var dirName = Path.GetFileName(subDir);
                if (!SkippedResultScanDirectories.Contains(dirName))
                {
                    pending.Push(subDir);
                }
            }
        }
    }

    internal static SandboxScopeDescriptor CreateVerificationScope(
        string workspacePath,
        string? customContainerImage,
        ExecutionMode executionMode,
        string containerName)
    {
        bool isWslUnc = WslPathConverter.TryExtractWslUnc(workspacePath, out var wslDistro, out var wslLinuxPath);
        var mountTable = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: workspacePath,
            wslWorkingDirectory: isWslUnc ? wslLinuxPath : null,
            isReadOnly: false);

        return new SandboxScopeDescriptor(
            scopeKind: SandboxScopeKind.WorkspaceClone,
            executionMode: executionMode,
            securityTier: SandboxSecurityTier.Standard,
            mountTable: mountTable,
            workspaceId: null,
            taskId: null,
            wslDistribution: isWslUnc && !string.IsNullOrWhiteSpace(wslDistro) ? wslDistro : null,
            containerImage: customContainerImage,
            containerName: containerName,
            useWslForContainers: isWslUnc,
            repositoryPath: workspacePath,
            ext4MirrorPath: isWslUnc ? wslLinuxPath : null,
            virtualRootPath: SandboxMountTable.WorkspaceVirtualRoot,
            defaultWorkingDirectory: SandboxMountTable.WorkspaceVirtualRoot,
            scopeCacheKey: $"bench-verify:{containerName}",
            hostWorkspacePath: workspacePath,
            canonicalClonePath: null,
            mountHostDotnetTools: false,
            protectCloneWorkingTree: false,
            disableNetwork: false,
            blockedHosts: BenchmarkDefaults.BlockedAntiCheatHosts,
            mountAcpHostPaths: false);
    }

    private (TaskFrontmatter? Frontmatter, string? Body) ParseTaskMarkdownSafe(string? markdown) =>
        ParseTaskMarkdown(markdown, _logger);

    internal static (TaskFrontmatter? Frontmatter, string? Body) ParseTaskMarkdown(string? markdown, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return (null, null);
        }

        var normalized = markdown.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').TrimStart();
        var lines = normalized.Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
        {
            return (null, normalized);
        }

        int closingIndex = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.Equals(lines[i].Trim(), "---", StringComparison.Ordinal))
            {
                closingIndex = i;
                break;
            }
        }

        if (closingIndex < 0)
        {
            return (null, normalized);
        }

        var yamlPart = string.Join('\n', lines, 1, closingIndex - 1).Trim();
        var bodyPart = closingIndex + 1 < lines.Length
            ? string.Join('\n', lines, closingIndex + 1, lines.Length - closingIndex - 1).TrimStart('\n')
            : string.Empty;

        var deserializer = TaskMarkdownSyncService.CreateYamlDeserializer();
        try
        {
            var fm = deserializer.Deserialize<TaskFrontmatter>(yamlPart) ?? new TaskFrontmatter();
            return (fm, bodyPart);
        }
        catch (YamlException ex)
        {
            logger?.LogDebug(ex, "Task markdown frontmatter YAML deserialization failed during benchmark verification.");
            return (null, bodyPart);
        }
    }

    private static string BuildCombinedCommandOutput(CommandResult result)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            sb.AppendLine(result.StandardOutput);
        }
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            sb.AppendLine("[STDERR]");
            sb.AppendLine(result.StandardError);
        }
        return sb.ToString();
    }
}
