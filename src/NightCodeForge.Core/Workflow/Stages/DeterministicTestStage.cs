using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Deterministic workflow stage executing test runner (e.g. dotnet test) purely on CPU with 0 GPU tokens.
/// Parses total, passed, and failed test metrics and feeds them into PipelineDataBag for quality gates.
/// </summary>
public partial class DeterministicTestStage : IWorkflowStage
{
    public const string DefaultStageId = StageImplementationTypes.DeterministicTest;
    public const string FallbackLoopBackStageId = StandardStageIds.Implementation;

    [GeneratedRegex(@"(?:Total tests:[ \t]*(\d+)|Total:[ \t]*(\d+)|total[ \t]*:[ \t]*(\d+)|(\d+)[ \t]+total)", RegexOptions.IgnoreCase)]
    private static partial Regex TotalTestsRegex();

    [GeneratedRegex(@"(?:Failed:[ \t]*(\d+)|échec[ \t]*:[ \t]*(\d+)|(\d+)[ \t]+failed)", RegexOptions.IgnoreCase)]
    private static partial Regex FailedTestsRegex();

    [GeneratedRegex(@"(?:Passed:[ \t]*(\d+)|réussite[ \t]*:[ \t]*(\d+)|(\d+)[ \t]+passed)", RegexOptions.IgnoreCase)]
    private static partial Regex PassedTestsRegex();

    [GeneratedRegex(@"Total tests:[ \t]*(\d+)\.[ \t]*Passed:[ \t]*(\d+)\.[ \t]*Failed:[ \t]*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex VSTestGlobalSummaryRegex();

    [GeneratedRegex(@"(?:Failed\s+([A-Za-z0-9_.]+)|\[FAIL\]\s+(.+)|FAIL:\s+(.+))")]
    private static partial Regex TestFailureLineRegex();

    private readonly IExecutionEnvironmentProvider _envProvider;
    private readonly ILogger<DeterministicTestStage> _logger;
    private readonly string _testCommand;

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public StageQueueTier QueueTier { get; set; } = StageQueueTier.Implementation;
    public string LoopBackStageId { get; set; } = FallbackLoopBackStageId;

    /// <summary>
    /// When true, runs the stack's coverage-collecting command (CoverageCommand) instead of the plain test command.
    /// </summary>
    public bool CollectCoverage { get; init; }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public DeterministicTestStage(
        IExecutionEnvironmentProvider envProvider,
        ILogger<DeterministicTestStage> logger)
        : this(envProvider, logger, "dotnet test --no-build", DefaultStageId, "Deterministic Unit Test Gate")
    {
    }

    public DeterministicTestStage(IExecutionEnvironmentProvider envProvider)
        : this(envProvider, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeterministicTestStage>.Instance, "dotnet test --no-build", DefaultStageId, "Deterministic Unit Test Gate")
    {
    }

    public DeterministicTestStage(
        IExecutionEnvironmentProvider envProvider,
        string testCommand,
        string stageId,
        string displayName)
        : this(envProvider, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeterministicTestStage>.Instance, testCommand, stageId, displayName)
    {
    }

    public DeterministicTestStage(
        IExecutionEnvironmentProvider envProvider,
        ILogger<DeterministicTestStage> logger,
        string testCommand,
        string stageId,
        string displayName)
    {
        _envProvider = envProvider ?? throw new ArgumentNullException(nameof(envProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _testCommand = !string.IsNullOrWhiteSpace(testCommand) ? testCommand : "dotnet test --no-build";
        StageId = !string.IsNullOrWhiteSpace(stageId) ? stageId : DefaultStageId;
        DisplayName = !string.IsNullOrWhiteSpace(displayName) ? displayName : "Deterministic Unit Test Gate";
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var workspace = context.Workspace ?? throw new InvalidOperationException("Workspace is required to resolve execution environment.");

        var bag = context.DataBag;

        // Reset previous run test metrics and failure keys so stale data is never carried over
        bag.Remove(PipelineDataBagKeys.TestsCurrentTotal);
        bag.Remove(PipelineDataBagKeys.TestsCurrentPassed);
        bag.Remove(PipelineDataBagKeys.TestsCurrentFailed);
        bag.Remove(PipelineDataBagKeys.TestsCurrentDurationMs);
        bag.Remove(PipelineDataBagKeys.CoverageCurrentLineRate);
        bag.Remove(PipelineDataBagKeys.CoverageDelta);
        bag.Remove(PipelineDataBagKeys.FailedTests);
        bag.Remove(PipelineDataBagKeys.FailedTestsTruncated);
        bag.Remove(PipelineDataBagKeys.TestFailureReason);
        bag.Remove(PipelineDataBagKeys.CoverageFailureReason);

        var repoPath = workspace.RepositoryPath;
        var workDir = !string.IsNullOrWhiteSpace(context.WorkingDirectory)
            ? context.WorkingDirectory
            : (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)
                ? workspace.Ext4MirrorPath
                : repoPath);

        var config = CodeStackPresets.ResolveConfig(repoPath, bag);
        var command = CollectCoverage
            ? (config.CoverageCommand ?? _testCommand)
            : (config.TestCommand ?? _testCommand);

        var effectiveEnv = _envProvider.GetEnvironment(workspace, SandboxSecurityTier.Standard, context.ForgeTask?.Id)
            ?? throw new InvalidOperationException($"No execution environment could be resolved for workspace #{workspace.Id}.");

        var timeout = workspace.TestTimeoutSeconds ?? config.TestTimeoutSeconds ?? WorkflowDefaults.DefaultTestTimeoutSeconds;
        bag.Set(PipelineDataBagKeys.TestTimeoutSeconds, timeout);

        var stageStartTimeUtc = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var res = await effectiveEnv.ExecuteAsync(command, workDir, timeoutSeconds: timeout, cancellationToken: cancellationToken);
        stopwatch.Stop();

        var fullOutput = string.IsNullOrWhiteSpace(res.StandardError)
            ? res.StandardOutput
            : $"{res.StandardOutput}\n{res.StandardError}";

        bag.Set(PipelineDataBagKeys.StageCommand(StageId), command);
        bag.Set(PipelineDataBagKeys.StageOutput(StageId), WorkflowDefaults.TruncateStageOutput(fullOutput));

        int totalTests = 0;
        int failedTests = 0;
        int passedTests = 0;
        var failedList = new List<string>();

        // 1. Attempt structured TRX parsing from fresh test result files
        var trxMetrics = TestDiagnosticsParser.ParseFreshTrxFiles(workDir, stageStartTimeUtc, _logger);
        if (trxMetrics != null && trxMetrics.Total > 0)
        {
            totalTests = trxMetrics.Total;
            passedTests = trxMetrics.Passed;
            failedTests = trxMetrics.Failed;
            failedList.AddRange(trxMetrics.FailedTestNames);
        }
        else
        {
            // 2. Check for VSTest summary lines (aggregate across all assemblies if present)
            var summaryMatches = VSTestGlobalSummaryRegex().Matches(fullOutput);
            if (summaryMatches.Count > 0)
            {
                foreach (Match m in summaryMatches)
                {
                    if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
                        totalTests += t;
                    if (int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                        passedTests += p;
                    if (int.TryParse(m.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f))
                        failedTests += f;
                }
                var regexFailures = TestFailureLineRegex().Matches(fullOutput).Select(m => m.Value.Trim()).Distinct();
                failedList.AddRange(regexFailures);
            }
            else
            {
                // 3. Fallback to stdout regex parsing with multi-match aggregation
                foreach (Match totalMatch in TotalTestsRegex().Matches(fullOutput))
                {
                    var val = totalMatch.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => !string.IsNullOrEmpty(g.Value))?.Value;
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
                    {
                        totalTests += t;
                    }
                }

                foreach (Match failedMatch in FailedTestsRegex().Matches(fullOutput))
                {
                    var val = failedMatch.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => !string.IsNullOrEmpty(g.Value))?.Value;
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f))
                    {
                        failedTests += f;
                    }
                }

                foreach (Match passedMatch in PassedTestsRegex().Matches(fullOutput))
                {
                    var val = passedMatch.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => !string.IsNullOrEmpty(g.Value))?.Value;
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                    {
                        passedTests += p;
                    }
                }

                if (totalTests == 0 && (passedTests > 0 || failedTests > 0))
                {
                    totalTests = passedTests + failedTests;
                }

                var regexFailures = TestFailureLineRegex().Matches(fullOutput).Select(m => m.Value.Trim()).Distinct();
                failedList.AddRange(regexFailures);
            }
        }

        bag.Set(PipelineDataBagKeys.TestsCurrentTotal, totalTests);
        bag.Set(PipelineDataBagKeys.TestsCurrentPassed, passedTests);
        bag.Set(PipelineDataBagKeys.TestsCurrentFailed, failedTests);
        bag.Set(PipelineDataBagKeys.TestsCurrentDurationMs, (int)stopwatch.ElapsedMilliseconds);

        // Parse code coverage across fresh reports and stdout summary tables
        var coverageRate = await CoverletCoverageParser.AggregateCoverageAsync(
            workDir,
            stageStartTimeUtc,
            fullOutput,
            _logger,
            cancellationToken);

        if (coverageRate.HasValue)
        {
            bag.Set(PipelineDataBagKeys.CoverageCurrentLineRate, coverageRate.Value);
        }
        else
        {
            bag.Remove(PipelineDataBagKeys.CoverageCurrentLineRate);
        }

        // Check timeout
        bool isTimeout = res.ExitCode == WorkflowDefaults.ProcessTimeoutExitCode ||
                         res.Truncated && (res.StandardError.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                                           res.StandardOutput.Contains("timed out", StringComparison.OrdinalIgnoreCase));

        if (isTimeout)
        {
            var timeoutMsg = $"Deterministic test execution timed out after {timeout} seconds. Tests failed to complete within the configured limit to avoid locking resources. Check recent edits for infinite loops, deadlocks, blocking network calls, or hanging tests.";
            bag.Set(PipelineDataBagKeys.FailedTests, new List<string> { timeoutMsg });
            bag.Set(PipelineDataBagKeys.TestFailureReason, timeoutMsg);
            bag.Set(PipelineDataBagKeys.FailedTestsTruncated, false);
            var target = bag.Get<string>(PipelineDataBagKeys.LoopBackId) ?? LoopBackStageId;
            return StageExecutionResult.Failure(timeoutMsg, target);
        }

        // Anti-silent success / zero-tests policy
        if (res.Success && totalTests == 0)
        {
            bool allowNoTests = (context.CurrentProfile != null && context.CurrentProfile.AllowNoTests) ||
                                (bag.TryGet<bool>(PipelineDataBagKeys.AllowNoTests, out var bagAllow) && bagAllow) ||
                                config.AllowNoTests == true;

            if (!allowNoTests)
            {
                var zeroMsg = "Deterministic test runner reported 0 tests executed. Zero tests discovered is treated as a quality gate failure unless AllowNoTests is explicitly configured.";
                bag.Set(PipelineDataBagKeys.TestFailureReason, zeroMsg);
                var target = bag.Get<string>(PipelineDataBagKeys.LoopBackId) ?? LoopBackStageId;
                return StageExecutionResult.Failure(zeroMsg, target);
            }
        }

        // Test failures or non-zero exit
        if (!res.Success || failedTests > 0)
        {
            if (failedList.Count > 10)
            {
                var truncatedList = failedList.Take(10).ToList();
                truncatedList.Add($"… and {failedList.Count - 10} more failure(s)");
                bag.Set(PipelineDataBagKeys.FailedTests, truncatedList);
                bag.Set(PipelineDataBagKeys.FailedTestsTruncated, true);
            }
            else
            {
                bag.Set(PipelineDataBagKeys.FailedTests, failedList);
                bag.Set(PipelineDataBagKeys.FailedTestsTruncated, false);
            }

            string errorMsg;
            if (failedTests > 0)
            {
                errorMsg = $"Deterministic tests failed: {failedTests} failed test(s) out of {totalTests}.";
            }
            else
            {
                var summarySnippet = fullOutput.Length > 300 ? fullOutput[^300..].Trim() : fullOutput.Trim();
                errorMsg = $"Deterministic test runner failed with exit code {res.ExitCode}. Output: {summarySnippet}";
                _logger?.LogWarning("Test runner exited with code {ExitCode} but 0 failed tests were parsed. Output: {Output}", res.ExitCode, fullOutput);
            }

            bag.Set(PipelineDataBagKeys.TestFailureReason, errorMsg);
            var targetStage = bag.Get<string>(PipelineDataBagKeys.LoopBackId) ?? LoopBackStageId;
            return StageExecutionResult.Failure(errorMsg, targetStage);
        }

        bag.Remove(PipelineDataBagKeys.FailedTests);
        bag.Remove(PipelineDataBagKeys.FailedTestsTruncated);
        bag.Remove(PipelineDataBagKeys.TestFailureReason);

        // Baseline policy: only record baseline on a successful run with > 0 tests
        if (res.Success && failedTests == 0 && totalTests > 0)
        {
            if (!bag.TryGet<int>(PipelineDataBagKeys.TestsBaselineTotal, out _))
            {
                bag.Set(PipelineDataBagKeys.TestsBaselineTotal, totalTests);
            }

            if (coverageRate.HasValue && !bag.TryGet<double>(PipelineDataBagKeys.CoverageBaselineLineRate, out _))
            {
                bag.Set(PipelineDataBagKeys.CoverageBaselineLineRate, coverageRate.Value);
            }
        }

        return StageExecutionResult.Success();
    }

    /// <summary>
    /// Legacy helper preserved for backward compatibility; delegates to <see cref="CoverletCoverageParser.ParseCobertura"/>.
    /// </summary>
    public static double? ParseCoberturaLineRate(string xmlContent, ILogger? logger = null)
    {
        return CoverletCoverageParser.ParseCobertura(xmlContent, logger)?.LineRate;
    }
}
