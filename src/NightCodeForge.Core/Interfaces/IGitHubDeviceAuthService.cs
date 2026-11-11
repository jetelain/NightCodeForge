using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Service managing the GitHub App Device Flow authentication, short-lived 8-hour access token lifecycle,
/// single-flight refresh token rotation, and credential persistence.
/// </summary>
public interface IGitHubDeviceAuthService
{
    /// <summary>
    /// Initiates GitHub App Device Flow and requests verification codes.
    /// </summary>
    Task<GitHubDeviceCodeResponse> RequestDeviceCodeAsync(string? clientId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Polls GitHub for token issuance while developer completes browser authorization.
    /// Upon authorization, populates authenticated user metadata and stores token atomically.
    /// </summary>
    Task<GitHubOAuthTokenPayload> PollForAccessTokenAsync(
        string deviceCode,
        int interval,
        DateTimeOffset expiresAt,
        string? clientId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a valid, non-expired access token. Automatically refreshes expiring tokens
    /// (proactively within 5 minutes of expiration) serialized via a single-flight concurrency lock.
    /// </summary>
    Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces a refresh of the access token using the rotating refresh token under a concurrency lock.
    /// </summary>
    Task<GitHubOAuthTokenPayload> RefreshAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the authenticated GitHub user profile, or null if not authenticated.
    /// </summary>
    Task<GitHubUser?> GetAuthenticatedUserAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether the integration is currently authenticated with a valid or refreshable token.
    /// </summary>
    Task<bool> IsConnectedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the stored credentials.
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

