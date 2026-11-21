namespace NightCodeForge.Core.Security;

/// <summary>
/// Identifies which external service credential changed in <see cref="Interfaces.IApiKeySecretStore"/>.
/// </summary>
public enum ApiKeySecretKind
{
    OpenAiCompatible = 0,
    OllamaWebSearch = 1
}

/// <summary>
/// Describes the effective resolution source of a service API key for UI status badges.
/// </summary>
public enum ApiKeyCredentialSource
{
    NotConfigured = 0,
    OsKeyring = 1,
    EnvironmentVariable = 2,
    KeyringUnavailable = 3,
    KeyringError = 4
}

/// <summary>
/// Event payload emitted when an API key secret is created, rotated, or deleted in the secret store.
/// Contains only credential kind and canonicalized endpoint—never raw secret values.
/// </summary>
public sealed record ApiKeySecretChangedEventArgs(ApiKeySecretKind Kind, string? NormalizedEndpoint);
