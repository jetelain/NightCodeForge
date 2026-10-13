using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Service contract for bidirectional synchronization between database issues and disk markdown files.
/// </summary>
public interface IIssueMarkdownSyncService : IDisposable
{
    string GenerateMarkdown(Issue issue, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null);
    (IssueFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent);
    string ComputeContentHash(string content);
    Task<string> SyncIssueToDiskAsync(Issue issue, string workspacePath, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null, CancellationToken cancellationToken = default);
    Task ProcessFileContentAsync(string filePath, string content, int workspaceId, CancellationToken cancellationToken = default);
    Task ProcessFileChangedAsync(string filePath, int workspaceId, CancellationToken cancellationToken = default);
    Task ArchiveOrCleanupIssueFileAsync(Issue issue, string workspacePath, bool archive = true, CancellationToken cancellationToken = default);
    void StartWatching(int workspaceId, string workspacePath);
    void StopWatching(int workspaceId);
}
