namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when an attachment format is unsupported, corrupt, fails validation, or fails conversion to Markdown.
/// </summary>
public class AttachmentConversionException : InvalidOperationException
{
    public string FileNameOrSource { get; }

    public AttachmentConversionException(string message)
        : base(message)
    {
        FileNameOrSource = string.Empty;
    }

    public AttachmentConversionException(string fileNameOrSource, string message)
        : base(message)
    {
        FileNameOrSource = fileNameOrSource;
    }

    public AttachmentConversionException(string fileNameOrSource, string message, Exception? innerException)
        : base(message, innerException)
    {
        FileNameOrSource = fileNameOrSource;
    }
}
