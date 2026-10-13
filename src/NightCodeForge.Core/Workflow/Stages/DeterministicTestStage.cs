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

    [GeneratedRegex(@"(?:Total:\s*(\d+)|total\s*:\s*(\d+)|(\d+)\s+total)", RegexOptions.IgnoreCase)]
    private static partial Regex TotalTestsRegex();

    [GeneratedRegex(@"(?:Failed:\s*(\d+)|échec\s*:\s*(\d+)|(\d+)\s+failed)", RegexOptions.IgnoreCase)]
    private static partial Regex FailedTestsRegex();

    [GeneratedRegex(@"(?:Passed:\s*(\d+)|réussite\s*:\s*(\d+)|(\d+)\s+passed)", RegexOptions.IgnoreCase)]
    private static partial Regex PassedTestsRegex();

    [GeneratedRegex(@"(?:Failed\s+([A-Za-z0-9_.]+)|\[FAIL\]\s+(.+)|FAIL:\s+(.+))")]
    private static partial Regex TestFailureLineRegex();

    private readonly IExecutionEnvironment? _executionEnv;
    private readonly string _testCommand;

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public string LoopBackStageId { get; set; } = FallbackLoopBackStageId;

    public DeterministicTestStage(
        IExecutionEnvironment? executionEnv = null,
        string? testCommand = null,
        string stageId = DefaultStageId,
        string displayName = "Deterministic Unit Test Gate")
    {
        _executionEnv = executionEnv;
        _testCommand = testCommand ?? "dotnet test --no-build";
        StageId = stageId;
        DisplayName = displayName;
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var bag = context.DataBag;
        var config = CodeStackPresets.ResolveConfig(context.Workspace.RepositoryPath, bag);
        var command = config.TestCommand ?? _testCommand;
        var workDir = context.Workspace.RepositoryPath;

        if (_executionEnv != null)
        {
            var res = await _executionEnv.ExecuteAsync(command, workDir, timeoutSeconds: 300, cancellationToken: cancellationToken);

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

            if (!res.Success || failedTests > 0)
            {
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
}
