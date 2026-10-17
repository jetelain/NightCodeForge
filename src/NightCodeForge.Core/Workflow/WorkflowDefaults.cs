namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Default numerical limits, thresholds, and exit codes for workflow execution and tool invocations.
/// </summary>
public static class WorkflowDefaults
{
    public const int DefaultMaxStageRetries = 3;
    public const int DefaultCommandTimeoutSeconds = 60;
    public const int DefaultBuildTimeoutSeconds = 120;
    public const int DefaultTestTimeoutSeconds = 120;
    public const int ProcessTimeoutExitCode = 124;
    public const int DefaultOutputSummaryTruncationLength = 300;
}
