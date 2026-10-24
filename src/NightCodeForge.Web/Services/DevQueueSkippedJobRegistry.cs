using System.Collections.Concurrent;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Singleton registry tracking jobs that cannot be scheduled or reordered
/// (e.g. missing workflow pipeline or unresolvable stage) across scoped DevQueueService instances.
/// </summary>
public interface IDevQueueSkippedJobRegistry
{
    void RecordSkippedJob(SkippedJobItem item);
    void RemoveSkippedJob(string jobId);
    IReadOnlyList<SkippedJobItem> GetSkippedJobs();
    void PruneActiveJobIds(ISet<string> currentEnqueuedJobIds);
}

/// <summary>
/// Thread-safe in-memory singleton implementation of <see cref="IDevQueueSkippedJobRegistry"/>.
/// </summary>
public class DevQueueSkippedJobRegistry : IDevQueueSkippedJobRegistry
{
    private readonly ConcurrentDictionary<string, SkippedJobItem> _skippedJobs = new();

    public void RecordSkippedJob(SkippedJobItem item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        _skippedJobs[item.JobId] = item;
    }

    public void RemoveSkippedJob(string jobId)
    {
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            _skippedJobs.TryRemove(jobId, out _);
        }
    }

    public IReadOnlyList<SkippedJobItem> GetSkippedJobs()
    {
        return _skippedJobs.Values.ToList();
    }

    public void PruneActiveJobIds(ISet<string> currentEnqueuedJobIds)
    {
        if (currentEnqueuedJobIds == null) throw new ArgumentNullException(nameof(currentEnqueuedJobIds));
        foreach (var key in _skippedJobs.Keys)
        {
            if (!currentEnqueuedJobIds.Contains(key))
            {
                _skippedJobs.TryRemove(key, out _);
            }
        }
    }
}
