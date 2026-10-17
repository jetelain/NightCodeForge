namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Canonical keys and helpers for accessing workflow state in <see cref="PipelineDataBag"/>.
/// </summary>
public static class PipelineDataBagKeys
{
    public const string LastHumanAnswer = "LastHumanAnswer";
    public const string ApprovedPlan = "Stage1.ApprovedPlan";
    public const string CompilerErrors = "diagnostics.compiler_errors";
    public const string PendingProposals = "PendingProposals";
    public const string TaskMarkdownPath = "TaskMarkdownPath";
    public const string SerializedMemory = "SerializedMemory";
    public const string ReviewerFeedback = "ReviewerFeedback";
    public const string ReviewDiff = "ReviewDiff";
    public const string MaxRetries = "Workflow.MaxRetries";
    public const string RetryCount = "Workflow.RetryCount";
    public const string StageRetries = "Workflow.StageRetries";
    public const string LoopBackId = "Stage.LoopBackId";
    public const string BuildCommand = "build.command";
    public const string TestCommand = "test.command";
    public const string SastCommand = "sast.command";
    public const string CoverageCommand = "coverage.command";
    public const string BuildTimeoutSeconds = "build.timeout_seconds";
    public const string TestTimeoutSeconds = "test.timeout_seconds";
    public const string TestsCurrentTotal = "tests.current.total";
    public const string TestsCurrentPassed = "tests.current.passed";
    public const string TestsCurrentFailed = "tests.current.failed";
    public const string TestsCurrentDurationMs = "tests.current.duration_ms";
    public const string TestsBaselineTotal = "tests.baseline.total";
    public const string CoverageCurrentLineRate = "coverage.current.line_rate";
    public const string CoverageBaselineLineRate = "coverage.baseline.line_rate";
    public const string CoverageDelta = "coverage.delta";
    public const string FailedTests = "diagnostics.failed_tests";
    public const string TestFailureReason = "test.failure.reason";
    public const string BuildFailureReason = "build.failure.reason";
    public const string CoverageFailureReason = "coverage.failure.reason";
    public const string SastFailureReason = "sast.failure.reason";
    public const string RatchetFailureReason = "ratchet.failure.reason";
    public const string WarningsCurrentTotal = "warnings.current.total";
    public const string WarningsBaselineTotal = "warnings.baseline.total";
    public const string WarningsDelta = "warnings.delta";
    public const string WarningsNew = "warnings.new";
    public const string ReviewFiles = "ReviewFiles";

    public static string StageSummary(string stageId) => $"StageSummary_{stageId}";
    public static string StageCommand(string stageId) => $"{stageId}.command";
    public static string StageOutput(string stageId) => $"{stageId}.output";
}
