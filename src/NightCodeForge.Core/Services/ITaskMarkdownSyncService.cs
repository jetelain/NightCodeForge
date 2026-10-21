using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Service contract for bidirectional synchronization between database tasks and disk markdown files.
/// </summary>
public interface ITaskMarkdownSyncService : IDisposable
{
    string GenerateMarkdown(ForgeTask task, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null);
    (TaskFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent);
    (TaskFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent, string? filePath) => ParseMarkdown(markdownContent);
    string ComputeContentHash(string content);
    void SuppressContentHash(string content) { }
    Task<string> SyncTaskToDiskAsync(ForgeTask task, string workspacePath, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null, CancellationToken cancellationToken = default);
    Task<string> SyncTaskToDiskAsync(ForgeTask task, string workspacePath, IReadOnlyList<int>? dependencies, string? agentProfileId, string? cloneDirectory, CancellationToken cancellationToken = default)
        => SyncTaskToDiskAsync(task, workspacePath, dependencies, agentProfileId, cancellationToken);
    Task<SyncAllToDiskResult> SyncAllTasksToDiskAsync(Workspace workspace, string targetDirectory, string? hostRepositoryPath = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new SyncAllToDiskResult(new Dictionary<int, string>(), new Dictionary<int, TaskBaselineEntry>()));
    Task<SyncAllToDiskResult> SyncAllTasksToDiskAsync(Workspace workspace, string targetDirectory, string? hostRepositoryPath, string? cloneDirectory, CancellationToken cancellationToken = default)
        => SyncAllTasksToDiskAsync(workspace, targetDirectory, hostRepositoryPath, cancellationToken);
    string? ResolveCloneTasksDirectory(int workspaceId, Workspace? workspace = null, string? workspacePath = null) => null;
    Task<string?> ResolveCloneTasksDirectoryAsync(int workspaceId, Workspace? workspace = null, string? workspacePath = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    Task<IReadOnlyList<ForgeTask>> SyncAllTasksFromDiskAsync(Workspace workspace, string targetDirectory, string? hostRepositoryPath, IReadOnlyDictionary<int, TaskBaselineEntry>? baselineSnapshot, string changeActor, string? changeReason, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ForgeTask>>(Array.Empty<ForgeTask>());
    Task ProcessFileContentAsync(string filePath, string content, int workspaceId, CancellationToken cancellationToken = default);
    Task ProcessFileContentAsync(string filePath, string content, int workspaceId, int? expectedTaskId, CancellationToken cancellationToken = default)
        => ProcessFileContentAsync(filePath, content, workspaceId, cancellationToken);
    Task ProcessFileChangedAsync(string filePath, int workspaceId, CancellationToken cancellationToken = default);
    Task ProcessFileChangedAsync(string filePath, int workspaceId, int? expectedTaskId, CancellationToken cancellationToken = default)
        => ProcessFileChangedAsync(filePath, workspaceId, cancellationToken);
    Task ArchiveOrCleanupTaskFileAsync(ForgeTask task, string workspacePath, bool archive = true, CancellationToken cancellationToken = default);
    Task ArchiveOrCleanupTaskFileAsync(ForgeTask task, string workspacePath, bool archive, string? cloneDirectory, CancellationToken cancellationToken = default)
        => ArchiveOrCleanupTaskFileAsync(task, workspacePath, archive, cancellationToken);
    void StartWatching(int workspaceId, string workspacePath);
    void StopWatching(int workspaceId);
}
