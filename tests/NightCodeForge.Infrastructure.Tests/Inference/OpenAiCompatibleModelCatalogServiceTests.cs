using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Inference;

public class OpenAiCompatibleModelCatalogServiceTests
{
    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public List<HttpRequestMessage> CapturedRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequests.Add(request);
            return Task.FromResult(handler(request));
        }
    }

    [Fact]
    public async Task GetAvailableModelsAsync_WhenEndpointReturns200_DiscoversAndCachesModels()
    {
        var responseJson = """
        {
          "data": [
            { "id": "model-cheap", "context_length": 32768, "pricing": { "prompt": 0.0000001, "completion": 0.0000002 } },
            { "id": "model-pricy", "context_length": 65536, "pricing": { "prompt": 0.000002, "completion": 0.000005 } }
          ]
        }
        """;

        var stub = new DelegatingHandlerStub(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });

        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("https://api.test-gateway.com/v1/") };
        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Endpoint = "https://api.test-gateway.com/v1",
                ApiKey = "sk-test-key-123",
                OrganizationId = "org-test-456"
            }
        });

        var cache = new OpenAiModelAllowlistCache();
        var catalog = new OpenAiCompatibleModelCatalogService(httpClient, options, cache, new OpenAiDefaultModelSelector(), NullLogger<OpenAiCompatibleModelCatalogService>.Instance);

        var models = await catalog.GetAvailableModelsAsync();

        Assert.Equal(2, models.Count);
        Assert.True(cache.IsModelAllowed("model-cheap"));
        Assert.True(cache.IsModelAllowed("model-pricy"));

        // Verify default model resolves to lowest prompt cost
        var defaultModel = await catalog.GetDefaultModelAsync();
        Assert.NotNull(defaultModel);
        Assert.Equal("model-cheap", defaultModel.ModelId);

        // Verify request headers
        Assert.NotEmpty(stub.CapturedRequests);
        var captured = stub.CapturedRequests[0];
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("sk-test-key-123", captured.Headers.Authorization?.Parameter);
        Assert.True(captured.Headers.Contains("OpenAI-Organization"));
        Assert.Equal("org-test-456", captured.Headers.GetValues("OpenAI-Organization").First());

        // Verify strict suppression of router headers
        Assert.False(captured.Headers.Contains("X-OllamaRouter-Target"));
        Assert.False(captured.Headers.Contains("X-OllamaRouter-Session-Id"));
    }

    [Fact]
    public async Task GetAvailableModelsAsync_WhenAllowedModelsConfigured_OverridesAndFiltersDiscovered()
    {
        var responseJson = """
        {
          "data": [
            { "id": "model-a", "context_length": 32768 },
            { "id": "model-b", "context_length": 65536 }
          ]
        }
        """;

        var stub = new DelegatingHandlerStub(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });

        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("https://api.test-gateway.com/v1/") };
        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Endpoint = "https://api.test-gateway.com/v1",
                AllowedModels =
                [
                    new OpenAiAllowedModel { ModelId = "model-b", DisplayName = "Custom Model B", ContextWindowTokens = 120000 }
                ]
            }
        });

        var cache = new OpenAiModelAllowlistCache();
        var catalog = new OpenAiCompatibleModelCatalogService(httpClient, options, cache, new OpenAiDefaultModelSelector(), NullLogger<OpenAiCompatibleModelCatalogService>.Instance);

        var models = await catalog.GetAvailableModelsAsync();

        Assert.Single(models);
        Assert.Equal("model-b", models[0].ModelId);
        Assert.Equal("Custom Model B", models[0].DisplayName);
        Assert.Equal(120000, models[0].ContextWindowTokens);

        // Verify cache reflects filtered allowed models with overridden context tokens
        Assert.True(cache.IsModelAllowed("model-b"));
        Assert.False(cache.IsModelAllowed("model-a"));
        Assert.True(cache.TryGetModel("model-b", out var cachedB));
        Assert.Equal(120000, cachedB?.ContextWindowTokens);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task GetAvailableModelsAsync_WhenProbeReturns404Or501_ActivatesManualConfiguration(HttpStatusCode statusCode)
    {
        var stub = new DelegatingHandlerStub(req => new HttpResponseMessage(statusCode));
        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("http://localhost:8080/v1/") };

        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Endpoint = "http://localhost:8080/v1",
                AllowedModels =
                [
                    new OpenAiAllowedModel { ModelId = "manual-model", DisplayName = "Manual Model", ContextWindowTokens = 65536 }
                ]
            }
        });

        var cache = new OpenAiModelAllowlistCache();
        var catalog = new OpenAiCompatibleModelCatalogService(httpClient, options, cache, new OpenAiDefaultModelSelector(), NullLogger<OpenAiCompatibleModelCatalogService>.Instance);

        var models = await catalog.GetAvailableModelsAsync();

        Assert.Single(models);
        Assert.Equal("manual-model", models[0].ModelId);
        Assert.True(cache.IsModelAllowed("manual-model"));

        var verify = await catalog.VerifyEndpointAsync();
        Assert.True(verify.IsSuccess);
        Assert.False(verify.IsDiscoveryImplemented);
        Assert.NotNull(verify.WarningMessage);
        Assert.Contains("Manual model configuration is active", verify.WarningMessage);
    }

    [Fact]
    public async Task VerifyEndpointAsync_WhenUnauthorized_ReturnsDiagnosticFailure()
    {
        var stub = new DelegatingHandlerStub(req => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("https://api.openai.com/v1/") };

        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions { Endpoint = "https://api.openai.com/v1" }
        });

        var cache = new OpenAiModelAllowlistCache();
        var catalog = new OpenAiCompatibleModelCatalogService(httpClient, options, cache, new OpenAiDefaultModelSelector(), NullLogger<OpenAiCompatibleModelCatalogService>.Instance);

        var verify = await catalog.VerifyEndpointAsync();

        Assert.False(verify.IsSuccess);
        Assert.False(verify.IsDiscoveryImplemented);
        Assert.NotNull(verify.ErrorMessage);
        Assert.Contains("HTTP 401 Unauthorized", verify.ErrorMessage);
    }

    [Fact]
    public async Task VerifyEndpointAsync_WhenConnectionFails_ReturnsConnectionDiagnostic()
    {
        var stub = new DelegatingHandlerStub(req => throw new HttpRequestException("Connection refused by peer"));
        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("http://localhost:9999/v1/") };

        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions { Endpoint = "http://localhost:9999/v1" }
        });

        var cache = new OpenAiModelAllowlistCache();
        var catalog = new OpenAiCompatibleModelCatalogService(httpClient, options, cache, new OpenAiDefaultModelSelector(), NullLogger<OpenAiCompatibleModelCatalogService>.Instance);

        var verify = await catalog.VerifyEndpointAsync();

        Assert.False(verify.IsSuccess);
        Assert.False(verify.IsDiscoveryImplemented);
        Assert.NotNull(verify.ErrorMessage);
        Assert.Contains("Connection failed", verify.ErrorMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task VerifyEndpointAsync_WhenDiscoveryUnimplementedAndNoManualModelsConfigured_FailsFastWithActionableDiagnostic(HttpStatusCode statusCode)
    {
        var stub = new DelegatingHandlerStub(req => new HttpResponseMessage(statusCode));
        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("http://localhost:8080/v1/") };

        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Endpoint = "http://localhost:8080/v1",
                AllowedModels = []
            }
        });

        var cache = new OpenAiModelAllowlistCache();
        var catalog = new OpenAiCompatibleModelCatalogService(httpClient, options, cache, new OpenAiDefaultModelSelector(), NullLogger<OpenAiCompatibleModelCatalogService>.Instance);

        var verify = await catalog.VerifyEndpointAsync();

        Assert.False(verify.IsSuccess);
        Assert.False(verify.IsDiscoveryImplemented);
        Assert.NotNull(verify.ErrorMessage);
        Assert.Contains("The remote OpenAI-compatible endpoint does not implement /api/v1/models", verify.ErrorMessage);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_WhenFirstCandidateReturnsEmpty200_ProbesSecondCandidateAndSucceeds()
    {
        var stub = new DelegatingHandlerStub(req =>
        {
            if (req.RequestUri?.AbsolutePath.Contains("api/v1/models") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[{"id":"fallback-model"}]}""")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"ok"}""")
            };
        });

        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("http://localhost:1234/") };
        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions { Endpoint = "http://localhost:1234" }
        });

        var cache = new OpenAiModelAllowlistCache();
        var catalog = new OpenAiCompatibleModelCatalogService(httpClient, options, cache, new OpenAiDefaultModelSelector(), NullLogger<OpenAiCompatibleModelCatalogService>.Instance);

        var models = await catalog.GetAvailableModelsAsync();

        Assert.Single(models);
        Assert.Equal("fallback-model", models[0].ModelId);
    }

    [Fact]
    public async Task VerifyEndpointAsync_WhenEndpointReturns200WithEmptyModels_SetsDiscoveryImplementedTrue()
    {
        var stub = new DelegatingHandlerStub(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[]}""")
        });

        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("http://localhost:1234/v1/") };
        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Endpoint = "http://localhost:1234/v1",
                AllowedModels =
                [
                    new OpenAiAllowedModel { ModelId = "manual-fallback", DisplayName = "Manual Fallback" }
                ]
            }
        });

        var cache = new OpenAiModelAllowlistCache();
        var catalog = new OpenAiCompatibleModelCatalogService(httpClient, options, cache, new OpenAiDefaultModelSelector(), NullLogger<OpenAiCompatibleModelCatalogService>.Instance);

        var verify = await catalog.VerifyEndpointAsync();

        Assert.True(verify.IsSuccess);
        Assert.True(verify.IsDiscoveryImplemented);
        Assert.Single(verify.VerifiedModels!);
        Assert.Equal("manual-fallback", verify.VerifiedModels![0].ModelId);
        Assert.NotNull(verify.WarningMessage);
        Assert.DoesNotContain("404/501", verify.WarningMessage);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_WhenBaseAddressHasNoTrailingSlash_ProbesRelativePathCorrectly()
    {
        var stub = new DelegatingHandlerStub(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/v1/models")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[{"id":"slash-test-model"}]}""")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        // BaseAddress configured without trailing slash: http://localhost:1234/v1
        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("http://localhost:1234/v1") };
        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions { Endpoint = "http://localhost:1234/v1" }
        });

        var cache = new OpenAiModelAllowlistCache();
        var catalog = new OpenAiCompatibleModelCatalogService(httpClient, options, cache, new OpenAiDefaultModelSelector(), NullLogger<OpenAiCompatibleModelCatalogService>.Instance);

        var models = await catalog.GetAvailableModelsAsync();

        Assert.Single(models);
        Assert.Equal("slash-test-model", models[0].ModelId);
        Assert.Contains(stub.CapturedRequests, r => r.RequestUri?.AbsolutePath == "/v1/models");
    }
}

