namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when a workflow definition or stage definition violates domain validation constraints.
/// </summary>
public class WorkflowValidationException : InvalidOperationException
{
    public WorkflowValidationException(string message)
        : base(message)
    {
    }

    public WorkflowValidationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
