using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Inference;

public class OllamaRouterGatewayServiceTests
{
    private const string InfoJson = """{"version":"2.1.0","isRouter":true,"fleet":{"totalTargets":4,"onlineTargets":3,"cloudEnabled":true}}""";

    private const string FleetJson = """
        {"targets":[
          {"name":"desktop","url":"http://a:11434","enabled":true,"liveness":"Online","latencyMs":12,
           "hardware":{"hardwareHint":"RTX 4090","gpuVendor":"Nvidia","devices":["GPU0"],"maxVramMB":24576,"maxParallel":2,"speedFactor":1.0},
           "execution":{"inFlightRequests":0,"activeModel":"m1","contextAllocation":null}},
          {"name":"laptop","url":"http://b:11434","enabled":true,"liveness":"Online","latencyMs":30,
           "hardware":{"hardwareHint":null,"gpuVendor":"Apple","devices":[],"maxVramMB":null,"maxParallel":1,"speedFactor":null},
           "execution":{"inFlightRequests":2,"activeModel":"m2","contextAllocation":"64k"}},
          {"name":"nas","url":"http://c:11434","enabled":false,"liveness":"Online","latencyMs":null,
           "hardware":{"hardwareHint":null,"gpuVendor":"Unknown","devices":[],"maxVramMB":null,"maxParallel":null,"speedFactor":null},
           "execution":{"inFlightRequests":0,"activeModel":null,"contextAllocation":null}},
          {"name":"cloud","url":null,"enabled":true,"liveness":"Offline","latencyMs":null,
           "hardware":{"hardwareHint":null,"gpuVendor":"Unknown","devices":[],"maxVramMB":null,"maxParallel":null,"speedFactor":null},
           "execution":{"inFlightRequests":0,"activeModel":null,"contextAllocation":null}},
          {"name":"gpu-box","url":"http://d:11434","enabled":true,"liveness":"Probing","latencyMs":null,
           "hardware":{"hardwareHint":null,"gpuVendor":"Amd","devices":[],"maxVramMB":16384,"maxParallel":1,"speedFactor":null},
           "execution":{"inFlightRequests":0,"activeModel":null,"contextAllocation":null}}
        ]}
        """;

    private static OllamaRouterGatewayService CreateService(StubHttpHandler handler, ILlmContextWindowCache? cache = null) =>
        new(StubHttpHandler.Client(handler), Options.Create(new LlmEndpointOptions()), cache ?? new LlmContextWindowCache(new MemoryCache(new MemoryCacheOptions())), NullLogger<OllamaRouterGatewayService>.Instance);

    private static HttpResponseMessage RouterRoutes(HttpRequestMessage req) => req.RequestUri!.AbsolutePath switch
    {
        "/api/router/info" => StubHttpHandler.Json(InfoJson),
        "/api/router/fleet" => StubHttpHandler.Json(FleetJson),
        _ => StubHttpHandler.Status(HttpStatusCode.NotFound)
    };

    [Fact]
    public async Task GetGatewayStatusAsync_MapsEveryFleetTargetDynamically()
    {
        var report = await CreateService(new StubHttpHandler(RouterRoutes)).GetGatewayStatusAsync();

        Assert.True(report.IsConnected);
        Assert.Equal(LlmProviderType.OllamaRouter, report.ProviderType);
        Assert.Equal("2.1.0", report.Version);
        Assert.Equal(5, report.FleetTargets.Count);
        Assert.Equal(["desktop", "laptop", "nas", "cloud", "gpu-box"], report.FleetTargets.Select(t => t.Name));
        Assert.Equal(24576, report.FleetTargets[0].Hardware.MaxVramMB);
        Assert.Equal("RTX 4090", report.FleetTargets[0].Hardware.HardwareHint);
    }

    [Fact]
    public async Task GetGatewayStatusAsync_DerivesStateFromLivenessEnabledAndInFlight()
    {
        var targets = (await CreateService(new StubHttpHandler(RouterRoutes)).GetGatewayStatusAsync()).FleetTargets;

        Assert.Equal(TargetBackendState.Active, targets.Single(t => t.Name == "desktop").State);
        Assert.Equal(TargetBackendState.Busy, targets.Single(t => t.Name == "laptop").State);
        Assert.Equal(TargetBackendState.Disabled, targets.Single(t => t.Name == "nas").State);
        Assert.Equal(TargetBackendState.Offline, targets.Single(t => t.Name == "cloud").State);
        Assert.Equal(TargetBackendState.Available, targets.Single(t => t.Name == "gpu-box").State);
    }

    [Fact]
    public async Task GetGatewayStatusAsync_WhenRouterUnreachable_IsDisconnectedWithNoTargets()
    {
        var report = await CreateService(new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable))).GetGatewayStatusAsync();

        Assert.False(report.IsConnected);
        Assert.Empty(report.FleetTargets);
    }

    [Fact]
    public async Task GetGatewayStatusAsync_WhenFleetShapeIsInvalid_IsDisconnected()
    {
        var report = await CreateService(new StubHttpHandler(req => req.RequestUri!.AbsolutePath == "/api/router/info"
            ? StubHttpHandler.Json(InfoJson)
            : StubHttpHandler.Json("""{"targets":[{"unexpected":1}]}"""))).GetGatewayStatusAsync();

        Assert.False(report.IsConnected);
        Assert.Empty(report.FleetTargets);
    }

    [Fact]
    public async Task DetectProviderAsync_WhenRouterInfoAnswers_ReturnsOllamaRouter()
    {
        var detected = await CreateService(new StubHttpHandler(RouterRoutes)).DetectProviderAsync();
        Assert.Equal(LlmProviderType.OllamaRouter, detected);
    }

    [Fact]
    public async Task DetectProviderAsync_WhenOnlyOllamaVersionAnswers_ReturnsOllama()
    {
        var detected = await CreateService(new StubHttpHandler(req => req.RequestUri!.AbsolutePath == "/api/version"
            ? StubHttpHandler.Json("""{"version":"0.6.0"}""")
            : StubHttpHandler.Status(HttpStatusCode.NotFound))).DetectProviderAsync();
        Assert.Equal(LlmProviderType.Ollama, detected);
    }

    [Fact]
    public async Task DetectProviderAsync_WhenNothingAnswers_ReturnsNull()
    {
        var detected = await CreateService(new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.NotFound))).DetectProviderAsync();
        Assert.Null(detected);
    }

    [Fact]
    public async Task ResolveContextWindowAsync_UsesRouterModelMetadata()
    {
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath.StartsWith("/api/router/models/")
            ? StubHttpHandler.Json("""{"name":"m1","recommendedContextWindow":65536,"nativeContextWindow":131072}""")
            : StubHttpHandler.Status(HttpStatusCode.NotFound));

        Assert.Equal(65536, await CreateService(handler).ResolveContextWindowAsync("m1"));
    }

    [Fact]
    public async Task ResolveContextWindowAsync_MultiNodeCeiling_ReturnsLargestCapableTargetContextCeiling()
    {
        var json = """
        {
          "name": "qwen3.8-27b",
          "architecture": "Dense",
          "parameterCount": 27800000000,
          "nativeContextWindow": 32768,
          "recommendedContextWindow": 262144,
          "targets": [
            { "target": "desktop", "capable": true, "contextCeiling": 49152, "isResident": true },
            { "target": "laptop", "capable": false, "contextCeiling": 4096, "vetoReason": "LargeDense (>= 14B) models are not supported on targets with < 12 GB VRAM" },
            { "target": "server1", "capable": true, "contextCeiling": 262144, "isResident": false }
          ]
        }
        """;

        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath.StartsWith("/api/router/models/")
            ? StubHttpHandler.Json(json)
            : StubHttpHandler.Status(HttpStatusCode.NotFound));

        var service = CreateService(handler);
        var resolved = await service.ResolveContextWindowAsync("qwen3.8-27b");

        Assert.Equal(262144, resolved);
    }

    [Fact]
    public async Task ResolveContextWindowAsync_WhenRouterModelUnknown_FallsBackToApiShow()
    {
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath == "/api/show"
            ? StubHttpHandler.Json("""{"parameters":"num_ctx 32768"}""")
            : StubHttpHandler.Status(HttpStatusCode.NotFound));

        Assert.Equal(32768, await CreateService(handler).ResolveContextWindowAsync("m1"));
    }

    [Fact]
    public async Task ResolveContextWindowAsync_WhenNothingKnown_UsesOllamaDefaultWithoutCaching()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.NotFound));
        var service = CreateService(handler);

        Assert.Equal(OllamaGatewayServiceBase.OllamaDefaultContextTokens, await service.ResolveContextWindowAsync("m1"));
        var before = handler.Requests.Count;
        await service.ResolveContextWindowAsync("m1");
        Assert.True(handler.Requests.Count > before);
    }

    [Fact]
    public async Task ReclaimVramAsync_PostsTargetAndReturnsRouterSuccess()
    {
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath == "/api/router/vram/reclaim"
            ? StubHttpHandler.Json("""{"success":true,"totalUnloaded":2,"targets":[]}""")
            : StubHttpHandler.Status(HttpStatusCode.NotFound));

        Assert.True(await CreateService(handler).ReclaimVramAsync("desktop"));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("\"target\":\"desktop\"", request.Body);
    }

    [Fact]
    public async Task ReclaimVramAsync_WhenRouterReportsFailure_ReturnsFalse()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("""{"success":false,"totalUnloaded":0,"targets":[]}"""));
        Assert.False(await CreateService(handler).ReclaimVramAsync());
    }

    [Fact]
    public async Task ReclaimVramAsync_WhenHttpError_ReturnsFalse()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.InternalServerError));
        Assert.False(await CreateService(handler).ReclaimVramAsync());
    }

    [Fact]
    public async Task GetGatewayStatusAsync_WhenFleetPayloadHasNullTargets_ReturnsReportWithEmptyFleet()
    {
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/router/info" => StubHttpHandler.Json(InfoJson),
            "/api/router/fleet" => StubHttpHandler.Json("""{"targets":null}"""),
            _ => StubHttpHandler.Status(HttpStatusCode.NotFound)
        });

        var report = await CreateService(handler).GetGatewayStatusAsync();
        Assert.True(report.IsConnected);
        Assert.Empty(report.FleetTargets);
    }

    [Fact]
    public async Task GetGatewayStatusAsync_WhenTargetHasNullHardwareAndExecution_MapsTargetWithDefaultsWithoutThrowing()
    {
        var fleetWithNulls = """
            {"targets":[
              {"name":"sparse-node","url":"http://sparse:11434","enabled":true,"liveness":"Online","latencyMs":5,
               "hardware":null,
               "execution":null}
            ]}
            """;
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/router/info" => StubHttpHandler.Json(InfoJson),
            "/api/router/fleet" => StubHttpHandler.Json(fleetWithNulls),
            _ => StubHttpHandler.Status(HttpStatusCode.NotFound)
        });

        var report = await CreateService(handler).GetGatewayStatusAsync();
        Assert.True(report.IsConnected);
        var target = Assert.Single(report.FleetTargets);
        Assert.Equal("sparse-node", target.Name);
        Assert.Equal(TargetBackendState.Active, target.State);
        Assert.NotNull(target.Hardware);
        Assert.NotNull(target.Execution);
        Assert.Equal("Unknown", target.Hardware.GpuVendor);
        Assert.Equal(0, target.Execution.InFlightRequests);
    }

    [Fact]
    public async Task GetGatewayStatusAsync_WhenEndpointsReturnMalformedJson_ReturnsDisconnectedStatusReport()
    {
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/router/info" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!DOCTYPE html><html><body>Error</body></html>") },
            "/api/router/fleet" => StubHttpHandler.Json("Malformed JSON { ["),
            _ => StubHttpHandler.Status(HttpStatusCode.NotFound)
        });

        var report = await CreateService(handler).GetGatewayStatusAsync();
        Assert.False(report.IsConnected);
        Assert.Empty(report.FleetTargets);
    }

    [Fact]
    public async Task ResolveContextWindowAsync_WhenDiscovered_CachesInContextWindowCache()
    {
        var cache = new LlmContextWindowCache(new MemoryCache(new MemoryCacheOptions()));
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/router/models/m1" => StubHttpHandler.Json("""{"name":"m1","recommendedContextWindow":65536}"""),
            _ => StubHttpHandler.Status(HttpStatusCode.NotFound)
        });
        var service = CreateService(handler, cache);

        var first = await service.ResolveContextWindowAsync("m1");
        Assert.Equal(65536, first);
        Assert.True(cache.TryGetContextWindow("m1", out var cachedTokens));
        Assert.Equal(65536, cachedTokens);

        var countBefore = handler.Requests.Count;
        var second = await service.ResolveContextWindowAsync("m1");
        Assert.Equal(65536, second);
        Assert.Equal(countBefore, handler.Requests.Count);
    }

    [Fact]
    public async Task GetGatewayStatusAsync_WhenTargetOmitsHardwareAndExecution_MapsWithoutThrowing()
    {
        var fleetOmittingProperties = """
            {"targets":[
              {"name":"minimal-node","url":"http://minimal:11434","enabled":true,"liveness":"Online"}
            ]}
            """;
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/router/info" => StubHttpHandler.Json(InfoJson),
            "/api/router/fleet" => StubHttpHandler.Json(fleetOmittingProperties),
            _ => StubHttpHandler.Status(HttpStatusCode.NotFound)
        });

        var report = await CreateService(handler).GetGatewayStatusAsync();
        Assert.True(report.IsConnected);
        var target = Assert.Single(report.FleetTargets);
        Assert.Equal("minimal-node", target.Name);
        Assert.Equal(TargetBackendState.Active, target.State);
        Assert.Equal("Unknown", target.Hardware.GpuVendor);
        Assert.Equal(0, target.Execution.InFlightRequests);
    }
}
