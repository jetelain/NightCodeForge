using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
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

    [Fact]
    public async Task ResolveThinkingCapabilityAsync_PreservesRouterRecommendedContextWindowOverApiShow()
    {
        var cache = new LlmContextWindowCache(new MemoryCache(new MemoryCacheOptions()));
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.StartsWith("/api/router/models/") => StubHttpHandler.Json("""{"name":"qwen3.8:27b","nativeContextWindow":32768,"recommendedContextWindow":262144}"""),
            "/api/show" => StubHttpHandler.Json("""{"model_info":{"qwen2.context_length":32768},"thinking":{"values":[false,"low","medium","xhigh"],"default":"medium"}}"""),
            _ => StubHttpHandler.Status(HttpStatusCode.NotFound)
        });
        var service = CreateService(handler, cache);

        // Cold-start order in HarnessAgentFactory: ResolveThinkingCapabilityAsync is called before ResolveContextWindowAsync
        var capability = await service.ResolveThinkingCapabilityAsync("qwen3.8:27b");
        var contextWindow = await service.ResolveContextWindowAsync("qwen3.8:27b");

        Assert.NotNull(capability);
        Assert.Equal(["low", "medium", "xhigh"], capability.StringLevels);
        Assert.Equal(262144, contextWindow);
        Assert.True(cache.TryGetContextWindow("qwen3.8:27b", out var cachedCtx));
        Assert.Equal(262144, cachedCtx);
    }

    [Fact]
    public async Task ReleaseSessionAffinityAsync_SendsPostWithHeaderAndParsesResult()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpHandler(req =>
        {
            capturedRequest = req;
            if (req.RequestUri!.AbsolutePath == "/api/router/sessions/affinity/release" && req.Method == HttpMethod.Post)
            {
                return StubHttpHandler.Json("""{"sessionId":"ncf-task42-stage-3","released":true}""");
            }
            return StubHttpHandler.Status(HttpStatusCode.BadRequest);
        });

        var service = CreateService(handler);
        var released = await service.ReleaseSessionAffinityAsync("ncf-task42-stage-3");

        Assert.True(released);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal("/api/router/sessions/affinity/release", capturedRequest.RequestUri!.AbsolutePath);
        Assert.True(capturedRequest.Headers.Contains("X-OllamaRouter-Session-Id"));
        Assert.Equal("ncf-task42-stage-3", capturedRequest.Headers.GetValues("X-OllamaRouter-Session-Id").Single());
    }

    [Fact]
    public async Task ReleaseSessionAffinityAsync_WhenFalseOrError_ReturnsFalseGracefully()
    {
        var falseHandler = new StubHttpHandler(req => StubHttpHandler.Json("""{"sessionId":"ncf-task42-stage-3","released":false}"""));
        var serviceFalse = CreateService(falseHandler);
        Assert.False(await serviceFalse.ReleaseSessionAffinityAsync("ncf-task42-stage-3"));

        var errorHandler = new StubHttpHandler(req => StubHttpHandler.Status(HttpStatusCode.InternalServerError));
        var serviceError = CreateService(errorHandler);
        Assert.False(await serviceError.ReleaseSessionAffinityAsync("ncf-task42-stage-3"));

        Assert.False(await serviceFalse.ReleaseSessionAffinityAsync("   "));
        Assert.False(await serviceFalse.ReleaseSessionAffinityAsync(null!));
    }

    [Fact]
    public async Task GetSessionUsageAsync_WhenFound_ParsesFullUsageAndPerTarget()
    {
        const string SessionJson = """
            {
              "currency": "$",
              "sessionId": "ncf-task42-stage-3",
              "requests": 8,
              "inputTokens": 34500,
              "cachedInputTokens": 26000,
              "outputTokens": 4120,
              "elapsedMs": 28430.2,
              "lastSeenAt": "2026-11-09T20:45:12.345Z",
              "cloudValue": 0.0890,
              "electricityCost": 0.0052,
              "savings": 0.0838,
              "actualCloudCost": 0.0000,
              "cost": 0.0052,
              "perTarget": [
                {
                  "target": "aiserver1",
                  "requests": 8,
                  "inputTokens": 34500,
                  "cachedInputTokens": 26000,
                  "outputTokens": 4120,
                  "elapsedMs": 28430.2,
                  "cloudValue": 0.0890,
                  "electricityCost": 0.0052,
                  "savings": 0.0838
                }
              ]
            }
            """;

        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpHandler(req =>
        {
            capturedRequest = req;
            if (req.RequestUri!.AbsolutePath == "/api/router/monitor/sessions/ncf-task42-stage-3")
            {
                return StubHttpHandler.Json(SessionJson);
            }
            return StubHttpHandler.Status(HttpStatusCode.NotFound);
        });

        var service = CreateService(handler);
        var usage = await service.GetSessionUsageAsync("ncf-task42-stage-3");

        Assert.NotNull(usage);
        Assert.Equal("ncf-task42-stage-3", usage.SessionId);
        Assert.Equal(8, usage.Requests);
        Assert.Equal(34500, usage.InputTokens);
        Assert.Equal(26000, usage.CachedInputTokens);
        Assert.Equal(4120, usage.OutputTokens);
        Assert.Equal(0.0052, usage.Cost);
        Assert.Equal(0.0052, usage.ElectricityCost);
        Assert.Equal(0.0, usage.ActualCloudCost);
        Assert.Equal(0.0838, usage.Savings);
        Assert.Equal("$", usage.Currency);
        Assert.NotNull(usage.PerTarget);
        Assert.Single(usage.PerTarget);
        Assert.Equal("aiserver1", usage.PerTarget[0].Target);
    }

    [Fact]
    public async Task GetSessionUsageAsync_When404OrEmpty_ReturnsNull()
    {
        var handler = new StubHttpHandler(req => StubHttpHandler.Status(HttpStatusCode.NotFound));
        var service = CreateService(handler);

        var usage = await service.GetSessionUsageAsync("unknown-session");
        Assert.Null(usage);

        Assert.Null(await service.GetSessionUsageAsync("  "));
        Assert.Null(await service.GetSessionUsageAsync(null!));

        var errorService = CreateService(new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.InternalServerError)));
        Assert.Null(await errorService.GetSessionUsageAsync("ncf-task42-stage-3"));
    }

    [Fact]
    public async Task GetRecommendationsAsync_ParsesFullFiveRoleMatrixAndNodeProfiles()
    {
        const string RecommendationsJson = """
            {
              "generatedAt": "2026-11-11T10:30:00.0000000+00:00",
              "roles": {
                "basic": {
                  "role": "basic",
                  "model": "gemma4:e4b",
                  "recommendedContextWindow": 131072,
                  "description": "Fast auxiliary tasks",
                  "nodes": [
                    { "target": "Local", "contextCeiling": 131072, "baselineContext": null, "tokensPerSecond": 68.4, "vramMB": 16384 }
                  ]
                },
                "agentic_chat": {
                  "role": "agentic_chat",
                  "model": "qwen3.8:27b",
                  "recommendedContextWindow": 262144,
                  "description": "Interactive ideation and chat",
                  "nodes": [
                    { "target": "Local", "contextCeiling": 49152, "baselineContext": null, "tokensPerSecond": 24.1, "vramMB": 16384 },
                    { "target": "Server", "contextCeiling": 262144, "baselineContext": null, "tokensPerSecond": 42.8, "vramMB": 98304 }
                  ]
                },
                "software_architect": {
                  "role": "software_architect",
                  "model": "qwen3.8:27b",
                  "recommendedContextWindow": 262144,
                  "description": "Architecture, task review, and code review",
                  "nodes": [
                    { "target": "Local", "contextCeiling": 49152, "baselineContext": null, "tokensPerSecond": 24.1, "vramMB": 16384 },
                    { "target": "Server", "contextCeiling": 262144, "baselineContext": null, "tokensPerSecond": 42.8, "vramMB": 98304 }
                  ]
                },
                "senior_developer": {
                  "role": "senior_developer",
                  "model": "qwen3.8:27b",
                  "recommendedContextWindow": 262144,
                  "description": "Complex code implementation",
                  "nodes": [
                    { "target": "Local", "contextCeiling": 49152, "baselineContext": null, "tokensPerSecond": 24.1, "vramMB": 16384 },
                    { "target": "Server", "contextCeiling": 262144, "baselineContext": null, "tokensPerSecond": 42.8, "vramMB": 98304 }
                  ]
                },
                "junior_developer": {
                  "role": "junior_developer",
                  "model": "qwen3.6:35b-a3b",
                  "recommendedContextWindow": 131072,
                  "description": "High-speed MoE implementation",
                  "nodes": [
                    { "target": "Local", "contextCeiling": 131072, "baselineContext": 65536, "tokensPerSecond": 54.0, "vramMB": 16384 }
                  ]
                }
              }
            }
            """;

        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath == "/api/router/recommendations"
            ? StubHttpHandler.Json(RecommendationsJson)
            : StubHttpHandler.Status(HttpStatusCode.NotFound));

        var result = await CreateService(handler).GetRecommendationsAsync();

        Assert.NotNull(result);
        Assert.NotNull(result.GeneratedAt);
        Assert.NotNull(result.Roles);
        Assert.Equal(5, result.Roles.Count);
        var junior = result.Roles[OllamaRouterRoleNames.JuniorDeveloper];
        Assert.Equal("qwen3.6:35b-a3b", junior.Model);
        Assert.Equal(131072, junior.RecommendedContextWindow);
        Assert.NotNull(junior.Nodes);
        var node = Assert.Single(junior.Nodes);
        Assert.Equal(65536, node.BaselineContext);
        Assert.Equal(54.0, node.TokensPerSecond);
        Assert.Equal(16384, node.VramMB);
    }

    [Fact]
    public async Task GetRecommendationsAsync_HandlesEmptyMatrix404ErrorAndMalformedJson()
    {
        var emptyService = CreateService(new StubHttpHandler(req => req.RequestUri!.AbsolutePath == "/api/router/recommendations"
            ? StubHttpHandler.Json("""{"generatedAt":null,"roles":{}}""")
            : StubHttpHandler.Status(HttpStatusCode.NotFound)));
        var emptyResult = await emptyService.GetRecommendationsAsync();
        Assert.NotNull(emptyResult);
        Assert.Null(emptyResult.GeneratedAt);
        Assert.NotNull(emptyResult.Roles);
        Assert.Empty(emptyResult.Roles);

        Assert.Null(await CreateService(new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.NotFound))).GetRecommendationsAsync());
        Assert.Null(await CreateService(new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.InternalServerError))).GetRecommendationsAsync());
        Assert.Null(await CreateService(new StubHttpHandler(_ => StubHttpHandler.Json("{ invalid json"))).GetRecommendationsAsync());
    }

    [Fact]
    public void RecommendationMapper_MapsPriorityFallbacksFloorsAndAppliesOptions()
    {
        var fullDto = new RouterRecommendationsDto(
            DateTimeOffset.UtcNow,
            new Dictionary<string, RouterRoleRecommendationDto>
            {
                ["BASIC"] = new("basic", "gemma4:e4b", 131072, null, []),
                ["Agentic_Chat"] = new("agentic_chat", " chat-model ", 131072, null, []),
                ["SOFTWARE_ARCHITECT"] = new("software_architect", " architect-model ", 262144, null, []),
                ["senior_developer"] = new("senior_developer", " senior-model ", 262144, null, []),
                ["junior_developer"] = new("junior_developer", " junior-model ", 131072, null, [])
            });

        var mappedFull = OllamaRouterRecommendationMapper.Map(fullDto);
        Assert.True(mappedFull.HasRecommendations);
        Assert.True(mappedFull.ShouldDifferentiateRoles);
        Assert.Equal("architect-model", mappedFull.ArchitectModelId);
        Assert.Equal("senior-model", mappedFull.CoderModelId);
        Assert.Equal("chat-model", mappedFull.DefaultModelId);
        Assert.Equal(262144, mappedFull.ContextWindowTokens);

        var options = new LlmProviderOptions { ProviderType = LlmProviderType.Ollama, Endpoint = "" };
        OllamaRouterRecommendationMapper.ApplyToOptions(options, mappedFull, " http://router.lan:11434 ");
        Assert.Equal(LlmProviderType.OllamaRouter, options.ProviderType);
        Assert.Equal("http://router.lan:11434", options.Endpoint);
        Assert.Equal("architect-model", options.ArchitectModelId);
        Assert.Equal("senior-model", options.CoderModelId);
        Assert.Equal("chat-model", options.DefaultModelId);
        Assert.Equal(262144, options.ContextWindowTokens);

        // Fallback chain: no software_architect, no senior_developer, no agentic_chat -> junior_developer becomes Coder, Architect=null, Default=junior_developer
        var juniorOnlyDto = new RouterRecommendationsDto(
            DateTimeOffset.UtcNow,
            new Dictionary<string, RouterRoleRecommendationDto>
            {
                ["basic"] = new("basic", "gemma4:e4b", 131072, null, []),
                ["junior_developer"] = new("junior_developer", "qwen3.6:35b-a3b", 65536, null, [])
            });

        var mappedJuniorOnly = OllamaRouterRecommendationMapper.Map(juniorOnlyDto);
        Assert.Null(mappedJuniorOnly.ArchitectModelId);
        Assert.Equal("qwen3.6:35b-a3b", mappedJuniorOnly.CoderModelId);
        Assert.Equal("qwen3.6:35b-a3b", mappedJuniorOnly.DefaultModelId);
        Assert.Equal(LlmProviderOptions.DefaultContextWindowTokens, mappedJuniorOnly.ContextWindowTokens);

        // Context floor: software_architect < 131072 floors to 131072; missing senior_developer uses junior_developer; missing software_architect uses agentic_chat
        var smallArchitectDto = new RouterRecommendationsDto(
            DateTimeOffset.UtcNow,
            new Dictionary<string, RouterRoleRecommendationDto>
            {
                ["software_architect"] = new("software_architect", "arch-64k", 65536, null, [])
            });
        var mappedSmallArch = OllamaRouterRecommendationMapper.Map(smallArchitectDto);
        Assert.Equal("arch-64k", mappedSmallArch.ArchitectModelId);
        Assert.Equal("arch-64k", mappedSmallArch.DefaultModelId);
        Assert.Null(mappedSmallArch.CoderModelId);
        Assert.Equal(131072, mappedSmallArch.ContextWindowTokens);

        // Basic-only / empty matrix has no usable recommendations and ApplyToOptions fails fast
        var basicOnlyDto = new RouterRecommendationsDto(
            null,
            new Dictionary<string, RouterRoleRecommendationDto>
            {
                ["basic"] = new("basic", "gemma4:e4b", 131072, null, [])
            });
        var mappedBasicOnly = OllamaRouterRecommendationMapper.Map(basicOnlyDto);
        Assert.False(mappedBasicOnly.HasRecommendations);
        Assert.Throws<InvalidOperationException>(() => OllamaRouterRecommendationMapper.ApplyToOptions(new LlmProviderOptions(), mappedBasicOnly));
    }

    [Fact]
    public async Task GetRecommendationsAsync_PropagatesCallerCancellation_AndReturnsNullOnInternalTimeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var okService = CreateService(new StubHttpHandler(_ => StubHttpHandler.Json("""{"generatedAt":null,"roles":{}}""")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => okService.GetRecommendationsAsync(cts.Token));

        var timeoutService = CreateService(new StubHttpHandler(_ => throw new TaskCanceledException("Simulated HTTP client timeout")));
        Assert.Null(await timeoutService.GetRecommendationsAsync(CancellationToken.None));
    }

    [Fact]
    public void RecommendationMapper_BlankSoftwareArchitectIgnoresContextFloor_AndNormalizeEndpointValidatesAndTrims()
    {
        var dto = new RouterRecommendationsDto(
            DateTimeOffset.UtcNow,
            new Dictionary<string, RouterRoleRecommendationDto>
            {
                [OllamaRouterRoleNames.SoftwareArchitect] = new("software_architect", "   ", 262144, null, []),
                [OllamaRouterRoleNames.AgenticChat] = new("agentic_chat", "chat-model", 131072, null, [])
            });

        var mapped = OllamaRouterRecommendationMapper.Map(dto);
        Assert.Equal("chat-model", mapped.ArchitectModelId);
        Assert.Equal(LlmProviderOptions.DefaultContextWindowTokens, mapped.ContextWindowTokens);

        Assert.Equal(LlmProviderOptions.DefaultEndpoint, OllamaRouterRecommendationMapper.NormalizeEndpoint(null));
        Assert.Equal(LlmProviderOptions.DefaultEndpoint, OllamaRouterRecommendationMapper.NormalizeEndpoint("   "));
        Assert.Equal("http://router.lan:11434", OllamaRouterRecommendationMapper.NormalizeEndpoint(" http://router.lan:11434/ "));

        Assert.Throws<ArgumentException>(() => OllamaRouterRecommendationMapper.NormalizeEndpoint("ftp://router.lan"));
        Assert.Throws<ArgumentException>(() => OllamaRouterRecommendationMapper.NormalizeEndpoint("not-a-url"));
        Assert.Throws<ArgumentException>(() => OllamaRouterRecommendationMapper.NormalizeEndpoint("http://router.lan?x=1"));
        Assert.Throws<ArgumentException>(() => OllamaRouterRecommendationMapper.NormalizeEndpoint("http://router.lan#frag"));
        Assert.Throws<ArgumentException>(() => OllamaRouterRecommendationMapper.NormalizeEndpoint("http:///path"));

        var manualMapped = new MappedRouterRecommendations(
            DefaultModelId: " default-m ",
            ArchitectModelId: " arch-m ",
            CoderModelId: " coder-m ",
            ContextWindowTokens: 0,
            GeneratedAt: null);
        var options = new LlmProviderOptions();
        OllamaRouterRecommendationMapper.ApplyToOptions(options, manualMapped, "https://router.local:8443/");
        Assert.Equal("https://router.local:8443", options.Endpoint);
        Assert.Equal("default-m", options.DefaultModelId);
        Assert.Equal("arch-m", options.ArchitectModelId);
        Assert.Equal("coder-m", options.CoderModelId);
        Assert.Equal(LlmProviderOptions.DefaultContextWindowTokens, options.ContextWindowTokens);

        Assert.Throws<ArgumentException>(() => OllamaRouterRecommendationMapper.ApplyToOptions(new LlmProviderOptions(), manualMapped, "not-a-url"));
    }

    [Fact]
    public void ConfigurationBinding_DefaultModelIdWinsOverLegacyModelId_WhileLegacyOnlyStillBinds()
    {
        const string baseJson = """{"LlmProvider":{"ModelId":"Qwen3.8-27B:latest"}}""";
        const string overlayJson = """{"LlmProvider":{"DefaultModelId":"qwen3.8:27b"}}""";

        var overlaidConfig = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(baseJson)))
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(overlayJson)))
            .Build();

        var providerOpts = overlaidConfig.GetSection(LlmProviderOptions.SectionName).Get<LlmProviderOptions>();
        var endpointOpts = overlaidConfig.GetSection(LlmProviderOptions.SectionName).Get<LlmEndpointOptions>();
        Assert.NotNull(providerOpts);
        Assert.NotNull(endpointOpts);
        Assert.Equal("qwen3.8:27b", providerOpts.DefaultModelId);
        Assert.Equal("qwen3.8:27b", providerOpts.ModelId);
        Assert.Equal("qwen3.8:27b", endpointOpts.DefaultModelId);
        Assert.Equal("qwen3.8:27b", endpointOpts.ModelId);

        var legacyOnlyConfig = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(baseJson)))
            .Build();

        var legacyProviderOpts = legacyOnlyConfig.GetSection(LlmProviderOptions.SectionName).Get<LlmProviderOptions>();
        var legacyEndpointOpts = legacyOnlyConfig.GetSection(LlmProviderOptions.SectionName).Get<LlmEndpointOptions>();
        Assert.NotNull(legacyProviderOpts);
        Assert.NotNull(legacyEndpointOpts);
        Assert.Equal("Qwen3.8-27B:latest", legacyProviderOpts.DefaultModelId);
        Assert.Equal("Qwen3.8-27B:latest", legacyProviderOpts.ModelId);
        Assert.Equal("Qwen3.8-27B:latest", legacyEndpointOpts.DefaultModelId);
        Assert.Equal("Qwen3.8-27B:latest", legacyEndpointOpts.ModelId);

        const string explicitEmptyOverlayJson = """{"LlmProvider":{"DefaultModelId":""}}""";
        var explicitEmptyConfig = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(baseJson)))
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(explicitEmptyOverlayJson)))
            .Build();

        var emptyDefaultProviderOpts = explicitEmptyConfig.GetSection(LlmProviderOptions.SectionName).Get<LlmProviderOptions>();
        var emptyDefaultEndpointOpts = explicitEmptyConfig.GetSection(LlmProviderOptions.SectionName).Get<LlmEndpointOptions>();
        Assert.Equal(string.Empty, emptyDefaultProviderOpts!.DefaultModelId);
        Assert.Equal(string.Empty, emptyDefaultEndpointOpts!.DefaultModelId);
    }
}
