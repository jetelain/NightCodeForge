using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Coordinates workspace-scoped attachment ingestion (file drop and WSL2 symlink host paths),
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
}
