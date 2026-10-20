using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
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
/// Orchestrates immediate bidirectional synchronization between database tasks and repository markdown files in docs/tasks/*.md.
/// First reads pending edits and new files from disk, then writes back the canonical state.
/// </summary>
public class TaskDiskSyncCoordinator : ITaskDiskSyncCoordinator
{
    private readonly ITaskMarkdownSyncService _markdownSyncService;
    private readonly ILogger<TaskDiskSyncCoordinator> _logger;

    public TaskDiskSyncCoordinator(
        ITaskMarkdownSyncService markdownSyncService,
        ILogger<TaskDiskSyncCoordinator> logger)
    {
        _markdownSyncService = markdownSyncService ?? throw new ArgumentNullException(nameof(markdownSyncService));
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

        // 1. Ingest any new task files or edited markdown specs from docs/tasks/
        var fromDiskTasks = await _markdownSyncService.SyncAllTasksFromDiskAsync(
            workspace,
            workspace.RepositoryPath,
            workspace.RepositoryPath,
            baselineSnapshot: null,
            changeActor: AuditActors.User,
            changeReason: TaskSyncConstants.DiskModificationsSync,
            cancellationToken: cancellationToken);

        // 2. Mirror all active and closed tasks from database back to docs/tasks/ to ensure parity
        var toDiskResult = await _markdownSyncService.SyncAllTasksToDiskAsync(
            workspace,
            workspace.RepositoryPath,
            workspace.RepositoryPath,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Completed docs/tasks sync for workspace #{WorkspaceId}: {IngestedCount} processed from disk, {WrittenCount} mirrored to disk.",
            workspace.Id, fromDiskTasks.Count, toDiskResult.WrittenPaths.Count);

        return new TaskDiskSyncResult(
            TasksIngestedOrUpdatedFromDisk: fromDiskTasks.Count,
            TasksWrittenToDisk: toDiskResult.WrittenPaths.Count,
            RefreshedTasks: fromDiskTasks,
            WrittenPaths: toDiskResult.WrittenPaths);
    }
}
