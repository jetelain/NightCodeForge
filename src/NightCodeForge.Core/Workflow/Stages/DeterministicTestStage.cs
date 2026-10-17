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
    public const string DefaultStageId = StageImplementationTypes.DeterministicTest;
    public const string FallbackLoopBackStageId = StandardStageIds.Implementation;

    [GeneratedRegex(@"(?:Total:[ \t]*(\d+)|total[ \t]*:[ \t]*(\d+)|(\d+)[ \t]+total)", RegexOptions.IgnoreCase)]
    private static partial Regex TotalTestsRegex();

    [GeneratedRegex(@"(?:Failed:[ \t]*(\d+)|échec[ \t]*:[ \t]*(\d+)|(\d+)[ \t]+failed)", RegexOptions.IgnoreCase)]
    private static partial Regex FailedTestsRegex();

    [GeneratedRegex(@"(?:Passed:[ \t]*(\d+)|réussite[ \t]*:[ \t]*(\d+)|(\d+)[ \t]+passed)", RegexOptions.IgnoreCase)]
    private static partial Regex PassedTestsRegex();

    [GeneratedRegex(@"(?:Failed\s+([A-Za-z0-9_.]+)|\[FAIL\]\s+(.+)|FAIL:\s+(.+))")]
    private static partial Regex TestFailureLineRegex();

    private readonly IExecutionEnvironmentProvider _envProvider;
    private readonly string _testCommand;

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public string LoopBackStageId { get; set; } = FallbackLoopBackStageId;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public DeterministicTestStage(IExecutionEnvironmentProvider envProvider)
        : this(envProvider, "dotnet test --no-build", DefaultStageId, "Deterministic Unit Test Gate")
    {
    }

    public DeterministicTestStage(
        IExecutionEnvironmentProvider envProvider,
        string testCommand,
        string stageId,
        string displayName)
    {
        _envProvider = envProvider ?? throw new ArgumentNullException(nameof(envProvider));
        _testCommand = !string.IsNullOrWhiteSpace(testCommand) ? testCommand : "dotnet test --no-build";
        StageId = !string.IsNullOrWhiteSpace(stageId) ? stageId : DefaultStageId;
        DisplayName = !string.IsNullOrWhiteSpace(displayName) ? displayName : "Deterministic Unit Test Gate";
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var workspace = context.Workspace ?? throw new InvalidOperationException("Workspace is required to resolve execution environment.");

        var bag = context.DataBag;
        var repoPath = workspace.RepositoryPath;
        var workDir = !string.IsNullOrWhiteSpace(context.WorkingDirectory)
            ? context.WorkingDirectory
            : (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)
                ? workspace.Ext4MirrorPath
                : repoPath);

        var config = CodeStackPresets.ResolveConfig(repoPath, bag);
        var command = config.TestCommand ?? _testCommand;

        var effectiveEnv = _envProvider.GetEnvironment(workspace, SandboxSecurityTier.Standard, context.ForgeTask?.Id)
            ?? throw new InvalidOperationException($"No execution environment could be resolved for workspace #{workspace.Id}.");

        var timeout = workspace.TestTimeoutSeconds ?? config.TestTimeoutSeconds ?? WorkflowDefaults.DefaultTestTimeoutSeconds;
        bag.Set(PipelineDataBagKeys.TestTimeoutSeconds, timeout);

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

            bag.Set(PipelineDataBagKeys.TestsCurrentTotal, totalTests);
            bag.Set(PipelineDataBagKeys.TestsCurrentFailed, failedTests);

            if (!bag.TryGet<int>(PipelineDataBagKeys.TestsBaselineTotal, out _))
            {
                bag.Set(PipelineDataBagKeys.TestsBaselineTotal, totalTests);
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
                            bag.Set(PipelineDataBagKeys.CoverageCurrentLineRate, rate.Value);
                            if (!bag.TryGet<double>(PipelineDataBagKeys.CoverageBaselineLineRate, out _))
                            {
                                bag.Set(PipelineDataBagKeys.CoverageBaselineLineRate, rate.Value);
                            }
                            break;
                        }
                    }
                }
                catch { }
            }

            if (!res.Success || failedTests > 0)
            {
                bool isTimeout = res.ExitCode == WorkflowDefaults.ProcessTimeoutExitCode ||
                                 res.Truncated && (res.StandardError.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                                                   res.StandardOutput.Contains("timed out", StringComparison.OrdinalIgnoreCase));

                if (isTimeout)
                {
                    var timeoutMsg = $"Deterministic test execution timed out after {timeout} seconds. Tests failed to complete within the configured limit to avoid locking resources. Check recent edits for infinite loops, deadlocks, blocking network calls, or hanging tests.";
                    bag.Set(PipelineDataBagKeys.FailedTests, new List<string> { timeoutMsg });
                    bag.Set(PipelineDataBagKeys.TestFailureReason, timeoutMsg);
                    var target = bag.Get<string>(PipelineDataBagKeys.LoopBackId) ?? LoopBackStageId;
                    return StageExecutionResult.Failure(timeoutMsg, target);
                }

                var failedLines = TestFailureLineRegex().Matches(fullOutput).Select(m => m.Value.Trim()).Take(10).ToList();
                bag.Set(PipelineDataBagKeys.FailedTests, failedLines);

                var errorMsg = $"Deterministic tests failed: {failedTests} failed test(s) out of {totalTests}.";
                bag.Set(PipelineDataBagKeys.TestFailureReason, errorMsg);
                var targetStage = bag.Get<string>(PipelineDataBagKeys.LoopBackId) ?? LoopBackStageId;
                return StageExecutionResult.Failure(errorMsg, targetStage);
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
