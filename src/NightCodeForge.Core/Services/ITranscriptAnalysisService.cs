namespace NightCodeForge.Core.Services;

/// <summary>
/// Service providing transcript execution analysis, metrics aggregation,
/// friction detection, and actionable remediation recommendations.
/// </summary>
public interface ITranscriptAnalysisService
{
    /// <summary>
    /// Analyzes all session transcripts (*.md) in the specified directory.
    /// </summary>
    Task<IReadOnlyList<TranscriptAnalysis>> AnalyzeDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads and analyzes an individual transcript file.
    /// </summary>
    Task<TranscriptAnalysis> AnalyzeFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses and evaluates transcript content in-memory for friction points,
    /// scorecard diagnostics, file churn, and tool distributions.
    /// </summary>
    TranscriptAnalysis Analyze(string content, string fileName, string filePath = "");
}
