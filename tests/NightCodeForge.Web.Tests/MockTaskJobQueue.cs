using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Tests;

public class MockTaskJobQueue : ITaskJobQueue
{
    public List<int> UrgentEnqueued { get; } = new();
    public List<int> InferenceEnqueued { get; } = new();

    public string EnqueueUrgent(int taskId)
    {
        UrgentEnqueued.Add(taskId);
        return $"job-urgent-{taskId}";
    }

    public string EnqueueInference(int taskId)
    {
        InferenceEnqueued.Add(taskId);
        return $"job-inference-{taskId}";
    }

    public List<string> DeletedJobs { get; } = new();

    public bool Delete(string jobId)
    {
        DeletedJobs.Add(jobId);
        return true;
    }
}
