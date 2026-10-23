namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when pushing base branch changes back to the host repository mirror is rejected (e.g. non-fast-forward race).
/// </summary>
public class MirrorPushRejectedException : Exception
{
    public string BaseBranch { get; }
    public string MirrorRepoPath { get; }
    public string RawGitOutput { get; }

    public MirrorPushRejectedException(
        string baseBranch,
        string mirrorRepoPath,
        string rawGitOutput,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? $"Push of base branch '{baseBranch}' from mirror '{mirrorRepoPath}' was rejected by host repository.", innerException)
    {
        BaseBranch = baseBranch ?? throw new ArgumentNullException(nameof(baseBranch));
        MirrorRepoPath = mirrorRepoPath ?? throw new ArgumentNullException(nameof(mirrorRepoPath));
        RawGitOutput = rawGitOutput ?? string.Empty;
    }
}
