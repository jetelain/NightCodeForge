using Hangfire;
using Hangfire.States;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire implementation of ITaskJobQueue dispatching execution jobs to priority-segregated queues.
/// </summary>
public class HangfireTaskJobQueue : ITaskJobQueue
{
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly JobStorage _jobStorage;
    private readonly ILogger<HangfireTaskJobQueue> _logger;

    public HangfireTaskJobQueue(
        IBackgroundJobClient backgroundJobClient,
        JobStorage jobStorage,
        ILogger<HangfireTaskJobQueue> logger)
    {
        _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));
        _jobStorage = jobStorage ?? throw new ArgumentNullException(nameof(jobStorage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string EnqueueUrgent(int taskId, DateTime? originalEnqueuedAt = null)
    {
        var jobId = _backgroundJobClient.Create<ITaskOrchestrator>(
            o => o.ProcessTaskAsync(taskId, default),
            new EnqueuedState(HangfireQueues.LlmUrgent));

        SetOriginalEnqueuedAt(jobId, originalEnqueuedAt);
        return jobId;
    }

    public string EnqueueStage(int taskId, StageQueueTier tier, int priority, DateTime? originalEnqueuedAt = null)
    {
        if (!Enum.IsDefined(typeof(StageQueueTier), tier))
        {
            throw new ArgumentOutOfRangeException(nameof(tier), tier, $"Unsupported stage queue tier: {tier}");
        }

        string queueName;
        if (priority <= (int)TaskPriority.Critical)
        {
            queueName = HangfireQueues.LlmUrgent;
        }
        else
        {
            queueName = tier switch
            {
                StageQueueTier.CodeReview => HangfireQueues.LlmCodeReview,
                StageQueueTier.Implementation => HangfireQueues.LlmImplementation,
                StageQueueTier.TaskReview => HangfireQueues.LlmTaskReview,
                _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, $"Unsupported stage queue tier: {tier}")
            };
        }

        var jobId = _backgroundJobClient.Create<ITaskOrchestrator>(
            o => o.ProcessTaskAsync(taskId, default),
            new EnqueuedState(queueName));

        SetOriginalEnqueuedAt(jobId, originalEnqueuedAt);
        return jobId;
    }

    public bool Delete(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return false;
        return _backgroundJobClient.Delete(jobId);
    }

    private void SetOriginalEnqueuedAt(string jobId, DateTime? originalEnqueuedAt)
    {
        var timestamp = originalEnqueuedAt ?? DateTime.UtcNow;
        try
        {
            using var connection = _jobStorage.GetConnection();
            connection.SetJobParameter(jobId, "OriginalEnqueuedAt", new DateTimeOffset(timestamp, TimeSpan.Zero).ToString("O"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record OriginalEnqueuedAt parameter for Hangfire job {JobId}", jobId);
            throw;
        }
    }
}
