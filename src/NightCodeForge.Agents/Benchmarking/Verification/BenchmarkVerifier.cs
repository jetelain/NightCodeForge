using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Benchmarking;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;
using NightCodeForge.Infrastructure.Execution;
using YamlDotNet.Core;

namespace NightCodeForge.Agents.Benchmarking.Verification;

/// <summary>
/// Verifies benchmark outcomes across Implementation (containerized multi-stack test verification),
/// Code Review, and ForgeTask Review (deterministic scoring + optional LLM-as-judge).
/// </summary>
public sealed partial class BenchmarkVerifier : IBenchmarkVerifier
{
    private const double ReviewVerdictWeightWithFindings = 0.6;
    private const double ReviewFindingsWeight = 0.4;
    private const double MinimumAnchorCoverageForPass = 0.5;

    [GeneratedRegex(@"[A-Za-z0-9_]{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex SummaryTokenRegex();

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

        await _workspaceManager.ResetToBaseAsync(workspace.WorkspacePath, workspace.BaseCommit, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(outcome.ModelPatch))
        {
            try
            {
                await _workspaceManager.ApplyPatchAsync(workspace.WorkspacePath, outcome.ModelPatch, cancellationToken).ConfigureAwait(false);
            }
            catch (BenchmarkWorkspaceException ex)
            {
                return new BenchmarkVerificationResult
                {
                    Passed = false,
                    DeterministicScore = 0.0,
                    FailToPassTotal = instance.FailToPass.Count,
                    PassToPassTotal = instance.PassToPass.Count,
                    FailedTests = instance.FailToPass.Concat(instance.PassToPass).ToList(),
                    ErrorMessage = $"Generated model patch failed to apply cleanly: {ex.Message}"
                };
            }
        }

        if (!string.IsNullOrWhiteSpace(instance.TestPatch))
        {
            await _workspaceManager.ApplyPatchAsync(workspace.WorkspacePath, instance.TestPatch, cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteTestSuiteAsync(instance, workspace.WorkspacePath, executionMode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BenchmarkVerificationResult> ExecuteTestSuiteAsync(
        BenchmarkInstance instance,
        string workspacePath,
        ExecutionMode executionMode,
        CancellationToken cancellationToken)
    {
        var resultsHostDir = Path.Combine(workspacePath, BenchmarkPaths.BenchmarkResultDirRelative);
        if (Directory.Exists(resultsHostDir))
        {
            Directory.Delete(resultsHostDir, recursive: true);
        }
        Directory.CreateDirectory(resultsHostDir);

        var containerName = $"ncf-bench-verify-{Guid.NewGuid():N}"[..32];
        var scope = CreateVerificationScope(workspacePath, instance.CustomContainerImage, executionMode, containerName);
        var testCommand = ResolveTestCommand(instance, workspacePath, executionMode);
        var format = instance.ResolveEffectiveTestResultFormat();
        var testRunStartedUtc = DateTime.UtcNow;

        CommandResult cmdResult;
        try
        {
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
                cmdResult = await env.ExecuteAsync(
                    testCommand,
                    workingDirectory: executionMode == ExecutionMode.ContainerSandbox ? null : workspacePath,
                    timeoutSeconds: BenchmarkDefaults.DefaultVerificationTimeoutSeconds,
                    environmentVariables: null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
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

        var combinedOutput = BuildCombinedCommandOutput(cmdResult);
        var parsedOutcomes = ParseStructuredOutcomes(format, resultsHostDir, workspacePath, combinedOutput, testRunStartedUtc, instance.InstanceId);

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
        int totalAssertions = eval.FailToPassTotal + eval.PassToPassTotal;
        double score = totalAssertions > 0
            ? (double)(eval.FailToPassPassed + eval.PassToPassPassed) / totalAssertions
            : (cmdResult.ExitCode == 0 ? 1.0 : 0.0);

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
            ErrorMessage = eval.AllPassed ? null : $"Failed tests: {string.Join(", ", eval.FailedTests)}"
        };
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
            outcome.Summary ?? string.Empty,
            outcome.ModelPatch ?? string.Empty);

        var (anchorScore, missingAnchors) = EvaluateExpectedFindings(instance.ExpectedFindings, combinedCorpus);

        bool minorFixTestsPassed = true;
        int f2pPassed = 0, f2pTotal = instance.FailToPass.Count;
        int p2pPassed = 0, p2pTotal = instance.PassToPass.Count;
        IReadOnlyList<string> failedTests = Array.Empty<string>();
        string? testLog = null;
        string? testError = null;

        if (actualVerdict == CodeReviewVerdict.MinorFix &&
            verifyWithTests &&
            (!string.IsNullOrWhiteSpace(instance.TestPatch) || instance.FailToPass.Count > 0 || instance.PassToPass.Count > 0))
        {
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
                if (!string.IsNullOrWhiteSpace(instance.TestPatch))
                {
                    await _workspaceManager.ApplyPatchAsync(workspace.WorkspacePath, instance.TestPatch, cancellationToken).ConfigureAwait(false);
                }

                var testRes = await ExecuteTestSuiteAsync(instance, workspace.WorkspacePath, executionMode, cancellationToken).ConfigureAwait(false);
                minorFixTestsPassed = testRes.Passed;
                f2pPassed = testRes.FailToPassPassed;
                p2pPassed = testRes.PassToPassPassed;
                failedTests = testRes.FailedTests;
                testLog = testRes.VerificationLog;
                testError = testRes.ErrorMessage;
            }
        }

        double verdictComponent = verdictMatched ? 1.0 : 0.0;
        double deterministicScore = instance.ExpectedFindings.Count > 0
            ? (ReviewVerdictWeightWithFindings * verdictComponent) + (ReviewFindingsWeight * anchorScore)
            : verdictComponent;
        if (!minorFixTestsPassed)
        {
            deterministicScore *= 0.5;
        }

        bool passed = verdictMatched &&
                      (instance.ExpectedFindings.Count == 0 || anchorScore >= MinimumAnchorCoverageForPass) &&
                      minorFixTestsPassed;

        BenchmarkJudgeResult? judgeResult = null;
        if (judgeConfig != null)
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
        bool difficultyMatched = true;
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
                    difficultyMatched = string.Equals(
                        detectedDifficulty,
                        instance.ExpectedDifficulty.Trim(),
                        StringComparison.OrdinalIgnoreCase);
                    if (!difficultyMatched)
                    {
                        failureReasons.Add($"Expected difficulty '{instance.ExpectedDifficulty}' but got '{detectedDifficulty ?? "<none>"}'.");
                    }
                }
            }
        }

        var questionText = string.Join("\n", outcome.PendingQuestions.Select(q => $"{q.Prompt} {q.Rationale}"));
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
                      difficultyMatched &&
                      (instance.ExpectedFindings.Count == 0 || anchorScore >= MinimumAnchorCoverageForPass);

        double deterministicScore;
        if (!scopeValid)
        {
            deterministicScore = 0.0;
        }
        else
        {
            var checks = new List<double>
            {
                verdictMatched ? 1.0 : 0.0
            };

            if (actualVerdict == TaskReviewVerdict.Ready)
            {
                checks.Add(frontmatterPreserved ? 1.0 : 0.0);
                checks.Add(nonTrivialEnrichment ? 1.0 : 0.0);
                if (!string.IsNullOrWhiteSpace(instance.ExpectedDifficulty))
                {
                    checks.Add(difficultyMatched ? 1.0 : 0.0);
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

    internal static CodeReviewVerdict ClassifyCodeReviewVerdict(BenchmarkAgentOutcome outcome)
    {
        if (!string.IsNullOrWhiteSpace(outcome.ReviewerFeedback) ||
            string.Equals(outcome.TargetRouting, StandardStageIds.Implementation, StringComparison.OrdinalIgnoreCase))
        {
            return CodeReviewVerdict.RequestChanges;
        }

        if (!string.IsNullOrWhiteSpace(outcome.ModelPatch))
        {
            return CodeReviewVerdict.MinorFix;
        }

        return CodeReviewVerdict.Approve;
    }

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

            if (fileMatched && symbolMatched && keywordsMatched && summaryMatched)
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
        ExecutionMode executionMode = ExecutionMode.ContainerSandbox)
    {
        if (!string.IsNullOrWhiteSpace(instance.CustomTestCommand))
        {
            return instance.CustomTestCommand.Trim();
        }

        var resultsDir = executionMode == ExecutionMode.ContainerSandbox
            ? BenchmarkPaths.ContainerResultsDir
            : BenchmarkPaths.BenchmarkResultDirRelative;

        return BenchmarkLanguages.Normalize(instance.Language) switch
        {
            BenchmarkLanguages.CSharp =>
                $"dotnet build --nologo && dotnet test --no-build --logger trx --results-directory {resultsDir}",
            BenchmarkLanguages.Python =>
                $"pytest --junitxml={resultsDir}/{BenchmarkPaths.JUnitFileName}",
            BenchmarkLanguages.TypeScript or BenchmarkLanguages.JavaScript =>
                ResolveNodeTestCommand(workspacePath, resultsDir),
            BenchmarkLanguages.Rust =>
                "cargo test --no-fail-fast",
            _ => throw new BenchmarkVerificationException(
                $"No default test command configured for language '{instance.Language}'. Provide custom_test_command.",
                instance.InstanceId)
        };
    }

    private static string ResolveNodeTestCommand(string workspacePath, string resultsDir)
    {
        var pkgJsonPath = Path.Combine(workspacePath, "package.json");
        if (File.Exists(pkgJsonPath))
        {
            var content = File.ReadAllText(pkgJsonPath);
            if (content.Contains("vitest", StringComparison.OrdinalIgnoreCase))
            {
                return $"npx vitest run --reporter=junit --outputFile={resultsDir}/{BenchmarkPaths.JUnitFileName}";
            }

            if (content.Contains("jest", StringComparison.OrdinalIgnoreCase))
            {
                return $"JEST_JUNIT_OUTPUT_DIR={resultsDir} JEST_JUNIT_OUTPUT_NAME={BenchmarkPaths.JUnitFileName} npx jest --ci --reporters=default --reporters=jest-junit";
            }
        }

        return "npm test";
    }

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

    private static IReadOnlyList<string> FindResultFiles(
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

        var testResultsDir = Path.Combine(workspacePath, "TestResults");
        if (Directory.Exists(testResultsDir))
        {
            var cutoffUtc = testRunStartedUtc.AddSeconds(-1);
            return Directory.GetFiles(testResultsDir, searchPattern, SearchOption.AllDirectories)
                .Where(f => File.GetLastWriteTimeUtc(f) >= cutoffUtc)
                .ToArray();
        }

        return Array.Empty<string>();
    }

    internal static SandboxScopeDescriptor CreateVerificationScope(
        string workspacePath,
        string? customContainerImage,
        ExecutionMode executionMode,
        string containerName)
    {
        var mountTable = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: workspacePath,
            isReadOnly: false);

        return new SandboxScopeDescriptor(
            scopeKind: SandboxScopeKind.WorkspaceClone,
            executionMode: executionMode,
            securityTier: SandboxSecurityTier.Standard,
            mountTable: mountTable,
            workspaceId: null,
            taskId: null,
            containerImage: customContainerImage,
            containerName: containerName,
            repositoryPath: workspacePath,
            virtualRootPath: SandboxMountTable.WorkspaceVirtualRoot,
            defaultWorkingDirectory: SandboxMountTable.WorkspaceVirtualRoot,
            scopeCacheKey: $"bench-verify:{containerName}",
            hostWorkspacePath: workspacePath,
            canonicalClonePath: null,
            mountHostDotnetTools: false,
            protectCloneWorkingTree: false);
    }

    private (TaskFrontmatter? Frontmatter, string? Body) ParseTaskMarkdownSafe(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return (null, null);
        }

        var normalized = markdown.TrimStart('\uFEFF').Replace("\r\n", "\n").TrimStart();
        if (!normalized.StartsWith("---", StringComparison.Ordinal))
        {
            return (null, normalized);
        }

        var secondFence = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (secondFence < 0)
        {
            return (null, normalized);
        }

        var yamlPart = normalized.Substring(3, secondFence - 3).Trim();
        var bodyPart = normalized[(secondFence + 4)..].TrimStart('\r', '\n');
        var deserializer = TaskMarkdownSyncService.CreateYamlDeserializer();
        try
        {
            var fm = deserializer.Deserialize<TaskFrontmatter>(yamlPart) ?? new TaskFrontmatter();
            return (fm, bodyPart);
        }
        catch (YamlException ex)
        {
            _logger.LogDebug(ex, "Task markdown frontmatter YAML deserialization failed during benchmark verification.");
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
