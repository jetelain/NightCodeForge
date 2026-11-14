using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
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
    private const string StripMarkdownCommitMessage = "chore(ncf): strip local task markdown before PR delivery";

    private readonly IGitCliRunner _gitCliRunner;
    private readonly IGitWorkspaceManager _gitWorkspaceManager;
    private readonly IGitMirrorSyncService _mirrorSyncService;
    private readonly IGitHubDeviceAuthService _authService;
    private readonly IGitHubTokenStore _tokenStore;
    private readonly IGitHubClientService _gitHubClient;
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
        IOptions<SecurityOptions> securityOptions,
        ILogger<GitHubPullRequestDeliveryService> logger)
    {
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        _gitWorkspaceManager = gitWorkspaceManager ?? throw new ArgumentNullException(nameof(gitWorkspaceManager));
        _mirrorSyncService = mirrorSyncService ?? throw new ArgumentNullException(nameof(mirrorSyncService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _gitHubClient = gitHubClient ?? throw new ArgumentNullException(nameof(gitHubClient));
        _securityOptions = securityOptions ?? throw new ArgumentNullException(nameof(securityOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> IsPullRequestDeliveryAvailableAsync(Workspace? workspace, CancellationToken cancellationToken = default)
    {
        if (_securityOptions.Value.AirGappedMode)
        {
            return false;
        }

        if (workspace?.GitHubSettings is not { Enabled: true, PullRequestDeliveryEnabled: true })
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
        var authArgs = BuildAuthArgs(accessToken);

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

        // 1. Remove any NCF-managed docs/tasks files from index and working tree
        await _gitCliRunner.ExecuteAsync(
            workingDirectory,
            ["rm", "-rf", "--ignore-unmatch", "docs/tasks"],
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

        // 3. Commit only if the index/working tree for docs/tasks actually changed relative to HEAD
        var status = await _gitCliRunner.ExecuteAsync(
            workingDirectory,
            ["status", "--porcelain", "--", "docs/tasks"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            await _gitCliRunner.ExecuteAsync(
                workingDirectory,
                ["commit", "-m", StripMarkdownCommitMessage, "--no-verify"],
                cancellationToken: cancellationToken).ConfigureAwait(false);
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

        var authArgs = BuildAuthArgs(accessToken);
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

        if (hasWorktree)
        {
            await StripTaskMarkdownDeliverablesFromBranchAsync(worktreePath!, baseBranch, cancellationToken).ConfigureAwait(false);
        }
        else if (hasCanonical)
        {
            var headResult = await _gitCliRunner.ExecuteAsync(
                canonicalPath!,
                ["rev-parse", "--abbrev-ref", "HEAD"],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (string.Equals(headResult.StandardOutput.Trim(), localBranch, StringComparison.Ordinal))
            {
                await StripTaskMarkdownDeliverablesFromBranchAsync(canonicalPath!, baseBranch, cancellationToken).ConfigureAwait(false);
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
            _logger.LogInformation(
                "Creating GitHub Pull Request for task #{TaskId} ({Head} -> {Base}) in {Owner}/{Repo}",
                task.Id, remoteBranchName, baseBranch, owner, repo);

            pr = await _gitHubClient.CreatePullRequestAsync(
                owner,
                repo,
                new CreateGitHubPullRequestRequest
                {
                    Title = title,
                    Head = remoteBranchName,
                    Base = baseBranch,
                    Body = body,
                    Draft = settings.CreateAsDraft
                },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation(
                "Updating existing GitHub Pull Request #{PrNumber} for task #{TaskId} ({Head} -> {Base}) in {Owner}/{Repo}",
                task.PullRequestNumber.Value, task.Id, remoteBranchName, baseBranch, owner, repo);

            pr = await _gitHubClient.UpdatePullRequestAsync(
                owner,
                repo,
                task.PullRequestNumber.Value,
                new UpdateGitHubPullRequestRequest
                {
                    Title = title,
                    Body = body,
                    Base = baseBranch
                },
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

    public static string BuildPullRequestTitle(ForgeTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var baseTitle = string.IsNullOrWhiteSpace(task.Title) ? $"Task #{task.Id}" : task.Title.Trim();

        if (string.Equals(task.ExternalSource, "GitHub", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(task.ExternalReferenceId))
        {
            var normalizedIssueNum = task.ExternalReferenceId.Trim().TrimStart('#');
            if (!string.IsNullOrWhiteSpace(normalizedIssueNum) &&
                !baseTitle.Contains($"#{normalizedIssueNum}", StringComparison.OrdinalIgnoreCase))
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

        if (string.Equals(task.ExternalSource, "GitHub", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(task.ExternalReferenceId))
        {
            var normalizedIssueNum = task.ExternalReferenceId.Trim().TrimStart('#');
            var resolvesToken = $"Resolves #{normalizedIssueNum}";
            if (!string.IsNullOrWhiteSpace(normalizedIssueNum) &&
                (task.Description == null ||
                 (!task.Description.Contains(resolvesToken, StringComparison.OrdinalIgnoreCase) &&
                  !task.Description.Contains($"Closes #{normalizedIssueNum}", StringComparison.OrdinalIgnoreCase) &&
                  !task.Description.Contains($"Fixes #{normalizedIssueNum}", StringComparison.OrdinalIgnoreCase))))
            {
                sb.AppendLine(resolvesToken);
                sb.AppendLine();
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
        return $"https://github.com/{owner}/{repo}.git";
    }

    private static string[] BuildAuthArgs(string? accessToken) =>
        !string.IsNullOrWhiteSpace(accessToken)
            ? ["-c", $"http.extraHeader=Authorization: Bearer {accessToken}"]
            : [];

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
