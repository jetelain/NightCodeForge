using KeySharp;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Infrastructure.GitHub;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.GitHub;

public class GitHubTokenStoreTests : IDisposable
{
    private const string TestPackage = "NightCodeForge.Tests";
    private const string TestService = "GitHubAppOAuth";
    private readonly string _testUsername;

    public GitHubTokenStoreTests()
    {
        _testUsername = $"test-token-{Guid.NewGuid():N}";
    }

    public void Dispose()
    {
        try
        {
            Keyring.DeletePassword(TestPackage, TestService, _testUsername);
        }
        catch (KeyringException ex) when (ex.Type == ErrorType.NotFound)
        {
            // Entry already deleted or never created
        }
    }

    [Fact]
    public async Task GetTokenAsync_WhenEntryDoesNotExist_ReturnsNull()
    {
        var store = new GitHubTokenStore(TestPackage, TestService, _testUsername);

        var token = await store.GetTokenAsync();

        Assert.Null(token);
        Assert.False(await store.HasTokenAsync());
    }

    [Fact]
    public async Task SaveTokenAsync_And_GetTokenAsync_RoundTripsPayloadInOsKeyring()
    {
        var store = new GitHubTokenStore(TestPackage, TestService, _testUsername);
        var payload = new GitHubOAuthTokenPayload
        {
            AccessToken = "ghu_1234567890abcdef",
            AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(8),
            RefreshToken = "ghr_0987654321fedcba",
            RefreshTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddMonths(6),
            TokenType = "bearer",
            Scope = "",
            AuthenticatedUserLogin = "test-developer",
            ClientId = "Iv23customClient"
        };

        await store.SaveTokenAsync(payload);

        Assert.True(await store.HasTokenAsync());

        // Verify directly via KeySharp that the entry was persisted in the OS keyring
        var rawStoredJson = Keyring.GetPassword(TestPackage, TestService, _testUsername);
        Assert.Contains("ghu_1234567890abcdef", rawStoredJson);
        Assert.Contains("ghr_0987654321fedcba", rawStoredJson);

        var loaded = await store.GetTokenAsync();

        Assert.NotNull(loaded);
        Assert.Equal(payload.AccessToken, loaded.AccessToken);
        Assert.Equal(payload.RefreshToken, loaded.RefreshToken);
        Assert.Equal(payload.TokenType, loaded.TokenType);
        Assert.Equal(payload.AuthenticatedUserLogin, loaded.AuthenticatedUserLogin);
        Assert.Equal(payload.ClientId, loaded.ClientId);
        Assert.Equal(payload.AccessTokenExpiresAtUtc.ToUnixTimeSeconds(), loaded.AccessTokenExpiresAtUtc.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task GetTokenAsync_WhenStoredJsonIsCorrupted_ThrowsGitHubAuthenticationException()
    {
        var store = new GitHubTokenStore(TestPackage, TestService, _testUsername);
        Keyring.SetPassword(TestPackage, TestService, _testUsername, "{not-valid-json");

        await Assert.ThrowsAsync<GitHubAuthenticationException>(() => store.GetTokenAsync());
    }

    [Fact]
    public async Task GetTokenAsync_WhenStoredPayloadHasEmptyAccessToken_ThrowsGitHubAuthenticationException()
    {
        var store = new GitHubTokenStore(TestPackage, TestService, _testUsername);
        Keyring.SetPassword(TestPackage, TestService, _testUsername, "{\"AccessToken\":\"\"}");

        await Assert.ThrowsAsync<GitHubAuthenticationException>(() => store.GetTokenAsync());
    }

    [Fact]
    public async Task ClearTokenAsync_DeletesStoredCredentialFromKeyring()
    {
        var store = new GitHubTokenStore(TestPackage, TestService, _testUsername);
        var payload = new GitHubOAuthTokenPayload
        {
            AccessToken = "ghu_test",
            RefreshToken = "ghr_test"
        };

        await store.SaveTokenAsync(payload);
        Assert.True(await store.HasTokenAsync());

        await store.ClearTokenAsync();

        Assert.False(await store.HasTokenAsync());
        Assert.Null(await store.GetTokenAsync());

        // Calling ClearTokenAsync again when entry is already gone should be idempotent
        await store.ClearTokenAsync();
    }
}
