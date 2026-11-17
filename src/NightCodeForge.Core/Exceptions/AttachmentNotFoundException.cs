namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when a requested workspace attachment cannot be found or does not belong to the expected workspace.
/// </summary>
public class AttachmentNotFoundException : InvalidOperationException
{
    public int? AttachmentId { get; }
    public int? WorkspaceId { get; }

    public AttachmentNotFoundException(int attachmentId)
        : base($"Attachment with ID #{attachmentId} was not found.")
    {
        AttachmentId = attachmentId;
    }

    public AttachmentNotFoundException(int attachmentId, int workspaceId)
        : base($"Attachment with ID #{attachmentId} was not found in Workspace #{workspaceId}.")
    {
        AttachmentId = attachmentId;
        WorkspaceId = workspaceId;
    }

    public AttachmentNotFoundException(int attachmentId, string message)
        : base(message)
    {
        AttachmentId = attachmentId;
    }

    public AttachmentNotFoundException(string message)
        : base(message)
    {
    }

    public AttachmentNotFoundException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
