using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Canonical constants for pre-flight GitHub Issue claim-conflict human clarification prompts.
/// </summary>
public static class GitHubClaimConflictConstants
{
    public const string ClaimConflictBatchPrefix = "github-claim-conflict-";
    public const string OverrideClaimOption = "Override claim and execute locally";
    public const string CancelTaskOption = "Cancel local task";
}

/// <summary>
/// Synchronizes NightCodeForge task lifecycle events bi-directionally with GitHub Issues and Pull Requests,
/// performs pre-flight multi-user claim verification before Stage 1 execution, and closes tasks whose PRs
/// were merged externally on GitHub.
/// </summary>
public interface IGitHubTaskMirrorService
{
    /// <summary>
    /// Verifies before Stage 1 (<see cref="StandardStageIds.TaskReview"/>) begins that a GitHub-linked issue
    /// is neither closed externally nor already claimed/in-progress by another GitHub user.
    /// Returns <see langword="true"/> when local execution may proceed, or <see langword="false"/> when
    /// the task has been cancelled or suspended awaiting human resolution in the Inbox.
    /// </summary>
    Task<bool> TryClaimIssueForExecutionAsync(
        ForgeTask task,
        Workspace workspace,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates answered claim-conflict clarification questions submitted via Inbox.
    /// Returns <see langword="true"/> when the user chose to cancel the local task (and the caller should not re-enqueue execution),
    /// or <see langword="false"/> when normal resumption may continue.
    /// </summary>
    Task<bool> TryHandleClaimConflictAnswerAsync(
        ForgeTask task,
        Workspace workspace,
        IReadOnlyList<Question> answeredQuestions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mirrors a task transitioning into <see cref="TaskOverallStatus.Open"/> prior to execution:
    /// optionally creates a new GitHub Issue for local tasks or swaps <c>ncf:triaged</c> for <c>ncf:backlog</c>.
    /// </summary>
    Task MirrorTaskOpenedAsync(
        ForgeTask task,
        Workspace workspace,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mirrors completion of Stage 1 (<see cref="StandardStageIds.TaskReview"/>) by applying <c>ncf:in-progress</c>
    /// and optionally posting the approved task specification summary as a milestone comment.
    /// </summary>
    Task MirrorTaskReviewCompletedAsync(
        ForgeTask task,
        Workspace workspace,
        string? specificationSummary,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mirrors completion of Stage 4 (<see cref="StandardStageIds.PostImplementationGates"/>) by applying
    /// <c>ncf:gates-passed</c> and optionally posting deterministic build, test, and coverage metrics.
    /// </summary>
    Task MirrorPostImplementationGatesCompletedAsync(
        ForgeTask task,
        Workspace workspace,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mirrors a task reaching <see cref="TaskOverallStatus.Ready"/> by applying <c>ncf:ready-for-review</c>
    /// and optionally posting a milestone comment referencing any opened Pull Request URL.
    /// </summary>
    Task MirrorTaskReadyAsync(
        ForgeTask task,
        Workspace workspace,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mirrors a task reaching <see cref="TaskOverallStatus.Closed"/> by posting a completion comment and
    /// closing the linked GitHub Issue with <c>ncf:closed</c> only when no unclosed sibling tasks remain.
    /// </summary>
    Task MirrorTaskClosedAsync(
        ForgeTask task,
        Workspace workspace,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Polls open or ready tasks with linked GitHub Pull Requests when <c>CloseTaskOnPrMerge</c> is enabled
    /// and invokes <paramref name="closeExternallyMergedTaskAsync"/> for each merged Pull Request.
    /// </summary>
    Task<IReadOnlyList<int>> SyncMergedPullRequestsAsync(
        Workspace workspace,
        Func<int, CancellationToken, Task> closeExternallyMergedTaskAsync,
        CancellationToken cancellationToken = default);
}
