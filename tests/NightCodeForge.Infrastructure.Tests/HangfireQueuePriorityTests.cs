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
}

public static class TestJobTarget
{
    private static readonly object Lock = new();
    public static List<string> ExecutionOrder { get; } = new();
    public static int CurrentConcurrentWorkers;
    public static int MaxConcurrentWorkers;

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
