using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Tests;

public class MockTaskJobQueue : ITaskJobQueue
{
    public List<int> UrgentEnqueued { get; } = new();
    public List<int> InferenceEnqueued { get; } = new();
    public List<(int TaskId, StageQueueTier Tier, int Priority)> EnqueuedStages { get; } = new();

    public string EnqueueUrgent(int taskId, DateTime? originalEnqueuedAt = null)
    {
        UrgentEnqueued.Add(taskId);
        return $"job-urgent-{taskId}";
    }

    public string EnqueueStage(int taskId, StageQueueTier tier, int priority, DateTime? originalEnqueuedAt = null)
    {
        EnqueuedStages.Add((taskId, tier, priority));
        if (priority <= TaskPriority.Critical)
        {
            UrgentEnqueued.Add(taskId);
            return $"job-urgent-{taskId}";
        }

        InferenceEnqueued.Add(taskId);
        return $"job-{tier.ToString().ToLowerInvariant()}-{taskId}";
    }

    public List<string> DeletedJobs { get; } = new();

    public bool Delete(string jobId)
    {
        DeletedJobs.Add(jobId);
        return true;
    }
}
