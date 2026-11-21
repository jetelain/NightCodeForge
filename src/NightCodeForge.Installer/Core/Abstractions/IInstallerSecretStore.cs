namespace NightCodeForge.Installer.Core.Abstractions;

/// <summary>
/// Native OS keyring secret store used by the installer to save and inspect
/// endpoint-scoped OpenAI-compatible API keys matching <c>NightCodeForge.Web</c>'s coordinates.
/// </summary>
public interface IInstallerSecretStore
{
    bool IsKeyringAvailable();

    string? GetOpenAiApiKey(string? endpoint);

    string? ResolveEffectiveOpenAiApiKey(string? endpoint);

    bool HasOpenAiApiKey(string? endpoint);

    bool HasEnvironmentOpenAiApiKey();

    void SetOpenAiApiKey(string endpoint, string apiKey);

    void DeleteOpenAiApiKey(string endpoint);
}
