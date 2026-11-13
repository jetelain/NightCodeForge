namespace NightCodeForge.Core.Services;

/// <summary>
/// Service providing transcript execution analysis, metrics aggregation,
/// friction detection, and actionable remediation recommendations.
/// </summary>
public interface ITranscriptAnalysisService
{
    /// <summary>
    /// Analyzes up to the default recent window of session transcripts (*.md) in the specified directory.
    /// </summary>
    Task<IReadOnlyList<TranscriptAnalysis>> AnalyzeDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes up to <paramref name="maxTranscripts"/> most recent session transcripts (*.md) in the specified directory.
    /// Null or non-positive values analyze all available transcripts.
    /// </summary>
    Task<IReadOnlyList<TranscriptAnalysis>> AnalyzeDirectoryAsync(string directoryPath, int? maxTranscripts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes up to <paramref name="maxTranscripts"/> most recent session transcripts (*.md) in the specified directory,
    /// returning both the windowed results and the total pre-slice transcript count.
    /// </summary>
    Task<TranscriptWindowResult<TranscriptAnalysis>> AnalyzeDirectoryWindowAsync(
        string directoryPath,
        int? maxTranscripts = TranscriptAnalysisDefaults.DefaultRecentLimit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads and analyzes an individual transcript file.
    /// </summary>
    Task<TranscriptAnalysis> AnalyzeFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses and evaluates transcript content in-memory for friction points,
    /// scorecard diagnostics, file churn, and tool distributions.
    /// </summary>
    TranscriptAnalysis Analyze(string content, string fileName, string filePath = "");

    /// <summary>
    /// Aggregates recent-window workspace friction hotspots (split-candidate files, recurring compiler errors,
    /// and recurring failing tests) for downstream prompt injection and Nightwatch triage.
    /// </summary>
    Task<WorkspaceFrictionHotspots> GetWorkspaceFrictionHotspotsAsync(
        string workspacePath,
        int? maxTranscripts = TranscriptAnalysisDefaults.DefaultRecentLimit,
        CancellationToken cancellationToken = default);
}
