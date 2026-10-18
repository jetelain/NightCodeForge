namespace NightCodeForge.Core.Services;

/// <summary>
/// Immutable baseline snapshot captured before stage execution to detect concurrent changes and agent modifications.
/// </summary>
public record TaskBaselineEntry(
    int TaskId,
    int Version,
    DateTime UpdatedAt,
    string ContentHash,
    string FilePath,
    TaskFrontmatter Frontmatter,
    string Description);

/// <summary>
/// Result of syncing all workspace tasks to disk.
/// </summary>
public record SyncAllToDiskResult(
    IReadOnlyDictionary<int, string> WrittenPaths,
    IReadOnlyDictionary<int, TaskBaselineEntry> Baseline);
