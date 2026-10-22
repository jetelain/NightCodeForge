using NightCodeForge.ScenarioRunner.Environment;
using Xunit;

namespace NightCodeForge.ScenarioRunner.Tests;

public class InMemoryTaskJobQueueTests
{
    [Fact]
    public void Delete_RemovesJob_AndPreventsSubsequentDequeue()
    {
        var queue = new InMemoryTaskJobQueue();

        var job1Id = queue.EnqueueUrgent(101);
        var job2Id = queue.EnqueueUrgent(102);

        // Delete job 1
        var deleted = queue.Delete(job1Id);
        Assert.True(deleted);

        // Attempting to delete again returns false
        Assert.False(queue.Delete(job1Id));

        // TryDequeue should skip job 1 and dequeue job 2
        var dequeued = queue.TryDequeue(out var job);
        Assert.True(dequeued);
        Assert.NotNull(job);
        Assert.Equal(job2Id, job.JobId);
        Assert.Equal(102, job.TaskId);

        // Queue should now be empty
        Assert.False(queue.TryDequeue(out var emptyJob));
        Assert.Null(emptyJob);
    }

    [Fact]
    public void Delete_OnInferenceQueue_SkipsDeletedInferenceJob()
    {
        var queue = new InMemoryTaskJobQueue();

        var job1Id = queue.EnqueueInference(201);
        var job2Id = queue.EnqueueInference(202);

        var deleted = queue.Delete(job1Id);
        Assert.True(deleted);

        var dequeued = queue.TryDequeue(out var job);
        Assert.True(dequeued);
        Assert.NotNull(job);
        Assert.Equal(job2Id, job.JobId);
        Assert.Equal(202, job.TaskId);

        Assert.False(queue.TryDequeue(out _));
    }
}
