using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Encapsulates the summary result of synchronizing database tasks with docs/tasks/*.md files.
/// </summary>
public record TaskDiskSyncResult(
    int TasksIngestedOrUpdatedFromDisk,
    int TasksWrittenToDisk,
    IReadOnlyList<ForgeTask> RefreshedTasks,
    IReadOnlyDictionary<int, string> WrittenPaths);

/// <summary>
/// Service contract for orchestrating immediate bidirectional synchronization between SQLite database
/// tasks and disk markdown files in docs/tasks/*.
/// </summary>
public interface ITaskDiskSyncCoordinator
{
    Task<TaskDiskSyncResult> SyncWorkspaceDocsTasksAsync(Workspace workspace, CancellationToken cancellationToken = default);
}

/// <summary>
/// Orchestrates immediate bidirectional synchronization between database tasks and repository markdown files in docs/tasks/*.md,
/// keeping both host repository and agent clone/docs/tasks in sync.
/// First reads pending edits and new files from disk, then writes back the canonical state.
/// </summary>
public class TaskDiskSyncCoordinator : ITaskDiskSyncCoordinator
{
    private readonly ITaskMarkdownSyncService _markdownSyncService;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly ILogger<TaskDiskSyncCoordinator> _logger;

    public TaskDiskSyncCoordinator(
        ITaskMarkdownSyncService markdownSyncService,
        IWorkspacePathResolver pathResolver,
        ILogger<TaskDiskSyncCoordinator> logger)
    {
        _markdownSyncService = markdownSyncService ?? throw new ArgumentNullException(nameof(markdownSyncService));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TaskDiskSyncResult> SyncWorkspaceDocsTasksAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (string.IsNullOrWhiteSpace(workspace.RepositoryPath) || !Directory.Exists(workspace.RepositoryPath))
        {
            throw new DirectoryNotFoundException($"Workspace repository path does not exist: '{workspace.RepositoryPath}'.");
        }

        _logger.LogInformation(
            "Initiating immediate docs/tasks sync for workspace #{WorkspaceId} ({WorkspaceName}) at '{RepoPath}'",
            workspace.Id, workspace.Name, workspace.RepositoryPath);

        // 1. Resolve host clone path if available (read-only mirror target for agent exploration)
        string? hostCloneDir = null;
        if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            var p = _pathResolver.ToHostPath(workspace.Ext4MirrorPath);
            if (Directory.Exists(p)) hostCloneDir = p;
        }
        if (hostCloneDir == null && workspace.Id > 0)
        {
            try
            {
                var canonicalClone = _pathResolver.GetAgentWorkingClonePath(workspace.Id);
                if (!string.IsNullOrWhiteSpace(canonicalClone))
                {
                    var p = _pathResolver.ToHostPath(canonicalClone);
                    if (Directory.Exists(p)) hostCloneDir = p;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve working clone directory for workspace #{WorkspaceId}", workspace.Id);
            }
        }

        // 2. Ingest any new task files or edited markdown specs from host docs/tasks/
        var fromDiskTasks = await _markdownSyncService.SyncAllTasksFromDiskAsync(
            workspace,
            workspace.RepositoryPath,
            workspace.RepositoryPath,
            baselineSnapshot: null,
            changeActor: AuditActors.User,
            changeReason: TaskSyncConstants.DiskModificationsSync,
            cancellationToken: cancellationToken);

        // 3. Mirror all active and closed tasks from database back to docs/tasks/ (both repo and clone)
        var toDiskResult = await _markdownSyncService.SyncAllTasksToDiskAsync(
            workspace,
            workspace.RepositoryPath,
            workspace.RepositoryPath,
            cloneDirectory: hostCloneDir,
            cancellationToken: cancellationToken);

        var writtenPaths = toDiskResult?.WrittenPaths ?? new Dictionary<int, string>();
        var writtenCount = writtenPaths.Count;

        _logger.LogInformation(
            "Completed docs/tasks sync for workspace #{WorkspaceId}: {IngestedCount} processed from disk, {WrittenCount} mirrored to disk (clone: {ClonePath}).",
            workspace.Id, fromDiskTasks.Count, writtenCount, hostCloneDir ?? "none");

        return new TaskDiskSyncResult(
            TasksIngestedOrUpdatedFromDisk: fromDiskTasks.Count,
            TasksWrittenToDisk: writtenCount,
            RefreshedTasks: fromDiskTasks,
            WrittenPaths: writtenPaths);
    }
}
