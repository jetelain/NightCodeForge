namespace NightCodeForge.Web.Services;

/// <summary>
/// Service responsible for scanning, parsing, rendering, and diagnosing session transcripts.
/// </summary>
public interface ITranscriptBrowserService
{
    /// <summary>
    /// Scans the transcripts directory for the active workspace (or fallback global directory)
    /// and extracts frontmatter summaries for all session logs.
    /// </summary>
    Task<IReadOnlyList<TranscriptSummary>> GetTranscriptsAsync(string? workspacePath = null, CancellationToken cancellationToken = default);

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
    Task<NightCodeForge.Core.Services.TranscriptAnalysis?> GetTranscriptAnalysisAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes all session transcripts across candidate directories for fleet-wide friction and metrics.
    /// </summary>
    Task<IReadOnlyList<NightCodeForge.Core.Services.TranscriptAnalysis>> GetFleetAnalysisAsync(string? workspacePath = null, CancellationToken cancellationToken = default);
}
