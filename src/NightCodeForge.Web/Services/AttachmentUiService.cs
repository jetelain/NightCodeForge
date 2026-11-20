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

    Task<TaskAttachment> CaptureUrlAsync(
        int workspaceId,
        string url,
        CancellationToken cancellationToken = default);

    MessageCitationExtraction ExtractMessageCitations(string? message);

    Task<IReadOnlyList<ConversationAttachmentChipViewModel>> IngestMessageCitationsAsync(
        int workspaceId,
        string? message,
        Action<ConversationAttachmentChipViewModel>? onChipChanged = null,
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
    private readonly ILogger<AttachmentUiService> _logger;

    public AttachmentUiService(
        IAttachmentIntakeService intakeService,
        ITaskAttachmentRepository attachmentRepository,
        ITaskRepository taskRepository,
        IWorkspaceRepository workspaceRepository,
        IWorkspacePathResolver pathResolver,
        IIdeationConversationCoordinator ideationCoordinator,
        IVisionCapabilityUiService visionCapabilityUiService,
        ILogger<AttachmentUiService> logger)
    {
        _intakeService = intakeService ?? throw new ArgumentNullException(nameof(intakeService));
        _attachmentRepository = attachmentRepository ?? throw new ArgumentNullException(nameof(attachmentRepository));
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _ideationCoordinator = ideationCoordinator ?? throw new ArgumentNullException(nameof(ideationCoordinator));
        _visionCapabilityUiService = visionCapabilityUiService ?? throw new ArgumentNullException(nameof(visionCapabilityUiService));
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

    public async Task<TaskAttachment> CaptureUrlAsync(
        int workspaceId,
        string url,
        CancellationToken cancellationToken = default)
    {
        var attachment = await _intakeService.CaptureUrlAsync(workspaceId, url, cancellationToken: cancellationToken);
        _ideationCoordinator.MarkWorkspaceContextDirty(workspaceId);
        return attachment;
    }

    public MessageCitationExtraction ExtractMessageCitations(string? message) =>
        AttachmentPresentationHelper.ExtractMessageCitations(message, _intakeService);

    public async Task<IReadOnlyList<ConversationAttachmentChipViewModel>> IngestMessageCitationsAsync(
        int workspaceId,
        string? message,
        Action<ConversationAttachmentChipViewModel>? onChipChanged = null,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId <= 0 || string.IsNullOrWhiteSpace(message))
        {
            return Array.Empty<ConversationAttachmentChipViewModel>();
        }

        var extraction = ExtractMessageCitations(message);
        if (!extraction.HasAny)
        {
            return Array.Empty<ConversationAttachmentChipViewModel>();
        }

        var chips = new List<ConversationAttachmentChipViewModel>();

        foreach (var hostPath in extraction.HostPaths)
        {
            var chip = ConversationAttachmentChipViewModel.CreatePendingHostPath(hostPath);
            AttachmentPresentationHelper.UpsertChip(chips, chip);
            onChipChanged?.Invoke(chip);

            try
            {
                var ingested = await _intakeService.IngestHostPathAsync(workspaceId, hostPath, cancellationToken);
                chip.ApplyIngested(ingested);
                AttachmentPresentationHelper.UpsertChip(chips, chip);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed ingesting cited host path '{HostPath}' in Workspace #{WorkspaceId}", hostPath, workspaceId);
                chip.MarkFailed(ex.Message);
            }

            onChipChanged?.Invoke(chip);
        }

        foreach (var url in extraction.Urls)
        {
            var chip = ConversationAttachmentChipViewModel.CreatePendingUrl(url);
            AttachmentPresentationHelper.UpsertChip(chips, chip);
            onChipChanged?.Invoke(chip);

            try
            {
                var ingested = await _intakeService.CaptureUrlAsync(workspaceId, url, cancellationToken: cancellationToken);
                chip.ApplyIngested(ingested);
                AttachmentPresentationHelper.UpsertChip(chips, chip);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed capturing cited URL '{Url}' in Workspace #{WorkspaceId}", url, workspaceId);
                chip.MarkFailed(ex.Message);
            }

            onChipChanged?.Invoke(chip);
        }

        foreach (var citedId in extraction.CitedAttachmentIds)
        {
            if (chips.Any(c => c.AttachmentId == citedId))
            {
                continue;
            }

            var existing = await _attachmentRepository.GetByIdAsync(citedId, cancellationToken)
                ?? await _intakeService.GetAttachmentAsync(citedId, cancellationToken);
            if (existing != null && existing.WorkspaceId == workspaceId)
            {
                var chip = ConversationAttachmentChipViewModel.FromAttachment(existing);
                AttachmentPresentationHelper.UpsertChip(chips, chip);
                onChipChanged?.Invoke(chip);
            }
        }

        if (chips.Count > 0)
        {
            _ideationCoordinator.MarkWorkspaceContextDirty(workspaceId);
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

        var hostAttachmentsDir = ResolveHostAttachmentsDirectory(attachment.WorkspaceId);
        var (isTargetReachable, resolvedSymlinkTarget) = EvaluateSymlinkReachability(attachment);

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
            string? imageDataUri = await TryLoadImageDataUriAsync(
                attachment,
                hostAttachmentsDir,
                resolvedSymlinkTarget,
                downscaleForThumbnail: false,
                cancellationToken);

            return new AttachmentPreviewModel
            {
                Attachment = attachment,
                WorkspaceName = workspace?.Name,
                ImageDataUri = imageDataUri,
                IsTargetReachable = isTargetReachable,
                LinkedTasks = linkedTasks
            };
        }

        var markdownRelPath = !string.IsNullOrWhiteSpace(attachment.MarkdownPath)
            ? attachment.MarkdownPath
            : attachment.RelativePath;
        var allowDirectSymlinkFallback = attachment.IsSymlink &&
                                         attachment.ContentKind == TaskAttachmentContentKind.PlainText;
        var markdownPath = ResolvePrimaryReadableFile(
            hostAttachmentsDir,
            markdownRelPath,
            resolvedSymlinkTarget,
            allowDirectSymlinkFallback);

        var markdownContent = string.Empty;
        if (!string.IsNullOrWhiteSpace(markdownPath) && File.Exists(markdownPath))
        {
            markdownContent = await File.ReadAllTextAsync(markdownPath, cancellationToken);
        }

        var renderedHtml = MarkdownHelper.ToHtml(markdownContent);
        return new AttachmentPreviewModel
        {
            Attachment = attachment,
            WorkspaceName = workspace?.Name,
            MarkdownContent = markdownContent,
            RenderedHtml = renderedHtml,
            IsTargetReachable = isTargetReachable,
            LinkedTasks = linkedTasks
        };
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
        await _intakeService.DeleteAttachmentAsync(attachmentId, cancellationToken);

        // If a test mocked IAttachmentIntakeService without backing the shared repository, ensure DB/repo link cleanup too.
        if (await _attachmentRepository.GetByIdAsync(attachmentId, cancellationToken) != null)
        {
            await _attachmentRepository.DeleteAsync(attachmentId, cancellationToken);
        }

        if (existing is { WorkspaceId: > 0 })
        {
            _ideationCoordinator.MarkWorkspaceContextDirty(existing.WorkspaceId);
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

        var hostAttachmentsDir = ResolveHostAttachmentsDirectory(attachment.WorkspaceId);
        var (isTargetReachable, resolvedSymlinkTarget) = EvaluateSymlinkReachability(attachment);
        if (attachment.IsSymlink && !isTargetReachable)
        {
            return null;
        }

        return await TryLoadImageDataUriAsync(
            attachment,
            hostAttachmentsDir,
            resolvedSymlinkTarget,
            downscaleForThumbnail: true,
            cancellationToken);
    }

    private async Task<string?> TryLoadImageDataUriAsync(
        TaskAttachment attachment,
        string hostAttachmentsDir,
        string? resolvedSymlinkTarget,
        bool downscaleForThumbnail,
        CancellationToken cancellationToken)
    {
        var imagePath = ResolvePrimaryReadableFile(hostAttachmentsDir, attachment.RelativePath, resolvedSymlinkTarget, attachment.IsSymlink);
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        try
        {
            var fileInfo = new FileInfo(imagePath);
            if (fileInfo.Length <= 0 || fileInfo.Length > VisionConstants.MaxVisionImageBytes)
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
            if (bytes.Length == 0 || bytes.Length > VisionConstants.MaxVisionImageBytes)
            {
                return null;
            }

            if (downscaleForThumbnail)
            {
                var thumb = VisionImageNormalizer.TryCreateThumbnailBytes(bytes, VisionConstants.ThumbnailMaxDimensionPx);
                if (thumb is var (thumbBytes, thumbMediaType))
                {
                    return $"data:{thumbMediaType};base64,{Convert.ToBase64String(thumbBytes)}";
                }
            }

            var mediaType = ImageHeaderInspector.TryInspect(bytes, out var header)
                ? header.MediaType
                : (string.IsNullOrWhiteSpace(attachment.MediaType) ? "image/png" : attachment.MediaType);
            return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed reading image preview bytes for attachment #{AttachmentId} from '{Path}'.", attachment.Id, imagePath);
            return null;
        }
    }

    private async Task<TaskAttachment> RequireAttachmentAsync(int attachmentId, CancellationToken cancellationToken)
    {
        return await _attachmentRepository.GetByIdAsync(attachmentId, cancellationToken)
            ?? await _intakeService.GetAttachmentAsync(attachmentId, cancellationToken)
            ?? throw new AttachmentNotFoundException(attachmentId);
    }

    private string ResolveHostAttachmentsDirectory(int workspaceId)
    {
        var rawAttachmentsDir = _pathResolver.GetAttachmentsPath(workspaceId);
        if (string.IsNullOrWhiteSpace(rawAttachmentsDir))
        {
            return string.Empty;
        }

        var expanded = ExpandLeadingTilde(rawAttachmentsDir);
        var hostPath = _pathResolver.ToHostPath(expanded);
        return string.IsNullOrWhiteSpace(hostPath) ? expanded : hostPath;
    }

    private string ExpandLeadingTilde(string path)
    {
        if (!IsTildePrefixedPath(path))
        {
            return path;
        }

        var relativePart = path.Length > 1
            ? path[1..].TrimStart('/', '\\').Replace('\\', '/')
            : string.Empty;

        var baseDirectory = ResolveHomeBaseDirectory();
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return path;
        }

        return string.IsNullOrEmpty(relativePart)
            ? baseDirectory
            : $"{baseDirectory}/{relativePart}";
    }

    private string? ResolveHomeBaseDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            _pathResolver.GetWslUserName() is { Length: > 0 } wslUser &&
            !string.IsNullOrWhiteSpace(wslUser))
        {
            return $"/home/{wslUser}";
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? null
            : userProfile.Replace('\\', '/').TrimEnd('/');
    }

    private static bool IsTildePrefixedPath(string path) =>
        path == "~" ||
        path.StartsWith("~/", StringComparison.Ordinal) ||
        path.StartsWith("~\\", StringComparison.Ordinal);

    private (bool IsReachable, string? ResolvedHostTarget) EvaluateSymlinkReachability(TaskAttachment attachment)
    {
        if (!attachment.IsSymlink)
        {
            return (true, null);
        }

        var resolvedTarget = ResolveHostReadableSourceRef(attachment.SourceRef);
        var reachable = !string.IsNullOrWhiteSpace(resolvedTarget) && File.Exists(resolvedTarget);
        return (reachable, resolvedTarget);
    }

    private string? ResolveHostReadableSourceRef(string? sourceRef)
    {
        if (string.IsNullOrWhiteSpace(sourceRef))
        {
            return null;
        }

        var expanded = ExpandLeadingTilde(sourceRef.Trim());
        if (File.Exists(expanded))
        {
            return expanded;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            TryConvertMntDriveToWindowsPath(expanded, out var windowsDrivePath) &&
            File.Exists(windowsDrivePath))
        {
            return windowsDrivePath;
        }

        var mapped = _pathResolver.ToHostPath(expanded);
        if (!string.IsNullOrWhiteSpace(mapped) && File.Exists(mapped))
        {
            return mapped;
        }

        return expanded;
    }

    private static string? ResolvePrimaryReadableFile(
        string hostAttachmentsDir,
        string? relativePath,
        string? resolvedSymlinkTarget,
        bool allowSymlinkTargetFallback)
    {
        if (!string.IsNullOrWhiteSpace(hostAttachmentsDir) && !string.IsNullOrWhiteSpace(relativePath))
        {
            var fullBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hostAttachmentsDir))
                + Path.DirectorySeparatorChar;
            var cleanRel = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            var candidate = Path.Combine(hostAttachmentsDir, cleanRel);
            var fullCandidate = Path.GetFullPath(candidate);
            var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (fullCandidate.StartsWith(fullBase, comparison) && File.Exists(fullCandidate))
            {
                return fullCandidate;
            }
        }

        if (allowSymlinkTargetFallback &&
            !string.IsNullOrWhiteSpace(resolvedSymlinkTarget) &&
            File.Exists(resolvedSymlinkTarget))
        {
            return resolvedSymlinkTarget;
        }

        return null;
    }

    private static bool TryConvertMntDriveToWindowsPath(string posixPath, out string windowsPath)
    {
        windowsPath = string.Empty;
        if (posixPath.Length >= 7 &&
            posixPath.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase) &&
            char.IsAsciiLetter(posixPath[5]) &&
            posixPath[6] == '/')
        {
            var drive = char.ToUpperInvariant(posixPath[5]);
            var rest = posixPath[7..].Replace('/', '\\');
            windowsPath = $"{drive}:\\{rest}";
            return true;
        }

        return false;
    }
}
