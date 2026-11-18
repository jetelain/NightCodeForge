using System.Globalization;

namespace NightCodeForge.Core.Workflow.Filtering;

/// <summary>
/// Standard headers, footers, and notices emitted by CLI filtering and truncation.
/// </summary>
public static class CliOutputFilterConstants
{
    public const string SimplifiedOutputHeader = "[This is a simplified output]";
    public const string BuildSucceededSummary = "Build succeeded.";
    public const string BuildFailedSummary = "Build failed.";
    public const string TestRunFailedSummary = "Test Run Failed.";
    public const string TestRunPassedSummary = "Test Run Successful.";

    public static string FormatFullOutputFooter(string virtualTmpPath, int charCount) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"[Full output : {virtualTmpPath} ({charCount} chars), use grep/rg/sed to extract additional informations]");

    public static string FormatAdditionalCharsMarker(int omittedChars) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"... additional {omittedChars} characters");

    public static string FormatSuppressedWarningsNotice(int warningCount, int errorCount) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"[⚠️ Suppressed {warningCount} compiler warnings to focus on {errorCount} fatal errors.]");

    public static string FormatOmittedWarningsNotice(int omittedCount) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"[... and {omittedCount} additional warnings omitted. Run with disable_filter=true for complete list.]");
}
