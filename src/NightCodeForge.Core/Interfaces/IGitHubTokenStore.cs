using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Dedicated store for GitHub App OAuth token payloads.
/// Persists credentials in the native OS keyring via Pmad.KeySharp (Windows Credential Manager, macOS Keychain, Linux Secret Service).
/// Tokens are never stored in SQLite or application configuration.
/// </summary>
public interface IGitHubTokenStore
{
    /// <summary>
    /// Loads the stored OAuth token payload from the OS keyring.
    /// Returns null if no token is stored.
    /// </summary>
    Task<GitHubOAuthTokenPayload?> GetTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes and saves the token payload into the OS keyring.
    /// </summary>
    Task SaveTokenAsync(GitHubOAuthTokenPayload payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the stored token entry from the OS keyring to cleanly disconnect the GitHub integration.
    /// </summary>
    Task ClearTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a stored token entry currently exists in the OS keyring.
    /// </summary>
    Task<bool> HasTokenAsync(CancellationToken cancellationToken = default);
}

