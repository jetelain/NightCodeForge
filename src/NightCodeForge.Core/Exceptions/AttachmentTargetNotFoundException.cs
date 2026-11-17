namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when a host path attachment or symbolic link target does not exist, escapes allowed boundaries, or is unreadable.
/// </summary>
public class AttachmentTargetNotFoundException : InvalidOperationException
{
    public string TargetPath { get; }
    public int? AttachmentId { get; }

    public AttachmentTargetNotFoundException(string targetPath)
        : base($"Attachment host target '{targetPath}' does not exist or is unreadable.")
    {
        TargetPath = targetPath;
    }

    public AttachmentTargetNotFoundException(string targetPath, string message)
        : base(message)
    {
        TargetPath = targetPath;
    }

    public AttachmentTargetNotFoundException(int attachmentId, string targetPath, string message)
        : base(message)
    {
        AttachmentId = attachmentId;
        TargetPath = targetPath;
    }

    public AttachmentTargetNotFoundException(string targetPath, string message, Exception? innerException)
        : base(message, innerException)
    {
        TargetPath = targetPath;
    }
}
