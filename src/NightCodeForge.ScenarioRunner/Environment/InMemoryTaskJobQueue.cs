using System.Collections.Concurrent;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.ScenarioRunner.Environment;

/// <summary>
/// In-memory implementation of ITaskJobQueue capturing all enqueued jobs
/// for inspection and synchronous/orchestrated execution during test scenarios.
/// </summary>
public class InMemoryTaskJobQueue : ITaskJobQueue
{
    public record EnqueuedJob(string JobId, int TaskId, string QueueName, DateTime EnqueuedAt, int Priority = TaskPriority.Normal);

    private readonly ConcurrentQueue<EnqueuedJob> _urgentJobs = new();
    private readonly ConcurrentQueue<EnqueuedJob> _codeReviewJobs = new();
    private readonly ConcurrentQueue<EnqueuedJob> _implementationJobs = new();
    private readonly ConcurrentQueue<EnqueuedJob> _taskReviewJobs = new();
    private readonly ConcurrentQueue<EnqueuedJob> _inferenceJobs = new();
    private readonly List<EnqueuedJob> _allJobs = new();
    private int _jobCounter;

    public IReadOnlyList<EnqueuedJob> AllJobs
    {
        get
        {
            lock (_allJobs)
            {
                return _allJobs.ToList();
            }
        }
    }

    public string EnqueueUrgent(int taskId, DateTime? originalEnqueuedAt = null)
    {
        var id = $"job-urgent-{Interlocked.Increment(ref _jobCounter)}";
        var job = new EnqueuedJob(id, taskId, "llm-urgent", originalEnqueuedAt ?? DateTime.UtcNow, TaskPriority.Critical);
        _urgentJobs.Enqueue(job);
        lock (_allJobs) { _allJobs.Add(job); }
        return id;
    }

    public string EnqueueStage(int taskId, StageQueueTier tier, int priority, DateTime? originalEnqueuedAt = null)
    {
        if (!Enum.IsDefined(typeof(StageQueueTier), tier))
        {
            throw new ArgumentOutOfRangeException(nameof(tier), tier, $"Unsupported stage queue tier: {tier}");
        }

        string queueName;
        ConcurrentQueue<EnqueuedJob> targetQueue;

        if (priority <= (int)TaskPriority.Critical)
        {
            queueName = "llm-urgent";
            targetQueue = _urgentJobs;
        }
        else
        {
            switch (tier)
            {
                case StageQueueTier.CodeReview:
                    queueName = "llm-code-review";
                    targetQueue = _codeReviewJobs;
                    break;
                case StageQueueTier.Implementation:
                    queueName = "llm-implementation";
                    targetQueue = _implementationJobs;
                    break;
                case StageQueueTier.TaskReview:
                    queueName = "llm-task-review";
                    targetQueue = _taskReviewJobs;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(tier), tier, $"Unsupported stage queue tier: {tier}");
            }
        }

        var id = $"job-{queueName}-{Interlocked.Increment(ref _jobCounter)}";
        var job = new EnqueuedJob(id, taskId, queueName, originalEnqueuedAt ?? DateTime.UtcNow, priority);
        targetQueue.Enqueue(job);
        lock (_allJobs) { _allJobs.Add(job); }
        return id;
    }

    public bool TryDequeue(out EnqueuedJob? job)
    {
        // Polling sequence: urgent -> code-review -> implementation -> task-review -> inference
        var queues = new[] { _urgentJobs, _codeReviewJobs, _implementationJobs, _taskReviewJobs, _inferenceJobs };

        foreach (var q in queues)
        {
            while (q.TryDequeue(out var candidate))
            {
                lock (_allJobs)
                {
                    if (_allJobs.Any(j => j.JobId == candidate.JobId))
                    {
                        job = candidate;
                        return true;
                    }
                }
            }
        }

        job = null;
        return false;
    }

    public bool Delete(string jobId)
    {
        lock (_allJobs)
        {
            return _allJobs.RemoveAll(j => j.JobId == jobId) > 0;
        }
    }

    public Task<bool> HasUrgentJobsWaitingAsync(CancellationToken cancellationToken = default)
    {
        lock (_allJobs)
        {
            var activeJobIds = new HashSet<string>(_allJobs.Select(j => j.JobId));
            return Task.FromResult(_urgentJobs.Any(j => activeJobIds.Contains(j.JobId)));
        }
    }

    public void Clear()
    {
        while (_urgentJobs.TryDequeue(out _)) { }
        while (_codeReviewJobs.TryDequeue(out _)) { }
        while (_implementationJobs.TryDequeue(out _)) { }
        while (_taskReviewJobs.TryDequeue(out _)) { }
        while (_inferenceJobs.TryDequeue(out _)) { }
        lock (_allJobs) { _allJobs.Clear(); }
    }
}
