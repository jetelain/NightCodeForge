using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.GitHub;

/// <summary>
/// Implements GitHub App Device Flow authentication, rotating 8-hour access token lifecycle,
/// single-flight concurrency refresh lock, and authenticated user resolution.
/// </summary>
public class GitHubDeviceAuthService : IGitHubDeviceAuthService
{
    private const string DeviceCodeEndpoint = "https://github.com/login/device/code";
    private const string AccessTokenEndpoint = "https://github.com/login/oauth/access_token";
    private const string UserEndpoint = "https://api.github.com/user";
    private const string DefaultVerificationUri = "https://github.com/login/device";
    private const string GitHubApiVersion = "2022-11-28";
    private const string UserAgentName = "NightCodeForge";
    private const string UserAgentVersion = "1.0";
    private const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private const string RefreshTokenGrantType = "refresh_token";

    private const int DefaultDeviceCodeExpiresInSeconds = 900;
    private const int DefaultPollingIntervalSeconds = 5;
    private const int SlowDownIncrementSeconds = 5;
    private const int DefaultAccessTokenLifetimeSeconds = 28800;
    private const int DefaultRefreshTokenLifetimeSeconds = 15768000;
    private const int ProactiveRefreshWindowMinutes = 5;

    private readonly HttpClient _httpClient;
    private readonly IGitHubTokenStore _tokenStore;
    private readonly ILogger<GitHubDeviceAuthService> _logger;
    private static readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _lastRefreshedAccessToken;

    public GitHubDeviceAuthService(
        HttpClient httpClient,
        IGitHubTokenStore tokenStore,
        ILogger<GitHubDeviceAuthService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GitHubDeviceCodeResponse> RequestDeviceCodeAsync(
        string? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveClientId = string.IsNullOrWhiteSpace(clientId)
            ? GitHubIntegrationSettings.DefaultClientId
            : clientId.Trim();

        using var request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = effectiveClientId
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to request GitHub device code: HTTP {StatusCode}, {Content}", response.StatusCode, content);
            throw new GitHubAuthenticationException($"Failed to request GitHub device code: HTTP {response.StatusCode}.");
        }

        using var json = JsonDocument.Parse(content);
        var root = json.RootElement;

        if (root.TryGetProperty("error", out var errorEl))
        {
            var err = errorEl.GetString();
            var desc = root.TryGetProperty("error_description", out var descEl) ? descEl.GetString() : null;
            throw new GitHubAuthenticationException($"GitHub device code error: {desc ?? err}");
        }

        return new GitHubDeviceCodeResponse
        {
            DeviceCode = root.GetProperty("device_code").GetString() ?? string.Empty,
            UserCode = root.GetProperty("user_code").GetString() ?? string.Empty,
            VerificationUri = root.TryGetProperty("verification_uri", out var uriEl)
                ? uriEl.GetString() ?? DefaultVerificationUri
                : DefaultVerificationUri,
            ExpiresIn = root.TryGetProperty("expires_in", out var expEl) ? expEl.GetInt32() : DefaultDeviceCodeExpiresInSeconds,
            Interval = root.TryGetProperty("interval", out var intEl) ? Math.Max(1, intEl.GetInt32()) : DefaultPollingIntervalSeconds
        };
    }

    public async Task<GitHubOAuthTokenPayload> PollForAccessTokenAsync(
        string deviceCode,
        int interval,
        DateTimeOffset expiresAt,
        string? clientId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceCode);

        var effectiveClientId = string.IsNullOrWhiteSpace(clientId)
            ? GitHubIntegrationSettings.DefaultClientId
            : clientId.Trim();

        var pollingInterval = Math.Max(1, interval);

        while (DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(pollingInterval), cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Post, AccessTokenEndpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = effectiveClientId,
                ["device_code"] = deviceCode,
                ["grant_type"] = DeviceCodeGrantType
            });

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                {
                    _logger.LogError("GitHub token poll failed with client error: HTTP {StatusCode}, {Content}", response.StatusCode, content);
                    throw new GitHubAuthenticationException($"GitHub device flow polling failed: HTTP {response.StatusCode}.");
                }

                _logger.LogWarning("GitHub token poll returned transient server error: HTTP {StatusCode}", response.StatusCode);
                continue;
            }

            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;

            if (root.TryGetProperty("error", out var errorEl))
            {
                var error = errorEl.GetString();
                var desc = root.TryGetProperty("error_description", out var descEl) ? descEl.GetString() : null;

                switch (error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        if (root.TryGetProperty("interval", out var newIntEl) && newIntEl.ValueKind == JsonValueKind.Number)
                        {
                            pollingInterval = Math.Max(pollingInterval + SlowDownIncrementSeconds, newIntEl.GetInt32());
                        }
                        else
                        {
                            pollingInterval += SlowDownIncrementSeconds;
                        }
                        continue;
                    case "expired_token":
                        throw new GitHubAuthenticationException("The device activation code has expired. Please initiate authentication again.");
                    case "access_denied":
                        throw new GitHubAuthenticationException("GitHub authorization was denied by the user.");
                    default:
                        throw new GitHubAuthenticationException($"GitHub authorization failed: {desc ?? error}");
                }
            }

            if (root.TryGetProperty("access_token", out var accessTokenEl))
            {
                var accessToken = accessTokenEl.GetString();
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    throw new GitHubAuthenticationException("GitHub returned empty access token.");
                }

                var expiresInSeconds = root.TryGetProperty("expires_in", out var expEl) && expEl.ValueKind == JsonValueKind.Number
                    ? expEl.GetInt32()
                    : DefaultAccessTokenLifetimeSeconds;
                var refreshToken = root.TryGetProperty("refresh_token", out var refEl) ? refEl.GetString() : null;
                var refreshExpiresInSeconds = root.TryGetProperty("refresh_token_expires_in", out var refExpEl) && refExpEl.ValueKind == JsonValueKind.Number
                    ? refExpEl.GetInt32()
                    : DefaultRefreshTokenLifetimeSeconds;
                var tokenType = root.TryGetProperty("token_type", out var typeEl) ? typeEl.GetString() : "bearer";
                var scope = root.TryGetProperty("scope", out var scEl) ? scEl.GetString() : null;

                var payload = new GitHubOAuthTokenPayload
                {
                    AccessToken = accessToken,
                    AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds),
                    RefreshToken = refreshToken,
                    RefreshTokenExpiresAtUtc = !string.IsNullOrWhiteSpace(refreshToken)
                        ? DateTimeOffset.UtcNow.AddSeconds(refreshExpiresInSeconds)
                        : null,
                    TokenType = tokenType,
                    Scope = scope,
                    ClientId = effectiveClientId
                };

                // Fetch authenticated user profile to store login identity
                try
                {
                    var (user, _) = await FetchUserWithTokenAsync(accessToken, cancellationToken);
                    if (user != null)
                    {
                        payload.AuthenticatedUserLogin = user.Login;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to resolve authenticated user profile during device flow authorization.");
                }

                await _tokenStore.SaveTokenAsync(payload, cancellationToken);
                _logger.LogInformation("Successfully authenticated GitHub user '{Login}'.", payload.AuthenticatedUserLogin ?? "unknown");
                return payload;
            }
        }

        throw new GitHubAuthenticationException("Device activation timed out before authorization was completed.");
    }

    public async Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var token = await _tokenStore.GetTokenAsync(cancellationToken);
        if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new GitHubAuthenticationException("GitHub is not connected. Please authenticate in Settings.");
        }

        // Proactive refresh window: within 5 minutes of expiration
        if (token.AccessTokenExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(ProactiveRefreshWindowMinutes))
        {
            var refreshed = await RefreshAccessTokenAsync(cancellationToken);
            return refreshed.AccessToken;
        }

        return token.AccessToken;
    }

    public async Task<GitHubOAuthTokenPayload> RefreshAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var tokenBeforeLock = await _tokenStore.GetTokenAsync(cancellationToken);

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check token under lock to avoid racing refresh calls
            var currentToken = await _tokenStore.GetTokenAsync(cancellationToken);
            if (currentToken == null || string.IsNullOrWhiteSpace(currentToken.AccessToken))
            {
                throw new GitHubAuthenticationException("GitHub is not connected. Please authenticate in Settings.");
            }

            var refreshedWhileWaiting = tokenBeforeLock != null &&
                !string.Equals(currentToken.AccessToken, tokenBeforeLock.AccessToken, StringComparison.Ordinal);
            var alreadyRefreshedByInstance = string.Equals(currentToken.AccessToken, _lastRefreshedAccessToken, StringComparison.Ordinal);

            // If another caller already rotated the token while we were waiting on the lock
            // and the new token is outside the proactive refresh window, return the newly rotated token.
            if (currentToken.AccessTokenExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(ProactiveRefreshWindowMinutes) &&
                (refreshedWhileWaiting || alreadyRefreshedByInstance))
            {
                return currentToken;
            }

            if (string.IsNullOrWhiteSpace(currentToken.RefreshToken))
            {
                throw new GitHubAuthenticationException("No GitHub refresh token available. Please re-authenticate in Settings.");
            }

            if (currentToken.RefreshTokenExpiresAtUtc.HasValue && currentToken.RefreshTokenExpiresAtUtc.Value <= DateTimeOffset.UtcNow)
            {
                throw new GitHubAuthenticationException("GitHub refresh token has expired. Please re-authenticate in Settings.");
            }

            var effectiveClientId = string.IsNullOrWhiteSpace(currentToken.ClientId)
                ? GitHubIntegrationSettings.DefaultClientId
                : currentToken.ClientId;

            _logger.LogInformation("Refreshing GitHub user access token using rotating refresh token.");

            using var request = new HttpRequestMessage(HttpMethod.Post, AccessTokenEndpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = effectiveClientId,
                ["grant_type"] = RefreshTokenGrantType,
                ["refresh_token"] = currentToken.RefreshToken
            });

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to refresh GitHub token: HTTP {StatusCode}, {Content}", response.StatusCode, content);
                throw new GitHubAuthenticationException($"GitHub token refresh failed: HTTP {response.StatusCode}. Please re-authenticate in Settings.");
            }

            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;

            if (root.TryGetProperty("error", out var errorEl))
            {
                var error = errorEl.GetString();
                var desc = root.TryGetProperty("error_description", out var descEl) ? descEl.GetString() : null;
                _logger.LogError("GitHub refresh returned error: {Error}, {Desc}", error, desc);
                throw new GitHubAuthenticationException($"GitHub token refresh rejected ({error}): {desc ?? "Please re-authenticate in Settings."}");
            }

            var newAccessToken = root.TryGetProperty("access_token", out var newAccessEl) ? newAccessEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(newAccessToken))
            {
                throw new GitHubAuthenticationException("GitHub refresh response missing access_token.");
            }

            var expiresInSeconds = root.TryGetProperty("expires_in", out var expEl) && expEl.ValueKind == JsonValueKind.Number
                ? expEl.GetInt32()
                : DefaultAccessTokenLifetimeSeconds;
            var newRefreshToken = root.TryGetProperty("refresh_token", out var refEl) ? refEl.GetString() : null;
            var refreshExpiresInSeconds = root.TryGetProperty("refresh_token_expires_in", out var refExpEl) && refExpEl.ValueKind == JsonValueKind.Number
                ? refExpEl.GetInt32()
                : DefaultRefreshTokenLifetimeSeconds;

            var updatedToken = new GitHubOAuthTokenPayload
            {
                AccessToken = newAccessToken,
                AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds),
                RefreshToken = !string.IsNullOrWhiteSpace(newRefreshToken) ? newRefreshToken : currentToken.RefreshToken,
                RefreshTokenExpiresAtUtc = !string.IsNullOrWhiteSpace(newRefreshToken)
                    ? DateTimeOffset.UtcNow.AddSeconds(refreshExpiresInSeconds)
                    : currentToken.RefreshTokenExpiresAtUtc,
                TokenType = currentToken.TokenType,
                Scope = currentToken.Scope,
                AuthenticatedUserLogin = currentToken.AuthenticatedUserLogin,
                ClientId = effectiveClientId
            };

            await _tokenStore.SaveTokenAsync(updatedToken, cancellationToken);
            _lastRefreshedAccessToken = updatedToken.AccessToken;
            _logger.LogInformation("Successfully rotated and refreshed GitHub access token.");
            return updatedToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<GitHubUser?> GetAuthenticatedUserAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await _tokenStore.GetTokenAsync(cancellationToken);
            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                return null;
            }

            var validToken = await GetValidAccessTokenAsync(cancellationToken);
            var (user, statusCode) = await FetchUserWithTokenAsync(validToken, cancellationToken);

            if (statusCode == HttpStatusCode.Unauthorized)
            {
                var refreshed = await RefreshAccessTokenAsync(cancellationToken);
                (user, _) = await FetchUserWithTokenAsync(refreshed.AccessToken, cancellationToken);
            }

            return user;
        }
        catch (GitHubAuthenticationException)
        {
            return null;
        }
    }

    public async Task<bool> IsConnectedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await _tokenStore.GetTokenAsync(cancellationToken);
            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                return false;
            }

            if (token.AccessTokenExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return true;
            }

            // Access token expired, check if refresh token is valid
            if (!string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                if (!token.RefreshTokenExpiresAtUtc.HasValue || token.RefreshTokenExpiresAtUtc.Value > DateTimeOffset.UtcNow)
                {
                    return true;
                }
            }

            return false;
        }
        catch (GitHubAuthenticationException)
        {
            return false;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _lastRefreshedAccessToken = null;
        await _tokenStore.ClearTokenAsync(cancellationToken);
        _logger.LogInformation("Disconnected GitHub integration and cleared stored token.");
    }

    private async Task<(GitHubUser? User, HttpStatusCode StatusCode)> FetchUserWithTokenAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UserEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", GitHubApiVersion);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(UserAgentName, UserAgentVersion));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return (null, response.StatusCode);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(content);
        var root = json.RootElement;

        var user = new GitHubUser
        {
            Id = root.GetProperty("id").GetInt64(),
            Login = root.GetProperty("login").GetString() ?? string.Empty,
            Name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null,
            Email = root.TryGetProperty("email", out var emailEl) ? emailEl.GetString() : null,
            AvatarUrl = root.TryGetProperty("avatar_url", out var avEl) ? avEl.GetString() : null
        };

        return (user, response.StatusCode);
    }
}
