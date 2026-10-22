namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Abstraction for enqueuing task execution jobs with priority routing.
/// </summary>
public interface ITaskJobQueue
{
    /// <summary>
    /// Enqueues a task with urgent priority (llm-urgent queue) to be executed ahead of standard backlog.
    /// </summary>
    string EnqueueUrgent(int taskId);

    /// <summary>
    /// Enqueues a task with standard inference priority (llm-inference queue).
    /// </summary>
    string EnqueueInference(int taskId);

    /// <summary>
    /// Deletes or cancels an enqueued job by its Hangfire job identifier.
    /// </summary>
    bool Delete(string jobId);
}
