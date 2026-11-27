using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Web.Services;

public enum TaskFileResolutionOutcome
{
    TaskFound = 0,
    FallbackToFileViewer = 1,
    InvalidPath = 2,
    WorkspaceNotFound = 3,
    NotFound = 4
}

public sealed record TaskFileResolutionResult(
    TaskFileResolutionOutcome Outcome,
    int? TaskId,
    string? CanonicalPath,
    string? ResolvedRef,
    int? TaskIdHint,
    string? ErrorMessage);

public interface ITaskFileResolverService
{
    Task<TaskFileResolutionResult> ResolveAsync(
        int workspaceId,
        string? rawPath,
        string? requestedRef,
        int? taskIdHint,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves a repository task markdown path (<c>docs/tasks/[archive/]*.md</c>) to a workspace
/// <see cref="ForgeTask"/> via Git frontmatter, uncommitted synced disk files, or slug/date matching,
/// falling back to the Step 12.46A repository file viewer when the file exists in Git without a DB task.
/// </summary>
public sealed class TaskFileResolverService : ITaskFileResolverService
{
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly ITaskRepository _taskRepository;
    private readonly IGitRepositoryBrowserService _gitBrowserService;
    private readonly ITaskMarkdownSyncService _markdownSyncService;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly ILogger<TaskFileResolverService> _logger;

    public TaskFileResolverService(
        IWorkspaceRepository workspaceRepository,
        ITaskRepository taskRepository,
        IGitRepositoryBrowserService gitBrowserService,
        ITaskMarkdownSyncService markdownSyncService,
        IWorkspacePathResolver pathResolver,
        ILogger<TaskFileResolverService> logger)
    {
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _gitBrowserService = gitBrowserService ?? throw new ArgumentNullException(nameof(gitBrowserService));
        _markdownSyncService = markdownSyncService ?? throw new ArgumentNullException(nameof(markdownSyncService));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TaskFileResolutionResult> ResolveAsync(
        int workspaceId,
        string? rawPath,
        string? requestedRef,
        int? taskIdHint,
        CancellationToken cancellationToken = default)
    {
        if (!MarkdownLinkBuilder.TryNormalizeTaskMarkdownPath(rawPath, out var canonicalPath, out var isArchived, out var fileName))
        {
            return new TaskFileResolutionResult(
                TaskFileResolutionOutcome.InvalidPath,
                TaskId: null,
                CanonicalPath: rawPath,
                ResolvedRef: null,
                TaskIdHint: taskIdHint,
                ErrorMessage: $"Path '{rawPath ?? string.Empty}' is not a valid task markdown path under docs/tasks/.");
        }

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace == null)
        {
            return new TaskFileResolutionResult(
                TaskFileResolutionOutcome.WorkspaceNotFound,
                TaskId: null,
                CanonicalPath: canonicalPath,
                ResolvedRef: null,
                TaskIdHint: taskIdHint,
                ErrorMessage: $"Workspace #{workspaceId} was not found.");
        }

        var effectiveRef = FileViewerPresentationHelper.ResolveEffectiveRef(requestedRef, workspace.DefaultBaseBranch);

        // Step 1: Attempt to read the task markdown from Git at the requested ref (with Step 12.46A task fallback)
        var existsInGit = false;
        string? gitResolvedRef = null;
        string? markdownContent = null;

        try
        {
            var inspection = await _gitBrowserService.InspectPathAsync(
                workspaceId,
                effectiveRef,
                canonicalPath,
                taskIdHint,
                cancellationToken).ConfigureAwait(false);

            gitResolvedRef = inspection.ResolvedRef.ResolvedRef;
            if (inspection.ContentKind != GitFileContentKind.Directory)
            {
                existsInGit = true;
            }

            if (inspection.ContentKind == GitFileContentKind.Text && !string.IsNullOrWhiteSpace(inspection.TextContent))
            {
                markdownContent = inspection.TextContent;
            }
        }
        catch (Exception ex) when (ex is GitRepositoryBrowserException or GitCommandException)
        {
            _logger.LogDebug(
                ex,
                "Git lookup for task markdown '{Path}' at ref '{Ref}' in workspace #{WorkspaceId} did not return text content; falling back to host/clone disk and slug matching.",
                canonicalPath,
                effectiveRef,
                workspaceId);
        }

        // Step 2: Fall back to newly synced uncommitted host/clone markdown file with strict containment + symlink guards
        if (!existsInGit)
        {
            markdownContent = await TryReadUncommittedTaskFileAsync(
                workspace,
                isArchived,
                fileName,
                cancellationToken).ConfigureAwait(false);
        }

        // Step 3: Resolve frontmatter ID when file content was readable
        if (!string.IsNullOrWhiteSpace(markdownContent))
        {
            var resolvedByFrontmatter = await TryResolveTaskByFrontmatterIdAsync(
                workspaceId,
                canonicalPath,
                markdownContent,
                cancellationToken).ConfigureAwait(false);

            if (resolvedByFrontmatter != null)
            {
                return new TaskFileResolutionResult(
                    TaskFileResolutionOutcome.TaskFound,
                    TaskId: resolvedByFrontmatter.Id,
                    CanonicalPath: canonicalPath,
                    ResolvedRef: gitResolvedRef ?? effectiveRef,
                    TaskIdHint: taskIdHint,
                    ErrorMessage: null);
            }
        }

        // Step 4: Fall back to matching filename/slug/date/archive status across workspace tasks
        var workspaceTasks = await _taskRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var matchedTask = MatchTaskByFileMetadata(workspaceTasks, canonicalPath, fileName, isArchived, taskIdHint);
        if (matchedTask != null)
        {
            return new TaskFileResolutionResult(
                TaskFileResolutionOutcome.TaskFound,
                TaskId: matchedTask.Id,
                CanonicalPath: canonicalPath,
                ResolvedRef: gitResolvedRef ?? effectiveRef,
                TaskIdHint: taskIdHint,
                ErrorMessage: null);
        }

        // Step 5: Fall back to Step 12.46A File Viewer when file exists in Git without a DB task
        if (existsInGit)
        {
            return new TaskFileResolutionResult(
                TaskFileResolutionOutcome.FallbackToFileViewer,
                TaskId: null,
                CanonicalPath: canonicalPath,
                ResolvedRef: gitResolvedRef ?? effectiveRef,
                TaskIdHint: taskIdHint,
                ErrorMessage: null);
        }

        return new TaskFileResolutionResult(
            TaskFileResolutionOutcome.NotFound,
            TaskId: null,
            CanonicalPath: canonicalPath,
            ResolvedRef: effectiveRef,
            TaskIdHint: taskIdHint,
            ErrorMessage: $"No task or repository file was found for '{canonicalPath}'.");
    }

    private async Task<string?> TryReadUncommittedTaskFileAsync(
        Workspace workspace,
        bool isArchived,
        string fileName,
        CancellationToken cancellationToken)
    {
        var candidateBaseTasksDirs = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(workspace.RepositoryPath))
        {
            candidateBaseTasksDirs.Add(Path.Combine(workspace.RepositoryPath, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment));
        }

        var cloneTasksDir = await _markdownSyncService
            .ResolveCloneTasksDirectoryAsync(workspace.Id, workspace, workspace.RepositoryPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cloneTasksDir))
        {
            candidateBaseTasksDirs.Add(cloneTasksDir);
        }

        foreach (var baseTasksDir in candidateBaseTasksDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var content = await TryReadBoundedTaskFileFromDirectoryAsync(baseTasksDir, isArchived, fileName, cancellationToken)
                .ConfigureAwait(false);
            if (content != null)
            {
                return content;
            }
        }

        return null;
    }

    private async Task<string?> TryReadBoundedTaskFileFromDirectoryAsync(
        string baseTasksDir,
        bool isArchived,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseTasksDir) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        var hostBaseTasksDir = _pathResolver.ToHostPath(baseTasksDir);
        if (string.IsNullOrWhiteSpace(hostBaseTasksDir) || !Directory.Exists(hostBaseTasksDir))
        {
            return null;
        }

        string fullBaseTasksDir;
        string candidateFilePath;
        try
        {
            fullBaseTasksDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hostBaseTasksDir));
            var targetSubDir = isArchived
                ? Path.Combine(fullBaseTasksDir, TaskSyncConstants.ArchiveSegment)
                : fullBaseTasksDir;
            candidateFilePath = Path.GetFullPath(Path.Combine(targetSubDir, fileName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _logger.LogDebug(ex, "Invalid host task path '{BaseDir}' / '{FileName}'.", hostBaseTasksDir, fileName);
            return null;
        }

        // Strict containment check
        if (!SandboxPathResolver.IsPathInsideDirectory(candidateFilePath, fullBaseTasksDir))
        {
            return null;
        }

        var docsDir = Path.GetDirectoryName(fullBaseTasksDir);
        if (!string.IsNullOrEmpty(docsDir) &&
            string.Equals(Path.GetFileName(docsDir), TaskSyncConstants.DocsSegment, StringComparison.OrdinalIgnoreCase) &&
            AgentMemoryReparseGuard.IsReparsePoint(docsDir))
        {
            _logger.LogWarning("Rejected task markdown read through reparse point '{Path}'.", docsDir);
            return null;
        }

        if (AgentMemoryReparseGuard.HasReparsePointInChain(fullBaseTasksDir, candidateFilePath, includeRoot: true))
        {
            _logger.LogWarning("Rejected task markdown read through reparse point '{Path}'.", candidateFilePath);
            return null;
        }

        if (!File.Exists(candidateFilePath))
        {
            return null;
        }

        var fileInfo = new FileInfo(candidateFilePath);
        if (fileInfo.Length > GitRepositoryBrowserConstants.MaxPreviewFileBytes)
        {
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(candidateFilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed reading disk task markdown '{Path}'.", candidateFilePath);
            return null;
        }
    }

    private async Task<ForgeTask?> TryResolveTaskByFrontmatterIdAsync(
        int workspaceId,
        string canonicalPath,
        string markdownContent,
        CancellationToken cancellationToken)
    {
        try
        {
            var (frontmatter, _) = _markdownSyncService.ParseMarkdown(markdownContent, canonicalPath);
            if (!frontmatter.Id.HasValue || frontmatter.Id.Value <= 0)
            {
                return null;
            }

            var task = await _taskRepository.GetByIdAsync(frontmatter.Id.Value, cancellationToken).ConfigureAwait(false);
            return task != null && task.WorkspaceId == workspaceId ? task : null;
        }
        catch (TaskMarkdownSyncException ex)
        {
            _logger.LogDebug(ex, "Failed to parse frontmatter in '{Path}' during task-by-file resolution.", canonicalPath);
            return null;
        }
    }

    private static ForgeTask? MatchTaskByFileMetadata(
        IReadOnlyList<ForgeTask>? tasks,
        string canonicalPath,
        string fileName,
        bool isArchived,
        int? taskIdHint)
    {
        if (tasks == null || tasks.Count == 0)
        {
            return null;
        }

        var targetSlug = TaskMarkdownSyncService.NormalizeDependencyKey(fileName);
        if (string.IsNullOrEmpty(targetSlug))
        {
            return null;
        }

        var targetDate = TaskMarkdownSyncService.TryExtractDateFromPath(canonicalPath);

        var slugMatches = tasks
            .Where(t => t != null &&
                        t.Id > 0 &&
                        string.Equals(TaskMarkdownSyncService.GenerateSlug(t.Title), targetSlug, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (slugMatches.Count == 0)
        {
            return null;
        }

        return slugMatches
            .OrderByDescending(t => TaskMarkdownSyncService.IsArchivedStatus(t.Status) == isArchived)
            .ThenByDescending(t => targetDate.HasValue && (t.CreatedAt.Kind == DateTimeKind.Local ? t.CreatedAt.ToUniversalTime() : t.CreatedAt).Date == targetDate.Value.Date)
            .ThenByDescending(t => taskIdHint.HasValue && t.Id == taskIdHint.Value)
            .ThenByDescending(t => t.UpdatedAt)
            .ThenBy(t => t.Id)
            .FirstOrDefault();
    }
}
