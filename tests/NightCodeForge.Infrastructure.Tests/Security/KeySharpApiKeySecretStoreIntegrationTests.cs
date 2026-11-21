using KeySharp;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Security;
using NightCodeForge.Infrastructure.Security;

namespace NightCodeForge.Infrastructure.Tests.Security;

public sealed class KeySharpApiKeySecretStoreIntegrationTests : IDisposable
{
    private const string TestPackage = "NightCodeForge.Tests";
    private readonly string _openAiService;
    private readonly string _ollamaService;
    private readonly string _ollamaUsername;
    private readonly List<(string Service, string Username)> _createdCoordinates = [];

    public KeySharpApiKeySecretStoreIntegrationTests()
    {
        var suffix = Guid.NewGuid().ToString("N");
        _openAiService = $"OpenAiCompatibleApiKey-{suffix}";
        _ollamaService = $"OllamaWebSearchApiKey-{suffix}";
        _ollamaUsername = $"ollama-web-search-{suffix}";
    }

    public void Dispose()
    {
        foreach (var (service, username) in _createdCoordinates)
        {
            try
            {
                Keyring.DeletePassword(TestPackage, service, username);
            }
            catch (KeyringException ex) when (ex.Type == ErrorType.NotFound)
            {
                // Already deleted or never persisted
            }
        }
    }

    [Fact]
    public void OpenAiAndOllamaKeys_RoundTripNormalizationOverwriteAndIdempotentDelete_InNativeOsKeyring()
    {
        var store = new KeySharpApiKeySecretStore(
            NullLogger<KeySharpApiKeySecretStore>.Instance,
            TestPackage,
            _openAiService,
            _ollamaService,
            _ollamaUsername);

        if (!store.IsKeyringAvailable())
        {
            return;
        }

        const string rawEndpointA = "  HTTPS://API.OPENAI.COM:443/v1/ ";
        const string canonicalEndpointA = "https://api.openai.com/v1";
        const string endpointB = "https://openrouter.ai/api/v1";

        _createdCoordinates.Add((_openAiService, canonicalEndpointA));
        _createdCoordinates.Add((_openAiService, endpointB));
        _createdCoordinates.Add((_ollamaService, _ollamaUsername));

        // 1. NotFound maps cleanly to absent
        Assert.False(store.HasOpenAiApiKey(canonicalEndpointA));
        Assert.Null(store.GetOpenAiApiKey(canonicalEndpointA));
        Assert.False(store.HasOllamaWebSearchApiKey());
        Assert.Null(store.GetOllamaWebSearchApiKey());

        // 2. Store & round-trip OpenAI endpoint-scoped keys (including normalization equivalence)
        store.SetOpenAiApiKey(rawEndpointA, "sk-test-openai-alpha");
        store.SetOpenAiApiKey(endpointB, "sk-test-openrouter-beta");

        Assert.True(store.HasOpenAiApiKey(canonicalEndpointA));
        Assert.Equal("sk-test-openai-alpha", store.GetOpenAiApiKey("https://api.openai.com/v1?x=1#frag"));
        Assert.Equal("sk-test-openrouter-beta", store.GetOpenAiApiKey(endpointB));
        Assert.Equal("sk-test-openai-alpha", Keyring.GetPassword(TestPackage, _openAiService, canonicalEndpointA));

        // Fresh uncached store instance observes persisted OS keyring entries
        var freshStore = new KeySharpApiKeySecretStore(
            NullLogger<KeySharpApiKeySecretStore>.Instance,
            TestPackage,
            _openAiService,
            _ollamaService,
            _ollamaUsername);
        Assert.Equal("sk-test-openai-alpha", freshStore.GetOpenAiApiKey(canonicalEndpointA));

        // 3. Overwrite / rotate key
        store.SetOpenAiApiKey(canonicalEndpointA, "sk-test-openai-rotated");
        Assert.Equal("sk-test-openai-rotated", store.GetOpenAiApiKey(canonicalEndpointA));
        Assert.Equal("sk-test-openai-rotated", Keyring.GetPassword(TestPackage, _openAiService, canonicalEndpointA));

        var rotatedFreshStore = new KeySharpApiKeySecretStore(
            NullLogger<KeySharpApiKeySecretStore>.Instance,
            TestPackage,
            _openAiService,
            _ollamaService,
            _ollamaUsername);
        Assert.Equal("sk-test-openai-rotated", rotatedFreshStore.GetOpenAiApiKey(canonicalEndpointA));

        // 4. Store & overwrite Ollama Cloud Search key
        store.SetOllamaWebSearchApiKey("ollama-key-v1");
        Assert.True(store.HasOllamaWebSearchApiKey());
        Assert.Equal("ollama-key-v1", Keyring.GetPassword(TestPackage, _ollamaService, _ollamaUsername));

        store.SetOllamaWebSearchApiKey("ollama-key-v2");
        Assert.Equal("ollama-key-v2", store.GetOllamaWebSearchApiKey());
        Assert.Equal("ollama-key-v2", Keyring.GetPassword(TestPackage, _ollamaService, _ollamaUsername));

        // 5. Delete & verify idempotency
        store.DeleteOpenAiApiKey(rawEndpointA);
        Assert.False(store.HasOpenAiApiKey(canonicalEndpointA));
        Assert.Null(store.GetOpenAiApiKey(canonicalEndpointA));
        store.DeleteOpenAiApiKey(canonicalEndpointA); // Idempotent

        store.DeleteOllamaWebSearchApiKey();
        Assert.False(store.HasOllamaWebSearchApiKey());
        Assert.Null(store.GetOllamaWebSearchApiKey());
        store.DeleteOllamaWebSearchApiKey(); // Idempotent

        // Endpoint B remains intact
        Assert.Equal("sk-test-openrouter-beta", store.GetOpenAiApiKey(endpointB));
    }
}
