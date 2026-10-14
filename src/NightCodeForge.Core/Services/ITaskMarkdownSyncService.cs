using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Service contract for bidirectional synchronization between database tasks and disk markdown files.
/// </summary>
public interface ITaskMarkdownSyncService : IDisposable
{
    string GenerateMarkdown(ForgeTask task, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null);
    (TaskFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent);
    string ComputeContentHash(string content);
    void SuppressContentHash(string content) { }
    Task<string> SyncTaskToDiskAsync(ForgeTask task, string workspacePath, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null, CancellationToken cancellationToken = default);
    Task ProcessFileContentAsync(string filePath, string content, int workspaceId, CancellationToken cancellationToken = default);
    Task ProcessFileContentAsync(string filePath, string content, int workspaceId, int? expectedTaskId, CancellationToken cancellationToken = default)
        => ProcessFileContentAsync(filePath, content, workspaceId, cancellationToken);
    Task ProcessFileChangedAsync(string filePath, int workspaceId, CancellationToken cancellationToken = default);
    Task ProcessFileChangedAsync(string filePath, int workspaceId, int? expectedTaskId, CancellationToken cancellationToken = default)
        => ProcessFileChangedAsync(filePath, workspaceId, cancellationToken);
    Task ArchiveOrCleanupTaskFileAsync(ForgeTask task, string workspacePath, bool archive = true, CancellationToken cancellationToken = default);
    void StartWatching(int workspaceId, string workspacePath);
    void StopWatching(int workspaceId);
}
