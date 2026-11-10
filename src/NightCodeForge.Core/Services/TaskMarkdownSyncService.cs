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

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static bool IsArchivedStatus(TaskOverallStatus status) =>
        status is TaskOverallStatus.Closed or TaskOverallStatus.Cancelled or TaskOverallStatus.Rejected;

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

    internal static IDeserializer CreateYamlDeserializer() =>
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
        return GenerateMarkdownWithFrontmatter(task, dependencies, agentProfileId).Markdown;
    }

    internal (string Markdown, TaskFrontmatter Frontmatter) GenerateMarkdownWithFrontmatter(ForgeTask task, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null)
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
            Version = task.Version > 0 ? task.Version : 1,
            BaseBranch = task.BaseBranch,
            FeatureBranch = string.IsNullOrWhiteSpace(task.FeatureBranch) ? null : task.FeatureBranch,
            DependsOn = new TaskDependencyReferenceList(depList),
            AgentProfile = agentProfileId,
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt
        };

        var bodySb = new StringBuilder();
        bodySb.Append($"# {task.Title}\n\n");
        bodySb.Append("## 📋 Requirements & Description\n");
        bodySb.Append(task.Description).Append('\n');

        if (task.Questions != null && task.Questions.Count > 0)
        {
            bodySb.Append("\n## ❓ Human Clarifications (Q&A)\n");
            int qIndex = 1;
            foreach (var q in task.Questions)
            {
                bodySb.Append($"### Question {qIndex++} (Asked during Stage: {q.StageId} on {q.AskedAt:yyyy-MM-dd HH:mm:ss})\n");
                bodySb.Append($"**Prompt:** {q.Prompt}\n");
                bodySb.Append($"**Answer:** {q.Answer ?? "_Awaiting response_"}\n\n");
            }
        }

        var bodyText = bodySb.ToString().Replace("\r\n", "\n");
        var extractedDescription = ExtractDescriptionFromBody(bodyText);
        frontmatter.ContentHash = ComputeTaskContentHash(frontmatter, extractedDescription);

        var yaml = _yamlSerializer.Serialize(frontmatter).Replace("\r\n", "\n");
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append(yaml);
        if (!yaml.EndsWith('\n'))
        {
            sb.Append('\n');
        }
        sb.Append("---\n\n");
        sb.Append(bodyText);

        var markdown = sb.ToString().Replace("\r\n", "\n");
        return (markdown, frontmatter);
    }

    public static string ComputeTaskContentHash(TaskFrontmatter frontmatter, string? description)
    {
        ArgumentNullException.ThrowIfNull(frontmatter);
        var deps = frontmatter.DependsOn != null && frontmatter.DependsOn.Count > 0
            ? string.Join(",", frontmatter.DependsOn.Select(d => d?.ToString()?.Trim() ?? string.Empty))
            : string.Empty;
        var normDesc = (NormalizeLineEndings(description) ?? string.Empty).Trim();
        var payload = string.Join("\n",
            $"id:{frontmatter.Id}",
            $"title:{(frontmatter.Title ?? string.Empty).Trim()}",
            $"status:{(frontmatter.Status ?? string.Empty).Trim()}",
            $"origin:{(frontmatter.Origin ?? string.Empty).Trim()}",
            $"priority:{frontmatter.Priority}",
            $"merge_mode:{(frontmatter.MergeMode ?? string.Empty).Trim()}",
            $"base_branch:{(frontmatter.BaseBranch ?? string.Empty).Trim()}",
            $"feature_branch:{(frontmatter.FeatureBranch ?? string.Empty).Trim()}",
            $"agent_profile:{(frontmatter.AgentProfile ?? string.Empty).Trim()}",
            $"depends_on:{deps}",
            $"description:{normDesc}");
        return ComputeSha256Hash(payload);
    }

    public (TaskFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent) => ParseMarkdown(markdownContent, null);

    public (TaskFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent, string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(markdownContent))
        {
            return (new TaskFrontmatter(), string.Empty);
        }

        var normalized = markdownContent.TrimStart('\uFEFF').Replace("\r\n", "\n");
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

    public string ComputeContentHash(string content) => ComputeSha256Hash(content);

    private static string ComputeSha256Hash(string content)
    {
        var normalized = content.TrimStart('\uFEFF').Replace("\r\n", "\n");
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
        var archiveDir = Path.Combine(tasksDir, TaskSyncConstants.ArchiveSegment);
        var isArchived = IsArchivedStatus(task.Status);
        var effectiveTasksDir = isArchived ? archiveDir : tasksDir;
        var oppositeTasksDir = isArchived ? tasksDir : archiveDir;

        Directory.CreateDirectory(effectiveTasksDir);

        var effectiveTargetIndexTask = DirectoryTaskIndex.CreateAsync(effectiveTasksDir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: false, failFast: true, cancellationToken);
        var oppositeTargetIndexTask = DirectoryTaskIndex.CreateAsync(oppositeTasksDir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: false, failFast: false, cancellationToken);

        await Task.WhenAll(effectiveTargetIndexTask, oppositeTargetIndexTask);
        var effectiveTargetIndex = await effectiveTargetIndexTask;
        var oppositeTargetIndex = await oppositeTargetIndexTask;

        var targetFile = ResolveTargetFilePathFromIndex(effectiveTargetIndex, task, oppositeTargetIndex);
        CleanupOtherFilesForTaskId(effectiveTargetIndex, oppositeTargetIndex, task.Id, targetFile);

        var markdown = GenerateMarkdown(task, dependencies, agentProfileId);
        var normalizedMarkdown = markdown.Replace("\r\n", "\n");
        var hash = ComputeContentHash(normalizedMarkdown);
        _suppressedHashes[hash] = 1;

        await File.WriteAllTextAsync(targetFile, normalizedMarkdown, Utf8NoBom, cancellationToken);

        // Keep issues in sync into clone/docs/tasks (or clone/docs/tasks/archive for terminal tasks)
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
                    var cloneArchiveDir = Path.Combine(cloneTasksDir, TaskSyncConstants.ArchiveSegment);
                    var effectiveCloneDir = isArchived ? cloneArchiveDir : cloneTasksDir;
                    var oppositeCloneDir = isArchived ? cloneTasksDir : cloneArchiveDir;

                    Directory.CreateDirectory(effectiveCloneDir);
                    var effectiveCloneIndexTask = DirectoryTaskIndex.CreateAsync(effectiveCloneDir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: true, failFast: false, cancellationToken);
                    var oppositeCloneIndexTask = DirectoryTaskIndex.CreateAsync(oppositeCloneDir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: true, failFast: false, cancellationToken);

                    await Task.WhenAll(effectiveCloneIndexTask, oppositeCloneIndexTask);
                    var effectiveCloneIndex = await effectiveCloneIndexTask;
                    var oppositeCloneIndex = await oppositeCloneIndexTask;

                    var hostFileName = Path.GetFileName(targetFile);
                    var cloneTargetFile = !string.IsNullOrWhiteSpace(hostFileName)
                        ? Path.Combine(effectiveCloneDir, hostFileName)
                        : ResolveTargetFilePathFromIndex(effectiveCloneIndex, task, oppositeCloneIndex);

                    CleanupOtherFilesForTaskId(effectiveCloneIndex, oppositeCloneIndex, task.Id, cloneTargetFile);

                    bool cloneMatches = effectiveCloneIndex.TryGetEntryByPath(cloneTargetFile, out var existingCloneEntry) &&
                        string.Equals(existingCloneEntry.ContentHash, hash, StringComparison.Ordinal);

                    if (!cloneMatches)
                    {
                        _suppressedHashes[hash] = 1;
                        await File.WriteAllTextAsync(cloneTargetFile, normalizedMarkdown, Utf8NoBom, cancellationToken);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Best-effort clone mirror write failed for task #{TaskId}; primary workspace path already succeeded", task.Id);
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
            var archiveDir = Path.Combine(tasksDir, TaskSyncConstants.ArchiveSegment);
            Directory.CreateDirectory(tasksDir);
            Directory.CreateDirectory(archiveDir);

            string? hostTasksDir = null;
            string? hostArchiveDir = null;
            if (!string.IsNullOrWhiteSpace(hostRepositoryPath) &&
                !string.Equals(hostRepositoryPath, targetDirectory, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(hostRepositoryPath))
            {
                hostTasksDir = Path.Combine(hostRepositoryPath, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
                hostArchiveDir = Path.Combine(hostTasksDir, TaskSyncConstants.ArchiveSegment);
                Directory.CreateDirectory(hostTasksDir);
                Directory.CreateDirectory(hostArchiveDir);
            }

            var cloneTasksDir = !string.IsNullOrWhiteSpace(cloneDirectory)
                ? NormalizeCloneTasksDirectory(cloneDirectory)
                : await ResolveCloneTasksDirectoryAsync(workspace.Id, workspace, targetDirectory, cancellationToken);

            string? normalizedCloneTasksDir = null;
            string? cloneArchiveDir = null;
            bool mirrorToClone = false;

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
                        cloneArchiveDir = Path.Combine(cloneTasksDir, TaskSyncConstants.ArchiveSegment);
                        Directory.CreateDirectory(cloneArchiveDir);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Could not prepare clone directory '{CloneTasksDir}' for workspace #{WorkspaceId}", cloneTasksDir, workspace.Id);
                    mirrorToClone = false;
                }
            }

            // Upfront single-pass directory indexing
            var tasksIndexTask = DirectoryTaskIndex.CreateAsync(tasksDir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: false, failFast: true, cancellationToken);
            var archiveIndexTask = DirectoryTaskIndex.CreateAsync(archiveDir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: false, failFast: true, cancellationToken);

            Task<DirectoryTaskIndex>? hostTasksIndexTask = null;
            Task<DirectoryTaskIndex>? hostArchiveIndexTask = null;
            if (hostTasksDir != null && hostArchiveDir != null)
            {
                hostTasksIndexTask = DirectoryTaskIndex.CreateAsync(hostTasksDir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: false, failFast: false, cancellationToken);
                hostArchiveIndexTask = DirectoryTaskIndex.CreateAsync(hostArchiveDir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: false, failFast: false, cancellationToken);
            }

            Task<DirectoryTaskIndex>? cloneTasksIndexTask = null;
            Task<DirectoryTaskIndex>? cloneArchiveIndexTask = null;
            if (mirrorToClone && normalizedCloneTasksDir != null && cloneArchiveDir != null)
            {
                cloneTasksIndexTask = DirectoryTaskIndex.CreateAsync(normalizedCloneTasksDir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: true, failFast: false, cancellationToken);
                cloneArchiveIndexTask = DirectoryTaskIndex.CreateAsync(cloneArchiveDir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: true, failFast: false, cancellationToken);
            }

            await Task.WhenAll(
                tasksIndexTask,
                archiveIndexTask,
                hostTasksIndexTask ?? Task.FromResult<DirectoryTaskIndex?>(null)!,
                hostArchiveIndexTask ?? Task.FromResult<DirectoryTaskIndex?>(null)!,
                cloneTasksIndexTask ?? Task.FromResult<DirectoryTaskIndex?>(null)!,
                cloneArchiveIndexTask ?? Task.FromResult<DirectoryTaskIndex?>(null)!
            );

            var tasksIndex = await tasksIndexTask;
            var archiveIndex = await archiveIndexTask;
            var hostTasksIndex = hostTasksIndexTask != null ? await hostTasksIndexTask : null;
            var hostArchiveIndex = hostArchiveIndexTask != null ? await hostArchiveIndexTask : null;
            var cloneTasksIndex = cloneTasksIndexTask != null ? await cloneTasksIndexTask : null;
            var cloneArchiveIndex = cloneArchiveIndexTask != null ? await cloneArchiveIndexTask : null;

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

                var isArchived = IsArchivedStatus(task.Status);
                var effectiveTargetIndex = isArchived ? archiveIndex : tasksIndex;
                var oppositeTargetIndex = isArchived ? tasksIndex : archiveIndex;
                var effectiveHostIndex = hostTasksDir != null ? (isArchived ? hostArchiveIndex : hostTasksIndex) : null;
                var oppositeHostIndex = hostTasksDir != null ? (isArchived ? hostTasksIndex : hostArchiveIndex) : null;
                var effectiveCloneIndex = mirrorToClone ? (isArchived ? cloneArchiveIndex : cloneTasksIndex) : null;
                var oppositeCloneIndex = mirrorToClone ? (isArchived ? cloneTasksIndex : cloneArchiveIndex) : null;

                var targetFile = ResolveTargetFilePathFromIndex(
                    effectiveTargetIndex,
                    task,
                    oppositeTargetIndex,
                    effectiveHostIndex,
                    oppositeHostIndex);

                CleanupOtherFilesForTaskId(effectiveTargetIndex, oppositeTargetIndex, task.Id, targetFile);

                var (markdown, frontmatter) = GenerateMarkdownWithFrontmatter(task, depIds);
                var normalizedMarkdown = markdown.Replace("\r\n", "\n");
                var hash = ComputeContentHash(normalizedMarkdown);

                bool matchesExisting = effectiveTargetIndex.TryGetEntryByPath(targetFile, out var existingTargetEntry) &&
                    string.Equals(existingTargetEntry.ContentHash, hash, StringComparison.Ordinal);

                if (matchesExisting)
                {
                    writtenPaths[task.Id] = targetFile;
                    baseline[task.Id] = new TaskBaselineEntry(
                        task.Id,
                        task.Version,
                        task.UpdatedAt,
                        hash,
                        targetFile,
                        frontmatter,
                        task.Description);
                }
                else
                {
                    _suppressedHashes[hash] = 1;
                    await File.WriteAllTextAsync(targetFile, normalizedMarkdown, Utf8NoBom, cancellationToken);
                    writtenPaths[task.Id] = targetFile;
                    baseline[task.Id] = new TaskBaselineEntry(
                        task.Id,
                        task.Version,
                        task.UpdatedAt,
                        hash,
                        targetFile,
                        frontmatter,
                        task.Description);

                    effectiveTargetIndex.UpsertEntry(new DirectoryTaskEntry
                    {
                        FilePath = targetFile,
                        FileName = Path.GetFileName(targetFile),
                        TaskId = task.Id,
                        Version = task.Version,
                        ContentHash = hash,
                        Frontmatter = frontmatter,
                        PreferredSlug = GenerateSlug(task.Title)
                    });
                }

                if (effectiveHostIndex != null && oppositeHostIndex != null)
                {
                    var hostFile = Path.Combine(effectiveHostIndex.DirectoryPath, Path.GetFileName(targetFile));
                    CleanupOtherFilesForTaskId(effectiveHostIndex, oppositeHostIndex, task.Id, hostFile);

                    bool hostMatches = effectiveHostIndex.TryGetEntryByPath(hostFile, out var existingHostEntry) &&
                        string.Equals(existingHostEntry.ContentHash, hash, StringComparison.Ordinal);

                    if (!hostMatches)
                    {
                        _suppressedHashes[hash] = 1;
                        await File.WriteAllTextAsync(hostFile, normalizedMarkdown, Utf8NoBom, cancellationToken);
                        effectiveHostIndex.UpsertEntry(new DirectoryTaskEntry
                        {
                            FilePath = hostFile,
                            FileName = Path.GetFileName(hostFile),
                            TaskId = task.Id,
                            Version = task.Version,
                            ContentHash = hash,
                            Frontmatter = frontmatter,
                            PreferredSlug = GenerateSlug(task.Title)
                        });
                    }
                }

                if (mirrorToClone && effectiveCloneIndex != null && oppositeCloneIndex != null)
                {
                    try
                    {
                        var cloneFile = Path.Combine(effectiveCloneIndex.DirectoryPath, Path.GetFileName(targetFile));
                        CleanupOtherFilesForTaskId(effectiveCloneIndex, oppositeCloneIndex, task.Id, cloneFile);

                        bool cloneMatches = effectiveCloneIndex.TryGetEntryByPath(cloneFile, out var existingCloneEntry) &&
                            string.Equals(existingCloneEntry.ContentHash, hash, StringComparison.Ordinal);

                        if (!cloneMatches)
                        {
                            _suppressedHashes[hash] = 1;
                            await File.WriteAllTextAsync(cloneFile, normalizedMarkdown, Utf8NoBom, cancellationToken);
                            effectiveCloneIndex.UpsertEntry(new DirectoryTaskEntry
                            {
                                FilePath = cloneFile,
                                FileName = Path.GetFileName(cloneFile),
                                TaskId = task.Id,
                                Version = task.Version,
                                ContentHash = hash,
                                Frontmatter = frontmatter,
                                PreferredSlug = GenerateSlug(task.Title)
                            });
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "Best-effort mirror to clone failed for task #{TaskId}", task.Id);
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

    private static string ResolveTargetFilePathFromIndex(
        DirectoryTaskIndex targetIndex,
        ForgeTask task,
        params DirectoryTaskIndex?[] fallbackIndices)
    {
        var datePrefix = (task.CreatedAt != default ? task.CreatedAt : DateTime.UtcNow).ToString("yyyy-MM-dd");
        var slug = GenerateSlug(task.Title);
        var preferredFileName = $"{datePrefix}-{slug}{TaskSyncConstants.MarkdownExtension}";

        var candidates = new List<(string FilePath, int Version, bool MatchesPreferredName, bool IsInTargetDir, TaskFrontmatter? Frontmatter)>();

        // 1. Check if an existing file already holds this task's ID in the primary target directory
        if (task.Id > 0)
        {
            var existingInTarget = targetIndex.GetFilesByTaskId(task.Id);
            foreach (var entry in existingInTarget)
            {
                var isPreferred = string.Equals(entry.FileName, preferredFileName, StringComparison.OrdinalIgnoreCase);
                candidates.Add((entry.FilePath, entry.Version ?? 0, isPreferred, true, entry.Frontmatter));
            }

            // 2. Check fallback directories (e.g. opposite active/archive directory or host directory)
            if (fallbackIndices != null)
            {
                foreach (var fallbackIndex in fallbackIndices)
                {
                    if (fallbackIndex == null) continue;
                    var fallbackEntries = fallbackIndex.GetFilesByTaskId(task.Id);
                    foreach (var entry in fallbackEntries)
                    {
                        if (!targetIndex.TryGetEntryByFileName(entry.FileName, out var existingInTargetEntry) ||
                            existingInTargetEntry.TaskId == task.Id)
                        {
                            var candidateInTarget = Path.Combine(targetIndex.DirectoryPath, entry.FileName);
                            var isPreferred = string.Equals(entry.FileName, preferredFileName, StringComparison.OrdinalIgnoreCase);
                            candidates.Add((candidateInTarget, entry.Version ?? 0, isPreferred, false, entry.Frontmatter));
                        }
                    }
                }
            }
        }

        if (candidates.Count > 0)
        {
            var chosen = candidates
                .OrderByDescending(c => c.Version)
                .ThenByDescending(c => c.MatchesPreferredName)
                .ThenByDescending(c => c.IsInTargetDir)
                .ThenBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)
                .First();

            if (!targetIndex.TryGetEntryByPath(chosen.FilePath, out _))
            {
                targetIndex.UpsertEntry(new DirectoryTaskEntry
                {
                    FilePath = chosen.FilePath,
                    FileName = Path.GetFileName(chosen.FilePath),
                    TaskId = task.Id > 0 ? task.Id : null,
                    Version = chosen.Version,
                    ContentHash = string.Empty,
                    Frontmatter = chosen.Frontmatter ?? new TaskFrontmatter { Id = task.Id > 0 ? task.Id : null, Version = task.Version, Title = task.Title },
                    PreferredSlug = slug
                });
            }

            return chosen.FilePath;
        }

        var targetFile = Path.Combine(targetIndex.DirectoryPath, preferredFileName);
        int suffix = 2;

        while (targetIndex.TryGetEntryByFileName(Path.GetFileName(targetFile), out var existingEntry))
        {
            if (task.Id > 0 && existingEntry.TaskId == task.Id)
            {
                break;
            }
            targetFile = Path.Combine(targetIndex.DirectoryPath, $"{datePrefix}-{slug}-{suffix++}{TaskSyncConstants.MarkdownExtension}");
        }

        if (!targetIndex.TryGetEntryByPath(targetFile, out _))
        {
            targetIndex.UpsertEntry(new DirectoryTaskEntry
            {
                FilePath = targetFile,
                FileName = Path.GetFileName(targetFile),
                TaskId = task.Id > 0 ? task.Id : null,
                Version = task.Version,
                ContentHash = string.Empty,
                Frontmatter = new TaskFrontmatter { Id = task.Id > 0 ? task.Id : null, Version = task.Version, Title = task.Title },
                PreferredSlug = slug
            });
        }

        return targetFile;
    }

    private void CleanupOtherFilesForTaskId(
        DirectoryTaskIndex targetIndex,
        DirectoryTaskIndex? oppositeIndex,
        int taskId,
        string keepFilePath)
    {
        if (taskId <= 0) return;

        var normalizedKeepPath = Path.GetFullPath(keepFilePath);

        void CleanupInIndex(DirectoryTaskIndex? index)
        {
            if (index == null) return;
            var entries = index.GetFilesByTaskId(taskId).ToList();
            foreach (var entry in entries)
            {
                if (string.Equals(Path.GetFullPath(entry.FilePath), normalizedKeepPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(entry.FilePath))
                    {
                        File.Delete(entry.FilePath);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Ignored error deleting duplicate file '{FilePath}' for task #{TaskId}", entry.FilePath, taskId);
                }

                index.RemoveEntry(entry.FilePath);
            }
        }

        CleanupInIndex(targetIndex);
        CleanupInIndex(oppositeIndex);
    }

    public async Task<IReadOnlyList<ForgeTask>> SyncAllTasksFromDiskAsync(
        Workspace workspace,
        string targetDirectory,
        string? hostRepositoryPath,
        IReadOnlyDictionary<int, TaskBaselineEntry>? baselineSnapshot,
        string changeActor,
        string? changeReason,
        int? currentTaskId = null,
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

        var archiveDir = Path.Combine(tasksDir, TaskSyncConstants.ArchiveSegment);

        string? hostTasksDir = null;
        string? hostArchiveDir = null;
        if (!string.IsNullOrWhiteSpace(hostRepositoryPath) &&
            !string.Equals(hostRepositoryPath, targetDirectory, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(hostRepositoryPath))
        {
            hostTasksDir = Path.Combine(hostRepositoryPath, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
            hostArchiveDir = Path.Combine(hostTasksDir, TaskSyncConstants.ArchiveSegment);
            Directory.CreateDirectory(hostTasksDir);
            Directory.CreateDirectory(hostArchiveDir);
        }

        var tasksIndexTask = DirectoryTaskIndex.CreateAsync(tasksDir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: false, failFast: true, cancellationToken);
        var archiveIndexTask = DirectoryTaskIndex.CreateAsync(archiveDir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: false, failFast: false, cancellationToken);

        Task<DirectoryTaskIndex>? hostTasksIndexTask = null;
        Task<DirectoryTaskIndex>? hostArchiveIndexTask = null;
        if (hostTasksDir != null && hostArchiveDir != null)
        {
            hostTasksIndexTask = DirectoryTaskIndex.CreateAsync(hostTasksDir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: false, failFast: false, cancellationToken);
            hostArchiveIndexTask = DirectoryTaskIndex.CreateAsync(hostArchiveDir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: false, failFast: false, cancellationToken);
        }

        await Task.WhenAll(
            tasksIndexTask,
            archiveIndexTask,
            hostTasksIndexTask ?? Task.FromResult<DirectoryTaskIndex?>(null)!,
            hostArchiveIndexTask ?? Task.FromResult<DirectoryTaskIndex?>(null)!
        );

        var tasksIndex = await tasksIndexTask;
        var archiveIndex = await archiveIndexTask;
        var hostTasksIndex = hostTasksIndexTask != null ? await hostTasksIndexTask : null;
        var hostArchiveIndex = hostArchiveIndexTask != null ? await hostArchiveIndexTask : null;

        var (taskRepo, depEngine, scope) = ResolveDependencies();
        try
        {
            var dbTasksList = await taskRepo.GetByWorkspaceIdAsync(workspace.Id, cancellationToken);
            var dbTasks = dbTasksList.ToDictionary(t => t.Id);

            var parsedFiles = tasksIndex.AllEntries
                .OrderBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(e => (
                    e.FilePath,
                    e.TaskContentHash,
                    e.Content,
                    Hash: e.ContentHash,
                    e.Frontmatter,
                    e.Body,
                    e.Description
                ))
                .ToList();

            // 1. Validate Existing and New Tasks (auto-cleaning stale untouched snapshots first)
            static bool IsUntouchedSnapshot((string FilePath, string TaskContentHash, string Content, string Hash, TaskFrontmatter Frontmatter, string Body, string Description) p) =>
                !string.IsNullOrWhiteSpace(p.Frontmatter.ContentHash) &&
                string.Equals(p.Frontmatter.ContentHash.Trim(), p.TaskContentHash, StringComparison.OrdinalIgnoreCase);

            static bool MatchesPreferredSlug((string FilePath, string TaskContentHash, string Content, string Hash, TaskFrontmatter Frontmatter, string Body, string Description) p)
            {
                var slug = GenerateSlug(p.Frontmatter.Title);
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(p.FilePath);
                return fileNameWithoutExt.EndsWith($"-{slug}", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(fileNameWithoutExt, slug, StringComparison.OrdinalIgnoreCase);
            }

            var duplicateGroups = parsedFiles
                .Where(p => p.Frontmatter.Id.HasValue && p.Frontmatter.Id.Value > 0)
                .GroupBy(p => p.Frontmatter.Id!.Value)
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in duplicateGroups)
            {
                var ordered = group
                    .OrderByDescending(p => p.Frontmatter.Version ?? 0)
                    .ToList();
                var maxVersion = ordered[0].Frontmatter.Version ?? 0;

                if (maxVersion > 0)
                {
                    var staleItems = ordered.Where(p =>
                        p.Frontmatter.Version.HasValue &&
                        p.Frontmatter.Version.Value < maxVersion &&
                        IsUntouchedSnapshot(p))
                        .ToList();

                    var remaining = ordered.Except(staleItems).ToList();
                    if (remaining.Count > 1)
                    {
                        var modifiedRemaining = remaining.Where(p => !IsUntouchedSnapshot(p)).ToList();
                        var untouchedRemaining = remaining.Where(IsUntouchedSnapshot).ToList();

                        if (modifiedRemaining.Count == 1 && untouchedRemaining.Count == remaining.Count - 1)
                        {
                            staleItems.AddRange(untouchedRemaining);
                        }
                        else if (untouchedRemaining.Count == remaining.Count &&
                                 untouchedRemaining.Select(p => p.TaskContentHash).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                        {
                            var keep = untouchedRemaining
                                .OrderByDescending(MatchesPreferredSlug)
                                .ThenBy(p => p.FilePath, StringComparer.OrdinalIgnoreCase)
                                .First();
                            staleItems.AddRange(untouchedRemaining.Where(p => !ReferenceEquals(p.FilePath, keep.FilePath) && !string.Equals(p.FilePath, keep.FilePath, StringComparison.OrdinalIgnoreCase)));
                        }
                    }

                    if (staleItems.Count == ordered.Count - 1)
                    {
                        foreach (var stale in staleItems)
                        {
                            try
                            {
                                if (File.Exists(stale.FilePath))
                                {
                                    File.Delete(stale.FilePath);
                                }
                                tasksIndex.RemoveEntry(stale.FilePath);

                                if (hostTasksDir != null)
                                {
                                    var staleHostFile = Path.Combine(hostTasksDir, Path.GetFileName(stale.FilePath));
                                    if (File.Exists(staleHostFile))
                                    {
                                        File.Delete(staleHostFile);
                                    }
                                    hostTasksIndex?.RemoveEntry(staleHostFile);
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _logger.LogDebug(ex, "Best-effort cleanup of stale duplicate file '{FilePath}' failed", stale.FilePath);
                            }
                            parsedFiles.Remove(stale);
                        }
                    }
                }
            }

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

            var untouchedOrStaleTaskIds = new HashSet<int>();

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

                    bool contentHashMatches = !string.IsNullOrWhiteSpace(item.Frontmatter.ContentHash) &&
                        string.Equals(item.Frontmatter.ContentHash.Trim(), item.TaskContentHash, StringComparison.OrdinalIgnoreCase);

                    // 1. Untouched file detection against baseline snapshot or embedded content_hash:
                    // If the disk content has not changed since the file was generated, skip all validation checks and updates.
                    if (baselineSnapshot != null && baselineSnapshot.TryGetValue(taskId, out var baseline))
                    {
                        bool diskDiffersFromBaseline = !string.Equals(item.Hash, baseline.ContentHash, StringComparison.Ordinal) && !contentHashMatches;
                        if (!diskDiffersFromBaseline)
                        {
                            untouchedOrStaleTaskIds.Add(taskId);
                            continue;
                        }

                        // Sibling stale file detection when currentTaskId is known
                        if (currentTaskId.HasValue && taskId != currentTaskId.Value &&
                            item.Frontmatter.Version.HasValue && dbTask.Version > item.Frontmatter.Version.Value)
                        {
                            _logger.LogInformation(
                                "Task #{TaskId} database version ({DbVersion}) is more recent than disk version ({DiskVersion}). Retaining authoritative database state.",
                                taskId, dbTask.Version, item.Frontmatter.Version.Value);
                            untouchedOrStaleTaskIds.Add(taskId);
                            continue;
                        }

                        // Concurrency check for modified files:
                        if (dbTask.Version != baseline.Version)
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
                    else if (baselineSnapshot == null)
                    {
                        if (contentHashMatches)
                        {
                            untouchedOrStaleTaskIds.Add(taskId);
                            continue;
                        }

                        // Stale file detection: When syncing without baseline (e.g. host sync or stage resume),
                        // if DB version is strictly newer than the markdown file's version, the file is stale
                        // (e.g. another task completed/closed concurrently while this task was paused).
                        // For sibling tasks, the database is authoritative: ignore the stale file without throwing status tamper errors.
                        if (item.Frontmatter.Version.HasValue && dbTask.Version > item.Frontmatter.Version.Value)
                        {
                            if (currentTaskId.HasValue && taskId == currentTaskId.Value)
                            {
                                // Current task exception: User/maintainer updated task in DB (e.g. changed merge mode),
                                // but agent worked on disk refining the specification.
                                // Do not skip: preserve agent spec refinements.
                            }
                            else
                            {
                                _logger.LogInformation(
                                    "Task #{TaskId} database version ({DbVersion}) is more recent than disk version ({DiskVersion}). Retaining authoritative database state.",
                                    taskId, dbTask.Version, item.Frontmatter.Version.Value);
                                untouchedOrStaleTaskIds.Add(taskId);
                                continue;
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
                                if (currentTaskId.HasValue && taskId == currentTaskId.Value)
                                {
                                    _logger.LogInformation(
                                        "Task #{TaskId} markdown frontmatter specifies status '{MarkdownStatus}', which differs from authoritative status '{DbStatus}'. Retaining database status as workflow gates govern lifecycle.",
                                        taskId,
                                        item.Frontmatter.Status.Trim(),
                                        referenceStatus);
                                }
                                else
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

            // Also index archived task files in docs/tasks/archive so filename references to closed/archived tasks resolve
            foreach (var archivedEntry in archiveIndex.AllEntries)
            {
                if (archivedEntry.TaskId.HasValue && archivedEntry.TaskId.Value > 0 && dbTasks.ContainsKey(archivedEntry.TaskId.Value))
                {
                    var id = archivedEntry.TaskId.Value;
                    var fileName = archivedEntry.FileName;
                    existingTaskLookup.TryAdd(fileName, id);
                    existingTaskLookup.TryAdd(Path.GetFileNameWithoutExtension(fileName), id);
                    var fileSlug = NormalizeDependencyKey(fileName);
                    if (!string.IsNullOrEmpty(fileSlug))
                    {
                        existingTaskLookup.TryAdd(fileSlug, id);
                    }
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
                if (untouchedOrStaleTaskIds.Contains(taskId))
                {
                    continue;
                }

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
                while (tasksIndex.TryGetEntryByFileName(Path.GetFileName(newFilePath), out _) &&
                       !string.Equals(newFilePath, nt.Item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    newFilePath = Path.Combine(tasksDir, $"{datePrefix}-{slug}-{suffix++}{TaskSyncConstants.MarkdownExtension}");
                }

                if (!string.Equals(newFilePath, nt.Item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(nt.Item.FilePath))
                    {
                        File.Delete(nt.Item.FilePath);
                    }
                    tasksIndex.RemoveEntry(nt.Item.FilePath);
                }

                await File.WriteAllTextAsync(newFilePath, rewrittenMarkdown.Replace("\r\n", "\n"), Utf8NoBom, cancellationToken);
                tasksIndex.UpsertEntry(new DirectoryTaskEntry
                {
                    FilePath = newFilePath,
                    FileName = Path.GetFileName(newFilePath),
                    TaskId = created.Id,
                    Version = created.Version,
                    ContentHash = rewrittenHash,
                    Frontmatter = nt.Item.Frontmatter,
                    PreferredSlug = slug
                });

                if (hostTasksDir != null && hostTasksIndex != null)
                {
                    var oldHostFile = Path.Combine(hostTasksDir, Path.GetFileName(nt.Item.FilePath));
                    if (File.Exists(oldHostFile) && !string.Equals(oldHostFile, Path.Combine(hostTasksDir, Path.GetFileName(newFilePath)), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(oldHostFile);
                        hostTasksIndex.RemoveEntry(oldHostFile);
                    }

                    var newHostFile = Path.Combine(hostTasksDir, Path.GetFileName(newFilePath));
                    _suppressedHashes[rewrittenHash] = 1;
                    await File.WriteAllTextAsync(newHostFile, rewrittenMarkdown.Replace("\r\n", "\n"), Utf8NoBom, cancellationToken);
                    hostTasksIndex.UpsertEntry(new DirectoryTaskEntry
                    {
                        FilePath = newHostFile,
                        FileName = Path.GetFileName(newHostFile),
                        TaskId = created.Id,
                        Version = created.Version,
                        ContentHash = rewrittenHash,
                        Frontmatter = nt.Item.Frontmatter,
                        PreferredSlug = slug
                    });
                }

                refreshedTasks.Add(created);
            }

            // 5. Update existing tasks
            foreach (var item in existingFiles)
            {
                var taskId = item.Frontmatter.Id!.Value;
                var task = dbTasks[taskId];

                // If the file has not changed on disk or is a stale sibling file, retain authoritative database state.
                if (untouchedOrStaleTaskIds.Contains(taskId))
                {
                    if (IsArchivedStatus(task.Status))
                    {
                        Directory.CreateDirectory(archiveDir);
                        var archivedDeps = task.Dependencies?.Select(d => d.DependsOnTaskId).ToList() ?? new List<int>();
                        var archivedTargetFile = ResolveTargetFilePathFromIndex(
                            archiveIndex,
                            task,
                            tasksIndex,
                            hostArchiveIndex,
                            hostTasksIndex);
                        CleanupOtherFilesForTaskId(archiveIndex, tasksIndex, task.Id, archivedTargetFile);

                        var authoritativeMarkdown = GenerateMarkdown(task, archivedDeps, item.Frontmatter.AgentProfile);
                        var authHash = ComputeContentHash(authoritativeMarkdown);

                        bool archMatches = archiveIndex.TryGetEntryByPath(archivedTargetFile, out var exArch) &&
                            string.Equals(exArch.ContentHash, authHash, StringComparison.Ordinal);

                        if (!archMatches)
                        {
                            _suppressedHashes[authHash] = 1;
                            await File.WriteAllTextAsync(archivedTargetFile, authoritativeMarkdown.Replace("\r\n", "\n"), Utf8NoBom, cancellationToken);
                            archiveIndex.UpsertEntry(new DirectoryTaskEntry
                            {
                                FilePath = archivedTargetFile,
                                FileName = Path.GetFileName(archivedTargetFile),
                                TaskId = task.Id,
                                Version = task.Version,
                                ContentHash = authHash,
                                Frontmatter = item.Frontmatter,
                                PreferredSlug = GenerateSlug(task.Title)
                            });
                        }

                        if (hostTasksDir != null && hostArchiveDir != null && hostArchiveIndex != null && hostTasksIndex != null)
                        {
                            Directory.CreateDirectory(hostArchiveDir);
                            var hostArchivedFile = Path.Combine(hostArchiveDir, Path.GetFileName(archivedTargetFile));
                            CleanupOtherFilesForTaskId(hostArchiveIndex, hostTasksIndex, task.Id, hostArchivedFile);

                            bool hostArchMatches = hostArchiveIndex.TryGetEntryByPath(hostArchivedFile, out var exHostArch) &&
                                string.Equals(exHostArch.ContentHash, authHash, StringComparison.Ordinal);

                            if (!hostArchMatches)
                            {
                                _suppressedHashes[authHash] = 1;
                                await File.WriteAllTextAsync(hostArchivedFile, authoritativeMarkdown.Replace("\r\n", "\n"), Utf8NoBom, cancellationToken);
                                hostArchiveIndex.UpsertEntry(new DirectoryTaskEntry
                                {
                                    FilePath = hostArchivedFile,
                                    FileName = Path.GetFileName(hostArchivedFile),
                                    TaskId = task.Id,
                                    Version = task.Version,
                                    ContentHash = authHash,
                                    Frontmatter = item.Frontmatter,
                                    PreferredSlug = GenerateSlug(task.Title)
                                });
                            }
                        }
                    }

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

                if (baselineSnapshot != null && baselineSnapshot.TryGetValue(taskId, out var baseline) && baseline != null)
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
                    if (currentTaskId.HasValue && taskId == currentTaskId.Value &&
                        item.Frontmatter.Version.HasValue && task.Version > item.Frontmatter.Version.Value)
                    {
                        // Maintainer updated DB while agent refined task on disk:
                        // Keep authoritative DB values for maintainer fields (MergeMode, Priority, BaseBranch),
                        // but take the agent's refined description and title from disk!
                        bool diskDescChanged = !string.IsNullOrWhiteSpace(item.Description) &&
                            !string.Equals(NormalizeLineEndings(item.Description)?.Trim(), NormalizeLineEndings(task.Description)?.Trim(), StringComparison.Ordinal);
                        newDesc = diskDescChanged ? item.Description : task.Description;

                        bool diskTitleChanged = !string.IsNullOrWhiteSpace(item.Frontmatter.Title) &&
                            !string.Equals(item.Frontmatter.Title.Trim(), task.Title?.Trim(), StringComparison.Ordinal);
                        newTitle = diskTitleChanged ? item.Frontmatter.Title!.Trim() : (task.Title ?? string.Empty);

                        newPriority = task.Priority;
                        newMergeMode = task.MergeMode;
                        newBaseBranch = task.BaseBranch ?? workspace.DefaultBaseBranch ?? WorkflowDefaults.DefaultBaseBranch;
                    }
                    else
                    {
                        newTitle = !string.IsNullOrWhiteSpace(item.Frontmatter.Title) ? item.Frontmatter.Title.Trim() : (task.Title ?? string.Empty);
                        newDesc = !string.IsNullOrWhiteSpace(item.Description) ? item.Description : task.Description;
                        newPriority = item.Frontmatter.Priority > 0 ? item.Frontmatter.Priority : task.Priority;
                        newMergeMode = ParseMergeMode(item.Frontmatter.MergeMode) ?? task.MergeMode;
                        newBaseBranch = !string.IsNullOrWhiteSpace(item.Frontmatter.BaseBranch) ? item.Frontmatter.BaseBranch.Trim() : task.BaseBranch;
                    }
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

                bool anyFieldChanged = titleChanged || descChanged || priorityChanged || depsChanged || mergeModeChanged || baseBranchChanged || originChanged;
                if (anyFieldChanged)
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

                // In-place normalization for existing files if fields changed (to update version and content_hash),
                // depends_on changed, agent tampered with status, or maintainer fields were reconciled
                bool statusTampered = !string.IsNullOrWhiteSpace(item.Frontmatter.Status) &&
                                      !string.Equals(item.Frontmatter.Status.Trim(), task.Status.ToString(), StringComparison.OrdinalIgnoreCase);

                var effectiveDiskMergeMode = ParseMergeMode(item.Frontmatter.MergeMode) ?? Domain.MergeMode.ManualReviewRequired;
                var effectiveDiskPriority = item.Frontmatter.Priority > 0 ? item.Frontmatter.Priority : TaskSyncConstants.DefaultPriority;
                var effectiveDiskBaseBranch = !string.IsNullOrWhiteSpace(item.Frontmatter.BaseBranch) ? item.Frontmatter.BaseBranch.Trim() : (workspace.DefaultBaseBranch ?? WorkflowDefaults.DefaultBaseBranch);

                var currentDbDepIds = task.Dependencies?.Select(d => d.DependsOnTaskId).OrderBy(x => x).ToList() ?? new List<int>();
                var currentDiskDepIds = (finalDepIds ?? (resolvedExistingDeps.TryGetValue(taskId, out var resDeps)
                    ? resDeps.ExistingDepIds
                    : new List<int>())).OrderBy(x => x).ToList();
                bool depsDifferFromDisk = !currentDiskDepIds.SequenceEqual(currentDbDepIds);

                bool maintainerFieldsSynced = (baselineSnapshot != null || (currentTaskId.HasValue && taskId == currentTaskId.Value)) &&
                                              (task.MergeMode != effectiveDiskMergeMode ||
                                               task.Priority != effectiveDiskPriority ||
                                               !string.Equals(task.BaseBranch, effectiveDiskBaseBranch, StringComparison.Ordinal) ||
                                               !string.Equals(task.Title?.Trim(), item.Frontmatter.Title?.Trim(), StringComparison.Ordinal) ||
                                               depsDifferFromDisk);

                var isArchived = IsArchivedStatus(task.Status);
                var effectiveTargetIndex = isArchived ? archiveIndex : tasksIndex;
                var oppositeTargetIndex = isArchived ? tasksIndex : archiveIndex;
                var effectiveHostIndex = hostTasksDir != null ? (isArchived ? hostArchiveIndex : hostTasksIndex) : null;
                var oppositeHostIndex = hostTasksDir != null ? (isArchived ? hostTasksIndex : hostArchiveIndex) : null;
                var targetWritePath = isArchived
                    ? Path.Combine(archiveDir, Path.GetFileName(item.FilePath))
                    : item.FilePath;

                if (anyFieldChanged || finalDepIds != null || statusTampered || maintainerFieldsSynced || isArchived)
                {
                    Directory.CreateDirectory(effectiveTargetIndex.DirectoryPath);
                    var effectiveDeps = finalDepIds ?? task.Dependencies?.Select(d => d.DependsOnTaskId).ToList() ?? new List<int>();
                    var normalizedMarkdown = GenerateMarkdown(task, effectiveDeps, item.Frontmatter.AgentProfile);
                    var normHash = ComputeContentHash(normalizedMarkdown);
                    CleanupOtherFilesForTaskId(effectiveTargetIndex, oppositeTargetIndex, task.Id, targetWritePath);
                    _suppressedHashes[normHash] = 1;
                    await File.WriteAllTextAsync(targetWritePath, normalizedMarkdown.Replace("\r\n", "\n"), Utf8NoBom, cancellationToken);

                    effectiveTargetIndex.UpsertEntry(new DirectoryTaskEntry
                    {
                        FilePath = targetWritePath,
                        FileName = Path.GetFileName(targetWritePath),
                        TaskId = task.Id,
                        Version = task.Version,
                        ContentHash = normHash,
                        Frontmatter = item.Frontmatter,
                        PreferredSlug = GenerateSlug(task.Title ?? string.Empty)
                    });

                    if (effectiveHostIndex != null && oppositeHostIndex != null)
                    {
                        Directory.CreateDirectory(effectiveHostIndex.DirectoryPath);
                        var hostFile = Path.Combine(effectiveHostIndex.DirectoryPath, Path.GetFileName(targetWritePath));
                        CleanupOtherFilesForTaskId(effectiveHostIndex, oppositeHostIndex, task.Id, hostFile);

                        bool hostMatches = effectiveHostIndex.TryGetEntryByPath(hostFile, out var existingHost) &&
                            string.Equals(existingHost.ContentHash, normHash, StringComparison.Ordinal);

                        if (!hostMatches)
                        {
                            _suppressedHashes[normHash] = 1;
                            await File.WriteAllTextAsync(hostFile, normalizedMarkdown.Replace("\r\n", "\n"), Utf8NoBom, cancellationToken);

                            effectiveHostIndex.UpsertEntry(new DirectoryTaskEntry
                            {
                                FilePath = hostFile,
                                FileName = Path.GetFileName(hostFile),
                                TaskId = task.Id,
                                Version = task.Version,
                                ContentHash = normHash,
                                Frontmatter = item.Frontmatter,
                                PreferredSlug = GenerateSlug(task.Title ?? string.Empty)
                            });
                        }
                    }
                }
                else if (effectiveHostIndex != null && oppositeHostIndex != null)
                {
                    Directory.CreateDirectory(effectiveHostIndex.DirectoryPath);
                    var hostFile = Path.Combine(effectiveHostIndex.DirectoryPath, Path.GetFileName(targetWritePath));
                    CleanupOtherFilesForTaskId(effectiveHostIndex, oppositeHostIndex, task.Id, hostFile);

                    bool hostMatches = effectiveHostIndex.TryGetEntryByPath(hostFile, out var existingHost) &&
                        string.Equals(existingHost.ContentHash, item.Hash, StringComparison.Ordinal);

                    if (!hostMatches)
                    {
                        _suppressedHashes[item.Hash] = 1;
                        await File.WriteAllTextAsync(hostFile, item.Content.Replace("\r\n", "\n"), Utf8NoBom, cancellationToken);

                        effectiveHostIndex.UpsertEntry(new DirectoryTaskEntry
                        {
                            FilePath = hostFile,
                            FileName = Path.GetFileName(hostFile),
                            TaskId = task.Id,
                            Version = task.Version,
                            ContentHash = item.Hash,
                            Frontmatter = item.Frontmatter,
                            PreferredSlug = GenerateSlug(task.Title ?? string.Empty)
                        });
                    }
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

        // If filePath is provided and in a directory, index existing files in the same directory and archive directory
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                var archiveSubDir = Path.Combine(dir, TaskSyncConstants.ArchiveSegment);
                var activeIndexTask = DirectoryTaskIndex.CreateAsync(dir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: false, failFast: false, cancellationToken);
                var archiveIndexTask = DirectoryTaskIndex.CreateAsync(archiveSubDir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: false, failFast: false, cancellationToken);

                await Task.WhenAll(activeIndexTask, archiveIndexTask);
                DirectoryTaskIndex[] indices = [await activeIndexTask, await archiveIndexTask];

                var normalizedCurrentFilePath = Path.GetFullPath(filePath).TrimEnd('/', '\\');
                foreach (var idx in indices)
                {
                    if (idx == null) continue;
                    foreach (var entry in idx.AllEntries)
                    {
                        if (string.Equals(entry.FilePath, normalizedCurrentFilePath, StringComparison.OrdinalIgnoreCase)) continue;
                        if (entry.TaskId.HasValue && entry.TaskId.Value > 0 && dbTasks.ContainsKey(entry.TaskId.Value))
                        {
                            var id = entry.TaskId.Value;
                            var fileName = entry.FileName;
                            lookup.TryAdd(fileName, id);
                            lookup.TryAdd(Path.GetFileNameWithoutExtension(fileName), id);
                            var fileSlug = NormalizeDependencyKey(fileName);
                            if (!string.IsNullOrEmpty(fileSlug))
                            {
                                lookup.TryAdd(fileSlug, id);
                            }
                        }
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

        if (!string.IsNullOrWhiteSpace(frontmatter.ContentHash) &&
            string.Equals(frontmatter.ContentHash.Trim(), ComputeTaskContentHash(frontmatter, description), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

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

                    if (frontmatter.Version.HasValue && task.Version > frontmatter.Version.Value)
                    {
                        _logger.LogInformation(
                            "Task #{TaskId} database version ({DbVersion}) is more recent than disk version ({DiskVersion}). Retaining authoritative database state.",
                            task.Id, task.Version, frontmatter.Version.Value);
                        return;
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

                    bool anyFieldChanged = titleChanged || descChanged || priorityChanged || depsChanged || mergeModeChanged || baseBranchChanged || originChanged;
                    if (anyFieldChanged)
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

                        if (resolvedDeps != null)
                        {
                            task.Dependencies = resolvedDeps.Select(depId => new TaskDependency
                            {
                                TaskId = task.Id,
                                DependsOnTaskId = depId
                            }).ToList();
                        }
                    }

                    // In-place normalization for existing files if fields changed (to update version and content_hash) or depends_on had references
                    if ((anyFieldChanged || resolvedDeps != null) && File.Exists(filePath))
                    {
                        var effectiveDeps = resolvedDeps ?? task.Dependencies?.Select(d => d.DependsOnTaskId).ToList() ?? new List<int>();
                        frontmatter.DependsOn = new TaskDependencyReferenceList(effectiveDeps);
                        var normalizedMarkdown = GenerateMarkdown(task, effectiveDeps, frontmatter.AgentProfile);
                        var normHash = ComputeContentHash(normalizedMarkdown);
                        _suppressedHashes[normHash] = 1;
                        await File.WriteAllTextAsync(filePath, normalizedMarkdown.Replace("\r\n", "\n"), Utf8NoBom, cancellationToken);
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
                await File.WriteAllTextAsync(filePath, inPlaceMarkdown.Replace("\r\n", "\n"), Utf8NoBom, cancellationToken);
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Best-effort cleanup in clone tasks directory '{CloneTasksDir}' failed for task #{TaskId}", cloneTasksDir, task.Id);
            }
        }
    }

    private async Task ArchiveOrCleanupInDirectoryAsync(string tasksDir, int taskId, bool archive, CancellationToken cancellationToken)
    {
        if (taskId <= 0) return;

        var archiveDir = Path.Combine(tasksDir, TaskSyncConstants.ArchiveSegment);

        var tasksIndexTask = DirectoryTaskIndex.CreateAsync(tasksDir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: false, failFast: false, cancellationToken);
        var archiveIndexTask = DirectoryTaskIndex.CreateAsync(archiveDir, ParseMarkdown, ComputeContentHash, _logger, isRemoteOrParallel: false, failFast: false, cancellationToken);

        await Task.WhenAll(tasksIndexTask, archiveIndexTask);
        var tasksIndex = await tasksIndexTask;
        var archiveIndex = await archiveIndexTask;

        if (tasksIndex != null)
        {
            var matchingEntries = tasksIndex.GetFilesByTaskId(taskId).ToList();
            foreach (var entry in matchingEntries)
            {
                try
                {
                    if (archive)
                    {
                        Directory.CreateDirectory(archiveDir);
                        var target = Path.Combine(archiveDir, entry.FileName);
                        if (File.Exists(target)) File.Delete(target);
                        if (File.Exists(entry.FilePath)) File.Move(entry.FilePath, target);
                        tasksIndex.RemoveEntry(entry.FilePath);

                        archiveIndex ??= new DirectoryTaskIndex(archiveDir);
                        archiveIndex.UpsertEntry(new DirectoryTaskEntry
                        {
                            FilePath = target,
                            FileName = entry.FileName,
                            TaskId = taskId,
                            Version = entry.Version,
                            ContentHash = entry.ContentHash,
                            Frontmatter = entry.Frontmatter,
                            PreferredSlug = entry.PreferredSlug
                        });
                        CleanupOtherFilesForTaskId(archiveIndex, oppositeIndex: null, taskId, target);
                    }
                    else
                    {
                        if (File.Exists(entry.FilePath)) File.Delete(entry.FilePath);
                        tasksIndex.RemoveEntry(entry.FilePath);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Ignored unreadable or locked candidate file '{FilePath}' during archive/cleanup for task #{TaskId}", entry.FilePath, taskId);
                }
            }
        }

        if (!archive && archiveIndex != null)
        {
            var archivedEntries = archiveIndex.GetFilesByTaskId(taskId).ToList();
            foreach (var entry in archivedEntries)
            {
                try
                {
                    if (File.Exists(entry.FilePath)) File.Delete(entry.FilePath);
                    archiveIndex.RemoveEntry(entry.FilePath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Ignored unreadable or locked archived file '{FilePath}' during cleanup for task #{TaskId}", entry.FilePath, taskId);
                }
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
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignored transient I/O conflict during watcher dispatch for '{FilePath}'", fullPath);
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
