namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Abstraction for enqueuing issue execution jobs with priority routing.
/// </summary>
public interface IIssueJobQueue
{
    /// <summary>
    /// Enqueues an issue with urgent priority (llm-urgent queue) to be executed ahead of standard backlog.
    /// </summary>
    string EnqueueUrgent(int issueId);

    /// <summary>
    /// Enqueues an issue with standard inference priority (llm-inference queue).
    /// </summary>
    string EnqueueInference(int issueId);
}
