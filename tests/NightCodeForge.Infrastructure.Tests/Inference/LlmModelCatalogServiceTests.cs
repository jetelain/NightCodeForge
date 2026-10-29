using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Inference;

public class LlmModelCatalogServiceTests
{
    private const string TagsJson = """{"models":[{"name":"a:1","size":1073741824},{"name":"b:2","size":2147483648}]}""";

    private static HttpResponseMessage OllamaRoutes(HttpRequestMessage req) => req.RequestUri!.AbsolutePath switch
    {
        "/api/tags" => StubHttpHandler.Json(TagsJson),
        "/api/show" => StubHttpHandler.Json("""{"model_info":{"llama.context_length":8192},"capabilities":["completion","thinking"]}"""),
        _ => StubHttpHandler.Status(HttpStatusCode.NotFound)
    };

    [Fact]
    public async Task Ollama_EnrichesModelsFromApiShow()
    {
        var service = new OllamaModelCatalogService(
            StubHttpHandler.Client(new StubHttpHandler(OllamaRoutes)), Options.Create(new LlmEndpointOptions()), NullLogger<OllamaModelCatalogService>.Instance);

        var models = await service.GetAvailableModelsAsync();

        Assert.Equal(2, models.Count);
        Assert.All(models, m =>
        {
            Assert.Equal(LlmProviderType.Ollama, m.ProviderType);
            Assert.Equal(8192, m.ContextWindowTokens);
            Assert.True(m.SupportsReasoning);
        });
    }

    [Fact]
    public async Task Ollama_ModelWithoutThinkingCapability_IsNotReasoning()
    {
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath == "/api/show"
            ? StubHttpHandler.Json("""{"capabilities":["completion"],"parameters":"num_ctx 4096"}""")
            : OllamaRoutes(req));
        var service = new OllamaModelCatalogService(
            StubHttpHandler.Client(handler), Options.Create(new LlmEndpointOptions()), NullLogger<OllamaModelCatalogService>.Instance);

        var model = (await service.GetAvailableModelsAsync()).First();

        Assert.False(model.SupportsReasoning);
        Assert.Equal(4096, model.ContextWindowTokens);
    }

    [Fact]
    public async Task Router_PrefersRouterRecommendedContextWindow()
    {
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath == "/api/router/models"
            ? StubHttpHandler.Json("""{"models":[{"name":"a:1","nativeContextWindow":131072,"recommendedContextWindow":65536},{"name":"b:2","nativeContextWindow":32768,"recommendedContextWindow":0}]}""")
            : OllamaRoutes(req));
        var service = new OllamaRouterModelCatalogService(
            StubHttpHandler.Client(handler), Options.Create(new LlmEndpointOptions()), NullLogger<OllamaRouterModelCatalogService>.Instance);

        var models = await service.GetAvailableModelsAsync();

        Assert.Equal(65536, models.Single(m => m.ModelId == "a:1").ContextWindowTokens);
        Assert.Equal(32768, models.Single(m => m.ModelId == "b:2").ContextWindowTokens);
        Assert.All(models, m => Assert.Equal(LlmProviderType.OllamaRouter, m.ProviderType));
    }

    [Fact]
    public async Task Router_ParsesRouterModelsResponse_WithArchitectureAndParameterCount()
    {
        var json = """
        {
          "models": [
            {
              "name": "Qwen3.8-27B:latest",
              "architecture": "Dense",
              "parameterCount": 27800000000,
              "nativeContextWindow": 32768,
              "recommendedContextWindow": 262144,
              "cloudOverflowAvailable": true
            }
          ]
        }
        """;
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath == "/api/router/models"
            ? StubHttpHandler.Json(json)
            : StubHttpHandler.Status(HttpStatusCode.NotFound));

        var service = new OllamaRouterModelCatalogService(
            StubHttpHandler.Client(handler), Options.Create(new LlmEndpointOptions()), NullLogger<OllamaRouterModelCatalogService>.Instance);

        var models = await service.GetAvailableModelsAsync();

        var model = Assert.Single(models);
        Assert.Equal("Qwen3.8-27B:latest", model.ModelId);
        Assert.Equal("Qwen3.8-27B:latest", model.DisplayName);
        Assert.Equal(262144, model.ContextWindowTokens);
        Assert.Equal(LlmProviderType.OllamaRouter, model.ProviderType);
        Assert.True(model.SupportsReasoning);
    }

    [Fact]
    public async Task Router_WhenRouterModelsUnavailable_ReturnsEmptyGracefully()
    {
        var service = new OllamaRouterModelCatalogService(
            StubHttpHandler.Client(new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.InternalServerError))),
            Options.Create(new LlmEndpointOptions()), NullLogger<OllamaRouterModelCatalogService>.Instance);

        Assert.Empty(await service.GetAvailableModelsAsync());
    }

    [Fact]
    public async Task WhenTagsUnavailable_ReturnsEmpty()
    {
        var service = new OllamaModelCatalogService(
            StubHttpHandler.Client(new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.InternalServerError))),
            Options.Create(new LlmEndpointOptions()), NullLogger<OllamaModelCatalogService>.Instance);

        Assert.Empty(await service.GetAvailableModelsAsync());
    }

    [Fact]
    public async Task GetAvailableModelsAsync_DeduplicatesTagsWithSameName()
    {
        const string duplicateTags = """{"models":[{"name":"dup:1","size":100},{"name":"dup:1","size":100},{"name":"unique:2","size":200}]}""";
        var showCallCount = 0;
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/tags")
            {
                return StubHttpHandler.Json(duplicateTags);
            }
            if (req.RequestUri!.AbsolutePath == "/api/show")
            {
                Interlocked.Increment(ref showCallCount);
                return StubHttpHandler.Json("""{"model_info":{"llama.context_length":4096}}""");
            }
            return StubHttpHandler.Status(HttpStatusCode.NotFound);
        });

        var service = new OllamaModelCatalogService(
            StubHttpHandler.Client(handler), Options.Create(new LlmEndpointOptions()), NullLogger<OllamaModelCatalogService>.Instance);

        var models = await service.GetAvailableModelsAsync();

        Assert.Equal(2, models.Count);
        Assert.Equal(2, showCallCount);
        Assert.Contains(models, m => m.ModelId == "dup:1");
        Assert.Contains(models, m => m.ModelId == "unique:2");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_WhenTagsProbeTimesOut_ReturnsEmptyGracefully()
    {
        var handler = new StubHttpHandler(_ =>
        {
            // Simulate a hanging / dropped packet response exceeding probe timeout
            Thread.Sleep(3500);
            return StubHttpHandler.Json(TagsJson);
        });

        var service = new OllamaModelCatalogService(
            StubHttpHandler.Client(handler), Options.Create(new LlmEndpointOptions()), NullLogger<OllamaModelCatalogService>.Instance);

        var models = await service.GetAvailableModelsAsync();
        Assert.Empty(models);
    }
}

