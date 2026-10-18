using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Git;

/// <summary>
/// Native Git CLI workspace manager handling branch isolation, WIP checkpoints, noise-filtered diffs, and squash merges.
/// </summary>
public class GitWorkspaceManager : IGitWorkspaceManager
{
    private readonly IGitCliRunner _gitCliRunner;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IWslMetadataService _wslMetadataService;
    private readonly ILogger<GitWorkspaceManager> _logger;

    public GitWorkspaceManager(
        IGitCliRunner gitCliRunner,
        IWorkspacePathResolver pathResolver,
        IWslMetadataService wslMetadataService,
        ILogger<GitWorkspaceManager> logger)
    {
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _wslMetadataService = wslMetadataService ?? throw new ArgumentNullException(nameof(wslMetadataService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
    public async Task CheckoutTaskBranchAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default)
    {
        await EnsureWorktreeAsync(workspace, taskId, baseBranch, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> EnsureWorktreeAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default)
    {
        var canonicalPath = GetCanonicalWorkingPath(workspace);
        var worktreePath = GetWorktreePath(workspace, taskId);
        var branchName = $"feature/task-{taskId}";

        _logger.LogInformation("Ensuring worktree for ForgeTask #{TaskId} at '{WorktreePath}' (branch: '{BranchName}', base: '{BaseBranch}')",
            taskId, worktreePath, branchName, baseBranch);

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
                _logger.LogInformation("Worktree for ForgeTask #{TaskId} is already registered at '{WorktreePath}'", taskId, worktreePath);
                return worktreePath;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list worktrees in '{CanonicalPath}'; proceeding with add", canonicalPath);
        }

        // If directory exists on disk but is not registered in git, clean it up before worktree add
        if (WorktreeDirectoryExists(worktreePath))
        {
            _logger.LogWarning("Worktree directory '{WorktreePath}' exists on disk but is not registered in git; cleaning up", worktreePath);
            DeleteDirectorySafe(worktreePath);
        }

        // Prune stale worktrees
        try
        {
            await _gitCliRunner.ExecuteAsync(canonicalPath, ["worktree", "prune"], cancellationToken: cancellationToken);
        }
        catch (GitCommandException ex)
        {
            _logger.LogDebug(ex, "Best-effort worktree prune in '{CanonicalPath}' failed", canonicalPath);
        }

        await EnsureWorktreeExcludedAsync(canonicalPath, cancellationToken);

        // Check if branch already exists
        var branchListResult = await _gitCliRunner.ExecuteAsync(canonicalPath, ["branch", "--list", branchName], cancellationToken: cancellationToken);
        var branchExists = branchListResult.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(b => b.TrimStart('*', '+', ' ', '\t').Equals(branchName, StringComparison.OrdinalIgnoreCase));

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
        catch (GitCommandException ex)
        {
            // Expected when extensions.relativeworktrees was not configured
            _logger.LogDebug(ex, "Unsetting extensions.relativeworktrees in '{CanonicalPath}' ignored (not configured)", canonicalPath);
        }

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
                    // Sibling layout: ~/.nightcodeforge/wks/{id}/clone and ~/.nightcodeforge/wks/{id}/task-{id}
                    targetGitDir = $"../{cloneDirName}/.git/worktrees/task-{taskId}";
                }
                else
                {
                    // Child layout fallback: <canonical>/task-{id}
                    targetGitDir = $"../.git/worktrees/task-{taskId}";
                }

                File.SetAttributes(gitFile, FileAttributes.Normal);
                await File.WriteAllTextAsync(gitFile, $"gitdir: {targetGitDir}\n", cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not rewrite relative gitdir in '{WorktreePath}'", worktreePath);
        }

        _logger.LogInformation("Successfully created worktree for ForgeTask #{TaskId} at '{WorktreePath}'", taskId, worktreePath);
        return worktreePath;
    }

    /// <inheritdoc />
    public async Task RemoveWorktreeAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default)
    {
        var canonicalPath = GetCanonicalWorkingPath(workspace);
        var worktreePath = GetWorktreePath(workspace, taskId);

        _logger.LogInformation("Removing worktree for ForgeTask #{TaskId} at '{WorktreePath}'", taskId, worktreePath);

        try
        {
            await _gitCliRunner.ExecuteAsync(canonicalPath, ["worktree", "remove", worktreePath, "--force"], cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove worktree cleanly for ForgeTask #{TaskId}; attempting prune", taskId);
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
            if (WorktreeDirectoryExists(worktreePath))
            {
                DeleteDirectorySafe(worktreePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete worktree directory '{WorktreePath}'", worktreePath);
        }
    }

    /// <inheritdoc />
    public string GetWorktreePath(Workspace workspace, int taskId)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            return _pathResolver.GetAgentWorktreePath(workspace.Id, taskId);
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
            var wt = Path.Combine(parent, $"task-{taskId}");
            return canonical.Contains('/') && !canonical.Contains('\\')
                ? wt.Replace('\\', '/')
                : wt;
        }

        if (canonical.StartsWith('/') || canonical.StartsWith('~'))
        {
            return $"{canonicalTrimmed}/task-{taskId}";
        }

        return Path.Combine(canonical, $"task-{taskId}").Replace('\\', '/');
    }

    /// <inheritdoc />
    public async Task<string> CreateWipCheckpointAsync(
        Workspace workspace,
        int taskId,
        string stageName,
        string summary,
        CancellationToken cancellationToken = default)
    {
        var workingPath = GetWorkingPath(workspace, taskId);
        var safeSummary = SanitizeMessage(summary);
        var commitMessage = $"WIP: [ForgeTask #{taskId}][Stage: {stageName}] {safeSummary}";

        _logger.LogInformation("Creating WIP checkpoint for ForgeTask #{TaskId} [Stage: {StageName}] in '{WorkingPath}'",
            taskId, stageName, workingPath);

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

        _logger.LogInformation("WIP checkpoint created for ForgeTask #{TaskId}: {Sha}", taskId, sha);
        return sha;
    }

    /// <inheritdoc />
    public async Task<string> GenerateDiffAsync(
        Workspace workspace,
        int taskId,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        var workingPath = GetWorkingPath(workspace, taskId);
        var branchName = $"feature/task-{taskId}";

        _logger.LogInformation("Generating noise-filtered diff for ForgeTask #{TaskId} against '{BaseBranch}' in '{WorkingPath}'",
            taskId, baseBranch, workingPath);

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

        string rawDiff = string.Empty;
        try
        {
            var result = await _gitCliRunner.ExecuteAsync(workingPath, diffArgs, cancellationToken: cancellationToken);
            rawDiff = result.StandardOutput;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate branch diff for ForgeTask #{TaskId} in '{WorkingPath}'", taskId, workingPath);
        }

        if (string.IsNullOrWhiteSpace(rawDiff) &&
            !string.IsNullOrWhiteSpace(workspace.RepositoryPath) &&
            !string.Equals(workspace.RepositoryPath, workingPath, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(workspace.RepositoryPath))
        {
            try
            {
                var fallbackResult = await _gitCliRunner.ExecuteAsync(workspace.RepositoryPath, diffArgs, cancellationToken: cancellationToken);
                rawDiff = fallbackResult.StandardOutput;
            }
            catch (GitCommandException ex)
            {
                _logger.LogDebug(ex, "Fallback diff generation against host repository '{RepoPath}' failed", workspace.RepositoryPath);
            }
        }

        return rawDiff;
    }

    /// <inheritdoc />
    public async Task<string> GenerateLiveDiffAsync(
        Workspace workspace,
        int taskId,
        string baseBranch = "main",
        CancellationToken cancellationToken = default)
    {
        var workingPath = GetWorkingPath(workspace, taskId);
        if (!WorktreeDirectoryExists(workingPath) && !Directory.Exists(workingPath))
        {
            return await GenerateDiffAsync(workspace, taskId, baseBranch, cancellationToken);
        }

        _logger.LogInformation("Generating live diff for ForgeTask #{TaskId} against '{BaseBranch}' in '{WorkingPath}'",
            taskId, baseBranch, workingPath);

        string[] diffArgs = [
            "diff",
            baseBranch,
            "-U2",
            "--",
            ".",
            ":(exclude)package-lock.json",
            ":(exclude)pnpm-lock.yaml",
            ":(exclude)yarn.lock",
            ":(exclude)*.Designer.cs",
            ":(exclude)*.generated.cs"
        ];

        try
        {
            var result = await _gitCliRunner.ExecuteAsync(workingPath, diffArgs, cancellationToken: cancellationToken);
            return result.StandardOutput;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to run live git diff in '{WorkingPath}', falling back to branch diff", workingPath);
            return await GenerateDiffAsync(workspace, taskId, baseBranch, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<string> GetCommitDiffAsync(
        Workspace workspace,
        string commitSha,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        var canonicalPath = GetCanonicalWorkingPath(workspace);

        _logger.LogInformation("Retrieving commit diff for SHA '{CommitSha}' in canonical repo '{CanonicalPath}'",
            commitSha, canonicalPath);

        string[] diffArgs = [
            "show",
            "--format=",
            "-U2",
            commitSha,
            "--",
            ".",
            ":(exclude)package-lock.json",
            ":(exclude)pnpm-lock.yaml",
            ":(exclude)yarn.lock",
            ":(exclude)*.Designer.cs",
            ":(exclude)*.generated.cs"
        ];

        string rawDiff = string.Empty;
        try
        {
            var result = await _gitCliRunner.ExecuteAsync(canonicalPath, diffArgs, cancellationToken: cancellationToken);
            rawDiff = result.StandardOutput;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve commit diff for SHA '{CommitSha}' in canonical repo '{CanonicalPath}'", commitSha, canonicalPath);
        }

        if (string.IsNullOrWhiteSpace(rawDiff) &&
            !string.IsNullOrWhiteSpace(workspace.RepositoryPath) &&
            !string.Equals(workspace.RepositoryPath, canonicalPath, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(workspace.RepositoryPath))
        {
            try
            {
                _logger.LogInformation("Attempting fallback commit diff for SHA '{CommitSha}' in host repo '{RepositoryPath}'", commitSha, workspace.RepositoryPath);
                var fallbackResult = await _gitCliRunner.ExecuteAsync(workspace.RepositoryPath, diffArgs, cancellationToken: cancellationToken);
                rawDiff = fallbackResult.StandardOutput;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fallback commit diff also failed in host repo '{RepositoryPath}'", workspace.RepositoryPath);
            }
        }

        return rawDiff;
    }

    /// <inheritdoc />
    public Task<string> SquashAndMergeAsync(
        Workspace workspace,
        int taskId,
        string baseBranch,
        string commitMessage,
        CancellationToken cancellationToken = default)
        => SquashAndMergeAsync(workspace, taskId, baseBranch, commitMessage, allowCreateBaseBranch: false, cancellationToken: cancellationToken);

    /// <inheritdoc />
    public async Task<string> SquashAndMergeAsync(
        Workspace workspace,
        int taskId,
        string baseBranch,
        string commitMessage,
        bool allowCreateBaseBranch,
        CancellationToken cancellationToken = default)
    {
        var canonicalPath = GetCanonicalWorkingPath(workspace);
        var branchName = $"feature/task-{taskId}";
        var safeMessage = SanitizeMessage(commitMessage);

        _logger.LogInformation("Squashing and merging '{BranchName}' into '{BaseBranch}' in canonical repository '{CanonicalPath}'",
            branchName, baseBranch, canonicalPath);

        // 1. Checkout target base branch in canonical clone (ensuring clean state first)
        await _gitCliRunner.ExecuteAsync(canonicalPath, ["reset", "--hard", "HEAD"], cancellationToken: cancellationToken);
        await _gitCliRunner.ExecuteAsync(canonicalPath, ["clean", "-fd"], cancellationToken: cancellationToken);
        try
        {
            await _gitCliRunner.ExecuteAsync(canonicalPath, ["checkout", baseBranch], cancellationToken: cancellationToken);
        }
        catch (GitCommandException ex)
        {
            if (!allowCreateBaseBranch)
            {
                throw new BaseBranchNotFoundException(baseBranch, canonicalPath, ex);
            }

            await _gitCliRunner.ExecuteAsync(canonicalPath, ["checkout", "-B", baseBranch], cancellationToken: cancellationToken);
        }

        // Ensure feature branch exists locally (fetch from host-origin or origin if working in mirror)
        var branchCheck = await _gitCliRunner.ExecuteAsync(canonicalPath, ["branch", "--list", branchName], cancellationToken: cancellationToken);
        var hasBranch = branchCheck.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(b => b.TrimStart('*', '+', ' ', '\t').Equals(branchName, StringComparison.OrdinalIgnoreCase));

        if (!hasBranch)
        {
            var fetched = false;
            try
            {
                await _gitCliRunner.ExecuteAsync(canonicalPath, ["fetch", "host-origin", $"{branchName}:{branchName}"], cancellationToken: cancellationToken);
                fetched = true;
            }
            catch (GitCommandException ex)
            {
                _logger.LogDebug(ex, "Failed to fetch '{BranchName}' from host-origin in '{CanonicalPath}'. Trying origin...", branchName, canonicalPath);
            }

            if (!fetched)
            {
                try
                {
                    await _gitCliRunner.ExecuteAsync(canonicalPath, ["fetch", "origin", $"{branchName}:{branchName}"], cancellationToken: cancellationToken);
                    fetched = true;
                }
                catch (GitCommandException ex)
                {
                    _logger.LogDebug(ex, "Failed to fetch '{BranchName}' from origin in '{CanonicalPath}'.", branchName, canonicalPath);
                }
            }

            if (!fetched)
            {
                throw new FeatureBranchNotFoundException(branchName, canonicalPath);
            }
        }

        // 2. Perform squash merge
        await _gitCliRunner.ExecuteAsync(canonicalPath, ["clean", "-fd"], cancellationToken: cancellationToken);
        await _gitCliRunner.ExecuteAsync(canonicalPath, ["merge", "--squash", branchName], cancellationToken: cancellationToken);

        // 3. Commit squashed changeset
        await _gitCliRunner.ExecuteAsync(
            canonicalPath,
            ["commit", "-m", safeMessage, "--no-verify"],
            cancellationToken: cancellationToken);

        var revParse = await _gitCliRunner.ExecuteAsync(canonicalPath, ["rev-parse", "HEAD"], cancellationToken: cancellationToken);
        var squashSha = revParse.StandardOutput.Trim();

        _logger.LogInformation("Squash merge completed for ForgeTask #{TaskId} on '{BaseBranch}' with SHA {Sha}", taskId, baseBranch, squashSha);

        // 4. Remove the task's ephemeral worktree
        await RemoveWorktreeAsync(workspace, taskId, cancellationToken);

        return squashSha;
    }

    /// <inheritdoc />
    public Task RevertOrCleanAsync(Workspace workspace, CancellationToken cancellationToken = default)
        => RevertOrCleanAsync(workspace, null, cancellationToken);

    /// <inheritdoc />
    public async Task RevertOrCleanAsync(Workspace workspace, int? taskId, CancellationToken cancellationToken = default)
    {
        var workingPath = GetWorkingPath(workspace, taskId);

        _logger.LogWarning("Reverting and cleaning repository working tree in '{WorkingPath}'", workingPath);

        await _gitCliRunner.ExecuteAsync(workingPath, ["reset", "--hard", "HEAD"], cancellationToken: cancellationToken);
        await _gitCliRunner.ExecuteAsync(workingPath, ["clean", "-fd"], cancellationToken: cancellationToken);
    }

    private string GetWorkingPath(Workspace workspace, int? taskId = null)
    {
        if (taskId.HasValue)
        {
            var wt = GetWorktreePath(workspace, taskId.Value);
            // If the worktree directory actually exists, use it
            if (WorktreeDirectoryExists(wt))
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
                    var distro = _wslMetadataService.GetDefaultDistributionName();
                    var user = _wslMetadataService.GetDefaultUserName();
                    checkPath = WslPathConverter.ToWslUncPath(workspace.Ext4MirrorPath, distro, user);
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
                var distro = _wslMetadataService.GetDefaultDistributionName();
                var user = _wslMetadataService.GetDefaultUserName();
                targetCanonical = WslPathConverter.ToWslUncPath(canonicalPath, distro, user);
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
                if (!lines.Contains("task-*/") && !lines.Contains("task-*"))
                {
                    await File.AppendAllTextAsync(excludePath, "\ntask-*/\nwt/\n", cancellationToken);
                }
            }
            else if (Directory.Exists(targetCanonical))
            {
                var dir = Path.GetDirectoryName(excludePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                await File.WriteAllTextAsync(excludePath, "task-*/\nwt/\n", cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update git info/exclude in '{CanonicalPath}'", canonicalPath);
        }
    }

    private string ResolveHostAccessiblePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && (path.StartsWith('/') || path.StartsWith('~')))
        {
            var distro = _wslMetadataService.GetDefaultDistributionName();
            var user = _wslMetadataService.GetDefaultUserName();
            return WslPathConverter.ToWslUncPath(path, distro, user);
        }
        return path;
    }

    private bool WorktreeDirectoryExists(string worktreePath)
    {
        if (Directory.Exists(worktreePath)) return true;
        var hostPath = ResolveHostAccessiblePath(worktreePath);
        return !string.Equals(hostPath, worktreePath, StringComparison.OrdinalIgnoreCase) && Directory.Exists(hostPath);
    }

    private void DeleteDirectorySafe(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var hostPath = ResolveHostAccessiblePath(path);
        if (Directory.Exists(hostPath))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(hostPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to clear attributes on file '{File}'", file);
                    }
                }

                Directory.Delete(hostPath, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete directory '{HostPath}'", hostPath);
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && (path.StartsWith('/') || path.StartsWith('~')))
        {
            try
            {
                var distro = _pathResolver.GetWslDistributionName() ?? Environment.GetEnvironmentVariable("NCF_WSL_DISTRIBUTION") ?? "Ubuntu";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add(distro);
                psi.ArgumentList.Add("-u");
                psi.ArgumentList.Add("root");
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add("rm");
                psi.ArgumentList.Add("-rf");
                psi.ArgumentList.Add(path);

                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove directory in WSL at '{Path}'", path);
            }
        }
    }
}
