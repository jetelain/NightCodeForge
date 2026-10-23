using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

public enum FileChangeType { Modified, Added, Deleted, Renamed }

public record FileDiffSummary(
    string Path,
    FileChangeType ChangeType,
    int Additions,
    int Deletions,
    string OldContent,
    string NewContent);

public record TaskReviewSnapshot(
    int TaskId,
    string Title,
    string FeatureBranch,
    string BaseBranch,
    MergeMode MergeMode,
    TaskOverallStatus Status,
    IReadOnlyList<FileDiffSummary> Files,
    QualityGateMetrics GateMetrics,
    bool HasResolvedConflict = false);

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
