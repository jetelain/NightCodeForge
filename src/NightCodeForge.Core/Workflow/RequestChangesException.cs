namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Exception thrown when a review agent determines implementation is incomplete or requires rework,
/// routing the task back to the implementation stage with diagnostic feedback.
/// </summary>
public class RequestChangesException : Exception
{
    public string Feedback { get; }
    public string? Reason { get; }
    public string TargetStageId { get; }

    public RequestChangesException(string feedback, string? reason = null, string targetStageId = "implementation")
        : base(feedback)
    {
        Feedback = feedback;
        Reason = reason;
        TargetStageId = targetStageId;
    }
}
