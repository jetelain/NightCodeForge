using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Implements bidirectional mirroring of active issues to docs/issues/*.md with debouncing and echo loop suppression.
/// </summary>
public partial class IssueMarkdownSyncService : IIssueMarkdownSyncService
{
    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphaNumericRegex();

    private readonly IIssueRepository _issueRepository;
    private readonly IDependencyResolutionEngine? _dependencyEngine;
    private readonly IDeserializer _yamlDeserializer;
    private readonly ISerializer _yamlSerializer;
    private readonly ConcurrentDictionary<string, byte> _suppressedHashes = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounceTokens = new();
    private readonly ConcurrentDictionary<int, FileSystemWatcher> _watchers = new();
    private readonly TimeSpan _debounceWindow = TimeSpan.FromMilliseconds(300);

    public IssueMarkdownSyncService(IIssueRepository issueRepository, IDependencyResolutionEngine? dependencyEngine = null)
    {
        _issueRepository = issueRepository ?? throw new ArgumentNullException(nameof(issueRepository));
        _dependencyEngine = dependencyEngine;

        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _yamlSerializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
    }

    public string GenerateMarkdown(Issue issue, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null)
    {
        var frontmatter = new IssueFrontmatter
        {
            Id = issue.Id > 0 ? issue.Id : null,
            Title = issue.Title,
            Workspace = issue.Workspace?.Name,
            Status = issue.Status.ToString(),
            CurrentStage = issue.CurrentStageId,
            Priority = issue.Priority,
            MergeMode = issue.MergeMode == Domain.MergeMode.Automatic ? "automatic" : "manual_review_required",
            WaitingForHuman = issue.IsWaitingForHuman,
            BaseBranch = issue.BaseBranch,
            FeatureBranch = string.IsNullOrWhiteSpace(issue.FeatureBranch) ? null : issue.FeatureBranch,
            DependsOn = dependencies != null ? dependencies.ToList() : issue.Dependencies?.Select(d => d.DependsOnIssueId).ToList() ?? new List<int>(),
            AgentProfile = agentProfileId,
            CreatedAt = issue.CreatedAt,
            UpdatedAt = issue.UpdatedAt
        };

        var yaml = _yamlSerializer.Serialize(frontmatter);
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.Append(yaml);
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {issue.Title}");
        sb.AppendLine();
        sb.AppendLine("## 📋 Requirements & Description");
        sb.AppendLine(issue.Description);

        if (issue.Questions != null && issue.Questions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## ❓ Human Clarifications (Q&A)");
            int qIndex = 1;
            foreach (var q in issue.Questions)
            {
                sb.AppendLine($"### Question {qIndex++} (Asked during Stage: {q.StageId} on {q.AskedAt:yyyy-MM-dd HH:mm:ss})");
                sb.AppendLine($"**Prompt:** {q.Prompt}");
                sb.AppendLine($"**Answer:** {q.Answer ?? "_Awaiting response_"}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    public (IssueFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent)
    {
        if (string.IsNullOrWhiteSpace(markdownContent))
        {
            return (new IssueFrontmatter(), string.Empty);
        }

        var normalized = markdownContent.Replace("\r\n", "\n");
        if (normalized.StartsWith("---"))
        {
            var secondFence = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (secondFence != -1)
            {
                var yamlPart = normalized.Substring(3, secondFence - 3).Trim();
                var bodyPart = normalized.Substring(secondFence + 4).TrimStart('\r', '\n');

                try
                {
                    var frontmatter = _yamlDeserializer.Deserialize<IssueFrontmatter>(yamlPart) ?? new IssueFrontmatter();
                    return (frontmatter, bodyPart);
                }
                catch
                {
                    // If frontmatter deserialization fails, return raw body
                    return (new IssueFrontmatter(), normalized);
                }
            }
        }

        return (new IssueFrontmatter(), markdownContent);
    }

    public string ComputeContentHash(string content)
    {
        var normalized = content.Replace("\r\n", "\n");
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public static string GenerateSlug(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "issue";
        var slug = NonAlphaNumericRegex().Replace(title.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "issue" : slug;
    }

    public async Task<string> SyncIssueToDiskAsync(
        Issue issue,
        string workspacePath,
        IReadOnlyList<int>? dependencies = null,
        string? agentProfileId = null,
        CancellationToken cancellationToken = default)
    {
        var issuesDir = Path.Combine(workspacePath, "docs", "issues");
        Directory.CreateDirectory(issuesDir);

        var datePrefix = (issue.CreatedAt != default ? issue.CreatedAt : DateTime.UtcNow).ToString("yyyy-MM-dd");
        var slug = GenerateSlug(issue.Title);

        // Find or allocate target file path
        var targetFile = Path.Combine(issuesDir, $"{datePrefix}-{slug}.md");
        int suffix = 2;

        while (File.Exists(targetFile))
        {
            try
            {
                var existingContent = await File.ReadAllTextAsync(targetFile, cancellationToken);
                var (existingFm, _) = ParseMarkdown(existingContent);
                if (existingFm.Id == issue.Id)
                {
                    break; // Overwrite this issue's existing file
                }
            }
            catch
            {
                // Unreadable file, advance suffix
            }

            targetFile = Path.Combine(issuesDir, $"{datePrefix}-{slug}-{suffix++}.md");
        }

        var markdown = GenerateMarkdown(issue, dependencies, agentProfileId);
        var hash = ComputeContentHash(markdown);
        _suppressedHashes[hash] = 1;

        await File.WriteAllTextAsync(targetFile, markdown, Encoding.UTF8, cancellationToken);
        return targetFile;
    }

    public async Task ProcessFileChangedAsync(string filePath, int workspaceId, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) return;

        string content;
        try
        {
            content = await File.ReadAllTextAsync(filePath, cancellationToken);
        }
        catch
        {
            return;
        }

        await ProcessFileContentAsync(filePath, content, workspaceId, cancellationToken);
    }

    public async Task ProcessFileContentAsync(string filePath, string content, int workspaceId, CancellationToken cancellationToken = default)
    {
        var hash = ComputeContentHash(content);
        if (_suppressedHashes.TryRemove(hash, out _))
        {
            // Self-triggering echo loop suppressed
            return;
        }

        var (frontmatter, body) = ParseMarkdown(content);

        // Extract description from body (strip '# Title' and '## Requirements' if present)
        var description = ExtractDescriptionFromBody(body);

        if (frontmatter.Id.HasValue && frontmatter.Id.Value > 0)
        {
            var issue = await _issueRepository.GetByIdAsync(frontmatter.Id.Value, cancellationToken);
            if (issue != null)
            {
                bool restoreProtected = false;

                // Validate system-managed fields
                if (!string.Equals(frontmatter.Status, issue.Status.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    restoreProtected = true;
                    frontmatter.Status = issue.Status.ToString();
                }

                if (!string.Equals(frontmatter.CurrentStage, issue.CurrentStageId, StringComparison.OrdinalIgnoreCase))
                {
                    restoreProtected = true;
                    frontmatter.CurrentStage = issue.CurrentStageId;
                }

                if (frontmatter.WaitingForHuman != issue.IsWaitingForHuman)
                {
                    restoreProtected = true;
                    frontmatter.WaitingForHuman = issue.IsWaitingForHuman;
                }

                // Update editable fields
                if (!string.IsNullOrWhiteSpace(frontmatter.Title))
                {
                    issue.Title = frontmatter.Title;
                }
                if (!string.IsNullOrWhiteSpace(description))
                {
                    issue.Description = description;
                }
                issue.Priority = frontmatter.Priority;
                issue.BaseBranch = frontmatter.BaseBranch;
                if (!string.IsNullOrWhiteSpace(frontmatter.FeatureBranch))
                {
                    issue.FeatureBranch = frontmatter.FeatureBranch;
                }
                issue.UpdatedAt = DateTime.UtcNow;

                // Validate DAG cycles on dependency changes if engine is provided
                if (_dependencyEngine != null && frontmatter.DependsOn != null)
                {
                    var testDependencies = frontmatter.DependsOn.Select(d => (issue.Id, d));
                    if (_dependencyEngine.HasCycle(workspaceId, testDependencies))
                    {
                        // Cycle detected, revert dependencies in frontmatter
                        restoreProtected = true;
                        var existingDeps = await _issueRepository.GetDependenciesAsync(issue.Id, cancellationToken);
                        frontmatter.DependsOn = existingDeps.ToList();
                    }
                    else
                    {
                        await _issueRepository.UpdateAsync(issue, frontmatter.DependsOn, cancellationToken);
                    }
                }
                else
                {
                    await _issueRepository.UpdateAsync(issue, frontmatter.DependsOn, cancellationToken);
                }

                if (restoreProtected)
                {
                    // Rewrite back authoritative state
                    var rewritten = GenerateMarkdown(issue, frontmatter.DependsOn, frontmatter.AgentProfile);
                    var newHash = ComputeContentHash(rewritten);
                    _suppressedHashes[newHash] = 1;
                    if (File.Exists(filePath))
                    {
                        await File.WriteAllTextAsync(filePath, rewritten, Encoding.UTF8, cancellationToken);
                    }
                }

                return;
            }
        }

        // New issue dropped in without an ID
        var mergeMode = string.Equals(frontmatter.MergeMode, "automatic", StringComparison.OrdinalIgnoreCase)
            ? Domain.MergeMode.Automatic
            : Domain.MergeMode.ManualReviewRequired;

        var newIssue = new Issue
        {
            WorkspaceId = workspaceId,
            Title = string.IsNullOrWhiteSpace(frontmatter.Title) ? Path.GetFileNameWithoutExtension(filePath) : frontmatter.Title,
            Description = string.IsNullOrWhiteSpace(description) ? body : description,
            Priority = frontmatter.Priority,
            MergeMode = mergeMode,
            IsWaitingForHuman = frontmatter.WaitingForHuman,
            BaseBranch = string.IsNullOrWhiteSpace(frontmatter.BaseBranch) ? "main" : frontmatter.BaseBranch,
            Status = IssueOverallStatus.Suggested,
            CurrentStageId = "issue-review",
            CreatedAt = frontmatter.CreatedAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await _issueRepository.CreateAsync(newIssue, frontmatter.DependsOn, cancellationToken);

        // Update frontmatter in-place with allocated ID and creation timestamp
        frontmatter.Id = created.Id;
        frontmatter.CreatedAt = created.CreatedAt;
        frontmatter.Status = created.Status.ToString();
        frontmatter.MergeMode = created.MergeMode == Domain.MergeMode.Automatic ? "automatic" : "manual_review_required";
        frontmatter.WaitingForHuman = created.IsWaitingForHuman;
        frontmatter.CurrentStage = created.CurrentStageId;

        var inPlaceMarkdown = GenerateMarkdown(created, frontmatter.DependsOn, frontmatter.AgentProfile);
        var inPlaceHash = ComputeContentHash(inPlaceMarkdown);
        _suppressedHashes[inPlaceHash] = 1;

        if (File.Exists(filePath))
        {
            await File.WriteAllTextAsync(filePath, inPlaceMarkdown, Encoding.UTF8, cancellationToken);
        }
    }

    public async Task ArchiveOrCleanupIssueFileAsync(Issue issue, string workspacePath, bool archive = true, CancellationToken cancellationToken = default)
    {
        var issuesDir = Path.Combine(workspacePath, "docs", "issues");
        if (!Directory.Exists(issuesDir)) return;

        var files = Directory.GetFiles(issuesDir, "*.md");
        foreach (var file in files)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file, cancellationToken);
                var (fm, _) = ParseMarkdown(content);
                if (fm.Id == issue.Id)
                {
                    if (archive)
                    {
                        var archiveDir = Path.Combine(issuesDir, "archive");
                        Directory.CreateDirectory(archiveDir);
                        var target = Path.Combine(archiveDir, Path.GetFileName(file));
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(file, target);
                    }
                    else
                    {
                        File.Delete(file);
                    }
                    break;
                }
            }
            catch
            {
                // Continue scanning
            }
        }
    }

    public void StartWatching(int workspaceId, string workspacePath)
    {
        var issuesDir = Path.Combine(workspacePath, "docs", "issues");
        Directory.CreateDirectory(issuesDir);

        if (_watchers.ContainsKey(workspaceId)) return;

        var watcher = new FileSystemWatcher(issuesDir, "*.md")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        watcher.Changed += (_, e) => OnFileEvent(e.FullPath, workspaceId);
        watcher.Created += (_, e) => OnFileEvent(e.FullPath, workspaceId);
        watcher.EnableRaisingEvents = true;

        _watchers[workspaceId] = watcher;
    }

    public void StopWatching(int workspaceId)
    {
        if (_watchers.TryRemove(workspaceId, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    private void OnFileEvent(string fullPath, int workspaceId)
    {
        // Coalesce rapid disk I/O bursts within 300ms window
        if (_debounceTokens.TryGetValue(fullPath, out var existingCts))
        {
            existingCts.Cancel();
            existingCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _debounceTokens[fullPath] = cts;

        Task.Delay(_debounceWindow, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            _debounceTokens.TryRemove(fullPath, out _);
            try
            {
                await ProcessFileChangedAsync(fullPath, workspaceId);
            }
            catch
            {
                // Ignore transient I/O conflicts during watcher dispatch
            }
        }, TaskScheduler.Default);
    }

    private static string ExtractDescriptionFromBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        var lines = body.Split('\n');
        var descLines = new List<string>();
        bool inRequirements = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("## 📋 Requirements & Description", StringComparison.OrdinalIgnoreCase))
            {
                inRequirements = true;
                continue;
            }
            if (inRequirements && line.StartsWith("## "))
            {
                break;
            }
            if (inRequirements)
            {
                descLines.Add(line);
            }
        }

        if (descLines.Count > 0)
        {
            return string.Join("\n", descLines).Trim();
        }

        return body.Trim();
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        foreach (var cts in _debounceTokens.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _debounceTokens.Clear();
    }
}
