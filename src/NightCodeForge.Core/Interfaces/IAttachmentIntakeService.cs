using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Configuration options governing workspace attachment intake behavior.
/// </summary>
public sealed class AttachmentIntakeOptions
{
    public const string SectionName = "Attachments";

    /// <summary>
    /// Permits capturing http(s) URLs resolving to private/loopback networks (excluding cloud metadata 169.254.0.0/16 and link-local).
    /// </summary>
    public bool AllowInternalNetworkUrlCapture { get; set; }
}

/// <summary>
/// Coordinates workspace-scoped attachment ingestion (file drop, WSL2 symlink host paths, URL capture),
/// MarkItDown conversion, manifest synchronization, and host-safe deletion.
/// </summary>
public interface IAttachmentIntakeService
{
    Task<TaskAttachment> IngestFileDropAsync(
        int workspaceId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<TaskAttachment> IngestHostPathAsync(
        int workspaceId,
        string hostPath,
        CancellationToken cancellationToken = default);

    Task<TaskAttachment> CaptureUrlAsync(
        int workspaceId,
        string url,
        bool allowInternalNetwork = false,
        CancellationToken cancellationToken = default);

    Task DeleteAttachmentAsync(
        int attachmentId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskAttachment>> ListWorkspaceAttachmentsAsync(
        int workspaceId,
        CancellationToken cancellationToken = default);

    Task<TaskAttachment?> GetAttachmentAsync(
        int attachmentId,
        CancellationToken cancellationToken = default);

    Task SyncManifestAsync(
        int workspaceId,
        CancellationToken cancellationToken = default);

    IReadOnlyList<string> ExtractCitedHostPaths(string message);

    IReadOnlyList<string> ExtractCitedUrls(string message);
}
