using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Inference;
using NightCodeForge.Infrastructure.Tests.Inference;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests;

public class VisionDiscoveryTests
{
    [Fact]
    public void OllamaShowParser_SupportsVision_DetectsCapabilitiesAndProjectorInfo()
    {
        using var capDoc = JsonDocument.Parse("""{"capabilities":["completion","vision","tools"]}""");
        using var projDoc = JsonDocument.Parse("""{"projector_info":{"clip.vision.embedding_length":1024}}""");
        using var emptyProjDoc = JsonDocument.Parse("""{"capabilities":["completion"],"projector_info":{}}""");
        using var textDoc = JsonDocument.Parse("""{"capabilities":["completion","thinking"]}""");

        Assert.True(OllamaShowParser.SupportsVision(capDoc.RootElement));
        Assert.True(OllamaShowParser.SupportsVision(projDoc.RootElement));
        Assert.False(OllamaShowParser.SupportsVision(emptyProjDoc.RootElement));
        Assert.False(OllamaShowParser.SupportsVision(textDoc.RootElement));
    }

    [Fact]
    public void OpenAiModelsResponseParser_Parse_DetectsMultipleVisionSignals()
    {
        var json = """
        {
          "data": [
            {
              "id": "gpt-4o",
              "context_length": 128000,
              "supports_vision": true
            },
            {
              "id": "openrouter/vision-model",
              "context_length": 64000,
              "architecture": { "input_modalities": ["text", "image"] }
            },
            {
              "id": "openrouter/legacy-multimodal",
              "context_length": 32768,
              "architecture": { "modality": "text+image->text" }
            },
            {
              "id": "text-only-coder",
              "context_length": 131072,
              "architecture": { "modality": "text->text" }
            }
          ]
        }
        """;

        var models = OpenAiModelsResponseParser.Parse(json);

        Assert.True(models.Single(m => m.ModelId == "gpt-4o").SupportsVision);
        Assert.True(models.Single(m => m.ModelId == "openrouter/vision-model").SupportsVision);
        Assert.True(models.Single(m => m.ModelId == "openrouter/legacy-multimodal").SupportsVision);
        Assert.False(models.Single(m => m.ModelId == "text-only-coder").SupportsVision);
    }

    [Fact]
    public void LlmContextWindowCache_CachesVisionCapabilityPerModel()
    {
        ILlmContextWindowCache cache = new LlmContextWindowCache(new MemoryCache(new MemoryCacheOptions()));

        Assert.False(cache.TryGetVisionCapability("qwen3-vl:8b", out _));
        cache.SetVisionCapability("qwen3-vl:8b", true);
        cache.SetVisionCapability("qwen2.5-coder:32b", false);

        Assert.True(cache.TryGetVisionCapability("qwen3-vl:8b", out var qwenVl));
        Assert.True(qwenVl);
        Assert.True(cache.TryGetVisionCapability("qwen2.5-coder:32b", out var coder));
        Assert.False(coder);
    }

    [Fact]
    public async Task OllamaGatewayService_ResolveVisionCapabilityAsync_ProbesShowEndpointAndCaches()
    {
        int showCalls = 0;
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/show")
            {
                showCalls++;
                return StubHttpHandler.Json("""{"capabilities":["completion","vision"],"model_info":{"qwen2vl.context_length":65536}}""");
            }
            return StubHttpHandler.Status(HttpStatusCode.NotFound);
        });

        var cache = new LlmContextWindowCache(new MemoryCache(new MemoryCacheOptions()));
        var gateway = new OllamaGatewayService(
            StubHttpHandler.Client(handler),
            Options.Create(new LlmEndpointOptions()),
            cache,
            NullLogger<OllamaGatewayService>.Instance);

        bool first = await gateway.ResolveVisionCapabilityAsync("qwen2.5-vl:7b");
        bool second = await gateway.ResolveVisionCapabilityAsync("qwen2.5-vl:7b");

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(1, showCalls);
        Assert.True(cache.TryGetVisionCapability("qwen2.5-vl:7b", out var cached) && cached);
    }

    [Fact]
    public async Task OpenAiCompatibleGatewayService_AndCatalog_RespectPrecedenceAndDiscovery()
    {
        var json = """
        {
          "data": [
            { "id": "discovered-vision", "context_length": 128000, "architecture": { "input_modalities": ["text", "image"] } },
            { "id": "allowed-vision", "context_length": 100000, "architecture": { "input_modalities": ["text"] } },
            { "id": "discovered-text", "context_length": 64000, "architecture": { "input_modalities": ["text"] } }
          ]
        }
        """;
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(json));
        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            ModelProfiles = new Dictionary<string, ModelCapabilityProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["discovered-text"] = new() { SupportsVision = true }
            },
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Endpoint = "https://api.example.test/v1",
                AllowedModels =
                [
                    new OpenAiAllowedModel { ModelId = "discovered-vision", ContextWindowTokens = 128000 },
                    new OpenAiAllowedModel { ModelId = "allowed-vision", ContextWindowTokens = 100000, SupportsVision = true }
                ]
            }
        });

        var allowlistCache = new OpenAiModelAllowlistCache();
        var catalog = new OpenAiCompatibleModelCatalogService(
            StubHttpHandler.Client(handler),
            options,
            allowlistCache,
            new OpenAiDefaultModelSelector(),
            NullLogger<OpenAiCompatibleModelCatalogService>.Instance);

        var listed = await catalog.GetAvailableModelsAsync();
        Assert.True(listed.Single(m => m.ModelId == "discovered-vision").SupportsVision);
        Assert.True(listed.Single(m => m.ModelId == "allowed-vision").SupportsVision);

        var gateway = new OpenAiCompatibleGatewayService(
            StubHttpHandler.Client(handler),
            options,
            allowlistCache,
            new OpenAiDefaultModelSelector(),
            NullLogger<OpenAiCompatibleGatewayService>.Instance);

        Assert.True(await gateway.ResolveVisionCapabilityAsync("discovered-vision"));
        Assert.True(await gateway.ResolveVisionCapabilityAsync("allowed-vision"));
        // Static ModelProfile overrides discovered text-only flag
        Assert.True(await gateway.ResolveVisionCapabilityAsync("discovered-text"));
        Assert.False(await gateway.ResolveVisionCapabilityAsync("unknown-model"));
    }

    [Fact]
    public async Task OllamaRouterGatewayService_ResolveVisionCapabilityAsync_PreservesRouterEffectiveContextWindow()
    {
        var handler = new StubHttpHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/show")
            {
                return StubHttpHandler.Json("""{"capabilities":["completion","vision"],"model_info":{"qwen2vl.context_length":32768}}""");
            }
            if (path.StartsWith("/api/router/models/", StringComparison.Ordinal))
            {
                return StubHttpHandler.Json("""{"name":"qwen3-vl:8b","nativeContextWindow":32768,"recommendedContextWindow":131072}""");
            }
            return StubHttpHandler.Status(HttpStatusCode.NotFound);
        });

        var cache = new LlmContextWindowCache(new MemoryCache(new MemoryCacheOptions()));
        var routerGateway = new OllamaRouterGatewayService(
            StubHttpHandler.Client(handler),
            Options.Create(new LlmEndpointOptions()),
            cache,
            NullLogger<OllamaRouterGatewayService>.Instance);

        bool supportsVision = await routerGateway.ResolveVisionCapabilityAsync("qwen3-vl:8b");
        int resolvedCtx = await routerGateway.ResolveContextWindowAsync("qwen3-vl:8b");

        Assert.True(supportsVision);
        Assert.Equal(131072, resolvedCtx);
    }
}
