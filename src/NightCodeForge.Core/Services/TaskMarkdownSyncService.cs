using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Implements bidirectional mirroring of active and closed tasks to docs/tasks/*.md with debouncing,
/// echo loop suppression, atomic history tracking, and strict fail-fast validation.
/// </summary>
public partial class TaskMarkdownSyncService : ITaskMarkdownSyncService
{
    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphaNumericRegex();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeserializer _yamlDeserializer;
    private readonly ISerializer _yamlSerializer;
    private readonly ConcurrentDictionary<string, byte> _suppressedHashes = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounceTokens = new();
    private readonly ConcurrentDictionary<int, FileSystemWatcher> _watchers = new();
    private readonly TimeSpan _debounceWindow = TimeSpan.FromMilliseconds(300);

    [ActivatorUtilitiesConstructor]
    public TaskMarkdownSyncService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _yamlDeserializer = CreateYamlDeserializer();
        _yamlSerializer = CreateYamlSerializer();
    }

    private static IDeserializer CreateYamlDeserializer() =>
        new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    private static ISerializer CreateYamlSerializer() =>
        new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

    private (ITaskRepository TaskRepo, IDependencyResolutionEngine DepEngine, IServiceScope Scope) ResolveDependencies()
    {
        var scope = _scopeFactory.CreateScope();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var depEngine = scope.ServiceProvider.GetRequiredService<IDependencyResolutionEngine>();
        return (taskRepo, depEngine, scope);
    }

    public string GenerateMarkdown(ForgeTask task, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null)
    {
        var depList = dependencies != null
            ? dependencies.ToList()
            : task.Dependencies?.Select(d => d.DependsOnTaskId).ToList() ?? new List<int>();

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
            DependsOn = depList,
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

    public (TaskFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent) => ParseMarkdown(markdownContent, null);

    public (TaskFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent, string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(markdownContent))
        {
            return (new TaskFrontmatter(), string.Empty);
        }

        var normalized = markdownContent.Replace("\r\n", "\n");
        if (normalized.StartsWith("---"))
        {
            var secondFence = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (secondFence == -1)
            {
                throw new TaskMarkdownSyncException(
                    $"Malformed markdown file '{filePath ?? "content"}': missing closing frontmatter fence ('---').",
                    filePath);
            }

            var yamlPart = normalized.Substring(3, secondFence - 3).Trim();
            var bodyPart = normalized.Substring(secondFence + 4).TrimStart('\r', '\n');

            try
            {
                var frontmatter = _yamlDeserializer.Deserialize<TaskFrontmatter>(yamlPart) ?? new TaskFrontmatter();
                return (frontmatter, bodyPart);
            }
            catch (Exception ex)
            {
                throw new TaskMarkdownSyncException(
                    $"Failed to parse YAML frontmatter in '{filePath ?? "task markdown"}': {ex.Message}",
                    ex,
                    filePath);
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
        var tasksDir = Path.Combine(workspacePath, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        var targetFile = await ResolveTargetFilePathAsync(tasksDir, task, cancellationToken);

        var markdown = GenerateMarkdown(task, dependencies, agentProfileId);
        var hash = ComputeContentHash(markdown);
        _suppressedHashes[hash] = 1;

        await File.WriteAllTextAsync(targetFile, markdown.Replace("\r\n", "\n"), Encoding.UTF8, cancellationToken);
        return targetFile;
    }

    public async Task<SyncAllToDiskResult> SyncAllTasksToDiskAsync(
        Workspace workspace,
        string targetDirectory,
        string? hostRepositoryPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new ArgumentException("Target directory cannot be null or empty.", nameof(targetDirectory));

        var (taskRepo, _, scope) = ResolveDependencies();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tasksDir = Path.Combine(targetDirectory, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
            Directory.CreateDirectory(tasksDir);

            string? hostTasksDir = null;
            if (!string.IsNullOrWhiteSpace(hostRepositoryPath) &&
                !string.Equals(hostRepositoryPath, targetDirectory, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(hostRepositoryPath))
            {
                hostTasksDir = Path.Combine(hostRepositoryPath, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
                Directory.CreateDirectory(hostTasksDir);
            }

            var allTasks = await taskRepo.GetByWorkspaceIdAsync(workspace.Id, cancellationToken);
            var workspaceDeps = await taskRepo.GetWorkspaceDependenciesAsync(workspace.Id, cancellationToken);

            var writtenPaths = new Dictionary<int, string>();
            var baseline = new Dictionary<int, TaskBaselineEntry>();

            foreach (var task in allTasks)
            {
                IReadOnlyList<int> depIds;
                if (task.Dependencies != null && task.Dependencies.Count > 0)
                {
                    depIds = task.Dependencies.Select(d => d.DependsOnTaskId).ToList();
                }
                else if (workspaceDeps.TryGetValue(task.Id, out var loadedDeps))
                {
                    depIds = loadedDeps;
                }
                else
                {
                    depIds = await taskRepo.GetDependenciesAsync(task.Id, cancellationToken);
                }

                var targetFile = await ResolveTargetFilePathAsync(tasksDir, task, cancellationToken);

                var markdown = GenerateMarkdown(task, depIds);
                var hash = ComputeContentHash(markdown);
                _suppressedHashes[hash] = 1;

                await File.WriteAllTextAsync(targetFile, markdown.Replace("\r\n", "\n"), Encoding.UTF8, cancellationToken);
                writtenPaths[task.Id] = targetFile;

                var (parsedFm, parsedBody) = ParseMarkdown(markdown, targetFile);
                var baselineEntry = new TaskBaselineEntry(
                    task.Id,
                    task.Version,
                    task.UpdatedAt,
                    hash,
                    targetFile,
                    parsedFm,
                    task.Description);
                baseline[task.Id] = baselineEntry;

                if (hostTasksDir != null)
                {
                    var hostFile = Path.Combine(hostTasksDir, Path.GetFileName(targetFile));
                    _suppressedHashes[hash] = 1;
                    await File.WriteAllTextAsync(hostFile, markdown.Replace("\r\n", "\n"), Encoding.UTF8, cancellationToken);
                }
            }

            return new SyncAllToDiskResult(writtenPaths, baseline);
        }
        finally
        {
            scope.Dispose();
        }
    }
    /// <summary>
    /// Finds the file name to use for a task: the existing file already holding its ID, or the first free slot.
    /// An unreadable or corrupt candidate file fails fast instead of being silently skipped.
    /// </summary>
    private async Task<string> ResolveTargetFilePathAsync(string tasksDir, ForgeTask task, CancellationToken cancellationToken)
    {
        var datePrefix = (task.CreatedAt != default ? task.CreatedAt : DateTime.UtcNow).ToString("yyyy-MM-dd");
        var slug = GenerateSlug(task.Title);
        var targetFile = Path.Combine(tasksDir, $"{datePrefix}-{slug}{TaskSyncConstants.MarkdownExtension}");
        int suffix = 2;

        while (File.Exists(targetFile))
        {
            string existingContent;
            try
            {
                existingContent = await File.ReadAllTextAsync(targetFile, cancellationToken);
            }
            catch (IOException ex)
            {
                throw new TaskMarkdownSyncException($"Failed to read task markdown file '{targetFile}': {ex.Message}", ex, targetFile);
            }

            var (existingFm, _) = ParseMarkdown(existingContent, targetFile);
            if (existingFm.Id == task.Id)
            {
                break;
            }

            targetFile = Path.Combine(tasksDir, $"{datePrefix}-{slug}-{suffix++}{TaskSyncConstants.MarkdownExtension}");
        }

        return targetFile;
    }


    public async Task<IReadOnlyList<ForgeTask>> SyncAllTasksFromDiskAsync(
        Workspace workspace,
        string targetDirectory,
        string? hostRepositoryPath,
        IReadOnlyDictionary<int, TaskBaselineEntry>? baselineSnapshot,
        string changeActor,
        string? changeReason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new ArgumentException("Target directory cannot be null or empty.", nameof(targetDirectory));

        cancellationToken.ThrowIfCancellationRequested();

        var tasksDir = Path.Combine(targetDirectory, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        if (!Directory.Exists(tasksDir))
        {
            return Array.Empty<ForgeTask>();
        }

        var files = Directory.GetFiles(tasksDir, TaskSyncConstants.MarkdownPattern, SearchOption.TopDirectoryOnly);

        var (taskRepo, depEngine, scope) = ResolveDependencies();
        try
        {
            var dbTasksList = await taskRepo.GetByWorkspaceIdAsync(workspace.Id, cancellationToken);
            var dbTasks = dbTasksList.ToDictionary(t => t.Id);

            string? hostTasksDir = null;
            if (!string.IsNullOrWhiteSpace(hostRepositoryPath) &&
                !string.Equals(hostRepositoryPath, targetDirectory, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(hostRepositoryPath))
            {
                hostTasksDir = Path.Combine(hostRepositoryPath, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
                Directory.CreateDirectory(hostTasksDir);
            }

            var parsedFiles = new List<(string FilePath, string Content, string Hash, TaskFrontmatter Frontmatter, string Body, string Description)>();

            foreach (var file in files)
            {
                string content;
                try
                {
                    content = await File.ReadAllTextAsync(file, cancellationToken);
                }
                catch (Exception ex)
                {
                    throw new TaskMarkdownSyncException($"Failed to read task markdown file '{file}': {ex.Message}", ex, file);
                }

                var (frontmatter, body) = ParseMarkdown(content, file);
                var hash = ComputeContentHash(content);
                var description = ExtractDescriptionFromBody(body);

                parsedFiles.Add((file, content, hash, frontmatter, body, description));
            }

            // 1. Validate Existing and New Tasks
            var duplicateId = parsedFiles
                .Where(p => p.Frontmatter.Id.HasValue && p.Frontmatter.Id.Value > 0)
                .GroupBy(p => p.Frontmatter.Id!.Value)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicateId != null)
            {
                throw new TaskMarkdownSyncException(
                    $"Task #{duplicateId.Key} is declared by multiple markdown files: {string.Join(", ", duplicateId.Select(p => Path.GetFileName(p.FilePath)))}.",
                    duplicateId.First().FilePath,
                    duplicateId.Key);
            }

            foreach (var item in parsedFiles)
            {
                if (item.Frontmatter.Id.HasValue && item.Frontmatter.Id.Value > 0)
                {
                    var taskId = item.Frontmatter.Id.Value;
                    if (!dbTasks.TryGetValue(taskId, out var dbTask))
                    {
                        throw new TaskMarkdownSyncException(
                            $"Task #{taskId} referenced in '{item.FilePath}' does not exist in workspace #{workspace.Id}.",
                            item.FilePath,
                            taskId);
                    }

                    // Concurrency check
                    if (baselineSnapshot != null && baselineSnapshot.TryGetValue(taskId, out var baseline))
                    {
                        bool diskDiffersFromBaseline = !string.Equals(item.Hash, baseline.ContentHash, StringComparison.Ordinal);
                        if (dbTask.Version != baseline.Version && diskDiffersFromBaseline)
                        {
                            throw new TaskConcurrencyException(taskId, baseline.Version, dbTask.Version);
                        }
                    }

                    // Protected status check. When a baseline exists the reference is the status written
                    // before the agent ran, so a workflow status change made meanwhile (e.g. user cancel)
                    // is not mistaken for an agent edit.
                    if (!string.IsNullOrWhiteSpace(item.Frontmatter.Status))
                    {
                        var referenceStatus = baselineSnapshot != null && baselineSnapshot.TryGetValue(taskId, out var statusBaseline) &&
                                              !string.IsNullOrWhiteSpace(statusBaseline.Frontmatter.Status)
                            ? statusBaseline.Frontmatter.Status.Trim()
                            : dbTask.Status.ToString();
                        if (!string.Equals(item.Frontmatter.Status.Trim(), referenceStatus, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new TaskMarkdownSyncException(
                                $"Modifying task status via markdown is forbidden. Task #{taskId} status is '{referenceStatus}', but markdown specifies '{item.Frontmatter.Status}'. Status transitions must go through workflow gates.",
                                item.FilePath,
                                taskId);
                        }
                    }
                }
                else
                {
                    // New task: Title must not be blank
                    if (string.IsNullOrWhiteSpace(item.Frontmatter.Title))
                    {
                        throw new TaskMarkdownSyncException(
                            $"New task in '{item.FilePath}' must specify a non-blank title in YAML frontmatter.",
                            item.FilePath);
                    }
                }
            }

            // 2. Validate Dependencies & Detect Cycles across the combined graph
            var combinedDeps = new Dictionary<int, List<int>>();
            foreach (var kvp in dbTasks)
            {
                combinedDeps[kvp.Key] = kvp.Value.Dependencies.Select(d => d.DependsOnTaskId).ToList();
            }

            // Overlay disk changes (missing depends_on leaves deps unchanged; explicit empty removes)
            foreach (var item in parsedFiles)
            {
                if (item.Frontmatter.Id.HasValue && item.Frontmatter.Id.Value > 0)
                {
                    if (item.Frontmatter.DependsOn != null)
                    {
                        combinedDeps[item.Frontmatter.Id.Value] = item.Frontmatter.DependsOn.Distinct().ToList();
                    }
                }
            }

            // Verify dependency targets exist in workspace and are not self-referential
            foreach (var (taskId, depList) in combinedDeps)
            {
                foreach (var depId in depList)
                {
                    if (depId == taskId)
                    {
                        var matchingFile = parsedFiles.FirstOrDefault(p => p.Frontmatter.Id == taskId).FilePath;
                        throw new TaskMarkdownSyncException($"Task #{taskId} cannot depend on itself.", matchingFile, taskId);
                    }

                    if (!dbTasks.ContainsKey(depId))
                    {
                        var matchingFile = parsedFiles.FirstOrDefault(p => p.Frontmatter.Id == taskId).FilePath;
                        throw new TaskMarkdownSyncException(
                            $"Task #{taskId} depends on #{depId}, which does not exist in workspace #{workspace.Id}.",
                            matchingFile,
                            taskId);
                    }
                }
            }

            // Validate new tasks' dependencies
            foreach (var item in parsedFiles.Where(p => !p.Frontmatter.Id.HasValue || p.Frontmatter.Id.Value <= 0))
            {
                if (item.Frontmatter.DependsOn != null)
                {
                    foreach (var depId in item.Frontmatter.DependsOn)
                    {
                        if (!dbTasks.ContainsKey(depId))
                        {
                            throw new TaskMarkdownSyncException(
                                $"New task in '{item.FilePath}' depends on #{depId}, which does not exist in workspace #{workspace.Id}.",
                                item.FilePath);
                        }
                    }
                }
            }

            // Combined acyclicity check
            var visited = new Dictionary<int, int>(); // 0: unvisited, 1: visiting (in current DFS stack), 2: visited
            foreach (var taskId in combinedDeps.Keys)
            {
                if (!visited.TryGetValue(taskId, out var state) || state == 0)
                {
                    var stack = new List<int>();
                    if (HasCycleDfs(taskId, combinedDeps, visited, stack))
                    {
                        var cyclePath = string.Join(" -> ", stack);
                        var matchingFile = parsedFiles.FirstOrDefault(p => p.Frontmatter.Id == taskId).FilePath;
                        throw new TaskMarkdownSyncException(
                            $"Circular dependency detected in workspace #{workspace.Id}: {cyclePath}",
                            matchingFile,
                            taskId);
                    }
                }
            }

            // 3. Apply updates and creations
            var refreshedTasks = new List<ForgeTask>();

            foreach (var item in parsedFiles)
            {
                if (item.Frontmatter.Id.HasValue && item.Frontmatter.Id.Value > 0)
                {
                    var taskId = item.Frontmatter.Id.Value;
                    var task = dbTasks[taskId];

                    var newTitle = !string.IsNullOrWhiteSpace(item.Frontmatter.Title) ? item.Frontmatter.Title.Trim() : task.Title;
                    var newDesc = !string.IsNullOrWhiteSpace(item.Description) ? item.Description : task.Description;
                    var newPriority = item.Frontmatter.Priority > 0 ? item.Frontmatter.Priority : task.Priority;
                    var newDeps = item.Frontmatter.DependsOn?.Distinct().ToList();

                    bool titleChanged = !string.Equals(newTitle, task.Title, StringComparison.Ordinal);
                    bool descChanged = !string.Equals(newDesc, task.Description, StringComparison.Ordinal);
                    bool priorityChanged = newPriority != task.Priority;

                    var currentDeps = task.Dependencies.Select(d => d.DependsOnTaskId).OrderBy(x => x).ToList();
                    bool depsChanged = newDeps != null && !newDeps.OrderBy(x => x).SequenceEqual(currentDeps);

                    if (titleChanged || descChanged || priorityChanged || depsChanged)
                    {
                        task.Title = newTitle;
                        task.Description = newDesc;
                        task.Priority = newPriority;
                        task.UpdatedAt = DateTime.UtcNow;

                        await taskRepo.UpdateWithHistoryAsync(
                            task,
                            newDeps,
                            changeActor,
                            changeReason ?? TaskSyncConstants.DiskModificationsSync,
                            cancellationToken);
                    }

                    // Mirror to host if needed
                    if (hostTasksDir != null)
                    {
                        var hostFile = Path.Combine(hostTasksDir, Path.GetFileName(item.FilePath));
                        _suppressedHashes[item.Hash] = 1;
                        await File.WriteAllTextAsync(hostFile, item.Content.Replace("\r\n", "\n"), Encoding.UTF8, cancellationToken);
                    }

                    refreshedTasks.Add(task);
                }
                else
                {
                    // New Task
                    var mergeMode = string.Equals(item.Frontmatter.MergeMode, "automatic", StringComparison.OrdinalIgnoreCase)
                        ? Domain.MergeMode.Automatic
                        : Domain.MergeMode.ManualReviewRequired;

                    var newTask = new ForgeTask
                    {
                        WorkspaceId = workspace.Id,
                        Title = item.Frontmatter.Title.Trim(),
                        Description = !string.IsNullOrWhiteSpace(item.Description) ? item.Description : item.Body,
                        Priority = item.Frontmatter.Priority > 0 ? item.Frontmatter.Priority : TaskSyncConstants.DefaultPriority,
                        MergeMode = mergeMode,
                        IsWaitingForHuman = item.Frontmatter.WaitingForHuman,
                        BaseBranch = !string.IsNullOrWhiteSpace(item.Frontmatter.BaseBranch) ? item.Frontmatter.BaseBranch : (workspace.DefaultBaseBranch ?? WorkflowDefaults.DefaultBaseBranch),
                        FeatureBranch = item.Frontmatter.FeatureBranch ?? string.Empty,
                        Status = TaskOverallStatus.Suggested,
                        CurrentStageId = StandardStageIds.TaskReview,
                        CreatedAt = item.Frontmatter.CreatedAt ?? DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        Version = 1
                    };

                    var created = await taskRepo.CreateWithBaselineAsync(
                        newTask,
                        item.Frontmatter.DependsOn,
                        changeActor,
                        changeReason ?? TaskSyncConstants.TaskCreatedFromDisk,
                        cancellationToken);

                    // Update frontmatter and rewrite file in-place with allocated ID
                    item.Frontmatter.Id = created.Id;
                    item.Frontmatter.CreatedAt = created.CreatedAt;
                    item.Frontmatter.Status = created.Status.ToString();
                    item.Frontmatter.MergeMode = created.MergeMode == Domain.MergeMode.Automatic ? "automatic" : "manual_review_required";
                    item.Frontmatter.WaitingForHuman = created.IsWaitingForHuman;
                    item.Frontmatter.CurrentStage = created.CurrentStageId;

                    var rewrittenMarkdown = GenerateMarkdown(created, item.Frontmatter.DependsOn, item.Frontmatter.AgentProfile);
                    var rewrittenHash = ComputeContentHash(rewrittenMarkdown);
                    _suppressedHashes[rewrittenHash] = 1;

                    var datePrefix = (created.CreatedAt != default ? created.CreatedAt : DateTime.UtcNow).ToString("yyyy-MM-dd");
                    var slug = GenerateSlug(created.Title);
                    var newFileName = $"{datePrefix}-{slug}{TaskSyncConstants.MarkdownExtension}";
                    var newFilePath = Path.Combine(tasksDir, newFileName);

                    int suffix = 2;
                    while (File.Exists(newFilePath) && !string.Equals(newFilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        newFilePath = Path.Combine(tasksDir, $"{datePrefix}-{slug}-{suffix++}{TaskSyncConstants.MarkdownExtension}");
                    }

                    if (!string.Equals(newFilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(item.FilePath))
                        {
                            File.Delete(item.FilePath);
                        }
                    }

                    await File.WriteAllTextAsync(newFilePath, rewrittenMarkdown.Replace("\r\n", "\n"), Encoding.UTF8, cancellationToken);

                    // Mirror to host repository using new path and clean up stale old name
                    if (hostTasksDir != null)
                    {
                        var oldHostFile = Path.Combine(hostTasksDir, Path.GetFileName(item.FilePath));
                        if (File.Exists(oldHostFile) && !string.Equals(oldHostFile, Path.Combine(hostTasksDir, Path.GetFileName(newFilePath)), StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(oldHostFile);
                        }

                        var newHostFile = Path.Combine(hostTasksDir, Path.GetFileName(newFilePath));
                        _suppressedHashes[rewrittenHash] = 1;
                        await File.WriteAllTextAsync(newHostFile, rewrittenMarkdown.Replace("\r\n", "\n"), Encoding.UTF8, cancellationToken);
                    }

                    refreshedTasks.Add(created);
                }
            }

            return refreshedTasks;
        }
        finally
        {
            scope.Dispose();
        }
    }

    private static bool HasCycleDfs(int node, Dictionary<int, List<int>> graph, Dictionary<int, int> visited, List<int> stack)
    {
        visited[node] = 1;
        stack.Add(node);

        if (graph.TryGetValue(node, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                if (!visited.TryGetValue(neighbor, out var state) || state == 0)
                {
                    if (HasCycleDfs(neighbor, graph, visited, stack))
                    {
                        return true;
                    }
                }
                else if (state == 1)
                {
                    stack.Add(neighbor);
                    return true;
                }
            }
        }

        visited[node] = 2;
        stack.RemoveAt(stack.Count - 1);
        return false;
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
        catch (Exception ex)
        {
            throw new TaskMarkdownSyncException($"Failed to read task markdown file '{filePath}': {ex.Message}", ex, filePath);
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
            return;
        }

        var (frontmatter, body) = ParseMarkdown(content, filePath);
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
                    if (task.WorkspaceId != workspaceId)
                    {
                        throw new TaskMarkdownSyncException(
                            $"Task #{task.Id} belongs to workspace #{task.WorkspaceId}, not #{workspaceId}.",
                            filePath,
                            task.Id);
                    }

                    // Enforce protected status
                    if (!string.IsNullOrWhiteSpace(frontmatter.Status) &&
                        !string.Equals(frontmatter.Status.Trim(), task.Status.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new TaskMarkdownSyncException(
                            $"Modifying task status via markdown is forbidden. Task #{task.Id} status is '{task.Status}', but markdown specifies '{frontmatter.Status}'. Status transitions must go through workflow gates.",
                            filePath,
                            task.Id);
                    }

                    var newTitle = !string.IsNullOrWhiteSpace(frontmatter.Title) ? frontmatter.Title.Trim() : task.Title;
                    var newDesc = !string.IsNullOrWhiteSpace(description) ? description : task.Description;
                    var newPriority = frontmatter.Priority > 0 ? frontmatter.Priority : task.Priority;
                    var newDeps = frontmatter.DependsOn?.Distinct().ToList();

                    if (depEngine != null && newDeps != null)
                    {
                        var testDeps = newDeps.Select(d => (task.Id, d));
                        if (depEngine.HasCycle(workspaceId, testDeps))
                        {
                            throw new TaskMarkdownSyncException(
                                $"Circular dependency detected when updating task #{task.Id}.",
                                filePath,
                                task.Id);
                        }
                    }

                    task.Title = newTitle;
                    task.Description = newDesc;
                    task.Priority = newPriority;
                    task.BaseBranch = !string.IsNullOrWhiteSpace(frontmatter.BaseBranch) ? frontmatter.BaseBranch : task.BaseBranch;
                    if (!string.IsNullOrWhiteSpace(frontmatter.FeatureBranch))
                    {
                        task.FeatureBranch = frontmatter.FeatureBranch;
                    }
                    task.UpdatedAt = DateTime.UtcNow;

                    await taskRepo.UpdateWithHistoryAsync(
                        task,
                        newDeps,
                        AuditActors.User,
                        TaskSyncConstants.DiskModificationsSync,
                        cancellationToken);

                    return;
                }
            }

            // New task
            if (string.IsNullOrWhiteSpace(frontmatter.Title))
            {
                throw new TaskMarkdownSyncException(
                    $"New task in '{filePath}' must specify a non-blank title in YAML frontmatter.",
                    filePath);
            }

            var mergeMode = string.Equals(frontmatter.MergeMode, "automatic", StringComparison.OrdinalIgnoreCase)
                ? Domain.MergeMode.Automatic
                : Domain.MergeMode.ManualReviewRequired;

            var newTask = new ForgeTask
            {
                WorkspaceId = workspaceId,
                Title = frontmatter.Title.Trim(),
                Description = !string.IsNullOrWhiteSpace(description) ? description : body,
                Priority = frontmatter.Priority > 0 ? frontmatter.Priority : TaskSyncConstants.DefaultPriority,
                MergeMode = mergeMode,
                IsWaitingForHuman = frontmatter.WaitingForHuman,
                BaseBranch = string.IsNullOrWhiteSpace(frontmatter.BaseBranch) ? WorkflowDefaults.DefaultBaseBranch : frontmatter.BaseBranch,
                Status = TaskOverallStatus.Suggested,
                CurrentStageId = StandardStageIds.TaskReview,
                CreatedAt = frontmatter.CreatedAt ?? DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Version = 1
            };

            var created = await taskRepo.CreateWithBaselineAsync(
                newTask,
                frontmatter.DependsOn,
                AuditActors.User,
                TaskSyncConstants.TaskCreatedFromDisk,
                cancellationToken);

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
            scope.Dispose();
        }
    }

    public async Task ArchiveOrCleanupTaskFileAsync(ForgeTask task, string workspacePath, bool archive = true, CancellationToken cancellationToken = default)
    {
        var tasksDir = Path.Combine(workspacePath, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        if (!Directory.Exists(tasksDir)) return;

        var files = Directory.GetFiles(tasksDir, TaskSyncConstants.MarkdownPattern);
        foreach (var file in files)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file, cancellationToken);
                var (fm, _) = ParseMarkdown(content, file);
                if (fm.Id == task.Id)
                {
                    if (archive)
                    {
                        var archiveDir = Path.Combine(tasksDir, TaskSyncConstants.ArchiveSegment);
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
        var tasksDir = Path.Combine(workspacePath, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        if (_watchers.ContainsKey(workspaceId)) return;

        var watcher = new FileSystemWatcher(tasksDir, TaskSyncConstants.MarkdownPattern)
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
