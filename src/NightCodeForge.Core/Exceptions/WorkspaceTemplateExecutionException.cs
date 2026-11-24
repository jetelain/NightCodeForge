namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when workspace template validation, CLI scaffolding, or local Git initialization fails.
/// </summary>
public class WorkspaceTemplateExecutionException : Exception
{
    public string? TemplateId { get; }
    public string? FailedCommand { get; }
    public int? ExitCode { get; }
    public string? StandardError { get; }

    public WorkspaceTemplateExecutionException(string message)
        : base(message)
    {
    }

    public WorkspaceTemplateExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WorkspaceTemplateExecutionException(
        string templateId,
        string? failedCommand,
        int? exitCode,
        string? standardError,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        TemplateId = templateId;
        FailedCommand = failedCommand;
        ExitCode = exitCode;
        StandardError = standardError;
    }
}
