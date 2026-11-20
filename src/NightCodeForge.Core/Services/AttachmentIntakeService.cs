using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using Pmad.Markitdown;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Implements the workspace attachment intake pipeline (file drop and WSL2 symlink host paths),
/// MarkItDown conversion, SHA-256 deduplication, manifest synchronization, and host-safe deletion.
/// </summary>
public partial class AttachmentIntakeService : IAttachmentIntakeService
{
    private static readonly byte[] PdfMagicBytes = [(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-'];
    private static readonly byte[] ZipMagicBytes = [0x50, 0x4B, 0x03, 0x04];
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex NonAlphanumericSlugRegex();

    private readonly ITaskAttachmentRepository _attachmentRepository;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IProcessRunner _processRunner;
    private readonly IMarkitdown _markitdown;
    private readonly ILogger<AttachmentIntakeService> _logger;

    public AttachmentIntakeService(
        ITaskAttachmentRepository attachmentRepository,
        IWorkspaceRepository workspaceRepository,
        IWorkspacePathResolver pathResolver,
        IProcessRunner processRunner,
        IMarkitdown markitdown,
        ILogger<AttachmentIntakeService> logger)
    {
        _attachmentRepository = attachmentRepository ?? throw new ArgumentNullException(nameof(attachmentRepository));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _markitdown = markitdown ?? throw new ArgumentNullException(nameof(markitdown));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TaskAttachment> IngestFileDropAsync(
        int workspaceId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new AttachmentConversionException(string.Empty, "Attachment file name cannot be empty.");
        }

        var workspace = await RequireWorkspaceAsync(workspaceId, cancellationToken);
        var cleanFileName = Path.GetFileName(fileName.Trim().Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(cleanFileName))
        {
            throw new AttachmentConversionException(fileName.Trim(), "Attachment file name cannot be empty.");
        }

        var extension = Path.GetExtension(cleanFileName).ToLowerInvariant();

        if (!AttachmentConstants.TryClassifyExtension(extension, out var contentKind))
        {
            throw new AttachmentConversionException(
                cleanFileName,
                AttachmentConstants.BuildUnsupportedFormatMessage(cleanFileName, extension));
        }

        var bytes = await ReadStreamBoundedAsync(
            content,
            AttachmentConstants.MaxFileDropBytes,
            cleanFileName,
            cancellationToken);

        if (bytes.Length == 0)
        {
            throw new AttachmentConversionException(cleanFileName, $"Attachment '{cleanFileName}' is empty (0 bytes) and cannot be ingested.");
        }

        var sha256 = ComputeSha256Hex(bytes);
        var existing = await _attachmentRepository.GetByWorkspaceAndSha256Async(workspaceId, sha256, cancellationToken);
        if (existing != null)
        {
            return existing;
        }

        var prepared = await ValidateAndConvertContentAsync(
            bytes,
            extension,
            contentKind,
            cleanFileName,
            baseUrl: null,
            cancellationToken);

        var (wslAttachmentsDir, hostAttachmentsDir) = EnsureAttachmentsDirectories(workspace);
        var title = cleanFileName;
        var slug = Slugify(Path.GetFileNameWithoutExtension(cleanFileName));
        var now = DateTime.UtcNow;

        var pending = new TaskAttachment
        {
            WorkspaceId = workspaceId,
            Title = title,
            Slug = slug,
            RelativePath = BuildPendingRelativePath(extension),
            MarkdownPath = null,
            ContentKind = contentKind,
            MediaType = prepared.MediaType,
            WidthPx = prepared.WidthPx,
            HeightPx = prepared.HeightPx,
            SourceKind = TaskAttachmentSourceKind.FileDrop,
            SourceRef = cleanFileName,
            IsSymlink = false,
            Sha256 = sha256,
            SizeBytes = bytes.LongLength,
            CreatedAt = now
        };

        var created = await _attachmentRepository.CreateAsync(pending, cancellationToken);
        var writtenFiles = new List<string>();
        try
        {
            var seqPrefix = FormatSequencePrefix(created.Id);
            var relativePath = $"{seqPrefix}-{slug}{extension}";
            var markdownPath = contentKind switch
            {
                TaskAttachmentContentKind.PlainText => relativePath,
                TaskAttachmentContentKind.RichDocument => $"{seqPrefix}-{slug}{AttachmentConstants.MarkdownExtension}",
                _ => null
            };

            var hostPrimaryPath = Path.Combine(hostAttachmentsDir, relativePath);
            await File.WriteAllBytesAsync(hostPrimaryPath, bytes, cancellationToken);
            writtenFiles.Add(hostPrimaryPath);

            if (contentKind == TaskAttachmentContentKind.RichDocument && markdownPath != null)
            {
                var hostMarkdownPath = Path.Combine(hostAttachmentsDir, markdownPath);
                await File.WriteAllTextAsync(hostMarkdownPath, prepared.ConvertedMarkdown!, Encoding.UTF8, cancellationToken);
                writtenFiles.Add(hostMarkdownPath);
            }

            created.RelativePath = relativePath;
            created.MarkdownPath = markdownPath;
            await _attachmentRepository.UpdateAsync(created, cancellationToken);
            await SyncManifestInternalAsync(workspaceId, hostAttachmentsDir, cancellationToken);

            _logger.LogInformation(
                "Ingested file drop attachment #{AttachmentId} ('{RelativePath}', {ContentKind}, {SizeBytes} bytes) in Workspace #{WorkspaceId} ({WslDir}).",
                created.Id, created.RelativePath, created.ContentKind, created.SizeBytes, workspaceId, wslAttachmentsDir);

            return created;
        }
        catch (Exception ex)
        {
            await RollbackFailedAttachmentAsync(created.Id, writtenFiles, ex);
            throw;
        }
    }

    public async Task<TaskAttachment> IngestHostPathAsync(
        int workspaceId,
        string hostPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            throw new AttachmentTargetNotFoundException(hostPath ?? string.Empty, "Host path cannot be empty.");
        }

        var workspace = await RequireWorkspaceAsync(workspaceId, cancellationToken);
        var (wslAttachmentsDir, hostAttachmentsDir) = EnsureAttachmentsDirectories(workspace);

        var (hostReadablePath, canonicalWslSourceRef) = ResolveAndValidateHostPath(
            hostPath,
            wslAttachmentsDir,
            hostAttachmentsDir);

        var fileName = Path.GetFileName(hostReadablePath.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = Path.GetFileName(canonicalWslSourceRef.Replace('\\', '/'));
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AttachmentConstants.TryClassifyExtension(extension, out var contentKind))
        {
            throw new AttachmentConversionException(
                fileName,
                AttachmentConstants.BuildUnsupportedFormatMessage(fileName, extension));
        }

        byte[] bytes;
        try
        {
            await using var fs = new FileStream(
                hostReadablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                useAsync: true);
            bytes = await ReadStreamBoundedAsync(
                fs,
                AttachmentConstants.MaxHostFileBytes,
                canonicalWslSourceRef,
                cancellationToken);
        }
        catch (AttachmentConversionException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            throw new AttachmentTargetNotFoundException(
                canonicalWslSourceRef,
                $"Attachment host target '{hostPath}' (resolved to '{hostReadablePath}') does not exist or is unreadable.",
                ex);
        }

        if (bytes.Length == 0)
        {
            throw new AttachmentConversionException(fileName, $"Host attachment '{canonicalWslSourceRef}' is empty (0 bytes) and cannot be ingested.");
        }

        var sha256 = ComputeSha256Hex(bytes);
        var existing = await _attachmentRepository.GetByWorkspaceAndSha256Async(workspaceId, sha256, cancellationToken);
        if (existing != null)
        {
            return existing;
        }

        var prepared = await ValidateAndConvertContentAsync(
            bytes,
            extension,
            contentKind,
            fileName,
            baseUrl: null,
            cancellationToken);

        var slug = Slugify(Path.GetFileNameWithoutExtension(fileName));
        var now = DateTime.UtcNow;

        var pending = new TaskAttachment
        {
            WorkspaceId = workspaceId,
            Title = fileName,
            Slug = slug,
            RelativePath = BuildPendingRelativePath(extension),
            MarkdownPath = null,
            ContentKind = contentKind,
            MediaType = prepared.MediaType,
            WidthPx = prepared.WidthPx,
            HeightPx = prepared.HeightPx,
            SourceKind = TaskAttachmentSourceKind.HostPath,
            SourceRef = canonicalWslSourceRef,
            IsSymlink = true,
            Sha256 = sha256,
            SizeBytes = bytes.LongLength,
            CreatedAt = now
        };

        var created = await _attachmentRepository.CreateAsync(pending, cancellationToken);
        var writtenFiles = new List<string>();
        var createdSymlinkWslPath = string.Empty;

        try
        {
            var seqPrefix = FormatSequencePrefix(created.Id);
            var relativePath = $"{seqPrefix}-{slug}{extension}";
            var markdownPath = contentKind switch
            {
                TaskAttachmentContentKind.PlainText => relativePath,
                TaskAttachmentContentKind.RichDocument => $"{seqPrefix}-{slug}{AttachmentConstants.MarkdownExtension}",
                _ => null
            };

            var wslLinkPath = CombineUnixPath(wslAttachmentsDir, relativePath);
            await CreatePosixSymlinkAsync(workspace, canonicalWslSourceRef, wslLinkPath, cancellationToken);
            createdSymlinkWslPath = wslLinkPath;

            if (contentKind == TaskAttachmentContentKind.RichDocument && markdownPath != null)
            {
                var hostMarkdownPath = Path.Combine(hostAttachmentsDir, markdownPath);
                await File.WriteAllTextAsync(hostMarkdownPath, prepared.ConvertedMarkdown!, Encoding.UTF8, cancellationToken);
                writtenFiles.Add(hostMarkdownPath);
            }

            created.RelativePath = relativePath;
            created.MarkdownPath = markdownPath;
            await _attachmentRepository.UpdateAsync(created, cancellationToken);
            await SyncManifestInternalAsync(workspaceId, hostAttachmentsDir, cancellationToken);

            _logger.LogInformation(
                "Mapped host path attachment #{AttachmentId} ('{RelativePath}' -> '{SourceRef}', {ContentKind}) in Workspace #{WorkspaceId}.",
                created.Id, created.RelativePath, created.SourceRef, created.ContentKind, workspaceId);

            return created;
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(createdSymlinkWslPath))
            {
                await TryRemovePosixSymlinkAsync(workspace, createdSymlinkWslPath);
            }

            await RollbackFailedAttachmentAsync(created.Id, writtenFiles, ex);
            throw;
        }
    }

    public async Task DeleteAttachmentAsync(int attachmentId, CancellationToken cancellationToken = default)
    {
        var attachment = await _attachmentRepository.GetByIdAsync(attachmentId, cancellationToken)
            ?? throw new AttachmentNotFoundException(attachmentId);

        var workspace = await RequireWorkspaceAsync(attachment.WorkspaceId, cancellationToken);
        var (wslAttachmentsDir, hostAttachmentsDir) = ResolveAttachmentsDirectories(workspace);

        var hostPrimaryPath = ResolveContainedFilePath(hostAttachmentsDir, attachment.RelativePath);
        var wslPrimaryPath = hostPrimaryPath != null
            ? CombineUnixPath(wslAttachmentsDir, attachment.RelativePath)
            : string.Empty;

        if (attachment.IsSymlink)
        {
            if (hostPrimaryPath != null)
            {
                await RemovePosixSymlinkAsync(workspace, wslPrimaryPath, cancellationToken);
            }

            // In unit tests with mocked IProcessRunner or on non-WSL hosts, ensure the link entry itself
            // inside attachments/ is removed while strictly verifying it is never the external SourceRef.
            if (hostPrimaryPath != null && !PathsEqual(hostPrimaryPath, attachment.SourceRef))
            {
                TryDeleteLocalFileIfPresent(hostPrimaryPath);
            }
        }
        else if (hostPrimaryPath != null)
        {
            TryDeleteLocalFileIfPresent(hostPrimaryPath);
        }

        if (!string.IsNullOrWhiteSpace(attachment.MarkdownPath) &&
            !string.Equals(attachment.MarkdownPath, attachment.RelativePath, StringComparison.OrdinalIgnoreCase))
        {
            var hostMarkdownPath = ResolveContainedFilePath(hostAttachmentsDir, attachment.MarkdownPath);
            if (hostMarkdownPath != null && !PathsEqual(hostMarkdownPath, attachment.SourceRef))
            {
                TryDeleteLocalFileIfPresent(hostMarkdownPath);
            }
        }

        await _attachmentRepository.DeleteAsync(attachmentId, cancellationToken);

        if (Directory.Exists(hostAttachmentsDir))
        {
            await SyncManifestInternalAsync(attachment.WorkspaceId, hostAttachmentsDir, cancellationToken);
        }

        _logger.LogInformation(
            "Deleted attachment #{AttachmentId} ('{RelativePath}', IsSymlink={IsSymlink}) from Workspace #{WorkspaceId} while preserving any host source '{SourceRef}'.",
            attachment.Id, attachment.RelativePath, attachment.IsSymlink, attachment.WorkspaceId, attachment.SourceRef);
    }

    public Task<IReadOnlyList<TaskAttachment>> ListWorkspaceAttachmentsAsync(
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        return _attachmentRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
    }

    public Task<TaskAttachment?> GetAttachmentAsync(
        int attachmentId,
        CancellationToken cancellationToken = default)
    {
        return _attachmentRepository.GetByIdAsync(attachmentId, cancellationToken);
    }

    public async Task SyncManifestAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var workspace = await RequireWorkspaceAsync(workspaceId, cancellationToken);
        var (_, hostAttachmentsDir) = EnsureAttachmentsDirectories(workspace);
        await SyncManifestInternalAsync(workspaceId, hostAttachmentsDir, cancellationToken);
    }

    private async Task<Workspace> RequireWorkspaceAsync(int workspaceId, CancellationToken cancellationToken)
    {
        return await _workspaceRepository.GetByIdAsync(workspaceId, cancellationToken)
            ?? throw new InvalidOperationException($"Workspace #{workspaceId} was not found.");
    }

    private (string WslAttachmentsDir, string HostAttachmentsDir) ResolveAttachmentsDirectories(Workspace workspace)
    {
        var rawAttachmentsDir = _pathResolver.GetAttachmentsPath(workspace.Id);
        if (string.IsNullOrWhiteSpace(rawAttachmentsDir))
        {
            throw new InvalidOperationException($"Attachments path could not be resolved for Workspace #{workspace.Id}.");
        }

        var expandedUnixDir = AttachmentHostPathResolver.ExpandLeadingTilde(rawAttachmentsDir, _pathResolver);
        var wslAttachmentsDir = VirtualMountEntry.ToDefaultWslPath(expandedUnixDir);
        var hostAttachmentsDir = _pathResolver.ToHostPath(expandedUnixDir);
        if (string.IsNullOrWhiteSpace(hostAttachmentsDir))
        {
            hostAttachmentsDir = expandedUnixDir;
        }

        return (wslAttachmentsDir, hostAttachmentsDir);
    }

    private (string WslAttachmentsDir, string HostAttachmentsDir) EnsureAttachmentsDirectories(Workspace workspace)
    {
        var (wslAttachmentsDir, hostAttachmentsDir) = ResolveAttachmentsDirectories(workspace);
        if (!Directory.Exists(hostAttachmentsDir))
        {
            Directory.CreateDirectory(hostAttachmentsDir);
        }

        return (wslAttachmentsDir, hostAttachmentsDir);
    }

    private (string HostReadablePath, string CanonicalWslSourceRef) ResolveAndValidateHostPath(
        string rawHostPath,
        string wslAttachmentsDir,
        string hostAttachmentsDir)
    {
        var trimmed = rawHostPath.Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new AttachmentTargetNotFoundException(rawHostPath, "Host path cannot be empty.");
        }

        var segments = trimmed.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(seg => seg == ".."))
        {
            throw new AttachmentTargetNotFoundException(
                trimmed,
                $"Host path '{trimmed}' contains disallowed relative traversal segments ('..').");
        }

        var isWindowsHost = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var normalizedSlashes = trimmed.Replace('\\', '/');

        string hostReadablePath;
        string canonicalWslSourceRef;

        if (normalizedSlashes.StartsWith("//wsl.localhost/", StringComparison.OrdinalIgnoreCase) ||
            normalizedSlashes.StartsWith("//wsl$/", StringComparison.OrdinalIgnoreCase))
        {
            canonicalWslSourceRef = VirtualMountEntry.ToDefaultWslPath(trimmed);
            hostReadablePath = isWindowsHost ? trimmed.Replace('/', '\\') : canonicalWslSourceRef;
        }
        else if (AttachmentHostPathResolver.IsWindowsDrivePath(trimmed))
        {
            canonicalWslSourceRef = VirtualMountEntry.ToDefaultWslPath(trimmed);
            hostReadablePath = isWindowsHost ? Path.GetFullPath(trimmed) : canonicalWslSourceRef;
        }
        else if (normalizedSlashes.StartsWith("//", StringComparison.Ordinal))
        {
            var uncParts = normalizedSlashes.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (uncParts.Length < 3)
            {
                throw new AttachmentTargetNotFoundException(trimmed, $"UNC host path '{trimmed}' is incomplete.");
            }

            canonicalWslSourceRef = normalizedSlashes;
            hostReadablePath = isWindowsHost ? trimmed.Replace('/', '\\') : canonicalWslSourceRef;
        }
        else if (trimmed == "~" || trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal))
        {
            var rel = trimmed.Length > 1 ? trimmed[1..].TrimStart('/', '\\').Replace('\\', '/') : string.Empty;
            if (isWindowsHost)
            {
                var wslUser = _pathResolver.GetWslUserName();
                if (string.IsNullOrWhiteSpace(wslUser))
                {
                    throw new AttachmentTargetNotFoundException(trimmed, $"Cannot resolve tilde path '{trimmed}' because WSL user name is unavailable.");
                }

                canonicalWslSourceRef = string.IsNullOrEmpty(rel) ? $"/home/{wslUser}" : $"/home/{wslUser}/{rel}";
                hostReadablePath = _pathResolver.ToHostPath(canonicalWslSourceRef);
            }
            else
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(profile))
                {
                    throw new AttachmentTargetNotFoundException(trimmed, $"Cannot resolve tilde path '{trimmed}' because UserProfile is unavailable.");
                }

                canonicalWslSourceRef = string.IsNullOrEmpty(rel)
                    ? profile.Replace('\\', '/').TrimEnd('/')
                    : $"{profile.Replace('\\', '/').TrimEnd('/')}/{rel}";
                hostReadablePath = canonicalWslSourceRef;
            }
        }
        else if (normalizedSlashes.StartsWith('/'))
        {
            canonicalWslSourceRef = normalizedSlashes;
            if (isWindowsHost)
            {
                if (AttachmentHostPathResolver.TryConvertMntDriveToWindowsPath(canonicalWslSourceRef, out var windowsDrivePath))
                {
                    hostReadablePath = windowsDrivePath;
                }
                else
                {
                    var mapped = _pathResolver.ToHostPath(canonicalWslSourceRef);
                    hostReadablePath = !string.IsNullOrWhiteSpace(mapped) ? mapped : canonicalWslSourceRef;
                }
            }
            else
            {
                hostReadablePath = canonicalWslSourceRef;
            }
        }
        else
        {
            throw new AttachmentTargetNotFoundException(
                trimmed,
                $"Host path '{trimmed}' must be an absolute host or WSL path.");
        }

        canonicalWslSourceRef = CollapsePosixDotSegments(canonicalWslSourceRef);

        var wslFromHostReadable = VirtualMountEntry.ToDefaultWslPath(hostReadablePath);
        if (IsInsideDirectory(hostReadablePath, hostAttachmentsDir) ||
            IsInsideDirectory(canonicalWslSourceRef, wslAttachmentsDir) ||
            IsInsideDirectory(wslFromHostReadable, wslAttachmentsDir))
        {
            throw new AttachmentTargetNotFoundException(
                trimmed,
                $"Host path '{trimmed}' cannot reference a file inside the workspace attachments directory.");
        }

        if (Directory.Exists(hostReadablePath))
        {
            throw new AttachmentConversionException(
                trimmed,
                $"Host path '{trimmed}' is a directory. Only individual supported files can be attached.");
        }

        if (!File.Exists(hostReadablePath))
        {
            throw new AttachmentTargetNotFoundException(
                canonicalWslSourceRef,
                $"Attachment host target '{trimmed}' (resolved to '{hostReadablePath}') does not exist or is unreadable.");
        }

        return (hostReadablePath, canonicalWslSourceRef);
    }

    private static bool IsInsideDirectory(string candidatePath, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        var normCandidate = CollapsePosixDotSegments(candidatePath).TrimEnd('/');
        var normDir = CollapsePosixDotSegments(directoryPath).TrimEnd('/');
        return normCandidate.Equals(normDir, StringComparison.OrdinalIgnoreCase) ||
               normCandidate.StartsWith(normDir + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string CollapsePosixDotSegments(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var slashNormalized = path.Replace('\\', '/');
        var isUnc = slashNormalized.StartsWith("//", StringComparison.Ordinal);
        var isRootedPosix = !isUnc && slashNormalized.StartsWith('/');
        var parts = slashNormalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(seg => seg != ".");
        var joined = string.Join("/", parts);

        if (isUnc)
        {
            return "//" + joined;
        }

        if (isRootedPosix)
        {
            return "/" + joined;
        }

        return joined;
    }

    private async Task<PreparedAttachmentContent> ValidateAndConvertContentAsync(
        byte[] bytes,
        string extension,
        TaskAttachmentContentKind contentKind,
        string sourceName,
        Uri? baseUrl,
        CancellationToken cancellationToken)
    {
        if (bytes.Length == 0)
        {
            throw new AttachmentConversionException(sourceName, $"Attachment '{sourceName}' is empty (0 bytes) and cannot be converted.");
        }

        switch (contentKind)
        {
            case TaskAttachmentContentKind.PlainText:
                return new PreparedAttachmentContent(
                    AttachmentConstants.ResolveMediaType(extension),
                    WidthPx: null,
                    HeightPx: null,
                    ConvertedMarkdown: null);

            case TaskAttachmentContentKind.Image:
                var imageInfo = ImageHeaderInspector.Inspect(bytes, extension, sourceName);
                return new PreparedAttachmentContent(
                    imageInfo.MediaType,
                    imageInfo.WidthPx,
                    imageInfo.HeightPx,
                    ConvertedMarkdown: null);

            case TaskAttachmentContentKind.RichDocument:
                if (bytes.Length == 0)
                {
                    throw new AttachmentConversionException(sourceName, $"Document '{sourceName}' is empty and cannot be converted.");
                }

                ValidateRichDocumentSignature(bytes, extension, sourceName);
                var markdown = await ConvertRichDocumentWithMarkitdownAsync(
                    bytes,
                    extension,
                    sourceName,
                    baseUrl,
                    cancellationToken);

                return new PreparedAttachmentContent(
                    AttachmentConstants.ResolveMediaType(extension),
                    WidthPx: null,
                    HeightPx: null,
                    ConvertedMarkdown: markdown);

            default:
                throw new AttachmentConversionException(sourceName, $"Unsupported attachment content kind '{contentKind}'.");
        }
    }

    private static void ValidateRichDocumentSignature(byte[] bytes, string extension, string sourceName)
    {
        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var searchWindow = bytes.AsSpan(0, Math.Min(bytes.Length, 1024));
            if (searchWindow.IndexOf(PdfMagicBytes) < 0)
            {
                throw new AttachmentConversionException(sourceName, $"File '{sourceName}' does not have a valid PDF header.");
            }
        }
        else if (string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase))
        {
            if (bytes.Length < ZipMagicBytes.Length || !bytes.AsSpan(0, ZipMagicBytes.Length).SequenceEqual(ZipMagicBytes))
            {
                throw new AttachmentConversionException(sourceName, $"File '{sourceName}' does not have a valid DOCX (ZIP) header.");
            }
        }
    }

    private async Task<string> ConvertRichDocumentWithMarkitdownAsync(
        byte[] bytes,
        string extensionOrMediaType,
        string sourceName,
        Uri? baseUrl,
        CancellationToken cancellationToken)
    {
        var options = new MarkitdownOptions
        {
            IncludeTitle = true,
            CleanHtml = true,
            FilterHiddenText = true,
            BaseUrl = baseUrl
        };

        try
        {
            await using var stream = new MemoryStream(bytes, writable: false);
            var markdown = await _markitdown.ConvertAsync(stream, extensionOrMediaType, options, cancellationToken);
            if (markdown == null)
            {
                throw new AttachmentConversionException(sourceName, $"MarkItDown produced no output for '{sourceName}'.");
            }

            if (string.IsNullOrWhiteSpace(markdown))
            {
                throw new AttachmentConversionException(sourceName, $"Document '{sourceName}' produced empty Markdown after conversion.");
            }

            return markdown;
        }
        catch (AttachmentConversionException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AttachmentConversionException(
                sourceName,
                $"Failed to convert '{sourceName}' via Pmad.Markitdown: {ex.Message}",
                ex);
        }
    }

    private async Task CreatePosixSymlinkAsync(
        Workspace workspace,
        string targetPosixPath,
        string linkPosixPath,
        CancellationToken cancellationToken)
    {
        var spec = BuildPosixShellCommandSpec(workspace, "ln", ["-sfn", targetPosixPath, linkPosixPath]);
        var result = await _processRunner.RunAsync(spec, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new AttachmentTargetNotFoundException(
                targetPosixPath,
                $"Failed to create POSIX symlink '{linkPosixPath}' -> '{targetPosixPath}' (exit code {result.ExitCode}): {result.StandardError}");
        }
    }

    private async Task RemovePosixSymlinkAsync(
        Workspace workspace,
        string linkPosixPath,
        CancellationToken cancellationToken)
    {
        var spec = BuildPosixShellCommandSpec(workspace, "rm", ["-f", linkPosixPath]);
        var result = await _processRunner.RunAsync(spec, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to unlink attachment symlink '{linkPosixPath}' (exit code {result.ExitCode}): {result.StandardError}");
        }
    }

    private async Task TryRemovePosixSymlinkAsync(Workspace workspace, string linkPosixPath)
    {
        try
        {
            var spec = BuildPosixShellCommandSpec(workspace, "rm", ["-f", linkPosixPath]);
            await _processRunner.RunAsync(spec, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove rolled-back symlink '{LinkPath}'.", linkPosixPath);
        }
    }

    private ProcessStartSpec BuildPosixShellCommandSpec(
        Workspace workspace,
        string executable,
        IReadOnlyList<string> executableArgs)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var distro = !string.IsNullOrWhiteSpace(workspace.WslDistribution)
                ? workspace.WslDistribution.Trim()
                : _pathResolver.GetWslDistributionName();

            var args = new List<string>();
            if (!string.IsNullOrWhiteSpace(distro))
            {
                args.Add("-d");
                args.Add(distro);
            }
            args.Add("--");
            args.Add(executable);
            args.AddRange(executableArgs);

            return new ProcessStartSpec
            {
                FileName = "wsl.exe",
                ArgumentList = args,
                TimeoutSeconds = AttachmentConstants.SymlinkCreationTimeoutSeconds
            };
        }

        return new ProcessStartSpec
        {
            FileName = executable,
            ArgumentList = executableArgs.ToList(),
            TimeoutSeconds = AttachmentConstants.SymlinkCreationTimeoutSeconds
        };
    }

    private async Task SyncManifestInternalAsync(
        int workspaceId,
        string hostAttachmentsDir,
        CancellationToken cancellationToken)
    {
        var attachments = await _attachmentRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        var manifest = new AttachmentManifest
        {
            WorkspaceId = workspaceId,
            UpdatedAt = DateTime.UtcNow,
            Attachments = attachments.Select(AttachmentManifestEntry.FromDomain).ToList()
        };

        var manifestPath = Path.Combine(hostAttachmentsDir, AttachmentConstants.ManifestFileName);
        var tempPath = Path.Combine(hostAttachmentsDir, $".manifest.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
            await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken);
            File.Move(tempPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to clean up temporary manifest file '{TempPath}'.", tempPath);
                }
            }
        }
    }

    private async Task RollbackFailedAttachmentAsync(
        int attachmentId,
        IEnumerable<string> writtenFiles,
        Exception originalException)
    {
        _logger.LogWarning(
            originalException,
            "Rolling back attachment #{AttachmentId} after intake failure.",
            attachmentId);

        foreach (var file in writtenFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception deleteEx)
            {
                _logger.LogWarning(deleteEx, "Failed to remove partial attachment file '{FilePath}' during rollback.", file);
            }
        }

        try
        {
            await _attachmentRepository.DeleteAsync(attachmentId, CancellationToken.None);
        }
        catch (Exception dbEx)
        {
            _logger.LogWarning(dbEx, "Failed to delete rolled-back attachment row #{AttachmentId}.", attachmentId);
        }
    }

    private static async Task<byte[]> ReadStreamBoundedAsync(
        Stream stream,
        long maxBytes,
        string sourceName,
        CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
        {
            var remaining = stream.Length - stream.Position;
            if (remaining > maxBytes)
            {
                throw new AttachmentConversionException(
                    sourceName,
                    $"Attachment '{sourceName}' ({remaining} bytes) exceeds maximum allowed size of {maxBytes} bytes.");
            }
        }

        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long totalRead = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
            if (totalRead > maxBytes)
            {
                throw new AttachmentConversionException(
                    sourceName,
                    $"Attachment '{sourceName}' exceeds maximum allowed size of {maxBytes} bytes.");
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    private static string Slugify(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return AttachmentConstants.DefaultSlug;
        }

        var lower = raw.Trim().ToLowerInvariant();
        var slug = NonAlphanumericSlugRegex().Replace(lower, "-").Trim('-');

        if (slug.Length > AttachmentConstants.MaxSlugLength)
        {
            slug = slug[..AttachmentConstants.MaxSlugLength].Trim('-');
        }

        return string.IsNullOrEmpty(slug) ? AttachmentConstants.DefaultSlug : slug;
    }

    private static string FormatSequencePrefix(int id) =>
        id.ToString($"D{AttachmentConstants.SequencePaddingDigits}", CultureInfo.InvariantCulture);

    private static string BuildPendingRelativePath(string suffix) =>
        $"pending-{Guid.NewGuid():N}{suffix}";

    private static string CombineUnixPath(string baseDir, string relativePath) =>
        $"{baseDir.Replace('\\', '/').TrimEnd('/')}/{relativePath.Replace('\\', '/').TrimStart('/')}";

    private static string? ResolveContainedFilePath(string hostAttachmentsDir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(hostAttachmentsDir) || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        if (relativePath is "." or ".." ||
            relativePath.Contains('/') ||
            relativePath.Contains('\\') ||
            relativePath.Contains("..", StringComparison.Ordinal))
        {
            throw new AttachmentTargetNotFoundException(
                relativePath,
                $"Attachment relative path '{relativePath}' must be a single file name inside the attachments directory.");
        }

        var baseFull = Path.GetFullPath(hostAttachmentsDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var combinedFull = Path.GetFullPath(Path.Combine(baseFull, relativePath));
        var expectedPrefix = baseFull + Path.DirectorySeparatorChar;
        if (!combinedFull.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new AttachmentTargetNotFoundException(
                relativePath,
                $"Attachment relative path '{relativePath}' resolves outside attachments directory '{baseFull}'.");
        }

        return combinedFull;
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var normLeft = left.Replace('\\', '/').TrimEnd('/');
        var normRight = right.Replace('\\', '/').TrimEnd('/');
        if (string.Equals(normLeft, normRight, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var wslLeft = VirtualMountEntry.ToDefaultWslPath(left).TrimEnd('/');
        var wslRight = VirtualMountEntry.ToDefaultWslPath(right).TrimEnd('/');
        return !string.IsNullOrWhiteSpace(wslLeft) &&
               string.Equals(wslLeft, wslRight, StringComparison.OrdinalIgnoreCase);
    }

    private void TryDeleteLocalFileIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _logger.LogDebug(ex, "Attachment file '{Path}' was already absent on disk during deletion.", path);
        }
    }

    private readonly record struct PreparedAttachmentContent(
        string MediaType,
        int? WidthPx,
        int? HeightPx,
        string? ConvertedMarkdown);
}
