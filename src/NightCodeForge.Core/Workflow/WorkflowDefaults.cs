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
    public const int DefaultFormatTimeoutSeconds = 120;
    public const int DefaultSastTimeoutSeconds = 180;
    public const int ProcessTimeoutExitCode = 124;
    public const int DefaultOutputSummaryTruncationLength = 300;
    public const int MaxStageOutputChars = 32768;
    public const string DefaultCoverageTestCommand = "dotnet test --no-build --collect:\"XPlat Code Coverage\" --logger trx";
    public const string ModelPreloadKeepAlive = "15m";

    /// <summary>
    /// Tail-truncates stage output if it exceeds <see cref="MaxStageOutputChars"/> to prevent DataBag bloat.
    /// </summary>
    public static string TruncateStageOutput(string? output)
    {
        if (string.IsNullOrEmpty(output)) return string.Empty;
        if (output.Length <= MaxStageOutputChars) return output;

        var marker = $"\n... [Output truncated to last {MaxStageOutputChars} characters] ...\n";
        var keep = MaxStageOutputChars - marker.Length;
        if (keep <= 0) return output[^MaxStageOutputChars..];
        return marker + output[^keep..];
    }
}
