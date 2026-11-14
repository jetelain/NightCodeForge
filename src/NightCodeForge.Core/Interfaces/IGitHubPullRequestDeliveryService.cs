using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Orchestrates GitHub outbound Pull Request delivery, remote base branch synchronization,
/// local task markdown stripping, lease-guarded branch pushing, and external merge verification.
/// </summary>
public interface IGitHubPullRequestDeliveryService
{
    /// <summary>
    /// Checks whether GitHub Pull Request delivery is currently available for the specified workspace
    /// (AirGappedMode == false, workspace GitHub integration &amp; PR delivery enabled, and an OAuth token is stored).
    /// </summary>
    Task<bool> IsPullRequestDeliveryAvailableAsync(Workspace? workspace, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the latest commits on <paramref name="baseBranch"/> from the remote GitHub repository
    /// using an ephemeral HTTP Authorization header and fast-forwards the local canonical base branch
    /// (and host repository when an ext4 mirror is active).
    /// </summary>
    Task FetchAndFastForwardBaseBranchFromRemoteAsync(Workspace workspace, string baseBranch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Strips NCF-added <c>docs/tasks/</c> markdown deliverables from the working directory while restoring any
    /// <c>docs/tasks/</c> files that already existed on <paramref name="baseBranch"/>, committing only if the index changed.
    /// </summary>
    Task StripTaskMarkdownDeliverablesFromBranchAsync(string workingDirectory, string baseBranch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries <c>ls-remote</c> on <paramref name="remoteUrl"/> for <paramref name="remoteBranchName"/> and pushes
    /// <paramref name="localBranch"/> using explicit <c>--force-with-lease=refs/heads/{remoteBranchName}:{expectedSha}</c>.
    /// Returns the pushed commit SHA.
    /// </summary>
    Task<string> PushTaskBranchWithLeaseAsync(
        string repoDirectory,
        string remoteUrl,
        string localBranch,
        string remoteBranchName,
        string? accessToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Strips <c>docs/tasks/</c> deliverables on the task branch, pushes <c>feature/task-{id}</c> to
    /// <c>refs/heads/ncf/{sanitizedLogin}/task-{id}</c> on GitHub, and creates or updates the Pull Request.
    /// Updates <see cref="ForgeTask.PullRequestNumber"/> and <see cref="ForgeTask.PullRequestUrl"/> on the task instance.
    /// </summary>
    Task<GitHubPullRequest> DeliverPullRequestAsync(ForgeTask task, Workspace workspace, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies that the task's GitHub Pull Request is merged on GitHub and fast-forwards the local <see cref="ForgeTask.BaseBranch"/>
    /// from the remote repository. Throws <see cref="InvalidOperationException"/> if the Pull Request is not yet merged.
    /// </summary>
    Task<GitHubPullRequest> VerifyAndSyncExternallyMergedPullRequestAsync(ForgeTask task, Workspace workspace, CancellationToken cancellationToken = default);
}

