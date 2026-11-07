using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Inference;

public class OllamaModelPreloadServicePauseTests
{
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task PreloadModelAsync_WhenOperationsPaused_ThrowsOperationsPausedException()
    {
        var opsMock = new Mock<IOperationsControlService>();
        opsMock.Setup(o => o.IsPaused).Returns(true);
        var streamerMock = new Mock<IAgentActivityStreamer>();
        var telemetryMock = new Mock<IOperationsTelemetryService>();

        bool httpCalled = false;
        var httpClient = new HttpClient(new FakeHttpMessageHandler(req =>
        {
            httpCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var options = Options.Create(new LlmEndpointOptions { Endpoint = "http://localhost:11434" });
        var service = new OllamaModelPreloadService(
            httpClient,
            opsMock.Object,
            streamerMock.Object,
            telemetryMock.Object,
            options,
            NullLogger<OllamaModelPreloadService>.Instance);

        await Assert.ThrowsAsync<OperationsPausedException>(() =>
            service.PreloadModelAsync("qwen2.5-coder:32b"));

        Assert.False(httpCalled);
    }

    [Fact]
    public async Task PreloadModelAsync_WhenOperationsNotPaused_SendsPostRequest()
    {
        var opsMock = new Mock<IOperationsControlService>();
        opsMock.Setup(o => o.IsPaused).Returns(false);
        var streamerMock = new Mock<IAgentActivityStreamer>();
        var telemetryMock = new Mock<IOperationsTelemetryService>();

        bool httpCalled = false;
        var httpClient = new HttpClient(new FakeHttpMessageHandler(req =>
        {
            httpCalled = true;
            Assert.Contains("api/generate", req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var options = Options.Create(new LlmEndpointOptions { Endpoint = "http://localhost:11434" });
        var service = new OllamaModelPreloadService(
            httpClient,
            opsMock.Object,
            streamerMock.Object,
            telemetryMock.Object,
            options,
            NullLogger<OllamaModelPreloadService>.Instance);

        var result = await service.PreloadModelAsync("qwen2.5-coder:32b");

        Assert.True(result.Success);
        Assert.True(result.LoadAccepted);
        Assert.True(httpCalled);
    }

    [Theory]
    [InlineData(null, "15m")]
    [InlineData(-1, "-1")]
    [InlineData(900, "900s")]
    public async Task PreloadModelAsync_FormatsKeepAliveAccordingToOptions(int? keepAliveSeconds, string expectedKeepAlive)
    {
        var opsMock = new Mock<IOperationsControlService>();
        opsMock.Setup(o => o.IsPaused).Returns(false);
        var streamerMock = new Mock<IAgentActivityStreamer>();
        var telemetryMock = new Mock<IOperationsTelemetryService>();

        string? capturedBody = null;
        var httpClient = new HttpClient(new FakeHttpMessageHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var options = Options.Create(new LlmEndpointOptions { Endpoint = "http://localhost:11434" });
        var service = new OllamaModelPreloadService(
            httpClient,
            opsMock.Object,
            streamerMock.Object,
            telemetryMock.Object,
            options,
            NullLogger<OllamaModelPreloadService>.Instance);

        var result = await service.PreloadModelAsync("qwen2.5-coder:32b", new ModelPreloadOptions(KeepAliveSeconds: keepAliveSeconds, TaskId: 7));

        Assert.True(result.Success);
        Assert.NotNull(capturedBody);
        Assert.Contains($"\"keep_alive\":\"{expectedKeepAlive}\"", capturedBody);

        streamerMock.Verify(s => s.BroadcastAsync(It.Is<AgentActivityEvent>(e =>
            e.Kind == AgentActivityKind.OptimizationAction &&
            e.TaskId == 7 &&
            e.Metadata != null &&
            Equals(e.Metadata[AgentActivityMetadataKeys.ActionType], AgentActivityMetadataKeys.ActionTypeJitModelPreload)), default), Times.Once);
    }

    [Fact]
    public async Task PreloadModelAsync_WhenNonSuccessStatus_RecordsFailureAndBroadcastsNotice()
    {
        var opsMock = new Mock<IOperationsControlService>();
        opsMock.Setup(o => o.IsPaused).Returns(false);
        var streamerMock = new Mock<IAgentActivityStreamer>();
        var telemetryMock = new Mock<IOperationsTelemetryService>();

        var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var options = Options.Create(new LlmEndpointOptions { Endpoint = "http://localhost:11434" });
        var service = new OllamaModelPreloadService(
            httpClient,
            opsMock.Object,
            streamerMock.Object,
            telemetryMock.Object,
            options,
            NullLogger<OllamaModelPreloadService>.Instance);

        var result = await service.PreloadModelAsync("qwen2.5-coder:32b");

        Assert.False(result.Success);
        Assert.False(result.LoadAccepted ?? true);
        telemetryMock.Verify(t => t.RecordModelPreload(It.Is<ModelPreloadTelemetryRecord>(r =>
            !r.Success && !r.IsActive && r.Model == "qwen2.5-coder:32b")), Times.Once);
        streamerMock.Verify(s => s.BroadcastAsync(It.Is<AgentActivityEvent>(e =>
            e.Kind == AgentActivityKind.Notice && e.Content.Contains("JIT Model Preload Warning")), default), Times.Once);
    }

    [Fact]
    public async Task PreloadModelAsync_WhenCallerTokenCancelled_ClearsActiveTelemetryAndRethrows()
    {
        var opsMock = new Mock<IOperationsControlService>();
        opsMock.Setup(o => o.IsPaused).Returns(false);
        var streamerMock = new Mock<IAgentActivityStreamer>();
        var telemetryMock = new Mock<IOperationsTelemetryService>();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => throw new OperationCanceledException(cts.Token)));
        var options = Options.Create(new LlmEndpointOptions { Endpoint = "http://localhost:11434" });
        var service = new OllamaModelPreloadService(
            httpClient,
            opsMock.Object,
            streamerMock.Object,
            telemetryMock.Object,
            options,
            NullLogger<OllamaModelPreloadService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PreloadModelAsync("qwen2.5-coder:32b", cancellationToken: cts.Token));

        telemetryMock.Verify(t => t.RecordModelPreload(It.Is<ModelPreloadTelemetryRecord>(r =>
            !r.Success && !r.IsActive && r.Model == "qwen2.5-coder:32b")), Times.Once);
    }
}
