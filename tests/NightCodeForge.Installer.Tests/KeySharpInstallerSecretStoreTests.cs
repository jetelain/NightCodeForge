namespace NightCodeForge.Installer.Tests;

using Moq;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;

public sealed class KeySharpInstallerSecretStoreTests : IDisposable
{
    private readonly TempTestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData("HTTPS://API.OpenAI.com:443/v1/", "https://api.openai.com/v1")]
    [InlineData("http://localhost:1234/v1/?x=1#frag", "http://localhost:1234/v1")]
    [InlineData("http://LOCALHOST:80/api/v1/", "http://localhost/api/v1")]
    public void InstallerEndpointNormalizer_CanonicalizesUrlsIdenticallyToCore(string input, string expected)
    {
        Assert.True(InstallerEndpointNormalizer.TryNormalizeEndpoint(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ftp://api.openai.com/v1")]
    [InlineData("not-a-url")]
    public void InstallerEndpointNormalizer_RejectsInvalidEndpoints(string? input)
    {
        Assert.False(InstallerEndpointNormalizer.TryNormalizeEndpoint(input, out _));
        Assert.Throws<ArgumentException>(() => InstallerEndpointNormalizer.NormalizeEndpoint(input));
    }

    [Fact]
    public void SecretStore_SetGetHasDeleteAndEnvFallback_UseCanonicalCoordinates()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        string? envKey = "sk-env-fallback";
        platform.Setup(p => p.GetEnvironmentVariable(InstallerConstants.EnvOpenAiApiKey)).Returns(() => envKey);

        var backend = new FakeKeyringBackend(available: true);
        var sut = new KeySharpInstallerSecretStore(platform.Object, backend);

        Assert.True(sut.IsKeyringAvailable());
        Assert.False(sut.HasOpenAiApiKey("https://api.openai.com/v1"));
        Assert.Equal("sk-env-fallback", sut.ResolveEffectiveOpenAiApiKey("https://api.openai.com/v1"));

        sut.SetOpenAiApiKey("HTTPS://API.OpenAI.com/v1/", "  sk-secret-123  ");
        Assert.True(sut.HasOpenAiApiKey("https://api.openai.com/v1"));
        Assert.Equal("sk-secret-123", sut.GetOpenAiApiKey("https://api.openai.com/v1"));
        Assert.Equal("sk-secret-123", sut.ResolveEffectiveOpenAiApiKey("https://api.openai.com/v1"));
        Assert.True(backend.Contains(
            InstallerConstants.KeyringPackageName,
            InstallerConstants.KeyringOpenAiCompatibleServiceName,
            "https://api.openai.com/v1"));

        sut.DeleteOpenAiApiKey("https://api.openai.com/v1/");
        Assert.False(sut.HasOpenAiApiKey("https://api.openai.com/v1"));
    }

    [Fact]
    public void SecretStore_WhenKeyringUnavailableOrControlChars_ThrowsWithoutLeakingSecret()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        var backend = new FakeKeyringBackend(available: false);

        var sut = new KeySharpInstallerSecretStore(platform.Object, backend);
        const string secret = "sk-do-not-leak-me";

        var ex = Assert.Throws<InstallerSecretStoreException>(() => sut.SetOpenAiApiKey("https://api.openai.com/v1", secret));
        Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => sut.SetOpenAiApiKey("https://api.openai.com/v1", "bad\nkey"));
    }

    [Fact]
    public void SecretStore_CachesReadsPerCanonicalEndpointAndUpdatesCacheOnMutation()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var backend = new FakeKeyringBackend(available: true);
        backend.SetPassword(
            InstallerConstants.KeyringPackageName,
            InstallerConstants.KeyringOpenAiCompatibleServiceName,
            "https://api.openai.com/v1",
            "sk-initial");

        var sut = new KeySharpInstallerSecretStore(platform.Object, backend);

        Assert.Equal("sk-initial", sut.GetOpenAiApiKey("https://api.openai.com/v1"));
        Assert.Equal("sk-initial", sut.GetOpenAiApiKey("HTTPS://API.OpenAI.com:443/v1/"));
        Assert.True(sut.HasOpenAiApiKey("https://api.openai.com/v1"));
        Assert.Equal(1, backend.GetPasswordCalls);

        Assert.False(sut.HasOpenAiApiKey("https://openrouter.ai/api/v1"));
        Assert.Null(sut.GetOpenAiApiKey("https://openrouter.ai/api/v1"));
        Assert.Equal(2, backend.GetPasswordCalls);

        sut.SetOpenAiApiKey("https://api.openai.com/v1/", "sk-rotated");
        Assert.Equal("sk-rotated", sut.GetOpenAiApiKey("https://api.openai.com/v1"));
        Assert.Equal(2, backend.GetPasswordCalls);

        sut.DeleteOpenAiApiKey("https://api.openai.com/v1");
        Assert.False(sut.HasOpenAiApiKey("https://api.openai.com/v1"));
        Assert.Equal(2, backend.GetPasswordCalls);
    }

    [Fact]
    public void SecretStore_HasEnvironmentOpenAiApiKey_ReflectsProcessEnvironment()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        string? envValue = null;
        platform.Setup(p => p.GetEnvironmentVariable(InstallerConstants.EnvOpenAiApiKey)).Returns(() => envValue);

        var sut = new KeySharpInstallerSecretStore(platform.Object, new FakeKeyringBackend(available: false));
        Assert.False(sut.HasEnvironmentOpenAiApiKey());

        envValue = "  ";
        Assert.False(sut.HasEnvironmentOpenAiApiKey());

        envValue = "sk-env";
        Assert.True(sut.HasEnvironmentOpenAiApiKey());
    }

    [Fact]
    public void SecretStore_WhenBackendReturnsUnavailableOrOtherError_UpdatesAvailabilityAndThrows()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        var backend = new FakeKeyringBackend(available: true);
        backend.ReadOverrides["https://api.openai.com/v1"] =
            new InstallerKeyringReadResult(InstallerKeyringErrorType.Unavailable, null, "locked", null);
        backend.ReadOverrides["https://openrouter.ai/api/v1"] =
            new InstallerKeyringReadResult(InstallerKeyringErrorType.OtherError, null, "dbus error", null);
        backend.NextSetResult =
            new InstallerKeyringMutationResult(InstallerKeyringErrorType.OtherError, "write denied", null);

        var sut = new KeySharpInstallerSecretStore(platform.Object, backend);
        Assert.True(sut.IsKeyringAvailable());
        Assert.Null(sut.GetOpenAiApiKey("https://api.openai.com/v1"));
        Assert.False(sut.IsKeyringAvailable());

        var writeEx = Assert.Throws<InstallerSecretStoreException>(() => sut.SetOpenAiApiKey("https://api.openai.com/v1", "sk-secret"));
        Assert.Contains("write denied", writeEx.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secret", writeEx.Message, StringComparison.Ordinal);

        var sut2 = new KeySharpInstallerSecretStore(platform.Object, backend);
        var getEx = Assert.Throws<InstallerSecretStoreException>(() => sut2.GetOpenAiApiKey("https://openrouter.ai/api/v1"));
        Assert.Contains("dbus error", getEx.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("  sk-valid-123  ", true, "sk-valid-123")]
    [InlineData(null, false, "")]
    [InlineData("   ", false, "")]
    [InlineData("bad\nkey", false, "")]
    [InlineData("sk with space", false, "")]
    [InlineData("sk-nonascii-é", false, "")]
    [InlineData("sk-comma,key", false, "")]
    public void TryValidateApiKeyHeaderValue_ValidatesStrictly(string? input, bool expectedValid, string expectedTrimmed)
    {
        var ok = KeySharpInstallerSecretStore.TryValidateApiKeyHeaderValue(input, out var trimmed);
        Assert.Equal(expectedValid, ok);
        Assert.Equal(expectedTrimmed, trimmed);
    }

    private sealed class FakeKeyringBackend(bool available) : IInstallerKeyringBackend
    {
        private readonly Dictionary<(string Package, string Service, string Username), string> _entries = new();

        public Dictionary<string, InstallerKeyringReadResult> ReadOverrides { get; } = new(StringComparer.Ordinal);

        public InstallerKeyringMutationResult? NextSetResult { get; set; }

        public int GetPasswordCalls { get; private set; }

        public bool IsAvailable() => available;

        public bool Contains(string package, string service, string username) =>
            _entries.ContainsKey((package, service, username));

        public InstallerKeyringReadResult GetPassword(string package, string service, string username)
        {
            GetPasswordCalls++;
            if (ReadOverrides.TryGetValue(username, out var overridden))
            {
                return overridden;
            }

            return _entries.TryGetValue((package, service, username), out var val)
                ? new InstallerKeyringReadResult(InstallerKeyringErrorType.NoError, val, null, null)
                : new InstallerKeyringReadResult(InstallerKeyringErrorType.NotFound, null, null, null);
        }

        public InstallerKeyringMutationResult SetPassword(string package, string service, string username, string password)
        {
            if (NextSetResult is { } next)
            {
                NextSetResult = null;
                return next;
            }

            _entries[(package, service, username)] = password;
            return new InstallerKeyringMutationResult(InstallerKeyringErrorType.NoError, null, null);
        }

        public InstallerKeyringMutationResult DeletePassword(string package, string service, string username)
        {
            _entries.Remove((package, service, username));
            return new InstallerKeyringMutationResult(InstallerKeyringErrorType.NoError, null, null);
        }
    }
}
