using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Result of deterministic conflict resolution verification.
/// </summary>
/// <param name="IsSuccess">Whether all conflict markers are eliminated, files are staged, no unmerged entries remain, and merge is committed.</param>
/// <param name="ErrorMessage">Detailed verification failure message if any step failed.</param>
/// <param name="RemainingConflictedFiles">List of file paths that still contain conflict markers or unmerged index entries.</param>
/// <param name="CompilerErrors">Truncated compiler/diagnostic errors when deterministic compilation fails.</param>
public record ConflictResolutionVerificationResult(
    bool IsSuccess,
    string? ErrorMessage = null,
    IReadOnlyList<string>? RemainingConflictedFiles = null,
    string? CompilerErrors = null);

/// <summary>
/// Deterministically verifies that an agent's conflict resolution has removed all conflict markers,
/// resolved all index entries, and committed the merge.
/// </summary>
public interface IConflictResolutionVerifier
{
    /// <summary>
    /// Scans conflicted files for markers, stages resolved files, verifies empty unmerged index, and commits merge.
    /// </summary>
    Task<ConflictResolutionVerificationResult> VerifyResolutionAsync(
        Workspace workspace,
        int taskId,
        string baseBranch,
        IReadOnlyList<string> conflictedFiles,
        PipelineDataBag? dataBag = null,
        CancellationToken cancellationToken = default);
}
