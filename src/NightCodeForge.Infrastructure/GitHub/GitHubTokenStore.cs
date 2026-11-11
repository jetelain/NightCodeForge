using System.Text.Json;
using KeySharp;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.GitHub;

/// <summary>
/// Dedicated OS keyring-backed token store powered by <see cref="Keyring"/> (Pmad.KeySharp).
/// Persists GitHub App OAuth credentials in the native OS credential store
/// (Windows Credential Manager, macOS Keychain, Linux Secret Service).
/// </summary>
public class GitHubTokenStore : IGitHubTokenStore
{
    public const string DefaultPackage = "NightCodeForge";
    public const string DefaultService = "GitHubAppOAuth";
    public const string DefaultUsername = "oauth-token";

    private readonly string _package;
    private readonly string _service;
    private readonly string _username;

    public GitHubTokenStore()
        : this(DefaultPackage, DefaultService, DefaultUsername)
    {
    }

    public GitHubTokenStore(string package, string service, string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        _package = package;
        _service = service;
        _username = username;
    }

    public Task<GitHubOAuthTokenPayload?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var json = Keyring.GetPassword(_package, _service, _username);
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new GitHubAuthenticationException("Corrupt GitHub OAuth token payload: keyring entry is empty.");
            }

            var payload = JsonSerializer.Deserialize<GitHubOAuthTokenPayload>(json);
            if (payload == null || string.IsNullOrWhiteSpace(payload.AccessToken))
            {
                throw new GitHubAuthenticationException("Failed to deserialize GitHub OAuth token payload.");
            }

            return Task.FromResult<GitHubOAuthTokenPayload?>(payload);
        }
        catch (KeyringException ex) when (ex.Type == ErrorType.NotFound)
        {
            return Task.FromResult<GitHubOAuthTokenPayload?>(null);
        }
        catch (KeyringException ex)
        {
            throw new GitHubAuthenticationException($"Failed to retrieve stored GitHub credentials from OS keyring ({ex.Type}): {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new GitHubAuthenticationException("Corrupt GitHub OAuth token payload detected in OS keyring.", ex);
        }
    }

    public Task SaveTokenAsync(GitHubOAuthTokenPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();

        var json = JsonSerializer.Serialize(payload);

        try
        {
            Keyring.SetPassword(_package, _service, _username, json);
            return Task.CompletedTask;
        }
        catch (KeyringException ex)
        {
            throw new GitHubAuthenticationException($"Failed to store GitHub credentials in OS keyring ({ex.Type}): {ex.Message}", ex);
        }
    }

    public Task ClearTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            Keyring.DeletePassword(_package, _service, _username);
            return Task.CompletedTask;
        }
        catch (KeyringException ex) when (ex.Type == ErrorType.NotFound)
        {
            return Task.CompletedTask;
        }
        catch (KeyringException ex)
        {
            throw new GitHubAuthenticationException($"Failed to delete GitHub credentials from OS keyring ({ex.Type}): {ex.Message}", ex);
        }
    }

    public Task<bool> HasTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var json = Keyring.GetPassword(_package, _service, _username);
            return Task.FromResult(!string.IsNullOrWhiteSpace(json));
        }
        catch (KeyringException ex) when (ex.Type == ErrorType.NotFound)
        {
            return Task.FromResult(false);
        }
        catch (KeyringException ex)
        {
            throw new GitHubAuthenticationException($"Failed to check GitHub credentials in OS keyring ({ex.Type}): {ex.Message}", ex);
        }
    }
}
