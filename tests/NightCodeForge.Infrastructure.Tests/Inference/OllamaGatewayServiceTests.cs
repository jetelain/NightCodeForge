using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Inference;

public class OllamaGatewayServiceTests
{
    private static OllamaGatewayService CreateService(StubHttpHandler handler, ILlmContextWindowCache? cache = null) =>
        new(StubHttpHandler.Client(handler), Options.Create(new LlmEndpointOptions()), cache ?? new LlmContextWindowCache(new MemoryCache(new MemoryCacheOptions())), NullLogger<OllamaGatewayService>.Instance);

    private static HttpResponseMessage Routes(HttpRequestMessage req) => req.RequestUri!.AbsolutePath switch
    {
        "/api/version" => StubHttpHandler.Json("""{"version":"0.6.0"}"""),
        "/api/ps" => StubHttpHandler.Json("""{"models":[{"name":"qwen:32b","model":"qwen:32b","size":20,"size_vram":20}]}"""),
        "/api/generate" => StubHttpHandler.Status(HttpStatusCode.OK),
        _ => StubHttpHandler.Status(HttpStatusCode.NotFound)
    };

    [Fact]
    public async Task GetGatewayStatusAsync_ReportsSingleHostTarget()
    {
        var report = await CreateService(new StubHttpHandler(Routes)).GetGatewayStatusAsync();

        Assert.True(report.IsConnected);
        Assert.Equal(LlmProviderType.Ollama, report.ProviderType);
        var target = Assert.Single(report.FleetTargets);
        Assert.Equal("localhost:11434", target.Name);
        Assert.Equal("qwen:32b", target.Execution.ActiveModel);
        Assert.Equal(TargetBackendState.Active, target.State);
    }

    [Fact]
    public async Task GetGatewayStatusAsync_WhenOffline_ReportsOfflineHost()
    {
        var report = await CreateService(new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable))).GetGatewayStatusAsync();

        Assert.False(report.IsConnected);
        Assert.Equal(TargetBackendState.Offline, Assert.Single(report.FleetTargets).State);
    }

    [Fact]
    public async Task ReclaimVramAsync_UnloadsResidentModelsWithKeepAliveZero()
    {
        var handler = new StubHttpHandler(Routes);

        Assert.True(await CreateService(handler).ReclaimVramAsync());
        var unload = Assert.Single(handler.Requests, r => r.Path == "api/generate");
        Assert.Contains("\"keep_alive\":0", unload.Body);
        Assert.Contains("qwen:32b", unload.Body);
        Assert.DoesNotContain(handler.Requests, r => r.Path.StartsWith("api/router"));
    }

    [Fact]
    public async Task ReclaimVramAsync_WhenHostOffline_ReturnsFalse()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable));
        var result = await CreateService(handler).ReclaimVramAsync();
        Assert.False(result);
    }

    [Fact]
    public async Task DetectProviderAsync_DoesNotMistakeOllamaForRouter()
    {
        Assert.Equal(LlmProviderType.Ollama, await CreateService(new StubHttpHandler(Routes)).DetectProviderAsync());
    }

    [Fact]
    public async Task ResolveContextWindowAsync_ParsesModelInfoContextLengthFallback()
    {
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath == "/api/show"
            ? StubHttpHandler.Json("""{"model_info":{"qwen2.context_length":32768}}""")
            : StubHttpHandler.Status(HttpStatusCode.NotFound));

        var service = CreateService(handler);
        var resolved = await service.ResolveContextWindowAsync("qwen2:32b");

        Assert.Equal(32768, resolved);
    }

    [Fact]
    public async Task ResolveThinkingCapabilityAsync_ProbesShowEndpointAndCachesResult()
    {
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath == "/api/show"
            ? StubHttpHandler.Json("""{"model_info":{"llama.context_length":32768},"thinking":{"values":[false,"low","medium","xhigh"],"default":"medium"}}""")
            : StubHttpHandler.Status(HttpStatusCode.NotFound));

        var cache = new LlmContextWindowCache(new MemoryCache(new MemoryCacheOptions()));
        var service = CreateService(handler, cache);
        var capability = await service.ResolveThinkingCapabilityAsync("qwen3.8:27b");

        Assert.NotNull(capability);
        Assert.True(capability.SupportsThinking);
        Assert.Equal(["low", "medium", "xhigh"], capability.StringLevels);
        Assert.Equal("medium", capability.DefaultLevel);

        // Verify cached
        Assert.True(cache.TryGetThinkingCapability("qwen3.8:27b", out var cachedCap));
        Assert.Equal(capability, cachedCap);
        Assert.True(cache.TryGetContextWindow("qwen3.8:27b", out var cachedCtx));
        Assert.Equal(32768, cachedCtx);
    }
}
