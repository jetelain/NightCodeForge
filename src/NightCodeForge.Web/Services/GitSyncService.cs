using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Default implementation of <see cref="IGitSyncService"/> that queries Ext4 mirror repository status,
/// active worktrees, and performs host dirty tree reconciliations.
/// </summary>
public class GitSyncService : IGitSyncService
{
    private readonly IGitCliRunner _gitCliRunner;
    private readonly IGitWorkspaceManager _gitWorkspaceManager;
    private readonly IGitMirrorSyncService _mirrorSyncService;
    private readonly ITaskRepository _taskRepository;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IWslMetadataService _wslMetadataService;
    private readonly IExecutionEnvironmentProvider _environmentProvider;
    private readonly ILogger<GitSyncService> _logger;

    private static readonly ConcurrentDictionary<int, DateTime> LastFetchedTimes = new();
    private static readonly Regex TaskIdRegex = new(@"task-(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public GitSyncService(
        IGitCliRunner gitCliRunner,
        IGitWorkspaceManager gitWorkspaceManager,
        IGitMirrorSyncService mirrorSyncService,
        ITaskRepository taskRepository,
        IWorkspacePathResolver pathResolver,
        IWslMetadataService wslMetadataService,
        IExecutionEnvironmentProvider environmentProvider,
        ILogger<GitSyncService> logger)
    {
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        _gitWorkspaceManager = gitWorkspaceManager ?? throw new ArgumentNullException(nameof(gitWorkspaceManager));
        _mirrorSyncService = mirrorSyncService ?? throw new ArgumentNullException(nameof(mirrorSyncService));
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _wslMetadataService = wslMetadataService ?? throw new ArgumentNullException(nameof(wslMetadataService));
        _environmentProvider = environmentProvider ?? throw new ArgumentNullException(nameof(environmentProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private bool IsDirectoryAccessible(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (Directory.Exists(path))
        {
            return true;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            (path.StartsWith('/') || path.StartsWith('~') || WslPathConverter.IsWslUncPath(path)))
        {
            var distro = _wslMetadataService.GetDefaultDistributionName();
            var user = _wslMetadataService.GetDefaultUserName();
            var uncPath = WslPathConverter.ToWslUncPath(path, distro, user);
            return Directory.Exists(uncPath);
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<MirrorStatusSnapshot> GetMirrorStatusAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var mirrorPath = workspace.Ext4MirrorPath ?? string.Empty;
        var hostOriginPath = workspace.RepositoryPath ?? string.Empty;
        var baseBranch = string.IsNullOrWhiteSpace(workspace.DefaultBaseBranch) ? "main" : workspace.DefaultBaseBranch;
        var commitsAhead = 0;
        var commitsBehind = 0;
        var isHostDirty = false;

        // 1. Inspect host repository status (is dirty?)
        if (!string.IsNullOrWhiteSpace(workspace.RepositoryPath) && Directory.Exists(workspace.RepositoryPath))
        {
            try
            {
                var statusResult = await _gitCliRunner.ExecuteAsync(
                    workspace.RepositoryPath,
                    ["status", "--porcelain"],
                    cancellationToken: cancellationToken);

                isHostDirty = !string.IsNullOrWhiteSpace(statusResult.StandardOutput);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to check host dirty status in '{Path}'", workspace.RepositoryPath);
            }
        }

        // 2. Inspect mirror repository
        var inspectPath = !string.IsNullOrWhiteSpace(mirrorPath) && IsDirectoryAccessible(mirrorPath)
            ? mirrorPath
            : workspace.RepositoryPath;

        if (!string.IsNullOrWhiteSpace(inspectPath) && IsDirectoryAccessible(inspectPath))
        {
            // First check what remotes exist safely using `git remote`
            var targetRemote = string.Empty;
            try
            {
                var remotesResult = await _gitCliRunner.ExecuteAsync(
                    inspectPath,
                    ["remote"],
                    cancellationToken: cancellationToken);

                var remotes = remotesResult.StandardOutput
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (remotes.Contains("host-origin", StringComparer.OrdinalIgnoreCase))
                {
                    targetRemote = "host-origin";
                }
                else if (remotes.Contains("origin", StringComparer.OrdinalIgnoreCase))
                {
                    targetRemote = "origin";
                }
            }
            catch (GitCommandException ex)
            {
                _logger.LogDebug(ex, "Failed to query git remotes in '{InspectPath}'", inspectPath);
            }

            if (!string.IsNullOrEmpty(targetRemote))
            {
                // Try resolving target remote URL
                try
                {
                    var remoteResult = await _gitCliRunner.ExecuteAsync(
                        inspectPath,
                        ["remote", "get-url", targetRemote],
                        cancellationToken: cancellationToken);

                    var resolvedRemote = remoteResult.StandardOutput.Trim();
                    if (!string.IsNullOrWhiteSpace(resolvedRemote))
                    {
                        hostOriginPath = resolvedRemote;
                    }
                }
                catch (GitCommandException ex)
                {
                    _logger.LogDebug(ex, "Failed to resolve remote url for '{TargetRemote}' in '{InspectPath}'", targetRemote, inspectPath);
                }

                // Check if the remote tracking branch actually exists before rev-list
                var remoteBranchRef = $"{targetRemote}/{baseBranch}";
                var branchExists = false;
                try
                {
                    var branchCheck = await _gitCliRunner.ExecuteAsync(
                        inspectPath,
                        ["branch", "-r", "--list", remoteBranchRef],
                        cancellationToken: cancellationToken);

                    branchExists = branchCheck.StandardOutput
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Any(b => b.Trim().Equals(remoteBranchRef, StringComparison.OrdinalIgnoreCase));
                }
                catch (GitCommandException ex)
                {
                    _logger.LogDebug(ex, "Failed to check if remote branch '{RemoteBranchRef}' exists in '{InspectPath}'", remoteBranchRef, inspectPath);
                }

                if (branchExists)
                {
                    // Check divergence: commits ahead of remote tracking branch
                    try
                    {
                        var aheadResult = await _gitCliRunner.ExecuteAsync(
                            inspectPath,
                            ["rev-list", "--count", $"{remoteBranchRef}..HEAD"],
                            cancellationToken: cancellationToken);

                        if (int.TryParse(aheadResult.StandardOutput.Trim(), out var ahead))
                        {
                            commitsAhead = ahead;
                        }
                    }
                    catch (GitCommandException ex)
                    {
                        _logger.LogDebug(ex, "Failed to query commits ahead of '{RemoteBranchRef}' in '{InspectPath}'", remoteBranchRef, inspectPath);
                    }

                    // Check divergence: commits behind remote tracking branch
                    try
                    {
                        var behindResult = await _gitCliRunner.ExecuteAsync(
                            inspectPath,
                            ["rev-list", "--count", $"HEAD..{remoteBranchRef}"],
                            cancellationToken: cancellationToken);

                        if (int.TryParse(behindResult.StandardOutput.Trim(), out var behind))
                        {
                            commitsBehind = behind;
                        }
                    }
                    catch (GitCommandException ex)
                    {
                        _logger.LogDebug(ex, "Failed to query commits behind '{RemoteBranchRef}' in '{InspectPath}'", remoteBranchRef, inspectPath);
                    }
                }
            }
        }

        LastFetchedTimes.TryGetValue(workspace.Id, out var lastFetchedAt);

        return new MirrorStatusSnapshot(
            WorkspaceId: workspace.Id,
            MirrorPath: mirrorPath,
            HostOriginPath: hostOriginPath,
            BaseBranch: baseBranch,
            CommitsAhead: commitsAhead,
            CommitsBehind: commitsBehind,
            LastFetchedAt: lastFetchedAt == default ? null : lastFetchedAt,
            IsHostDirty: isHostDirty,
            HostRepoPath: workspace.RepositoryPath ?? string.Empty);
    }

    /// <inheritdoc />
    public async Task<List<WorktreeSnapshot>> GetWorktreesAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var targetPath = !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath) && IsDirectoryAccessible(workspace.Ext4MirrorPath)
            ? workspace.Ext4MirrorPath
            : workspace.RepositoryPath;

        var snapshots = new List<WorktreeSnapshot>();
        if (string.IsNullOrWhiteSpace(targetPath) || !IsDirectoryAccessible(targetPath))
        {
            return snapshots;
        }

        try
        {
            var result = await _gitCliRunner.ExecuteAsync(
                targetPath,
                ["worktree", "list", "--porcelain"],
                cancellationToken: cancellationToken);

            var lines = result.StandardOutput.Split('\n');
            string currentWorktreePath = string.Empty;
            string currentBranch = string.Empty;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith("worktree ", StringComparison.OrdinalIgnoreCase))
                {
                    currentWorktreePath = line[9..].Trim();
                }
                else if (line.StartsWith("branch ", StringComparison.OrdinalIgnoreCase))
                {
                    currentBranch = line[7..].Trim().Replace("refs/heads/", "");
                }
                else if (string.IsNullOrEmpty(line) && !string.IsNullOrEmpty(currentWorktreePath))
                {
                    // End of entry
                    AddParsedWorktree(snapshots, currentWorktreePath, currentBranch, workspace);
                    currentWorktreePath = string.Empty;
                    currentBranch = string.Empty;
                }
            }

            if (!string.IsNullOrEmpty(currentWorktreePath))
            {
                AddParsedWorktree(snapshots, currentWorktreePath, currentBranch, workspace);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list git worktrees in '{TargetPath}'", targetPath);
        }

        // Fetch task states from repository to mark active/idle accurately
        try
        {
            var tasks = await _taskRepository.GetByWorkspaceIdAsync(workspace.Id, cancellationToken);
            var taskDict = tasks.ToDictionary(t => t.Id);

                for (var i = 0; i < snapshots.Count; i++)
                {
                    var wt = snapshots[i];
                    if (taskDict.TryGetValue(wt.TaskId, out var task))
                    {
                        var isActive = task.Status != TaskOverallStatus.Closed &&
                                       task.Status != TaskOverallStatus.Failed &&
                                       task.Status != TaskOverallStatus.Cancelled &&
                                       task.Status != TaskOverallStatus.Rejected;

                        snapshots[i] = wt with { IsActive = isActive };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not correlate worktrees with task repository status");
            }

        return snapshots;
    }

    private void AddParsedWorktree(List<WorktreeSnapshot> snapshots, string path, string branch, Workspace workspace)
    {
        var match = TaskIdRegex.Match(branch);
        if (!match.Success)
        {
            match = TaskIdRegex.Match(path);
        }

        var taskId = match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : 0;
        if (taskId > 0)
        {
            snapshots.Add(new WorktreeSnapshot(
                Path: path,
                TaskId: taskId,
                BranchName: string.IsNullOrWhiteSpace(branch) ? $"feature/task-{taskId}" : branch,
                IsActive: true,
                CreatedAt: DateTime.UtcNow));
        }
    }

    /// <inheritdoc />
    public async Task PruneWorktreesAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var targetPath = !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath) && IsDirectoryAccessible(workspace.Ext4MirrorPath)
            ? workspace.Ext4MirrorPath
            : workspace.RepositoryPath;

        if (string.IsNullOrWhiteSpace(targetPath) || !IsDirectoryAccessible(targetPath))
        {
            return;
        }

        _logger.LogInformation("Pruning stale Git worktrees for workspace #{WorkspaceId}", workspace.Id);

        try
        {
            await _gitCliRunner.ExecuteAsync(targetPath, ["worktree", "prune"], cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed executing git worktree prune on '{TargetPath}'", targetPath);
        }

        // Reap sandbox containers of closed / rejected / deleted tasks
        try
        {
            await TaskContainerReaper.ReapAsync(_environmentProvider, _taskRepository, workspace, _logger, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to reap orphaned task containers for workspace #{WorkspaceId}", workspace.Id);
        }

        // Clean up inactive/done task worktrees
        try
        {
            var worktrees = await GetWorktreesAsync(workspace, cancellationToken);
            var tasks = await _taskRepository.GetByWorkspaceIdAsync(workspace.Id, cancellationToken);
            var taskDict = tasks.ToDictionary(t => t.Id);

                foreach (var wt in worktrees)
                {
                    if (taskDict.TryGetValue(wt.TaskId, out var task))
                    {
                        if (task.Status == TaskOverallStatus.Closed ||
                            task.Status == TaskOverallStatus.Failed ||
                            task.Status == TaskOverallStatus.Cancelled ||
                            task.Status == TaskOverallStatus.Rejected)
                        {
                            _logger.LogInformation("Removing inactive worktree for completed task #{TaskId}", wt.TaskId);
                            await _gitWorkspaceManager.RemoveWorktreeAsync(workspace, wt.TaskId, cancellationToken);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prune inactive task worktrees");
            }
    }

    /// <inheritdoc />
    public async Task<GitSyncResult> ReconcileHostBranchAsync(
        Workspace workspace,
        int taskId,
        string branchName,
        string strategy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        var hostRepo = workspace.RepositoryPath;
        if (string.IsNullOrWhiteSpace(hostRepo) || !Directory.Exists(hostRepo))
        {
            throw new DirectoryNotFoundException($"Host repository path '{hostRepo}' does not exist.");
        }

        var rescueBranch = $"{branchName}-ncf";
        _logger.LogInformation("Reconciling host repository branch '{BranchName}' with strategy '{Strategy}'",
            branchName, strategy);

        try
        {
            switch (strategy.ToLowerInvariant())
            {
                case "stash-and-merge":
                case "fast-forward":
                    // 1. Stash host uncommitted changes
                    await _gitCliRunner.ExecuteAsync(
                        hostRepo,
                        ["stash", "push", "-m", $"NightCodeForge auto-stash before reconcile #{taskId}"],
                        cancellationToken: cancellationToken);

                    // 2. Checkout the branch
                    await _gitCliRunner.ExecuteAsync(
                        hostRepo,
                        ["checkout", branchName],
                        cancellationToken: cancellationToken);

                    // 3. Merge or fast-forward the rescue branch
                    await _gitCliRunner.ExecuteAsync(
                        hostRepo,
                        ["merge", rescueBranch],
                        cancellationToken: cancellationToken);

                    return new GitSyncResult(
                        Success: true,
                        FallbackUsed: false,
                        BranchPushed: branchName,
                        WarningMessage: null);

                case "force-sync":
                    // Force checkout and reset
                    await _gitCliRunner.ExecuteAsync(
                        hostRepo,
                        ["checkout", branchName],
                        cancellationToken: cancellationToken);

                    await _gitCliRunner.ExecuteAsync(
                        hostRepo,
                        ["reset", "--hard", rescueBranch],
                        cancellationToken: cancellationToken);

                    return new GitSyncResult(
                        Success: true,
                        FallbackUsed: false,
                        BranchPushed: branchName,
                        WarningMessage: null);

                case "dismiss":
                default:
                    return new GitSyncResult(
                        Success: true,
                        FallbackUsed: false,
                        BranchPushed: branchName,
                        WarningMessage: null);
            }
        }
        catch (GitCommandException gex)
        {
            _logger.LogError(gex, "Reconcile command failed for branch '{BranchName}'", branchName);
            return new GitSyncResult(
                Success: false,
                FallbackUsed: true,
                BranchPushed: rescueBranch,
                WarningMessage: gex.StandardError);
        }
    }

    /// <inheritdoc />
    public void RecordFetch(int workspaceId)
    {
        LastFetchedTimes[workspaceId] = DateTime.UtcNow;
    }
}
