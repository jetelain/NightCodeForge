using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Agents.Tests.Inference;

public class LlmCorrelationHeadersTests
{
    [Theory]
    [InlineData("ncf-task42-implementation", 42, "implementation")]
    [InlineData("ncf-task101-review", 101, "review")]
    [InlineData("ncf-task999-code-review-fix", 999, "code-review-fix")]
    public void ParseSessionId_ParsesTaskIdAndStageIdCorrectly(string sessionId, int expectedTaskId, string expectedStageId)
    {
        var (taskId, stageId) = LlmCorrelationHeaders.ParseSessionId(sessionId);
        Assert.Equal(expectedTaskId, taskId);
        Assert.Equal(expectedStageId, stageId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("arbitrary-session-without-task-pattern")]
    public void ParseSessionId_HandlesNonMatchingOrNullGracefully(string? sessionId)
    {
        var (taskId, stageId) = LlmCorrelationHeaders.ParseSessionId(sessionId);
        Assert.Null(taskId);
        Assert.Null(stageId);
    }

    [Fact]
    public void Apply_AttachesHeadersToHttpClient()
    {
        using var client = new HttpClient();
        LlmCorrelationHeaders.Apply(client, "ncf-task77-implementation");

        Assert.True(client.DefaultRequestHeaders.Contains(LlmCorrelationHeaders.SessionId));
        Assert.Equal("ncf-task77-implementation", client.DefaultRequestHeaders.GetValues(LlmCorrelationHeaders.SessionId).Single());

        Assert.True(client.DefaultRequestHeaders.Contains(LlmCorrelationHeaders.TaskId));
        Assert.Equal("77", client.DefaultRequestHeaders.GetValues(LlmCorrelationHeaders.TaskId).Single());

        Assert.True(client.DefaultRequestHeaders.Contains(LlmCorrelationHeaders.StageId));
        Assert.Equal("implementation", client.DefaultRequestHeaders.GetValues(LlmCorrelationHeaders.StageId).Single());
    }

    [Fact]
    public async Task BroadcastRetryNoticeAsync_ResolvesCorrelationFromHttpHeaders_AndBroadcastsNotice()
    {
        var mockTelemetry = new Mock<IOperationsTelemetryService>();
        var mockOps = new Mock<IOperationsControlService>();
        var mockStreamer = new Mock<IAgentActivityStreamer>();
        AgentActivityEvent? broadcasted = null;
        mockStreamer
            .Setup(s => s.BroadcastAsync(It.IsAny<AgentActivityEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AgentActivityEvent, CancellationToken>((evt, _) => broadcasted = evt)
            .Returns(Task.CompletedTask);

        using var factory = new TestableChatClientFactory(
            Options.Create(new LlmProviderOptions { Endpoint = "http://localhost:11434" }),
            NullLoggerFactory.Instance,
            mockTelemetry.Object,
            mockOps.Object,
            TimeProvider.System,
            Options.Create(new LlmRetryPolicyOptions()),
            mockStreamer.Object);

        var effectiveOptions = factory.ExposeCreateEffectiveRetryOptions(sessionId: null);

        using var httpClient = new HttpClient();
        LlmCorrelationHeaders.Apply(httpClient, "ncf-task88-code-review-fix");

        var retryCtx = new RetryNotificationContext(
            new Uri("http://localhost:11434/api/chat"),
            HttpMethod.Post,
            Headers: httpClient.DefaultRequestHeaders,
            Attempt: 2,
            MaxRetries: 20,
            Delay: TimeSpan.FromSeconds(4),
            ElapsedDuration: TimeSpan.FromSeconds(6),
            MaxDuration: TimeSpan.FromSeconds(300),
            ErrorMessage: "Bad Gateway",
            StatusCode: HttpStatusCode.BadGateway);

        await effectiveOptions.OnRetryAsync!(retryCtx, CancellationToken.None);

        Assert.NotNull(broadcasted);
        Assert.Equal("ncf-task88-code-review-fix", broadcasted!.SessionId);
        Assert.Equal(88, broadcasted.TaskId);
        Assert.Equal("code-review-fix", broadcasted.StageId);
        Assert.Equal(AgentActivityKind.Notice, broadcasted.Kind);
        Assert.Contains("HTTP 502 BadGateway", broadcasted.Content);
        Assert.Equal(2, broadcasted.Metadata![AgentActivityMetadataKeys.Attempt]);
        Assert.Equal(20, broadcasted.Metadata[AgentActivityMetadataKeys.MaxRetries]);
    }

    [Fact]
    public async Task BroadcastRetryNoticeAsync_FallsBackToActiveTask_AndThrowsWhenOperationsPaused()
    {
        var mockTelemetry = new Mock<IOperationsTelemetryService>();
        mockTelemetry
            .Setup(t => t.GetActiveTask())
            .Returns(new ActiveTaskSummary(55, "Fix Retry", "implementation", 1, "llm-inference", DateTime.UtcNow, "Local GPU"));

        bool paused = false;
        var mockOps = new Mock<IOperationsControlService>();
        mockOps.Setup(o => o.IsPaused).Returns(() => paused);

        var events = new List<AgentActivityEvent>();
        var mockStreamer = new Mock<IAgentActivityStreamer>();
        mockStreamer
            .Setup(s => s.BroadcastAsync(It.IsAny<AgentActivityEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AgentActivityEvent, CancellationToken>((evt, _) => events.Add(evt))
            .Returns(Task.CompletedTask);

        using var factory = new TestableChatClientFactory(
            Options.Create(new LlmProviderOptions { Endpoint = "http://localhost:11434" }),
            NullLoggerFactory.Instance,
            mockTelemetry.Object,
            mockOps.Object,
            TimeProvider.System,
            Options.Create(new LlmRetryPolicyOptions()),
            mockStreamer.Object);

        var effectiveOptions = factory.ExposeCreateEffectiveRetryOptions(sessionId: null);
        var retryCtx = new RetryNotificationContext(
            new Uri("http://localhost:11434/api/chat"),
            HttpMethod.Post,
            Headers: null,
            Attempt: 1,
            MaxRetries: 20,
            Delay: TimeSpan.FromSeconds(1),
            ElapsedDuration: TimeSpan.Zero,
            MaxDuration: TimeSpan.FromSeconds(300),
            ErrorMessage: "Connection refused");

        await effectiveOptions.OnRetryAsync!(retryCtx, CancellationToken.None);

        Assert.Single(events);
        Assert.Equal(55, events[0].TaskId);
        Assert.Equal("implementation", events[0].StageId);
        Assert.Equal("task-55", events[0].SessionId);

        paused = true;
        await Assert.ThrowsAsync<OperationsPausedException>(() =>
            effectiveOptions.OnRetryAsync!(retryCtx, CancellationToken.None));
    }

    [Fact]
    public async Task CreateSocketsHttpHandler_ConnectKeepAliveSocketAsync_ConnectsToLocalTcpEndpoint_AndDisposesOnFailure()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var handler = OllamaHttpConstants.CreateSocketsHttpHandler();
        using var client = new HttpClient(handler);

        var serverTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptSocketAsync();
            using var stream = new NetworkStream(accepted, ownsSocket: false);
            var buf = new byte[256];
            _ = await stream.ReadAsync(buf);
            var httpResp = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
            await stream.WriteAsync(httpResp);
        });

        var response = await client.GetAsync($"http://127.0.0.1:{port}/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await serverTask;

        listener.Stop();

        // Connecting after listener stopped should fail with HttpRequestException (wrapping SocketException)
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{port}/health"));
    }

    private sealed class TestableChatClientFactory : LlmChatClientFactoryBase
    {
        public TestableChatClientFactory(
            IOptions<LlmProviderOptions> options,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            IOperationsTelemetryService telemetryService,
            IOperationsControlService operationsControlService,
            TimeProvider timeProvider,
            IOptions<LlmRetryPolicyOptions> retryOptions,
            IAgentActivityStreamer activityStreamer)
            : base(options, loggerFactory, telemetryService, operationsControlService, timeProvider, retryOptions, activityStreamer)
        {
        }

        public LlmRetryPolicyOptions ExposeCreateEffectiveRetryOptions(string? sessionId) =>
            CreateEffectiveRetryOptions(sessionId);

        protected override IChatClient CreateRawChatClient(
            LlmProviderOptions options,
            string effectiveModel,
            OllamaRoutingTarget routingTarget,
            string? sessionId,
            SocketsHttpHandler socketsHandler) => new CallbackChatClient(_ => Task.FromResult(new ChatResponse()));
    }

    private sealed class CallbackChatClient(Func<CancellationToken, Task<ChatResponse>> handler) : IChatClient
    {
        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => handler(cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}

