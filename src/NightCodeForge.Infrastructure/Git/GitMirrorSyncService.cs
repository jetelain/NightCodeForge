using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Git;

/// <summary>
/// Manages Ext4 workspace repository mirroring to eliminate Windows/WSL2 9p filesystem latency traps.
/// Manages local remote push/fetch synchronization, updateInstead configuration, and dirty tree fallback branches.
/// </summary>
public class GitMirrorSyncService : IGitMirrorSyncService
{
    private readonly IGitCliRunner _gitCliRunner;
    private readonly IGitAlertNotifier? _alertNotifier;
    private readonly ILogger<GitMirrorSyncService> _logger;

    public GitMirrorSyncService(
        IGitCliRunner gitCliRunner,
        IGitAlertNotifier? alertNotifier = null,
        ILogger<GitMirrorSyncService>? logger = null)
    {
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        _alertNotifier = alertNotifier;
        _logger = logger ?? NullLogger<GitMirrorSyncService>.Instance;
    }

    /// <inheritdoc />
    public async Task ConfigureHostRepositoryAsync(string hostRepoPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostRepoPath);

        if (!Directory.Exists(hostRepoPath))
        {
            throw new DirectoryNotFoundException($"Host repository path '{hostRepoPath}' does not exist.");
        }

        _logger.LogInformation("Configuring receive.denyCurrentBranch updateInstead on host repository '{HostRepoPath}'", hostRepoPath);

        // Allows git push to update currently checked-out branch if working tree is clean
        await _gitCliRunner.ExecuteAsync(
            hostRepoPath,
            "config receive.denyCurrentBranch updateInstead",
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> InitializeMirrorAsync(
        Workspace workspace,
        string mirrorDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace.RepositoryPath);

        if (!Directory.Exists(workspace.RepositoryPath))
        {
            throw new DirectoryNotFoundException($"Host repository path '{workspace.RepositoryPath}' does not exist.");
        }

        // 1. Configure receive.denyCurrentBranch updateInstead on host
        await ConfigureHostRepositoryAsync(workspace.RepositoryPath, cancellationToken);

        // 2. Clone repository into mirrorDirectory if not already initialized
        var isAlreadyInitialized = false;
        if (Directory.Exists(mirrorDirectory))
        {
            try
            {
                var check = await _gitCliRunner.ExecuteAsync(mirrorDirectory, "rev-parse --is-inside-work-tree", cancellationToken: cancellationToken);
                isAlreadyInitialized = check.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                isAlreadyInitialized = false;
            }
        }

        if (!isAlreadyInitialized)
        {
            _logger.LogInformation("Cloning host repository '{HostRepo}' into ext4 mirror '{MirrorDir}'",
                workspace.RepositoryPath, mirrorDirectory);

            var parentDir = Path.GetDirectoryName(Path.GetFullPath(mirrorDirectory));
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            // Clone from host repository
            var cloneDir = Directory.Exists(parentDir) ? parentDir : workspace.RepositoryPath;
            await _gitCliRunner.ExecuteAsync(
                cloneDir,
                $"clone \"{workspace.RepositoryPath}\" \"{mirrorDirectory}\"",
                timeoutSeconds: 120,
                cancellationToken: cancellationToken);
        }

        // 3. Ensure local remote 'host-origin' exists
        var remoteResult = await _gitCliRunner.ExecuteAsync(mirrorDirectory, "remote", cancellationToken: cancellationToken);
        var remotes = remoteResult.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!remotes.Contains("host-origin", StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Adding local remote 'host-origin' pointing to '{HostRepo}'", workspace.RepositoryPath);
            await _gitCliRunner.ExecuteAsync(
                mirrorDirectory,
                $"remote add host-origin \"{workspace.RepositoryPath}\"",
                cancellationToken: cancellationToken);
        }
        else
        {
            // Ensure host-origin url is up to date
            await _gitCliRunner.ExecuteAsync(
                mirrorDirectory,
                $"remote set-url host-origin \"{workspace.RepositoryPath}\"",
                cancellationToken: cancellationToken);
        }

        workspace.Ext4MirrorPath = mirrorDirectory;
        return mirrorDirectory;
    }

    /// <inheritdoc />
    public async Task FetchFromHostAsync(
        string mirrorRepoPath,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorRepoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);

        _logger.LogInformation("Fetching base branch '{BaseBranch}' from host-origin in '{MirrorRepoPath}'",
            baseBranch, mirrorRepoPath);

        await _gitCliRunner.ExecuteAsync(
            mirrorRepoPath,
            $"fetch host-origin {baseBranch}",
            timeoutSeconds: 120,
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GitSyncResult> PushToHostAsync(
        string mirrorRepoPath,
        int issueId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorRepoPath);

        var branchName = $"feature/issue-{issueId}";
        _logger.LogInformation("Pushing branch '{BranchName}' from mirror to host-origin", branchName);

        try
        {
            // Primary sync push attempt to host-origin
            await _gitCliRunner.ExecuteAsync(
                mirrorRepoPath,
                $"push host-origin {branchName}",
                timeoutSeconds: 60,
                cancellationToken: cancellationToken);

            _logger.LogInformation("Successfully pushed '{BranchName}' to host-origin", branchName);
            return new GitSyncResult(
                Success: true,
                FallbackUsed: false,
                BranchPushed: branchName,
                WarningMessage: null);
        }
        catch (GitCommandException ex)
        {
            _logger.LogWarning(ex, "Push to host-origin for '{BranchName}' rejected. Initiating dirty host tree fallback.", branchName);

            var fallbackBranch = $"{branchName}-ncf";
            var warningMessage = $"⚠️ Host Working Tree Dirty: Changes pushed to '{fallbackBranch}'. Please commit or stash local edits in your host IDE to reconcile with the active agent branch.";

            try
            {
                // Fallback: push to dedicated -ncf ref on host
                await _gitCliRunner.ExecuteAsync(
                    mirrorRepoPath,
                    $"push host-origin {branchName}:refs/heads/{fallbackBranch} --force",
                    timeoutSeconds: 60,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Fallback push succeeded: '{BranchName}' pushed to 'refs/heads/{FallbackBranch}' on host",
                    branchName, fallbackBranch);

                if (_alertNotifier != null)
                {
                    await _alertNotifier.NotifyAlertAsync("Host Working Tree Dirty", warningMessage, cancellationToken);
                }

                return new GitSyncResult(
                    Success: true,
                    FallbackUsed: true,
                    BranchPushed: fallbackBranch,
                    WarningMessage: warningMessage);
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "Fallback push to '{FallbackBranch}' also failed.", fallbackBranch);
                throw new GitCommandException(
                    mirrorRepoPath,
                    $"push host-origin {branchName}:refs/heads/{fallbackBranch}",
                    ex.ExitCode,
                    ex.StandardOutput,
                    $"Primary push failed: {ex.StandardError}\nFallback push failed: {fallbackEx.Message}");
            }
        }
    }
}
