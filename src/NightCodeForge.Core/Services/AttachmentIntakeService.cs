using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using Pmad.Markitdown;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Implements the three-channel workspace attachment intake pipeline (file drop, WSL2 symlink host paths, URL capture),
/// MarkItDown conversion, SHA-256 deduplication, manifest synchronization, and host-safe deletion.
/// </summary>
public partial class AttachmentIntakeService : IAttachmentIntakeService
{
    private static readonly byte[] PdfMagicBytes = [(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-'];
    private static readonly byte[] ZipMagicBytes = [0x50, 0x4B, 0x03, 0x04];
    private static readonly IPAddress AwsIpv6MetadataAddress = IPAddress.Parse("fd00:ec2::254");
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };
    private static readonly char[] TrailingCitationPunctuation = ['.', ',', ';', ':', '!', '?', ')', ']', '>', '}', '\'', '"', '`'];

    [GeneratedRegex(@"""(?<path>(?:[a-zA-Z]:[\\/]|\\\\(?:wsl\.localhost|wsl\$|[a-zA-Z0-9._-]+)\\|/(?:home|mnt|Users|tmp|var|opt)/|~/)(?:[^""\r\n])+)""", RegexOptions.Compiled)]
    private static partial Regex QuotedHostPathRegex();

    [GeneratedRegex(@"`(?<path>(?:[a-zA-Z]:[\\/]|\\\\(?:wsl\.localhost|wsl\$|[a-zA-Z0-9._-]+)\\|/(?:home|mnt|Users|tmp|var|opt)/|~/)(?:[^`\r\n])+)`", RegexOptions.Compiled)]
    private static partial Regex BacktickHostPathRegex();

    [GeneratedRegex(@"<title\b[^>]*>(?<title>[\s\S]*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HtmlTitleRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceCollapseRegex();

    [GeneratedRegex(@"(?<![:\w])(?<path>[a-zA-Z]:[\\/][^\s""'`<>|*?]+)", RegexOptions.Compiled)]
    private static partial Regex WindowsDrivePathRegex();

    [GeneratedRegex(@"(?<![:\\/])(?<path>\\\\[^\s""'`<>|*?\\]+\\[^\s""'`<>|*?]+)", RegexOptions.Compiled)]
    private static partial Regex UncPathRegex();

    [GeneratedRegex(@"(?<=^|[\s(\['""`])(?<path>(?:/(?:home|mnt|Users|tmp|var|opt)/|~/)[A-Za-z0-9._/\-~+%:@-]+)", RegexOptions.Compiled)]
    private static partial Regex PosixOrTildePathRegex();

    [GeneratedRegex(@"(?<url>https?://[^\s<>""'`\)\]]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HttpUrlRegex();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex NonAlphanumericSlugRegex();

    private readonly ITaskAttachmentRepository _attachmentRepository;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IProcessRunner _processRunner;
    private readonly IMarkitdown _markitdown;
    private readonly HttpClient _httpClient;
    private readonly SecurityOptions _securityOptions;
    private readonly AttachmentIntakeOptions _intakeOptions;
    private readonly ILogger<AttachmentIntakeService> _logger;

    public AttachmentIntakeService(
        ITaskAttachmentRepository attachmentRepository,
        IWorkspaceRepository workspaceRepository,
        IWorkspacePathResolver pathResolver,
        IProcessRunner processRunner,
        IMarkitdown markitdown,
        HttpClient httpClient,
        IOptions<SecurityOptions> securityOptions,
        IOptions<AttachmentIntakeOptions> intakeOptions,
        ILogger<AttachmentIntakeService> logger)
    {
        _attachmentRepository = attachmentRepository ?? throw new ArgumentNullException(nameof(attachmentRepository));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _markitdown = markitdown ?? throw new ArgumentNullException(nameof(markitdown));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _securityOptions = (securityOptions ?? throw new ArgumentNullException(nameof(securityOptions))).Value
            ?? throw new ArgumentException("Security options value cannot be null.", nameof(securityOptions));
        _intakeOptions = (intakeOptions ?? throw new ArgumentNullException(nameof(intakeOptions))).Value
            ?? throw new ArgumentException("Attachment intake options value cannot be null.", nameof(intakeOptions));
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
                $"Unsupported attachment format '{extension}' for '{cleanFileName}'.");
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
                $"Unsupported attachment format '{extension}' for host file '{canonicalWslSourceRef}'.");
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

    public async Task<TaskAttachment> CaptureUrlAsync(
        int workspaceId,
        string url,
        bool allowInternalNetwork = false,
        CancellationToken cancellationToken = default)
    {
        if (_securityOptions.AirGappedMode)
        {
            throw new AirGappedOperationException(
                "URL attachment capture is prohibited because NightCodeForge is operating in Air-Gapped mode.");
        }

        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var initialUri) ||
            (initialUri.Scheme != Uri.UriSchemeHttp && initialUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new AttachmentConversionException(
                url ?? string.Empty,
                $"URL '{url}' must be an absolute http:// or https:// URL.");
        }

        var workspace = await RequireWorkspaceAsync(workspaceId, cancellationToken);
        var effectiveAllowInternal = allowInternalNetwork || _intakeOptions.AllowInternalNetworkUrlCapture;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(AttachmentConstants.UrlFetchTimeoutSeconds));

        var (finalUri, downloadedBytes, contentTypeMediaType) = await FetchUrlWithRedirectsAndSsrfGuardAsync(
            initialUri,
            effectiveAllowInternal,
            timeoutCts.Token,
            cancellationToken);

        var sourceSha256 = ComputeSha256Hex(downloadedBytes);
        var existing = await _attachmentRepository.GetByWorkspaceAndSha256Async(workspaceId, sourceSha256, cancellationToken);
        if (existing != null)
        {
            return existing;
        }

        var (convertedBody, extractedTitle) = await ConvertCapturedResponseAsync(
            finalUri,
            downloadedBytes,
            contentTypeMediaType,
            cancellationToken);

        var (_, hostAttachmentsDir) = EnsureAttachmentsDirectories(workspace);
        var title = string.IsNullOrWhiteSpace(extractedTitle) ? BuildFallbackUrlTitle(finalUri) : extractedTitle;
        var slug = Slugify(BuildUrlSlugSeed(finalUri, extractedTitle));
        var capturedAt = DateTime.UtcNow;
        var fullMarkdown = BuildUrlCaptureMarkdown(finalUri.AbsoluteUri, capturedAt, title, sourceSha256, convertedBody);
        var storedBytes = Encoding.UTF8.GetBytes(fullMarkdown);

        var pending = new TaskAttachment
        {
            WorkspaceId = workspaceId,
            Title = title,
            Slug = slug,
            RelativePath = BuildPendingRelativePath(AttachmentConstants.UrlCaptureSuffix),
            MarkdownPath = null,
            ContentKind = TaskAttachmentContentKind.RichDocument,
            MediaType = AttachmentConstants.ResolveMediaType(AttachmentConstants.MarkdownExtension),
            WidthPx = null,
            HeightPx = null,
            SourceKind = TaskAttachmentSourceKind.UrlCapture,
            SourceRef = finalUri.AbsoluteUri,
            IsSymlink = false,
            Sha256 = sourceSha256,
            SizeBytes = storedBytes.LongLength,
            CreatedAt = capturedAt
        };

        var created = await _attachmentRepository.CreateAsync(pending, cancellationToken);
        var writtenFiles = new List<string>();
        try
        {
            var seqPrefix = FormatSequencePrefix(created.Id);
            var relativePath = $"{seqPrefix}-{slug}{AttachmentConstants.UrlCaptureSuffix}";
            var hostFilePath = Path.Combine(hostAttachmentsDir, relativePath);

            await File.WriteAllBytesAsync(hostFilePath, storedBytes, cancellationToken);
            writtenFiles.Add(hostFilePath);

            created.RelativePath = relativePath;
            created.MarkdownPath = relativePath;
            await _attachmentRepository.UpdateAsync(created, cancellationToken);
            await SyncManifestInternalAsync(workspaceId, hostAttachmentsDir, cancellationToken);

            _logger.LogInformation(
                "Captured URL attachment #{AttachmentId} ('{SourceRef}' -> '{RelativePath}', {SizeBytes} bytes) in Workspace #{WorkspaceId}.",
                created.Id, created.SourceRef, created.RelativePath, created.SizeBytes, workspaceId);

            return created;
        }
        catch (Exception ex)
        {
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
            if (hostPrimaryPath != null &&
                !PathsEqual(hostPrimaryPath, attachment.SourceRef) &&
                File.Exists(hostPrimaryPath))
            {
                File.Delete(hostPrimaryPath);
            }
        }
        else if (hostPrimaryPath != null && File.Exists(hostPrimaryPath))
        {
            File.Delete(hostPrimaryPath);
        }

        if (!string.IsNullOrWhiteSpace(attachment.MarkdownPath) &&
            !string.Equals(attachment.MarkdownPath, attachment.RelativePath, StringComparison.OrdinalIgnoreCase))
        {
            var hostMarkdownPath = ResolveContainedFilePath(hostAttachmentsDir, attachment.MarkdownPath);
            if (hostMarkdownPath != null &&
                !PathsEqual(hostMarkdownPath, attachment.SourceRef) &&
                File.Exists(hostMarkdownPath))
            {
                File.Delete(hostMarkdownPath);
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

    public IReadOnlyList<string> ExtractCitedHostPaths(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string raw, bool isQuoted)
        {
            var candidate = raw.Trim();
            if (!isQuoted)
            {
                candidate = candidate.TrimEnd(TrailingCitationPunctuation);
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            if (candidate.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals("/workspace", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (seen.Add(candidate))
            {
                results.Add(candidate);
            }
        }

        foreach (Match match in QuotedHostPathRegex().Matches(message))
        {
            TryAdd(match.Groups["path"].Value, isQuoted: true);
        }

        var scrubbed = QuotedHostPathRegex().Replace(message, " ");

        foreach (Match match in BacktickHostPathRegex().Matches(scrubbed))
        {
            TryAdd(match.Groups["path"].Value, isQuoted: true);
        }

        scrubbed = BacktickHostPathRegex().Replace(scrubbed, " ");

        foreach (Match match in UncPathRegex().Matches(scrubbed))
        {
            TryAdd(match.Groups["path"].Value, isQuoted: false);
        }

        scrubbed = UncPathRegex().Replace(scrubbed, " ");

        foreach (Match match in WindowsDrivePathRegex().Matches(scrubbed))
        {
            TryAdd(match.Groups["path"].Value, isQuoted: false);
        }

        foreach (Match match in PosixOrTildePathRegex().Matches(scrubbed))
        {
            TryAdd(match.Groups["path"].Value, isQuoted: false);
        }

        return results;
    }

    public IReadOnlyList<string> ExtractCitedUrls(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in HttpUrlRegex().Matches(message))
        {
            var candidate = match.Groups["url"].Value.Trim().TrimEnd(TrailingCitationPunctuation);
            if (!string.IsNullOrWhiteSpace(candidate) &&
                Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                seen.Add(uri.AbsoluteUri))
            {
                results.Add(uri.AbsoluteUri);
            }
        }

        return results;
    }

    protected virtual Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken)
    {
        return Dns.GetHostAddressesAsync(host, cancellationToken);
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

        var expandedUnixDir = ExpandLeadingTildeForWslOrHost(rawAttachmentsDir);
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

    private string ExpandLeadingTildeForWslOrHost(string path)
    {
        if (path != "~" && !path.StartsWith("~/", StringComparison.Ordinal) && !path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return path;
        }

        var rel = path.Length > 1 ? path[1..].TrimStart('/', '\\').Replace('\\', '/') : string.Empty;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var wslUser = _pathResolver.GetWslUserName();
            if (!string.IsNullOrWhiteSpace(wslUser))
            {
                return string.IsNullOrEmpty(rel) ? $"/home/{wslUser}" : $"/home/{wslUser}/{rel}";
            }
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var baseDir = userProfile.Replace('\\', '/').TrimEnd('/');
            return string.IsNullOrEmpty(rel) ? baseDir : $"{baseDir}/{rel}";
        }

        return path;
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
        else if (IsWindowsDrivePath(trimmed))
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
                if (TryConvertMntDriveToWindowsPath(canonicalWslSourceRef, out var windowsDrivePath) &&
                    File.Exists(windowsDrivePath))
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

        if (!File.Exists(hostReadablePath))
        {
            throw new AttachmentTargetNotFoundException(
                canonicalWslSourceRef,
                $"Attachment host target '{trimmed}' (resolved to '{hostReadablePath}') does not exist or is unreadable.");
        }

        return (hostReadablePath, canonicalWslSourceRef);
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

    private static bool IsWindowsDrivePath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

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

    private async Task<(Uri FinalUri, byte[] Bytes, string? ContentTypeMediaType)> FetchUrlWithRedirectsAndSsrfGuardAsync(
        Uri initialUri,
        bool allowInternalNetwork,
        CancellationToken fetchCancellationToken,
        CancellationToken callerCancellationToken)
    {
        var currentUri = initialUri;

        for (var redirectCount = 0; redirectCount <= AttachmentConstants.MaxUrlRedirects; redirectCount++)
        {
            await ValidateUriSsrfSafeAsync(currentUri, allowInternalNetwork, fetchCancellationToken);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    fetchCancellationToken);

                if (IsRedirectStatusCode(response.StatusCode))
                {
                    if (redirectCount >= AttachmentConstants.MaxUrlRedirects)
                    {
                        throw new AttachmentConversionException(
                            initialUri.AbsoluteUri,
                            $"URL '{initialUri.AbsoluteUri}' exceeded maximum redirect limit ({AttachmentConstants.MaxUrlRedirects}).");
                    }

                    var location = response.Headers.Location
                        ?? throw new AttachmentConversionException(
                            currentUri.AbsoluteUri,
                            $"Redirect response ({(int)response.StatusCode}) from '{currentUri.AbsoluteUri}' omitted the Location header.");

                    var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                    if (nextUri.Scheme != Uri.UriSchemeHttp && nextUri.Scheme != Uri.UriSchemeHttps)
                    {
                        throw new AttachmentConversionException(
                            nextUri.AbsoluteUri,
                            $"Redirect target '{nextUri.AbsoluteUri}' uses unsupported scheme '{nextUri.Scheme}'.");
                    }

                    currentUri = nextUri;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new AttachmentConversionException(
                        currentUri.AbsoluteUri,
                        $"Failed to capture URL '{currentUri.AbsoluteUri}': HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
                }

                if (response.Content.Headers.ContentLength is long contentLength &&
                    contentLength > AttachmentConstants.MaxUrlDownloadBytes)
                {
                    throw new AttachmentConversionException(
                        currentUri.AbsoluteUri,
                        $"URL '{currentUri.AbsoluteUri}' exceeds maximum download size of {AttachmentConstants.MaxUrlDownloadBytes} bytes (Content-Length: {contentLength}).");
                }

                await using var responseStream = await response.Content.ReadAsStreamAsync(fetchCancellationToken);
                var bytes = await ReadStreamBoundedAsync(
                    responseStream,
                    AttachmentConstants.MaxUrlDownloadBytes,
                    currentUri.AbsoluteUri,
                    fetchCancellationToken);

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                return (currentUri, bytes, mediaType);
            }
            catch (AttachmentConversionException)
            {
                throw;
            }
            catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
            {
                throw new AttachmentConversionException(
                    currentUri.AbsoluteUri,
                    $"Capturing URL '{currentUri.AbsoluteUri}' timed out after {AttachmentConstants.UrlFetchTimeoutSeconds}s.");
            }
            catch (HttpRequestException ex)
            {
                throw new AttachmentConversionException(
                    currentUri.AbsoluteUri,
                    $"HTTP request failed while capturing URL '{currentUri.AbsoluteUri}': {ex.Message}",
                    ex);
            }
        }

        throw new AttachmentConversionException(
            initialUri.AbsoluteUri,
            $"URL '{initialUri.AbsoluteUri}' exceeded maximum redirect limit ({AttachmentConstants.MaxUrlRedirects}).");
    }

    private async Task ValidateUriSsrfSafeAsync(
        Uri uri,
        bool allowInternalNetwork,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new AttachmentConversionException(
                uri.AbsoluteUri,
                $"URL '{uri.AbsoluteUri}' uses unsupported scheme '{uri.Scheme}'. Only http and https are allowed.");
        }

        var host = uri.DnsSafeHost.TrimEnd('.');
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new AttachmentConversionException(uri.AbsoluteUri, $"URL '{uri.AbsoluteUri}' does not contain a valid host.");
        }

        if ((host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) &&
            !allowInternalNetwork)
        {
            throw new AttachmentConversionException(
                uri.AbsoluteUri,
                $"URL '{uri.AbsoluteUri}' blocked by SSRF guard: localhost targets are prohibited.");
        }

        if (IPAddress.TryParse(host, out var literalIp))
        {
            ValidateIpAddressSsrfSafe(uri, literalIp, allowInternalNetwork);
            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await ResolveHostAddressesAsync(host, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AttachmentConversionException(
                uri.AbsoluteUri,
                $"DNS resolution failed for host '{host}' in URL '{uri.AbsoluteUri}': {ex.Message}",
                ex);
        }

        if (addresses.Length == 0)
        {
            throw new AttachmentConversionException(
                uri.AbsoluteUri,
                $"DNS resolution returned no addresses for host '{host}' in URL '{uri.AbsoluteUri}'.");
        }

        foreach (var address in addresses)
        {
            ValidateIpAddressSsrfSafe(uri, address, allowInternalNetwork);
        }
    }

    private static void ValidateIpAddressSsrfSafe(Uri uri, IPAddress rawAddress, bool allowInternalNetwork)
    {
        var address = rawAddress.IsIPv4MappedToIPv6 ? rawAddress.MapToIPv4() : rawAddress;

        if (IsAlwaysBlockedAddress(address))
        {
            throw new AttachmentConversionException(
                uri.AbsoluteUri,
                $"URL '{uri.AbsoluteUri}' blocked by SSRF guard: link-local, cloud metadata, or unspecified address '{address}' is prohibited.");
        }

        if (!allowInternalNetwork && IsPrivateOrLoopbackAddress(address))
        {
            throw new AttachmentConversionException(
                uri.AbsoluteUri,
                $"URL '{uri.AbsoluteUri}' blocked by SSRF guard: loopback or private network address '{address}' is prohibited.");
        }
    }

    private static bool IsAlwaysBlockedAddress(IPAddress address)
    {
        if (IPAddress.Any.Equals(address) ||
            IPAddress.IPv6Any.Equals(address) ||
            IPAddress.Broadcast.Equals(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            // 0.0.0.0/8 ("this" network)
            if (bytes[0] == 0)
            {
                return true;
            }
            // 169.254.0.0/16 (link-local & cloud metadata 169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal ||
                   address.Equals(IPAddress.IPv6None) ||
                   address.Equals(AwsIpv6MetadataAddress);
        }

        return false;
    }

    private static bool IsPrivateOrLoopbackAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 127 // 127.0.0.0/8 loopback
                || bytes[0] == 10 // 10.0.0.0/8 private
                || (bytes[0] == 100 && (bytes[1] & 0xC0) == 64) // 100.64.0.0/10 CGNAT
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) // 172.16.0.0/12 private
                || (bytes[0] == 192 && bytes[1] == 168); // 192.168.0.0/16 private
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            // fc00::/7 Unique Local IPv6 Unicast
            return (bytes[0] & 0xFE) == 0xFC || address.IsIPv6SiteLocal;
        }

        return false;
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private async Task<(string MarkdownBody, string? ExtractedTitle)> ConvertCapturedResponseAsync(
        Uri finalUri,
        byte[] bytes,
        string? contentTypeMediaType,
        CancellationToken cancellationToken)
    {
        if (bytes.Length == 0)
        {
            throw new AttachmentConversionException(
                finalUri.AbsoluteUri,
                $"URL '{finalUri.AbsoluteUri}' returned an empty response body.");
        }

        var normalizedMediaType = ResolveEffectiveUrlMediaType(finalUri, contentTypeMediaType);

        string markdown;
        if (normalizedMediaType is "text/markdown" or "text/plain" or "application/json")
        {
            markdown = Encoding.UTF8.GetString(bytes);
        }
        else if (normalizedMediaType is "application/pdf" or "application/vnd.openxmlformats-officedocument.wordprocessingml.document" or "text/html" or "application/xhtml+xml")
        {
            var expectedExt = normalizedMediaType == "application/pdf"
                ? ".pdf"
                : normalizedMediaType == "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                    ? ".docx"
                    : ".html";
            ValidateRichDocumentSignature(bytes, expectedExt, finalUri.AbsoluteUri);
            markdown = await ConvertRichDocumentWithMarkitdownAsync(
                bytes,
                normalizedMediaType,
                finalUri.AbsoluteUri,
                finalUri,
                cancellationToken);
        }
        else
        {
            throw new AttachmentConversionException(
                finalUri.AbsoluteUri,
                $"URL '{finalUri.AbsoluteUri}' returned unsupported content type '{normalizedMediaType}'.");
        }

        string? title = null;
        if (normalizedMediaType is "text/html" or "application/xhtml+xml")
        {
            title = ExtractHtmlTitle(bytes);
        }

        title ??= ExtractLeadingMarkdownHeading(markdown);
        return (markdown, title);
    }

    private static string ResolveEffectiveUrlMediaType(Uri finalUri, string? contentTypeMediaType)
    {
        var raw = contentTypeMediaType?.Split(';')[0].Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(raw) && !string.Equals(raw, "application/octet-stream", StringComparison.Ordinal))
        {
            return raw;
        }

        var ext = Path.GetExtension(finalUri.AbsolutePath);
        var fromExtension = AttachmentConstants.ResolveMediaType(ext);
        return string.Equals(fromExtension, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? "text/html"
            : fromExtension.ToLowerInvariant();
    }

    private static string? ExtractHtmlTitle(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var html = reader.ReadToEnd();
        var match = HtmlTitleRegex().Match(html);
        if (!match.Success)
        {
            return null;
        }

        var decoded = WebUtility.HtmlDecode(match.Groups["title"].Value);
        var collapsed = WhitespaceCollapseRegex().Replace(decoded, " ").Trim();
        return string.IsNullOrWhiteSpace(collapsed) ? null : collapsed;
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

    private static string? ExtractLeadingMarkdownHeading(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        using var reader = new StringReader(markdown);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                var heading = trimmed[2..].Trim();
                return string.IsNullOrWhiteSpace(heading) ? null : heading;
            }

            break;
        }

        return null;
    }

    private static string BuildFallbackUrlTitle(Uri uri)
    {
        var path = uri.AbsolutePath.Trim('/');
        return string.IsNullOrEmpty(path) ? uri.Host : $"{uri.Host}/{path}";
    }

    private static string BuildUrlSlugSeed(Uri uri, string? extractedTitle)
    {
        if (!string.IsNullOrWhiteSpace(extractedTitle))
        {
            return extractedTitle;
        }

        var lastSegment = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (!string.IsNullOrWhiteSpace(lastSegment))
        {
            var withoutExtension = Path.GetFileNameWithoutExtension(lastSegment);
            if (!string.IsNullOrWhiteSpace(withoutExtension))
            {
                return withoutExtension;
            }

            var trimmedDots = lastSegment.TrimStart('.');
            if (!string.IsNullOrWhiteSpace(trimmedDots))
            {
                return trimmedDots;
            }
        }

        return uri.Host;
    }

    private static string BuildUrlCaptureMarkdown(
        string sourceUrl,
        DateTime capturedAtUtc,
        string title,
        string sha256,
        string markdownBody)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"source: \"{EscapeYamlDoubleQuoted(sourceUrl)}\"");
        sb.AppendLine($"capturedAt: \"{capturedAtUtc:O}\"");
        sb.AppendLine($"title: \"{EscapeYamlDoubleQuoted(title)}\"");
        sb.AppendLine($"sha256: \"{sha256}\"");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(markdownBody);
        if (!markdownBody.EndsWith('\n'))
        {
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string EscapeYamlDoubleQuoted(string value) =>
        (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private readonly record struct PreparedAttachmentContent(
        string MediaType,
        int? WidthPx,
        int? HeightPx,
        string? ConvertedMarkdown);
}
