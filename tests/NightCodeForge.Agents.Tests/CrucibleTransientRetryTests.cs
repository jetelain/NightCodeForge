using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Resilience;
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

    [Fact]
    public async Task CrucibleAgent_WhenWrappedOperationsPausedExceptionThrown_SuspendsAsInterrupted_WithoutFailing()
    {
        int invocationCount = 0;
        var client = new ScriptedChatClient((messages, options, ct) =>
        {
            Interlocked.Increment(ref invocationCount);
            throw new HttpRequestException(
                "Transport aborted",
                new NightCodeForge.Core.Exceptions.OperationsPausedException("Global operations are paused."));
        });

        var taskRepo = new InMemoryTaskRepository();
        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            TaskRepository = taskRepo
        }.Build();

        var context = NewContext();
        await taskRepo.CreateAsync(context.ForgeTask);

        await Assert.ThrowsAsync<OperationCanceledException>(() => harness.RunStageAsync(context, CancellationToken.None));
        Assert.Equal(1, invocationCount);

        var convs = await taskRepo.GetConversationsAsync(context.ForgeTask.Id);
        var conv = Assert.Single(convs);
        Assert.Equal(StageExecutionStatus.Interrupted, conv.Status);
    }

    [Fact]
    public async Task CrucibleAgent_LongTurnBeforeMidStreamError_DoesNotPrematurelyExhaustOutageTimer()
    {
        var fakeTime = new ManualTimeProvider();
        int invocationCount = 0;

        var client = new ScriptedChatClient((messages, options, ct) =>
        {
            var callIndex = Interlocked.Increment(ref invocationCount);
            if (callIndex == 1)
            {
                return SlowTurnThenFailureStream(fakeTime, ct);
            }

            return RecoveryStream(ct);
        });

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            TaskRepository = new InMemoryTaskRepository(),
            TimeProvider = fakeTime,
            RetryOptions = Microsoft.Extensions.Options.Options.Create(new NightCodeForge.Core.Resilience.LlmRetryPolicyOptions
            {
                MaxRetries = 3,
                BaseDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(5),
                MaxRetryDuration = TimeSpan.FromSeconds(30)
            })
        }.Build();

        var context = NewContext();
        var result = await harness.RunStageAsync(context, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(2, invocationCount);

        static async IAsyncEnumerable<ChatResponseUpdate> SlowTurnThenFailureStream(
            ManualTimeProvider clock,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            // Simulate a 10-minute prompt evaluation / tool execution before a mid-stream connection drop
            clock.Advance(TimeSpan.FromMinutes(10));
            throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        static async IAsyncEnumerable<ChatResponseUpdate> RecoveryStream([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate { Contents = [new TextContent("Recovered after mid-stream reset.")] };
        }
    }

    [Fact]
    public async Task CrucibleAgent_WhenPausedDuringRetryBackoff_SuspendsImmediately_AndMarksConversationInterrupted()
    {
        int invocationCount = 0;
        bool isPaused = false;
        var mockOps = new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsControlService>();
        mockOps.Setup(m => m.IsPaused).Returns(() => isPaused);

        var streamer = new CallbackStreamer(evt =>
        {
            if (evt.Kind == AgentActivityKind.Notice)
            {
                // Trigger global operations pause right as the harness enters retry backoff
                _ = Task.Run(async () =>
                {
                    await Task.Delay(15);
                    isPaused = true;
                    mockOps.Raise(m => m.OnOperationsStateChanged += null, true);
                });
            }
        });

        var client = new ScriptedChatClient((messages, options, ct) =>
        {
            Interlocked.Increment(ref invocationCount);
            throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
        });

        var taskRepo = new InMemoryTaskRepository();
        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            ActivityStreamer = streamer,
            TaskRepository = taskRepo,
            OperationsControlService = mockOps.Object,
            RetryOptions = Microsoft.Extensions.Options.Options.Create(new NightCodeForge.Core.Resilience.LlmRetryPolicyOptions
            {
                MaxRetries = 5,
                BaseDelay = TimeSpan.FromSeconds(30),
                MaxDelay = TimeSpan.FromSeconds(30),
                MaxRetryDuration = TimeSpan.FromMinutes(5)
            })
        }.Build();

        var context = NewContext();
        await taskRepo.CreateAsync(context.ForgeTask);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.RunStageAsync(context, CancellationToken.None));
        Assert.Equal(1, invocationCount);

        var convs = await taskRepo.GetConversationsAsync(context.ForgeTask.Id);
        var conv = Assert.Single(convs);
        Assert.Equal(StageExecutionStatus.Interrupted, conv.Status);
    }

    [Fact]
    public void AddNightCodeForgeAgents_WithoutConfigurationInDi_ResolvesRetryOptionsDefaultsWithoutThrowing()
    {
        var services = new ServiceCollection();
        services.AddNightCodeForgeAgents(LlmProviderType.OllamaRouter);

        using var sp = services.BuildServiceProvider();
        var retryOptions = sp.GetRequiredService<IOptions<RetryPolicyOptions>>().Value;
        var llmRetryOptions = sp.GetRequiredService<IOptions<LlmRetryPolicyOptions>>().Value;

        Assert.Equal(RetryPolicyOptions.DefaultMaxRetries, retryOptions.MaxRetries);
        Assert.Equal(LlmRetryPolicyOptions.DefaultLlmMaxRetries, llmRetryOptions.MaxRetries);
    }

    [Fact]
    public void AddNightCodeForgeAgents_WithConfigurationInDi_BindsCanonicalRetryPolicySections()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RetryPolicy:MaxRetries"] = "4",
                ["LlmRetryPolicy:MaxRetries"] = "42",
                ["LlmRetryPolicy:MaxRetryDurationSeconds"] = "900.0"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddNightCodeForgeAgents(LlmProviderType.OllamaRouter);

        using var sp = services.BuildServiceProvider();
        var retryOptions = sp.GetRequiredService<IOptions<RetryPolicyOptions>>().Value;
        var llmRetryOptions = sp.GetRequiredService<IOptions<LlmRetryPolicyOptions>>().Value;

        Assert.Equal(4, retryOptions.MaxRetries);
        Assert.Equal(42, llmRetryOptions.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(900.0), llmRetryOptions.MaxRetryDuration);
    }

    [Fact]
    public void AddNightCodeForgeAgents_BindsSecurityOptions()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AirGappedMode"] = "true"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddNightCodeForgeAgents(LlmProviderType.OllamaRouter);

        using var sp = services.BuildServiceProvider();
        var secOptions = sp.GetRequiredService<IOptions<NightCodeForge.Core.Security.SecurityOptions>>().Value;

        Assert.True(secOptions.AirGappedMode);
    }

    [Fact]
    public void AddNightCodeForgeAgents_WhenConfigureCalledBeforeAddNightCodeForgeAgents_ExplicitConfigureOverridesConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RetryPolicy:MaxRetries"] = "3",
                ["LlmRetryPolicy:MaxRetries"] = "20"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.Configure<RetryPolicyOptions>(o => o.MaxRetries = 11);
        services.Configure<LlmRetryPolicyOptions>(o => o.MaxRetries = 55);
        services.AddNightCodeForgeAgents(LlmProviderType.OllamaRouter);

        using var sp = services.BuildServiceProvider();
        var retryOptions = sp.GetRequiredService<IOptions<RetryPolicyOptions>>().Value;
        var llmRetryOptions = sp.GetRequiredService<IOptions<LlmRetryPolicyOptions>>().Value;

        Assert.Equal(11, retryOptions.MaxRetries);
        Assert.Equal(55, llmRetryOptions.MaxRetries);
    }

    [Fact]
    public async Task OllamaRouterChatClientFactory_BroadcastRetryNotice_WhenMultipleTasksActive_ResolvesStageFromMatchingTaskId()
    {
        var telemetryMock = new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsTelemetryService>();
        var opsControlMock = new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsControlService>();
        var task101 = new NightCodeForge.Core.Workflow.ActiveTaskSummary(101, "Task 101", "Implementation", 1, "default", DateTime.UtcNow.AddMinutes(-5), "Node-A");
        var task102 = new NightCodeForge.Core.Workflow.ActiveTaskSummary(102, "Task 102", "CodeReview", 1, "default", DateTime.UtcNow.AddMinutes(-1), "Node-B");
        telemetryMock.Setup(t => t.GetActiveTask(null)).Returns(task102);
        telemetryMock.Setup(t => t.GetActiveTask(101)).Returns(task101);
        telemetryMock.Setup(t => t.GetActiveTask(102)).Returns(task102);

        string? capturedStageId = null;
        int? capturedTaskId = null;
        telemetryMock
            .Setup(t => t.RecordLlmCallRetry(
                Moq.It.IsAny<RetryNotificationContext>(),
                Moq.It.IsAny<int?>(),
                Moq.It.IsAny<string?>(),
                Moq.It.IsAny<string?>()))
            .Callback<RetryNotificationContext, int?, string?, string?>((_, taskId, stageId, _) =>
            {
                capturedTaskId = taskId;
                capturedStageId = stageId;
            });

        AgentActivityEvent? capturedEvent = null;
        var streamer = new CallbackStreamer(evt => capturedEvent = evt);
        using var factory = new NightCodeForge.Agents.Inference.OllamaRouterChatClientFactory(
            Microsoft.Extensions.Options.Options.Create(new NightCodeForge.Agents.Inference.LlmProviderOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            telemetryMock.Object,
            opsControlMock.Object,
            TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new LlmRetryPolicyOptions()),
            streamer);

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/chat");
        request.Headers.Add(NightCodeForge.Agents.Inference.LlmCorrelationHeaders.TaskId, "101");

        var retryCtx = new RetryNotificationContext(
            request.RequestUri,
            request.Method,
            request.Headers,
            Attempt: 1,
            MaxRetries: 20,
            Delay: TimeSpan.FromSeconds(2),
            ElapsedDuration: TimeSpan.FromSeconds(2),
            MaxDuration: TimeSpan.FromMinutes(30),
            ErrorMessage: "HTTP 503",
            StatusCode: System.Net.HttpStatusCode.ServiceUnavailable);

        // Invoke protected BroadcastRetryNoticeAsync on LlmChatClientFactoryBase via reflection to verify correlation resolution
        var method = typeof(NightCodeForge.Agents.Inference.LlmChatClientFactoryBase)
            .GetMethod("BroadcastRetryNoticeAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task?)method.Invoke(factory, new object?[] { retryCtx, null, CancellationToken.None });
        Assert.NotNull(task);
        await task;

        Assert.Equal(101, capturedTaskId);
        Assert.Equal("Implementation", capturedStageId);
        Assert.NotNull(capturedEvent);
        Assert.Equal(101, capturedEvent.TaskId);
        Assert.Equal("Implementation", capturedEvent.StageId);
    }

    [Fact]
    public async Task CrucibleAgent_WhenMidStreamTimeoutOccursAfterDanglingToolCall_SanitizesHistoryBeforeRetry()
    {
        int invocationCount = 0;
        var streamer = new RecordingStreamer();
        List<ChatMessage>? secondCallMessages = null;
        List<ChatMessage>? retryReceivedMessages = null;

        var client = new ScriptedChatClient((messages, options, ct) =>
        {
            var callIndex = Interlocked.Increment(ref invocationCount);
            if (callIndex == 1)
            {
                // Call 1 completes normally with a tool call so PerServiceCallChatHistoryPersistingChatClient
                // persists the Assistant(FunctionCallContent) turn into chatHistory.
                return EmitCompletedToolCallTurnAsync(ct);
            }

            if (callIndex == 2)
            {
                // Call 2 is the follow-up model call after tool execution, which aborts mid-stream before completion,
                // leaving chatHistory with the dangling Assistant(FunctionCallContent) from Call 1.
                secondCallMessages = messages.ToList();
                return EmitPartialThenTimeoutAsync(ct);
            }

            retryReceivedMessages = messages.ToList();
            return SuccessStream(ct);
        });

        static async IAsyncEnumerable<ChatResponseUpdate> EmitCompletedToolCallTurnAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new FunctionCallContent("call_dangling", "read", new Dictionary<string, object?> { ["path"] = "foo.txt" })]
            };
        }

        static async IAsyncEnumerable<ChatResponseUpdate> EmitPartialThenTimeoutAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate { Contents = [new TextContent("Partial response before stall")] };
            throw new LlmStreamTimeoutException(
                "LLM stream timeout",
                new TimeoutException(),
                StreamTimeoutPhase.MidStreamIdleStall,
                elapsedSeconds: 305.0,
                configuredTimeoutSeconds: 300.0,
                generatedTokens: 42,
                tokensPerSecond: 12.5);
        }

        static async IAsyncEnumerable<ChatResponseUpdate> SuccessStream([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate { Contents = [new TextContent("Done")] };
        }

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            ActivityStreamer = streamer,
            TaskRepository = new InMemoryTaskRepository()
        }.Build();

        var context = NewContext();
        var result = await harness.RunStageAsync(context, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(3, invocationCount);

        // Verify Call 2 saw the tool call in flight
        Assert.NotNull(secondCallMessages);
        Assert.Contains(
            secondCallMessages.SelectMany(m => m.Contents).OfType<FunctionCallContent>(),
            fc => fc.CallId == "call_dangling");

        // Verify Call 3 (the retry after Call 2 failed mid-stream) had the dangling tool call stripped by SanitizeInFlightTurns
        Assert.NotNull(retryReceivedMessages);
        var hasDanglingToolCall = retryReceivedMessages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Any(fc => fc.CallId == "call_dangling");
        Assert.False(hasDanglingToolCall, "Dangling in-flight tool call should have been stripped by SanitizeInFlightTurns before retry.");

        var notice = streamer.Events.FirstOrDefault(e => e.Kind == AgentActivityKind.Notice && e.Content.Contains("Transient network error"));
        Assert.NotNull(notice);
        Assert.Contains("42 tokens generated in 305.0s before failure", notice.Content);
    }

    private sealed class CallbackStreamer(Action<AgentActivityEvent> onEvent) : IAgentActivityStreamer
    {
        public Task BroadcastAsync(AgentActivityEvent activityEvent, CancellationToken cancellationToken = default)
        {
            onEvent(activityEvent);
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<AgentActivityEvent> SubscribeAsync(int? taskId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        private long _timestampTicks;

        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestampTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta)
        {
            _utcNow = _utcNow.Add(delta);
            _timestampTicks += delta.Ticks;
        }
    }
}
