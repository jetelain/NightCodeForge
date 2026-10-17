namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when a requested workflow definition cannot be found in the database.
/// </summary>
public class WorkflowNotFoundException : InvalidOperationException
{
    public int? WorkflowId { get; }
    public string? WorkflowCode { get; }

    public WorkflowNotFoundException(int workflowId)
        : base($"Workflow with ID {workflowId} was not found in the database.")
    {
        WorkflowId = workflowId;
    }

    public WorkflowNotFoundException(string workflowCodeOrMessage)
        : base(workflowCodeOrMessage.Contains(' ')
            ? workflowCodeOrMessage
            : $"Workflow with code '{workflowCodeOrMessage}' was not found in the database.")
    {
        if (!workflowCodeOrMessage.Contains(' '))
        {
            WorkflowCode = workflowCodeOrMessage;
        }
    }

    public WorkflowNotFoundException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public static WorkflowNotFoundException ForCode(string workflowCode)
    {
        return new WorkflowNotFoundException(workflowCode);
    }
}
