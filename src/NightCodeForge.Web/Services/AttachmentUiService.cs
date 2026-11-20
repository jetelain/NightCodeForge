using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Vision;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Web UI coordination service encapsulating attachment intake, citation ingestion, preview loading,
/// filtering, and host-safe deletion outside Blazor Razor components.
/// </summary>
public interface IAttachmentUiService
{
    Task<IReadOnlyList<TaskAttachment>> LoadWorkspaceAttachmentsAsync(
        int workspaceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskAttachment>> LoadTaskAttachmentsAsync(
        ForgeTask? task,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskAttachment>> LoadTaskAttachmentsAsync(
        int taskId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskAttachment>> ResolveAttachmentsByIdsAsync(
        int workspaceId,
        IEnumerable<int>? attachmentIds,
        string? description = null,
        CancellationToken cancellationToken = default);

    Task<TaskAttachment> UploadFileAsync(
        int workspaceId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<TaskAttachment> AttachHostFileAsync(
        int workspaceId,
        string hostPath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationAttachmentChipViewModel>> ResolveCitedAttachmentChipsAsync(
        int workspaceId,
        string? message,
        CancellationToken cancellationToken = default);

    Task<AttachmentPreviewModel> LoadPreviewAsync(
        int attachmentId,
        CancellationToken cancellationToken = default);

    Task<AttachmentDeleteImpact> GetDeleteImpactAsync(
        int attachmentId,
        CancellationToken cancellationToken = default);

    Task DeleteAttachmentAsync(
        int attachmentId,
        CancellationToken cancellationToken = default);

    Task<VisionCapabilityStatus> EvaluateIdeationVisionAsync(
        int workspaceId,
        string? modelOverride,
        CancellationToken cancellationToken = default);

    Task<VisionCapabilityStatus> EvaluateTaskVisionAsync(
        int workspaceId,
        TaskDifficulty difficulty,
        CancellationToken cancellationToken = default);

    Task<string?> LoadImageThumbnailDataUriAsync(
        int attachmentId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class AttachmentUiService : IAttachmentUiService
{
    private readonly IAttachmentIntakeService _intakeService;
    private readonly ITaskAttachmentRepository _attachmentRepository;
    private readonly ITaskRepository _taskRepository;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IIdeationConversationCoordinator _ideationCoordinator;
    private readonly IVisionCapabilityUiService _visionCapabilityUiService;
    private readonly ITaskMarkdownSyncService _markdownSyncService;
    private readonly IWorkspaceLockProvider _lockProvider;
    private readonly ILogger<AttachmentUiService> _logger;

    public AttachmentUiService(
        IAttachmentIntakeService intakeService,
        ITaskAttachmentRepository attachmentRepository,
        ITaskRepository taskRepository,
        IWorkspaceRepository workspaceRepository,
        IWorkspacePathResolver pathResolver,
        IIdeationConversationCoordinator ideationCoordinator,
        IVisionCapabilityUiService visionCapabilityUiService,
        ITaskMarkdownSyncService markdownSyncService,
        IWorkspaceLockProvider lockProvider,
        ILogger<AttachmentUiService> logger)
    {
        _intakeService = intakeService ?? throw new ArgumentNullException(nameof(intakeService));
        _attachmentRepository = attachmentRepository ?? throw new ArgumentNullException(nameof(attachmentRepository));
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _ideationCoordinator = ideationCoordinator ?? throw new ArgumentNullException(nameof(ideationCoordinator));
        _visionCapabilityUiService = visionCapabilityUiService ?? throw new ArgumentNullException(nameof(visionCapabilityUiService));
        _markdownSyncService = markdownSyncService ?? throw new ArgumentNullException(nameof(markdownSyncService));
        _lockProvider = lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<TaskAttachment>> LoadWorkspaceAttachmentsAsync(
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId <= 0)
        {
            return Array.Empty<TaskAttachment>();
        }

        var fromRepo = await _attachmentRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken)
            ?? Array.Empty<TaskAttachment>();
        if (fromRepo.Count > 0)
        {
            return fromRepo.OrderBy(a => a.Id).ToList();
        }

        var fromIntake = await _intakeService.ListWorkspaceAttachmentsAsync(workspaceId, cancellationToken);
        return fromIntake?
            .OrderBy(a => a.Id)
            .ToList() ?? new List<TaskAttachment>();
    }

    public async Task<IReadOnlyList<TaskAttachment>> LoadTaskAttachmentsAsync(
        ForgeTask? task,
        CancellationToken cancellationToken = default)
    {
        if (task == null || task.Id <= 0)
        {
            return Array.Empty<TaskAttachment>();
        }

        var byId = new Dictionary<int, TaskAttachment>();

        var persisted = await _attachmentRepository.GetByTaskIdAsync(task.Id, cancellationToken)
            ?? Array.Empty<TaskAttachment>();
        foreach (var att in persisted)
        {
            if (att != null)
            {
                byId[att.Id] = att;
            }
        }

        if (byId.Count == 0 && task.AttachmentLinks is { Count: > 0 })
        {
            var linkIds = task.AttachmentLinks
                .Select(l => l.TaskAttachmentId)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            if (linkIds.Count > 0)
            {
                var resolved = await _attachmentRepository.GetByIdsAsync(linkIds, cancellationToken)
                    ?? Array.Empty<TaskAttachment>();
                foreach (var att in resolved)
                {
                    if (att != null && (task.WorkspaceId <= 0 || att.WorkspaceId == task.WorkspaceId))
                    {
                        byId[att.Id] = att;
                    }
                }
            }

            if (byId.Count == 0)
            {
                foreach (var navAtt in task.AttachmentLinks.Select(l => l.TaskAttachment))
                {
                    if (navAtt != null && (task.WorkspaceId <= 0 || navAtt.WorkspaceId == task.WorkspaceId))
                    {
                        byId[navAtt.Id] = navAtt;
                    }
                }
            }
        }

        var citedIds = AttachmentCitationParser.ExtractAttachmentIds(task.Description);
        var missingCitedIds = citedIds.Where(id => id > 0 && !byId.ContainsKey(id)).ToList();
        if (missingCitedIds.Count > 0)
        {
            var citedAttachments = await _attachmentRepository.GetByIdsAsync(missingCitedIds, cancellationToken)
                ?? Array.Empty<TaskAttachment>();
            foreach (var att in citedAttachments)
            {
                if (att != null && (task.WorkspaceId <= 0 || att.WorkspaceId == task.WorkspaceId))
                {
                    byId[att.Id] = att;
                }
            }
        }

        return byId.Values.OrderBy(a => a.Id).ToList();
    }

    public async Task<IReadOnlyList<TaskAttachment>> LoadTaskAttachmentsAsync(
        int taskId,
        CancellationToken cancellationToken = default)
    {
        if (taskId <= 0)
        {
            return Array.Empty<TaskAttachment>();
        }

        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task != null)
        {
            return await LoadTaskAttachmentsAsync(task, cancellationToken);
        }

        var persisted = await _attachmentRepository.GetByTaskIdAsync(taskId, cancellationToken)
            ?? Array.Empty<TaskAttachment>();
        return persisted.OrderBy(a => a.Id).ToList();
    }

    public async Task<IReadOnlyList<TaskAttachment>> ResolveAttachmentsByIdsAsync(
        int workspaceId,
        IEnumerable<int>? attachmentIds,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId <= 0)
        {
            return Array.Empty<TaskAttachment>();
        }

        var unionIds = AttachmentCitationParser.UnionAttachmentIds(attachmentIds, description)
            .Where(id => id > 0)
            .ToList();
        if (unionIds.Count == 0)
        {
            return Array.Empty<TaskAttachment>();
        }

        var loaded = await _attachmentRepository.GetByIdsAsync(unionIds, cancellationToken)
            ?? Array.Empty<TaskAttachment>();
        return loaded
            .Where(a => a.WorkspaceId == workspaceId)
            .OrderBy(a => a.Id)
            .ToList();
    }

    public async Task<TaskAttachment> UploadFileAsync(
        int workspaceId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var attachment = await _intakeService.IngestFileDropAsync(workspaceId, fileName, content, cancellationToken);
        _ideationCoordinator.MarkWorkspaceContextDirty(workspaceId);
        return attachment;
    }

    public async Task<TaskAttachment> AttachHostFileAsync(
        int workspaceId,
        string hostPath,
        CancellationToken cancellationToken = default)
    {
        var attachment = await _intakeService.IngestHostPathAsync(workspaceId, hostPath, cancellationToken);
        _ideationCoordinator.MarkWorkspaceContextDirty(workspaceId);
        return attachment;
    }

    public async Task<IReadOnlyList<ConversationAttachmentChipViewModel>> ResolveCitedAttachmentChipsAsync(
        int workspaceId,
        string? message,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId <= 0 || string.IsNullOrWhiteSpace(message))
        {
            return Array.Empty<ConversationAttachmentChipViewModel>();
        }

        var citedIds = AttachmentCitationParser.ExtractAttachmentIds(message);
        if (citedIds.Count == 0)
        {
            return Array.Empty<ConversationAttachmentChipViewModel>();
        }

        var loaded = await _attachmentRepository.GetByIdsAsync(citedIds, cancellationToken)
            ?? Array.Empty<TaskAttachment>();
        var existingById = new Dictionary<int, TaskAttachment>();
        foreach (var att in loaded)
        {
            if (att != null && att.WorkspaceId == workspaceId)
            {
                existingById[att.Id] = att;
            }
        }

        var chips = new List<ConversationAttachmentChipViewModel>();
        foreach (var citedId in citedIds)
        {
            if (existingById.TryGetValue(citedId, out var existing))
            {
                AttachmentPresentationHelper.UpsertChip(chips, ConversationAttachmentChipViewModel.FromAttachment(existing));
            }
        }

        return chips;
    }

    public async Task<AttachmentPreviewModel> LoadPreviewAsync(
        int attachmentId,
        CancellationToken cancellationToken = default)
    {
        var attachment = await RequireAttachmentAsync(attachmentId, cancellationToken);
        var linkedTasks = await _attachmentRepository.GetLinkedTasksAsync(attachmentId, cancellationToken)
            ?? Array.Empty<ForgeTask>();
        var workspace = attachment.WorkspaceId > 0
            ? await _workspaceRepository.GetByIdAsync(attachment.WorkspaceId, cancellationToken)
            : null;

        try
        {
            var hostAttachmentsDir = AttachmentHostPathResolver.ResolveHostAttachmentsDirectory(attachment.WorkspaceId, _pathResolver);
            var isTargetReachable = IsSymlinkTargetReachable(attachment);

            if (attachment.IsSymlink && !isTargetReachable)
            {
                return new AttachmentPreviewModel
                {
                    Attachment = attachment,
                    WorkspaceName = workspace?.Name,
                    IsTargetReachable = false,
                    LinkedTasks = linkedTasks
                };
            }

            if (attachment.ContentKind == TaskAttachmentContentKind.Image)
            {
                var (imageDataUri, imageReadError) = await TryLoadImageDataUriWithErrorAsync(
                    attachment,
                    hostAttachmentsDir,
                    downscaleForThumbnail: false,
                    cancellationToken);

                return new AttachmentPreviewModel
                {
                    Attachment = attachment,
                    WorkspaceName = workspace?.Name,
                    ImageDataUri = imageDataUri,
                    ContentReadError = imageDataUri == null ? imageReadError : null,
                    IsTargetReachable = isTargetReachable,
                    LinkedTasks = linkedTasks
                };
            }

            var candidates = AttachmentHostPathResolver.EnumerateReadableAttachmentPaths(
                attachment,
                hostAttachmentsDir,
                _pathResolver);

            var markdownContent = string.Empty;
            string? lastReadError = null;
            var readSucceeded = false;

            foreach (var candidate in candidates)
            {
                try
                {
                    markdownContent = await File.ReadAllTextAsync(candidate, cancellationToken);
                    readSucceeded = true;
                    lastReadError = null;
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed reading preview content for attachment #{AttachmentId} from '{Path}'.",
                        attachment.Id,
                        candidate);
                    lastReadError ??= ex.Message;
                }
            }

            var renderedHtml = readSucceeded ? MarkdownHelper.ToHtml(markdownContent) : string.Empty;
            return new AttachmentPreviewModel
            {
                Attachment = attachment,
                WorkspaceName = workspace?.Name,
                MarkdownContent = markdownContent,
                RenderedHtml = renderedHtml,
                ContentReadError = readSucceeded ? null : lastReadError,
                IsTargetReachable = isTargetReachable,
                LinkedTasks = linkedTasks
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed resolving preview paths or reading content for attachment #{AttachmentId}; returning degraded metadata preview.",
                attachmentId);
            return new AttachmentPreviewModel
            {
                Attachment = attachment,
                WorkspaceName = workspace?.Name,
                IsTargetReachable = true,
                IsReachabilityUnknown = attachment.IsSymlink,
                ContentReadError = ex.Message,
                LinkedTasks = linkedTasks
            };
        }
    }

    public async Task<AttachmentDeleteImpact> GetDeleteImpactAsync(
        int attachmentId,
        CancellationToken cancellationToken = default)
    {
        var attachment = await RequireAttachmentAsync(attachmentId, cancellationToken);
        var linkedTasks = await _attachmentRepository.GetLinkedTasksAsync(attachmentId, cancellationToken)
            ?? Array.Empty<ForgeTask>();
        return new AttachmentDeleteImpact(attachment, linkedTasks);
    }

    public async Task DeleteAttachmentAsync(
        int attachmentId,
        CancellationToken cancellationToken = default)
    {
        var existing = await _attachmentRepository.GetByIdAsync(attachmentId, cancellationToken);
        var linkedTaskIds = existing != null
            ? (await _attachmentRepository.GetLinkedTaskIdsAsync(attachmentId, cancellationToken) ?? Array.Empty<int>()).ToHashSet()
            : new HashSet<int>();

        await _intakeService.DeleteAttachmentAsync(attachmentId, cancellationToken);

        // If a test mocked IAttachmentIntakeService without backing the shared repository, ensure DB/repo link cleanup too.
        if (await _attachmentRepository.GetByIdAsync(attachmentId, cancellationToken) != null)
        {
            await _attachmentRepository.DeleteAsync(attachmentId, cancellationToken);
        }

        if (existing is { WorkspaceId: > 0 })
        {
            try
            {
                await CleanupDeletedAttachmentReferencesAsync(existing, linkedTaskIds, cancellationToken);
            }
            finally
            {
                _ideationCoordinator.MarkWorkspaceContextDirty(existing.WorkspaceId);
            }
        }
    }

    private async Task CleanupDeletedAttachmentReferencesAsync(
        TaskAttachment deletedAttachment,
        HashSet<int> preLinkedTaskIds,
        CancellationToken cancellationToken)
    {
        var workspaceTasks = await _taskRepository.GetByWorkspaceIdAsync(deletedAttachment.WorkspaceId, cancellationToken)
            ?? Array.Empty<ForgeTask>();
        if (workspaceTasks.Count == 0)
        {
            return;
        }

        var candidateTaskIds = new List<int>();
        foreach (var task in workspaceTasks)
        {
            if (task == null ||
                task.Status == TaskOverallStatus.Triage ||
                TaskMarkdownSyncService.IsArchivedStatus(task.Status))
            {
                continue;
            }

            var citedIds = AttachmentCitationParser.ExtractAttachmentIds(task.Description);
            var hasCitation = citedIds.Contains(deletedAttachment.Id);
            var hasInMemoryLink = task.AttachmentLinks?.Any(l => l.TaskAttachmentId == deletedAttachment.Id) ?? false;
            var hadLink = preLinkedTaskIds.Contains(task.Id) || hasInMemoryLink;
            if (hasCitation || hadLink)
            {
                candidateTaskIds.Add(task.Id);
            }
        }

        var distinctCandidateIds = candidateTaskIds.Distinct().ToList();
        if (distinctCandidateIds.Count == 0)
        {
            return;
        }

        var workspace = await _workspaceRepository.GetByIdAsync(deletedAttachment.WorkspaceId, cancellationToken);
        var canSyncToDisk = workspace != null &&
            !string.IsNullOrWhiteSpace(workspace.RepositoryPath) &&
            Directory.Exists(workspace.RepositoryPath);

        foreach (var taskId in distinctCandidateIds)
        {
            try
            {
                if (!_lockProvider.TryAcquireTaskLock(deletedAttachment.WorkspaceId, taskId, out var taskLock) ||
                    taskLock == null)
                {
                    _logger.LogWarning(
                        "Skipping attachment #{AttachmentId} citation cleanup for task #{TaskId} because the task is currently locked.",
                        deletedAttachment.Id,
                        taskId);
                    continue;
                }

                using (taskLock)
                {
                    var freshTask = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
                    if (freshTask == null ||
                        freshTask.Status == TaskOverallStatus.Triage ||
                        TaskMarkdownSyncService.IsArchivedStatus(freshTask.Status) ||
                        BacklogStatusClassifier.GetColumn(freshTask) == BacklogKanbanColumn.InFlight)
                    {
                        continue;
                    }

                    var hasCitation = AttachmentCitationParser.ExtractAttachmentIds(freshTask.Description)
                        .Contains(deletedAttachment.Id);
                    if (hasCitation)
                    {
                        var updatedDescription = AttachmentCitationParser.RemoveAttachmentCitation(
                            freshTask.Description,
                            deletedAttachment.Id,
                            deletedAttachment.Title);
                        if (!string.Equals(updatedDescription, freshTask.Description, StringComparison.Ordinal))
                        {
                            freshTask.Description = updatedDescription;
                            freshTask.UpdatedAt = DateTime.UtcNow;
                            var reason = $"Removed citation to deleted attachment #{deletedAttachment.Id} ('{deletedAttachment.Title}').";
                            await _taskRepository.UpdateWithHistoryAsync(
                                freshTask,
                                dependsOn: null,
                                changedBy: AuditActors.User,
                                reason: reason,
                                cancellationToken: cancellationToken);
                        }
                    }

                    if (canSyncToDisk)
                    {
                        var remaining = await _attachmentRepository.GetByTaskIdAsync(freshTask.Id, cancellationToken)
                            ?? Array.Empty<TaskAttachment>();
                        freshTask.AttachmentLinks = remaining
                            .Where(a => a != null && a.Id > 0 && a.Id != deletedAttachment.Id)
                            .Select(a => new TaskAttachmentLink
                            {
                                TaskId = freshTask.Id,
                                TaskAttachmentId = a.Id
                            })
                            .ToList();

                        var deps = (await _taskRepository.GetDependenciesAsync(freshTask.Id, cancellationToken))?.ToList()
                            ?? new List<int>();
                        await _markdownSyncService.SyncTaskToDiskAsync(
                            freshTask,
                            workspace!.RepositoryPath,
                            deps,
                            cancellationToken: cancellationToken);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to clean up deleted attachment #{AttachmentId} references on task #{TaskId}.",
                    deletedAttachment.Id,
                    taskId);
            }
        }
    }

    public Task<VisionCapabilityStatus> EvaluateIdeationVisionAsync(
        int workspaceId,
        string? modelOverride,
        CancellationToken cancellationToken = default)
        => _visionCapabilityUiService.EvaluateWorkspaceStageVisionAsync(
            workspaceId,
            StandardStageIds.Ideation,
            difficulty: null,
            modelOverride: modelOverride,
            cancellationToken: cancellationToken);

    public Task<VisionCapabilityStatus> EvaluateTaskVisionAsync(
        int workspaceId,
        TaskDifficulty difficulty,
        CancellationToken cancellationToken = default)
        => _visionCapabilityUiService.EvaluateWorkspaceTaskVisionAsync(
            workspaceId,
            difficulty,
            cancellationToken);

    public async Task<string?> LoadImageThumbnailDataUriAsync(
        int attachmentId,
        CancellationToken cancellationToken = default)
    {
        if (attachmentId <= 0)
        {
            return null;
        }

        var attachment = await _attachmentRepository.GetByIdAsync(attachmentId, cancellationToken)
            ?? await _intakeService.GetAttachmentAsync(attachmentId, cancellationToken);
        if (attachment == null || attachment.ContentKind != TaskAttachmentContentKind.Image)
        {
            return null;
        }

        var hostAttachmentsDir = AttachmentHostPathResolver.ResolveHostAttachmentsDirectory(attachment.WorkspaceId, _pathResolver);
        var isTargetReachable = IsSymlinkTargetReachable(attachment);
        if (attachment.IsSymlink && !isTargetReachable)
        {
            return null;
        }

        var (dataUri, _) = await TryLoadImageDataUriWithErrorAsync(
            attachment,
            hostAttachmentsDir,
            downscaleForThumbnail: true,
            cancellationToken);
        return dataUri;
    }

    private async Task<(string? DataUri, string? ReadError)> TryLoadImageDataUriWithErrorAsync(
        TaskAttachment attachment,
        string hostAttachmentsDir,
        bool downscaleForThumbnail,
        CancellationToken cancellationToken)
    {
        var candidates = AttachmentHostPathResolver.EnumerateReadableAttachmentPaths(
            attachment,
            hostAttachmentsDir,
            _pathResolver,
            readPrimaryFile: true);

        string? lastError = null;
        foreach (var imagePath in candidates)
        {
            try
            {
                var fileInfo = new FileInfo(imagePath);
                if (fileInfo.Length <= 0 || fileInfo.Length > VisionConstants.MaxVisionImageBytes)
                {
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
                if (bytes.Length == 0 || bytes.Length > VisionConstants.MaxVisionImageBytes)
                {
                    continue;
                }

                if (downscaleForThumbnail)
                {
                    var thumb = VisionImageNormalizer.TryCreateThumbnailBytes(bytes, VisionConstants.ThumbnailMaxDimensionPx);
                    if (thumb is var (thumbBytes, thumbMediaType))
                    {
                        return ($"data:{thumbMediaType};base64,{Convert.ToBase64String(thumbBytes)}", null);
                    }
                }

                var mediaType = ImageHeaderInspector.TryInspect(bytes, out var header)
                    ? header.MediaType
                    : (string.IsNullOrWhiteSpace(attachment.MediaType) ? "image/png" : attachment.MediaType);
                return ($"data:{mediaType};base64,{Convert.ToBase64String(bytes)}", null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed reading image preview bytes for attachment #{AttachmentId} from '{Path}'.", attachment.Id, imagePath);
                lastError ??= ex.Message;
            }
        }

        return (null, lastError);
    }

    private async Task<TaskAttachment> RequireAttachmentAsync(int attachmentId, CancellationToken cancellationToken)
    {
        return await _attachmentRepository.GetByIdAsync(attachmentId, cancellationToken)
            ?? await _intakeService.GetAttachmentAsync(attachmentId, cancellationToken)
            ?? throw new AttachmentNotFoundException(attachmentId);
    }

    private bool IsSymlinkTargetReachable(TaskAttachment attachment)
    {
        if (!attachment.IsSymlink)
        {
            return true;
        }

        var resolvedTarget = AttachmentHostPathResolver.ResolveHostReadableSourceRef(attachment.SourceRef, _pathResolver);
        return !string.IsNullOrWhiteSpace(resolvedTarget) && File.Exists(resolvedTarget);
    }
}
