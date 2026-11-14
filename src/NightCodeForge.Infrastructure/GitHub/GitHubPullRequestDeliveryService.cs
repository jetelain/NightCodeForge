using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Infrastructure.GitHub;

/// <summary>
/// Orchestrates GitHub outbound Pull Request delivery, remote base branch synchronization,
/// local task markdown stripping, lease-guarded branch pushing, and external merge verification.
/// </summary>
public partial class GitHubPullRequestDeliveryService : IGitHubPullRequestDeliveryService
{
    private const string DefaultSanitizedLogin = "user";
    private const string GitHubExternalSource = "GitHub";
    private const string ClosedPullRequestState = "closed";
    private const string StripMarkdownCommitMessage = "chore(ncf): strip local task markdown before PR delivery";

    private readonly IGitCliRunner _gitCliRunner;
    private readonly IGitWorkspaceManager _gitWorkspaceManager;
    private readonly IGitMirrorSyncService _mirrorSyncService;
    private readonly IGitHubDeviceAuthService _authService;
    private readonly IGitHubTokenStore _tokenStore;
    private readonly IGitHubClientService _gitHubClient;
    private readonly ITaskRepository _taskRepository;
    private readonly IOptions<SecurityOptions> _securityOptions;
    private readonly ILogger<GitHubPullRequestDeliveryService> _logger;

    [GeneratedRegex(@"[^a-z0-9-]+", RegexOptions.Compiled)]
    private static partial Regex NonAlphanumericOrHyphenRegex();

    [GeneratedRegex(@"-{2,}", RegexOptions.Compiled)]
    private static partial Regex ConsecutiveHyphensRegex();

    public GitHubPullRequestDeliveryService(
        IGitCliRunner gitCliRunner,
        IGitWorkspaceManager gitWorkspaceManager,
        IGitMirrorSyncService mirrorSyncService,
        IGitHubDeviceAuthService authService,
        IGitHubTokenStore tokenStore,
        IGitHubClientService gitHubClient,
        ITaskRepository taskRepository,
        IOptions<SecurityOptions> securityOptions,
        ILogger<GitHubPullRequestDeliveryService> logger)
    {
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        _gitWorkspaceManager = gitWorkspaceManager ?? throw new ArgumentNullException(nameof(gitWorkspaceManager));
        _mirrorSyncService = mirrorSyncService ?? throw new ArgumentNullException(nameof(mirrorSyncService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _gitHubClient = gitHubClient ?? throw new ArgumentNullException(nameof(gitHubClient));
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _securityOptions = securityOptions ?? throw new ArgumentNullException(nameof(securityOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> IsPullRequestDeliveryAvailableAsync(Workspace? workspace, CancellationToken cancellationToken = default)
    {
        if (_securityOptions.Value.AirGappedMode)
        {
            return false;
        }

        if (workspace?.GitHubSettings is not { Enabled: true, PullRequestDeliveryEnabled: true } settings ||
            string.IsNullOrWhiteSpace(settings.RepositoryOwner) ||
            string.IsNullOrWhiteSpace(settings.RepositoryName))
        {
            return false;
        }

        return await _tokenStore.HasTokenAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string SanitizeBranchSegment(string? login)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return DefaultSanitizedLogin;
        }

        var lowered = login.Trim().ToLowerInvariant();
        var replaced = NonAlphanumericOrHyphenRegex().Replace(lowered, "-");
        var collapsed = ConsecutiveHyphensRegex().Replace(replaced, "-").Trim('-');

        return string.IsNullOrWhiteSpace(collapsed) ? DefaultSanitizedLogin : collapsed;
    }

    public static string BuildRemoteBranchName(string? login, int taskId) =>
        $"ncf/{SanitizeBranchSegment(login)}/task-{taskId}";

    public async Task FetchAndFastForwardBaseBranchFromRemoteAsync(
        Workspace workspace,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);

        if (_securityOptions.Value.AirGappedMode)
        {
            return;
        }

        if (workspace.GitHubSettings is not { Enabled: true } settings ||
            string.IsNullOrWhiteSpace(settings.RepositoryOwner) ||
            string.IsNullOrWhiteSpace(settings.RepositoryName))
        {
            return;
        }

        if (!await _tokenStore.HasTokenAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var canonicalPath = ResolveCanonicalRepoPath(workspace);
        if (string.IsNullOrWhiteSpace(canonicalPath) || !WorkspaceDirectoryExists(canonicalPath, workspace))
        {
            return;
        }

        var accessToken = await _authService.GetValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var remoteUrl = ResolveRemoteUrl(settings);
        var authArgs = BuildAuthArgs(accessToken, remoteUrl);

        _logger.LogInformation(
            "Fetching remote base branch '{BaseBranch}' from GitHub for workspace #{WorkspaceId}",
            baseBranch, workspace.Id);

        await _gitCliRunner.ExecuteAsync(
            canonicalPath,
            [..authArgs, "fetch", remoteUrl, baseBranch],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var headBranchResult = await _gitCliRunner.ExecuteAsync(
            canonicalPath,
            ["rev-parse", "--abbrev-ref", "HEAD"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var currentBranch = headBranchResult.StandardOutput.Trim();

        if (string.Equals(currentBranch, baseBranch, StringComparison.Ordinal))
        {
            await _gitCliRunner.ExecuteAsync(
                canonicalPath,
                ["merge", "--ff-only", "FETCH_HEAD"],
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _gitCliRunner.ExecuteAsync(
                canonicalPath,
                ["fetch", ".", $"FETCH_HEAD:{baseBranch}"],
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath) &&
            string.Equals(canonicalPath, workspace.Ext4MirrorPath, StringComparison.OrdinalIgnoreCase))
        {
            await _mirrorSyncService.PushBaseBranchToHostAsync(workspace.Ext4MirrorPath, baseBranch, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StripTaskMarkdownDeliverablesFromBranchAsync(
        string workingDirectory,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);

        // 1. Remove any NCF-managed docs/tasks files from index and working tree, including untracked files
        await _gitCliRunner.ExecuteAsync(
            workingDirectory,
            ["rm", "-rf", "--ignore-unmatch", "--", "docs/tasks"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await _gitCliRunner.ExecuteAsync(
            workingDirectory,
            ["clean", "-fd", "--", "docs/tasks"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // 2. Restore any docs/tasks files that legitimately existed on the base branch
        var lsTree = await _gitCliRunner.ExecuteAsync(
            workingDirectory,
            ["ls-tree", baseBranch, "--", "docs/tasks"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(lsTree.StandardOutput))
        {
            await _gitCliRunner.ExecuteAsync(
                workingDirectory,
                ["checkout", baseBranch, "--", "docs/tasks"],
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // 3. Commit only if staged index entries under docs/tasks actually differ from HEAD
        // (ignores untracked files in docs/tasks so `git commit` never fails with "nothing added to commit").
        var stagedDiff = await _gitCliRunner.ExecuteAsync(
            workingDirectory,
            ["diff", "--cached", "--name-only", "--", "docs/tasks"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(stagedDiff.StandardOutput))
        {
            await _gitCliRunner.ExecuteAsync(
                workingDirectory,
                ["commit", "-m", StripMarkdownCommitMessage, "--no-verify", "--", "docs/tasks"],
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StripTaskMarkdownDeliverablesFromRefPlumbingAsync(
        string repoDirectory,
        string localBranch,
        string baseBranch,
        CancellationToken cancellationToken)
    {
        var branchRef = $"refs/heads/{localBranch}";
        var hasBranchTasks = !string.IsNullOrWhiteSpace(
            (await _gitCliRunner.ExecuteAsync(
                repoDirectory,
                ["ls-tree", branchRef, "--", "docs/tasks"],
                cancellationToken: cancellationToken).ConfigureAwait(false)).StandardOutput);

        var hasBaseTasks = !string.IsNullOrWhiteSpace(
            (await _gitCliRunner.ExecuteAsync(
                repoDirectory,
                ["ls-tree", baseBranch, "--", "docs/tasks"],
                cancellationToken: cancellationToken).ConfigureAwait(false)).StandardOutput);

        if (!hasBranchTasks && !hasBaseTasks)
        {
            return;
        }

        var isUnixTarget = repoDirectory.StartsWith('/') || repoDirectory.StartsWith('~') || repoDirectory.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase);
        var tempIndexPath = isUnixTarget
            ? $"/tmp/ncf-strip-idx-{Guid.NewGuid():N}"
            : Path.Combine(Path.GetTempPath(), $"ncf-strip-idx-{Guid.NewGuid():N}");

        var indexEnv = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_INDEX_FILE"] = tempIndexPath
        };

        try
        {
            var oldTree = (await _gitCliRunner.ExecuteAsync(
                repoDirectory,
                ["rev-parse", $"{branchRef}^{{tree}}"],
                cancellationToken: cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();

            await _gitCliRunner.ExecuteAsync(
                repoDirectory,
                ["read-tree", branchRef],
                timeoutSeconds: null,
                environmentVariables: indexEnv,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await _gitCliRunner.ExecuteAsync(
                repoDirectory,
                ["rm", "-r", "--cached", "--ignore-unmatch", "--", "docs/tasks"],
                timeoutSeconds: null,
                environmentVariables: indexEnv,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (hasBaseTasks)
            {
                await _gitCliRunner.ExecuteAsync(
                    repoDirectory,
                    ["read-tree", "--prefix=docs/tasks/", $"{baseBranch}:docs/tasks"],
                    timeoutSeconds: null,
                    environmentVariables: indexEnv,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var newTree = (await _gitCliRunner.ExecuteAsync(
                repoDirectory,
                ["write-tree"],
                timeoutSeconds: null,
                environmentVariables: indexEnv,
                cancellationToken: cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();

            if (!string.Equals(newTree, oldTree, StringComparison.OrdinalIgnoreCase))
            {
                var newCommit = (await _gitCliRunner.ExecuteAsync(
                    repoDirectory,
                    ["commit-tree", newTree, "-p", branchRef, "-m", StripMarkdownCommitMessage],
                    timeoutSeconds: null,
                    environmentVariables: indexEnv,
                    cancellationToken: cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();

                await _gitCliRunner.ExecuteAsync(
                    repoDirectory,
                    ["update-ref", branchRef, newCommit],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!isUnixTarget && File.Exists(tempIndexPath))
            {
                try
                {
                    File.Delete(tempIndexPath);
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "Best-effort cleanup failed for temp index file '{TempIndexPath}'", tempIndexPath);
                }
            }
        }
    }

    public async Task<string> PushTaskBranchWithLeaseAsync(
        string repoDirectory,
        string remoteUrl,
        string localBranch,
        string remoteBranchName,
        string? accessToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(localBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteBranchName);

        var authArgs = BuildAuthArgs(accessToken, remoteUrl);
        var remoteRef = $"refs/heads/{remoteBranchName}";

        // 1. Query current remote branch tip via ls-remote
        var lsRemote = await _gitCliRunner.ExecuteAsync(
            repoDirectory,
            [..authArgs, "ls-remote", "--refs", remoteUrl, remoteRef],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var expectedSha = ExtractFirstRefSha(lsRemote.StandardOutput);

        // 2. Push with explicit expected SHA lease
        await _gitCliRunner.ExecuteAsync(
            repoDirectory,
            [
                ..authArgs,
                "push",
                $"--force-with-lease={remoteRef}:{expectedSha}",
                remoteUrl,
                $"{localBranch}:{remoteRef}"
            ],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var revParse = await _gitCliRunner.ExecuteAsync(
            repoDirectory,
            ["rev-parse", localBranch],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return revParse.StandardOutput.Trim();
    }

    public async Task<GitHubPullRequest> DeliverPullRequestAsync(
        ForgeTask task,
        Workspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(workspace);

        if (_securityOptions.Value.AirGappedMode)
        {
            throw new InvalidOperationException("GitHub Pull Request delivery is disabled while Air-Gapped Mode is active.");
        }

        if (workspace.GitHubSettings is not { Enabled: true, PullRequestDeliveryEnabled: true } settings ||
            string.IsNullOrWhiteSpace(settings.RepositoryOwner) ||
            string.IsNullOrWhiteSpace(settings.RepositoryName))
        {
            throw new InvalidOperationException("GitHub Pull Request delivery is not enabled or configured for this workspace.");
        }

        var baseBranch = string.IsNullOrWhiteSpace(task.BaseBranch)
            ? (workspace.DefaultBaseBranch ?? "main")
            : task.BaseBranch;
        var localBranch = string.IsNullOrWhiteSpace(task.FeatureBranch)
            ? $"feature/task-{task.Id}"
            : task.FeatureBranch;

        var worktreePath = _gitWorkspaceManager.GetWorktreePath(workspace, task.Id);
        var canonicalPath = ResolveCanonicalRepoPath(workspace);
        var hasWorktree = !string.IsNullOrWhiteSpace(worktreePath) && WorkspaceDirectoryExists(worktreePath, workspace);
        var hasCanonical = !string.IsNullOrWhiteSpace(canonicalPath) && WorkspaceDirectoryExists(canonicalPath, workspace);
        var defaultBranch = $"feature/task-{task.Id}";

        if (hasWorktree)
        {
            var worktreeHead = (await _gitCliRunner.ExecuteAsync(
                worktreePath!,
                ["rev-parse", "--abbrev-ref", "HEAD"],
                cancellationToken: cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
            if (!string.IsNullOrWhiteSpace(worktreeHead) && !string.Equals(worktreeHead, "HEAD", StringComparison.Ordinal))
            {
                localBranch = worktreeHead;
            }

            await StripTaskMarkdownDeliverablesFromBranchAsync(worktreePath!, baseBranch, cancellationToken).ConfigureAwait(false);
        }
        else if (hasCanonical)
        {
            localBranch = await ResolveExistingLocalBranchAsync(canonicalPath!, localBranch, defaultBranch, cancellationToken).ConfigureAwait(false);
            var headResult = await _gitCliRunner.ExecuteAsync(
                canonicalPath!,
                ["rev-parse", "--abbrev-ref", "HEAD"],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (string.Equals(headResult.StandardOutput.Trim(), localBranch, StringComparison.Ordinal))
            {
                await StripTaskMarkdownDeliverablesFromBranchAsync(canonicalPath!, baseBranch, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await StripTaskMarkdownDeliverablesFromRefPlumbingAsync(canonicalPath!, localBranch, baseBranch, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath) && WorkspaceDirectoryExists(workspace.Ext4MirrorPath, workspace))
        {
            await _mirrorSyncService.PushToHostAsync(workspace.Ext4MirrorPath, task.Id, cancellationToken).ConfigureAwait(false);
        }

        var pushRepoDir = hasCanonical
            ? canonicalPath
            : (hasWorktree ? worktreePath : null);

        if (string.IsNullOrWhiteSpace(pushRepoDir))
        {
            throw new InvalidOperationException($"Repository directory not found for workspace #{workspace.Id} while delivering Pull Request for task #{task.Id}.");
        }

        localBranch = await ResolveExistingLocalBranchAsync(pushRepoDir, localBranch, defaultBranch, cancellationToken).ConfigureAwait(false);

        var accessToken = await _authService.GetValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var login = await ResolveAuthenticatedLoginAsync(cancellationToken).ConfigureAwait(false);
        var remoteBranchName = BuildRemoteBranchName(login, task.Id);
        var remoteUrl = ResolveRemoteUrl(settings);

        await PushTaskBranchWithLeaseAsync(
            pushRepoDir,
            remoteUrl,
            localBranch,
            remoteBranchName,
            accessToken,
            cancellationToken).ConfigureAwait(false);

        var title = BuildPullRequestTitle(task);
        var body = await BuildPullRequestBodyAsync(task, workspace, baseBranch, cancellationToken).ConfigureAwait(false);
        var owner = settings.RepositoryOwner.Trim();
        var repo = settings.RepositoryName.Trim();

        GitHubPullRequest pr;
        if (task.PullRequestNumber is null)
        {
            pr = await CreatePullRequestAsync(owner, repo, title, remoteBranchName, baseBranch, body, settings.CreateAsDraft, task.Id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation(
                "Updating existing GitHub Pull Request #{PrNumber} for task #{TaskId} ({Head} -> {Base}) in {Owner}/{Repo}",
                task.PullRequestNumber.Value, task.Id, remoteBranchName, baseBranch, owner, repo);

            pr = await UpdateOrRecreatePullRequestAsync(
                owner,
                repo,
                task.PullRequestNumber.Value,
                title,
                remoteBranchName,
                baseBranch,
                body,
                settings.CreateAsDraft,
                task.Id,
                cancellationToken).ConfigureAwait(false);
        }

        task.PullRequestNumber = pr.Number;
        if (!string.IsNullOrWhiteSpace(pr.HtmlUrl))
        {
            task.PullRequestUrl = pr.HtmlUrl;
        }

        return pr;
    }

    public async Task<GitHubPullRequest> VerifyAndSyncExternallyMergedPullRequestAsync(
        ForgeTask task,
        Workspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(workspace);

        if (!task.PullRequestNumber.HasValue)
        {
            throw new InvalidOperationException($"ForgeTask #{task.Id} does not have an associated GitHub Pull Request.");
        }

        if (_securityOptions.Value.AirGappedMode)
        {
            throw new InvalidOperationException("Cannot verify external GitHub Pull Request while Air-Gapped Mode is active.");
        }

        if (workspace.GitHubSettings is not { Enabled: true } settings ||
            string.IsNullOrWhiteSpace(settings.RepositoryOwner) ||
            string.IsNullOrWhiteSpace(settings.RepositoryName))
        {
            throw new InvalidOperationException("GitHub integration is not enabled or configured for this workspace.");
        }

        var owner = settings.RepositoryOwner.Trim();
        var repo = settings.RepositoryName.Trim();
        var prNumber = task.PullRequestNumber.Value;

        var pr = await _gitHubClient.GetPullRequestAsync(owner, repo, prNumber, cancellationToken).ConfigureAwait(false);
        if (!pr.Merged)
        {
            throw new InvalidOperationException(
                $"GitHub Pull Request #{prNumber} has not been merged on GitHub (current state: '{pr.State}'). Please merge it on GitHub first, or use 'Approve & Merge Locally'.");
        }

        var baseBranch = string.IsNullOrWhiteSpace(task.BaseBranch)
            ? (workspace.DefaultBaseBranch ?? "main")
            : task.BaseBranch;

        try
        {
            await FetchAndFastForwardBaseBranchFromRemoteAsync(workspace, baseBranch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Best-effort remote base branch sync for '{BaseBranch}' failed while closing externally merged PR #{PrNumber} on task #{TaskId}; continuing task closure.",
                baseBranch,
                prNumber,
                task.Id);
        }

        return pr;
    }

    private async Task<string> ResolveExistingLocalBranchAsync(
        string repoDirectory,
        string preferredBranch,
        string fallbackBranch,
        CancellationToken cancellationToken)
    {
        if (await LocalBranchExistsAsync(repoDirectory, preferredBranch, cancellationToken).ConfigureAwait(false))
        {
            return preferredBranch;
        }

        if (!string.IsNullOrWhiteSpace(fallbackBranch) &&
            !string.Equals(fallbackBranch, preferredBranch, StringComparison.Ordinal) &&
            await LocalBranchExistsAsync(repoDirectory, fallbackBranch, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Configured feature branch '{PreferredBranch}' does not exist locally in '{RepoDirectory}'; falling back to '{FallbackBranch}'.",
                preferredBranch,
                repoDirectory,
                fallbackBranch);
            return fallbackBranch;
        }

        return preferredBranch;
    }

    private async Task<bool> LocalBranchExistsAsync(
        string repoDirectory,
        string branchName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return false;
        }

        var result = await _gitCliRunner.ExecuteAsync(
            repoDirectory,
            ["for-each-ref", "--format=%(refname:short)", $"refs/heads/{branchName}"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return !string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    private async Task<GitHubPullRequest> CreatePullRequestAsync(
        string owner,
        string repo,
        string title,
        string head,
        string baseBranch,
        string body,
        bool draft,
        int taskId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Creating new GitHub Pull Request for task #{TaskId} ({Head} -> {Base}, Draft={Draft}) in {Owner}/{Repo}",
            taskId, head, baseBranch, draft, owner, repo);

        try
        {
            return await _gitHubClient.CreatePullRequestAsync(
                owner,
                repo,
                new CreateGitHubPullRequestRequest
                {
                    Title = title,
                    Head = head,
                    Base = baseBranch,
                    Body = body,
                    Draft = draft
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var existingOpenPr = await _gitHubClient.FindOpenPullRequestByHeadAsync(owner, repo, head, cancellationToken).ConfigureAwait(false);
            if (existingOpenPr != null)
            {
                _logger.LogInformation(
                    "Found existing open Pull Request #{PrNumber} for head '{Head}' after 422 response; updating PR metadata instead.",
                    existingOpenPr.Number,
                    head);

                return await _gitHubClient.UpdatePullRequestAsync(
                    owner,
                    repo,
                    existingOpenPr.Number,
                    new UpdateGitHubPullRequestRequest
                    {
                        Title = title,
                        Body = body,
                        Base = baseBranch
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task<GitHubPullRequest> UpdateOrRecreatePullRequestAsync(
        string owner,
        string repo,
        int prNumber,
        string title,
        string head,
        string baseBranch,
        string body,
        bool draft,
        int taskId,
        CancellationToken cancellationToken)
    {
        try
        {
            var updatedPr = await _gitHubClient.UpdatePullRequestAsync(
                owner,
                repo,
                prNumber,
                new UpdateGitHubPullRequestRequest
                {
                    Title = title,
                    Body = body,
                    Base = baseBranch
                },
                cancellationToken).ConfigureAwait(false);

            if (string.Equals(updatedPr.State, ClosedPullRequestState, StringComparison.OrdinalIgnoreCase) &&
                !updatedPr.Merged)
            {
                _logger.LogInformation(
                    "Pull Request #{PrNumber} for task #{TaskId} was closed without merging; recovering by locating or creating a fresh open PR.",
                    prNumber,
                    taskId);

                var existingOpenPr = await _gitHubClient.FindOpenPullRequestByHeadAsync(owner, repo, head, cancellationToken).ConfigureAwait(false);
                if (existingOpenPr != null)
                {
                    return await _gitHubClient.UpdatePullRequestAsync(
                        owner,
                        repo,
                        existingOpenPr.Number,
                        new UpdateGitHubPullRequestRequest
                        {
                            Title = title,
                            Body = body,
                            Base = baseBranch
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                return await CreatePullRequestAsync(owner, repo, title, head, baseBranch, body, draft, taskId, cancellationToken).ConfigureAwait(false);
            }

            return updatedPr;
        }
        catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            _logger.LogWarning(
                ex,
                "Updating Pull Request #{PrNumber} for task #{TaskId} failed with HTTP {StatusCode}; attempting recovery via open head PR or new PR creation.",
                prNumber,
                taskId,
                ex.StatusCode);

            var existingOpenPr = await _gitHubClient.FindOpenPullRequestByHeadAsync(owner, repo, head, cancellationToken).ConfigureAwait(false);
            if (existingOpenPr != null)
            {
                return await _gitHubClient.UpdatePullRequestAsync(
                    owner,
                    repo,
                    existingOpenPr.Number,
                    new UpdateGitHubPullRequestRequest
                    {
                        Title = title,
                        Body = body,
                        Base = baseBranch
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            return await CreatePullRequestAsync(owner, repo, title, head, baseBranch, body, draft, taskId, cancellationToken).ConfigureAwait(false);
        }
    }

    public static string BuildPullRequestTitle(ForgeTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var baseTitle = string.IsNullOrWhiteSpace(task.Title) ? $"Task #{task.Id}" : task.Title.Trim();

        if (string.Equals(task.ExternalSource, GitHubExternalSource, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(task.ExternalReferenceId))
        {
            var normalizedIssueNum = task.ExternalReferenceId.Trim().TrimStart('#');
            if (!string.IsNullOrWhiteSpace(normalizedIssueNum) &&
                !Regex.IsMatch(baseTitle, $@"(?<!\d)#{Regex.Escape(normalizedIssueNum)}(?!\d)", RegexOptions.IgnoreCase))
            {
                return $"{baseTitle} (#{normalizedIssueNum})";
            }
        }

        return baseTitle;
    }

    public async Task<string> BuildPullRequestBodyAsync(
        ForgeTask task,
        Workspace workspace,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            sb.AppendLine(task.Description.Trim());
            sb.AppendLine();
        }

        if (string.Equals(task.ExternalSource, GitHubExternalSource, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(task.ExternalReferenceId))
        {
            var normalizedIssueNum = task.ExternalReferenceId.Trim().TrimStart('#');
            if (!string.IsNullOrWhiteSpace(normalizedIssueNum))
            {
                var workspaceTasks = await _taskRepository.GetByWorkspaceIdAsync(workspace.Id, cancellationToken).ConfigureAwait(false);
                var hasActiveSibling = workspaceTasks.Any(s =>
                    s.Id != task.Id &&
                    string.Equals(s.ExternalSource, GitHubExternalSource, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(s.ExternalReferenceId) &&
                    string.Equals(s.ExternalReferenceId.Trim().TrimStart('#'), normalizedIssueNum, StringComparison.OrdinalIgnoreCase) &&
                    s.Status is not (TaskOverallStatus.Closed or TaskOverallStatus.Rejected or TaskOverallStatus.Cancelled));

                var keywordLine = hasActiveSibling
                    ? $"Refs #{normalizedIssueNum}"
                    : $"Resolves #{normalizedIssueNum}";

                var escapedNum = Regex.Escape(normalizedIssueNum);
                var alreadyReferencedPattern = hasActiveSibling
                    ? $@"(?:^|\b)(?:Resolves|Closes|Fixes|Refs)\s+#{escapedNum}(?!\d)"
                    : $@"(?:^|\b)(?:Resolves|Closes|Fixes)\s+#{escapedNum}(?!\d)";

                if (task.Description == null ||
                    !Regex.IsMatch(task.Description, alreadyReferencedPattern, RegexOptions.IgnoreCase))
                {
                    sb.AppendLine(keywordLine);
                    sb.AppendLine();
                }
            }
        }

        string? diffStat = null;
        try
        {
            diffStat = await _gitWorkspaceManager.GenerateDiffStatAsync(workspace, task.Id, baseBranch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not generate diff stat for PR body of task #{TaskId}", task.Id);
        }

        if (!string.IsNullOrWhiteSpace(diffStat))
        {
            sb.AppendLine("### Diff Summary");
            sb.AppendLine("```text");
            sb.AppendLine(diffStat.Trim());
            sb.AppendLine("```");
            sb.AppendLine();
        }

        var metrics = QualityGateMetricsExtractor.Extract(task);
        sb.AppendLine("### Quality Gate Summary");
        sb.AppendLine("| Metric | Result |");
        sb.AppendLine("| :--- | :--- |");
        sb.AppendLine($"| **Tests** | {metrics.PassedTests} / {metrics.TotalTests} passed ({metrics.FailedTests} failed) |");
        sb.AppendLine($"| **Line Coverage** | {FormatDouble(metrics.CurrentCoverage)}% (Baseline: {FormatDouble(metrics.BaselineCoverage)}% -> Current: {FormatDouble(metrics.CurrentCoverage)}%, Δ {FormatSignedDouble(metrics.CoverageDelta)}%) |");
        sb.AppendLine($"| **Compiler Warnings** | {metrics.CurrentWarnings} (Δ {FormatSignedInt(metrics.WarningDelta)}) |");

        var login = await ResolveAuthenticatedLoginAsync(cancellationToken).ConfigureAwait(false);
        var attributionUser = string.IsNullOrWhiteSpace(login) ? string.Empty : $" by @{login.Trim()}";
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"*Generated via [NightCodeForge](https://github.com/jetelain/NightCodeForge){attributionUser} (Local Task #{task.Id})*");

        return sb.ToString().TrimEnd();
    }

    private async Task<string?> ResolveAuthenticatedLoginAsync(CancellationToken cancellationToken)
    {
        var tokenPayload = await _tokenStore.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(tokenPayload?.AuthenticatedUserLogin))
        {
            return tokenPayload.AuthenticatedUserLogin;
        }

        try
        {
            var user = await _authService.GetAuthenticatedUserAsync(cancellationToken).ConfigureAwait(false);
            return user?.Login;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Falling back to default branch login segment after failing to query authenticated GitHub user");
            return null;
        }
    }

    private static string ResolveCanonicalRepoPath(Workspace workspace) =>
        !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath) && WorkspaceDirectoryExists(workspace.Ext4MirrorPath, workspace)
            ? workspace.Ext4MirrorPath
            : workspace.RepositoryPath;

    private static bool WorkspaceDirectoryExists(string? path, Workspace workspace)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (Directory.Exists(path))
        {
            return true;
        }

        if ((workspace.ExecutionMode == ExecutionMode.ContainerSandbox || RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) &&
            (path.StartsWith('/') || path.StartsWith('~')))
        {
            return true;
        }

        return false;
    }

    private static string ResolveRemoteUrl(GitHubIntegrationSettings settings)
    {
        var repo = (settings.RepositoryName ?? string.Empty).Trim();
        if (Path.IsPathRooted(repo) || repo.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return repo;
        }

        var owner = (settings.RepositoryOwner ?? string.Empty).Trim();
        return repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? $"https://github.com/{owner}/{repo}"
            : $"https://github.com/{owner}/{repo}.git";
    }

    private static string[] BuildAuthArgs(string? accessToken, string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return [];
        }

        var encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{accessToken}"));
        var configKey = Uri.TryCreate(remoteUrl, UriKind.Absolute, out var remoteUri) &&
                        (string.Equals(remoteUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(remoteUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            ? $"http.{remoteUri.Scheme}://{remoteUri.Authority}/.extraHeader"
            : "http.extraHeader";

        return ["-c", $"{configKey}=Authorization: Basic {encodedCredentials}"];
    }

    private static string ExtractFirstRefSha(string? lsRemoteOutput)
    {
        if (string.IsNullOrWhiteSpace(lsRemoteOutput))
        {
            return string.Empty;
        }

        var firstToken = lsRemoteOutput
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return firstToken ?? string.Empty;
    }

    private static string FormatDouble(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatSignedDouble(double value) =>
        value >= 0
            ? $"+{value.ToString("0.##", CultureInfo.InvariantCulture)}"
            : value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatSignedInt(int value) =>
        value >= 0
            ? $"+{value.ToString(CultureInfo.InvariantCulture)}"
            : value.ToString(CultureInfo.InvariantCulture);
}
