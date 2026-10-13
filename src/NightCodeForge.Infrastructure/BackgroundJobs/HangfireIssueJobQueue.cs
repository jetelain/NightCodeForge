using Hangfire;
using Hangfire.States;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire implementation of IIssueJobQueue dispatching execution jobs to priority-segregated queues.
/// </summary>
public class HangfireIssueJobQueue : IIssueJobQueue
{
    private readonly IBackgroundJobClient _backgroundJobClient;

    public HangfireIssueJobQueue(IBackgroundJobClient backgroundJobClient)
    {
        _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));
    }

    public string EnqueueUrgent(int issueId)
    {
        return _backgroundJobClient.Create<IIssueOrchestrator>(
            o => o.ProcessIssueAsync(issueId, CancellationToken.None),
            new EnqueuedState(HangfireQueues.LlmUrgent));
    }

    public string EnqueueInference(int issueId)
    {
        return _backgroundJobClient.Create<IIssueOrchestrator>(
            o => o.ProcessIssueAsync(issueId, CancellationToken.None),
            new EnqueuedState(HangfireQueues.LlmInference));
    }
}
