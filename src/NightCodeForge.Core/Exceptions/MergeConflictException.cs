namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when a Git merge operation encounters merge conflicts that cannot be resolved automatically.
/// </summary>
public class MergeConflictException : Exception
{
    public int TaskId { get; }
    public string BaseBranch { get; }
    public IReadOnlyList<string> ConflictedFiles { get; }
    public string RawGitOutput { get; }

    public MergeConflictException(
        int taskId,
        string baseBranch,
        IReadOnlyList<string> conflictedFiles,
        string rawGitOutput,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? $"Merge conflict detected for ForgeTask #{taskId} with '{baseBranch}' in {conflictedFiles.Count} file(s).", innerException)
    {
        TaskId = taskId;
        BaseBranch = baseBranch ?? throw new ArgumentNullException(nameof(baseBranch));
        ConflictedFiles = conflictedFiles ?? throw new ArgumentNullException(nameof(conflictedFiles));
        RawGitOutput = rawGitOutput ?? string.Empty;
    }
}
