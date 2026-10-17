using System.Runtime.InteropServices;
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
    private readonly IWorkspacePathResolver? _pathResolver;
    private readonly IWslMetadataService _wslMetadataService;
    private readonly ILogger<GitMirrorSyncService> _logger;

    public GitMirrorSyncService(
        IGitCliRunner gitCliRunner,
        IGitAlertNotifier? alertNotifier = null,
        IWorkspacePathResolver? pathResolver = null,
        IWslMetadataService? wslMetadataService = null,
        ILogger<GitMirrorSyncService>? logger = null)
    {
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        _alertNotifier = alertNotifier;
        _pathResolver = pathResolver;
        _wslMetadataService = wslMetadataService ?? new Execution.WslMetadataService(new NightCodeForge.Core.Execution.SystemProcessRunner());
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
            ["config", "receive.denyCurrentBranch", "updateInstead"],
            cancellationToken: cancellationToken);

        // Local Git Config: set core.autocrlf false to prevent CRLF conversions on host
        try
        {
            await _gitCliRunner.ExecuteAsync(
                hostRepoPath,
                ["config", "--local", "core.autocrlf", "false"],
                cancellationToken: cancellationToken);
            _logger.LogInformation("Configured git config --local core.autocrlf false on '{HostRepoPath}'", hostRepoPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to configure core.autocrlf false in '{HostRepoPath}'", hostRepoPath);
        }

        // Ensure task deliverables and transcripts are ignored by Git
        await EnsureGitIgnoreEntriesAsync(hostRepoPath, cancellationToken);

        // Ensure .gitattributes line-ending normalization (* text=auto eol=lf)
        await EnsureGitAttributesAsync(hostRepoPath, cancellationToken);

        // Ensure .editorconfig cross-platform LF line endings
        await EnsureEditorConfigAsync(hostRepoPath, cancellationToken);
    }

    private async Task EnsureGitAttributesAsync(string repoPath, CancellationToken cancellationToken)
    {
        try
        {
            var gitattributesPath = Path.Combine(repoPath, ".gitattributes");
            if (File.Exists(gitattributesPath))
            {
                var content = await File.ReadAllTextAsync(gitattributesPath, cancellationToken);
                if (!content.Contains("eol=lf", StringComparison.OrdinalIgnoreCase))
                {
                    var textToAppend = "\n# NightCodeForge line ending normalization for cross-platform Windows / WSL2 compatibility\n* text=auto eol=lf\n";
                    await File.AppendAllTextAsync(gitattributesPath, textToAppend, cancellationToken);
                    _logger.LogInformation("Appended LF normalization entries to '{GitAttributesPath}'", gitattributesPath);
                    try
                    {
                        await _gitCliRunner.ExecuteAsync(repoPath, ["add", ".gitattributes"], cancellationToken: cancellationToken);
                        await _gitCliRunner.ExecuteAsync(repoPath, ["commit", "-m", "chore: configure .gitattributes for NightCodeForge"], cancellationToken: cancellationToken);
                    }
                    catch (Exception commitEx)
                    {
                        _logger.LogDebug(commitEx, "Could not commit .gitattributes updates to '{RepoPath}'", repoPath);
                    }
                }
            }
            else
            {
                var initialContent = "# NightCodeForge line ending normalization for cross-platform Windows / WSL2 compatibility\n* text=auto eol=lf\n";
                await File.WriteAllTextAsync(gitattributesPath, initialContent, cancellationToken);
                _logger.LogInformation("Created '{GitAttributesPath}' with LF normalization", gitattributesPath);
                try
                {
                    await _gitCliRunner.ExecuteAsync(repoPath, ["add", ".gitattributes"], cancellationToken: cancellationToken);
                    await _gitCliRunner.ExecuteAsync(repoPath, ["commit", "-m", "chore: configure .gitattributes for NightCodeForge"], cancellationToken: cancellationToken);
                }
                catch (Exception commitEx)
                {
                    _logger.LogDebug(commitEx, "Could not commit .gitattributes creation to '{RepoPath}'", repoPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure .gitattributes entries in '{RepoPath}'", repoPath);
        }
    }

    private async Task EnsureEditorConfigAsync(string repoPath, CancellationToken cancellationToken)
    {
        try
        {
            var editorConfigPath = Path.Combine(repoPath, ".editorconfig");
            if (File.Exists(editorConfigPath))
            {
                var content = await File.ReadAllTextAsync(editorConfigPath, cancellationToken);
                if (!content.Contains("end_of_line = lf", StringComparison.OrdinalIgnoreCase) &&
                    !content.Contains("end_of_line=lf", StringComparison.OrdinalIgnoreCase))
                {
                    var textToAppend = "\n[*]\nend_of_line = lf\n";
                    await File.AppendAllTextAsync(editorConfigPath, textToAppend, cancellationToken);
                    _logger.LogInformation("Appended end_of_line = lf block to '{EditorConfigPath}'", editorConfigPath);
                    try
                    {
                        await _gitCliRunner.ExecuteAsync(repoPath, ["add", ".editorconfig"], cancellationToken: cancellationToken);
                        await _gitCliRunner.ExecuteAsync(repoPath, ["commit", "-m", "chore: configure .editorconfig for NightCodeForge"], cancellationToken: cancellationToken);
                    }
                    catch (Exception commitEx)
                    {
                        _logger.LogDebug(commitEx, "Could not commit .editorconfig updates to '{RepoPath}'", repoPath);
                    }
                }
            }
            else
            {
                var initialContent =
                    "# EditorConfig for cross-platform consistency\n" +
                    "root = true\n\n" +
                    "[*]\n" +
                    "indent_style = space\n" +
                    "indent_size = 4\n" +
                    "end_of_line = lf\n" +
                    "charset = utf-8\n" +
                    "trim_trailing_whitespace = true\n" +
                    "insert_final_newline = true\n\n" +
                    "[*.{json,yml,yaml,md}]\n" +
                    "indent_size = 2\n\n" +
                    "[*.{bat,cmd}]\n" +
                    "end_of_line = crlf\n";
                await File.WriteAllTextAsync(editorConfigPath, initialContent, cancellationToken);
                _logger.LogInformation("Created '{EditorConfigPath}' with cross-platform configuration", editorConfigPath);
                try
                {
                    await _gitCliRunner.ExecuteAsync(repoPath, ["add", ".editorconfig"], cancellationToken: cancellationToken);
                    await _gitCliRunner.ExecuteAsync(repoPath, ["commit", "-m", "chore: configure .editorconfig for NightCodeForge"], cancellationToken: cancellationToken);
                }
                catch (Exception commitEx)
                {
                    _logger.LogDebug(commitEx, "Could not commit .editorconfig creation to '{RepoPath}'", repoPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure .editorconfig entries in '{RepoPath}'", repoPath);
        }
    }

    private async Task EnsureGitIgnoreEntriesAsync(string repoPath, CancellationToken cancellationToken)
    {
        try
        {
            var gitignorePath = Path.Combine(repoPath, ".gitignore");
            var requiredEntries = new[] { "docs/tasks/", "docs/transcripts/" };

            if (File.Exists(gitignorePath))
            {
                var content = await File.ReadAllTextAsync(gitignorePath, cancellationToken);
                var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var missing = requiredEntries.Where(e => !lines.Contains(e) && !lines.Contains(e.TrimEnd('/'))).ToList();

                if (missing.Count > 0)
                {
                    var textToAppend = "\n# NightCodeForge task artifacts and transcripts\n" + string.Join("\n", missing) + "\n";
                    await File.AppendAllTextAsync(gitignorePath, textToAppend, cancellationToken);
                    _logger.LogInformation("Appended {Count} NightCodeForge entries to '{GitIgnorePath}'", missing.Count, gitignorePath);
                    try
                    {
                        await _gitCliRunner.ExecuteAsync(repoPath, ["add", ".gitignore"], cancellationToken: cancellationToken);
                        await _gitCliRunner.ExecuteAsync(repoPath, ["commit", "-m", "chore: configure .gitignore for NightCodeForge"], cancellationToken: cancellationToken);
                    }
                    catch (Exception commitEx)
                    {
                        _logger.LogDebug(commitEx, "Could not commit .gitignore updates to '{RepoPath}'", repoPath);
                    }
                }
            }
            else
            {
                var initialContent = "# NightCodeForge task artifacts and transcripts\ndocs/tasks/\ndocs/transcripts/\n";
                await File.WriteAllTextAsync(gitignorePath, initialContent, cancellationToken);
                _logger.LogInformation("Created '{GitIgnorePath}' with NightCodeForge entries", gitignorePath);
                try
                {
                    await _gitCliRunner.ExecuteAsync(repoPath, ["add", ".gitignore"], cancellationToken: cancellationToken);
                    await _gitCliRunner.ExecuteAsync(repoPath, ["commit", "-m", "chore: create .gitignore for NightCodeForge"], cancellationToken: cancellationToken);
                }
                catch (Exception commitEx)
                {
                    _logger.LogDebug(commitEx, "Could not commit .gitignore creation to '{RepoPath}'", repoPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure .gitignore entries in '{RepoPath}'", repoPath);
        }
    }

    /// <inheritdoc />
    public async Task<string> InitializeMirrorAsync(
        Workspace workspace,
        string mirrorDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace.RepositoryPath);

        var targetMirrorDir = !string.IsNullOrWhiteSpace(mirrorDirectory)
            ? mirrorDirectory
            : (_pathResolver?.GetAgentWorkingClonePath(workspace.Id) ?? mirrorDirectory);

        ArgumentException.ThrowIfNullOrWhiteSpace(targetMirrorDir);

        if (!Directory.Exists(workspace.RepositoryPath))
        {
            throw new DirectoryNotFoundException($"Host repository path '{workspace.RepositoryPath}' does not exist.");
        }

        // 1. Configure receive.denyCurrentBranch updateInstead on host
        await ConfigureHostRepositoryAsync(workspace.RepositoryPath, cancellationToken);

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var isTargetWsl = isWindows && (targetMirrorDir.StartsWith('/') || targetMirrorDir.StartsWith('~'));
        var remoteUrl = isTargetWsl && _pathResolver != null
            ? _pathResolver.GetHostRemoteUrl(workspace.RepositoryPath)
            : workspace.RepositoryPath;

        // 2. Clone repository into targetMirrorDir if not already initialized
        var isAlreadyInitialized = false;
        var checkPath = targetMirrorDir;
        if (isTargetWsl)
        {
            var distro = _wslMetadataService.GetDefaultDistributionName();
            var user = _wslMetadataService.GetDefaultUserName();
            checkPath = WslPathConverter.ToWslUncPath(targetMirrorDir, distro, user);
        }

        if (Directory.Exists(checkPath))
        {
            try
            {
                var check = await _gitCliRunner.ExecuteAsync(targetMirrorDir, ["rev-parse", "--is-inside-work-tree"], cancellationToken: cancellationToken);
                if (check.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    var remoteCheck = await _gitCliRunner.ExecuteAsync(targetMirrorDir, ["remote", "get-url", "host-origin"], cancellationToken: cancellationToken);
                    var currentRemote = remoteCheck.StandardOutput.Trim().Replace('\\', '/');
                    var expectedRemote = remoteUrl.Replace('\\', '/');
                    if (string.Equals(currentRemote, expectedRemote, StringComparison.OrdinalIgnoreCase))
                    {
                        isAlreadyInitialized = true;
                    }
                }
            }
            catch (Exception)
            {
                isAlreadyInitialized = false;
            }

            if (!isAlreadyInitialized)
            {
                try
                {
                    Directory.Delete(checkPath, true);
                }
                catch { }
            }
        }

        if (!isAlreadyInitialized)
        {
            _logger.LogInformation("Cloning host repository '{HostRepo}' into ext4 mirror '{MirrorDir}'",
                workspace.RepositoryPath, targetMirrorDir);

            if (isWindows && (targetMirrorDir.StartsWith('/') || targetMirrorDir.StartsWith('~')))
            {
                var distro = _wslMetadataService.GetDefaultDistributionName();
                var user = _wslMetadataService.GetDefaultUserName();
                var uncMirror = WslPathConverter.ToWslUncPath(targetMirrorDir, distro, user);
                var parentUnc = Path.GetDirectoryName(uncMirror);
                if (!string.IsNullOrEmpty(parentUnc) && !Directory.Exists(parentUnc))
                {
                    Directory.CreateDirectory(parentUnc);
                }

                await _gitCliRunner.ExecuteAsync(
                    "/tmp",
                    ["clone", remoteUrl, targetMirrorDir],
                    timeoutSeconds: 120,
                    cancellationToken: cancellationToken);
            }
            else
            {
                var parentDir = Path.GetDirectoryName(Path.GetFullPath(targetMirrorDir));
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                var cloneDir = Directory.Exists(parentDir) ? parentDir : workspace.RepositoryPath;
                await _gitCliRunner.ExecuteAsync(
                    cloneDir,
                    ["clone", remoteUrl, targetMirrorDir],
                    timeoutSeconds: 120,
                    cancellationToken: cancellationToken);
            }
        }

        // 3. Ensure local remote 'host-origin' exists
        var remoteResult = await _gitCliRunner.ExecuteAsync(targetMirrorDir, ["remote"], cancellationToken: cancellationToken);
        var remotes = remoteResult.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!remotes.Contains("host-origin", StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Adding local remote 'host-origin' pointing to '{RemoteUrl}'", remoteUrl);
            await _gitCliRunner.ExecuteAsync(
                targetMirrorDir,
                ["remote", "add", "host-origin", remoteUrl],
                cancellationToken: cancellationToken);
        }
        else
        {
            // Ensure host-origin url is up to date
            await _gitCliRunner.ExecuteAsync(
                targetMirrorDir,
                ["remote", "set-url", "host-origin", remoteUrl],
                cancellationToken: cancellationToken);
        }

        // Configure default remote for checkouts to disambiguate branches when multiple remotes exist
        await _gitCliRunner.ExecuteAsync(
            targetMirrorDir,
            ["config", "checkout.defaultRemote", "host-origin"],
            cancellationToken: cancellationToken);

        workspace.Ext4MirrorPath = targetMirrorDir;
        return targetMirrorDir;
    }

    /// <inheritdoc />
    public async Task PullFromHostAsync(
        string mirrorRepoPath,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorRepoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);

        _logger.LogInformation("Pulling latest base branch '{BaseBranch}' from host-origin into mirror '{MirrorRepoPath}'",
            baseBranch, mirrorRepoPath);

        // 1. Fetch latest commits from host-origin
        await _gitCliRunner.ExecuteAsync(
            mirrorRepoPath,
            ["fetch", "host-origin", baseBranch],
            timeoutSeconds: 120,
            cancellationToken: cancellationToken);

        // 2. Fast-forward local base branch to host-origin so working tree stays 100% current for ideation and triage
        try
        {
            await _gitCliRunner.ExecuteAsync(
                mirrorRepoPath,
                ["checkout", baseBranch],
                cancellationToken: cancellationToken);

            var mergeResult = await _gitCliRunner.ExecuteAsync(
                mirrorRepoPath,
                ["merge", "--ff-only", $"host-origin/{baseBranch}"],
                cancellationToken: cancellationToken);

            if (mergeResult.ExitCode != 0)
            {
                await _gitCliRunner.ExecuteAsync(
                    mirrorRepoPath,
                    ["reset", "--hard", $"host-origin/{baseBranch}"],
                    cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fast-forward merge failed in mirror '{MirrorRepoPath}'. Attempting hard reset to host-origin/{BaseBranch}",
                mirrorRepoPath, baseBranch);

            try
            {
                await _gitCliRunner.ExecuteAsync(
                    mirrorRepoPath,
                    ["reset", "--hard", $"host-origin/{baseBranch}"],
                    cancellationToken: cancellationToken);
            }
            catch (Exception rex)
            {
                _logger.LogWarning(rex, "Failed to hard-reset mirror '{MirrorRepoPath}' to host-origin/{BaseBranch}",
                    mirrorRepoPath, baseBranch);
            }
        }
    }

    /// <inheritdoc />
    public async Task<GitSyncResult> PushToHostAsync(
        string mirrorRepoPath,
        int taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorRepoPath);

        var branchName = $"feature/task-{taskId}";
        _logger.LogInformation("Pushing branch '{BranchName}' from mirror to host-origin", branchName);

        try
        {
            // Primary sync push attempt to host-origin
            await _gitCliRunner.ExecuteAsync(
                mirrorRepoPath,
                ["push", "host-origin", branchName],
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
                    ["push", "host-origin", $"{branchName}:refs/heads/{fallbackBranch}", "--force"],
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
                    ["push", "host-origin", $"{branchName}:refs/heads/{fallbackBranch}"],
                    ex.ExitCode,
                    ex.StandardOutput,
                    $"Primary push failed: {ex.StandardError}\nFallback push failed: {fallbackEx.Message}");
            }
        }
    }

    /// <inheritdoc />
    public async Task<GitSyncResult> PushBaseBranchToHostAsync(
        string mirrorRepoPath,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorRepoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);

        _logger.LogInformation("Pushing merged base branch '{BaseBranch}' from mirror to host-origin", baseBranch);

        try
        {
            await _gitCliRunner.ExecuteAsync(
                mirrorRepoPath,
                ["push", "host-origin", baseBranch],
                timeoutSeconds: 60,
                cancellationToken: cancellationToken);

            _logger.LogInformation("Successfully pushed merged '{BaseBranch}' to host-origin", baseBranch);
            return new GitSyncResult(
                Success: true,
                FallbackUsed: false,
                BranchPushed: baseBranch,
                WarningMessage: null);
        }
        catch (GitCommandException ex)
        {
            _logger.LogWarning(ex, "Push of base branch '{BaseBranch}' to host-origin rejected. Initiating dirty host tree fallback.", baseBranch);

            var fallbackBranch = $"{baseBranch}-ncf";
            var warningMessage = $"⚠️ Host Working Tree Dirty on {baseBranch}: Merged changes pushed to '{fallbackBranch}'. Please commit or stash local edits in your host IDE to reconcile with the active branch.";

            try
            {
                await _gitCliRunner.ExecuteAsync(
                    mirrorRepoPath,
                    ["push", "host-origin", $"{baseBranch}:refs/heads/{fallbackBranch}", "--force"],
                    timeoutSeconds: 60,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Fallback push succeeded: '{BaseBranch}' pushed to 'refs/heads/{FallbackBranch}' on host",
                    baseBranch, fallbackBranch);

                if (_alertNotifier != null)
                {
                    await _alertNotifier.NotifyAlertAsync("Host Working Tree Dirty on Merge", warningMessage, cancellationToken);
                }

                return new GitSyncResult(
                    Success: true,
                    FallbackUsed: true,
                    BranchPushed: fallbackBranch,
                    WarningMessage: warningMessage);
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "Fallback push of '{BaseBranch}' to '{FallbackBranch}' also failed.", baseBranch, fallbackBranch);
                throw new GitCommandException(
                    mirrorRepoPath,
                    ["push", "host-origin", $"{baseBranch}:refs/heads/{fallbackBranch}"],
                    ex.ExitCode,
                    ex.StandardOutput,
                    $"Primary push failed: {ex.StandardError}\nFallback push failed: {fallbackEx.Message}");
            }
        }
    }
}
