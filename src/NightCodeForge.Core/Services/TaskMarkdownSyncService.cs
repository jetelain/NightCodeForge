using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
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

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}-")]
    private static partial Regex DatePrefixRegex();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly ILogger<TaskMarkdownSyncService> _logger;
    private readonly IDeserializer _yamlDeserializer;
    private readonly ISerializer _yamlSerializer;
    private readonly ConcurrentDictionary<string, byte> _suppressedHashes = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounceTokens = new();
    private readonly ConcurrentDictionary<int, FileSystemWatcher> _watchers = new();
    private readonly TimeSpan _debounceWindow = TimeSpan.FromMilliseconds(300);

    [ActivatorUtilitiesConstructor]
    public TaskMarkdownSyncService(
        IServiceScopeFactory scopeFactory,
        IWorkspacePathResolver pathResolver,
        ILogger<TaskMarkdownSyncService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _yamlDeserializer = CreateYamlDeserializer();
        _yamlSerializer = CreateYamlSerializer();
    }

    private static IDeserializer CreateYamlDeserializer() =>
        new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new TaskDependencyReferenceListYamlConverter())
            .IgnoreUnmatchedProperties()
            .Build();

    private static ISerializer CreateYamlSerializer() =>
        new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new TaskDependencyReferenceListYamlConverter())
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
            Origin = FormatOrigin(task.Origin),
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

    public static string FormatOrigin(TaskOrigin origin) => origin switch
    {
        TaskOrigin.UntrustedIntake => "untrusted_intake",
        TaskOrigin.Ideation => "ideation",
        TaskOrigin.Import => "import",
        TaskOrigin.GitHubIssue => "github_issue",
        _ => "manual"
    };

    public static TaskOrigin ParseOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return TaskOrigin.Manual;
        return origin.Trim().ToLowerInvariant() switch
        {
            "untrusted_intake" or "untrustedintake" => TaskOrigin.UntrustedIntake,
            "ideation" => TaskOrigin.Ideation,
            "import" => TaskOrigin.Import,
            "github_issue" or "githubissue" or "github" => TaskOrigin.GitHubIssue,
            _ => TaskOrigin.Manual
        };
    }

    public static string GenerateSlug(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "task";
        var slug = NonAlphaNumericRegex().Replace(title.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "task" : slug;
    }

    public static string NormalizeDependencyKey(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var trimmed = input.Trim();
        if (trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3];
        }
        if (DatePrefixRegex().IsMatch(trimmed))
        {
            trimmed = trimmed.Substring(11);
        }
        return GenerateSlug(trimmed);
    }

    public static DateTime? TryExtractDateFromPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var fileName = Path.GetFileName(filePath);
        if (fileName.Length >= 10 && DatePrefixRegex().IsMatch(fileName) &&
            DateTime.TryParseExact(fileName[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }
        return null;
    }

    private static string NormalizeCloneTasksDirectory(string cloneDirectory)
    {
        var trimmed = cloneDirectory.TrimEnd('/', '\\');
        var leaf = Path.GetFileName(trimmed);
        var parent = Path.GetDirectoryName(trimmed);
        var parentLeaf = !string.IsNullOrEmpty(parent) ? Path.GetFileName(parent) : null;

        if (leaf.Equals(TaskSyncConstants.TasksSegment, StringComparison.OrdinalIgnoreCase) &&
            parentLeaf?.Equals(TaskSyncConstants.DocsSegment, StringComparison.OrdinalIgnoreCase) == true)
        {
            return trimmed;
        }

        return Path.Combine(trimmed, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
    }

    public string? ResolveCloneTasksDirectory(int workspaceId, Workspace? workspace = null, string? workspacePath = null)
    {
        return ResolveCloneTasksDirectoryAsync(workspaceId, workspace, workspacePath).GetAwaiter().GetResult();
    }

    public async Task<string?> ResolveCloneTasksDirectoryAsync(int workspaceId, Workspace? workspace = null, string? workspacePath = null, CancellationToken cancellationToken = default)
    {
        if (workspaceId <= 0) return null;

        // Try to resolve workspace from repository if null
        if (workspace == null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var wsRepo = scope.ServiceProvider.GetService<IWorkspaceRepository>();
                if (wsRepo != null)
                {
                    workspace = await wsRepo.GetByIdAsync(workspaceId, cancellationToken);
                }
            }
            catch (Exception)
            {
                // Best-effort workspace lookup; continues with path resolver fallbacks
            }
        }

        if (workspace != null && workspace.ExecutionMode == ExecutionMode.BareHost && string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            return null;
        }

        // 1. If explicit Ext4MirrorPath is present on workspace
        if (workspace != null && !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            var hostMirror = _pathResolver.ToHostPath(workspace.Ext4MirrorPath);
            if (!string.IsNullOrWhiteSpace(hostMirror) && Directory.Exists(hostMirror))
            {
                return NormalizeCloneTasksDirectory(hostMirror);
            }
        }

        // 2. Canonical working clone path
        try
        {
            var canonicalClone = _pathResolver.GetAgentWorkingClonePath(workspaceId);
            if (!string.IsNullOrWhiteSpace(canonicalClone))
            {
                var hostClone = _pathResolver.ToHostPath(canonicalClone);
                if (!string.IsNullOrWhiteSpace(hostClone) && Directory.Exists(hostClone))
                {
                    return NormalizeCloneTasksDirectory(hostClone);
                }
            }
        }
        catch (Exception)
        {
            // Ignore path resolution failure if environment metadata is inaccessible
        }

        // 3. Fallback: Crucible path resolver targeting the clone (taskId = null)
        try
        {
            var cruciblePath = _pathResolver.ResolveCruciblePath(workspaceId, string.Empty, null);
            if (!string.IsNullOrWhiteSpace(cruciblePath) && Directory.Exists(cruciblePath))
            {
                return NormalizeCloneTasksDirectory(cruciblePath);
            }
        }
        catch (Exception)
        {
            // Ignore if crucible resolution cannot map the directory
        }

        // 4. Fallback: Check if given workspacePath is an agent worktree / repo copy with sibling clone
        // Note: Sibling clone applies when workspacePath is an isolated worktree or working copy (e.g. leaf is 'repo', 'task-*', 'wt-*', 'mirror')
        var targetCandidate = !string.IsNullOrWhiteSpace(workspacePath) ? workspacePath : workspace?.RepositoryPath;
        if (!string.IsNullOrWhiteSpace(targetCandidate))
        {
            try
            {
                var normPath = Path.GetFullPath(targetCandidate).TrimEnd('/', '\\');
                var leafName = Path.GetFileName(normPath);
                if (leafName.StartsWith("task-", StringComparison.OrdinalIgnoreCase) ||
                    leafName.StartsWith("wt-", StringComparison.OrdinalIgnoreCase) ||
                    leafName.Equals("repo", StringComparison.OrdinalIgnoreCase) ||
                    leafName.Equals("mirror", StringComparison.OrdinalIgnoreCase))
                {
                    var parentDir = Path.GetDirectoryName(normPath);
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        var siblingClone = Path.Combine(parentDir, "clone");
                        if (Directory.Exists(siblingClone))
                        {
                            return NormalizeCloneTasksDirectory(siblingClone);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore invalid path formats
            }
        }

        return null;
    }

    private async Task<string> ResolveCloneTargetFilePathAsync(string cloneTasksDir, string hostTargetFile, ForgeTask task, CancellationToken cancellationToken)
    {
        var hostFileName = Path.GetFileName(hostTargetFile);
        if (!string.IsNullOrWhiteSpace(hostFileName))
        {
            var targetClonePath = Path.Combine(cloneTasksDir, hostFileName);

            // Clean up any stale or renamed file holding this task's ID under a different name in clone
            if (Directory.Exists(cloneTasksDir))
            {
                var files = Directory.GetFiles(cloneTasksDir, TaskSyncConstants.MarkdownPattern, SearchOption.TopDirectoryOnly);
                foreach (var file in files)
                {
                    if (string.Equals(file, targetClonePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        var existingContent = await File.ReadAllTextAsync(file, cancellationToken);
                        var (existingFm, _) = ParseMarkdown(existingContent, file);
                        if (existingFm.Id == task.Id)
                        {
                            File.Delete(file);
                        }
                    }
                    catch (Exception)
                    {
                        // Continue scanning other candidate files
                    }
                }
            }

            return targetClonePath;
        }

        return await ResolveTargetFilePathAsync(cloneTasksDir, task, cancellationToken);
    }

    public Task<string> SyncTaskToDiskAsync(
        ForgeTask task,
        string workspacePath,
        IReadOnlyList<int>? dependencies = null,
        string? agentProfileId = null,
        CancellationToken cancellationToken = default)
    {
        return SyncTaskToDiskAsync(task, workspacePath, dependencies, agentProfileId, cloneDirectory: null, cancellationToken);
    }

    public async Task<string> SyncTaskToDiskAsync(
        ForgeTask task,
        string workspacePath,
        IReadOnlyList<int>? dependencies,
        string? agentProfileId,
        string? cloneDirectory,
        CancellationToken cancellationToken = default)
    {
        var tasksDir = Path.Combine(workspacePath, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        var targetFile = await ResolveTargetFilePathAsync(tasksDir, task, cancellationToken);

        var markdown = GenerateMarkdown(task, dependencies, agentProfileId);
        var hash = ComputeContentHash(markdown);
        _suppressedHashes[hash] = 1;

        await File.WriteAllTextAsync(targetFile, markdown.Replace("\r\n", "\n"), Encoding.UTF8, cancellationToken);

        // Keep issues in sync into clone/docs/tasks
        var cloneTasksDir = !string.IsNullOrWhiteSpace(cloneDirectory)
            ? NormalizeCloneTasksDirectory(cloneDirectory)
            : await ResolveCloneTasksDirectoryAsync(task.WorkspaceId, task.Workspace, workspacePath, cancellationToken);

        if (!string.IsNullOrWhiteSpace(cloneTasksDir))
        {
            try
            {
                var normClone = Path.GetFullPath(cloneTasksDir).TrimEnd('/', '\\');
                var normTasks = Path.GetFullPath(tasksDir).TrimEnd('/', '\\');
                if (!string.Equals(normClone, normTasks, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(cloneTasksDir);
                    var cloneTargetFile = await ResolveCloneTargetFilePathAsync(cloneTasksDir, targetFile, task, cancellationToken);
                    _suppressedHashes[hash] = 1;
                    await File.WriteAllTextAsync(cloneTargetFile, markdown.Replace("\r\n", "\n"), Encoding.UTF8, cancellationToken);
                }
            }
            catch (Exception)
            {
                // Best-effort clone mirror write; primary workspace path already succeeded
            }
        }

        return targetFile;
    }

    public Task<SyncAllToDiskResult> SyncAllTasksToDiskAsync(
        Workspace workspace,
        string targetDirectory,
        string? hostRepositoryPath = null,
        CancellationToken cancellationToken = default)
    {
        return SyncAllTasksToDiskAsync(workspace, targetDirectory, hostRepositoryPath, cloneDirectory: null, cancellationToken);
    }

    public async Task<SyncAllToDiskResult> SyncAllTasksToDiskAsync(
        Workspace workspace,
        string targetDirectory,
        string? hostRepositoryPath,
        string? cloneDirectory,
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

            var cloneTasksDir = !string.IsNullOrWhiteSpace(cloneDirectory)
                ? NormalizeCloneTasksDirectory(cloneDirectory)
                : await ResolveCloneTasksDirectoryAsync(workspace.Id, workspace, targetDirectory, cancellationToken);

            string? normalizedCloneTasksDir = null;
            bool mirrorToClone = false;
            var cloneExistingFilesById = new Dictionary<int, string>();

            if (!string.IsNullOrWhiteSpace(cloneTasksDir))
            {
                try
                {
                    normalizedCloneTasksDir = Path.GetFullPath(cloneTasksDir).TrimEnd('/', '\\');
                    var normTasksDir = Path.GetFullPath(tasksDir).TrimEnd('/', '\\');
                    var normHostTasksDir = hostTasksDir != null ? Path.GetFullPath(hostTasksDir).TrimEnd('/', '\\') : null;

                    mirrorToClone = !string.Equals(normalizedCloneTasksDir, normTasksDir, StringComparison.OrdinalIgnoreCase) &&
                                    (normHostTasksDir == null || !string.Equals(normalizedCloneTasksDir, normHostTasksDir, StringComparison.OrdinalIgnoreCase));

                    if (mirrorToClone)
                    {
                        Directory.CreateDirectory(cloneTasksDir);

                        // Index existing clone markdown files once to avoid O(N^2) file reads and enable stale file cleanup
                        foreach (var file in Directory.GetFiles(cloneTasksDir, TaskSyncConstants.MarkdownPattern, SearchOption.TopDirectoryOnly))
                        {
                            try
                            {
                                var content = await File.ReadAllTextAsync(file, cancellationToken);
                                var (fm, _) = ParseMarkdown(content, file);
                                if (fm.Id.HasValue && fm.Id.Value > 0)
                                {
                                    cloneExistingFilesById[fm.Id.Value] = file;
                                }
                            }
                            catch (Exception)
                            {
                                // Ignore unparseable or locked candidate files
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    mirrorToClone = false;
                }
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

                if (mirrorToClone && normalizedCloneTasksDir != null)
                {
                    try
                    {
                        var cloneFile = Path.Combine(cloneTasksDir!, Path.GetFileName(targetFile));

                        // If an older file existed for this task under a different name, delete it to ensure exact parity
                        if (cloneExistingFilesById.TryGetValue(task.Id, out var oldCloneFile) &&
                            !string.Equals(Path.GetFullPath(oldCloneFile), Path.GetFullPath(cloneFile), StringComparison.OrdinalIgnoreCase) &&
                            File.Exists(oldCloneFile))
                        {
                            File.Delete(oldCloneFile);
                        }

                        _suppressedHashes[hash] = 1;
                        await File.WriteAllTextAsync(cloneFile, markdown.Replace("\r\n", "\n"), Encoding.UTF8, cancellationToken);
                        cloneExistingFilesById[task.Id] = cloneFile;
                    }
                    catch (Exception)
                    {
                        // Best effort mirror to clone
                    }
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
        // First check if an existing file already holds this task's ID
        if (Directory.Exists(tasksDir))
        {
            var files = Directory.GetFiles(tasksDir, TaskSyncConstants.MarkdownPattern, SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                string existingContent;
                try
                {
                    existingContent = await File.ReadAllTextAsync(file, cancellationToken);
                }
                catch (IOException ex)
                {
                    throw new TaskMarkdownSyncException($"Failed to read task markdown file '{file}': {ex.Message}", ex, file);
                }

                var (existingFm, _) = ParseMarkdown(existingContent, file);
                if (existingFm.Id == task.Id)
                {
                    return file;
                }
            }
        }

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
                            // 3-way check: A true conflict exists only if both DB and disk concurrently modified
                            // the same core specification fields (Title or Description) to conflicting values.
                            // Independent modifications to maintainer-controlled fields (e.g. MergeMode, Priority,
                            // Dependencies) are reconciled by giving authority to the database.
                            bool titleConflict = HasTitleConflict(dbTask, baseline, item.Frontmatter);
                            bool descConflict = HasDescriptionConflict(dbTask, baseline, item.Description);

                            if (titleConflict || descConflict)
                            {
                                throw new TaskConcurrencyException(taskId, baseline.Version, dbTask.Version);
                            }
                        }
                    }

                    // Protected status check: When syncing from an agent worktree (baselineSnapshot != null),
                    // status transitions are strictly governed by workflow gates and the orchestrator, not the agent.
                    // If an agent modified the frontmatter status (e.g. setting 'In_Review' or 'Completed'),
                    // we ignore the frontmatter status change rather than aborting the pipeline execution.
                    // For direct manual host edits (baselineSnapshot == null), status cannot be altered via markdown.
                    if (!string.IsNullOrWhiteSpace(item.Frontmatter.Status))
                    {
                        if (baselineSnapshot == null)
                        {
                            var referenceStatus = dbTask.Status.ToString();
                            if (!string.Equals(item.Frontmatter.Status.Trim(), referenceStatus, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new TaskMarkdownSyncException(
                                    $"Modifying task status via markdown is forbidden. Task #{taskId} status is '{referenceStatus}', but markdown specifies '{item.Frontmatter.Status}'. Status transitions must go through workflow gates.",
                                    item.FilePath,
                                    taskId);
                            }
                        }
                        else
                        {
                            var referenceStatus = baselineSnapshot.TryGetValue(taskId, out var statusBaseline) &&
                                                  !string.IsNullOrWhiteSpace(statusBaseline.Frontmatter.Status)
                                ? statusBaseline.Frontmatter.Status.Trim()
                                : dbTask.Status.ToString();

                            if (!string.Equals(item.Frontmatter.Status.Trim(), referenceStatus, StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogInformation(
                                    "Task #{TaskId} markdown frontmatter specifies status '{MarkdownStatus}', which differs from authoritative status '{DbStatus}'. Ignoring frontmatter status as workflow gates govern lifecycle.",
                                    taskId,
                                    item.Frontmatter.Status.Trim(),
                                    referenceStatus);
                            }
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
            // Build lookup index for existing tasks in workspace
            var existingTaskLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dbId, dbTask) in dbTasks)
            {
                existingTaskLookup[dbId.ToString()] = dbId;
                if (!string.IsNullOrWhiteSpace(dbTask.Title))
                {
                    existingTaskLookup[dbTask.Title.Trim()] = dbId;
                    var slug = NormalizeDependencyKey(dbTask.Title);
                    if (!string.IsNullOrEmpty(slug))
                    {
                        existingTaskLookup[slug] = dbId;
                    }
                }
            }

            var existingFiles = parsedFiles
                .Where(p => p.Frontmatter.Id.HasValue && p.Frontmatter.Id.Value > 0)
                .ToList();

            // Also map existing files on disk to their task IDs
            foreach (var p in existingFiles)
            {
                var id = p.Frontmatter.Id!.Value;
                var fileName = Path.GetFileName(p.FilePath);
                existingTaskLookup[fileName] = id;
                existingTaskLookup[Path.GetFileNameWithoutExtension(p.FilePath)] = id;
                var fileSlug = NormalizeDependencyKey(fileName);
                if (!string.IsNullOrEmpty(fileSlug))
                {
                    existingTaskLookup[fileSlug] = id;
                }
            }

            // Index new tasks in current batch
            var newTasks = parsedFiles
                .Where(p => !p.Frontmatter.Id.HasValue || p.Frontmatter.Id.Value <= 0)
                .Select((item, idx) => (Index: idx, Item: item))
                .ToList();

            var newTasksLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var nt in newTasks)
            {
                var fileName = Path.GetFileName(nt.Item.FilePath);
                newTasksLookup[fileName] = nt.Index;
                newTasksLookup[Path.GetFileNameWithoutExtension(nt.Item.FilePath)] = nt.Index;
                var fileSlug = NormalizeDependencyKey(fileName);
                if (!string.IsNullOrEmpty(fileSlug))
                {
                    newTasksLookup[fileSlug] = nt.Index;
                }
                if (!string.IsNullOrWhiteSpace(nt.Item.Frontmatter.Title))
                {
                    newTasksLookup[nt.Item.Frontmatter.Title.Trim()] = nt.Index;
                    var titleSlug = NormalizeDependencyKey(nt.Item.Frontmatter.Title);
                    if (!string.IsNullOrEmpty(titleSlug))
                    {
                        newTasksLookup[titleSlug] = nt.Index;
                    }
                }
            }

            (int? ExistingTaskId, int? NewTaskIndex) ResolveBatchReference(object rawRef, string taskTitle, string filePath)
            {
                if (rawRef == null) return (null, null);

                if (rawRef is int intId)
                {
                    if (dbTasks.ContainsKey(intId)) return (intId, null);
                    if (existingTaskLookup.TryGetValue(intId.ToString(), out var mappedId)) return (mappedId, null);
                    throw new TaskMarkdownSyncException(
                        $"Task '{taskTitle}' in '{filePath}' depends on #{intId}, which does not exist in workspace #{workspace.Id}.",
                        filePath,
                        intId);
                }

                var str = rawRef.ToString()?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(str)) return (null, null);

                if (int.TryParse(str, out var parsedId))
                {
                    if (dbTasks.ContainsKey(parsedId)) return (parsedId, null);
                    if (existingTaskLookup.TryGetValue(str, out var mappedId)) return (mappedId, null);
                    throw new TaskMarkdownSyncException(
                        $"Task '{taskTitle}' in '{filePath}' depends on #{parsedId}, which does not exist in workspace #{workspace.Id}.",
                        filePath,
                        parsedId);
                }

                if (existingTaskLookup.TryGetValue(str, out var matchedExistingId) ||
                    existingTaskLookup.TryGetValue(NormalizeDependencyKey(str), out matchedExistingId))
                {
                    return (matchedExistingId, null);
                }

                if (newTasksLookup.TryGetValue(str, out var matchedNewIndex) ||
                    newTasksLookup.TryGetValue(NormalizeDependencyKey(str), out matchedNewIndex))
                {
                    return (null, matchedNewIndex);
                }

                throw new TaskMarkdownSyncException(
                    $"Task '{taskTitle}' in '{filePath}' depends on '{str}', which could not be resolved to any existing or pending task in workspace #{workspace.Id}.",
                    filePath);
            }

            var combinedGraph = new Dictionary<int, List<int>>();
            foreach (var (dbId, dbTask) in dbTasks)
            {
                combinedGraph[dbId] = dbTask.Dependencies.Select(d => d.DependsOnTaskId).ToList();
            }

            var resolvedExistingDeps = new Dictionary<int, (List<int> ExistingDepIds, List<int> NewDepIndices)>();
            foreach (var item in existingFiles)
            {
                var taskId = item.Frontmatter.Id!.Value;
                if (item.Frontmatter.DependsOn != null)
                {
                    var existingDepIds = new List<int>();
                    var newDepIndices = new List<int>();
                    var graphEdges = new List<int>();

                    foreach (var rawRef in item.Frontmatter.DependsOn)
                    {
                        var (resId, resNewIdx) = ResolveBatchReference(rawRef, item.Frontmatter.Title, item.FilePath);
                        if (resId.HasValue)
                        {
                            if (resId.Value == taskId)
                            {
                                throw new TaskMarkdownSyncException($"Task #{taskId} cannot depend on itself.", item.FilePath, taskId);
                            }
                            existingDepIds.Add(resId.Value);
                            graphEdges.Add(resId.Value);
                        }
                        else if (resNewIdx.HasValue)
                        {
                            newDepIndices.Add(resNewIdx.Value);
                            graphEdges.Add(-(resNewIdx.Value + 1));
                        }
                    }

                    resolvedExistingDeps[taskId] = (existingDepIds.Distinct().ToList(), newDepIndices.Distinct().ToList());
                    combinedGraph[taskId] = graphEdges.Distinct().ToList();
                }
            }

            var resolvedNewDeps = new Dictionary<int, (List<int> ExistingDepIds, List<int> NewDepIndices)>();
            foreach (var nt in newTasks)
            {
                var myNodeId = -(nt.Index + 1);
                var existingDepIds = new List<int>();
                var newDepIndices = new List<int>();
                var graphEdges = new List<int>();

                if (nt.Item.Frontmatter.DependsOn != null)
                {
                    foreach (var rawRef in nt.Item.Frontmatter.DependsOn)
                    {
                        var (resId, resNewIdx) = ResolveBatchReference(rawRef, nt.Item.Frontmatter.Title, nt.Item.FilePath);
                        if (resId.HasValue)
                        {
                            existingDepIds.Add(resId.Value);
                            graphEdges.Add(resId.Value);
                        }
                        else if (resNewIdx.HasValue)
                        {
                            if (resNewIdx.Value == nt.Index)
                            {
                                throw new TaskMarkdownSyncException(
                                    $"New task '{nt.Item.Frontmatter.Title}' in '{nt.Item.FilePath}' cannot depend on itself.",
                                    nt.Item.FilePath);
                            }
                            newDepIndices.Add(resNewIdx.Value);
                            graphEdges.Add(-(resNewIdx.Value + 1));
                        }
                    }
                }

                resolvedNewDeps[nt.Index] = (existingDepIds.Distinct().ToList(), newDepIndices.Distinct().ToList());
                combinedGraph[myNodeId] = graphEdges.Distinct().ToList();
            }

            // Cycle detection across the unified graph
            var visited = new Dictionary<int, int>();
            foreach (var nodeId in combinedGraph.Keys)
            {
                if (!visited.TryGetValue(nodeId, out var state) || state == 0)
                {
                    var stack = new List<int>();
                    if (HasCycleDfs(nodeId, combinedGraph, visited, stack))
                    {
                        string FormatNodeName(int nId)
                        {
                            if (nId > 0)
                            {
                                return dbTasks.TryGetValue(nId, out var t) ? $"#{nId} ('{t.Title}')" : $"#{nId}";
                            }
                            var idx = -(nId + 1);
                            return $"'{newTasks[idx].Item.Frontmatter.Title}'";
                        }

                        var cyclePath = string.Join(" -> ", stack.Select(FormatNodeName));
                        string? matchingFile = null;
                        int? cycleTaskId = null;
                        if (nodeId > 0)
                        {
                            cycleTaskId = nodeId;
                            matchingFile = parsedFiles.FirstOrDefault(p => p.Frontmatter.Id == nodeId).FilePath;
                        }
                        else
                        {
                            var newIdx = -(nodeId + 1);
                            matchingFile = newTasks[newIdx].Item.FilePath;
                        }

                        throw new TaskMarkdownSyncException(
                            $"Circular dependency detected in workspace #{workspace.Id}: {cyclePath}",
                            matchingFile,
                            cycleTaskId);
                    }
                }
            }

            // 3. Topological ordering for new tasks
            var orderedNewTaskIndices = new List<int>();
            var newVisited = new HashSet<int>();

            void VisitNewTask(int idx)
            {
                if (newVisited.Contains(idx)) return;
                newVisited.Add(idx);

                var prereqIndices = resolvedNewDeps[idx].NewDepIndices;
                foreach (var pIdx in prereqIndices)
                {
                    VisitNewTask(pIdx);
                }
                orderedNewTaskIndices.Add(idx);
            }

            for (int i = 0; i < newTasks.Count; i++)
            {
                VisitNewTask(i);
            }

            var refreshedTasks = new List<ForgeTask>();
            var newTaskIdMap = new Dictionary<int, int>();

            // 4. Create new tasks in topological order
            foreach (var newIdx in orderedNewTaskIndices)
            {
                var nt = newTasks[newIdx];
                var (existingDepIds, newDepIndices) = resolvedNewDeps[newIdx];
                var finalDepIds = new List<int>(existingDepIds);
                foreach (var prereqNewIdx in newDepIndices)
                {
                    finalDepIds.Add(newTaskIdMap[prereqNewIdx]);
                }
                finalDepIds = finalDepIds.Distinct().ToList();

                var mergeMode = string.Equals(nt.Item.Frontmatter.MergeMode, "automatic", StringComparison.OrdinalIgnoreCase)
                    ? Domain.MergeMode.Automatic
                    : Domain.MergeMode.ManualReviewRequired;

                var newTask = new ForgeTask
                {
                    WorkspaceId = workspace.Id,
                    Title = nt.Item.Frontmatter.Title.Trim(),
                    Description = !string.IsNullOrWhiteSpace(nt.Item.Description) ? nt.Item.Description : nt.Item.Body,
                    Priority = nt.Item.Frontmatter.Priority > 0 ? nt.Item.Frontmatter.Priority : TaskSyncConstants.DefaultPriority,
                    MergeMode = mergeMode,
                    IsWaitingForHuman = nt.Item.Frontmatter.WaitingForHuman,
                    BaseBranch = !string.IsNullOrWhiteSpace(nt.Item.Frontmatter.BaseBranch) ? nt.Item.Frontmatter.BaseBranch : (workspace.DefaultBaseBranch ?? WorkflowDefaults.DefaultBaseBranch),
                    FeatureBranch = nt.Item.Frontmatter.FeatureBranch ?? string.Empty,
                    Status = TaskOverallStatus.Suggested,
                    Origin = ParseOrigin(nt.Item.Frontmatter.Origin),
                    CurrentStageId = string.Empty,
                    CreatedAt = nt.Item.Frontmatter.CreatedAt ?? TryExtractDateFromPath(nt.Item.FilePath) ?? DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Version = 1
                };

                var created = await taskRepo.CreateWithBaselineAsync(
                    newTask,
                    finalDepIds,
                    changeActor,
                    changeReason ?? TaskSyncConstants.TaskCreatedFromDisk,
                    cancellationToken);

                newTaskIdMap[newIdx] = created.Id;

                nt.Item.Frontmatter.Id = created.Id;
                nt.Item.Frontmatter.CreatedAt = created.CreatedAt;
                nt.Item.Frontmatter.Status = created.Status.ToString();
                nt.Item.Frontmatter.Origin = FormatOrigin(created.Origin);
                nt.Item.Frontmatter.MergeMode = created.MergeMode == Domain.MergeMode.Automatic ? "automatic" : "manual_review_required";
                nt.Item.Frontmatter.WaitingForHuman = created.IsWaitingForHuman;
                nt.Item.Frontmatter.CurrentStage = created.CurrentStageId;
                nt.Item.Frontmatter.DependsOn = new TaskDependencyReferenceList(finalDepIds);

                var rewrittenMarkdown = GenerateMarkdown(created, finalDepIds, nt.Item.Frontmatter.AgentProfile);
                var rewrittenHash = ComputeContentHash(rewrittenMarkdown);
                _suppressedHashes[rewrittenHash] = 1;

                var datePrefix = (created.CreatedAt != default ? created.CreatedAt : DateTime.UtcNow).ToString("yyyy-MM-dd");
                var slug = GenerateSlug(created.Title);
                var newFileName = $"{datePrefix}-{slug}{TaskSyncConstants.MarkdownExtension}";
                var newFilePath = Path.Combine(tasksDir, newFileName);

                int suffix = 2;
                while (File.Exists(newFilePath) && !string.Equals(newFilePath, nt.Item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    newFilePath = Path.Combine(tasksDir, $"{datePrefix}-{slug}-{suffix++}{TaskSyncConstants.MarkdownExtension}");
                }

                if (!string.Equals(newFilePath, nt.Item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(nt.Item.FilePath))
                    {
                        File.Delete(nt.Item.FilePath);
                    }
                }

                await File.WriteAllTextAsync(newFilePath, rewrittenMarkdown.Replace("\r\n", "\n"), Encoding.UTF8, cancellationToken);

                if (hostTasksDir != null)
                {
                    var oldHostFile = Path.Combine(hostTasksDir, Path.GetFileName(nt.Item.FilePath));
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

            // 5. Update existing tasks
            foreach (var item in existingFiles)
            {
                var taskId = item.Frontmatter.Id!.Value;
                var task = dbTasks[taskId];

                // If syncing against a baseline snapshot (e.g. from an agent worktree) and the file has not changed on disk,
                // do not clobber any database changes that happened concurrently during the stage execution.
                if (baselineSnapshot != null && baselineSnapshot.TryGetValue(taskId, out var baseline) &&
                    string.Equals(item.Hash, baseline.ContentHash, StringComparison.Ordinal))
                {
                    refreshedTasks.Add(task);
                    continue;
                }

                string newTitle;
                string newDesc;
                int newPriority;
                Domain.MergeMode newMergeMode;
                string newBaseBranch;
                List<int>? finalDepIds = null;

                if (resolvedExistingDeps.TryGetValue(taskId, out var resolved))
                {
                    finalDepIds = new List<int>(resolved.ExistingDepIds);
                    foreach (var newIdx in resolved.NewDepIndices)
                    {
                        finalDepIds.Add(newTaskIdMap[newIdx]);
                    }
                    finalDepIds = finalDepIds.Distinct().ToList();
                }

                if (baselineSnapshot != null && baselineSnapshot.TryGetValue(taskId, out baseline) && baseline != null)
                {
                    // 3-way merge between Baseline, DB (authoritative maintainer fields), and Disk (agent modifications):
                    // 1. Title: disk modification takes effect only if disk altered it relative to baseline
                    bool diskTitleChanged = !string.IsNullOrWhiteSpace(item.Frontmatter.Title) &&
                        !string.Equals(item.Frontmatter.Title.Trim(), baseline.Frontmatter.Title?.Trim(), StringComparison.Ordinal);
                    newTitle = diskTitleChanged ? item.Frontmatter.Title!.Trim() : (task.Title ?? string.Empty);

                    // 2. Description: disk modification takes effect only if disk altered it relative to baseline
                    bool diskDescChanged = !string.IsNullOrWhiteSpace(item.Description) &&
                        !string.Equals(NormalizeLineEndings(item.Description)?.Trim(), NormalizeLineEndings(baseline.Description)?.Trim(), StringComparison.Ordinal);
                    newDesc = diskDescChanged ? item.Description : task.Description;

                    // 3. Priority: DB takes precedence if DB modified priority; otherwise disk modification takes effect if disk altered it relative to baseline
                    bool dbPriorityChanged = task.Priority != baseline.Frontmatter.Priority;
                    bool diskPriorityChanged = item.Frontmatter.Priority > 0 &&
                        item.Frontmatter.Priority != baseline.Frontmatter.Priority;
                    newPriority = dbPriorityChanged ? task.Priority : (diskPriorityChanged ? item.Frontmatter.Priority : task.Priority);

                    // 4. MergeMode: disk modification takes effect only if disk altered it relative to baseline
                    var diskMergeMode = ParseMergeMode(item.Frontmatter.MergeMode);
                    var baselineMergeMode = ParseMergeMode(baseline.Frontmatter.MergeMode) ?? Domain.MergeMode.ManualReviewRequired;
                    bool diskMergeModeChanged = diskMergeMode.HasValue && diskMergeMode != baselineMergeMode;
                    newMergeMode = diskMergeModeChanged && diskMergeMode.HasValue ? diskMergeMode.Value : task.MergeMode;

                    // 5. BaseBranch: DB takes precedence if DB modified base branch; otherwise disk modification takes effect if disk altered it relative to baseline
                    var fallbackBaseBranch = task.BaseBranch ?? workspace.DefaultBaseBranch ?? WorkflowDefaults.DefaultBaseBranch;
                    bool dbBaseBranchChanged = !string.Equals(task.BaseBranch?.Trim(), baseline.Frontmatter.BaseBranch?.Trim(), StringComparison.Ordinal);
                    bool diskBaseBranchChanged = !string.IsNullOrWhiteSpace(item.Frontmatter.BaseBranch) &&
                        !string.Equals(item.Frontmatter.BaseBranch.Trim(), baseline.Frontmatter.BaseBranch?.Trim(), StringComparison.Ordinal);
                    newBaseBranch = dbBaseBranchChanged ? fallbackBaseBranch : (diskBaseBranchChanged ? item.Frontmatter.BaseBranch!.Trim() : fallbackBaseBranch);

                    // 6. Dependencies: if disk frontmatter did not alter dependencies relative to baseline, preserve DB dependencies
                    if (finalDepIds != null)
                    {
                        var baselineDepIds = baseline.Frontmatter.DependsOn?
                            .Select(r => r is int i ? i : (int.TryParse(r.ToString(), out var parsed) ? parsed : (int?)null))
                            .Where(id => id.HasValue)
                            .Select(id => id!.Value)
                            .OrderBy(x => x)
                            .ToList() ?? new List<int>();

                        bool diskDepsChanged = !finalDepIds.OrderBy(x => x).SequenceEqual(baselineDepIds);
                        if (!diskDepsChanged)
                        {
                            finalDepIds = null;
                        }
                    }
                }
                else
                {
                    newTitle = !string.IsNullOrWhiteSpace(item.Frontmatter.Title) ? item.Frontmatter.Title.Trim() : (task.Title ?? string.Empty);
                    newDesc = !string.IsNullOrWhiteSpace(item.Description) ? item.Description : task.Description;
                    newPriority = item.Frontmatter.Priority > 0 ? item.Frontmatter.Priority : task.Priority;
                    newMergeMode = ParseMergeMode(item.Frontmatter.MergeMode) ?? task.MergeMode;
                    newBaseBranch = !string.IsNullOrWhiteSpace(item.Frontmatter.BaseBranch) ? item.Frontmatter.BaseBranch.Trim() : task.BaseBranch;
                }

                var newOrigin = ParseOrigin(item.Frontmatter.Origin);

                bool titleChanged = !string.Equals(newTitle, task.Title, StringComparison.Ordinal);
                bool descChanged = !string.Equals(newDesc, task.Description, StringComparison.Ordinal);
                bool priorityChanged = newPriority != task.Priority;
                bool mergeModeChanged = newMergeMode != task.MergeMode;
                bool baseBranchChanged = !string.Equals(newBaseBranch, task.BaseBranch, StringComparison.Ordinal);
                bool originChanged = newOrigin != task.Origin;

                var currentDeps = task.Dependencies?.Select(d => d.DependsOnTaskId).OrderBy(x => x).ToList() ?? new List<int>();
                bool depsChanged = finalDepIds != null && !finalDepIds.OrderBy(x => x).SequenceEqual(currentDeps);

                if (titleChanged || descChanged || priorityChanged || depsChanged || mergeModeChanged || baseBranchChanged || originChanged)
                {
                    task.Title = newTitle;
                    task.Description = newDesc;
                    task.Priority = newPriority;
                    task.MergeMode = newMergeMode;
                    task.BaseBranch = newBaseBranch;
                    if (originChanged)
                    {
                        task.Origin = newOrigin;
                    }
                    if (!string.IsNullOrWhiteSpace(item.Frontmatter.FeatureBranch))
                    {
                        task.FeatureBranch = item.Frontmatter.FeatureBranch;
                    }
                    task.UpdatedAt = DateTime.UtcNow;

                    await taskRepo.UpdateWithHistoryAsync(
                        task,
                        finalDepIds,
                        changeActor,
                        changeReason ?? TaskSyncConstants.DiskModificationsSync,
                        cancellationToken);

                    if (finalDepIds != null)
                    {
                        task.Dependencies = finalDepIds.Select(depId => new TaskDependency
                        {
                            TaskId = task.Id,
                            DependsOnTaskId = depId
                        }).ToList();
                    }
                }

                // In-place normalization for existing files if depends_on changed, agent tampered with status, or maintainer fields were reconciled
                bool statusTampered = baselineSnapshot != null &&
                                      !string.IsNullOrWhiteSpace(item.Frontmatter.Status) &&
                                      !string.Equals(item.Frontmatter.Status.Trim(), task.Status.ToString(), StringComparison.OrdinalIgnoreCase);

                var effectiveDiskMergeMode = ParseMergeMode(item.Frontmatter.MergeMode) ?? Domain.MergeMode.ManualReviewRequired;
                var effectiveDiskPriority = item.Frontmatter.Priority > 0 ? item.Frontmatter.Priority : TaskSyncConstants.DefaultPriority;
                var effectiveDiskBaseBranch = !string.IsNullOrWhiteSpace(item.Frontmatter.BaseBranch) ? item.Frontmatter.BaseBranch.Trim() : (workspace.DefaultBaseBranch ?? WorkflowDefaults.DefaultBaseBranch);

                var currentDbDepIds = task.Dependencies?.Select(d => d.DependsOnTaskId).OrderBy(x => x).ToList() ?? new List<int>();
                var currentDiskDepIds = (finalDepIds ?? (resolvedExistingDeps.TryGetValue(taskId, out var resDeps)
                    ? resDeps.ExistingDepIds
                    : new List<int>())).OrderBy(x => x).ToList();
                bool depsDifferFromDisk = !currentDiskDepIds.SequenceEqual(currentDbDepIds);

                bool maintainerFieldsSynced = baselineSnapshot != null &&
                                              (task.MergeMode != effectiveDiskMergeMode ||
                                               task.Priority != effectiveDiskPriority ||
                                               !string.Equals(task.BaseBranch, effectiveDiskBaseBranch, StringComparison.Ordinal) ||
                                               !string.Equals(task.Title?.Trim(), item.Frontmatter.Title?.Trim(), StringComparison.Ordinal) ||
                                               depsDifferFromDisk);

                if (finalDepIds != null || statusTampered || maintainerFieldsSynced)
                {
                    var effectiveDeps = finalDepIds ?? task.Dependencies?.Select(d => d.DependsOnTaskId).ToList() ?? new List<int>();
                    var normalizedMarkdown = GenerateMarkdown(task, effectiveDeps, item.Frontmatter.AgentProfile);
                    var normHash = ComputeContentHash(normalizedMarkdown);
                    _suppressedHashes[normHash] = 1;
                    await File.WriteAllTextAsync(item.FilePath, normalizedMarkdown.Replace("\r\n", "\n"), Encoding.UTF8, cancellationToken);

                    if (hostTasksDir != null)
                    {
                        var hostFile = Path.Combine(hostTasksDir, Path.GetFileName(item.FilePath));
                        _suppressedHashes[normHash] = 1;
                        await File.WriteAllTextAsync(hostFile, normalizedMarkdown.Replace("\r\n", "\n"), Encoding.UTF8, cancellationToken);
                    }
                }
                else if (hostTasksDir != null)
                {
                    var hostFile = Path.Combine(hostTasksDir, Path.GetFileName(item.FilePath));
                    _suppressedHashes[item.Hash] = 1;
                    await File.WriteAllTextAsync(hostFile, item.Content.Replace("\r\n", "\n"), Encoding.UTF8, cancellationToken);
                }

                refreshedTasks.Add(task);
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

    private async Task<List<int>> ResolveDependencyReferencesAsync(
        ITaskRepository taskRepo,
        int workspaceId,
        IEnumerable<object> dependsOn,
        string taskTitle,
        string filePath,
        int? currentTaskId,
        CancellationToken cancellationToken)
    {
        var dbTasksList = await taskRepo.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        var dbTasks = dbTasksList.ToDictionary(t => t.Id);

        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dbId, dbTask) in dbTasks)
        {
            lookup[dbId.ToString()] = dbId;
            if (!string.IsNullOrWhiteSpace(dbTask.Title))
            {
                lookup[dbTask.Title.Trim()] = dbId;
                var slug = NormalizeDependencyKey(dbTask.Title);
                if (!string.IsNullOrEmpty(slug))
                {
                    lookup[slug] = dbId;
                }
            }
        }

        // If filePath is provided and in a directory, index existing files in the same directory
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                var files = Directory.GetFiles(dir, TaskSyncConstants.MarkdownPattern, SearchOption.TopDirectoryOnly);
                foreach (var file in files)
                {
                    if (string.Equals(file, filePath, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var content = await File.ReadAllTextAsync(file, cancellationToken);
                        var (fm, _) = ParseMarkdown(content, file);
                        if (fm.Id.HasValue && fm.Id.Value > 0 && dbTasks.ContainsKey(fm.Id.Value))
                        {
                            var id = fm.Id.Value;
                            var fileName = Path.GetFileName(file);
                            lookup[fileName] = id;
                            lookup[Path.GetFileNameWithoutExtension(file)] = id;
                            var fileSlug = NormalizeDependencyKey(fileName);
                            if (!string.IsNullOrEmpty(fileSlug))
                            {
                                lookup[fileSlug] = id;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Ignore unreadable sibling files during resolution
                    }
                }
            }
        }

        var resolvedIds = new List<int>();
        foreach (var rawRef in dependsOn)
        {
            if (rawRef == null) continue;

            if (rawRef is int intId)
            {
                if (currentTaskId.HasValue && intId == currentTaskId.Value)
                {
                    throw new TaskMarkdownSyncException($"Task #{currentTaskId.Value} cannot depend on itself.", filePath, currentTaskId.Value);
                }
                if (dbTasks.ContainsKey(intId))
                {
                    resolvedIds.Add(intId);
                    continue;
                }
                if (lookup.TryGetValue(intId.ToString(), out var mappedId))
                {
                    if (currentTaskId.HasValue && mappedId == currentTaskId.Value)
                    {
                        throw new TaskMarkdownSyncException($"Task #{currentTaskId.Value} cannot depend on itself.", filePath, currentTaskId.Value);
                    }
                    resolvedIds.Add(mappedId);
                    continue;
                }
                throw new TaskMarkdownSyncException(
                    $"Task '{taskTitle}' in '{filePath}' depends on #{intId}, which does not exist in workspace #{workspaceId}.",
                    filePath,
                    intId);
            }

            var str = rawRef.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(str)) continue;

            // Check self-dependency by title or filename
            if (!string.IsNullOrWhiteSpace(taskTitle) &&
                (string.Equals(str, taskTitle, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(NormalizeDependencyKey(str), NormalizeDependencyKey(taskTitle), StringComparison.OrdinalIgnoreCase)))
            {
                throw new TaskMarkdownSyncException(
                    currentTaskId.HasValue ? $"Task #{currentTaskId.Value} cannot depend on itself." : $"New task '{taskTitle}' in '{filePath}' cannot depend on itself.",
                    filePath,
                    currentTaskId);
            }
            if (!string.IsNullOrWhiteSpace(filePath) &&
                (string.Equals(str, Path.GetFileName(filePath), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(str, Path.GetFileNameWithoutExtension(filePath), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(NormalizeDependencyKey(str), NormalizeDependencyKey(Path.GetFileName(filePath)), StringComparison.OrdinalIgnoreCase)))
            {
                throw new TaskMarkdownSyncException(
                    currentTaskId.HasValue ? $"Task #{currentTaskId.Value} cannot depend on itself." : $"New task '{taskTitle}' in '{filePath}' cannot depend on itself.",
                    filePath,
                    currentTaskId);
            }

            if (int.TryParse(str, out var parsedId))
            {
                if (currentTaskId.HasValue && parsedId == currentTaskId.Value)
                {
                    throw new TaskMarkdownSyncException($"Task #{currentTaskId.Value} cannot depend on itself.", filePath, currentTaskId.Value);
                }
                if (dbTasks.ContainsKey(parsedId))
                {
                    resolvedIds.Add(parsedId);
                    continue;
                }
                if (lookup.TryGetValue(str, out var mappedId))
                {
                    if (currentTaskId.HasValue && mappedId == currentTaskId.Value)
                    {
                        throw new TaskMarkdownSyncException($"Task #{currentTaskId.Value} cannot depend on itself.", filePath, currentTaskId.Value);
                    }
                    resolvedIds.Add(mappedId);
                    continue;
                }
                throw new TaskMarkdownSyncException(
                    $"Task '{taskTitle}' in '{filePath}' depends on #{parsedId}, which does not exist in workspace #{workspaceId}.",
                    filePath,
                    parsedId);
            }

            if (lookup.TryGetValue(str, out var matchedId) ||
                lookup.TryGetValue(NormalizeDependencyKey(str), out matchedId))
            {
                if (currentTaskId.HasValue && matchedId == currentTaskId.Value)
                {
                    throw new TaskMarkdownSyncException($"Task #{currentTaskId.Value} cannot depend on itself.", filePath, currentTaskId.Value);
                }
                resolvedIds.Add(matchedId);
                continue;
            }

            throw new TaskMarkdownSyncException(
                $"Task '{taskTitle}' in '{filePath}' depends on '{str}', which could not be resolved to any existing task in workspace #{workspaceId}.",
                filePath);
        }

        return resolvedIds.Distinct().ToList();
    }

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

                    List<int>? resolvedDeps = null;
                    if (frontmatter.DependsOn != null)
                    {
                        resolvedDeps = await ResolveDependencyReferencesAsync(
                            taskRepo,
                            workspaceId,
                            frontmatter.DependsOn,
                            newTitle,
                            filePath,
                            currentTaskId: task.Id,
                            cancellationToken);
                    }

                    if (depEngine != null && resolvedDeps != null)
                    {
                        var workspaceDeps = await taskRepo.GetWorkspaceDependenciesAsync(workspaceId, cancellationToken);
                        var candidatePairs = new List<(int TaskId, int DependsOnTaskId)>();
                        foreach (var (tId, dIds) in workspaceDeps)
                        {
                            if (tId == task.Id) continue;
                            foreach (var d in dIds)
                            {
                                candidatePairs.Add((tId, d));
                            }
                        }
                        foreach (var d in resolvedDeps)
                        {
                            candidatePairs.Add((task.Id, d));
                        }

                        if (depEngine.HasCycle(workspaceId, candidatePairs))
                        {
                            throw new TaskMarkdownSyncException(
                                $"Circular dependency detected when updating task #{task.Id}.",
                                filePath,
                                task.Id);
                        }
                    }

                    var newMergeMode = string.IsNullOrWhiteSpace(frontmatter.MergeMode)
                        ? task.MergeMode
                        : (string.Equals(frontmatter.MergeMode, "automatic", StringComparison.OrdinalIgnoreCase)
                            ? Domain.MergeMode.Automatic
                            : Domain.MergeMode.ManualReviewRequired);
                    var newBaseBranch = !string.IsNullOrWhiteSpace(frontmatter.BaseBranch) ? frontmatter.BaseBranch.Trim() : task.BaseBranch;
                    var newOrigin = ParseOrigin(frontmatter.Origin);

                    var currentDeps = task.Dependencies.Select(d => d.DependsOnTaskId).OrderBy(x => x).ToList();
                    bool depsChanged = resolvedDeps != null && !resolvedDeps.OrderBy(x => x).SequenceEqual(currentDeps);

                    bool titleChanged = !string.Equals(newTitle, task.Title, StringComparison.Ordinal);
                    bool descChanged = !string.Equals(newDesc, task.Description, StringComparison.Ordinal);
                    bool priorityChanged = newPriority != task.Priority;
                    bool mergeModeChanged = newMergeMode != task.MergeMode;
                    bool baseBranchChanged = !string.Equals(newBaseBranch, task.BaseBranch, StringComparison.Ordinal);
                    bool originChanged = newOrigin != task.Origin;

                    if (titleChanged || descChanged || priorityChanged || depsChanged || mergeModeChanged || baseBranchChanged || originChanged)
                    {
                        task.Title = newTitle;
                        task.Description = newDesc;
                        task.Priority = newPriority;
                        task.MergeMode = newMergeMode;
                        task.BaseBranch = newBaseBranch;
                        if (originChanged)
                        {
                            task.Origin = newOrigin;
                        }
                        if (!string.IsNullOrWhiteSpace(frontmatter.FeatureBranch))
                        {
                            task.FeatureBranch = frontmatter.FeatureBranch;
                        }
                        task.UpdatedAt = DateTime.UtcNow;

                        await taskRepo.UpdateWithHistoryAsync(
                            task,
                            resolvedDeps,
                            AuditActors.User,
                            TaskSyncConstants.DiskModificationsSync,
                            cancellationToken);
                    }

                    // In-place normalization for existing files if depends_on changed or had string references
                    if (resolvedDeps != null && File.Exists(filePath))
                    {
                        frontmatter.DependsOn = new TaskDependencyReferenceList(resolvedDeps);
                        var normalizedMarkdown = GenerateMarkdown(task, resolvedDeps, frontmatter.AgentProfile);
                        var normHash = ComputeContentHash(normalizedMarkdown);
                        _suppressedHashes[normHash] = 1;
                        await File.WriteAllTextAsync(filePath, normalizedMarkdown.Replace("\r\n", "\n"), Encoding.UTF8, cancellationToken);
                    }

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

            List<int>? newResolvedDeps = null;
            if (frontmatter.DependsOn != null)
            {
                newResolvedDeps = await ResolveDependencyReferencesAsync(
                    taskRepo,
                    workspaceId,
                    frontmatter.DependsOn,
                    frontmatter.Title,
                    filePath,
                    currentTaskId: null,
                    cancellationToken);
            }

            var mergeMode = string.Equals(frontmatter.MergeMode, "automatic", StringComparison.OrdinalIgnoreCase)
                ? Domain.MergeMode.Automatic
                : Domain.MergeMode.ManualReviewRequired;

            string defaultBaseBranch = WorkflowDefaults.DefaultBaseBranch;
            if (string.IsNullOrWhiteSpace(frontmatter.BaseBranch))
            {
                var workspaceRepo = scope.ServiceProvider.GetService<IWorkspaceRepository>();
                if (workspaceRepo != null)
                {
                    var ws = await workspaceRepo.GetByIdAsync(workspaceId, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(ws?.DefaultBaseBranch))
                    {
                        defaultBaseBranch = ws.DefaultBaseBranch;
                    }
                }
            }

            var newTask = new ForgeTask
            {
                WorkspaceId = workspaceId,
                Title = frontmatter.Title.Trim(),
                Description = !string.IsNullOrWhiteSpace(description) ? description : body,
                Priority = frontmatter.Priority > 0 ? frontmatter.Priority : TaskSyncConstants.DefaultPriority,
                MergeMode = mergeMode,
                IsWaitingForHuman = frontmatter.WaitingForHuman,
                BaseBranch = string.IsNullOrWhiteSpace(frontmatter.BaseBranch) ? defaultBaseBranch : frontmatter.BaseBranch.Trim(),
                Status = TaskOverallStatus.Suggested,
                Origin = ParseOrigin(frontmatter.Origin),
                CurrentStageId = string.Empty,
                CreatedAt = frontmatter.CreatedAt ?? TryExtractDateFromPath(filePath) ?? DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Version = 1
            };

            var created = await taskRepo.CreateWithBaselineAsync(
                newTask,
                newResolvedDeps,
                AuditActors.User,
                TaskSyncConstants.TaskCreatedFromDisk,
                cancellationToken);

            frontmatter.Id = created.Id;
            frontmatter.CreatedAt = created.CreatedAt;
            frontmatter.Status = created.Status.ToString();
            frontmatter.Origin = FormatOrigin(created.Origin);
            frontmatter.MergeMode = created.MergeMode == Domain.MergeMode.Automatic ? "automatic" : "manual_review_required";
            frontmatter.WaitingForHuman = created.IsWaitingForHuman;
            frontmatter.CurrentStage = created.CurrentStageId;
            frontmatter.DependsOn = newResolvedDeps != null ? new TaskDependencyReferenceList(newResolvedDeps) : null;

            var inPlaceMarkdown = GenerateMarkdown(created, newResolvedDeps, frontmatter.AgentProfile);
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

    public Task ArchiveOrCleanupTaskFileAsync(ForgeTask task, string workspacePath, bool archive = true, CancellationToken cancellationToken = default)
        => ArchiveOrCleanupTaskFileAsync(task, workspacePath, archive, cloneDirectory: null, cancellationToken);

    public async Task ArchiveOrCleanupTaskFileAsync(ForgeTask task, string workspacePath, bool archive, string? cloneDirectory, CancellationToken cancellationToken = default)
    {
        var tasksDir = Path.Combine(workspacePath, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        await ArchiveOrCleanupInDirectoryAsync(tasksDir, task.Id, archive, cancellationToken);

        var cloneTasksDir = !string.IsNullOrWhiteSpace(cloneDirectory)
            ? NormalizeCloneTasksDirectory(cloneDirectory)
            : await ResolveCloneTasksDirectoryAsync(task.WorkspaceId, task.Workspace, workspacePath, cancellationToken);

        if (!string.IsNullOrWhiteSpace(cloneTasksDir))
        {
            try
            {
                var normClone = Path.GetFullPath(cloneTasksDir).TrimEnd('/', '\\');
                var normTasks = Path.GetFullPath(tasksDir).TrimEnd('/', '\\');
                if (!string.Equals(normClone, normTasks, StringComparison.OrdinalIgnoreCase))
                {
                    await ArchiveOrCleanupInDirectoryAsync(cloneTasksDir, task.Id, archive, cancellationToken);
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup in clone tasks directory
            }
        }
    }

    private async Task ArchiveOrCleanupInDirectoryAsync(string tasksDir, int taskId, bool archive, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(tasksDir)) return;

        var files = Directory.GetFiles(tasksDir, TaskSyncConstants.MarkdownPattern);
        foreach (var file in files)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file, cancellationToken);
                var (fm, _) = ParseMarkdown(content, file);
                if (fm.Id == taskId)
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


    private static bool HasTitleConflict(ForgeTask dbTask, TaskBaselineEntry baseline, TaskFrontmatter diskFrontmatter)
    {
        var baselineTitle = baseline.Frontmatter.Title?.Trim() ?? string.Empty;
        var dbTitle = dbTask.Title?.Trim() ?? string.Empty;
        var diskTitle = !string.IsNullOrWhiteSpace(diskFrontmatter.Title) ? diskFrontmatter.Title.Trim() : null;

        bool dbTitleChanged = !string.Equals(dbTitle, baselineTitle, StringComparison.Ordinal);
        bool diskTitleChanged = diskTitle != null && !string.Equals(diskTitle, baselineTitle, StringComparison.Ordinal);

        return dbTitleChanged && diskTitleChanged && !string.Equals(dbTitle, diskTitle, StringComparison.Ordinal);
    }

    private static bool HasDescriptionConflict(ForgeTask dbTask, TaskBaselineEntry baseline, string? diskDescription)
    {
        var baselineDesc = NormalizeLineEndings(baseline.Description)?.Trim() ?? string.Empty;
        var dbDesc = NormalizeLineEndings(dbTask.Description)?.Trim() ?? string.Empty;
        var diskDesc = !string.IsNullOrWhiteSpace(diskDescription) ? NormalizeLineEndings(diskDescription)?.Trim() : null;

        bool dbDescChanged = !string.Equals(dbDesc, baselineDesc, StringComparison.Ordinal);
        bool diskDescChanged = diskDesc != null && !string.Equals(diskDesc, baselineDesc, StringComparison.Ordinal);

        return dbDescChanged && diskDescChanged && !string.Equals(dbDesc, diskDesc, StringComparison.Ordinal);
    }

    private static string? NormalizeLineEndings(string? text)
    {
        return text?.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static Domain.MergeMode? ParseMergeMode(string? mergeModeStr)
    {
        if (string.IsNullOrWhiteSpace(mergeModeStr)) return null;
        return string.Equals(mergeModeStr.Trim(), "automatic", StringComparison.OrdinalIgnoreCase)
            ? Domain.MergeMode.Automatic
            : Domain.MergeMode.ManualReviewRequired;
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
