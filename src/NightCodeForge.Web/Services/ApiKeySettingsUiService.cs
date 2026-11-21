using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Security;

namespace NightCodeForge.Web.Services;

public enum ApiKeySaveAction
{
    PreservedExisting = 0,
    NoKeyProvided = 1,
    SavedToKeyring = 2
}

public sealed record ApiKeyCredentialBadgeViewModel(
    ApiKeyCredentialSource Source,
    string BadgeText,
    string BadgeCssClass,
    string InputPlaceholder,
    string StorageNoticeText,
    bool HasKeyringEntry,
    bool HasEnvironmentFallback,
    bool IsKeyringAvailable,
    string? NormalizedEndpoint);

public sealed record ApiKeySaveOutcome(
    ApiKeySaveAction Action,
    ApiKeyCredentialBadgeViewModel UpdatedStatus);

public interface IApiKeySettingsUiService
{
    ApiKeyCredentialBadgeViewModel GetOpenAiCredentialStatus(string? endpoint);
    ApiKeyCredentialBadgeViewModel GetOllamaWebSearchCredentialStatus();
    ApiKeyCredentialBadgeViewModel CreateOpenAiKeyringErrorStatus(string? endpoint);
    ApiKeyCredentialBadgeViewModel CreateOllamaWebSearchKeyringErrorStatus();
    ApiKeySaveOutcome ApplyOpenAiApiKeyInput(string endpoint, string? pendingApiKeyInput);
    ApiKeyCredentialBadgeViewModel ClearOpenAiApiKey(string endpoint);
    ApiKeySaveOutcome ApplyOllamaWebSearchApiKeyInput(string? pendingApiKeyInput);
    ApiKeyCredentialBadgeViewModel ClearOllamaWebSearchApiKey();
    bool CanTestWebSearchConnection(bool isAirGapped, ApiKeyCredentialBadgeViewModel status, string? pendingApiKeyInput);
}

/// <summary>
/// Presentation and orchestration helper isolating credential source resolution, badge metadata,
/// endpoint normalization, and write-only save/clear semantics from Blazor Razor components.
/// </summary>
public sealed class ApiKeySettingsUiService : IApiKeySettingsUiService
{
    public const string StoredInKeyringBadgeText = "🔐 Stored in OS Keyring";
    public const string InheritedFromOpenAiEnvBadgeText = $"🌿 Inherited from {ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable}";
    public const string InheritedFromOllamaEnvBadgeText = $"🌿 Inherited from {ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable}";
    public const string KeyringUnavailableBadgeText = "⚠️ OS Keyring Unavailable";
    public const string KeyringErrorBadgeText = "⚠️ OS Keyring Error";
    public const string NotConfiguredBadgeText = "⚪ Not Configured";

    public const string OpenAiStorageNotice =
        "API keys are persisted securely in your operating system keyring via Pmad.KeySharp (scoped per endpoint URL)—never in config/config.json.";

    public const string OllamaStorageNotice =
        "API keys are persisted securely in your operating system keyring via Pmad.KeySharp—never in SQLite.";

    private const string KeyringStoredPlaceholder =
        "Key stored in OS keyring — leave blank to preserve existing key, or paste a new key to replace";

    private const string OpenAiEnvOverridePlaceholder =
        $"Inherited from {ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable} — paste a key to store an override in OS keyring";

    private const string OllamaEnvOverridePlaceholder =
        $"Inherited from {ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable} — paste a key to store an override in OS keyring";

    public const string OpenAiEnvKeyringUnavailablePlaceholder =
        $"Inherited from {ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable} — OS keyring unavailable on this host";

    public const string OllamaEnvKeyringUnavailablePlaceholder =
        $"Inherited from {ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable} — OS keyring unavailable on this host";

    private const string OpenAiKeyringUnavailablePlaceholder =
        $"OS keyring unavailable — set {ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable} in your process environment";

    private const string OllamaKeyringUnavailablePlaceholder =
        $"OS keyring unavailable — set {ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable} in your process environment";

    public const string KeyringErrorPlaceholder =
        "OS keyring access failed — check keyring permissions or use environment variables";

    private const string OpenAiNotConfiguredPlaceholder =
        "sk-... (leave empty for local unauthenticated runtimes)";

    private const string OllamaNotConfiguredPlaceholder =
        "Enter Ollama Cloud API Key...";

    private readonly IApiKeySecretStore _secretStore;
    private readonly Func<string, string?> _getEnvironmentVariable;

    [ActivatorUtilitiesConstructor]
    public ApiKeySettingsUiService(IApiKeySecretStore secretStore)
        : this(secretStore, Environment.GetEnvironmentVariable)
    {
    }

    internal ApiKeySettingsUiService(
        IApiKeySecretStore secretStore,
        Func<string, string?> getEnvironmentVariable)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _getEnvironmentVariable = getEnvironmentVariable ?? throw new ArgumentNullException(nameof(getEnvironmentVariable));
    }

    public ApiKeyCredentialBadgeViewModel GetOpenAiCredentialStatus(string? endpoint)
    {
        var isKeyringAvailable = _secretStore.IsKeyringAvailable();
        var hasValidEndpoint = ApiKeyEndpointNormalizer.TryNormalizeEndpoint(endpoint, out var normalizedEndpoint);
        var hasKeyringEntry = isKeyringAvailable && hasValidEndpoint && _secretStore.HasOpenAiApiKey(normalizedEndpoint);
        isKeyringAvailable = _secretStore.IsKeyringAvailable();
        var hasEnvFallback = !string.IsNullOrWhiteSpace(_getEnvironmentVariable(ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable));

        return BuildBadgeViewModel(
            hasKeyringEntry: hasKeyringEntry,
            hasEnvFallback: hasEnvFallback,
            isKeyringAvailable: isKeyringAvailable,
            normalizedEndpoint: normalizedEndpoint,
            inheritedEnvBadgeText: InheritedFromOpenAiEnvBadgeText,
            envOverridePlaceholder: OpenAiEnvOverridePlaceholder,
            envUnavailablePlaceholder: OpenAiEnvKeyringUnavailablePlaceholder,
            unavailablePlaceholder: OpenAiKeyringUnavailablePlaceholder,
            notConfiguredPlaceholder: OpenAiNotConfiguredPlaceholder,
            storageNoticeText: OpenAiStorageNotice);
    }

    public ApiKeyCredentialBadgeViewModel GetOllamaWebSearchCredentialStatus()
    {
        var isKeyringAvailable = _secretStore.IsKeyringAvailable();
        var hasKeyringEntry = isKeyringAvailable && _secretStore.HasOllamaWebSearchApiKey();
        isKeyringAvailable = _secretStore.IsKeyringAvailable();
        var hasEnvFallback = !string.IsNullOrWhiteSpace(_getEnvironmentVariable(ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable));

        return BuildBadgeViewModel(
            hasKeyringEntry: hasKeyringEntry,
            hasEnvFallback: hasEnvFallback,
            isKeyringAvailable: isKeyringAvailable,
            normalizedEndpoint: null,
            inheritedEnvBadgeText: InheritedFromOllamaEnvBadgeText,
            envOverridePlaceholder: OllamaEnvOverridePlaceholder,
            envUnavailablePlaceholder: OllamaEnvKeyringUnavailablePlaceholder,
            unavailablePlaceholder: OllamaKeyringUnavailablePlaceholder,
            notConfiguredPlaceholder: OllamaNotConfiguredPlaceholder,
            storageNoticeText: OllamaStorageNotice);
    }

    public ApiKeyCredentialBadgeViewModel CreateOpenAiKeyringErrorStatus(string? endpoint)
    {
        ApiKeyEndpointNormalizer.TryNormalizeEndpoint(endpoint, out var normalizedEndpoint);
        var hasEnvFallback = !string.IsNullOrWhiteSpace(_getEnvironmentVariable(ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable));
        return BuildKeyringErrorViewModel(hasEnvFallback, normalizedEndpoint, OpenAiStorageNotice);
    }

    public ApiKeyCredentialBadgeViewModel CreateOllamaWebSearchKeyringErrorStatus()
    {
        var hasEnvFallback = !string.IsNullOrWhiteSpace(_getEnvironmentVariable(ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable));
        return BuildKeyringErrorViewModel(hasEnvFallback, normalizedEndpoint: null, OllamaStorageNotice);
    }

    public ApiKeySaveOutcome ApplyOpenAiApiKeyInput(string endpoint, string? pendingApiKeyInput)
    {
        var normalizedEndpoint = ApiKeyEndpointNormalizer.NormalizeEndpoint(endpoint);
        if (!string.IsNullOrWhiteSpace(pendingApiKeyInput))
        {
            _secretStore.SetOpenAiApiKey(normalizedEndpoint, pendingApiKeyInput.Trim());
            return new ApiKeySaveOutcome(ApiKeySaveAction.SavedToKeyring, GetOpenAiCredentialStatus(normalizedEndpoint));
        }

        var status = GetOpenAiCredentialStatus(normalizedEndpoint);
        var action = status.HasKeyringEntry ? ApiKeySaveAction.PreservedExisting : ApiKeySaveAction.NoKeyProvided;
        return new ApiKeySaveOutcome(action, status);
    }

    public ApiKeyCredentialBadgeViewModel ClearOpenAiApiKey(string endpoint)
    {
        var normalizedEndpoint = ApiKeyEndpointNormalizer.NormalizeEndpoint(endpoint);
        _secretStore.DeleteOpenAiApiKey(normalizedEndpoint);
        return GetOpenAiCredentialStatus(normalizedEndpoint);
    }

    public ApiKeySaveOutcome ApplyOllamaWebSearchApiKeyInput(string? pendingApiKeyInput)
    {
        if (!string.IsNullOrWhiteSpace(pendingApiKeyInput))
        {
            _secretStore.SetOllamaWebSearchApiKey(pendingApiKeyInput.Trim());
            return new ApiKeySaveOutcome(ApiKeySaveAction.SavedToKeyring, GetOllamaWebSearchCredentialStatus());
        }

        var status = GetOllamaWebSearchCredentialStatus();
        var action = status.HasKeyringEntry ? ApiKeySaveAction.PreservedExisting : ApiKeySaveAction.NoKeyProvided;
        return new ApiKeySaveOutcome(action, status);
    }

    public ApiKeyCredentialBadgeViewModel ClearOllamaWebSearchApiKey()
    {
        _secretStore.DeleteOllamaWebSearchApiKey();
        return GetOllamaWebSearchCredentialStatus();
    }

    public bool CanTestWebSearchConnection(
        bool isAirGapped,
        ApiKeyCredentialBadgeViewModel status,
        string? pendingApiKeyInput)
    {
        ArgumentNullException.ThrowIfNull(status);
        return !isAirGapped &&
               (status.HasKeyringEntry || status.HasEnvironmentFallback || !string.IsNullOrWhiteSpace(pendingApiKeyInput));
    }

    private static ApiKeyCredentialBadgeViewModel BuildBadgeViewModel(
        bool hasKeyringEntry,
        bool hasEnvFallback,
        bool isKeyringAvailable,
        string? normalizedEndpoint,
        string inheritedEnvBadgeText,
        string envOverridePlaceholder,
        string envUnavailablePlaceholder,
        string unavailablePlaceholder,
        string notConfiguredPlaceholder,
        string storageNoticeText)
    {
        if (hasKeyringEntry)
        {
            return new ApiKeyCredentialBadgeViewModel(
                Source: ApiKeyCredentialSource.OsKeyring,
                BadgeText: StoredInKeyringBadgeText,
                BadgeCssClass: "badge-teal",
                InputPlaceholder: KeyringStoredPlaceholder,
                StorageNoticeText: storageNoticeText,
                HasKeyringEntry: true,
                HasEnvironmentFallback: hasEnvFallback,
                IsKeyringAvailable: isKeyringAvailable,
                NormalizedEndpoint: normalizedEndpoint);
        }

        if (hasEnvFallback)
        {
            return new ApiKeyCredentialBadgeViewModel(
                Source: ApiKeyCredentialSource.EnvironmentVariable,
                BadgeText: inheritedEnvBadgeText,
                BadgeCssClass: "badge-indigo",
                InputPlaceholder: isKeyringAvailable ? envOverridePlaceholder : envUnavailablePlaceholder,
                StorageNoticeText: storageNoticeText,
                HasKeyringEntry: false,
                HasEnvironmentFallback: true,
                IsKeyringAvailable: isKeyringAvailable,
                NormalizedEndpoint: normalizedEndpoint);
        }

        if (!isKeyringAvailable)
        {
            return new ApiKeyCredentialBadgeViewModel(
                Source: ApiKeyCredentialSource.KeyringUnavailable,
                BadgeText: KeyringUnavailableBadgeText,
                BadgeCssClass: "badge-amber",
                InputPlaceholder: unavailablePlaceholder,
                StorageNoticeText: storageNoticeText,
                HasKeyringEntry: false,
                HasEnvironmentFallback: false,
                IsKeyringAvailable: false,
                NormalizedEndpoint: normalizedEndpoint);
        }

        return new ApiKeyCredentialBadgeViewModel(
            Source: ApiKeyCredentialSource.NotConfigured,
            BadgeText: NotConfiguredBadgeText,
            BadgeCssClass: "badge-gray",
            InputPlaceholder: notConfiguredPlaceholder,
            StorageNoticeText: storageNoticeText,
            HasKeyringEntry: false,
            HasEnvironmentFallback: false,
            IsKeyringAvailable: true,
            NormalizedEndpoint: normalizedEndpoint);
    }

    private static ApiKeyCredentialBadgeViewModel BuildKeyringErrorViewModel(
        bool hasEnvFallback,
        string? normalizedEndpoint,
        string storageNoticeText)
    {
        return new ApiKeyCredentialBadgeViewModel(
            Source: ApiKeyCredentialSource.KeyringError,
            BadgeText: KeyringErrorBadgeText,
            BadgeCssClass: "badge-crimson",
            InputPlaceholder: KeyringErrorPlaceholder,
            StorageNoticeText: storageNoticeText,
            HasKeyringEntry: false,
            HasEnvironmentFallback: hasEnvFallback,
            IsKeyringAvailable: false,
            NormalizedEndpoint: normalizedEndpoint);
    }
}
