using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Git;

/// <summary>
/// Native Git CLI workspace manager handling branch isolation, WIP checkpoints, noise-filtered diffs, and squash merges.
/// </summary>
public class GitWorkspaceManager : IGitWorkspaceManager
{
    private readonly IGitCliRunner _gitCliRunner;
    private readonly IWorkspacePathResolver? _pathResolver;
    private readonly ILogger<GitWorkspaceManager> _logger;

    public GitWorkspaceManager(
        IGitCliRunner gitCliRunner,
        IWorkspacePathResolver? pathResolver = null,
        ILogger<GitWorkspaceManager>? logger = null)
    {
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        _pathResolver = pathResolver;
        _logger = logger ?? NullLogger<GitWorkspaceManager>.Instance;
    }

    /// <inheritdoc />
    public async Task EnsureRepositoryValidAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);

        if (!Directory.Exists(repoPath))
        {
            throw new DirectoryNotFoundException($"Directory '{repoPath}' does not exist.");
        }

        try
        {
            var result = await _gitCliRunner.ExecuteAsync(repoPath, ["rev-parse", "--is-inside-work-tree"], cancellationToken: cancellationToken);
            if (!result.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Directory '{repoPath}' is not inside a Git working tree.");
            }
        }
        catch (GitCommandException ex)
        {
            throw new InvalidOperationException($"Directory '{repoPath}' is not a valid Git repository.", ex);
        }
    }

    /// <inheritdoc />
    public async Task CheckoutIssueBranchAsync(Workspace workspace, int issueId, string baseBranch, CancellationToken cancellationToken = default)
    {
        await EnsureWorktreeAsync(workspace, issueId, baseBranch, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> EnsureWorktreeAsync(Workspace workspace, int issueId, string baseBranch, CancellationToken cancellationToken = default)
    {
        var canonicalPath = GetCanonicalWorkingPath(workspace);
        var worktreePath = GetWorktreePath(workspace, issueId);
        var branchName = $"feature/issue-{issueId}";

        _logger.LogInformation("Ensuring worktree for Issue #{IssueId} at '{WorktreePath}' (branch: '{BranchName}', base: '{BaseBranch}')",
            issueId, worktreePath, branchName, baseBranch);

        // Check if worktree is already registered
        try
        {
            var worktreeListResult = await _gitCliRunner.ExecuteAsync(canonicalPath, ["worktree", "list", "--porcelain"], cancellationToken: cancellationToken);
            var normWorktreePath = worktreePath.Replace('\\', '/').TrimEnd('/');
            var isWorktreeRegistered = worktreeListResult.StandardOutput
                .Split('\n')
                .Any(line => line.StartsWith("worktree ", StringComparison.OrdinalIgnoreCase) &&
                             line[9..].Trim().Replace('\\', '/').TrimEnd('/').Equals(normWorktreePath, StringComparison.OrdinalIgnoreCase));

            if (isWorktreeRegistered)
            {
                _logger.LogInformation("Worktree for Issue #{IssueId} is already registered at '{WorktreePath}'", issueId, worktreePath);
                return worktreePath;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list worktrees in '{CanonicalPath}'; proceeding with add", canonicalPath);
        }

        // If directory exists on disk but is not registered in git, clean it up before worktree add
        if (Directory.Exists(worktreePath))
        {
            _logger.LogWarning("Worktree directory '{WorktreePath}' exists on disk but is not registered in git; cleaning up", worktreePath);
            DeleteDirectorySafe(worktreePath);
        }

        // Prune stale worktrees
        try
        {
            await _gitCliRunner.ExecuteAsync(canonicalPath, ["worktree", "prune"], cancellationToken: cancellationToken);
        }
        catch { }

        await EnsureWorktreeExcludedAsync(canonicalPath, cancellationToken);

        // Check if branch already exists
        var branchListResult = await _gitCliRunner.ExecuteAsync(canonicalPath, ["branch", "--list", branchName], cancellationToken: cancellationToken);
        var branchExists = branchListResult.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(b => b.TrimStart('*', ' ').Equals(branchName, StringComparison.OrdinalIgnoreCase));

        // Create worktree
        if (branchExists)
        {
            await _gitCliRunner.ExecuteAsync(canonicalPath, ["worktree", "add", worktreePath, branchName], cancellationToken: cancellationToken);
        }
        else
        {
            await _gitCliRunner.ExecuteAsync(canonicalPath, ["worktree", "add", "-b", branchName, worktreePath, baseBranch], cancellationToken: cancellationToken);
        }

        // Ensure relativeworktrees extension is unset so Microsoft.Build.Tasks.Git in MSBuild does not fail
        try
        {
            await _gitCliRunner.ExecuteAsync(canonicalPath, ["config", "--unset", "extensions.relativeworktrees"], cancellationToken: cancellationToken);
        }
        catch { }

        // Make the .git pointer relative so it resolves seamlessly across host, WSL, and Docker containers
        try
        {
            var gitFile = Path.Combine(worktreePath, ".git");
            if (File.Exists(gitFile))
            {
                var normWorktree = worktreePath.Replace('\\', '/').TrimEnd('/');
                var normCanonical = canonicalPath.Replace('\\', '/').TrimEnd('/');
                var cloneDirName = Path.GetFileName(normCanonical);
                var wtParent = Path.GetDirectoryName(normWorktree);
                var canParent = Path.GetDirectoryName(normCanonical);

                string targetGitDir;
                if (!string.IsNullOrEmpty(wtParent) && string.Equals(wtParent, canParent, StringComparison.OrdinalIgnoreCase))
                {
                    // Sibling layout: ~/.nightcodeforge/wks/{id}/clone and ~/.nightcodeforge/wks/{id}/issue-{id}
                    targetGitDir = $"../{cloneDirName}/.git/worktrees/issue-{issueId}";
                }
                else
                {
                    // Child layout fallback: <canonical>/issue-{id}
                    targetGitDir = $"../.git/worktrees/issue-{issueId}";
                }

                File.SetAttributes(gitFile, FileAttributes.Normal);
                await File.WriteAllTextAsync(gitFile, $"gitdir: {targetGitDir}\n", cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not rewrite relative gitdir in '{WorktreePath}'", worktreePath);
        }

        _logger.LogInformation("Successfully created worktree for Issue #{IssueId} at '{WorktreePath}'", issueId, worktreePath);
        return worktreePath;
    }

    /// <inheritdoc />
    public async Task RemoveWorktreeAsync(Workspace workspace, int issueId, CancellationToken cancellationToken = default)
    {
        var canonicalPath = GetCanonicalWorkingPath(workspace);
        var worktreePath = GetWorktreePath(workspace, issueId);

        _logger.LogInformation("Removing worktree for Issue #{IssueId} at '{WorktreePath}'", issueId, worktreePath);

        try
        {
            await _gitCliRunner.ExecuteAsync(canonicalPath, ["worktree", "remove", worktreePath, "--force"], cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove worktree cleanly for Issue #{IssueId}; attempting prune", issueId);
        }

        try
        {
            await _gitCliRunner.ExecuteAsync(canonicalPath, ["worktree", "prune"], cancellationToken: cancellationToken);
        }
        catch (Exception pex)
        {
            _logger.LogWarning(pex, "Failed to prune worktrees for workspace #{WorkspaceId}", workspace.Id);
        }

        try
        {
            if (Directory.Exists(worktreePath))
            {
                DeleteDirectorySafe(worktreePath);
            }
        }
        catch { }
    }

    /// <inheritdoc />
    public string GetWorktreePath(Workspace workspace, int issueId)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            if (_pathResolver != null)
            {
                return _pathResolver.GetAgentWorktreePath(workspace.Id, issueId);
            }
            var mirrorTrimmed = workspace.Ext4MirrorPath.TrimEnd('/', '\\');
            var parentDir = Path.GetDirectoryName(mirrorTrimmed);
            return !string.IsNullOrEmpty(parentDir)
                ? Path.Combine(parentDir, $"issue-{issueId}").Replace('\\', '/')
                : $"{mirrorTrimmed}/issue-{issueId}";
        }

        var canonical = GetCanonicalWorkingPath(workspace);
        var canonicalTrimmed = canonical.TrimEnd('/', '\\');

        var parent = Path.GetDirectoryName(canonicalTrimmed);
        var dirName = Path.GetFileName(canonicalTrimmed);
        if (!string.IsNullOrEmpty(parent) &&
            (dirName.Equals("clone", StringComparison.OrdinalIgnoreCase) ||
             dirName.Equals("repo", StringComparison.OrdinalIgnoreCase) ||
             dirName.Equals("mirror", StringComparison.OrdinalIgnoreCase)))
        {
            var wt = Path.Combine(parent, $"issue-{issueId}");
            return canonical.Contains('/') && !canonical.Contains('\\')
                ? wt.Replace('\\', '/')
                : wt;
        }

        if (canonical.StartsWith('/') || canonical.StartsWith('~'))
        {
            return $"{canonicalTrimmed}/issue-{issueId}";
        }

        return Path.Combine(canonical, $"issue-{issueId}").Replace('\\', '/');
    }

    /// <inheritdoc />
    public async Task<string> CreateWipCheckpointAsync(
        Workspace workspace,
        int issueId,
        string stageName,
        string summary,
        CancellationToken cancellationToken = default)
    {
        var workingPath = GetWorkingPath(workspace, issueId);
        var safeSummary = SanitizeMessage(summary);
        var commitMessage = $"WIP: [Issue #{issueId}][Stage: {stageName}] {safeSummary}";

        _logger.LogInformation("Creating WIP checkpoint for Issue #{IssueId} [Stage: {StageName}] in '{WorkingPath}'",
            issueId, stageName, workingPath);

        // Stage all changes
        await _gitCliRunner.ExecuteAsync(workingPath, ["add", "-A"], cancellationToken: cancellationToken);

        // Check if there are changes to commit
        var status = await _gitCliRunner.ExecuteAsync(workingPath, ["status", "--porcelain"], cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            // Empty commit to guarantee checkpoint creation
            await _gitCliRunner.ExecuteAsync(
                workingPath,
                ["commit", "--allow-empty", "-m", commitMessage, "--no-verify"],
                cancellationToken: cancellationToken);
        }
        else
        {
            await _gitCliRunner.ExecuteAsync(
                workingPath,
                ["commit", "-m", commitMessage, "--no-verify"],
                cancellationToken: cancellationToken);
        }

        // Return exact 40-char SHA commit hash
        var revParseResult = await _gitCliRunner.ExecuteAsync(workingPath, ["rev-parse", "HEAD"], cancellationToken: cancellationToken);
        var sha = revParseResult.StandardOutput.Trim();

        _logger.LogInformation("WIP checkpoint created for Issue #{IssueId}: {Sha}", issueId, sha);
        return sha;
    }

    /// <inheritdoc />
    public async Task<string> GenerateDiffAsync(
        Workspace workspace,
        int issueId,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        var workingPath = GetWorkingPath(workspace, issueId);
        var branchName = $"feature/issue-{issueId}";

        _logger.LogInformation("Generating noise-filtered diff for Issue #{IssueId} against '{BaseBranch}' in '{WorkingPath}'",
            issueId, baseBranch, workingPath);

        // -U2 context window reduction and noisy artifact exclusions
        string[] diffArgs = [
            "diff",
            $"{baseBranch}...{branchName}",
            "-U2",
            "--",
            ".",
            ":(exclude)package-lock.json",
            ":(exclude)pnpm-lock.yaml",
            ":(exclude)yarn.lock",
            ":(exclude)*.Designer.cs",
            ":(exclude)*.generated.cs"
        ];

        var result = await _gitCliRunner.ExecuteAsync(workingPath, diffArgs, cancellationToken: cancellationToken);
        return result.StandardOutput;
    }

    /// <inheritdoc />
    public async Task SquashAndMergeAsync(
        Workspace workspace,
        int issueId,
        string baseBranch,
        string commitMessage,
        CancellationToken cancellationToken = default)
    {
        var canonicalPath = GetCanonicalWorkingPath(workspace);
        var branchName = $"feature/issue-{issueId}";
        var safeMessage = SanitizeMessage(commitMessage);

        _logger.LogInformation("Squashing and merging '{BranchName}' into '{BaseBranch}' in canonical repository '{CanonicalPath}'",
            branchName, baseBranch, canonicalPath);

        // 1. Checkout target base branch in canonical clone (ensuring clean state first)
        try { await _gitCliRunner.ExecuteAsync(canonicalPath, ["reset", "--hard", "HEAD"], cancellationToken: cancellationToken); } catch { }
        try { await _gitCliRunner.ExecuteAsync(canonicalPath, ["clean", "-fd"], cancellationToken: cancellationToken); } catch { }
        try
        {
            await _gitCliRunner.ExecuteAsync(canonicalPath, ["checkout", baseBranch], cancellationToken: cancellationToken);
        }
        catch (GitCommandException)
        {
            await _gitCliRunner.ExecuteAsync(canonicalPath, ["checkout", "-B", baseBranch], cancellationToken: cancellationToken);
        }

        // Ensure feature branch exists locally (fetch from host-origin or origin if working in mirror)
        var branchCheck = await _gitCliRunner.ExecuteAsync(canonicalPath, ["branch", "--list", branchName], cancellationToken: cancellationToken);
        var hasBranch = branchCheck.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(b => b.TrimStart('*', ' ').Equals(branchName, StringComparison.OrdinalIgnoreCase));

        if (!hasBranch)
        {
            try
            {
                await _gitCliRunner.ExecuteAsync(canonicalPath, ["fetch", "host-origin", $"{branchName}:{branchName}"], cancellationToken: cancellationToken);
            }
            catch
            {
                try
                {
                    await _gitCliRunner.ExecuteAsync(canonicalPath, ["fetch", "origin", $"{branchName}:{branchName}"], cancellationToken: cancellationToken);
                }
                catch { }
            }
        }

        // 2. Perform squash merge
        try { await _gitCliRunner.ExecuteAsync(canonicalPath, ["clean", "-fd"], cancellationToken: cancellationToken); } catch { }
        await _gitCliRunner.ExecuteAsync(canonicalPath, ["merge", "--squash", branchName], cancellationToken: cancellationToken);

        // 3. Commit squashed changeset
        await _gitCliRunner.ExecuteAsync(
            canonicalPath,
            ["commit", "-m", safeMessage],
            cancellationToken: cancellationToken);

        _logger.LogInformation("Squash merge completed for Issue #{IssueId} on '{BaseBranch}'", issueId, baseBranch);

        // 4. Remove the issue's ephemeral worktree
        await RemoveWorktreeAsync(workspace, issueId, cancellationToken);
    }

    /// <inheritdoc />
    public Task RevertOrCleanAsync(Workspace workspace, CancellationToken cancellationToken = default)
        => RevertOrCleanAsync(workspace, null, cancellationToken);

    /// <inheritdoc />
    public async Task RevertOrCleanAsync(Workspace workspace, int? issueId, CancellationToken cancellationToken = default)
    {
        var workingPath = GetWorkingPath(workspace, issueId);

        _logger.LogWarning("Reverting and cleaning repository working tree in '{WorkingPath}'", workingPath);

        await _gitCliRunner.ExecuteAsync(workingPath, ["reset", "--hard", "HEAD"], cancellationToken: cancellationToken);
        await _gitCliRunner.ExecuteAsync(workingPath, ["clean", "-fd"], cancellationToken: cancellationToken);
    }

    private string GetWorkingPath(Workspace workspace, int? issueId = null)
    {
        if (issueId.HasValue)
        {
            var wt = GetWorktreePath(workspace, issueId.Value);
            // If the worktree directory already exists or is in WSL/Linux, use it
            if (Directory.Exists(wt) || wt.StartsWith('/') || wt.StartsWith('~') || WslPathConverter.IsWslUncPath(wt))
            {
                return wt;
            }
        }

        return GetCanonicalWorkingPath(workspace);
    }

    private string GetCanonicalWorkingPath(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            if (Directory.Exists(workspace.Ext4MirrorPath))
            {
                return workspace.Ext4MirrorPath;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string checkPath;
                if (WslPathConverter.IsWslUncPath(workspace.Ext4MirrorPath))
                {
                    checkPath = workspace.Ext4MirrorPath;
                }
                else
                {
                    var distro = _pathResolver?.GetWslDistributionName() ?? Environment.GetEnvironmentVariable("NCF_WSL_DISTRIBUTION") ?? "Ubuntu";
                    checkPath = WslPathConverter.ToWslUncPath(workspace.Ext4MirrorPath, distro);
                }

                if (Directory.Exists(checkPath))
                {
                    return workspace.Ext4MirrorPath;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(workspace.RepositoryPath) && Directory.Exists(workspace.RepositoryPath))
        {
            return workspace.RepositoryPath;
        }

        throw new DirectoryNotFoundException(
            $"Neither workspace RepositoryPath '{workspace.RepositoryPath}' nor Ext4MirrorPath '{workspace.Ext4MirrorPath}' exists.");
    }

    private static string SanitizeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Automated commit";
        }

        return message
            .Replace("\"", "\\\"")
            .Replace("\r\n", " ")
            .Replace('\n', ' ')
            .Trim();
    }

    private async Task EnsureWorktreeExcludedAsync(string canonicalPath, CancellationToken cancellationToken)
    {
        try
        {
            // If canonicalPath is in WSL without UNC, resolve UNC path if possible
            string targetCanonical = canonicalPath;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && (canonicalPath.StartsWith('/') || canonicalPath.StartsWith('~')))
            {
                var distro = _pathResolver?.GetWslDistributionName() ?? Environment.GetEnvironmentVariable("NCF_WSL_DISTRIBUTION") ?? "Ubuntu";
                var user = _pathResolver?.GetWslUserName() ?? Environment.UserName.ToLowerInvariant();
                var expanded = canonicalPath.StartsWith('~') ? $"/home/{user}/{canonicalPath.TrimStart('~', '/')}" : canonicalPath;
                targetCanonical = WslPathConverter.ToWslUncPath(expanded, distro);
            }

            var result = await _gitCliRunner.ExecuteAsync(canonicalPath, ["rev-parse", "--git-path", "info/exclude"], cancellationToken: cancellationToken);
            var excludeRelOrAbs = result.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(excludeRelOrAbs))
            {
                return;
            }

            var excludePath = Path.IsPathRooted(excludeRelOrAbs)
                ? excludeRelOrAbs
                : Path.Combine(targetCanonical, excludeRelOrAbs);

            if (File.Exists(excludePath))
            {
                var content = await File.ReadAllTextAsync(excludePath, cancellationToken);
                var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!lines.Contains("issue-*/") && !lines.Contains("issue-*"))
                {
                    await File.AppendAllTextAsync(excludePath, "\nissue-*/\nwt/\n", cancellationToken);
                }
            }
            else if (Directory.Exists(targetCanonical))
            {
                var dir = Path.GetDirectoryName(excludePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                await File.WriteAllTextAsync(excludePath, "issue-*/\nwt/\n", cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update git info/exclude in '{CanonicalPath}'", canonicalPath);
        }
    }

    private static void DeleteDirectorySafe(string path)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch { }
            }

            Directory.Delete(path, true);
        }
        catch { }
    }
}
