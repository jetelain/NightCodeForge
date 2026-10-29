namespace NightCodeForge.Core.Domain;

/// <summary>
/// Snapshot of task review state for presentation in review workflows.
/// </summary>
public record TaskReviewSnapshot(
    int TaskId,
    string Title,
    string FeatureBranch,
    string BaseBranch,
    MergeMode MergeMode,
    TaskOverallStatus Status,
    IReadOnlyList<FileDiffSummary> Files,
    QualityGateMetrics GateMetrics,
    bool HasResolvedConflict = false,
    bool HasExecutionRun = false);

/// <summary>
/// Quality gate metrics associated with a task review.
/// </summary>
public record QualityGateMetrics(
    double BaselineCoverage,
    double CurrentCoverage,
    double CoverageDelta,
    int BaselineWarnings,
    int CurrentWarnings,
    int WarningDelta,
    int TotalTests,
    int PassedTests,
    int FailedTests,
    int DurationMs);
