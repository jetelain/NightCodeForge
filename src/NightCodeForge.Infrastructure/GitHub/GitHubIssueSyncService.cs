using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.BackgroundJobs;

namespace NightCodeForge.Infrastructure.GitHub;

/// <summary>
/// Polls configured GitHub repositories for untrusted issues and dispatches eligible issues into
/// isolated <see cref="ITriageService"/> execution while preventing duplicate triage across local and shared teams.
/// </summary>
public sealed class GitHubIssueSyncService : IGitHubIssueSyncService
{
    private const int ConcurrentExecutionTimeoutSeconds = 60;
    private const int MinPollingIntervalMinutes = 1;
    private const int MaxPollingIntervalMinutes = 1440;
    private const int MinutesPerHour = 60;
    private const int HoursPerDay = 24;
    private const string OpenIssueState = "open";
    private const string RecurringJobIdPrefix = "github-sync-workspace-";
    private const string DefaultTriagedActorDisplay = "NightCodeForge";
    private const string DefaultIssueDescriptionFallback = "No issue body provided on GitHub.";

    private static readonly ConcurrentDictionary<int, SemaphoreSlim> WorkspaceLocks = new();

    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly ITaskRepository _taskRepository;
    private readonly IGitHubClientService _gitHubClientService;
    private readonly IGitHubTokenStore _tokenStore;
    private readonly IGitHubDeviceAuthService _authService;
    private readonly ITriageService _triageService;
    private readonly IOperationsControlService _operationsControlService;
    private readonly SecurityOptions _securityOptions;
    private readonly ILogger<GitHubIssueSyncService> _logger;

    public GitHubIssueSyncService(
        IWorkspaceRepository workspaceRepository,
        ITaskRepository taskRepository,
        IGitHubClientService gitHubClientService,
        IGitHubTokenStore tokenStore,
        IGitHubDeviceAuthService authService,
        ITriageService triageService,
        IOperationsControlService operationsControlService,
        IOptions<SecurityOptions> securityOptions,
        ILogger<GitHubIssueSyncService> logger)
    {
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _gitHubClientService = gitHubClientService ?? throw new ArgumentNullException(nameof(gitHubClientService));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _triageService = triageService ?? throw new ArgumentNullException(nameof(triageService));
        _operationsControlService = operationsControlService ?? throw new ArgumentNullException(nameof(operationsControlService));
        ArgumentNullException.ThrowIfNull(securityOptions);
        _securityOptions = securityOptions.Value ?? throw new ArgumentNullException(nameof(securityOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    [DisableConcurrentExecution(timeoutInSeconds: ConcurrentExecutionTimeoutSeconds)]
    public async Task SyncOpenIssuesAsync(CancellationToken cancellationToken = default)
    {
        if (ShouldSkipSync("global GitHub issue sync"))
        {
            return;
        }

        var workspaces = await _workspaceRepository.GetAllAsync(cancellationToken);
        foreach (var workspace in workspaces)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsSyncEligibleWorkspace(workspace))
            {
                continue;
            }

            try
            {
                await SyncWorkspaceWithLockAsync(workspace.Id, preloadedWorkspace: workspace, cancellationToken);
            }
            catch (OperationsPausedException)
            {
                _logger.LogInformation("Stopping global GitHub issue sync: global operations were paused.");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to synchronize GitHub issues for workspace #{WorkspaceId}.", workspace.Id);
            }
        }
    }

    /// <inheritdoc />
    [DisableConcurrentExecution("github-sync-workspace-{0}", ConcurrentExecutionTimeoutSeconds)]
    public async Task SyncWorkspaceIssuesAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        if (ShouldSkipSync($"workspace #{workspaceId} GitHub issue sync"))
        {
            return;
        }

        try
        {
            await SyncWorkspaceWithLockAsync(workspaceId, preloadedWorkspace: null, cancellationToken);
        }
        catch (OperationsPausedException)
        {
            _logger.LogInformation("Stopping GitHub issue sync for workspace #{WorkspaceId}: global operations were paused.", workspaceId);
        }
        catch (OperationCanceledException ex) when (IsOperationsPausedCancellation(ex, cancellationToken))
        {
            _logger.LogInformation("Stopping GitHub issue sync for workspace #{WorkspaceId}: global operations were paused.", workspaceId);
        }
    }

    /// <summary>
    /// Removes the recurring GitHub issue polling Hangfire job for a deleted or decommissioned workspace.
    /// </summary>
    public static void RemoveWorkspaceRecurringJob(IRecurringJobManager recurringJobManager, int workspaceId)
    {
        ArgumentNullException.ThrowIfNull(recurringJobManager);
        recurringJobManager.RemoveIfExists(GetWorkspaceRecurringJobId(workspaceId));
    }

    /// <summary>
    /// Computes the Hangfire recurring job identifier for a workspace's GitHub issue polling schedule.
    /// </summary>
    public static string GetWorkspaceRecurringJobId(int workspaceId)
    {
        return $"{RecurringJobIdPrefix}{workspaceId}";
    }

    /// <summary>
    /// Converts a polling interval in minutes (1-1440) into a valid 5-field Hangfire cron expression.
    /// </summary>
    public static string BuildCronExpression(int pollingIntervalMinutes)
    {
        var clampedMinutes = Math.Clamp(pollingIntervalMinutes, MinPollingIntervalMinutes, MaxPollingIntervalMinutes);

        if (clampedMinutes == MinPollingIntervalMinutes)
        {
            return "* * * * *";
        }

        if (clampedMinutes < MinutesPerHour)
        {
            return $"*/{clampedMinutes} * * * *";
        }

        if (clampedMinutes == MinutesPerHour)
        {
            return "0 * * * *";
        }

        if (clampedMinutes >= MaxPollingIntervalMinutes)
        {
            return "0 0 * * *";
        }

        var hours = (int)Math.Round(clampedMinutes / (double)MinutesPerHour, MidpointRounding.AwayFromZero);
        if (hours <= 1)
        {
            return "0 * * * *";
        }

        if (hours >= HoursPerDay)
        {
            return "0 0 * * *";
        }

        return $"0 */{hours} * * *";
    }

    /// <summary>
    /// Registers, updates, or removes the Hangfire recurring poll job for a single workspace based on its settings.
    /// </summary>
    public static void SynchronizeWorkspaceRecurringJob(IRecurringJobManager recurringJobManager, Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(recurringJobManager);
        ArgumentNullException.ThrowIfNull(workspace);

        var jobId = GetWorkspaceRecurringJobId(workspace.Id);
        if (!IsSyncEligibleWorkspace(workspace))
        {
            recurringJobManager.RemoveIfExists(jobId);
            return;
        }

        var cronExpression = BuildCronExpression(workspace.GitHubSettings.PollingIntervalMinutes);
        recurringJobManager.AddOrUpdate<IGitHubIssueSyncService>(
            jobId,
            HangfireQueues.Default,
            syncService => syncService.SyncWorkspaceIssuesAsync(workspace.Id, CancellationToken.None),
            cronExpression,
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc
            });
    }

    /// <summary>
    /// Synchronizes Hangfire recurring poll jobs across all provided workspaces.
    /// </summary>
    public static Task SynchronizeAllRecurringJobsAsync(
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
            SynchronizeWorkspaceRecurringJob(recurringJobManager, workspace);
        }

        GitHubRecurringJobCleanup.RemoveOrphanedWorkspaceRecurringJobs(
            recurringJobManager,
            RecurringJobIdPrefix,
            activeWorkspaceIds);

        return Task.CompletedTask;
    }

    private bool ShouldSkipSync(string operationName)
    {
        if (_securityOptions.AirGappedMode)
        {
            _logger.LogDebug("Skipping {Operation}: platform is running in AirGappedMode.", operationName);
            return true;
        }

        if (_operationsControlService.IsPaused)
        {
            _logger.LogDebug("Skipping {Operation}: global operations are currently paused.", operationName);
            return true;
        }

        return false;
    }

    private async Task SyncWorkspaceWithLockAsync(
        int workspaceId,
        Workspace? preloadedWorkspace,
        CancellationToken cancellationToken)
    {
        var semaphore = WorkspaceLocks.GetOrAdd(workspaceId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            if (ShouldSkipSync($"workspace #{workspaceId} GitHub issue sync"))
            {
                return;
            }

            var loadedWorkspace = await _workspaceRepository.GetByIdAsync(workspaceId, cancellationToken);
            var workspace = loadedWorkspace ?? preloadedWorkspace;
            if (workspace == null)
            {
                _logger.LogDebug("Skipping GitHub issue sync: workspace #{WorkspaceId} was not found.", workspaceId);
                return;
            }

            if (!IsSyncEligibleWorkspace(workspace))
            {
                _logger.LogDebug(
                    "Skipping GitHub issue sync for workspace #{WorkspaceId}: integration or issue triage is not enabled/configured.",
                    workspace.Id);
                return;
            }

            await SyncEligibleWorkspaceLockedAsync(workspace, initialLookupFound: loadedWorkspace != null, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task SyncEligibleWorkspaceLockedAsync(
        Workspace workspace,
        bool initialLookupFound,
        CancellationToken cancellationToken)
    {
        var settings = workspace.GitHubSettings;
        var owner = settings.RepositoryOwner!.Trim();
        var repo = settings.RepositoryName!.Trim();
        var filterLabel = string.IsNullOrWhiteSpace(settings.IssueFilterLabel)
            ? null
            : settings.IssueFilterLabel.Trim();

        var openIssues = await _gitHubClientService.ListIssuesAsync(
            owner,
            repo,
            new GitHubIssueFilter
            {
                State = OpenIssueState,
                Labels = filterLabel
            },
            cancellationToken);

        if (openIssues == null || openIssues.Count == 0)
        {
            return;
        }

        var authenticatedLogin = await ResolveAuthenticatedLoginAsync(cancellationToken);

        settings.TriagedIssueNumbers ??= [];
        var triagedWatermark = new HashSet<int>(settings.TriagedIssueNumbers);

        var existingTasks = await _taskRepository.GetByWorkspaceIdAsync(workspace.Id, cancellationToken);
        var existingExternalRefs = existingTasks
            .Where(t => t.Status != TaskOverallStatus.Triage
                        && string.Equals(t.ExternalSource, GitHubSyncConstants.ExternalSourceGitHub, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(t.ExternalReferenceId))
            .Select(t => t.ExternalReferenceId!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var processedIssueIndex = 0;
        var verifiedAfterPreviousIteration = false;
        foreach (var issue in openIssues.OrderBy(i => i.Number))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ShouldSkipSync($"GitHub issue #{issue.Number} triage in workspace #{workspace.Id}"))
            {
                return;
            }

            if (processedIssueIndex > 0 && !verifiedAfterPreviousIteration)
            {
                var latestWorkspace = await _workspaceRepository.GetByIdAsync(workspace.Id, cancellationToken);
                if (IsWorkspaceStoppedOrChanged(workspace, latestWorkspace, initialLookupFound))
                {
                    _logger.LogInformation(
                        "Stopping GitHub issue sync for workspace #{WorkspaceId}: workspace settings changed or triage was disabled mid-run.",
                        workspace.Id);
                    return;
                }
            }

            processedIssueIndex++;
            verifiedAfterPreviousIteration = false;

            if (issue.IsPullRequest || !string.Equals(issue.State, OpenIssueState, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!MatchesAssigneeScope(issue, settings.IssueTriageScope, authenticatedLogin))
            {
                _logger.LogDebug(
                    "Skipping GitHub issue #{IssueNumber} in {Owner}/{Repo}: excluded by triage scope {Scope}.",
                    issue.Number,
                    owner,
                    repo,
                    settings.IssueTriageScope);
                continue;
            }

            var externalRefId = issue.Number.ToString(CultureInfo.InvariantCulture);
            if (triagedWatermark.Contains(issue.Number) || existingExternalRefs.Contains(externalRefId))
            {
                _logger.LogDebug(
                    "Skipping GitHub issue #{IssueNumber} in {Owner}/{Repo}: already recorded locally.",
                    issue.Number,
                    owner,
                    repo);
                continue;
            }

            if (HasAnyNcfLabel(issue.Labels))
            {
                _logger.LogDebug(
                    "Skipping GitHub issue #{IssueNumber} in {Owner}/{Repo}: already carries an '{Prefix}' label.",
                    issue.Number,
                    owner,
                    repo,
                    GitHubSyncConstants.LabelPrefix);
                continue;
            }

            if (ContainsTriagedMarker(issue.Body))
            {
                _logger.LogDebug(
                    "Skipping GitHub issue #{IssueNumber} in {Owner}/{Repo}: issue body contains triaged marker.",
                    issue.Number,
                    owner,
                    repo);
                continue;
            }

            try
            {
                var comments = await _gitHubClientService.ListIssueCommentsAsync(owner, repo, issue.Number, cancellationToken)
                    ?? Array.Empty<GitHubIssueComment>();

                var hasTriagedComment = comments.Any(c => ContainsTriagedMarker(c.Body));
                if (hasTriagedComment)
                {
                    _logger.LogDebug(
                        "Skipping GitHub issue #{IssueNumber} in {Owner}/{Repo}: existing comment contains '{Marker}'.",
                        issue.Number,
                        owner,
                        repo,
                        GitHubSyncConstants.TriagedCommentMarker);
                    await PersistTriagedIssueWatermarkAsync(workspace, settings, triagedWatermark, issue.Number, cancellationToken);
                    continue;
                }

                var request = BuildTriageRequest(workspace, issue, externalRefId);
                var proposedTasks = await _triageService.TriageExternalReportAsync(workspace, request, cancellationToken);

                // Persist the local watermark immediately (even when 0 tasks are proposed) so non-actionable
                // issues are never re-triaged when status labels or comments are disabled or fail to write back.
                existingExternalRefs.Add(externalRefId);
                var shouldContinue = await PersistTriagedIssueWatermarkAsync(workspace, settings, triagedWatermark, issue.Number, cancellationToken);
                verifiedAfterPreviousIteration = true;
                if (!shouldContinue)
                {
                    _logger.LogInformation(
                        "Stopping GitHub issue sync for workspace #{WorkspaceId}: workspace settings changed or triage was disabled during issue #{IssueNumber}.",
                        workspace.Id,
                        issue.Number);
                    return;
                }

                await ApplyTriagedLabelIfEnabledAsync(settings, owner, repo, issue, cancellationToken);
                await PostTriagedCommentIfEnabledAsync(
                    settings,
                    owner,
                    repo,
                    issue.Number,
                    authenticatedLogin,
                    proposedTasks.Count,
                    cancellationToken);
            }
            catch (OperationsPausedException)
            {
                _logger.LogInformation(
                    "Stopping GitHub issue sync for workspace #{WorkspaceId}: global operations were paused while processing issue #{IssueNumber}.",
                    workspace.Id,
                    issue.Number);
                throw;
            }
            catch (OperationCanceledException ex) when (IsOperationsPausedCancellation(ex, cancellationToken))
            {
                _logger.LogInformation(
                    "Stopping GitHub issue sync for workspace #{WorkspaceId}: global operations were paused while processing issue #{IssueNumber}.",
                    workspace.Id,
                    issue.Number);
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to ingest or triage GitHub issue #{IssueNumber} in {Owner}/{Repo} for workspace #{WorkspaceId}.",
                    issue.Number,
                    owner,
                    repo,
                    workspace.Id);
            }
        }
    }

    private async Task<bool> PersistTriagedIssueWatermarkAsync(
        Workspace workspace,
        GitHubIntegrationSettings settings,
        HashSet<int> triagedWatermark,
        int issueNumber,
        CancellationToken cancellationToken)
    {
        if (triagedWatermark.Add(issueNumber))
        {
            settings.TriagedIssueNumbers.Add(issueNumber);
        }

        // Re-fetch latest workspace state under the lock so concurrent settings edits are not clobbered.
        var latestWorkspace = await _workspaceRepository.GetByIdAsync(workspace.Id, cancellationToken);
        if (latestWorkspace != null && GitHubIntegrationSettings.HasRepositoryChanged(workspace.GitHubSettings, latestWorkspace.GitHubSettings))
        {
            return false;
        }

        var targetWorkspace = latestWorkspace ?? workspace;
        targetWorkspace.GitHubSettings ??= new GitHubIntegrationSettings();
        targetWorkspace.GitHubSettings.TriagedIssueNumbers ??= [];

        foreach (var triagedNumber in triagedWatermark)
        {
            if (!targetWorkspace.GitHubSettings.TriagedIssueNumbers.Contains(triagedNumber))
            {
                targetWorkspace.GitHubSettings.TriagedIssueNumbers.Add(triagedNumber);
            }
        }

        await _workspaceRepository.UpdateAsync(targetWorkspace, cancellationToken);
        return latestWorkspace == null || IsSyncEligibleWorkspace(latestWorkspace);
    }

    private bool IsOperationsPausedCancellation(OperationCanceledException exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception.InnerException is OperationsPausedException
               || _operationsControlService.IsPaused;
    }

    private static bool IsWorkspaceStoppedOrChanged(Workspace initialWorkspace, Workspace? latestWorkspace, bool initialLookupFound)
    {
        if (latestWorkspace == null)
        {
            return initialLookupFound;
        }

        return !IsSyncEligibleWorkspace(latestWorkspace)
               || GitHubIntegrationSettings.HasRepositoryChanged(initialWorkspace.GitHubSettings, latestWorkspace.GitHubSettings);
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

            var user = await _authService.GetAuthenticatedUserAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(user?.Login) ? null : user.Login.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to resolve authenticated GitHub user login during issue intake.");
            return null;
        }
    }

    private async Task ApplyTriagedLabelIfEnabledAsync(
        GitHubIntegrationSettings settings,
        string owner,
        string repo,
        GitHubIssue issue,
        CancellationToken cancellationToken)
    {
        if (!settings.SyncStatusLabels || HasAnyNcfLabel(issue.Labels))
        {
            return;
        }

        try
        {
            var latestIssue = await _gitHubClientService.GetIssueAsync(owner, repo, issue.Number, cancellationToken) ?? issue;
            var latestLabels = latestIssue.Labels ?? [];
            if (HasAnyNcfLabel(latestLabels))
            {
                _logger.LogDebug(
                    "Skipping '{Label}' writeback on GitHub issue #{IssueNumber} in {Owner}/{Repo}: issue already carries an '{Prefix}' label.",
                    GitHubSyncConstants.LabelTriaged,
                    issue.Number,
                    owner,
                    repo,
                    GitHubSyncConstants.LabelPrefix);
                return;
            }

            var updatedLabels = latestLabels
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Append(GitHubSyncConstants.LabelTriaged)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            await _gitHubClientService.UpdateIssueAsync(
                owner,
                repo,
                issue.Number,
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
                "Failed to apply '{Label}' label to GitHub issue #{IssueNumber} in {Owner}/{Repo}.",
                GitHubSyncConstants.LabelTriaged,
                issue.Number,
                owner,
                repo);
        }
    }

    private async Task PostTriagedCommentIfEnabledAsync(
        GitHubIntegrationSettings settings,
        string owner,
        string repo,
        int issueNumber,
        string? authenticatedLogin,
        int proposedTaskCount,
        CancellationToken cancellationToken)
    {
        if (!settings.PostMilestoneComments)
        {
            return;
        }

        try
        {
            var latestComments = await _gitHubClientService.ListIssueCommentsAsync(owner, repo, issueNumber, cancellationToken)
                ?? Array.Empty<GitHubIssueComment>();
            if (latestComments.Any(c => ContainsTriagedMarker(c.Body)))
            {
                _logger.LogDebug(
                    "Skipping triage acknowledgment comment on GitHub issue #{IssueNumber} in {Owner}/{Repo}: comment with '{Marker}' already exists.",
                    issueNumber,
                    owner,
                    repo,
                    GitHubSyncConstants.TriagedCommentMarker);
                return;
            }

            var commentBody = BuildTriagedCommentBody(authenticatedLogin, proposedTaskCount);
            await _gitHubClientService.AddIssueCommentAsync(owner, repo, issueNumber, commentBody, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to post triage acknowledgment comment to GitHub issue #{IssueNumber} in {Owner}/{Repo}.",
                issueNumber,
                owner,
                repo);
        }
    }

    internal static string BuildTriagedCommentBody(string? authenticatedLogin, int proposedTaskCount)
    {
        var actor = string.IsNullOrWhiteSpace(authenticatedLogin)
            ? DefaultTriagedActorDisplay
            : $"@{authenticatedLogin.Trim()}";

        var summary = proposedTaskCount > 0
            ? $"Proposed {proposedTaskCount} candidate backlog task(s) for maintainer approval in NightCodeForge."
            : "Automated triage completed with 0 actionable tasks proposed.";

        return $"{GitHubSyncConstants.TriagedCommentMarker}\nAutomated triage completed by NightCodeForge ({actor}). {summary}";
    }

    private static bool IsSyncEligibleWorkspace(Workspace workspace)
    {
        var settings = workspace.GitHubSettings;
        return settings != null
               && settings.Enabled
               && settings.IssueTriageEnabled
               && !string.IsNullOrWhiteSpace(settings.RepositoryOwner)
               && !string.IsNullOrWhiteSpace(settings.RepositoryName);
    }

    private static bool MatchesAssigneeScope(
        GitHubIssue issue,
        GitHubIssueTriageScope scope,
        string? authenticatedLogin)
    {
        return scope switch
        {
            GitHubIssueTriageScope.AllOpenIssues => true,
            GitHubIssueTriageScope.AssignedToMeOnly => !string.IsNullOrWhiteSpace(authenticatedLogin)
                && issue.Assignees.Any(a => string.Equals(a, authenticatedLogin, StringComparison.OrdinalIgnoreCase)),
            GitHubIssueTriageScope.AssignedToMeOrUnassigned => issue.Assignees.Count == 0
                || (!string.IsNullOrWhiteSpace(authenticatedLogin)
                    && issue.Assignees.Any(a => string.Equals(a, authenticatedLogin, StringComparison.OrdinalIgnoreCase))),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, $"Unsupported GitHub issue triage scope '{scope}'.")
        };
    }

    private static bool HasAnyNcfLabel(IReadOnlyList<string> labels)
    {
        return labels.Any(l => l.StartsWith(GitHubSyncConstants.LabelPrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsTriagedMarker(string? content)
    {
        return !string.IsNullOrWhiteSpace(content)
               && content.Contains(GitHubSyncConstants.TriagedCommentMarker, StringComparison.OrdinalIgnoreCase);
    }

    private static TriageReportRequest BuildTriageRequest(
        Workspace workspace,
        GitHubIssue issue,
        string externalReferenceId)
    {
        var title = string.IsNullOrWhiteSpace(issue.Title)
            ? $"GitHub Issue #{issue.Number}"
            : $"GitHub Issue #{issue.Number}: {issue.Title.Trim()}";

        var description = BuildIssueDescription(issue);
        var baseBranch = string.IsNullOrWhiteSpace(workspace.DefaultBaseBranch)
            ? WorkflowDefaults.DefaultBaseBranch
            : workspace.DefaultBaseBranch.Trim();

        return new TriageReportRequest(
            Title: title,
            Description: description,
            BaseBranch: baseBranch,
            Origin: TaskOrigin.GitHubIssue,
            ExternalSource: GitHubSyncConstants.ExternalSourceGitHub,
            ExternalReferenceId: externalReferenceId);
    }

    private static string BuildIssueDescription(GitHubIssue issue)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"GitHub Issue: #{issue.Number}");

        if (!string.IsNullOrWhiteSpace(issue.HtmlUrl))
        {
            sb.AppendLine($"URL: {issue.HtmlUrl.Trim()}");
        }

        if (issue.Labels.Count > 0)
        {
            sb.AppendLine($"Labels: {string.Join(", ", issue.Labels)}");
        }

        sb.AppendLine();
        sb.Append(string.IsNullOrWhiteSpace(issue.Body)
            ? DefaultIssueDescriptionFallback
            : issue.Body.Trim());

        return sb.ToString();
    }
}
