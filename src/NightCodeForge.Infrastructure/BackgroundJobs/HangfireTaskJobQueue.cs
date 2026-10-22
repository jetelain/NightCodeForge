using Hangfire;
using Hangfire.States;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire implementation of ITaskJobQueue dispatching execution jobs to priority-segregated queues.
/// </summary>
public class HangfireTaskJobQueue : ITaskJobQueue
{
    private readonly IBackgroundJobClient _backgroundJobClient;

    public HangfireTaskJobQueue(IBackgroundJobClient backgroundJobClient)
    {
        _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));
    }

    public string EnqueueUrgent(int taskId)
    {
        return _backgroundJobClient.Create<ITaskOrchestrator>(
            o => o.ProcessTaskAsync(taskId, default),
            new EnqueuedState(HangfireQueues.LlmUrgent));
    }

    public string EnqueueInference(int taskId)
    {
        return _backgroundJobClient.Create<ITaskOrchestrator>(
            o => o.ProcessTaskAsync(taskId, default),
            new EnqueuedState(HangfireQueues.LlmInference));
    }

    public bool Delete(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return false;
        return _backgroundJobClient.Delete(jobId);
    }
}
