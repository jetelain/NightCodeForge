using System.Text.RegularExpressions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Deterministic workflow stage executing test runner (e.g. dotnet test) purely on CPU with 0 GPU tokens.
/// Parses total, passed, and failed test metrics and feeds them into PipelineDataBag for quality gates.
/// </summary>
public partial class DeterministicTestStage : IWorkflowStage
{
    public const string DefaultStageId = "deterministic-test";
    public const string FallbackLoopBackStageId = "implementation";

    [GeneratedRegex(@"(?:Total:[ \t]*(\d+)|total[ \t]*:[ \t]*(\d+)|(\d+)[ \t]+total)", RegexOptions.IgnoreCase)]
    private static partial Regex TotalTestsRegex();

    [GeneratedRegex(@"(?:Failed:[ \t]*(\d+)|échec[ \t]*:[ \t]*(\d+)|(\d+)[ \t]+failed)", RegexOptions.IgnoreCase)]
    private static partial Regex FailedTestsRegex();

    [GeneratedRegex(@"(?:Passed:[ \t]*(\d+)|réussite[ \t]*:[ \t]*(\d+)|(\d+)[ \t]+passed)", RegexOptions.IgnoreCase)]
    private static partial Regex PassedTestsRegex();

    [GeneratedRegex(@"(?:Failed\s+([A-Za-z0-9_.]+)|\[FAIL\]\s+(.+)|FAIL:\s+(.+))")]
    private static partial Regex TestFailureLineRegex();

    private readonly IExecutionEnvironment? _executionEnv;
    private readonly IExecutionEnvironmentProvider? _envProvider;
    private readonly string _testCommand;

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public string LoopBackStageId { get; set; } = FallbackLoopBackStageId;

    public DeterministicTestStage(
        IExecutionEnvironment? executionEnv = null,
        string? testCommand = null,
        string stageId = DefaultStageId,
        string displayName = "Deterministic Unit Test Gate",
        IExecutionEnvironmentProvider? envProvider = null)
    {
        _executionEnv = executionEnv;
        _envProvider = envProvider;
        _testCommand = testCommand ?? "dotnet test --no-build";
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
        var command = config.TestCommand ?? _testCommand;

        var effectiveEnv = (context.Workspace != null && _envProvider != null)
            ? _envProvider.GetEnvironment(context.Workspace, SandboxSecurityTier.Standard, context.Issue?.Id)
            : _executionEnv;

        if (effectiveEnv != null)
        {
            var timeout = context.Workspace?.TestTimeoutSeconds ?? config.TestTimeoutSeconds ?? 120;
            bag.Set("test.timeout_seconds", timeout);

            var res = await effectiveEnv.ExecuteAsync(command, workDir, timeoutSeconds: timeout, cancellationToken: cancellationToken);

            var fullOutput = string.IsNullOrWhiteSpace(res.StandardError)
                ? res.StandardOutput
                : $"{res.StandardOutput}\n{res.StandardError}";

            // Parse total tests
            int totalTests = 0;
            var totalMatch = TotalTestsRegex().Match(fullOutput);
            if (totalMatch.Success)
            {
                var val = totalMatch.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => !string.IsNullOrEmpty(g.Value))?.Value;
                int.TryParse(val, out totalTests);
            }

            // Parse failed tests
            int failedTests = 0;
            var failedMatch = FailedTestsRegex().Match(fullOutput);
            if (failedMatch.Success)
            {
                var val = failedMatch.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => !string.IsNullOrEmpty(g.Value))?.Value;
                int.TryParse(val, out failedTests);
            }

            // Parse passed tests
            int passedTests = 0;
            var passedMatch = PassedTestsRegex().Match(fullOutput);
            if (passedMatch.Success)
            {
                var val = passedMatch.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => !string.IsNullOrEmpty(g.Value))?.Value;
                int.TryParse(val, out passedTests);
            }

            if (totalTests == 0 && (passedTests > 0 || failedTests > 0))
            {
                totalTests = passedTests + failedTests;
            }

            bag.Set("tests.current.total", totalTests);
            bag.Set("tests.current.failed", failedTests);

            if (!bag.TryGet<int>("tests.baseline.total", out _))
            {
                bag.Set("tests.baseline.total", totalTests);
            }

            // Parse code coverage (Cobertura / Coverlet XML)
            if (Directory.Exists(workDir))
            {
                try
                {
                    var coverageFiles = Directory.GetFiles(workDir, "*coverage*.xml", SearchOption.AllDirectories);
                    foreach (var covFile in coverageFiles)
                    {
                        var xml = await File.ReadAllTextAsync(covFile, cancellationToken);
                        var rate = ParseCoberturaLineRate(xml);
                        if (rate.HasValue)
                        {
                            bag.Set("coverage.current.line_rate", rate.Value);
                            if (!bag.TryGet<double>("coverage.baseline.line_rate", out _))
                            {
                                bag.Set("coverage.baseline.line_rate", rate.Value);
                            }
                            break;
                        }
                    }
                }
                catch { }
            }

            if (!res.Success || failedTests > 0)
            {
                bool isTimeout = res.ExitCode == 124 ||
                                 res.Truncated && (res.StandardError.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                                                   res.StandardOutput.Contains("timed out", StringComparison.OrdinalIgnoreCase));

                if (isTimeout)
                {
                    var timeoutMsg = $"Deterministic test execution timed out after {timeout} seconds. Tests failed to complete within the configured limit to avoid locking resources. Check recent edits for infinite loops, deadlocks, blocking network calls, or hanging tests.";
                    bag.Set("diagnostics.failed_tests", new List<string> { timeoutMsg });
                    bag.Set("test.failure.reason", timeoutMsg);
                    var target = bag.Get<string>("Stage.LoopBackId") ?? LoopBackStageId;
                    return StageExecutionResult.Failure(timeoutMsg, target);
                }

                var failedLines = TestFailureLineRegex().Matches(fullOutput).Select(m => m.Value.Trim()).Take(10).ToList();
                bag.Set("diagnostics.failed_tests", failedLines);

                var errorMsg = $"Deterministic tests failed: {failedTests} failed test(s) out of {totalTests}.";
                bag.Set("test.failure.reason", errorMsg);
                var targetStage = bag.Get<string>("Stage.LoopBackId") ?? LoopBackStageId;
                return StageExecutionResult.Failure(errorMsg, targetStage);
            }
        }

        return StageExecutionResult.Success();
    }

    public static double? ParseCoberturaLineRate(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent)) return null;

        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xmlContent);
            var root = doc.Root;
            if (root != null)
            {
                var lineRateAttr = root.Attribute("line-rate");
                if (lineRateAttr != null && double.TryParse(lineRateAttr.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var rate))
                {
                    return rate;
                }
                var linesCovered = root.Attribute("lines-covered");
                var linesValid = root.Attribute("lines-valid");
                if (linesCovered != null && linesValid != null &&
                    double.TryParse(linesCovered.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var covered) &&
                    double.TryParse(linesValid.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var valid) &&
                    valid > 0)
                {
                    return covered / valid;
                }
            }
        }
        catch { }

        return null;
    }
}
