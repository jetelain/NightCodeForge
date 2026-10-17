using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Implements bidirectional mirroring of active tasks to docs/tasks/*.md with debouncing and echo loop suppression.
/// </summary>
public partial class TaskMarkdownSyncService : ITaskMarkdownSyncService
{
    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphaNumericRegex();

    private readonly ITaskRepository? _fallbackTaskRepository;
    private readonly IDependencyResolutionEngine? _fallbackDependencyEngine;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IDeserializer _yamlDeserializer;
    private readonly ISerializer _yamlSerializer;
    private readonly ConcurrentDictionary<string, byte> _suppressedHashes = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounceTokens = new();
    private readonly ConcurrentDictionary<int, FileSystemWatcher> _watchers = new();
    private readonly TimeSpan _debounceWindow = TimeSpan.FromMilliseconds(300);

    [ActivatorUtilitiesConstructor]
    public TaskMarkdownSyncService(IServiceScopeFactory scopeFactory)
        : this(null, null, scopeFactory)
    {
    }

    public TaskMarkdownSyncService(ITaskRepository taskRepository, IDependencyResolutionEngine? dependencyEngine = null)
        : this(taskRepository, dependencyEngine, null)
    {
    }

    public TaskMarkdownSyncService(
        ITaskRepository? taskRepository,
        IDependencyResolutionEngine? dependencyEngine,
        IServiceScopeFactory? scopeFactory)
    {
        _fallbackTaskRepository = taskRepository;
        _fallbackDependencyEngine = dependencyEngine;
        _scopeFactory = scopeFactory;

        if (_fallbackTaskRepository == null && _scopeFactory == null)
        {
            throw new ArgumentException("Either taskRepository or scopeFactory must be provided.");
        }

        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _yamlSerializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
    }

    private (ITaskRepository TaskRepo, IDependencyResolutionEngine? DepEngine, IDisposable? Scope) ResolveDependencies()
    {
        if (_scopeFactory != null)
        {
            var scope = _scopeFactory.CreateScope();
            var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
            var depEngine = scope.ServiceProvider.GetService<IDependencyResolutionEngine>();
            return (taskRepo, depEngine, scope);
        }

        return (_fallbackTaskRepository!, _fallbackDependencyEngine, null);
    }

    public string GenerateMarkdown(ForgeTask task, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null)
    {
        var frontmatter = new TaskFrontmatter
        {
            Id = task.Id > 0 ? task.Id : null,
            Title = task.Title,
            Workspace = task.Workspace?.Name,
            Status = task.Status.ToString(),
            CurrentStage = task.CurrentStageId,
            Priority = task.Priority,
            MergeMode = task.MergeMode == Domain.MergeMode.Automatic ? "automatic" : "manual_review_required",
            WaitingForHuman = task.IsWaitingForHuman,
            BaseBranch = task.BaseBranch,
            FeatureBranch = string.IsNullOrWhiteSpace(task.FeatureBranch) ? null : task.FeatureBranch,
            DependsOn = dependencies != null ? dependencies.ToList() : task.Dependencies?.Select(d => d.DependsOnTaskId).ToList() ?? new List<int>(),
            AgentProfile = agentProfileId,
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt
        };

        var yaml = _yamlSerializer.Serialize(frontmatter).Replace("\r\n", "\n");
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append(yaml);
        if (!yaml.EndsWith('\n'))
        {
            sb.Append('\n');
        }
        sb.Append("---\n\n");
        sb.Append($"# {task.Title}\n\n");
        sb.Append("## 📋 Requirements & Description\n");
        sb.Append(task.Description).Append('\n');

        if (task.Questions != null && task.Questions.Count > 0)
        {
            sb.Append("\n## ❓ Human Clarifications (Q&A)\n");
            int qIndex = 1;
            foreach (var q in task.Questions)
            {
                sb.Append($"### Question {qIndex++} (Asked during Stage: {q.StageId} on {q.AskedAt:yyyy-MM-dd HH:mm:ss})\n");
                sb.Append($"**Prompt:** {q.Prompt}\n");
                sb.Append($"**Answer:** {q.Answer ?? "_Awaiting response_"}\n\n");
            }
        }

        return sb.ToString().Replace("\r\n", "\n");
    }

    public (TaskFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent)
    {
        if (string.IsNullOrWhiteSpace(markdownContent))
        {
            return (new TaskFrontmatter(), string.Empty);
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
                    var frontmatter = _yamlDeserializer.Deserialize<TaskFrontmatter>(yamlPart) ?? new TaskFrontmatter();
                    return (frontmatter, bodyPart);
                }
                catch
                {
                    // If frontmatter deserialization fails, return raw body
                    return (new TaskFrontmatter(), normalized);
                }
            }
        }

        return (new TaskFrontmatter(), markdownContent);
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
        if (string.IsNullOrWhiteSpace(title)) return "task";
        var slug = NonAlphaNumericRegex().Replace(title.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "task" : slug;
    }

    public async Task<string> SyncTaskToDiskAsync(
        ForgeTask task,
        string workspacePath,
        IReadOnlyList<int>? dependencies = null,
        string? agentProfileId = null,
        CancellationToken cancellationToken = default)
    {
        var tasksDir = Path.Combine(workspacePath, "docs", "tasks");
        Directory.CreateDirectory(tasksDir);

        var datePrefix = (task.CreatedAt != default ? task.CreatedAt : DateTime.UtcNow).ToString("yyyy-MM-dd");
        var slug = GenerateSlug(task.Title);

        // Find or allocate target file path
        var targetFile = Path.Combine(tasksDir, $"{datePrefix}-{slug}.md");
        int suffix = 2;

        while (File.Exists(targetFile))
        {
            try
            {
                var existingContent = await File.ReadAllTextAsync(targetFile, cancellationToken);
                var (existingFm, _) = ParseMarkdown(existingContent);
                if (existingFm.Id == task.Id)
                {
                    break; // Overwrite this task's existing file
                }
            }
            catch
            {
                // Unreadable file, advance suffix
            }

            targetFile = Path.Combine(tasksDir, $"{datePrefix}-{slug}-{suffix++}.md");
        }

        var markdown = GenerateMarkdown(task, dependencies, agentProfileId);
        var hash = ComputeContentHash(markdown);
        _suppressedHashes[hash] = 1;

        await File.WriteAllTextAsync(targetFile, markdown.Replace("\r\n", "\n"), Encoding.UTF8, cancellationToken);
        return targetFile;
    }

    public void SuppressContentHash(string content)
    {
        var hash = ComputeContentHash(content);
        _suppressedHashes[hash] = 1;
    }

    public Task ProcessFileChangedAsync(string filePath, int workspaceId, CancellationToken cancellationToken = default)
        => ProcessFileChangedAsync(filePath, workspaceId, expectedTaskId: null, cancellationToken: cancellationToken);

    public async Task ProcessFileChangedAsync(string filePath, int workspaceId, int? expectedTaskId, CancellationToken cancellationToken = default)
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

        await ProcessFileContentAsync(filePath, content, workspaceId, expectedTaskId, cancellationToken);
    }

    public Task ProcessFileContentAsync(string filePath, string content, int workspaceId, CancellationToken cancellationToken = default)
        => ProcessFileContentAsync(filePath, content, workspaceId, expectedTaskId: null, cancellationToken: cancellationToken);

    public async Task ProcessFileContentAsync(string filePath, string content, int workspaceId, int? expectedTaskId, CancellationToken cancellationToken = default)
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

        var targetTaskId = frontmatter.Id.HasValue && frontmatter.Id.Value > 0
            ? frontmatter.Id.Value
            : (expectedTaskId.HasValue && expectedTaskId.Value > 0 ? expectedTaskId.Value : (int?)null);

        var (taskRepo, depEngine, scope) = ResolveDependencies();
        try
        {
            if (targetTaskId.HasValue)
            {
                var task = await taskRepo.GetByIdAsync(targetTaskId.Value, cancellationToken);
                if (task != null)
                {
                    bool restoreProtected = false;

                    // Validate system-managed fields
                    if (!string.Equals(frontmatter.Status, task.Status.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        restoreProtected = true;
                        frontmatter.Status = task.Status.ToString();
                    }

                    if (!string.Equals(frontmatter.CurrentStage, task.CurrentStageId, StringComparison.OrdinalIgnoreCase))
                    {
                        restoreProtected = true;
                        frontmatter.CurrentStage = task.CurrentStageId;
                    }

                    if (frontmatter.WaitingForHuman != task.IsWaitingForHuman)
                    {
                        restoreProtected = true;
                        frontmatter.WaitingForHuman = task.IsWaitingForHuman;
                    }

                    // Update editable fields
                    if (!string.IsNullOrWhiteSpace(frontmatter.Title))
                    {
                        task.Title = frontmatter.Title;
                    }
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        task.Description = description;
                    }
                    task.Priority = frontmatter.Priority;
                    task.BaseBranch = frontmatter.BaseBranch;
                    if (!string.IsNullOrWhiteSpace(frontmatter.FeatureBranch))
                    {
                        task.FeatureBranch = frontmatter.FeatureBranch;
                    }
                    task.UpdatedAt = DateTime.UtcNow;

                    // Validate DAG cycles on dependency changes if engine is provided
                    if (depEngine != null && frontmatter.DependsOn != null)
                    {
                        var testDependencies = frontmatter.DependsOn.Select(d => (task.Id, d));
                        if (depEngine.HasCycle(workspaceId, testDependencies))
                        {
                            // Cycle detected, revert dependencies in frontmatter
                            restoreProtected = true;
                            var existingDeps = await taskRepo.GetDependenciesAsync(task.Id, cancellationToken);
                            frontmatter.DependsOn = existingDeps.ToList();
                        }
                        else
                        {
                            await taskRepo.UpdateAsync(task, frontmatter.DependsOn, cancellationToken);
                        }
                    }
                    else
                    {
                        await taskRepo.UpdateAsync(task, frontmatter.DependsOn, cancellationToken);
                    }

                    if (restoreProtected)
                    {
                        // Rewrite back authoritative state
                        var rewritten = GenerateMarkdown(task, frontmatter.DependsOn, frontmatter.AgentProfile);
                        var newHash = ComputeContentHash(rewritten);
                        _suppressedHashes[newHash] = 1;
                        if (File.Exists(filePath))
                        {
                            await File.WriteAllTextAsync(filePath, rewritten.Replace("\r\n", "\n"), Encoding.UTF8, cancellationToken);
                        }
                    }

                    return;
                }
            }

            // New task dropped in without an ID
            var mergeMode = string.Equals(frontmatter.MergeMode, "automatic", StringComparison.OrdinalIgnoreCase)
                ? Domain.MergeMode.Automatic
                : Domain.MergeMode.ManualReviewRequired;

            var newTask = new ForgeTask
            {
                WorkspaceId = workspaceId,
                Title = string.IsNullOrWhiteSpace(frontmatter.Title) ? Path.GetFileNameWithoutExtension(filePath) : frontmatter.Title,
                Description = string.IsNullOrWhiteSpace(description) ? body : description,
                Priority = frontmatter.Priority,
                MergeMode = mergeMode,
                IsWaitingForHuman = frontmatter.WaitingForHuman,
                BaseBranch = string.IsNullOrWhiteSpace(frontmatter.BaseBranch) ? "main" : frontmatter.BaseBranch,
                Status = TaskOverallStatus.Suggested,
                CurrentStageId = StandardStageIds.TaskReview,
                CreatedAt = frontmatter.CreatedAt ?? DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var created = await taskRepo.CreateAsync(newTask, frontmatter.DependsOn, cancellationToken);

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
                await File.WriteAllTextAsync(filePath, inPlaceMarkdown.Replace("\r\n", "\n"), Encoding.UTF8, cancellationToken);
            }
        }
        finally
        {
            scope?.Dispose();
        }
    }

    public async Task ArchiveOrCleanupTaskFileAsync(ForgeTask task, string workspacePath, bool archive = true, CancellationToken cancellationToken = default)
    {
        var tasksDir = Path.Combine(workspacePath, "docs", "tasks");
        if (!Directory.Exists(tasksDir)) return;

        var files = Directory.GetFiles(tasksDir, "*.md");
        foreach (var file in files)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file, cancellationToken);
                var (fm, _) = ParseMarkdown(content);
                if (fm.Id == task.Id)
                {
                    if (archive)
                    {
                        var archiveDir = Path.Combine(tasksDir, "archive");
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
        var tasksDir = Path.Combine(workspacePath, "docs", "tasks");
        Directory.CreateDirectory(tasksDir);

        if (_watchers.ContainsKey(workspaceId)) return;

        var watcher = new FileSystemWatcher(tasksDir, "*.md")
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

    public static string ExtractDescriptionFromBody(string body)
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
            if (inRequirements && line.StartsWith("## ❓ Human Clarifications", StringComparison.OrdinalIgnoreCase))
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

        var qnaIndex = body.IndexOf("## ❓ Human Clarifications", StringComparison.OrdinalIgnoreCase);
        var trimmedBody = qnaIndex >= 0 ? body.Substring(0, qnaIndex).Trim() : body.Trim();

        if (trimmedBody.StartsWith("# "))
        {
            var newlineIdx = trimmedBody.IndexOf('\n');
            if (newlineIdx >= 0)
            {
                trimmedBody = trimmedBody.Substring(newlineIdx + 1).Trim();
            }
            else
            {
                trimmedBody = string.Empty;
            }
        }

        return trimmedBody;
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
