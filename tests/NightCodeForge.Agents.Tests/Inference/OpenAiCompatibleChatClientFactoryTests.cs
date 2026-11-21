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
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Agents.Tests.Inference;

public class OpenAiCompatibleChatClientFactoryTests
{
    private sealed class FakeSecretStore : IApiKeySecretStore
    {
        private readonly Dictionary<string, string> _openAiKeys = new(StringComparer.Ordinal);
        public string? OpenAiEnvFallback { get; set; }
        public List<string?> ResolveCalls { get; } = [];
        public Action? OnNextResolve { get; set; }
        public bool HasSecretChangedSubscribers => SecretChanged != null;

        public event EventHandler<ApiKeySecretChangedEventArgs>? SecretChanged;

        public bool IsKeyringAvailable() => true;

        public string? GetOpenAiApiKey(string? endpoint)
        {
            if (!ApiKeyEndpointNormalizer.TryNormalizeEndpoint(endpoint, out var normalized))
            {
                return null;
            }
            return _openAiKeys.GetValueOrDefault(normalized);
        }

        public string? ResolveEffectiveOpenAiApiKey(string? endpoint)
        {
            ResolveCalls.Add(endpoint);
            var callback = OnNextResolve;
            if (callback != null)
            {
                OnNextResolve = null;
                callback();
            }
            return GetOpenAiApiKey(endpoint) ?? (string.IsNullOrWhiteSpace(OpenAiEnvFallback) ? null : OpenAiEnvFallback.Trim());
        }

        public bool HasOpenAiApiKey(string? endpoint) => !string.IsNullOrWhiteSpace(GetOpenAiApiKey(endpoint));

        public void SetOpenAiApiKey(string endpoint, string apiKey)
        {
            var normalized = ApiKeyEndpointNormalizer.NormalizeEndpoint(endpoint);
            _openAiKeys[normalized] = apiKey.Trim();
            SecretChanged?.Invoke(this, new ApiKeySecretChangedEventArgs(ApiKeySecretKind.OpenAiCompatible, normalized));
        }

        public void DeleteOpenAiApiKey(string endpoint)
        {
            var normalized = ApiKeyEndpointNormalizer.NormalizeEndpoint(endpoint);
            _openAiKeys.Remove(normalized);
            SecretChanged?.Invoke(this, new ApiKeySecretChangedEventArgs(ApiKeySecretKind.OpenAiCompatible, normalized));
        }

        public string? GetOllamaWebSearchApiKey() => null;
        public string? ResolveEffectiveOllamaWebSearchApiKey() => null;
        public bool HasOllamaWebSearchApiKey() => false;

        public void SetOllamaWebSearchApiKey(string apiKey) =>
            SecretChanged?.Invoke(this, new ApiKeySecretChangedEventArgs(ApiKeySecretKind.OllamaWebSearch, null));

        public void DeleteOllamaWebSearchApiKey() =>
            SecretChanged?.Invoke(this, new ApiKeySecretChangedEventArgs(ApiKeySecretKind.OllamaWebSearch, null));
    }

    private readonly Mock<IOperationsTelemetryService> _telemetryMock = new();
    private readonly Mock<IOperationsControlService> _opsControlMock = new();
    private readonly IOptions<LlmRetryPolicyOptions> _retryOptions = Options.Create(new LlmRetryPolicyOptions());
    private readonly IAgentActivityStreamer _activityStreamer = new InMemoryAgentActivityStreamer();

    private OpenAiCompatibleChatClientFactory CreateFactory(
        LlmProviderOptions options,
        IOpenAiModelAllowlistCache cache,
        IApiKeySecretStore? secretStore = null) =>
        new(
            Options.Create(options),
            NullLoggerFactory.Instance,
            _telemetryMock.Object,
            _opsControlMock.Object,
            TimeProvider.System,
            _retryOptions,
            cache,
            new OpenAiDefaultModelSelector(),
            _activityStreamer,
            secretStore ?? new FakeSecretStore());

    [Fact]
    public void CreateClient_WhenModelNotAllowed_ThrowsModelNotAllowedException()
    {
        var options = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Endpoint = "http://localhost:1234/v1"
            }
        };

        var cache = new OpenAiModelAllowlistCache();
        cache.SetDiscoveredModels([new AvailableModelInfo("allowed-model", "Allowed")]);

        using var factory = CreateFactory(options, cache);

        var ex = Assert.Throws<ModelNotAllowedException>(() => factory.CreateClient("unlisted-model"));
        Assert.Equal("unlisted-model", ex.ModelId);
        Assert.Equal(LlmProviderType.OpenAiCompatible, ex.ProviderType);
    }

    [Fact]
    public void CreateClient_WhenModelAllowedInCache_AndNoKeyConfigured_InstantiatesClientSuccessfully()
    {
        var options = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Endpoint = "http://localhost:1234/v1"
            }
        };

        var cache = new OpenAiModelAllowlistCache();
        cache.SetDiscoveredModels([new AvailableModelInfo("allowed-model", "Allowed")]);

        var secretStore = new FakeSecretStore();
        using var factory = CreateFactory(options, cache, secretStore);

        var client = factory.CreateClient("allowed-model");
        Assert.NotNull(client);
        Assert.Contains("http://localhost:1234/v1", secretStore.ResolveCalls);
    }

    [Fact]
    public void CreateClient_WhenManualAllowedModelsConfigured_AllowsOnlyConfigured()
    {
        var options = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Endpoint = "https://api.openai.com/v1",
                AllowedModels =
                [
                    new OpenAiAllowedModel { ModelId = "custom-llama" }
                ]
            }
        };

        var cache = new OpenAiModelAllowlistCache();
        cache.SetDiscoveredModels([new AvailableModelInfo("discovered-but-unlisted", "Discovered")]);

        var secretStore = new FakeSecretStore();
        secretStore.SetOpenAiApiKey("https://api.openai.com/v1", "sk-test");
        using var factory = CreateFactory(options, cache, secretStore);

        // Allowed via manual configuration
        var client = factory.CreateClient("custom-llama");
        Assert.NotNull(client);

        // Discovered but not in AllowedModels -> throws
        Assert.Throws<ModelNotAllowedException>(() => factory.CreateClient("discovered-but-unlisted"));
    }

    [Fact]
    public void CreateClient_WhenDefaultModelIdNull_ResolvesOptimalDefaultModelFromCache()
    {
        var options = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            DefaultModelId = null,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Endpoint = "http://localhost:1234/v1"
            }
        };

        var cache = new OpenAiModelAllowlistCache();
        cache.SetDiscoveredModels([
            new AvailableModelInfo("pricy-model", "Pricy", PromptCostPerMillion: 2.0m),
            new AvailableModelInfo("cheap-model", "Cheap", PromptCostPerMillion: 0.15m)
        ]);

        using var factory = CreateFactory(options, cache);

        var client = factory.CreateClient();
        Assert.NotNull(client);
    }

    [Fact]
    public void CreateClient_CachesSessionLessClients_AndEvictsOnlyWhenOpenAiSecretChanges()
    {
        var options = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Endpoint = "https://api.openai.com/v1"
            }
        };

        var cache = new OpenAiModelAllowlistCache();
        cache.SetDiscoveredModels([new AvailableModelInfo("gpt-4o", "GPT-4o")]);

        var secretStore = new FakeSecretStore();
        secretStore.SetOpenAiApiKey("https://api.openai.com/v1", "sk-initial-1");

        using var factory = CreateFactory(options, cache, secretStore);

        var c1 = factory.CreateClient("gpt-4o");
        var c2 = factory.CreateClient("gpt-4o");
        Assert.Same(c1, c2);
        Assert.Single(secretStore.ResolveCalls);

        // Unrelated Ollama secret change does NOT evict OpenAI client cache
        secretStore.SetOllamaWebSearchApiKey("ollama-unrelated");
        var c2AfterOllama = factory.CreateClient("gpt-4o");
        Assert.Same(c1, c2AfterOllama);

        // Rotating OpenAI key evicts cached client immediately
        secretStore.SetOpenAiApiKey("https://api.openai.com/v1", "sk-rotated-2");
        var c3 = factory.CreateClient("gpt-4o");
        Assert.NotSame(c1, c3);
        Assert.Equal(2, secretStore.ResolveCalls.Count);

        // Deleting OpenAI key also evicts cached client (falls back to LocalAuthBypassKey or OPENAI_API_KEY)
        secretStore.DeleteOpenAiApiKey("https://api.openai.com/v1");
        var c4 = factory.CreateClient("gpt-4o");
        Assert.NotSame(c3, c4);
        Assert.Equal(3, secretStore.ResolveCalls.Count);
    }

    [Fact]
    public async Task CreateClient_SendsResolvedBearerAuthorizationHeader_AcrossRotationEnvFallbackAndLocalBypass()
    {
        var capturedAuthHeaders = new List<string?>();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var endpoint = $"http://127.0.0.1:{port}/v1";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = AcceptLoopAsync(listener, capturedAuthHeaders, cts.Token);

        try
        {
            var options = new LlmProviderOptions
            {
                ProviderType = LlmProviderType.OpenAiCompatible,
                OpenAiCompatible = new OpenAiCompatibleOptions
                {
                    Endpoint = endpoint
                }
            };

            var cache = new OpenAiModelAllowlistCache();
            cache.SetDiscoveredModels([new AvailableModelInfo("gpt-4o", "GPT-4o")]);

            var secretStore = new FakeSecretStore();
            secretStore.SetOpenAiApiKey(endpoint, "sk-initial-1");

            using var factory = CreateFactory(options, cache, secretStore);

            // 1. Initial keyring key
            await factory.CreateClient("gpt-4o").GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token);

            // 2. Rotated keyring key
            secretStore.SetOpenAiApiKey(endpoint, "sk-rotated-2");
            await factory.CreateClient("gpt-4o").GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token);

            // 3. Env fallback after keyring key deleted
            secretStore.OpenAiEnvFallback = "sk-env-3";
            secretStore.DeleteOpenAiApiKey(endpoint);
            await factory.CreateClient("gpt-4o").GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token);

            // 4. LocalAuthBypassKey when completely unconfigured
            secretStore.OpenAiEnvFallback = null;
            secretStore.DeleteOpenAiApiKey(endpoint);
            await factory.CreateClient("gpt-4o").GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token);

            Assert.Equal(
                [
                    "Bearer sk-initial-1",
                    "Bearer sk-rotated-2",
                    "Bearer sk-env-3",
                    $"Bearer {KnownDefaultModels.LocalAuthBypassKey}"
                ],
                capturedAuthHeaders);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            await serverTask;
        }
    }

    [Fact]
    public async Task CreateClient_WhenSecretChangesDuringClientBuild_DoesNotRetainStaleClientInCache()
    {
        var capturedAuthHeaders = new List<string?>();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var endpoint = $"http://127.0.0.1:{port}/v1";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = AcceptLoopAsync(listener, capturedAuthHeaders, cts.Token);

        try
        {
            var options = new LlmProviderOptions
            {
                ProviderType = LlmProviderType.OpenAiCompatible,
                OpenAiCompatible = new OpenAiCompatibleOptions
                {
                    Endpoint = endpoint
                }
            };

            var cache = new OpenAiModelAllowlistCache();
            cache.SetDiscoveredModels([new AvailableModelInfo("gpt-4o", "GPT-4o")]);

            var secretStore = new FakeSecretStore();
            secretStore.SetOpenAiApiKey(endpoint, "sk-before-race");
            secretStore.OnNextResolve = () => secretStore.SetOpenAiApiKey(endpoint, "sk-after-race");

            using var factory = CreateFactory(options, cache, secretStore);

            // First call builds a client while SecretChanged fires mid-build
            var staleClient = factory.CreateClient("gpt-4o");
            // Second call must NOT receive the stale pre-rotation client from cache
            var rotatedClient = factory.CreateClient("gpt-4o");
            var cachedRotatedClient = factory.CreateClient("gpt-4o");

            Assert.NotSame(staleClient, rotatedClient);
            Assert.Same(rotatedClient, cachedRotatedClient);
            Assert.Equal(2, secretStore.ResolveCalls.Count);

            await rotatedClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token);
            Assert.Equal(["Bearer sk-after-race"], capturedAuthHeaders);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            await serverTask;
        }
    }

    [Fact]
    public void Dispose_UnsubscribesFromSecretChangedEvent()
    {
        var options = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Endpoint = "https://api.openai.com/v1"
            }
        };

        var cache = new OpenAiModelAllowlistCache();
        var secretStore = new FakeSecretStore();
        var factory = CreateFactory(options, cache, secretStore);

        Assert.True(secretStore.HasSecretChangedSubscribers);
        factory.Dispose();
        Assert.False(secretStore.HasSecretChangedSubscribers);
    }

    private static async Task AcceptLoopAsync(
        TcpListener listener,
        List<string?> capturedAuthHeaders,
        CancellationToken cancellationToken)
    {
        const string responseJson = "{\"id\":\"chatcmpl-test\",\"object\":\"chat.completion\",\"created\":1700000000,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    var requestHeaderAndRemaining = await ReadHttpRequestHeadersAsync(stream, cancellationToken);
                    if (requestHeaderAndRemaining == null)
                    {
                        continue;
                    }

                    var (headerText, alreadyReadBodyBytes) = requestHeaderAndRemaining.Value;

                    string? authHeader = null;
                    var contentLength = 0;
                    foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                        {
                            authHeader = line["Authorization:".Length..].Trim();
                        }
                        else if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                                 int.TryParse(line["Content-Length:".Length..].Trim(), out var parsedLen))
                        {
                            contentLength = parsedLen;
                        }
                    }

                    var remainingBodyBytes = contentLength - alreadyReadBodyBytes;
                    if (remainingBodyBytes > 0)
                    {
                        var drain = new byte[remainingBodyBytes];
                        var readTotal = 0;
                        while (readTotal < remainingBodyBytes)
                        {
                            var read = await stream.ReadAsync(drain.AsMemory(readTotal, remainingBodyBytes - readTotal), cancellationToken);
                            if (read == 0) break;
                            readTotal += read;
                        }
                    }

                    lock (capturedAuthHeaders)
                    {
                        capturedAuthHeaders.Add(authHeader);
                    }

                    var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                    var httpResponse =
                        $"HTTP/1.1 200 OK\r\n" +
                        $"Content-Type: application/json\r\n" +
                        $"Content-Length: {responseBytes.Length}\r\n" +
                        $"Connection: close\r\n\r\n" +
                        responseJson;
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(httpResponse), cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private static async Task<(string HeaderText, int AlreadyReadBodyBytes)?> ReadHttpRequestHeadersAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();

        while (!cancellationToken.IsCancellationRequested)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                return null;
            }

            ms.Write(buffer, 0, bytesRead);
            if (TryExtractHttpHeaders(ms, out var headerText, out var alreadyReadBodyBytes))
            {
                return (headerText, alreadyReadBodyBytes);
            }
        }

        return null;
    }

    private static bool TryExtractHttpHeaders(MemoryStream ms, out string headerText, out int alreadyReadBodyBytes)
    {
        ReadOnlySpan<byte> separator = "\r\n\r\n"u8;
        var accumulated = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        var sepIndex = accumulated.IndexOf(separator);
        if (sepIndex >= 0)
        {
            headerText = Encoding.ASCII.GetString(accumulated[..sepIndex]);
            alreadyReadBodyBytes = (int)ms.Length - (sepIndex + separator.Length);
            return true;
        }

        headerText = string.Empty;
        alreadyReadBodyBytes = 0;
        return false;
    }
}
