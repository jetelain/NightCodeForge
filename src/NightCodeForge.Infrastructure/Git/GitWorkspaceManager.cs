using System.Collections.Concurrent;
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
    private readonly IGitMirrorSyncService _gitMirrorSyncService;
    private readonly IWorkspaceLockProvider _workspaceLockProvider;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<GitWorkspaceManager> _logger;

    public GitWorkspaceManager(
        IGitCliRunner gitCliRunner,
        IWorkspacePathResolver pathResolver,
        IWslMetadataService wslMetadataService,
        IGitMirrorSyncService gitMirrorSyncService,
        IWorkspaceLockProvider workspaceLockProvider,
        IProcessRunner processRunner,
        ILogger<GitWorkspaceManager> logger)
    {
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _wslMetadataService = wslMetadataService ?? throw new ArgumentNullException(nameof(wslMetadataService));
        _gitMirrorSyncService = gitMirrorSyncService ?? throw new ArgumentNullException(nameof(gitMirrorSyncService));
        _workspaceLockProvider = workspaceLockProvider ?? throw new ArgumentNullException(nameof(workspaceLockProvider));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
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
                await EnsureWorktreeRelativePathsAsync(workspace, taskId, cancellationToken);
                return worktreePath;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list worktrees in '{CanonicalPath}'; proceeding with add", canonicalPath);
        }

        // If directory exists on disk but is not registered in git, clean it up before worktree add
        if (WorktreeDirectoryExists(worktreePath, workspace))
        {
            _logger.LogWarning("Worktree directory '{WorktreePath}' exists on disk but is not registered in git; cleaning up", worktreePath);
            await DeleteDirectorySafeAsync(worktreePath, cancellationToken);
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

        // Configure Git native worktree relative paths
        try
        {
            await _gitCliRunner.ExecuteAsync(canonicalPath, ["config", "worktree.useRelativePaths", "true"], cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Setting worktree.useRelativePaths in '{CanonicalPath}' ignored", canonicalPath);
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
            var effectiveBaseBranch = await ResolveEffectiveBaseBranchCoreAsync(canonicalPath, workspace, taskId, baseBranch, cancellationToken);
            await _gitCliRunner.ExecuteAsync(canonicalPath, ["worktree", "add", "-b", branchName, worktreePath, effectiveBaseBranch], cancellationToken: cancellationToken);
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

        // Verify and enforce bidirectional relative paths for the worktree
        await EnsureWorktreeRelativePathsAsync(workspace, taskId, cancellationToken);

        _logger.LogInformation("Successfully created worktree for ForgeTask #{TaskId} at '{WorktreePath}'", taskId, worktreePath);
        return worktreePath;
    }

    /// <inheritdoc />
    public async Task<string> ResolveEffectiveBaseBranchAsync(
        Workspace workspace,
        int taskId,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var canonicalPath = GetCanonicalWorkingPath(workspace);
        return await ResolveEffectiveBaseBranchCoreAsync(canonicalPath, workspace, taskId, baseBranch, cancellationToken);
    }

    /// <inheritdoc />
    public async Task EnsureWorktreeRelativePathsAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        string canonicalPath;
        try
        {
            canonicalPath = GetCanonicalWorkingPath(workspace);
        }
        catch (DirectoryNotFoundException ex)
        {
            _logger.LogWarning(ex, "Cannot normalize relative worktree paths: canonical path not found for Workspace #{WorkspaceId}", workspace.Id);
            return;
        }

        var worktreePath = GetWorktreePath(workspace, taskId);

        var normWorktree = worktreePath.Replace('\\', '/').TrimEnd('/');
        var normCanonical = canonicalPath.Replace('\\', '/').TrimEnd('/');
        var cloneDirName = Path.GetFileName(normCanonical);
        var wtParent = Path.GetDirectoryName(normWorktree)?.Replace('\\', '/');
        var canParent = Path.GetDirectoryName(normCanonical)?.Replace('\\', '/');

        string targetGitDir;
        string reverseGitDir;

        bool isSibling = !string.IsNullOrEmpty(wtParent) && string.Equals(wtParent, canParent, StringComparison.OrdinalIgnoreCase);

        if (isSibling)
        {
            // Sibling layout: ~/.nightcodeforge/wks/{id}/clone and ~/.nightcodeforge/wks/{id}/task-{id}
            targetGitDir = $"../{cloneDirName}/.git/worktrees/task-{taskId}";
            reverseGitDir = $"../../../../task-{taskId}/.git";
        }
        else
        {
            // Child layout fallback: <canonical>/task-{id}
            targetGitDir = $"../.git/worktrees/task-{taskId}";
            reverseGitDir = $"../../../task-{taskId}/.git";
        }

        // 1. Forward pointer in worktree: <worktree>/.git
        var hostWorktree = ResolveHostAccessiblePath(worktreePath);
        var forwardGitFile = Path.Combine(hostWorktree, ".git");

        if (File.Exists(forwardGitFile))
        {
            try
            {
                var expectedForward = $"gitdir: {targetGitDir}\n";
                var currentForward = await File.ReadAllTextAsync(forwardGitFile, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(currentForward.Trim(), expectedForward.Trim(), StringComparison.Ordinal))
                {
                    try
                    {
                        File.SetAttributes(forwardGitFile, FileAttributes.Normal);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to clear attributes on forward pointer '{ForwardGitFile}'", forwardGitFile);
                    }

                    await File.WriteAllTextAsync(forwardGitFile, expectedForward, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Normalized forward worktree pointer for task #{TaskId} in '{ForwardGitFile}' to relative: '{TargetGitDir}'",
                        taskId, forwardGitFile, targetGitDir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to normalize forward worktree pointer in '{ForwardGitFile}'", forwardGitFile);
            }
        }

        // 2. Reverse pointer in canonical clone: <canonical>/.git/worktrees/task-{taskId}/gitdir
        var hostCanonical = ResolveHostAccessiblePath(canonicalPath);
        var gitVersion = await GetInstalledGitVersionAsync(canonicalPath, cancellationToken).ConfigureAwait(false);
        var supportsRelativeWorktrees = gitVersion is not null && gitVersion >= GitRelativeWorktreeMinimumVersion;

        string expectedReverse;
        if (!supportsRelativeWorktrees)
        {
            expectedReverse = IsPosixPath(normWorktree)
                ? $"{normWorktree}/.git\n"
                : $"{normWorktree.Replace('/', '\\')}\\.git\n";
        }
        else
        {
            expectedReverse = $"{reverseGitDir}\n";
        }

        var reverseGitDirFile = Path.Combine(hostCanonical, ".git", "worktrees", $"task-{taskId}", "gitdir");
        if (File.Exists(reverseGitDirFile))
        {
            try
            {
                var currentReverse = await File.ReadAllTextAsync(reverseGitDirFile, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(currentReverse.Trim(), expectedReverse.Trim(), StringComparison.Ordinal))
                {
                    try
                    {
                        File.SetAttributes(reverseGitDirFile, FileAttributes.Normal);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to clear attributes on reverse pointer '{ReverseGitDirFile}'", reverseGitDirFile);
                    }

                    await File.WriteAllTextAsync(reverseGitDirFile, expectedReverse, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Normalized reverse worktree pointer for task #{TaskId} in '{ReverseGitDirFile}' to: '{ExpectedReverse}'",
                        taskId, reverseGitDirFile, expectedReverse.Trim());
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to normalize reverse worktree pointer in '{ReverseGitDirFile}'", reverseGitDirFile);
            }
        }
        else
        {
            var worktreesParent = Path.Combine(hostCanonical, ".git", "worktrees");
            if (Directory.Exists(worktreesParent))
            {
                try
                {
                    foreach (var subDir in Directory.EnumerateDirectories(worktreesParent))
                    {
                        var candidate = Path.Combine(subDir, "gitdir");
                        if (File.Exists(candidate))
                        {
                            var content = (await File.ReadAllTextAsync(candidate, cancellationToken).ConfigureAwait(false)).Trim();
                            if (content.EndsWith($"/task-{taskId}/.git", StringComparison.OrdinalIgnoreCase) ||
                                content.EndsWith($"\\task-{taskId}\\.git", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(Path.GetFileName(subDir), $"task-{taskId}", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    File.SetAttributes(candidate, FileAttributes.Normal);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "Failed to clear attributes on candidate reverse pointer '{Candidate}'", candidate);
                                }

                                await File.WriteAllTextAsync(candidate, expectedReverse, cancellationToken).ConfigureAwait(false);
                                _logger.LogInformation("Normalized discovered reverse worktree pointer in '{Candidate}' to relative: '{ReverseGitDir}'",
                                    candidate, reverseGitDir);
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to enumerate worktree administrative directories in '{WorktreesParent}'", worktreesParent);
                }
            }
        }
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
            if (WorktreeDirectoryExists(worktreePath, workspace))
            {
                await DeleteDirectorySafeAsync(worktreePath, cancellationToken);
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
        var effectiveBaseBranch = await ResolveEffectiveBaseBranchCoreAsync(workingPath, workspace, taskId, baseBranch, cancellationToken);

        _logger.LogInformation("Generating noise-filtered diff for ForgeTask #{TaskId} against '{BaseBranch}' in '{WorkingPath}'",
            taskId, effectiveBaseBranch, workingPath);

        // -U2 context window reduction and noisy artifact exclusions
        string[] diffArgs = BuildDiffArgs($"{effectiveBaseBranch}...{branchName}", isStat: false);

        string rawDiff = string.Empty;
        bool diffSucceeded = false;
        try
        {
            var result = await _gitCliRunner.ExecuteAsync(workingPath, diffArgs, cancellationToken: cancellationToken);
            rawDiff = result.StandardOutput;
            diffSucceeded = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate branch diff for ForgeTask #{TaskId} in '{WorkingPath}'", taskId, workingPath);
        }

        string? canonicalPath = null;
        try
        {
            canonicalPath = GetCanonicalWorkingPath(workspace);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve canonical path for workspace #{WorkspaceId}", workspace.Id);
        }

        if (!diffSucceeded &&
            !string.IsNullOrWhiteSpace(canonicalPath) &&
            !string.Equals(canonicalPath, workingPath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _logger.LogInformation("Attempting fallback branch diff for ForgeTask #{TaskId} against canonical repo '{CanonicalPath}'", taskId, canonicalPath);
                var canonicalResult = await _gitCliRunner.ExecuteAsync(canonicalPath, diffArgs, cancellationToken: cancellationToken);
                rawDiff = canonicalResult.StandardOutput;
                diffSucceeded = true;
            }
            catch (GitCommandException ex)
            {
                _logger.LogDebug(ex, "Fallback diff generation against canonical repository '{CanonicalPath}' failed", canonicalPath);
            }
        }

        if (!diffSucceeded &&
            !string.IsNullOrWhiteSpace(workspace.RepositoryPath) &&
            !string.Equals(workspace.RepositoryPath, workingPath, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(workspace.RepositoryPath, canonicalPath, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(workspace.RepositoryPath))
        {
            try
            {
                _logger.LogInformation("Attempting fallback branch diff for ForgeTask #{TaskId} against host repo '{RepositoryPath}'", taskId, workspace.RepositoryPath);
                var fallbackResult = await _gitCliRunner.ExecuteAsync(workspace.RepositoryPath, diffArgs, cancellationToken: cancellationToken);
                rawDiff = fallbackResult.StandardOutput;
                diffSucceeded = true;
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
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(workspace);

        var worktreePath = GetWorktreePath(workspace, taskId);
        if (!WorktreeDirectoryExists(worktreePath, workspace))
        {
            throw new WorktreeNotFoundException(taskId, worktreePath);
        }

        var effectiveBaseBranch = await ResolveEffectiveBaseBranchCoreAsync(worktreePath, workspace, taskId, baseBranch, cancellationToken);

        _logger.LogInformation("Generating live diff for ForgeTask #{TaskId} against '{BaseBranch}' in '{WorktreePath}'",
            taskId, effectiveBaseBranch, worktreePath);

        var diffTarget = await ResolveMergeBaseAsync(worktreePath, effectiveBaseBranch, cancellationToken);
        string[] diffArgs = BuildDiffArgs(diffTarget, isStat: false);

        var result = await ExecuteWithIsolatedIndexAsync(worktreePath, taskId, diffArgs, cancellationToken);
        return result.StandardOutput;
    }

    /// <inheritdoc />
    public async Task<string> GenerateDiffStatAsync(
        Workspace workspace,
        int taskId,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(workspace);
        var workingPath = GetWorkingPath(workspace, taskId);
        var branchName = $"feature/task-{taskId}";

        _logger.LogInformation("Generating diff stat for ForgeTask #{TaskId} against '{BaseBranch}' in '{WorkingPath}'",
            taskId, baseBranch, workingPath);

        string[] statArgs = BuildDiffArgs($"{baseBranch}...{branchName}", isStat: true);

        var targetPath = workspace.ExecutionMode == ExecutionMode.ContainerSandbox || IsPosixPath(workingPath) || WorktreeDirectoryExists(workingPath, workspace) || Directory.Exists(workingPath)
            ? workingPath
            : (workspace.ExecutionMode != ExecutionMode.ContainerSandbox && Directory.Exists(workspace.RepositoryPath) ? workspace.RepositoryPath : workingPath);

        Exception? firstException = null;
        GitCommandException? lastGitException = null;
        string rawStat = string.Empty;
        bool statSucceeded = false;
        try
        {
            var result = await _gitCliRunner.ExecuteAsync(targetPath, statArgs, cancellationToken: cancellationToken);
            rawStat = result.StandardOutput;
            statSucceeded = true;
        }
        catch (Exception ex)
        {
            firstException = ex;
            if (ex is GitCommandException gce)
            {
                lastGitException = gce;
            }
            _logger.LogWarning(ex, "Failed to generate diff stat for ForgeTask #{TaskId} in '{TargetPath}'", taskId, targetPath);
        }

        string? canonicalPath = null;
        try
        {
            canonicalPath = GetCanonicalWorkingPath(workspace);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve canonical path for workspace #{WorkspaceId}", workspace.Id);
        }

        if (!statSucceeded &&
            !string.IsNullOrWhiteSpace(canonicalPath) &&
            !string.Equals(canonicalPath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _logger.LogInformation("Attempting fallback diff stat for ForgeTask #{TaskId} against canonical repo '{CanonicalPath}'", taskId, canonicalPath);
                var canonicalResult = await _gitCliRunner.ExecuteAsync(canonicalPath, statArgs, cancellationToken: cancellationToken);
                rawStat = canonicalResult.StandardOutput;
                statSucceeded = true;
                lastGitException = null;
                firstException = null;
            }
            catch (GitCommandException ex)
            {
                lastGitException = ex;
                _logger.LogDebug(ex, "Fallback diff stat generation against canonical repository '{CanonicalPath}' failed", canonicalPath);
            }
        }

        if (!statSucceeded &&
            !string.IsNullOrWhiteSpace(workspace.RepositoryPath) &&
            !string.Equals(workspace.RepositoryPath, targetPath, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(workspace.RepositoryPath, canonicalPath, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(workspace.RepositoryPath))
        {
            try
            {
                _logger.LogInformation("Attempting fallback diff stat for ForgeTask #{TaskId} against host repo '{RepositoryPath}'", taskId, workspace.RepositoryPath);
                var fallbackResult = await _gitCliRunner.ExecuteAsync(workspace.RepositoryPath, statArgs, cancellationToken: cancellationToken);
                rawStat = fallbackResult.StandardOutput;
                statSucceeded = true;
                lastGitException = null;
                firstException = null;
            }
            catch (GitCommandException ex)
            {
                lastGitException = ex;
                _logger.LogDebug(ex, "Fallback diff stat generation against host repository '{RepoPath}' failed", workspace.RepositoryPath);
            }
        }

        if (!statSucceeded)
        {
            if (lastGitException != null)
            {
                throw lastGitException;
            }
            if (firstException != null)
            {
                throw firstException;
            }
        }

        return FormatStatOutput(rawStat);
    }

    /// <inheritdoc />
    public async Task<string> GenerateLiveDiffStatAsync(
        Workspace workspace,
        int taskId,
        string baseBranch = "main",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(workspace);

        var worktreePath = GetWorktreePath(workspace, taskId);
        if (!WorktreeDirectoryExists(worktreePath, workspace))
        {
            throw new WorktreeNotFoundException(taskId, worktreePath);
        }

        var effectiveBaseBranch = await ResolveEffectiveBaseBranchCoreAsync(worktreePath, workspace, taskId, baseBranch, cancellationToken);

        _logger.LogInformation("Generating live diff stat for ForgeTask #{TaskId} against '{BaseBranch}' in '{WorktreePath}'",
            taskId, effectiveBaseBranch, worktreePath);

        var statTarget = await ResolveMergeBaseAsync(worktreePath, effectiveBaseBranch, cancellationToken);
        string[] statArgs = BuildDiffArgs(statTarget, isStat: true);

        var result = await ExecuteWithIsolatedIndexAsync(worktreePath, taskId, statArgs, cancellationToken);
        return FormatStatOutput(result.StandardOutput);
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

        var diffArgs = new List<string> { "show", "--format=", "-U2", commitSha, "--", "." };
        diffArgs.AddRange(GitDiffConstants.ExclusionPathspecs);

        string rawDiff = string.Empty;
        try
        {
            var result = await _gitCliRunner.ExecuteAsync(canonicalPath, diffArgs.ToArray(), cancellationToken: cancellationToken);
            rawDiff = result.StandardOutput;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve commit diff for SHA '{CommitSha}' in canonical repo '{CanonicalPath}'", commitSha, canonicalPath);
        }

        if (string.IsNullOrWhiteSpace(rawDiff) &&
            workspace.ExecutionMode != ExecutionMode.ContainerSandbox &&
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

        // Capture pre-squash SHA on base branch for deterministic rollback capability
        var preRev = await _gitCliRunner.ExecuteAsync(canonicalPath, ["rev-parse", "HEAD"], cancellationToken: cancellationToken);
        var preSquashSha = preRev.StandardOutput.Trim();

        // 2. Perform squash merge
        try
        {
            await _gitCliRunner.ExecuteAsync(canonicalPath, ["merge", "--squash", branchName], cancellationToken: cancellationToken);
        }
        catch (GitCommandException ex)
        {
            // Collect conflicted files before resetting canonical clone
            var diffResult = await _gitCliRunner.ExecuteAsync(canonicalPath, ["diff", "--name-only", "--diff-filter=U"], cancellationToken: cancellationToken);
            var conflictedFiles = diffResult.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            // Atomic rollback: restore canonical clone with git reset --hard HEAD (clean -fd is forbidden)
            await _gitCliRunner.ExecuteAsync(canonicalPath, ["reset", "--hard", "HEAD"], cancellationToken: cancellationToken);

            if (conflictedFiles.Count > 0)
            {
                _logger.LogWarning("Squash merge conflicted for ForgeTask #{TaskId} with '{BaseBranch}' in {Count} files: {Files}",
                    taskId, baseBranch, conflictedFiles.Count, string.Join(", ", conflictedFiles));
                throw new MergeConflictException(taskId, baseBranch, conflictedFiles, ex.StandardError + "\n" + ex.StandardOutput, innerException: ex);
            }

            throw;
        }

        // 3. Commit squashed changeset
        await _gitCliRunner.ExecuteAsync(
            canonicalPath,
            ["commit", "-m", safeMessage, "--no-verify"],
            cancellationToken: cancellationToken);

        var revParse = await _gitCliRunner.ExecuteAsync(canonicalPath, ["rev-parse", "HEAD"], cancellationToken: cancellationToken);
        var squashSha = revParse.StandardOutput.Trim();

        _logger.LogInformation("Squash merge completed for ForgeTask #{TaskId} on '{BaseBranch}' with SHA {Sha}", taskId, baseBranch, squashSha);

        // Worktree Preservation Rule: worktree remains intact until closed transition succeeds
        return squashSha;
    }

    public static readonly Version GitRelativeWorktreeMinimumVersion = new(2, 45, 0);
    private readonly ConcurrentDictionary<string, Version?> _gitVersionCache = new(StringComparer.OrdinalIgnoreCase);

    private async Task<Version?> GetInstalledGitVersionAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        if (_gitVersionCache.TryGetValue(workingDirectory, out var cachedVersion))
        {
            return cachedVersion;
        }

        try
        {
            var result = await _gitCliRunner.ExecuteAsync(workingDirectory, ["--version"], cancellationToken: cancellationToken).ConfigureAwait(false);
            var match = System.Text.RegularExpressions.Regex.Match(result.StandardOutput, @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (match.Success)
            {
                var major = int.Parse(match.Groups[1].Value);
                var minor = int.Parse(match.Groups[2].Value);
                var build = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
                var version = new Version(major, minor, build);
                _gitVersionCache[workingDirectory] = version;
                return version;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to determine Git version in '{WorkingDirectory}'", workingDirectory);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task ValidateGitVersionAsync(CancellationToken cancellationToken = default)
    {
        var canonicalOrGlobal = AppContext.BaseDirectory;
        var installedVersion = await GetInstalledGitVersionAsync(canonicalOrGlobal, cancellationToken)
            ?? throw new InvalidOperationException("Unable to determine the installed Git version.");

        if (installedVersion < NightCodeForge.Core.Workflow.ConflictResolutionConstants.MinimumRequiredGitVersion)
        {
            throw new InvalidOperationException(
                $"Installed Git version {installedVersion} is older than required minimum {NightCodeForge.Core.Workflow.ConflictResolutionConstants.MinimumRequiredGitVersion}. Please upgrade Git.");
        }
    }

    /// <inheritdoc />
    public async Task<MergeSyncResult> SyncFromBaseBranchAsync(
        Workspace workspace,
        int taskId,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);

        var worktreePath = GetWorktreePath(workspace, taskId);
        if (!WorktreeDirectoryExists(worktreePath, workspace))
        {
            await EnsureWorktreeAsync(workspace, taskId, baseBranch, cancellationToken);
        }

        // 1. Check if merge is already in progress (resuming)
        var isMergeInProgress = await IsMergeInProgressAsync(worktreePath, cancellationToken);
        if (isMergeInProgress)
        {
            _logger.LogInformation("Resuming existing in-progress merge in worktree '{WorktreePath}' for task #{TaskId}", worktreePath, taskId);
            var unmergedResult = await _gitCliRunner.ExecuteAsync(worktreePath, ["diff", "--name-only", "--diff-filter=U"], cancellationToken: cancellationToken);
            var unmergedFiles = unmergedResult.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            return new MergeSyncResult(
                Success: false,
                HasConflict: true,
                ConflictedFiles: unmergedFiles,
                MergeCommitSha: null);
        }

        // 2. Precondition: Worktree must be clean if merge is not in progress
        var statusCheck = await _gitCliRunner.ExecuteAsync(worktreePath, ["status", "--porcelain"], cancellationToken: cancellationToken);
        if (!string.IsNullOrWhiteSpace(statusCheck.StandardOutput))
        {
            throw new GitCommandException(
                worktreePath,
                ["status", "--porcelain"],
                1,
                statusCheck.StandardOutput,
                $"Worktree for task #{taskId} is dirty with uncommitted changes. Cannot synchronize from base branch.");
        }

        // 3. Pull latest base branch in canonical clone / ext4 mirror under workspace lock
        var effectiveBaseBranch = await ResolveEffectiveBaseBranchCoreAsync(worktreePath, workspace, taskId, baseBranch, cancellationToken);
        if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            using var wsLock = await _workspaceLockProvider.AcquireLockAsync(workspace.Id, cancellationToken);
            await _gitMirrorSyncService.PullFromHostAsync(workspace.Ext4MirrorPath, effectiveBaseBranch, cancellationToken);
        }

        // 4. Merge baseBranch into worktree with diff3 conflict style
        try
        {
            var mergeResult = await _gitCliRunner.ExecuteAsync(
                worktreePath,
                ["-c", "merge.conflictStyle=diff3", "merge", "--no-edit", effectiveBaseBranch],
                cancellationToken: cancellationToken);

            var revParse = await _gitCliRunner.ExecuteAsync(worktreePath, ["rev-parse", "HEAD"], cancellationToken: cancellationToken);
            var currentSha = revParse.StandardOutput.Trim();

            _logger.LogInformation("Cleanly synchronized feature branch for task #{TaskId} with '{BaseBranch}' (SHA: {Sha})", taskId, effectiveBaseBranch, currentSha);

            return new MergeSyncResult(
                Success: true,
                HasConflict: false,
                ConflictedFiles: Array.Empty<string>(),
                MergeCommitSha: currentSha);
        }
        catch (GitCommandException)
        {
            // Enumerate conflicted files
            var diffResult = await _gitCliRunner.ExecuteAsync(worktreePath, ["diff", "--name-only", "--diff-filter=U"], cancellationToken: cancellationToken);
            var conflictedFiles = diffResult.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            if (conflictedFiles.Count > 0)
            {
                _logger.LogWarning("Merge conflict detected while synchronizing task #{TaskId} with '{BaseBranch}': {Files}",
                    taskId, baseBranch, string.Join(", ", conflictedFiles));

                return new MergeSyncResult(
                    Success: false,
                    HasConflict: true,
                    ConflictedFiles: conflictedFiles,
                    MergeCommitSha: null);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<MergeabilityCheckResult> CheckMergeableAsync(
        Workspace workspace,
        int taskId,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);

        var canonicalPath = GetCanonicalWorkingPath(workspace);
        var branchName = $"feature/task-{taskId}";

        var baseShaResult = await _gitCliRunner.ExecuteAsync(canonicalPath, ["rev-parse", baseBranch], cancellationToken: cancellationToken);
        var baseSha = baseShaResult.StandardOutput.Trim();

        var featureShaResult = await _gitCliRunner.ExecuteAsync(canonicalPath, ["rev-parse", branchName], cancellationToken: cancellationToken);
        var featureSha = featureShaResult.StandardOutput.Trim();

        GitCommandResult? mergeTreeResult = null;
        string mergeTreeOutput = string.Empty;
        bool hasConflict = false;

        try
        {
            mergeTreeResult = await _gitCliRunner.ExecuteAsync(
                canonicalPath,
                ["merge-tree", "--write-tree", "--name-only", baseBranch, branchName],
                cancellationToken: cancellationToken);
            mergeTreeOutput = mergeTreeResult.StandardOutput;
        }
        catch (GitCommandException ex) when (ex.ExitCode == 1)
        {
            hasConflict = true;
            mergeTreeOutput = ex.StandardOutput;
        }

        if (!hasConflict && mergeTreeResult?.ExitCode == 0)
        {
            return new MergeabilityCheckResult(
                IsMergeable: true,
                ConflictedFiles: Array.Empty<string>(),
                BaseBranchSha: baseSha,
                FeatureBranchSha: featureSha);
        }

        if (hasConflict || mergeTreeResult?.ExitCode == 1)
        {
            // Parse structured output: line 0 is tree hash; subsequent lines until empty line are conflicted files
            var lines = mergeTreeOutput
                .Split('\n')
                .Select(l => l.Trim())
                .ToList();

            var conflictedFiles = new List<string>();
            for (int i = 1; i < lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    break;
                }
                conflictedFiles.Add(lines[i]);
            }

            return new MergeabilityCheckResult(
                IsMergeable: false,
                ConflictedFiles: conflictedFiles,
                BaseBranchSha: baseSha,
                FeatureBranchSha: featureSha);
        }

        throw new GitCommandException(
            canonicalPath,
            ["merge-tree", "--write-tree", "--name-only", baseBranch, branchName],
            mergeTreeResult!.ExitCode,
            mergeTreeResult.StandardOutput,
            mergeTreeResult.StandardError);
    }

    /// <inheritdoc />
    public async Task AbortMergeAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var worktreePath = GetWorktreePath(workspace, taskId);
        if (!WorktreeDirectoryExists(worktreePath, workspace)) return;

        if (await IsMergeInProgressAsync(worktreePath, cancellationToken))
        {
            _logger.LogInformation("Aborting in-progress merge for task #{TaskId} in '{WorktreePath}'", taskId, worktreePath);
            await _gitCliRunner.ExecuteAsync(worktreePath, ["merge", "--abort"], cancellationToken: cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<string> GetBranchShaAsync(Workspace workspace, string branchName, CancellationToken cancellationToken = default)
    {
        var canonicalPath = GetCanonicalWorkingPath(workspace);
        var result = await _gitCliRunner.ExecuteAsync(canonicalPath, ["rev-parse", branchName], cancellationToken: cancellationToken);
        return result.StandardOutput.Trim();
    }

    /// <inheritdoc />
    public async Task ResetCanonicalBranchAsync(Workspace workspace, string baseBranch, string targetSha, CancellationToken cancellationToken = default)
    {
        var canonicalPath = GetCanonicalWorkingPath(workspace);
        await _gitCliRunner.ExecuteAsync(canonicalPath, ["checkout", baseBranch], cancellationToken: cancellationToken);
        await _gitCliRunner.ExecuteAsync(canonicalPath, ["reset", "--hard", targetSha], cancellationToken: cancellationToken);
    }

    private async Task<bool> IsMergeInProgressAsync(string worktreePath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _gitCliRunner.ExecuteAsync(worktreePath, ["rev-parse", "-q", "--verify", "MERGE_HEAD"], cancellationToken: cancellationToken);
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput);
        }
        catch (GitCommandException)
        {
            return false;
        }
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

    /// <inheritdoc />
    public async Task ResetTaskBranchToBaseAsync(
        Workspace workspace,
        int taskId,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);

        var canonicalPath = GetCanonicalWorkingPath(workspace);
        var worktreePath = GetWorktreePath(workspace, taskId);
        var branchName = $"feature/task-{taskId}";
        var effectiveBaseBranch = await ResolveEffectiveBaseBranchCoreAsync(canonicalPath, workspace, taskId, baseBranch, cancellationToken);

        _logger.LogInformation("Hard-resetting branch '{BranchName}' to base branch '{BaseBranch}' for task #{TaskId}",
            branchName, effectiveBaseBranch, taskId);

        // 1. Upstream Base Branch Synchronization
        if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            await _gitMirrorSyncService.PullFromHostAsync(workspace.Ext4MirrorPath, effectiveBaseBranch, cancellationToken);
        }

        var remotesResult = await _gitCliRunner.ExecuteAsync(canonicalPath, ["remote"], cancellationToken: cancellationToken);
        var remotes = remotesResult.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var primaryRemote = remotes.FirstOrDefault(r => string.Equals(r, "host-origin", StringComparison.OrdinalIgnoreCase))
            ?? remotes.FirstOrDefault(r => string.Equals(r, "origin", StringComparison.OrdinalIgnoreCase));

        string targetResetRef = effectiveBaseBranch;
        if (primaryRemote != null)
        {
            // Upstream remote configured: fetch latest commits from remote base branch and reset to remote ref
            await _gitCliRunner.ExecuteAsync(canonicalPath, ["fetch", primaryRemote, effectiveBaseBranch], cancellationToken: cancellationToken);
            targetResetRef = $"{primaryRemote}/{effectiveBaseBranch}";
        }

        // 2. Worktree Clean and Reset
        bool isWorktreeRegistered = false;
        try
        {
            var worktreeListResult = await _gitCliRunner.ExecuteAsync(canonicalPath, ["worktree", "list", "--porcelain"], cancellationToken: cancellationToken);
            var normWorktreePath = worktreePath.Replace('\\', '/').TrimEnd('/');
            isWorktreeRegistered = worktreeListResult.StandardOutput
                .Split('\n')
                .Any(line => line.StartsWith("worktree ", StringComparison.OrdinalIgnoreCase) &&
                             line[9..].Trim().Replace('\\', '/').TrimEnd('/').Equals(normWorktreePath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list worktrees in '{CanonicalPath}'", canonicalPath);
        }

        bool worktreeExistsOnDisk = WorktreeDirectoryExists(worktreePath, workspace);

        if (isWorktreeRegistered && worktreeExistsOnDisk)
        {
            // Case A: Worktree Exists & Is Registered
            // Spec ordering: Clean and purge untracked files FIRST, then hard reset to base branch ref
            await _gitCliRunner.ExecuteAsync(worktreePath, ["reset", "--hard", "HEAD"], cancellationToken: cancellationToken);
            await _gitCliRunner.ExecuteAsync(worktreePath, ["clean", "-fdx"], cancellationToken: cancellationToken);
            await _gitCliRunner.ExecuteAsync(worktreePath, ["reset", "--hard", targetResetRef], cancellationToken: cancellationToken);

            await EnsureWorktreeRelativePathsAsync(workspace, taskId, cancellationToken);
        }
        else
        {
            // Case B: Worktree Does Not Exist or Is Corrupted
            try
            {
                await _gitCliRunner.ExecuteAsync(canonicalPath, ["worktree", "prune"], cancellationToken: cancellationToken);
            }
            catch (GitCommandException ex)
            {
                _logger.LogDebug(ex, "Worktree prune failed in '{CanonicalPath}'", canonicalPath);
            }

            if (worktreeExistsOnDisk && !isWorktreeRegistered)
            {
                await DeleteDirectorySafeAsync(worktreePath, cancellationToken);
            }

            await _gitCliRunner.ExecuteAsync(canonicalPath, ["branch", "-f", branchName, targetResetRef], cancellationToken: cancellationToken);
            await EnsureWorktreeAsync(workspace, taskId, effectiveBaseBranch, cancellationToken);
        }

        // 3. Remote Branch Reconciliation
        foreach (var targetRemote in new[] { "host-origin", "origin" })
        {
            if (remotes.Contains(targetRemote, StringComparer.OrdinalIgnoreCase))
            {
                var lsRemote = await _gitCliRunner.ExecuteAsync(canonicalPath, ["ls-remote", "--heads", targetRemote, branchName], cancellationToken: cancellationToken);
                if (!string.IsNullOrWhiteSpace(lsRemote.StandardOutput) && lsRemote.StandardOutput.Contains(branchName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Reconciling remote branch '{BranchName}' on '{Remote}' after hard reset", branchName, targetRemote);
                    await _gitCliRunner.ExecuteAsync(canonicalPath, ["push", "--force", targetRemote, branchName], cancellationToken: cancellationToken);
                }
            }
        }
    }

    private string GetWorkingPath(Workspace workspace, int? taskId = null)
    {
        if (taskId.HasValue)
        {
            var wt = GetWorktreePath(workspace, taskId.Value);
            // If in container sandbox or POSIX path or the worktree directory actually exists, use it
            if (workspace.ExecutionMode == ExecutionMode.ContainerSandbox || IsPosixPath(wt) || WorktreeDirectoryExists(wt, workspace))
            {
                return wt;
            }
        }

        return GetCanonicalWorkingPath(workspace);
    }

    private string GetCanonicalWorkingPath(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (workspace.ExecutionMode == ExecutionMode.ContainerSandbox && !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            return workspace.Ext4MirrorPath;
        }

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
                    checkPath = ToWslUncPathOrThrow(workspace.Ext4MirrorPath);
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
                targetCanonical = ToWslUncPathOrThrow(canonicalPath);
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
            return ToWslUncPathOrThrow(path);
        }
        return path;
    }

    private string ToWslUncPathOrThrow(string posixPath)
    {
        var distro = _wslMetadataService.GetDefaultDistributionName();
        if (string.IsNullOrWhiteSpace(distro))
        {
            throw new WslDistributionUnavailableException(posixPath);
        }
        var user = _wslMetadataService.GetDefaultUserName();
        return WslPathConverter.ToWslUncPath(posixPath, distro, user);
    }

    private const char PosixRootPathPrefix = '/';

    /// <summary>
    /// A rooted POSIX path is only foreign (WSL/container) when the host is Windows.
    /// On Linux/macOS every absolute path starts with '/', so it carries no information.
    /// </summary>
    public static bool IsForeignPosixPath(string? path, bool isWindowsHost) =>
        isWindowsHost && !string.IsNullOrEmpty(path) && path[0] == PosixRootPathPrefix;

    private static bool IsPosixPath(string path) =>
        IsForeignPosixPath(path, RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

    private bool WorktreeDirectoryExists(string worktreePath, Workspace workspace)
    {
        if (Directory.Exists(worktreePath))
        {
            return true;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && (worktreePath.StartsWith('/') || worktreePath.StartsWith('~')))
        {
            try
            {
                var hostPath = ResolveHostAccessiblePath(worktreePath);
                if (!string.Equals(hostPath, worktreePath, StringComparison.OrdinalIgnoreCase) && Directory.Exists(hostPath))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to resolve host-accessible path for worktree '{WorktreePath}'", worktreePath);
            }

            // In ContainerSandbox mode, the worktree path resides inside the container/WSL environment
            // and may not be accessible via Windows host file APIs (or when mocked in unit tests).
            // Defer existence validation to IGitCliRunner which validates via WSL or container context.
            if (workspace.ExecutionMode == ExecutionMode.ContainerSandbox)
            {
                return true;
            }
        }

        return false;
    }

    private string ResolveTempIndexPath(string worktreePath, int taskId)
    {
        var isWsl = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            (worktreePath.StartsWith('/') || worktreePath.StartsWith('~') || WslPathConverter.IsWslUncPath(worktreePath));

        if (isWsl || (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && worktreePath.StartsWith('/')))
        {
            return $"/tmp/ncf_diff_{taskId}_{Guid.NewGuid():N}.index";
        }

        return Path.Combine(Path.GetTempPath(), $"ncf_diff_{taskId}_{Guid.NewGuid():N}.index");
    }

    private async Task<string> ResolveMergeBaseAsync(string worktreePath, string baseBranch, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _gitCliRunner.ExecuteAsync(worktreePath, ["merge-base", baseBranch, "HEAD"], cancellationToken: cancellationToken);
            var sha = result.StandardOutput.Trim();
            if (!string.IsNullOrWhiteSpace(sha))
            {
                return sha;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve merge-base against '{BaseBranch}' in '{WorktreePath}'; using base branch directly", baseBranch, worktreePath);
        }

        return baseBranch;
    }

    private async Task<string> ResolveEffectiveBaseBranchCoreAsync(
        string workingDir,
        Workspace workspace,
        int taskId,
        string baseBranch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDir) ||
            string.IsNullOrWhiteSpace(workspace.DefaultBaseBranch) ||
            string.Equals(baseBranch, workspace.DefaultBaseBranch, StringComparison.OrdinalIgnoreCase))
        {
            return baseBranch;
        }

        var effectiveBaseBranch = baseBranch;
        var baseExists = await CheckRefExistsAsync(workingDir, effectiveBaseBranch, cancellationToken);
        if (!baseExists)
        {
            var defaultExists = await CheckRefExistsAsync(workingDir, workspace.DefaultBaseBranch, cancellationToken);
            if (defaultExists)
            {
                _logger.LogWarning(
                    "ForgeTask #{TaskId} specifies base branch '{BaseBranch}', which does not exist in '{WorkingDir}'. Falling back to workspace default base branch '{DefaultBaseBranch}'.",
                    taskId, baseBranch, workingDir, workspace.DefaultBaseBranch);
                effectiveBaseBranch = workspace.DefaultBaseBranch;
            }
        }

        return effectiveBaseBranch;
    }

    private async Task<bool> CheckRefExistsAsync(string workingDir, string refName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDir) || string.IsNullOrWhiteSpace(refName))
        {
            return false;
        }

        try
        {
            var result = await _gitCliRunner.ExecuteAsync(workingDir, ["rev-parse", "-q", "--verify", refName], cancellationToken: cancellationToken);
            return result != null && result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to verify ref '{RefName}' in '{WorkingDir}'", refName, workingDir);
            return false;
        }
    }

    private async Task<GitCommandResult> ExecuteWithIsolatedIndexAsync(
        string worktreePath,
        int taskId,
        string[] gitArgs,
        CancellationToken cancellationToken)
    {
        var tempIndex = ResolveTempIndexPath(worktreePath, taskId);
        try
        {
            var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = tempIndex };

            // 1. Initialize temp index from HEAD refs without touching .git/index
            await _gitCliRunner.ExecuteAsync(
                worktreePath,
                ["read-tree", "HEAD"],
                timeoutSeconds: null,
                environmentVariables: env,
                cancellationToken: cancellationToken);

            // 2. Register untracked files into the temporary index only
            var addArgs = new List<string> { "add", "-N", "--", "." };
            addArgs.AddRange(GitDiffConstants.ExclusionPathspecs);
            await _gitCliRunner.ExecuteAsync(
                worktreePath,
                addArgs.ToArray(),
                timeoutSeconds: null,
                environmentVariables: env,
                cancellationToken: cancellationToken);

            // 3. Diff comparing base target with working tree via the temporary index
            return await _gitCliRunner.ExecuteAsync(
                worktreePath,
                gitArgs,
                timeoutSeconds: null,
                environmentVariables: env,
                cancellationToken: cancellationToken);
        }
        catch (DirectoryNotFoundException dex)
        {
            throw new WorktreeNotFoundException(taskId, worktreePath, dex);
        }
        finally
        {
            await DeleteTempIndexSafeAsync(tempIndex, CancellationToken.None);
        }
    }

    private async Task DeleteTempIndexSafeAsync(string tempIndex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tempIndex)) return;

        try
        {
            if (File.Exists(tempIndex))
            {
                File.Delete(tempIndex);
                return;
            }

            var hostPath = ResolveHostAccessiblePath(tempIndex);
            if (!string.Equals(hostPath, tempIndex, StringComparison.OrdinalIgnoreCase) && File.Exists(hostPath))
            {
                File.Delete(hostPath);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete temp index via file API at '{TempIndex}'", tempIndex);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && (tempIndex.StartsWith('/') || tempIndex.StartsWith('~')))
        {
            try
            {
                var distro = _wslMetadataService.GetDefaultDistributionName();
                if (string.IsNullOrWhiteSpace(distro))
                {
                    distro = _pathResolver.GetWslDistributionName();
                }

                if (string.IsNullOrWhiteSpace(distro))
                {
                    _logger.LogWarning("Cannot remove temp index '{TempIndex}' in WSL: No WSL distribution name resolved.", tempIndex);
                    return;
                }

                var spec = new ProcessStartSpec
                {
                    FileName = "wsl.exe",
                    ArgumentList = ["-d", distro, "--exec", "rm", "-f", tempIndex],
                    TimeoutSeconds = 5
                };

                var result = await _processRunner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    _logger.LogWarning("Failed to remove temp index in WSL at '{TempIndex}'. Exit code: {ExitCode}. Stderr: {StdErr}",
                        tempIndex, result.ExitCode, result.StandardError);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove temp index in WSL at '{TempIndex}'", tempIndex);
            }
        }
    }

    private async Task DeleteDirectorySafeAsync(string path, CancellationToken cancellationToken = default)
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
                var distro = _wslMetadataService.GetDefaultDistributionName();
                if (string.IsNullOrWhiteSpace(distro))
                {
                    distro = _pathResolver.GetWslDistributionName();
                }

                if (string.IsNullOrWhiteSpace(distro))
                {
                    _logger.LogWarning("Cannot remove directory '{Path}' in WSL: No WSL distribution name resolved.", path);
                    return;
                }

                var spec = new ProcessStartSpec
                {
                    FileName = "wsl.exe",
                    ArgumentList = ["-d", distro, "-u", "root", "--", "rm", "-rf", path],
                    TimeoutSeconds = 10
                };

                var result = await _processRunner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    _logger.LogWarning("Failed to remove directory in WSL at '{Path}'. Exit code: {ExitCode}. Stderr: {StdErr}",
                        path, result.ExitCode, result.StandardError);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove directory in WSL at '{Path}'", path);
            }
        }
    }

    private static string[] BuildDiffArgs(string revisionRange, bool isStat = false)
    {
        var args = new List<string> { "diff", revisionRange };
        if (isStat)
        {
            args.Add($"--stat={GitDiffConstants.DefaultStatWidth},{GitDiffConstants.DefaultStatNameWidth}");
        }
        else
        {
            args.Add("-U2");
        }

        args.Add("--");
        args.Add(".");
        args.AddRange(GitDiffConstants.ExclusionPathspecs);
        return args.ToArray();
    }

    private static string FormatStatOutput(string rawOutput, int maxFiles = GitDiffConstants.DefaultMaxStatFiles)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return GitDiffConstants.NoChanges;
        }

        var lines = rawOutput.Split('\n', StringSplitOptions.TrimEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (lines.Count == 0)
        {
            return GitDiffConstants.NoChanges;
        }

        if (lines.Count == 1 && lines[0].Contains("0 files changed", StringComparison.OrdinalIgnoreCase))
        {
            return GitDiffConstants.NoChanges;
        }

        if (lines.Count <= 1)
        {
            return rawOutput.Trim();
        }

        var summaryLine = lines[^1];
        var fileLines = lines.Take(lines.Count - 1).ToList();

        if (fileLines.Count <= maxFiles)
        {
            return rawOutput.Trim();
        }

        var kept = fileLines.Take(maxFiles);
        int remaining = fileLines.Count - maxFiles;
        return string.Join("\n", kept.Append($" ... and {remaining} more files").Append(summaryLine)).Trim();
    }
}

