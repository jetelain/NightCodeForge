using System.Net;
using System.Text.Json;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Tests;

public class OllamaWebSearchServiceTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handler { get; set; }

        public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            Handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Handler(request, cancellationToken);
        }
    }

    private class FakeSettingsService : IWebSearchSettingsService
    {
        private WebSearchSettings _settings;
        public event Action<WebSearchSettings>? OnSettingsChanged;

        public FakeSettingsService(WebSearchSettings? settings = null)
        {
            _settings = settings ?? new WebSearchSettings();
        }

        public WebSearchSettings GetSettings() => _settings.Clone();
        public Task<WebSearchSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(GetSettings());
        public Task UpdateSettingsAsync(WebSearchSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings.Clone();
            OnSettingsChanged?.Invoke(_settings);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void IsConfigured_False_WhenNoApiKeyProvided()
    {
        var handler = new MockHttpMessageHandler((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new HttpClient(handler);
        var settingsService = new FakeSettingsService(new WebSearchSettings { ApiKey = "" });

        var service = new OllamaWebSearchService(client, settingsService, explicitApiKey: null);

        Assert.False(service.IsConfigured);
        Assert.Equal("Ollama", service.ProviderName);
    }

    [Fact]
    public void IsConfigured_True_WhenExplicitApiKeyProvided()
    {
        var handler = new MockHttpMessageHandler((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new HttpClient(handler);

        var service = new OllamaWebSearchService(client, explicitApiKey: "ollama_key_123");

        Assert.True(service.IsConfigured);
    }

    [Fact]
    public void IsConfigured_True_WhenSettingsServiceHasKey()
    {
        var handler = new MockHttpMessageHandler((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new HttpClient(handler);
        var settingsService = new FakeSettingsService(new WebSearchSettings { ApiKey = "settings_key_abc" });

        var service = new OllamaWebSearchService(client, settingsService);

        Assert.True(service.IsConfigured);
    }

    [Fact]
    public async Task SearchAsync_ThrowsInvalidOperationException_WhenUnconfigured()
    {
        var handler = new MockHttpMessageHandler((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new HttpClient(handler);
        var service = new OllamaWebSearchService(client, explicitApiKey: "");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync("test query"));
        Assert.Contains("OLLAMA_API_KEY is not configured", ex.Message);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_WhenQueryIsWhitespace()
    {
        var handler = new MockHttpMessageHandler((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new HttpClient(handler);
        var service = new OllamaWebSearchService(client, explicitApiKey: "test_key");

        var response = await service.SearchAsync("   ");

        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task SearchAsync_SendsCorrectRequestAndParsesResults()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            capturedRequest = req;
            capturedBody = await req.Content!.ReadAsStringAsync(ct);

            var sampleResponse = new
            {
                results = new[]
                {
                    new { title = "Ollama Documentation", url = "https://ollama.com/docs", content = "Official documentation for running LLMs." },
                    new { title = "GitHub - Ollama", url = "https://github.com/ollama/ollama", content = "Get up and running with large language models." }
                }
            };

            var json = JsonSerializer.Serialize(sampleResponse);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler);
        var service = new OllamaWebSearchService(client, explicitApiKey: "ollama_secret_456");

        var result = await service.SearchAsync("Ollama local LLM", 5);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("ollama_secret_456", capturedRequest.Headers.Authorization?.Parameter);

        Assert.NotNull(capturedBody);
        Assert.Contains("\"query\":\"Ollama local LLM\"", capturedBody);
        Assert.Contains("\"max_results\":5", capturedBody);

        Assert.Equal(2, result.Results.Count);
        Assert.Equal("Ollama Documentation", result.Results[0].Title);
        Assert.Equal("https://ollama.com/docs", result.Results[0].Url);
        Assert.Equal("Official documentation for running LLMs.", result.Results[0].Content);
    }

    [Fact]
    public async Task SearchAsync_ClampsMaxResultsTo10()
    {
        string? capturedBody = null;

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"results\":[]}", System.Text.Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler);
        var service = new OllamaWebSearchService(client, explicitApiKey: "test_key");

        await service.SearchAsync("test query", 25);

        Assert.NotNull(capturedBody);
        Assert.Contains("\"max_results\":10", capturedBody);
    }

    [Fact]
    public async Task SearchAsync_ThrowsHttpRequestException_OnApiError()
    {
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("Invalid API key provided", System.Text.Encoding.UTF8, "text/plain")
            });
        });

        var client = new HttpClient(handler);
        var service = new OllamaWebSearchService(client, explicitApiKey: "bad_key");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync("query"));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("Invalid API key provided", ex.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_ReturnsSuccessTuple()
    {
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            var sample = new
            {
                results = new[] { new { title = "Ollama", url = "https://ollama.com", content = "Local models" } }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(sample), System.Text.Encoding.UTF8, "application/json")
            });
        });

        var client = new HttpClient(handler);
        var service = new OllamaWebSearchService(client, explicitApiKey: "test_key");

        var (success, message, count, elapsed) = await service.TestConnectionAsync("Ollama");

        Assert.True(success);
        Assert.Equal(1, count);
        Assert.Contains("Successfully connected", message);
    }

    [Fact]
    public async Task TestConnectionAsync_ReturnsFailureTuple_OnException()
    {
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("Access denied", System.Text.Encoding.UTF8, "text/plain")
            });
        });

        var client = new HttpClient(handler);
        var service = new OllamaWebSearchService(client, explicitApiKey: "bad_key");

        var (success, message, count, elapsed) = await service.TestConnectionAsync("Ollama");

        Assert.False(success);
        Assert.Equal(0, count);
        Assert.Contains("Access denied", message);
    }
}
