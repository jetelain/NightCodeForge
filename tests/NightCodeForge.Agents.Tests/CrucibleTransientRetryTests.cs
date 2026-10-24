using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Streaming;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public class CrucibleTransientRetryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "crucible-retry-" + Guid.NewGuid().ToString("N"));

    public CrucibleTransientRetryTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> _streamHandler;

        public ScriptedChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> streamHandler)
        {
            _streamHandler = streamHandler ?? throw new ArgumentNullException(nameof(streamHandler));
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => _streamHandler(chatMessages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RecordingStreamer : IAgentActivityStreamer
    {
        private readonly List<AgentActivityEvent> _events = new();
        public IReadOnlyList<AgentActivityEvent> Events
        {
            get { lock (_events) return _events.ToList(); }
        }

        public Task BroadcastAsync(AgentActivityEvent activityEvent, CancellationToken cancellationToken = default)
        {
            lock (_events) _events.Add(activityEvent);
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<AgentActivityEvent> SubscribeAsync(int? taskId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private AgentSessionContext NewContext()
    {
        var task = new ForgeTask { Id = 101, Title = "Network retry task", Description = "Implement resilient retry" };
        return new AgentSessionContext
        {
            ForgeTask = task,
            Workspace = new Workspace { Id = 1, RepositoryPath = _tempDir },
            Profile = new AgentProfile { Id = "dev", Mode = AgentMode.Full },
            StageId = "implementation",
            RequiredMode = AgentMode.Full,
            WorkspacePath = _tempDir
        };
    }

    [Fact]
    public async Task CrucibleAgent_AutomaticallyRetries_OnReportedFrenchNetworkError_AndSucceeds()
    {
        int invocationCount = 0;
        var streamer = new RecordingStreamer();

        var client = new ScriptedChatClient((messages, options, ct) =>
        {
            var callIndex = Interlocked.Increment(ref invocationCount);
            if (callIndex == 1)
            {
                // Throw the exact French network error reported by the user on the first call
                var innerSocket = new SocketException((int)SocketError.ConnectionReset);
                throw new IOException(
                    "Unable to read data from the transport connection: Une connexion existante a dû être fermée par l’hôte distant..",
                    innerSocket);
            }

            return SuccessStream(ct);
        });

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            ActivityStreamer = streamer,
            TaskRepository = new InMemoryTaskRepository()
        }.Build();

        var context = NewContext();
        var result = await harness.RunStageAsync(context, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(2, invocationCount);

        // Verify that a Notice activity event was broadcast announcing the retry
        var notice = streamer.Events.FirstOrDefault(e => e.Kind == AgentActivityKind.Notice);
        Assert.NotNull(notice);
        Assert.Contains("Transient network error", notice.Content);
        Assert.Contains("Une connexion existante a dû être fermée", notice.Content);

        static async IAsyncEnumerable<ChatResponseUpdate> SuccessStream([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate { Contents = [new TextContent("Work completed cleanly after network reconnection.")] };
        }
    }

    [Fact]
    public async Task CrucibleAgent_FailsFast_WhenTransientNetworkErrorExhaustsRetries()
    {
        int invocationCount = 0;
        var client = new ScriptedChatClient((messages, options, ct) =>
        {
            Interlocked.Increment(ref invocationCount);
            var innerSocket = new SocketException((int)SocketError.ConnectionReset);
            throw new IOException(
                "Unable to read data from the transport connection: Une connexion existante a dû être fermée par l’hôte distant..",
                innerSocket);
        });

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            TaskRepository = new InMemoryTaskRepository()
        }.Build();

        var context = NewContext();
        var result = await harness.RunStageAsync(context, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains("Une connexion existante a dû être fermée", result.Error);
        // Initial attempt + 3 retries = 4 total invocations
        Assert.Equal(4, invocationCount);
    }

    [Fact]
    public async Task CrucibleAgent_FailsImmediately_WithoutRetry_OnNonTransientError()
    {
        int invocationCount = 0;
        var client = new ScriptedChatClient((messages, options, ct) =>
        {
            Interlocked.Increment(ref invocationCount);
            throw new InvalidOperationException("Fatal non-network failure.");
        });

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            TaskRepository = new InMemoryTaskRepository()
        }.Build();

        var context = NewContext();
        var result = await harness.RunStageAsync(context, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains("Fatal non-network failure", result.Error);
        // Non-transient errors must fail fast immediately on attempt 1 without retry
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task CrucibleAgent_DoesNotMultiplyRetries_WhenErrorAlreadyRetriedByHttpHandler()
    {
        int invocationCount = 0;
        var client = new ScriptedChatClient((messages, options, ct) =>
        {
            Interlocked.Increment(ref invocationCount);
            var innerSocket = new SocketException((int)SocketError.ConnectionReset);
            var ex = new IOException("Unable to read data from the transport connection: Connection reset.", innerSocket);
            // Mark exception as already retried by TransientHttpErrorRetryHandler
            ex.Data[NightCodeForge.Core.Resilience.ResilienceConstants.RetriedKey] = true;
            throw ex;
        });

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            TaskRepository = new InMemoryTaskRepository()
        }.Build();

        var context = NewContext();
        var result = await harness.RunStageAsync(context, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
        // Must fail immediately on invocation 1 without retrying since inner HTTP handler already exhausted retries
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task CrucibleAgent_WhenOperationsPausedExceptionThrown_SuspendsAsInterrupted_WithoutRetry()
    {
        int invocationCount = 0;
        var client = new ScriptedChatClient((messages, options, ct) =>
        {
            Interlocked.Increment(ref invocationCount);
            throw new NightCodeForge.Core.Exceptions.OperationsPausedException("Global operations are paused (GPU VRAM reclaimed).");
        });

        var taskRepo = new InMemoryTaskRepository();
        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            TaskRepository = taskRepo
        }.Build();

        var context = NewContext();
        await taskRepo.CreateAsync(context.ForgeTask);

        // RunStageAsync should suspend on pause and rethrow OperationCanceledException instead of failing or retrying
        await Assert.ThrowsAsync<OperationCanceledException>(() => harness.RunStageAsync(context, CancellationToken.None));

        // It must NOT retry on OperationsPausedException
        Assert.Equal(1, invocationCount);

        // Active conversation must be marked Interrupted
        var convs = await taskRepo.GetConversationsAsync(context.ForgeTask.Id);
        var conv = Assert.Single(convs);
        Assert.Equal(StageExecutionStatus.Interrupted, conv.Status);
    }
}
