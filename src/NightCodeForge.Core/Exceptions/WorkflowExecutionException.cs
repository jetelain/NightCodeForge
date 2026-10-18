namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Exception thrown when a workflow stage execution violates stage contracts or fails invariant validation.
/// </summary>
public class WorkflowExecutionException : Exception
{
    public WorkflowExecutionException(string message) : base(message)
    {
    }

    public WorkflowExecutionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
