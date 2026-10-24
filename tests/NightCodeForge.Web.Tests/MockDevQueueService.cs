using NightCodeForge.Core.Interfaces;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class MockDevQueueService : IDevQueueService
{
    public bool ShouldThrowOnGetSnapshot { get; set; }
    public DevQueueSnapshot Snapshot { get; set; } = new(
        UrgentJobs: new List<QueuedJobItem>(),
        InferenceJobs: new List<QueuedJobItem>(),
        UtilityJobs: new List<QueuedJobItem>(),
        ProcessingJobs: new List<QueuedJobItem>(),
        FailedJobs: new List<QueuedJobItem>(),
        ActiveLocks: new List<ActiveLockSnapshot>());

    public string? LastPromotedJobId { get; private set; }
    public int? LastPromotedTaskId { get; private set; }
    public string? LastCancelledJobId { get; private set; }
    public int? LastCancelledTaskId { get; private set; }
    public string? LastRequeuedJobId { get; private set; }
    public int? LastRequeuedTaskId { get; private set; }
    public int? LastReleasedWorkspaceId { get; private set; }
    public int? LastReleasedTaskId { get; private set; }

    public Task<DevQueueSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (ShouldThrowOnGetSnapshot)
        {
            throw new InvalidOperationException("Simulated transient database error");
        }
        return Task.FromResult(Snapshot);
    }

    public Task<bool> PromoteToUrgentAsync(string jobId, int taskId, CancellationToken cancellationToken = default)
    {
        LastPromotedJobId = jobId;
        LastPromotedTaskId = taskId;
        return Task.FromResult(true);
    }

    public Task<bool> PromoteToUrgentAsync(int taskId, CancellationToken cancellationToken = default)
    {
        LastPromotedTaskId = taskId;
        return Task.FromResult(true);
    }

    public Task<bool> CancelJobAsync(string jobId, int? taskId = null, CancellationToken cancellationToken = default)
    {
        LastCancelledJobId = jobId;
        LastCancelledTaskId = taskId;
        return Task.FromResult(true);
    }

    public Task<bool> RequeueJobAsync(string jobId, int? taskId = null, CancellationToken cancellationToken = default)
    {
        LastRequeuedJobId = jobId;
        LastRequeuedTaskId = taskId;
        return Task.FromResult(true);
    }

    public Task<bool> EmergencyReleaseLockAsync(int workspaceId, int? taskId = null, CancellationToken cancellationToken = default)
    {
        LastReleasedWorkspaceId = workspaceId;
        LastReleasedTaskId = taskId;
        return Task.FromResult(true);
    }

    public int? LastRescheduledTaskId { get; private set; }
    public bool? LastRescheduledRestartPipeline { get; private set; }
    public int? LastRetriedTaskId { get; private set; }
    public (int TaskId, Core.Domain.TaskRestartGitMode GitMode)? LastRestartPipelineCall { get; private set; }

    public Task<bool> RetryCurrentStageAsync(int taskId, CancellationToken cancellationToken = default)
    {
        LastRetriedTaskId = taskId;
        return Task.FromResult(true);
    }

    public Task<bool> RestartPipelineAsync(int taskId, Core.Domain.TaskRestartGitMode gitMode = Core.Domain.TaskRestartGitMode.PreserveWork, CancellationToken cancellationToken = default)
    {
        LastRestartPipelineCall = (taskId, gitMode);
        return Task.FromResult(true);
    }

    public int ReorderCallsCount { get; private set; }
    public int ReorderReturnCount { get; set; } = 2;

    public Task<int> ReorderInferenceQueueByPriorityAsync(CancellationToken cancellationToken = default)
    {
        ReorderCallsCount++;
        return Task.FromResult(ReorderReturnCount);
    }
}
