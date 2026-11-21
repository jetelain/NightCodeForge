using KeySharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Security;
using NightCodeForge.Infrastructure.GitHub;
using NightCodeForge.Infrastructure.Security;
using Pmad.LocalApi.Security;

namespace NightCodeForge.Infrastructure.Tests.Security;

public class KeySharpApiKeySecretStoreUnitTests
{
    private sealed class FakeKeyringBackend : IKeyringBackend
    {
        public bool Available { get; set; } = true;
        public Dictionary<(string Package, string Service, string Username), string> Entries { get; } = new();
        public int GetCalls { get; private set; }
        public int SetCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public ErrorType? OverrideGetError { get; set; }
        public ErrorType? OverrideSetError { get; set; }
        public ErrorType? OverrideDeleteError { get; set; }

        public bool IsAvailable() => Available;

        public KeyringReadResult GetPassword(string package, string service, string username)
        {
            GetCalls++;
            if (OverrideGetError.HasValue && OverrideGetError.Value != ErrorType.NoError)
            {
                return new KeyringReadResult(OverrideGetError.Value, null, $"Simulated read error: {OverrideGetError.Value}", null);
            }

            if (Entries.TryGetValue((package, service, username), out var value))
            {
                return new KeyringReadResult(ErrorType.NoError, value, null, null);
            }

            return new KeyringReadResult(ErrorType.NotFound, null, "Not found", null);
        }

        public KeyringWriteResult SetPassword(string package, string service, string username, string password)
        {
            SetCalls++;
            if (OverrideSetError.HasValue && OverrideSetError.Value != ErrorType.NoError)
            {
                return new KeyringWriteResult(OverrideSetError.Value, $"Simulated write error: {OverrideSetError.Value}", null);
            }

            Entries[(package, service, username)] = password;
            return new KeyringWriteResult(ErrorType.NoError, null, null);
        }

        public KeyringWriteResult DeletePassword(string package, string service, string username)
        {
            DeleteCalls++;
            if (OverrideDeleteError.HasValue && OverrideDeleteError.Value != ErrorType.NoError)
            {
                return new KeyringWriteResult(OverrideDeleteError.Value, $"Simulated delete error: {OverrideDeleteError.Value}", null);
            }

            if (!Entries.Remove((package, service, username)))
            {
                return new KeyringWriteResult(ErrorType.NotFound, "Not found", null);
            }

            return new KeyringWriteResult(ErrorType.NoError, null, null);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static KeySharpApiKeySecretStore CreateStore(
        FakeKeyringBackend backend,
        RecordingLogger<KeySharpApiKeySecretStore>? logger = null,
        Dictionary<string, string?>? envVars = null)
    {
        var effectiveEnv = envVars ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        return new KeySharpApiKeySecretStore(
            logger ?? new RecordingLogger<KeySharpApiKeySecretStore>(),
            ApiKeySecretConstants.DefaultPackage,
            ApiKeySecretConstants.OpenAiCompatibleService,
            ApiKeySecretConstants.OllamaWebSearchService,
            ApiKeySecretConstants.OllamaWebSearchUsername,
            backend,
            name => effectiveEnv.TryGetValue(name, out var val) ? val : null);
    }

    [Fact]
    public void OpenAiKeys_AreScopedByNormalizedEndpoint_CachedInMemory_AndEmitChangeNotifications()
    {
        var backend = new FakeKeyringBackend();
        var store = CreateStore(backend);
        var events = new List<ApiKeySecretChangedEventArgs>();
        store.SecretChanged += (_, e) => events.Add(e);

        store.SetOpenAiApiKey("  HTTPS://API.OPENAI.COM:443/v1/ ", "  sk-openai-1  ");
        store.SetOpenAiApiKey("https://openrouter.ai/api/v1", "sk-openrouter-2");

        Assert.Equal("sk-openai-1", store.GetOpenAiApiKey("https://api.openai.com/v1"));
        Assert.Equal("sk-openrouter-2", store.GetOpenAiApiKey("https://openrouter.ai/api/v1/"));
        Assert.Null(store.GetOpenAiApiKey("https://api.deepseek.com/v1"));

        // Cached in memory: repeated lookups should NOT hit native backend repeatedly
        Assert.Equal(1, backend.GetCalls); // Only deepseek hit backend (others populated cache on Set)
        _ = store.GetOpenAiApiKey("https://api.openai.com/v1");
        _ = store.HasOpenAiApiKey("https://api.openai.com/v1");
        _ = store.GetOpenAiApiKey("https://api.deepseek.com/v1");
        Assert.Equal(1, backend.GetCalls);

        Assert.Equal(2, events.Count);
        Assert.Equal(new ApiKeySecretChangedEventArgs(ApiKeySecretKind.OpenAiCompatible, "https://api.openai.com/v1"), events[0]);
        Assert.Equal(new ApiKeySecretChangedEventArgs(ApiKeySecretKind.OpenAiCompatible, "https://openrouter.ai/api/v1"), events[1]);

        // Rotate and delete
        store.SetOpenAiApiKey("https://api.openai.com/v1", "sk-openai-rotated");
        Assert.Equal("sk-openai-rotated", store.GetOpenAiApiKey("https://api.openai.com/v1"));

        store.DeleteOpenAiApiKey("HTTPS://api.openai.com/v1/");
        Assert.Null(store.GetOpenAiApiKey("https://api.openai.com/v1"));
        Assert.False(store.HasOpenAiApiKey("https://api.openai.com/v1"));

        // Delete when already absent is idempotent
        store.DeleteOpenAiApiKey("https://api.openai.com/v1");
        Assert.Equal(5, events.Count);
    }

    [Fact]
    public void SetAndDeleteOpenAiApiKey_FailFastOnInvalidEndpointOrBlankSecret()
    {
        var backend = new FakeKeyringBackend();
        var store = CreateStore(backend);

        Assert.Throws<ArgumentException>(() => store.SetOpenAiApiKey("", "sk-valid"));
        Assert.Throws<ArgumentException>(() => store.SetOpenAiApiKey("not-a-uri", "sk-valid"));
        Assert.Throws<ArgumentException>(() => store.SetOpenAiApiKey("https://api.openai.com/v1", "   "));
        Assert.Throws<ArgumentException>(() => store.DeleteOpenAiApiKey("   "));
        Assert.Throws<ArgumentException>(() => store.SetOllamaWebSearchApiKey("   "));
    }

    [Fact]
    public void ResolveEffectiveKeys_FollowsKeyringThenEnvironmentPrecedence()
    {
        var backend = new FakeKeyringBackend();
        var env = new Dictionary<string, string?>
        {
            [ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable] = "  sk-env-openai  ",
            [ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable] = "  ollama-env-key  "
        };
        var store = CreateStore(backend, envVars: env);

        // No keyring entry -> falls back to env var
        Assert.False(store.HasOpenAiApiKey("https://api.openai.com/v1"));
        Assert.Equal("sk-env-openai", store.ResolveEffectiveOpenAiApiKey("https://api.openai.com/v1"));
        Assert.False(store.HasOllamaWebSearchApiKey());
        Assert.Equal("ollama-env-key", store.ResolveEffectiveOllamaWebSearchApiKey());

        // Keyring entry overrides env var
        store.SetOpenAiApiKey("https://api.openai.com/v1", "sk-keyring-openai");
        store.SetOllamaWebSearchApiKey("ollama-keyring-key");

        Assert.Equal("sk-keyring-openai", store.ResolveEffectiveOpenAiApiKey("https://api.openai.com/v1"));
        Assert.Equal("ollama-keyring-key", store.ResolveEffectiveOllamaWebSearchApiKey());

        // Clear keyring -> reverts to env fallback
        store.DeleteOpenAiApiKey("https://api.openai.com/v1");
        store.DeleteOllamaWebSearchApiKey();
        Assert.Equal("sk-env-openai", store.ResolveEffectiveOpenAiApiKey("https://api.openai.com/v1"));
        Assert.Equal("ollama-env-key", store.ResolveEffectiveOllamaWebSearchApiKey());
    }

    [Fact]
    public void HeadlessKeyringUnavailable_AllowsEnvFallbackOnReadWithSingleWarning_AndFailsFastOnWriteOrDelete()
    {
        var backend = new FakeKeyringBackend { Available = false };
        var logger = new RecordingLogger<KeySharpApiKeySecretStore>();
        var env = new Dictionary<string, string?>
        {
            [ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable] = "sk-headless-env",
            [ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable] = "ollama-headless-env"
        };
        var store = CreateStore(backend, logger, env);

        Assert.False(store.IsKeyringAvailable());
        Assert.Null(store.GetOpenAiApiKey("https://api.openai.com/v1"));
        Assert.Null(store.GetOllamaWebSearchApiKey());
        Assert.Equal("sk-headless-env", store.ResolveEffectiveOpenAiApiKey("https://api.openai.com/v1"));
        Assert.Equal("ollama-headless-env", store.ResolveEffectiveOllamaWebSearchApiKey());

        // Prominent unavailable warning logged strictly once
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.DoesNotContain("sk-headless-env", warnings[0].Message);
        Assert.DoesNotContain("ollama-headless-env", warnings[0].Message);

        // Writes and deletes MUST fail fast without plaintext fallback or secret leakage
        var setOpenAiEx = Assert.Throws<SecretStoreException>(() => store.SetOpenAiApiKey("https://api.openai.com/v1", "sk-secret-value-never-logged"));
        var setOllamaEx = Assert.Throws<SecretStoreException>(() => store.SetOllamaWebSearchApiKey("ollama-secret-never-logged"));
        Assert.Throws<SecretStoreException>(() => store.DeleteOpenAiApiKey("https://api.openai.com/v1"));
        Assert.Throws<SecretStoreException>(() => store.DeleteOllamaWebSearchApiKey());

        Assert.DoesNotContain("sk-secret-value-never-logged", setOpenAiEx.Message);
        Assert.DoesNotContain("ollama-secret-never-logged", setOllamaEx.Message);
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain("sk-secret-value-never-logged", entry.Message);
            Assert.DoesNotContain("ollama-secret-never-logged", entry.Message);
        });
    }

    [Fact]
    public void ReadReturningErrorTypeUnavailable_FallsBackToEnvironmentAndLogsWarningOnce()
    {
        var backend = new FakeKeyringBackend
        {
            Available = true,
            OverrideGetError = ErrorType.Unavailable
        };
        var logger = new RecordingLogger<KeySharpApiKeySecretStore>();
        var env = new Dictionary<string, string?>
        {
            [ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable] = "ollama-env-after-unavailable"
        };
        var store = CreateStore(backend, logger, env);

        Assert.Null(store.GetOllamaWebSearchApiKey());
        Assert.Null(store.GetOpenAiApiKey("https://api.openai.com/v1"));
        Assert.Equal("ollama-env-after-unavailable", store.ResolveEffectiveOllamaWebSearchApiKey());
        Assert.False(store.IsKeyringAvailable());
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData("sk-line\nbreak")]
    [InlineData("sk-carriage\rreturn")]
    [InlineData("sk-null\0byte")]
    public void SetApiKeys_RejectControlCharacters_WithoutLeakingSecret(string badSecret)
    {
        var backend = new FakeKeyringBackend();
        var store = CreateStore(backend);

        var openAiEx = Assert.Throws<ArgumentException>(() => store.SetOpenAiApiKey("https://api.openai.com/v1", badSecret));
        Assert.DoesNotContain(badSecret, openAiEx.Message);

        var ollamaEx = Assert.Throws<ArgumentException>(() => store.SetOllamaWebSearchApiKey(badSecret));
        Assert.DoesNotContain(badSecret, ollamaEx.Message);
        Assert.Equal(0, backend.SetCalls);
    }

    [Fact]
    public void UnavailableRead_DoesNotPoisonCache_AndRecoversAfterBackendBecomesAvailable()
    {
        const string endpointA = "https://api.openai.com/v1";
        const string endpointB = "https://openrouter.ai/api/v1";

        var backend = new FakeKeyringBackend { Available = false };
        backend.Entries[(ApiKeySecretConstants.DefaultPackage, ApiKeySecretConstants.OpenAiCompatibleService, endpointA)] = "sk-existing-a";
        backend.Entries[(ApiKeySecretConstants.DefaultPackage, ApiKeySecretConstants.OllamaWebSearchService, ApiKeySecretConstants.OllamaWebSearchUsername)] = "ollama-existing";

        var logger = new RecordingLogger<KeySharpApiKeySecretStore>();
        var store = CreateStore(backend, logger);

        // Reads while unavailable return null without calling GetPassword
        Assert.Null(store.GetOpenAiApiKey(endpointA));
        Assert.Null(store.GetOllamaWebSearchApiKey());
        Assert.Equal(0, backend.GetCalls);

        // Repeated reads while still unavailable stay O(1) without extra backend calls
        Assert.Null(store.GetOpenAiApiKey(endpointA));
        Assert.Null(store.GetOllamaWebSearchApiKey());
        Assert.Equal(0, backend.GetCalls);

        // Also test ErrorType.Unavailable path: read hits backend once, marks _isAvailable=false, does not poison cache
        backend.Available = true;
        backend.OverrideGetError = ErrorType.Unavailable;
        var store2 = CreateStore(backend);
        Assert.Null(store2.GetOpenAiApiKey(endpointA));
        Assert.Null(store2.GetOllamaWebSearchApiKey());
        Assert.Equal(1, backend.GetCalls);

        // Backend becomes available again; a successful mutation refreshes _isAvailable = true
        backend.OverrideGetError = null;
        store.SetOpenAiApiKey(endpointB, "sk-new-b");

        // Previously queried endpoint A and Ollama key MUST NOT have been poisoned with null in cache
        Assert.Equal("sk-existing-a", store.GetOpenAiApiKey(endpointA));
        Assert.Equal("ollama-existing", store.GetOllamaWebSearchApiKey());

        backend.Available = true;
        store2.SetOpenAiApiKey(endpointB, "sk-new-b2");
        Assert.Equal("sk-existing-a", store2.GetOpenAiApiKey(endpointA));
        Assert.Equal("ollama-existing", store2.GetOllamaWebSearchApiKey());
    }

    [Fact]
    public void ReadError_WithNullExceptionAndBlankMessage_ThrowsSecretStoreExceptionWithoutSyntheticInnerException()
    {
        var backend = new FakeKeyringBackend { OverrideGetError = ErrorType.GenericError };
        var store = CreateStore(backend);

        var ex = Assert.Throws<SecretStoreException>(() => store.GetOpenAiApiKey("https://api.openai.com/v1"));
        Assert.Null(ex.InnerException);
        Assert.Contains("GenericError", ex.Message);
    }

    [Theory]
    [InlineData(ErrorType.AccessDenied)]
    [InlineData(ErrorType.PasswordTooLong)]
    [InlineData(ErrorType.GenericError)]
    public void UnexpectedKeyringErrors_LogAndThrowSecretStoreException_WithoutLeakingSecrets(ErrorType errorType)
    {
        var backend = new FakeKeyringBackend { OverrideSetError = errorType };
        var logger = new RecordingLogger<KeySharpApiKeySecretStore>();
        var store = CreateStore(backend, logger);

        var secretValue = "sk-super-secret-value-999";
        var writeEx = Assert.Throws<SecretStoreException>(() => store.SetOpenAiApiKey("https://api.openai.com/v1", secretValue));
        Assert.Contains(errorType.ToString(), writeEx.Message);
        Assert.DoesNotContain(secretValue, writeEx.Message);
        Assert.All(logger.Entries, e => Assert.DoesNotContain(secretValue, e.Message));

        backend.OverrideSetError = null;
        backend.OverrideDeleteError = errorType;
        Assert.Throws<SecretStoreException>(() => store.DeleteOllamaWebSearchApiKey());

        backend.OverrideDeleteError = null;
        backend.OverrideGetError = errorType;
        Assert.Throws<SecretStoreException>(() => store.GetOllamaWebSearchApiKey());
    }

    [Fact]
    public void AddNightCodeForgeCoreAndInfrastructure_RegistersSecretStoreAsSingleton_PreservingGitHubAndCompanionStores()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ncf-di-secret-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var hostEnvMock = new Mock<IHostEnvironment>();
            hostEnvMock.SetupGet(e => e.ContentRootPath).Returns(tempRoot);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(hostEnvMock.Object);
            services.AddNightCodeForgeCoreAndInfrastructure($"Data Source={Path.Combine(tempRoot, "test.db")};");

            using var provider = services.BuildServiceProvider();

            var s1 = provider.GetRequiredService<IApiKeySecretStore>();
            var s2 = provider.GetRequiredService<IApiKeySecretStore>();
            Assert.IsType<KeySharpApiKeySecretStore>(s1);
            Assert.Same(s1, s2);

            Assert.IsType<GitHubTokenStore>(provider.GetRequiredService<IGitHubTokenStore>());
            Assert.NotNull(provider.GetRequiredService<IApiKeyStore>());
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
