using Hangfire;
using Hangfire.MemoryStorage;
using Hangfire.States;
using NightCodeForge.Infrastructure.BackgroundJobs;

namespace NightCodeForge.Infrastructure.Tests;

public class HangfireQueuePriorityTests
{
    [Fact]
    public void LlmInferenceServerOptions_EnforcesWorkerCountOneAndPriorityQueues()
    {
        var options = HangfireConfigurationExtensions.CreateLlmInferenceServerOptions();

        Assert.Equal("NCF-LLM-Inference-Worker", options.ServerName);
        Assert.Equal(1, options.WorkerCount);
        Assert.Equal(new[] { HangfireQueues.LlmUrgent, HangfireQueues.LlmInference }, options.Queues);
    }

    [Fact]
    public void UtilityServerOptions_EnforcesWorkerCountFourAndDefaultQueue()
    {
        var options = HangfireConfigurationExtensions.CreateUtilityServerOptions();

        Assert.Equal("NCF-Utility-Worker", options.ServerName);
        Assert.Equal(4, options.WorkerCount);
        Assert.Equal(new[] { HangfireQueues.Default }, options.Queues);
    }

    [Fact]
    public async Task HangfireQueue_ProcessesUrgentBeforeInference()
    {
        var storage = new MemoryStorage();
        var client = new BackgroundJobClient(storage);

        // Pause processing initially by creating server after enqueuing
        // Enqueue inference job first, then urgent job
        client.Create(() => TestJobTarget.RecordJob("inference-1"), new EnqueuedState(HangfireQueues.LlmInference));
        client.Create(() => TestJobTarget.RecordJob("inference-2"), new EnqueuedState(HangfireQueues.LlmInference));
        client.Create(() => TestJobTarget.RecordJob("urgent-1"), new EnqueuedState(HangfireQueues.LlmUrgent));

        TestJobTarget.ExecutionOrder.Clear();

        var options = HangfireConfigurationExtensions.CreateLlmInferenceServerOptions();
        using var server = new BackgroundJobServer(options, storage);

        // Wait for jobs to be processed
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (TestJobTarget.ExecutionOrder.Count < 3 && DateTime.UtcNow < timeout)
        {
            await Task.Delay(50);
        }

        Assert.Equal(3, TestJobTarget.ExecutionOrder.Count);
        // urgent-1 must be dequeued and executed before inference-1 and inference-2
        Assert.Equal("urgent-1", TestJobTarget.ExecutionOrder[0]);
    }

    [Fact]
    public async Task HangfireQueue_P1CriticalTask_ExecutesAheadOfPreviouslyQueuedP3Tasks()
    {
        var storage = new MemoryStorage();
        var client = new BackgroundJobClient(storage);

        TestJobTarget.ExecutionOrder.Clear();

        // Enqueue standard P3 tasks first into inference queue
        client.Create(() => TestJobTarget.RecordJob("P3-Task-101"), new EnqueuedState(HangfireQueues.LlmInference));
        client.Create(() => TestJobTarget.RecordJob("P3-Task-102"), new EnqueuedState(HangfireQueues.LlmInference));

        // Enqueue P1 Critical task later into urgent queue
        client.Create(() => TestJobTarget.RecordJob("P1-Critical-Task-999"), new EnqueuedState(HangfireQueues.LlmUrgent));

        var options = HangfireConfigurationExtensions.CreateLlmInferenceServerOptions();
        using var server = new BackgroundJobServer(options, storage);

        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (TestJobTarget.ExecutionOrder.Count < 3 && DateTime.UtcNow < timeout)
        {
            await Task.Delay(50);
        }

        Assert.Equal(3, TestJobTarget.ExecutionOrder.Count);
        // P1 Critical task MUST execute first even though it was enqueued after P3 tasks
        Assert.Equal("P1-Critical-Task-999", TestJobTarget.ExecutionOrder[0]);
        Assert.Contains("P3-Task-101", TestJobTarget.ExecutionOrder.Skip(1));
        Assert.Contains("P3-Task-102", TestJobTarget.ExecutionOrder.Skip(1));
    }

    [Fact]
    public async Task HangfireQueue_StrictSingleWorkerConcurrency_NeverExecutesConcurrently()
    {
        var storage = new MemoryStorage();
        var client = new BackgroundJobClient(storage);

        TestJobTarget.MaxConcurrentWorkers = 0;
        TestJobTarget.CurrentConcurrentWorkers = 0;
        TestJobTarget.ExecutionOrder.Clear();

        for (int i = 0; i < 5; i++)
        {
            var id = i;
            client.Create(() => TestJobTarget.ConcurrentJob(id), new EnqueuedState(HangfireQueues.LlmInference));
        }

        var options = HangfireConfigurationExtensions.CreateLlmInferenceServerOptions();
        using var server = new BackgroundJobServer(options, storage);

        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (TestJobTarget.ExecutionOrder.Count < 5 && DateTime.UtcNow < timeout)
        {
            await Task.Delay(50);
        }

        Assert.Equal(5, TestJobTarget.ExecutionOrder.Count);
        // WorkerCount = 1 strictly prevents concurrency: max concurrency must be exactly 1
        Assert.Equal(1, TestJobTarget.MaxConcurrentWorkers);
    }

    [Fact]
    public void HangfireTaskJobQueue_EnqueueUrgent_EnqueuesToLlmUrgentQueue()
    {
        var storage = new MemoryStorage();
        var client = new BackgroundJobClient(storage);
        var queue = new HangfireTaskJobQueue(client);

        var jobId = queue.EnqueueUrgent(101);

        Assert.NotNull(jobId);
        using var connection = storage.GetConnection();
        var jobData = connection.GetJobData(jobId);
        Assert.NotNull(jobData);
        Assert.Equal("ProcessTaskAsync", jobData.Job.Method.Name);
        Assert.Equal(101, jobData.Job.Args[0]);
        Assert.Equal(default(CancellationToken), jobData.Job.Args[1]);

        var stateData = connection.GetStateData(jobId);
        Assert.NotNull(stateData);
        Assert.Equal(HangfireQueues.LlmUrgent, stateData.Data["Queue"]);
    }

    [Fact]
    public void HangfireTaskJobQueue_EnqueueInference_EnqueuesToLlmInferenceQueue()
    {
        var storage = new MemoryStorage();
        var client = new BackgroundJobClient(storage);
        var queue = new HangfireTaskJobQueue(client);

        var jobId = queue.EnqueueInference(202);

        Assert.NotNull(jobId);
        using var connection = storage.GetConnection();
        var jobData = connection.GetJobData(jobId);
        Assert.NotNull(jobData);
        Assert.Equal("ProcessTaskAsync", jobData.Job.Method.Name);
        Assert.Equal(202, jobData.Job.Args[0]);
        Assert.Equal(default(CancellationToken), jobData.Job.Args[1]);

        var stateData = connection.GetStateData(jobId);
        Assert.NotNull(stateData);
        Assert.Equal(HangfireQueues.LlmInference, stateData.Data["Queue"]);
    }

    [Fact]
    public async Task HangfireQueue_PropagatesCancellation_WhenServerStops()
    {
        var storage = new MemoryStorage();
        var client = new BackgroundJobClient(storage);

        var startedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        TestJobTarget.OnJobStarted = startedTcs;
        TestJobTarget.OnJobCancelled = cancelledTcs;

        client.Create(() => TestJobTarget.CancellableJob(default), new EnqueuedState(HangfireQueues.LlmInference));

        var options = HangfireConfigurationExtensions.CreateLlmInferenceServerOptions();
        var server = new BackgroundJobServer(options, storage);

        try
        {
            var started = await Task.WhenAny(startedTcs.Task, Task.Delay(5000));
            Assert.Same(startedTcs.Task, started);

            server.SendStop();

            var cancelled = await Task.WhenAny(cancelledTcs.Task, Task.Delay(5000));
            Assert.Same(cancelledTcs.Task, cancelled);
            Assert.True(await cancelledTcs.Task);
        }
        finally
        {
            server.Dispose();
        }
    }
}

public static class TestJobTarget
{
    private static readonly object Lock = new();
    public static List<string> ExecutionOrder { get; } = new();
    public static int CurrentConcurrentWorkers;
    public static int MaxConcurrentWorkers;
    public static TaskCompletionSource<bool>? OnJobStarted;
    public static TaskCompletionSource<bool>? OnJobCancelled;

    public static async Task CancellableJob(CancellationToken cancellationToken)
    {
        OnJobStarted?.TrySetResult(true);
        try
        {
            await Task.Delay(10000, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            OnJobCancelled?.TrySetResult(true);
            throw;
        }
    }

    public static void RecordJob(string name)
    {
        lock (Lock)
        {
            ExecutionOrder.Add(name);
        }
    }

    public static void ConcurrentJob(int id)
    {
        int current;
        lock (Lock)
        {
            CurrentConcurrentWorkers++;
            current = CurrentConcurrentWorkers;
            if (current > MaxConcurrentWorkers)
            {
                MaxConcurrentWorkers = current;
            }
        }

        Thread.Sleep(50);

        lock (Lock)
        {
            CurrentConcurrentWorkers--;
            ExecutionOrder.Add($"job-{id}");
        }
    }
}
