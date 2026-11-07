using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Inference;

public class OllamaRouterModelPreloadServiceTests
{
    private readonly Mock<IOperationsControlService> _opsMock = new();
    private readonly Mock<IAgentActivityStreamer> _streamerMock = new();
    private readonly Mock<IOperationsTelemetryService> _telemetryMock = new();
    private readonly IOptions<LlmEndpointOptions> _options = Options.Create(new LlmEndpointOptions { Endpoint = "http://localhost:11434" });

    public OllamaRouterModelPreloadServiceTests()
    {
        _opsMock.Setup(o => o.IsPaused).Returns(false);
    }

    private OllamaRouterModelPreloadService CreateService(HttpClient client)
    {
        return new OllamaRouterModelPreloadService(
            client,
            _opsMock.Object,
            _streamerMock.Object,
            _telemetryMock.Object,
            _options,
            NullLogger<OllamaRouterModelPreloadService>.Instance);
    }

    [Fact]
    public async Task PreloadModelAsync_WhenOperationsPaused_ThrowsOperationsPausedException()
    {
        _opsMock.Setup(o => o.IsPaused).Returns(true);
        var handler = new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.OK));
        var service = CreateService(StubHttpHandler.Client(handler));

        await Assert.ThrowsAsync<OperationsPausedException>(() =>
            service.PreloadModelAsync("qwen2.5-coder:32b"));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PreloadModelAsync_WhenModelEmpty_ReturnsFalseWithoutCallingNetwork()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.OK));
        var service = CreateService(StubHttpHandler.Client(handler));

        var result = await service.PreloadModelAsync("   ");

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PreloadModelAsync_BuildsCorrectPath_ForNamespacedModel()
    {
        const string responseJson = """
        {
            "model": "jetelain/qwen3.5-9b",
            "selectedTarget": "Server-Rack-1",
            "keepAliveSeconds": -1,
            "numCtx": 65536,
            "contextIntentTokens": 65536,
            "loadAccepted": true,
            "residencyConfirmed": true,
            "preloadDurationMs": 1420,
            "candidates": [
                {
                    "target": "Server-Rack-1",
                    "eligible": true,
                    "inFlightRequests": 0,
                    "isResident": true,
                    "contextCeiling": 131072,
                    "serverClass": true,
                    "busy": false,
                    "skipReason": null
                }
            ],
            "reason": "Server-class target selected"
        }
        """;

        var handler = new StubHttpHandler(req =>
        {
            Assert.Equal("POST", req.Method.Method);
            Assert.Equal("/api/router/models/jetelain/qwen3.5-9b/preload", req.RequestUri!.AbsolutePath);
            return StubHttpHandler.Json(responseJson);
        });

        var service = CreateService(StubHttpHandler.Client(handler));
        var options = new ModelPreloadOptions(
            KeepAliveSeconds: null,
            ContextIntentTokens: 65536,
            Target: "Server-Rack-1",
            TaskId: 42,
            StageId: "build");

        var result = await service.PreloadModelAsync("jetelain/qwen3.5-9b", options);

        Assert.True(result.Success);
        Assert.Equal("jetelain/qwen3.5-9b", result.Model);
        Assert.Equal("Server-Rack-1", result.SelectedTarget);
        Assert.Equal(-1, result.KeepAliveSeconds);
        Assert.Equal(65536, result.NumCtx);
        Assert.True(result.LoadAccepted);
        Assert.True(result.ResidencyConfirmed);
        Assert.Equal(1420, result.PreloadDurationMs);
        Assert.Single(result.Candidates);
        Assert.True(result.Candidates[0].Eligible);
        Assert.True(result.Candidates[0].ServerClass);

        _telemetryMock.Verify(t => t.RecordModelPreload(It.Is<ModelPreloadTelemetryRecord>(r =>
            r.Success && r.Model == "jetelain/qwen3.5-9b" && r.SelectedTarget == "Server-Rack-1")), Times.Once);

        _streamerMock.Verify(s => s.BroadcastAsync(It.Is<AgentActivityEvent>(e =>
            e.Kind == AgentActivityKind.OptimizationAction &&
            e.TaskId == 42 &&
            e.Metadata != null &&
            Equals(e.Metadata[AgentActivityMetadataKeys.ActionType], AgentActivityMetadataKeys.ActionTypeJitModelPreload) &&
            e.Content.Contains("resident on node 'Server-Rack-1'")), default), Times.Once);
    }

    [Fact]
    public async Task PreloadModelAsync_503WithContextIntent_RetriesOnceWithSoftIntent()
    {
        int callCount = 0;
        const string retrySuccessJson = """
        {
            "model": "qwen2.5-coder:32b",
            "selectedTarget": "Server-Rack-1",
            "keepAliveSeconds": -1,
            "numCtx": 32768,
            "contextIntentTokens": null,
            "loadAccepted": true,
            "residencyConfirmed": true,
            "preloadDurationMs": 950,
            "candidates": [
                {
                    "target": "Server-Rack-1",
                    "eligible": true,
                    "inFlightRequests": 0,
                    "isResident": true,
                    "contextCeiling": 32768,
                    "serverClass": true,
                    "busy": false,
                    "skipReason": null
                }
            ],
            "reason": "Target selected with soft intent fallback"
        }
        """;

        var handler = new StubHttpHandler(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("""{"reason":"Context ceiling too low with margin"}""", System.Text.Encoding.UTF8, "application/json")
                };
                return resp;
            }

            return StubHttpHandler.Json(retrySuccessJson);
        });

        var service = CreateService(StubHttpHandler.Client(handler));
        var options = new ModelPreloadOptions(ContextIntentTokens: 65536);

        var result = await service.PreloadModelAsync("qwen2.5-coder:32b", options);

        Assert.Equal(2, callCount);
        Assert.True(result.Success);
        Assert.True(result.LoadAccepted);
        Assert.Equal("Server-Rack-1", result.SelectedTarget);
        Assert.Null(result.ContextIntentTokens);

        // First request sent contextIntentTokens: 65536, retry sent contextIntentTokens: null
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("65536", handler.Requests[0].Body);
        var retryBody = JsonSerializer.Deserialize<JsonElement>(handler.Requests[1].Body);
        Assert.Equal(JsonValueKind.Null, retryBody.GetProperty("contextIntentTokens").ValueKind);
    }

    [Fact]
    public async Task PreloadModelAsync_503NoEligibleCandidates_ParsesSkipReasonsAndBroadcastsWarning()
    {
        const string failJson = """
        {
            "model": "deepseek-r1:70b",
            "selectedTarget": null,
            "keepAliveSeconds": null,
            "numCtx": null,
            "contextIntentTokens": null,
            "loadAccepted": false,
            "residencyConfirmed": false,
            "candidates": [
                {
                    "target": "Laptop-Workstation",
                    "eligible": false,
                    "inFlightRequests": 0,
                    "isResident": false,
                    "contextCeiling": 16384,
                    "serverClass": false,
                    "busy": false,
                    "skipReason": "Target context ceiling (16,384) below context intent (65,536)"
                }
            ],
            "reason": "No candidate targets online and eligible"
        }
        """;

        var handler = new StubHttpHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(failJson, System.Text.Encoding.UTF8, "application/json")
            };
            return resp;
        });

        var service = CreateService(StubHttpHandler.Client(handler));
        var result = await service.PreloadModelAsync("deepseek-r1:70b");

        Assert.False(result.Success);
        Assert.False(result.LoadAccepted ?? true);
        Assert.Single(result.Candidates);
        Assert.Equal("Laptop-Workstation", result.Candidates[0].Target);
        Assert.Contains("below context intent", result.Candidates[0].SkipReason);

        _telemetryMock.Verify(t => t.RecordModelPreload(It.Is<ModelPreloadTelemetryRecord>(r =>
            !r.Success && !r.IsActive && r.Model == "deepseek-r1:70b")), Times.Once);

        _streamerMock.Verify(s => s.BroadcastAsync(It.Is<AgentActivityEvent>(e =>
            e.Kind == AgentActivityKind.Notice && e.Content.Contains("JIT Model Preload Warning")), default), Times.Once);
    }

    [Fact]
    public async Task PreloadModelAsync_OnHttpRequestException_ReturnsFailSoftResult()
    {
        var handler = new StubHttpHandler(_ => throw new HttpRequestException("Connection refused"));
        var service = CreateService(StubHttpHandler.Client(handler));

        var result = await service.PreloadModelAsync("qwen2.5-coder:32b");

        Assert.False(result.Success);
        Assert.False(result.LoadAccepted ?? true);
        Assert.Contains("Connection refused", result.Reason);
    }

    [Fact]
    public async Task PreloadModelAsync_OnHttpTimeout_ReturnsFailSoftResultAndClearsActiveTelemetry()
    {
        var handler = new StubHttpHandler(_ => throw new TaskCanceledException("HttpClient.Timeout elapsed"));
        var service = CreateService(StubHttpHandler.Client(handler));

        var result = await service.PreloadModelAsync("qwen2.5-coder:32b");

        Assert.False(result.Success);
        Assert.False(result.LoadAccepted ?? true);
        Assert.Contains("HttpClient.Timeout", result.Reason);

        _telemetryMock.Verify(t => t.RecordModelPreload(It.Is<ModelPreloadTelemetryRecord>(r =>
            !r.Success && !r.IsActive && r.Model == "qwen2.5-coder:32b")), Times.Once);
    }

    [Fact]
    public async Task PreloadModelAsync_WhenCallerTokenCancelled_ClearsActiveTelemetryAndRethrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = new StubHttpHandler(_ => throw new OperationCanceledException(cts.Token));
        var service = CreateService(StubHttpHandler.Client(handler));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PreloadModelAsync("qwen2.5-coder:32b", cancellationToken: cts.Token));

        _telemetryMock.Verify(t => t.RecordModelPreload(It.Is<ModelPreloadTelemetryRecord>(r =>
            !r.Success && !r.IsActive && r.Model == "qwen2.5-coder:32b")), Times.Once);
    }
}
