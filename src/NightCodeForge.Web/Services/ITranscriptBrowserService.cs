using NightCodeForge.Core.Services;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Service responsible for scanning, parsing, rendering, and diagnosing session transcripts.
/// </summary>
public interface ITranscriptBrowserService
{
    /// <summary>
    /// Scans the transcripts directory for the active workspace (or fallback global directory)
    /// and extracts frontmatter summaries for up to the default recent window of session logs.
    /// </summary>
    Task<IReadOnlyList<TranscriptSummary>> GetTranscriptsAsync(string? workspacePath = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans the transcripts directory and extracts frontmatter summaries for up to <paramref name="maxTranscripts"/>
    /// most recent session logs (null or &lt;= 0 means unlimited).
    /// </summary>
    Task<IReadOnlyList<TranscriptSummary>> GetTranscriptsAsync(string? workspacePath, int? maxTranscripts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans the transcripts directory and returns both the recent windowed summaries and total available transcript count.
    /// </summary>
    Task<TranscriptWindowResult<TranscriptSummary>> GetTranscriptsWindowAsync(
        string? workspacePath = null,
        int? maxTranscripts = TranscriptAnalysisDefaults.DefaultRecentLimit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads and parses a specific transcript file by its absolute or relative path.
    /// </summary>
    Task<ParsedTranscriptDetail?> GetTranscriptDetailAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads and parses a specific transcript file by its file name within the active workspace.
    /// </summary>
    Task<ParsedTranscriptDetail?> GetTranscriptDetailByNameAsync(string fileName, string? workspacePath = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes hybrid AI diagnostics (Cloud vs. Local) on a parsed transcript to identify
    /// root causes, prompt critiques, and one-click remediation actions.
    /// </summary>
    Task<DiagnosticReport> RunDiagnosticsAsync(ParsedTranscriptDetail detail, string mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches a remediation suggestion (e.g. toggling optimization flags or adjusting limits)
    /// to SQLite agent profiles and global optimization settings.
    /// </summary>
    Task ApplyRemediationAsync(RemediationSuggestion suggestion, int? workspaceId = null, string? profileName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes an individual transcript file using the standard transcript analysis engine.
    /// </summary>
    Task<TranscriptAnalysis?> GetTranscriptAnalysisAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes up to the default recent window of session transcripts across candidate directories for fleet-wide friction and metrics.
    /// </summary>
    Task<IReadOnlyList<TranscriptAnalysis>> GetFleetAnalysisAsync(string? workspacePath = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes up to <paramref name="maxTranscripts"/> most recent session transcripts across candidate directories for fleet-wide friction and metrics.
    /// </summary>
    Task<IReadOnlyList<TranscriptAnalysis>> GetFleetAnalysisAsync(string? workspacePath, int? maxTranscripts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes up to <paramref name="maxTranscripts"/> most recent session transcripts across candidate directories,
    /// returning both the windowed analyses and total available transcript count.
    /// </summary>
    Task<TranscriptWindowResult<TranscriptAnalysis>> GetFleetAnalysisWindowAsync(
        string? workspacePath = null,
        int? maxTranscripts = TranscriptAnalysisDefaults.DefaultRecentLimit,
        CancellationToken cancellationToken = default);
}
