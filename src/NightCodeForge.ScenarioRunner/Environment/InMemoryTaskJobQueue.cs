using System.Collections.Concurrent;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.ScenarioRunner.Environment;

/// <summary>
/// In-memory implementation of ITaskJobQueue capturing all enqueued jobs
/// for inspection and synchronous/orchestrated execution during test scenarios.
/// </summary>
public class InMemoryTaskJobQueue : ITaskJobQueue
{
    public record EnqueuedJob(string JobId, int TaskId, string QueueName, DateTime EnqueuedAt);

    private readonly ConcurrentQueue<EnqueuedJob> _urgentJobs = new();
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

    public string EnqueueUrgent(int taskId)
    {
        var id = $"job-urgent-{Interlocked.Increment(ref _jobCounter)}";
        var job = new EnqueuedJob(id, taskId, "llm-urgent", DateTime.UtcNow);
        _urgentJobs.Enqueue(job);
        lock (_allJobs) { _allJobs.Add(job); }
        return id;
    }

    public string EnqueueInference(int taskId)
    {
        var id = $"job-inference-{Interlocked.Increment(ref _jobCounter)}";
        var job = new EnqueuedJob(id, taskId, "llm-inference", DateTime.UtcNow);
        _inferenceJobs.Enqueue(job);
        lock (_allJobs) { _allJobs.Add(job); }
        return id;
    }

    public bool TryDequeue(out EnqueuedJob? job)
    {
        // Prioritize urgent queue
        if (_urgentJobs.TryDequeue(out job))
        {
            return true;
        }

        return _inferenceJobs.TryDequeue(out job);
    }

    public void Clear()
    {
        while (_urgentJobs.TryDequeue(out _)) { }
        while (_inferenceJobs.TryDequeue(out _)) { }
        lock (_allJobs) { _allJobs.Clear(); }
    }
}
