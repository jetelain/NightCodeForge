namespace NightCodeForge.Core.Workflow.Filtering;

/// <summary>
/// Audit telemetry captured for each <c>bash</c> call, preserving raw logs and savings.
/// </summary>
public sealed record BashExecutionAuditEntry(
    string Command,
    int ExitCode,
    string? VirtualTmpPath,
    string RawOutput,
    int RawChars,
    int FilteredChars,
    bool WasSmartFiltered,
    bool WasTruncated)
{
    public int SavedChars => Math.Max(0, RawChars - FilteredChars);
}
