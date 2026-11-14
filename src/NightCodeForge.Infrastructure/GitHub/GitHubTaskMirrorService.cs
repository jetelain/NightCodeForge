using System.Globalization;
using System.Text;
using System.Text.Json;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.BackgroundJobs;

namespace NightCodeForge.Infrastructure.GitHub;

/// <summary>
/// Implements bi-directional GitHub task lifecycle mirroring, pre-flight multi-user issue claiming,
/// 1:N sibling-guarded issue closure, and Pull Request merge polling.
/// </summary>
public sealed class GitHubTaskMirrorService : IGitHubTaskMirrorService
{
    private const string OpenIssueState = "open";
    private const string ClosedIssueState = "closed";
    private const string PrMergeRecurringJobIdPrefix = "github-pr-merge-sync-workspace-";
    private const string LogSourceName = nameof(GitHubTaskMirrorService);
    private const int MaxSpecificationSummaryChars = 2000;

    private static readonly string[] KnownStatusLabels =
    [
        GitHubSyncConstants.LabelTriaged,
        GitHubSyncConstants.LabelBacklog,
        GitHubSyncConstants.LabelInProgress,
        GitHubSyncConstants.LabelGatesPassed,
        GitHubSyncConstants.LabelReadyForReview,
        GitHubSyncConstants.LabelClosed
    ];

    private static readonly string[] ActiveExecutionLabels =
    [
        GitHubSyncConstants.LabelInProgress,
        GitHubSyncConstants.LabelGatesPassed,
        GitHubSyncConstants.LabelReadyForReview
    ];

    private readonly ITaskRepository _taskRepository;
    private readonly ITaskDependencyStore _dependencyStore;
    private readonly IGitHubClientService _gitHubClient;
    private readonly IGitHubTokenStore _tokenStore;
    private readonly IGitHubDeviceAuthService _deviceAuthService;
    private readonly IAgentActivityStreamer _activityStreamer;
    private readonly IOperationsTelemetryService _telemetryService;
    private readonly SecurityOptions _securityOptions;
    private readonly ILogger<GitHubTaskMirrorService> _logger;

    public GitHubTaskMirrorService(
        ITaskRepository taskRepository,
        ITaskDependencyStore dependencyStore,
        IGitHubClientService gitHubClient,
        IGitHubTokenStore tokenStore,
        IGitHubDeviceAuthService deviceAuthService,
        IAgentActivityStreamer activityStreamer,
        IOperationsTelemetryService telemetryService,
        IOptions<SecurityOptions> securityOptions,
        ILogger<GitHubTaskMirrorService> logger)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _dependencyStore = dependencyStore ?? throw new ArgumentNullException(nameof(dependencyStore));
        _gitHubClient = gitHubClient ?? throw new ArgumentNullException(nameof(gitHubClient));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _deviceAuthService = deviceAuthService ?? throw new ArgumentNullException(nameof(deviceAuthService));
        _activityStreamer = activityStreamer ?? throw new ArgumentNullException(nameof(activityStreamer));
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        ArgumentNullException.ThrowIfNull(securityOptions);
        _securityOptions = securityOptions.Value ?? throw new ArgumentNullException(nameof(securityOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Computes the Hangfire recurring job identifier for a workspace's PR merge polling schedule.
    /// </summary>
    public static string GetWorkspacePrMergeRecurringJobId(int workspaceId)
    {
        return $"{PrMergeRecurringJobIdPrefix}{workspaceId}";
    }

    /// <summary>
    /// Registers, updates, or removes the Hangfire recurring PR merge poll job for a workspace.
    /// </summary>
    public static void SynchronizeWorkspacePrMergeRecurringJob(
        IRecurringJobManager recurringJobManager,
        Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(recurringJobManager);
        ArgumentNullException.ThrowIfNull(workspace);

        var jobId = GetWorkspacePrMergeRecurringJobId(workspace.Id);
        var settings = workspace.GitHubSettings;
        var isEligible = settings != null
            && settings.Enabled
            && settings.PullRequestDeliveryEnabled
            && settings.CloseTaskOnPrMerge
            && !string.IsNullOrWhiteSpace(settings.RepositoryOwner)
            && !string.IsNullOrWhiteSpace(settings.RepositoryName);

        if (!isEligible)
        {
            recurringJobManager.RemoveIfExists(jobId);
            return;
        }

        var cronExpression = GitHubIssueSyncService.BuildCronExpression(settings!.PollingIntervalMinutes);
        recurringJobManager.AddOrUpdate<ITaskOrchestrator>(
            jobId,
            HangfireQueues.Default,
            orchestrator => orchestrator.SyncMergedPullRequestsAsync(workspace.Id, CancellationToken.None),
            cronExpression,
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc
            });
    }

    /// <summary>
    /// Removes the recurring Pull Request merge polling Hangfire job for a deleted or decommissioned workspace.
    /// </summary>
    public static void RemoveWorkspacePrMergeRecurringJob(IRecurringJobManager recurringJobManager, int workspaceId)
    {
        ArgumentNullException.ThrowIfNull(recurringJobManager);
        recurringJobManager.RemoveIfExists(GetWorkspacePrMergeRecurringJobId(workspaceId));
    }

    /// <summary>
    /// Synchronizes Hangfire recurring PR merge poll jobs across all provided workspaces.
    /// </summary>
    public static Task SynchronizeAllPrMergeRecurringJobsAsync(
        IRecurringJobManager recurringJobManager,
        IEnumerable<Workspace> workspaces,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recurringJobManager);
        ArgumentNullException.ThrowIfNull(workspaces);

        var activeWorkspaceIds = new HashSet<int>();
        foreach (var workspace in workspaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            activeWorkspaceIds.Add(workspace.Id);
            SynchronizeWorkspacePrMergeRecurringJob(recurringJobManager, workspace);
        }

        GitHubRecurringJobCleanup.RemoveOrphanedWorkspaceRecurringJobs(
            recurringJobManager,
            PrMergeRecurringJobIdPrefix,
            activeWorkspaceIds);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimIssueForExecutionAsync(
        ForgeTask task,
        Workspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(workspace);

        if (!TryGetConfiguredMirroring(workspace, out var settings, out var owner, out var repo))
        {
            return true;
        }

        if (!task.HasExecutionRun &&
            string.IsNullOrWhiteSpace(task.ExternalReferenceId) &&
            settings.CreateGitHubIssuesForLocalTasks)
        {
            await MirrorTaskOpenedAsync(task, workspace, cancellationToken);
        }

        if (!TryGetIssueNumber(task, out var issueNumber))
        {
            return true;
        }

        var answeredConflictQuestion = FindLatestAnsweredClaimConflictQuestion(task);
        if (answeredConflictQuestion != null && IsCancelConflictAnswer(answeredConflictQuestion.Answer))
        {
            await CancelTaskForConflictAsync(
                task,
                issueNumber,
                $"Cancelled task #{task.Id} by maintainer choice after GitHub Issue #{issueNumber} claim conflict.",
                AuditActors.User,
                cancellationToken);
            return false;
        }

        var hasUserOverride = answeredConflictQuestion != null && IsOverrideConflictAnswer(answeredConflictQuestion.Answer);
        if (hasUserOverride && !task.HasExecutionRun)
        {
            task.InterruptedStageId = null;
            task.InterruptedAgentProfileId = null;
            task.IsWaitingForHuman = false;
        }

        GitHubIssue? liveIssue;
        try
        {
            liveIssue = await _gitHubClient.GetIssueAsync(owner, repo, issueNumber, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Pre-flight GitHub claim check failed to fetch Issue #{IssueNumber} in {Owner}/{Repo} for task #{TaskId}; continuing local execution.",
                issueNumber, owner, repo, task.Id);
            await SafeAddExecutionLogAsync(
                task.Id,
                StandardStageIds.TaskReview,
                LogLevel.Warning,
                $"GitHub pre-flight claim check skipped for Issue #{issueNumber} due to API error: {ex.Message}",
                cancellationToken);
            return true;
        }

        if (liveIssue == null)
        {
            return true;
        }

        var workspaceTasks = await _taskRepository.GetByWorkspaceIdAsync(workspace.Id, cancellationToken)
            ?? Array.Empty<ForgeTask>();
        var siblings = workspaceTasks
            .Where(t => t.Id != task.Id && TryGetIssueNumber(t, out var siblingIssueNumber) && siblingIssueNumber == issueNumber)
            .ToList();

        if (string.Equals(liveIssue.State, ClosedIssueState, StringComparison.OrdinalIgnoreCase))
        {
            var closedLocallyBySibling = siblings.Any(s =>
                s.Status == TaskOverallStatus.Closed ||
                (s.Status == TaskOverallStatus.Ready && s.PullRequestNumber is > 0));
            if (!closedLocallyBySibling)
            {
                await CancelTaskForConflictAsync(
                    task,
                    issueNumber,
                    $"Cancelled task #{task.Id} before Stage 1 because linked GitHub Issue #{issueNumber} is already closed on GitHub.",
                    AuditActors.System,
                    cancellationToken);
                return false;
            }
        }

        var authenticatedLogin = await ResolveAuthenticatedLoginAsync(cancellationToken);
        var assignees = liveIssue.Assignees ?? Array.Empty<string>();
        var labels = liveIssue.Labels ?? Array.Empty<string>();

        var isAssignedToCurrentUser = !string.IsNullOrWhiteSpace(authenticatedLogin) &&
            assignees.Any(a => string.Equals(a, authenticatedLogin, StringComparison.OrdinalIgnoreCase));
        var isAssignedToOtherUser = assignees.Count > 0 && !isAssignedToCurrentUser;
        var hasActiveExecutionLabel = labels.Any(l => ActiveExecutionLabels.Contains(l, StringComparer.OrdinalIgnoreCase));
        var hasActiveLocalSibling = siblings.Any(s =>
            s.HasExecutionRun || s.Status is TaskOverallStatus.Ready or TaskOverallStatus.Closed);
        var isInProgressByOther = hasActiveExecutionLabel && !isAssignedToCurrentUser && !hasActiveLocalSibling;

        if (!hasUserOverride && (isAssignedToOtherUser || isInProgressByOther))
        {
            await SuspendTaskForClaimConflictAsync(
                task,
                issueNumber,
                assignees,
                labels,
                isAssignedToOtherUser,
                cancellationToken);
            return false;
        }

        await ApplyClaimAndInProgressOnStartAsync(
            task,
            settings,
            owner,
            repo,
            issueNumber,
            liveIssue,
            authenticatedLogin,
            isAssignedToCurrentUser,
            cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> TryHandleClaimConflictAnswerAsync(
        ForgeTask task,
        Workspace workspace,
        IReadOnlyList<Question> answeredQuestions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(answeredQuestions);

        var conflictQuestion = answeredQuestions
            .FirstOrDefault(q =>
                !string.IsNullOrWhiteSpace(q.BatchId) &&
                q.BatchId.StartsWith(GitHubClaimConflictConstants.ClaimConflictBatchPrefix, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(q.Answer));

        if (conflictQuestion == null)
        {
            return false;
        }

        if (IsCancelConflictAnswer(conflictQuestion.Answer))
        {
            TryGetIssueNumber(task, out var issueNumber);
            var reason = issueNumber > 0
                ? $"Cancelled task #{task.Id} by maintainer choice after GitHub Issue #{issueNumber} claim conflict."
                : $"Cancelled task #{task.Id} by maintainer choice after GitHub Issue claim conflict.";

            await CancelTaskForConflictAsync(task, issueNumber, reason, AuditActors.User, cancellationToken);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public async Task MirrorTaskOpenedAsync(
        ForgeTask task,
        Workspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(workspace);

        if (task.HasExecutionRun || task.Status != TaskOverallStatus.Open)
        {
            return;
        }

        if (!TryGetConfiguredMirroring(workspace, out var settings, out var owner, out var repo))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(task.ExternalReferenceId))
        {
            if (!settings.CreateGitHubIssuesForLocalTasks)
            {
                return;
            }

            try
            {
                var authenticatedLogin = await ResolveAuthenticatedLoginAsync(cancellationToken);
                var createRequest = new CreateGitHubIssueRequest
                {
                    Title = string.IsNullOrWhiteSpace(task.Title) ? $"Task #{task.Id}" : task.Title.Trim(),
                    Body = BuildLocalTaskIssueBody(task),
                    Labels = settings.SyncStatusLabels ? [GitHubSyncConstants.LabelBacklog] : null,
                    Assignees = !string.IsNullOrWhiteSpace(authenticatedLogin) ? [authenticatedLogin] : null
                };

                var createdIssue = await _gitHubClient.CreateIssueAsync(owner, repo, createRequest, cancellationToken);
                if (createdIssue == null || createdIssue.Number <= 0)
                {
                    return;
                }

                task.ExternalSource = GitHubSyncConstants.ExternalSourceGitHub;
                task.ExternalReferenceId = createdIssue.Number.ToString(CultureInfo.InvariantCulture);
                task.UpdatedAt = DateTime.UtcNow;

                await _taskRepository.UpdateWithHistoryAsync(
                    task,
                    dependsOn: null,
                    changedBy: AuditActors.System,
                    reason: $"Created linked GitHub Issue #{createdIssue.Number} in {owner}/{repo} for local task.",
                    cancellationToken: cancellationToken);

                await SafeAddExecutionLogAsync(
                    task.Id,
                    stageId: null,
                    LogLevel.Information,
                    $"Created GitHub Issue #{createdIssue.Number} ({createdIssue.HtmlUrl}) for local Task #{task.Id}.",
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to create GitHub Issue in {Owner}/{Repo} for local Task #{TaskId}.",
                    owner, repo, task.Id);
                await SafeAddExecutionLogAsync(
                    task.Id,
                    stageId: null,
                    LogLevel.Warning,
                    $"GitHub issue creation failed for local Task #{task.Id}: {ex.Message}",
                    cancellationToken);
            }

            return;
        }

        if (settings.SyncStatusLabels && TryGetIssueNumber(task, out var issueNumber))
        {
            var workspaceTasks = await _taskRepository.GetByWorkspaceIdAsync(workspace.Id, cancellationToken)
                ?? Array.Empty<ForgeTask>();
            var hasActiveSibling = workspaceTasks.Any(t =>
                t.Id != task.Id &&
                TryGetIssueNumber(t, out var siblingIssueNumber) &&
                siblingIssueNumber == issueNumber &&
                (t.Status == TaskOverallStatus.Ready ||
                 (t.Status == TaskOverallStatus.Open && t.HasExecutionRun)));

            if (hasActiveSibling)
            {
                _logger.LogDebug(
                    "Skipping ncf:backlog status label update on GitHub Issue #{IssueNumber} for opened Task #{TaskId} because an active sibling task already owns a higher lifecycle state.",
                    issueNumber, task.Id);
                return;
            }

            await ApplyStatusLabelSafeAsync(
                task,
                owner,
                repo,
                issueNumber,
                GitHubSyncConstants.LabelBacklog,
                stageId: null,
                cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task MirrorTaskReviewCompletedAsync(
        ForgeTask task,
        Workspace workspace,
        string? specificationSummary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(workspace);

        if (!TryGetConfiguredMirroring(workspace, out var settings, out var owner, out var repo) ||
            !TryGetIssueNumber(task, out var issueNumber))
        {
            return;
        }

        if (settings.SyncStatusLabels)
        {
            await ApplyStatusLabelSafeAsync(
                task,
                owner,
                repo,
                issueNumber,
                GitHubSyncConstants.LabelInProgress,
                StandardStageIds.TaskReview,
                cancellationToken);
        }

        if (settings.PostMilestoneComments)
        {
            var body = BuildTaskReviewCompletedComment(task, specificationSummary);
            await AddIssueCommentSafeAsync(
                task,
                owner,
                repo,
                issueNumber,
                StandardStageIds.TaskReview,
                body,
                cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task MirrorPostImplementationGatesCompletedAsync(
        ForgeTask task,
        Workspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(workspace);

        if (!TryGetConfiguredMirroring(workspace, out var settings, out var owner, out var repo) ||
            !TryGetIssueNumber(task, out var issueNumber))
        {
            return;
        }

        if (settings.SyncStatusLabels)
        {
            await ApplyStatusLabelSafeAsync(
                task,
                owner,
                repo,
                issueNumber,
                GitHubSyncConstants.LabelGatesPassed,
                StandardStageIds.PostImplementationGates,
                cancellationToken);
        }

        if (settings.PostMilestoneComments)
        {
            var metrics = QualityGateMetricsExtractor.Extract(task);
            var body = BuildPostImplementationGatesComment(task, metrics);
            await AddIssueCommentSafeAsync(
                task,
                owner,
                repo,
                issueNumber,
                StandardStageIds.PostImplementationGates,
                body,
                cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task MirrorTaskReadyAsync(
        ForgeTask task,
        Workspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(workspace);

        if (!TryGetConfiguredMirroring(workspace, out var settings, out var owner, out var repo) ||
            !TryGetIssueNumber(task, out var issueNumber))
        {
            return;
        }

        if (settings.SyncStatusLabels)
        {
            await ApplyStatusLabelSafeAsync(
                task,
                owner,
                repo,
                issueNumber,
                GitHubSyncConstants.LabelReadyForReview,
                StandardStageIds.PostReviewVerification,
                cancellationToken);
        }

        if (settings.PostMilestoneComments)
        {
            var body = BuildTaskReadyComment(task);
            await AddIssueCommentSafeAsync(
                task,
                owner,
                repo,
                issueNumber,
                StandardStageIds.PostReviewVerification,
                body,
                cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task MirrorTaskClosedAsync(
        ForgeTask task,
        Workspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(workspace);

        if (!TryGetConfiguredMirroring(workspace, out var settings, out var owner, out var repo) ||
            !TryGetIssueNumber(task, out var issueNumber))
        {
            return;
        }

        var workspaceTasks = await _taskRepository.GetByWorkspaceIdAsync(workspace.Id, cancellationToken)
            ?? Array.Empty<ForgeTask>();
        var matchingSiblings = workspaceTasks
            .Where(t => t.Id != task.Id &&
                        TryGetIssueNumber(t, out var siblingIssueNumber) &&
                        siblingIssueNumber == issueNumber)
            .OrderBy(t => t.Id)
            .ToList();
        var unclosedSiblings = matchingSiblings
            .Where(t => t.Status is not (TaskOverallStatus.Closed or TaskOverallStatus.Cancelled or TaskOverallStatus.Rejected))
            .ToList();

        var allSiblingsClosed = unclosedSiblings.Count == 0;
        var isCancelledOrRejected = task.Status is TaskOverallStatus.Cancelled or TaskOverallStatus.Rejected;
        if (isCancelledOrRejected)
        {
            var hasCompletedSibling = matchingSiblings.Any(t => t.Status == TaskOverallStatus.Closed);
            if (!allSiblingsClosed || !hasCompletedSibling)
            {
                return;
            }
        }
        else if (settings.PostMilestoneComments)
        {
            var comment = BuildTaskClosedComment(task, unclosedSiblings);
            await AddIssueCommentSafeAsync(
                task,
                owner,
                repo,
                issueNumber,
                StandardStageIds.Merge,
                comment,
                cancellationToken);
        }

        if (!allSiblingsClosed)
        {
            var remainingIds = string.Join(", ", unclosedSiblings.Select(s => $"#{s.Id}"));
            await SafeAddExecutionLogAsync(
                task.Id,
                StandardStageIds.Merge,
                LogLevel.Information,
                $"Kept GitHub Issue #{issueNumber} open because sibling task(s) [{remainingIds}] sharing this issue remain unclosed.",
                cancellationToken);
            return;
        }

        try
        {
            IReadOnlyList<string>? updatedLabels = null;
            if (settings.SyncStatusLabels)
            {
                var currentIssue = await _gitHubClient.GetIssueAsync(owner, repo, issueNumber, cancellationToken);
                updatedLabels = BuildUpdatedLabels(currentIssue?.Labels, GitHubSyncConstants.LabelClosed);
            }

            await _gitHubClient.UpdateIssueAsync(
                owner,
                repo,
                issueNumber,
                new UpdateGitHubIssueRequest
                {
                    State = ClosedIssueState,
                    Labels = updatedLabels
                },
                cancellationToken);

            await SafeAddExecutionLogAsync(
                task.Id,
                StandardStageIds.Merge,
                LogLevel.Information,
                $"Closed linked GitHub Issue #{issueNumber} in {owner}/{repo} after completing Task #{task.Id}.",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to close GitHub Issue #{IssueNumber} in {Owner}/{Repo} for Task #{TaskId}.",
                issueNumber, owner, repo, task.Id);
            await SafeAddExecutionLogAsync(
                task.Id,
                StandardStageIds.Merge,
                LogLevel.Warning,
                $"Failed to close GitHub Issue #{issueNumber} on GitHub: {ex.Message}",
                cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> SyncMergedPullRequestsAsync(
        Workspace workspace,
        Func<int, CancellationToken, Task> closeExternallyMergedTaskAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(closeExternallyMergedTaskAsync);

        if (!TryGetConfiguredRepo(workspace, out var settings, out var owner, out var repo) ||
            !settings.PullRequestDeliveryEnabled ||
            !settings.CloseTaskOnPrMerge)
        {
            return Array.Empty<int>();
        }

        var workspaceTasks = await _taskRepository.GetByWorkspaceIdAsync(workspace.Id, cancellationToken)
            ?? Array.Empty<ForgeTask>();
        var candidates = workspaceTasks
            .Where(t => (t.Status == TaskOverallStatus.Ready || t.Status == TaskOverallStatus.Open) &&
                        t.PullRequestNumber is > 0)
            .OrderBy(t => t.Id)
            .ToList();

        if (candidates.Count == 0)
        {
            return Array.Empty<int>();
        }

        var closedTaskIds = new List<int>();
        foreach (var task in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var prNumber = task.PullRequestNumber!.Value;
            GitHubPullRequest? pr;
            try
            {
                pr = await _gitHubClient.GetPullRequestAsync(owner, repo, prNumber, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed polling GitHub Pull Request #{PrNumber} in {Owner}/{Repo} for Task #{TaskId}.",
                    prNumber, owner, repo, task.Id);
                await SafeAddExecutionLogAsync(
                    task.Id,
                    StandardStageIds.Merge,
                    LogLevel.Warning,
                    $"GitHub PR #{prNumber} merge status poll failed: {ex.Message}",
                    cancellationToken);
                continue;
            }

            if (pr == null || !pr.Merged)
            {
                continue;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(pr.MergeCommitSha) && string.IsNullOrWhiteSpace(task.SquashCommitSha))
                {
                    task.SquashCommitSha = pr.MergeCommitSha;
                    task.UpdatedAt = DateTime.UtcNow;
                    await _taskRepository.UpdateWithHistoryAsync(
                        task,
                        dependsOn: null,
                        changedBy: AuditActors.System,
                        reason: $"Recorded merged GitHub Pull Request #{prNumber} commit SHA {pr.MergeCommitSha}.",
                        cancellationToken: cancellationToken);
                }

                await closeExternallyMergedTaskAsync(task.Id, cancellationToken);
                closedTaskIds.Add(task.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed closing Task #{TaskId} after GitHub Pull Request #{PrNumber} merged in {Owner}/{Repo}.",
                    task.Id, prNumber, owner, repo);
                await SafeAddExecutionLogAsync(
                    task.Id,
                    StandardStageIds.Merge,
                    LogLevel.Warning,
                    $"Failed closing Task #{task.Id} after PR #{prNumber} merged: {ex.Message}",
                    cancellationToken);
            }
        }

        return closedTaskIds;
    }

    private bool TryGetConfiguredRepo(
        Workspace workspace,
        out GitHubIntegrationSettings settings,
        out string owner,
        out string repo)
    {
        if (_securityOptions.AirGappedMode ||
            workspace.GitHubSettings is not { Enabled: true } configured ||
            string.IsNullOrWhiteSpace(configured.RepositoryOwner) ||
            string.IsNullOrWhiteSpace(configured.RepositoryName))
        {
            settings = new GitHubIntegrationSettings();
            owner = string.Empty;
            repo = string.Empty;
            return false;
        }

        settings = configured;
        owner = configured.RepositoryOwner.Trim();
        repo = configured.RepositoryName.Trim();
        return true;
    }

    private bool TryGetConfiguredMirroring(
        Workspace workspace,
        out GitHubIntegrationSettings settings,
        out string owner,
        out string repo)
    {
        if (!TryGetConfiguredRepo(workspace, out settings, out owner, out repo) ||
            !settings.TaskMirroringEnabled)
        {
            return false;
        }

        return true;
    }

    private static bool TryGetIssueNumber(ForgeTask task, out int issueNumber)
    {
        issueNumber = 0;
        if (!string.Equals(task.ExternalSource, GitHubSyncConstants.ExternalSourceGitHub, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(task.ExternalReferenceId))
        {
            return false;
        }

        var normalized = task.ExternalReferenceId.Trim().TrimStart('#');
        return int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out issueNumber)
            && issueNumber > 0;
    }

    private static Question? FindLatestAnsweredClaimConflictQuestion(ForgeTask task)
    {
        return task.Questions?
            .Where(q => !string.IsNullOrWhiteSpace(q.BatchId) &&
                        q.BatchId.StartsWith(GitHubClaimConflictConstants.ClaimConflictBatchPrefix, StringComparison.OrdinalIgnoreCase) &&
                        q.Status == QuestionStatus.Answered &&
                        !string.IsNullOrWhiteSpace(q.Answer))
            .OrderByDescending(q => q.AnsweredAt ?? q.AskedAt)
            .ThenByDescending(q => q.Id)
            .FirstOrDefault();
    }

    private static bool IsCancelConflictAnswer(string? answer) =>
        GitHubClaimConflictConstants.IsCancelAnswer(answer);

    private static bool IsOverrideConflictAnswer(string? answer) =>
        GitHubClaimConflictConstants.IsOverrideAnswer(answer);

    private async Task CancelTaskForConflictAsync(
        ForgeTask task,
        int issueNumber,
        string reason,
        string changedBy,
        CancellationToken cancellationToken)
    {
        task.Status = TaskOverallStatus.Cancelled;
        task.IsWaitingForHuman = false;
        task.InterruptedStageId = null;
        task.InterruptedAgentProfileId = null;
        task.UpdatedAt = DateTime.UtcNow;

        if (task.Questions != null)
        {
            foreach (var pending in task.Questions.Where(q => q.Status == QuestionStatus.Pending))
            {
                pending.Status = QuestionStatus.Skipped;
            }
        }

        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: changedBy,
            reason: reason,
            cancellationToken: cancellationToken);
        await _dependencyStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Cancelled, cancellationToken);
        await _dependencyStore.SetWaitingForHumanAsync(task.Id, false, cancellationToken);
        await TaskBadgeCounter.SynchronizeBadgeCountsAsync(_taskRepository, _telemetryService, cancellationToken);

        await SafeAddExecutionLogAsync(
            task.Id,
            StandardStageIds.TaskReview,
            LogLevel.Warning,
            reason,
            cancellationToken);

        await SafeBroadcastAsync(
            new AgentActivityEvent(
                Guid.NewGuid().ToString("N"),
                task.Id,
                StandardStageIds.TaskReview,
                AgentActivityKind.Notice,
                issueNumber > 0
                    ? $"Task #{task.Id} transitioned to Cancelled due to GitHub Issue #{issueNumber} state."
                    : reason,
                DateTime.UtcNow,
                new Dictionary<string, object?>
                {
                    [AgentActivityMetadataKeys.TaskTitle] = task.Title,
                    [AgentActivityMetadataKeys.Reason] = reason
                }),
            cancellationToken);
    }

    private async Task SuspendTaskForClaimConflictAsync(
        ForgeTask task,
        int issueNumber,
        IReadOnlyList<string> assignees,
        IReadOnlyList<string> labels,
        bool isAssignedToOtherUser,
        CancellationToken cancellationToken)
    {
        var detailParts = new List<string>();
        if (isAssignedToOtherUser)
        {
            detailParts.Add($"assigned to {string.Join(", ", assignees.Select(a => "@" + a))}");
        }

        var activeLabels = labels
            .Where(l => ActiveExecutionLabels.Contains(l, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (activeLabels.Count > 0)
        {
            detailParts.Add($"labeled {string.Join(", ", activeLabels)}");
        }

        var conflictDetails = detailParts.Count > 0
            ? string.Join("; ", detailParts)
            : "claimed by another NightCodeForge instance";

        task.Questions ??= new List<Question>();
        var existingPending = task.Questions.FirstOrDefault(q =>
            q.Status == QuestionStatus.Pending &&
            !string.IsNullOrWhiteSpace(q.BatchId) &&
            q.BatchId.StartsWith(GitHubClaimConflictConstants.ClaimConflictBatchPrefix, StringComparison.OrdinalIgnoreCase));

        var batchId = !string.IsNullOrWhiteSpace(existingPending?.BatchId)
            ? existingPending.BatchId
            : $"{GitHubClaimConflictConstants.ClaimConflictBatchPrefix}{Guid.NewGuid():N}";
        var prompt = $"GitHub Issue #{issueNumber} is already claimed or in progress by another user on GitHub ({conflictDetails}). How should NightCodeForge proceed?";
        var rationale = $"Pre-flight multi-user claim check paused Task #{task.Id} before Stage 1 ({StandardStageIds.TaskReview}) to prevent duplicate work on GitHub Issue #{issueNumber}.";
        var optionsJson = JsonSerializer.Serialize(new[]
        {
            GitHubClaimConflictConstants.OverrideClaimOption,
            GitHubClaimConflictConstants.CancelTaskOption
        });
        var askedAt = DateTime.UtcNow;

        if (existingPending != null)
        {
            existingPending.Prompt = prompt;
            existingPending.Rationale = rationale;
            existingPending.OptionsJson = optionsJson;
            existingPending.AskedAt = askedAt;
        }
        else
        {
            var question = new Question
            {
                TaskId = task.Id,
                StageId = StandardStageIds.TaskReview,
                BatchId = batchId,
                QuestionType = QuestionType.SingleChoice,
                Prompt = prompt,
                Rationale = rationale,
                OptionsJson = optionsJson,
                Status = QuestionStatus.Pending,
                AskedAt = askedAt
            };
            task.Questions.Add(question);
        }

        task.IsWaitingForHuman = true;
        task.InterruptedStageId = StandardStageIds.TaskReview;
        task.CurrentStageId = StandardStageIds.TaskReview;
        task.UpdatedAt = DateTime.UtcNow;

        var auditReason = $"Suspended before Stage 1: GitHub Issue #{issueNumber} is already {conflictDetails}.";

        await _taskRepository.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.System,
            reason: auditReason,
            cancellationToken: cancellationToken);
        await _dependencyStore.SetWaitingForHumanAsync(task.Id, true, cancellationToken);
        await TaskBadgeCounter.SynchronizeBadgeCountsAsync(_taskRepository, _telemetryService, cancellationToken);

        await SafeAddExecutionLogAsync(
            task.Id,
            StandardStageIds.TaskReview,
            LogLevel.Warning,
            auditReason,
            cancellationToken);

        await SafeBroadcastAsync(
            new AgentActivityEvent(
                batchId,
                task.Id,
                StandardStageIds.TaskReview,
                AgentActivityKind.Interrupted,
                prompt,
                DateTime.UtcNow,
                new Dictionary<string, object?>
                {
                    [AgentActivityMetadataKeys.TaskTitle] = task.Title,
                    [AgentActivityMetadataKeys.StageKind] = nameof(WorkflowStageKind.Agentic)
                }),
            cancellationToken);
    }

    private async Task ApplyClaimAndInProgressOnStartAsync(
        ForgeTask task,
        GitHubIntegrationSettings settings,
        string owner,
        string repo,
        int issueNumber,
        GitHubIssue liveIssue,
        string? authenticatedLogin,
        bool isAssignedToCurrentUser,
        CancellationToken cancellationToken)
    {
        var shouldAssign = settings.AutoAssignIssueOnStart &&
            !string.IsNullOrWhiteSpace(authenticatedLogin) &&
            !isAssignedToCurrentUser;
        var shouldApplyLabel = settings.SyncStatusLabels;

        if (!shouldAssign && !shouldApplyLabel)
        {
            return;
        }

        IReadOnlyList<string>? updatedLabels = shouldApplyLabel
            ? BuildUpdatedLabels(liveIssue.Labels, GitHubSyncConstants.LabelInProgress)
            : null;
        IReadOnlyList<string>? updatedAssignees = shouldAssign
            ? [authenticatedLogin!]
            : null;

        try
        {
            await _gitHubClient.UpdateIssueAsync(
                owner,
                repo,
                issueNumber,
                new UpdateGitHubIssueRequest
                {
                    Labels = updatedLabels,
                    Assignees = updatedAssignees
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed claiming/labeling GitHub Issue #{IssueNumber} in {Owner}/{Repo} when starting Task #{TaskId}.",
                issueNumber, owner, repo, task.Id);
            await SafeAddExecutionLogAsync(
                task.Id,
                StandardStageIds.TaskReview,
                LogLevel.Warning,
                $"GitHub issue claim/label update failed for Issue #{issueNumber}: {ex.Message}",
                cancellationToken);
        }
    }

    private async Task ApplyStatusLabelSafeAsync(
        ForgeTask task,
        string owner,
        string repo,
        int issueNumber,
        string targetLabel,
        string? stageId,
        CancellationToken cancellationToken)
    {
        try
        {
            var currentIssue = await _gitHubClient.GetIssueAsync(owner, repo, issueNumber, cancellationToken);
            var existingLabels = currentIssue?.Labels ?? Array.Empty<string>();
            var updatedLabels = BuildUpdatedLabels(existingLabels, targetLabel);
            if (AreLabelSetsEqual(existingLabels, updatedLabels))
            {
                return;
            }

            await _gitHubClient.UpdateIssueAsync(
                owner,
                repo,
                issueNumber,
                new UpdateGitHubIssueRequest { Labels = updatedLabels },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed updating GitHub status label to '{Label}' on Issue #{IssueNumber} in {Owner}/{Repo} for Task #{TaskId}.",
                targetLabel, issueNumber, owner, repo, task.Id);
            await SafeAddExecutionLogAsync(
                task.Id,
                stageId,
                LogLevel.Warning,
                $"GitHub status label update ('{targetLabel}') failed for Issue #{issueNumber}: {ex.Message}",
                cancellationToken);
        }
    }

    private static bool AreLabelSetsEqual(IReadOnlyList<string> existingLabels, IReadOnlyList<string> updatedLabels)
    {
        var existingSet = new HashSet<string>(
            existingLabels.Where(l => !string.IsNullOrWhiteSpace(l)),
            StringComparer.OrdinalIgnoreCase);
        return existingSet.SetEquals(updatedLabels);
    }

    private async Task AddIssueCommentSafeAsync(
        ForgeTask task,
        string owner,
        string repo,
        int issueNumber,
        string? stageId,
        string commentBody,
        CancellationToken cancellationToken)
    {
        try
        {
            await _gitHubClient.AddIssueCommentAsync(owner, repo, issueNumber, commentBody, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed posting GitHub milestone comment on Issue #{IssueNumber} in {Owner}/{Repo} for Task #{TaskId}.",
                issueNumber, owner, repo, task.Id);
            await SafeAddExecutionLogAsync(
                task.Id,
                stageId,
                LogLevel.Warning,
                $"GitHub milestone comment failed for Issue #{issueNumber}: {ex.Message}",
                cancellationToken);
        }
    }

    private async Task<string?> ResolveAuthenticatedLoginAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tokenPayload = await _tokenStore.GetTokenAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(tokenPayload?.AuthenticatedUserLogin))
            {
                return tokenPayload.AuthenticatedUserLogin.Trim();
            }

            var user = await _deviceAuthService.GetAuthenticatedUserAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(user?.Login) ? null : user.Login.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to resolve authenticated GitHub user login during task mirroring.");
            return null;
        }
    }

    private async Task SafeAddExecutionLogAsync(
        int taskId,
        string? stageId,
        LogLevel level,
        string message,
        CancellationToken cancellationToken)
    {
        var redactedMessage = GitCredentialRedactor.Redact(message);
        try
        {
            await _taskRepository.AddExecutionLogAsync(
                new ExecutionLog
                {
                    TaskId = taskId,
                    StageId = stageId,
                    Level = level,
                    Source = LogSourceName,
                    Message = redactedMessage,
                    Timestamp = DateTime.UtcNow
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Failed to persist ExecutionLog for Task #{TaskId}: {Message}", taskId, redactedMessage);
        }
    }

    private async Task SafeBroadcastAsync(AgentActivityEvent activityEvent, CancellationToken cancellationToken)
    {
        try
        {
            await _activityStreamer.BroadcastAsync(activityEvent, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Failed to broadcast AgentActivityEvent ({Kind}) for Task #{TaskId}", activityEvent.Kind, activityEvent.TaskId);
        }
    }

    internal static IReadOnlyList<string> BuildUpdatedLabels(IReadOnlyList<string>? currentLabels, string targetStatusLabel)
    {
        var preserved = (currentLabels ?? Array.Empty<string>())
            .Where(l => !string.IsNullOrWhiteSpace(l) &&
                        !KnownStatusLabels.Contains(l, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return preserved
            .Append(targetStatusLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildLocalTaskIssueBody(ForgeTask task)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Created from NightCodeForge Task #{task.Id} (Base branch: `{task.BaseBranch}`).");
        sb.AppendLine();
        sb.Append(string.IsNullOrWhiteSpace(task.Description)
            ? "_No task description provided._"
            : task.Description.Trim());
        return sb.ToString();
    }

    internal static string BuildTaskReviewCompletedComment(ForgeTask task, string? specificationSummary)
    {
        var summaryText = !string.IsNullOrWhiteSpace(specificationSummary)
            ? specificationSummary.Trim()
            : (!string.IsNullOrWhiteSpace(task.Description) ? task.Description.Trim() : "Specification approved.");

        if (summaryText.Length > MaxSpecificationSummaryChars)
        {
            summaryText = summaryText[..MaxSpecificationSummaryChars] + "\n\n_(truncated)_";
        }

        return $"""
            ### 📋 NightCodeForge — Stage 1 Complete (`TaskReview`)
            Approved specification for local Task **#{task.Id}** (`{task.Title}`):

            {summaryText}
            """;
    }

    internal static string BuildPostImplementationGatesComment(ForgeTask task, QualityGateMetrics metrics)
    {
        var currentCoverageText = metrics.CurrentCoverage.ToString("F2", CultureInfo.InvariantCulture);
        var baselineCoverageText = metrics.BaselineCoverage.ToString("F2", CultureInfo.InvariantCulture);
        var coverageDeltaNumber = metrics.CoverageDelta.ToString("F2", CultureInfo.InvariantCulture);
        var coverageDeltaText = metrics.CoverageDelta >= 0
            ? $"+{coverageDeltaNumber}%"
            : $"{coverageDeltaNumber}%";
        var warningDeltaText = metrics.WarningDelta >= 0
            ? $"+{metrics.WarningDelta.ToString(CultureInfo.InvariantCulture)}"
            : metrics.WarningDelta.ToString(CultureInfo.InvariantCulture);
        var currentWarningsText = metrics.CurrentWarnings.ToString(CultureInfo.InvariantCulture);
        var baselineWarningsText = metrics.BaselineWarnings.ToString(CultureInfo.InvariantCulture);

        return $"""
            ### 🧪 NightCodeForge — Verification Gates Passed (`PostImplementationGates`)
            Deterministic quality gates passed for Task **#{task.Id}** (`{task.Title}`):

            - **Tests:** {metrics.PassedTests.ToString(CultureInfo.InvariantCulture)}/{metrics.TotalTests.ToString(CultureInfo.InvariantCulture)} passed ({metrics.FailedTests.ToString(CultureInfo.InvariantCulture)} failed)
            - **Code Coverage:** {currentCoverageText}% (baseline {baselineCoverageText}%, delta {coverageDeltaText})
            - **Warnings:** {currentWarningsText} (baseline {baselineWarningsText}, delta {warningDeltaText})
            """;
    }

    internal static string BuildTaskReadyComment(ForgeTask task)
    {
        var prSegment = !string.IsNullOrWhiteSpace(task.PullRequestUrl)
            ? task.PullRequestNumber.HasValue
                ? $"Pull Request [#{task.PullRequestNumber.Value}]({task.PullRequestUrl}) is open for review."
                : $"Pull Request: {task.PullRequestUrl}"
            : "Changes verified cleanly and awaiting maintainer review.";

        return $"""
            ### ✅ NightCodeForge — Ready for Review
            All pipeline stages and verification gates passed for Task **#{task.Id}** (`{task.Title}`).

            {prSegment}
            """;
    }

    internal static string BuildTaskClosedComment(ForgeTask task, IReadOnlyList<ForgeTask> unclosedSiblings)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(task.PullRequestUrl))
        {
            details.Add(task.PullRequestNumber.HasValue
                ? $"Pull Request: [#{task.PullRequestNumber.Value}]({task.PullRequestUrl})"
                : $"Pull Request: {task.PullRequestUrl}");
        }

        if (!string.IsNullOrWhiteSpace(task.SquashCommitSha))
        {
            details.Add($"Commit SHA: `{task.SquashCommitSha}`");
        }

        var detailLine = details.Count > 0
            ? string.Join(" | ", details)
            : $"Merged into `{task.BaseBranch}`";

        var siblingNote = unclosedSiblings.Count > 0
            ? $"\n\n> Note: {unclosedSiblings.Count} sibling NightCodeForge task(s) linked to this issue remain open ({string.Join(", ", unclosedSiblings.Select(s => "#" + s.Id))}); keeping this GitHub Issue open until all sibling tasks close."
            : string.Empty;

        return $"""
            ### 🚀 NightCodeForge — Task Completed (`Closed`)
            Task **#{task.Id}** (`{task.Title}`) has been closed ({detailLine}).{siblingNote}
            """;
    }
}
