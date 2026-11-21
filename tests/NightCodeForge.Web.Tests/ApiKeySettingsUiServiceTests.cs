using NightCodeForge.Core.Security;
using NightCodeForge.Web.Services;
using NightCodeForge.Web.Tests.Fixtures;

namespace NightCodeForge.Web.Tests;

public class ApiKeySettingsUiServiceTests
{
    private static ApiKeySettingsUiService CreateUiService(
        FakeApiKeySecretStore store,
        Dictionary<string, string?>? envVars = null)
    {
        var effectiveEnv = envVars ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        return new ApiKeySettingsUiService(
            store,
            name => effectiveEnv.TryGetValue(name, out var value) ? value : null);
    }

    [Fact]
    public void GetOpenAiCredentialStatus_ResolvesAllBadgeStatesAndCanonicalizesEndpoint()
    {
        var store = new FakeApiKeySecretStore();
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        var ui = CreateUiService(store, env);

        var notConfigured = ui.GetOpenAiCredentialStatus("https://api.openai.com/v1");
        Assert.Equal(ApiKeyCredentialSource.NotConfigured, notConfigured.Source);
        Assert.Equal(ApiKeySettingsUiService.NotConfiguredBadgeText, notConfigured.BadgeText);
        Assert.Contains("Pmad.KeySharp", notConfigured.StorageNoticeText);
        Assert.Contains("config/config.json", notConfigured.StorageNoticeText);

        env[ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable] = "sk-env-123";
        var fromEnv = ui.GetOpenAiCredentialStatus("https://api.openai.com/v1");
        Assert.Equal(ApiKeyCredentialSource.EnvironmentVariable, fromEnv.Source);
        Assert.Equal(ApiKeySettingsUiService.InheritedFromOpenAiEnvBadgeText, fromEnv.BadgeText);
        Assert.True(fromEnv.HasEnvironmentFallback);
        Assert.False(fromEnv.HasKeyringEntry);

        store.SeedOpenAiKey("https://api.openai.com/v1", "sk-keyring-456");
        var fromKeyring = ui.GetOpenAiCredentialStatus("  HTTPS://API.OPENAI.COM:443/v1/ ");
        Assert.Equal(ApiKeyCredentialSource.OsKeyring, fromKeyring.Source);
        Assert.Equal(ApiKeySettingsUiService.StoredInKeyringBadgeText, fromKeyring.BadgeText);
        Assert.Equal("https://api.openai.com/v1", fromKeyring.NormalizedEndpoint);
        Assert.True(fromKeyring.HasKeyringEntry);

        store.KeyringAvailable = false;
        env.Remove(ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable);
        var unavailable = ui.GetOpenAiCredentialStatus("https://api.openai.com/v1");
        Assert.Equal(ApiKeyCredentialSource.KeyringUnavailable, unavailable.Source);
        Assert.Equal(ApiKeySettingsUiService.KeyringUnavailableBadgeText, unavailable.BadgeText);
    }

    [Fact]
    public void ApplyOpenAiApiKeyInput_And_ClearOpenAiApiKey_FollowPreserveReplaceAndClearSemantics()
    {
        var store = new FakeApiKeySecretStore();
        var ui = CreateUiService(store);
        const string endpoint = "https://openrouter.ai/api/v1";

        // Save blank when empty -> NoKeyProvided
        var blankInitial = ui.ApplyOpenAiApiKeyInput(endpoint, "   ");
        Assert.Equal(ApiKeySaveAction.NoKeyProvided, blankInitial.Action);
        Assert.Equal(0, store.SetOpenAiCalls);

        // Save non-empty -> trims and saves
        var saved = ui.ApplyOpenAiApiKeyInput(endpoint, "  sk-or-v1  ");
        Assert.Equal(ApiKeySaveAction.SavedToKeyring, saved.Action);
        Assert.Equal("sk-or-v1", store.PeekRawOpenAiKey(endpoint));
        Assert.Equal(ApiKeyCredentialSource.OsKeyring, saved.UpdatedStatus.Source);

        // Save blank when existing -> preserves without overwrite/delete
        var preserved = ui.ApplyOpenAiApiKeyInput(endpoint, "");
        Assert.Equal(ApiKeySaveAction.PreservedExisting, preserved.Action);
        Assert.Equal("sk-or-v1", store.PeekRawOpenAiKey(endpoint));
        Assert.Equal(1, store.SetOpenAiCalls);
        Assert.Equal(0, store.DeleteOpenAiCalls);

        // Clear -> deletes from keyring
        var afterClear = ui.ClearOpenAiApiKey(endpoint);
        Assert.Null(store.PeekRawOpenAiKey(endpoint));
        Assert.Equal(ApiKeyCredentialSource.NotConfigured, afterClear.Source);
        Assert.Equal(1, store.DeleteOpenAiCalls);
    }

    [Fact]
    public void ApplyOpenAiApiKeyInput_InvalidEndpoint_ThrowsArgumentException()
    {
        var store = new FakeApiKeySecretStore();
        var ui = CreateUiService(store);

        Assert.Throws<ArgumentException>(() => ui.ApplyOpenAiApiKeyInput("not-a-url", "sk-123"));
        Assert.Throws<ArgumentException>(() => ui.ClearOpenAiApiKey("   "));
    }

    [Fact]
    public void OllamaWebSearch_StatusSaveClearAndTestGating_BehaveAsSpecified()
    {
        var store = new FakeApiKeySecretStore();
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        var ui = CreateUiService(store, env);

        var initial = ui.GetOllamaWebSearchCredentialStatus();
        Assert.Equal(ApiKeyCredentialSource.NotConfigured, initial.Source);
        Assert.Contains("Pmad.KeySharp", initial.StorageNoticeText);
        Assert.Contains("SQLite", initial.StorageNoticeText);
        Assert.False(ui.CanTestWebSearchConnection(isAirGapped: false, initial, null));
        Assert.True(ui.CanTestWebSearchConnection(isAirGapped: false, initial, "ollama-pending"));
        Assert.False(ui.CanTestWebSearchConnection(isAirGapped: true, initial, "ollama-pending"));

        var saved = ui.ApplyOllamaWebSearchApiKeyInput("  ollama-key-1  ");
        Assert.Equal(ApiKeySaveAction.SavedToKeyring, saved.Action);
        Assert.Equal(ApiKeyCredentialSource.OsKeyring, saved.UpdatedStatus.Source);
        Assert.True(ui.CanTestWebSearchConnection(isAirGapped: false, saved.UpdatedStatus, null));
        Assert.False(ui.CanTestWebSearchConnection(isAirGapped: true, saved.UpdatedStatus, null));

        var preserved = ui.ApplyOllamaWebSearchApiKeyInput("");
        Assert.Equal(ApiKeySaveAction.PreservedExisting, preserved.Action);
        Assert.Equal("ollama-key-1", store.PeekRawOllamaKey());

        var cleared = ui.ClearOllamaWebSearchApiKey();
        Assert.Null(store.PeekRawOllamaKey());
        Assert.Equal(ApiKeyCredentialSource.NotConfigured, cleared.Source);

        env[ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable] = "ollama-env-fallback";
        var fromEnv = ui.GetOllamaWebSearchCredentialStatus();
        Assert.Equal(ApiKeyCredentialSource.EnvironmentVariable, fromEnv.Source);
        Assert.Equal(ApiKeySettingsUiService.InheritedFromOllamaEnvBadgeText, fromEnv.BadgeText);
        Assert.True(ui.CanTestWebSearchConnection(isAirGapped: false, fromEnv, null));
    }

    [Fact]
    public void GetStatus_ReflectsUnavailableTransitionDuringRead_AndAdaptsEnvPlaceholderWhenKeyringUnavailable()
    {
        var store = new FakeApiKeySecretStore { BecomeUnavailableOnRead = true };
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        var ui = CreateUiService(store, env);

        // IsKeyringAvailable() starts true then flips to false inside HasOpenAiApiKey -> must report KeyringUnavailable
        var openAiStatus = ui.GetOpenAiCredentialStatus("https://api.openai.com/v1");
        Assert.Equal(ApiKeyCredentialSource.KeyringUnavailable, openAiStatus.Source);
        Assert.False(openAiStatus.IsKeyringAvailable);

        // Repeat for Ollama Web Search
        store.KeyringAvailable = true;
        var ollamaStatus = ui.GetOllamaWebSearchCredentialStatus();
        Assert.Equal(ApiKeyCredentialSource.KeyringUnavailable, ollamaStatus.Source);
        Assert.False(ollamaStatus.IsKeyringAvailable);

        // When keyring is unavailable AND env fallback is present, placeholder does NOT invite OS keyring override
        store.BecomeUnavailableOnRead = false;
        store.KeyringAvailable = false;
        env[ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable] = "sk-env-only";
        env[ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable] = "ollama-env-only";

        var openAiEnvUnavailable = ui.GetOpenAiCredentialStatus("https://api.openai.com/v1");
        Assert.Equal(ApiKeyCredentialSource.EnvironmentVariable, openAiEnvUnavailable.Source);
        Assert.False(openAiEnvUnavailable.IsKeyringAvailable);
        Assert.Equal(ApiKeySettingsUiService.OpenAiEnvKeyringUnavailablePlaceholder, openAiEnvUnavailable.InputPlaceholder);

        var ollamaEnvUnavailable = ui.GetOllamaWebSearchCredentialStatus();
        Assert.Equal(ApiKeyCredentialSource.EnvironmentVariable, ollamaEnvUnavailable.Source);
        Assert.False(ollamaEnvUnavailable.IsKeyringAvailable);
        Assert.Equal(ApiKeySettingsUiService.OllamaEnvKeyringUnavailablePlaceholder, ollamaEnvUnavailable.InputPlaceholder);
    }

    [Fact]
    public void CreateKeyringErrorStatus_ReturnsCrimsonErrorBadgeWithoutExposingSecrets()
    {
        var store = new FakeApiKeySecretStore();
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable] = "sk-env-present"
        };
        var ui = CreateUiService(store, env);

        var openAiErr = ui.CreateOpenAiKeyringErrorStatus("https://api.openai.com/v1/");
        Assert.Equal(ApiKeyCredentialSource.KeyringError, openAiErr.Source);
        Assert.Equal(ApiKeySettingsUiService.KeyringErrorBadgeText, openAiErr.BadgeText);
        Assert.Equal("badge-crimson", openAiErr.BadgeCssClass);
        Assert.Equal(ApiKeySettingsUiService.KeyringErrorPlaceholder, openAiErr.InputPlaceholder);
        Assert.Equal("https://api.openai.com/v1", openAiErr.NormalizedEndpoint);
        Assert.True(openAiErr.HasEnvironmentFallback);

        var ollamaErr = ui.CreateOllamaWebSearchKeyringErrorStatus();
        Assert.Equal(ApiKeyCredentialSource.KeyringError, ollamaErr.Source);
        Assert.Equal(ApiKeySettingsUiService.KeyringErrorBadgeText, ollamaErr.BadgeText);
        Assert.Equal("badge-crimson", ollamaErr.BadgeCssClass);
        Assert.False(ollamaErr.HasEnvironmentFallback);
    }
}
