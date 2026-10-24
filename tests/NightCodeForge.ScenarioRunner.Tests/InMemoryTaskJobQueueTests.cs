using NightCodeForge.Core.Domain;
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
    public void Delete_OnStageQueue_SkipsDeletedStageJob()
    {
        var queue = new InMemoryTaskJobQueue();

        var job1Id = queue.EnqueueStage(201, StageQueueTier.Implementation, TaskPriority.Normal);
        var job2Id = queue.EnqueueStage(202, StageQueueTier.Implementation, TaskPriority.Normal);

        var deleted = queue.Delete(job1Id);
        Assert.True(deleted);

        var dequeued = queue.TryDequeue(out var job);
        Assert.True(dequeued);
        Assert.NotNull(job);
        Assert.Equal(job2Id, job.JobId);
        Assert.Equal(202, job.TaskId);

        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void TryDequeue_RespectsStrictPriorityHierarchyAcrossTiers()
    {
        var queue = new InMemoryTaskJobQueue();

        // Enqueue in reverse priority order
        queue.EnqueueStage(401, StageQueueTier.TaskReview, TaskPriority.Normal);
        queue.EnqueueStage(301, StageQueueTier.Implementation, TaskPriority.Normal);
        queue.EnqueueStage(201, StageQueueTier.CodeReview, TaskPriority.Normal);
        queue.EnqueueUrgent(101);

        // 1. First should be Urgent
        Assert.True(queue.TryDequeue(out var j1));
        Assert.Equal(101, j1!.TaskId);

        // 2. Second should be CodeReview
        Assert.True(queue.TryDequeue(out var j2));
        Assert.Equal(201, j2!.TaskId);

        // 3. Third should be Implementation
        Assert.True(queue.TryDequeue(out var j3));
        Assert.Equal(301, j3!.TaskId);

        // 4. Fourth should be TaskReview
        Assert.True(queue.TryDequeue(out var j4));
        Assert.Equal(401, j4!.TaskId);

        Assert.False(queue.TryDequeue(out _));
    }
}
