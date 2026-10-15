using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Service managing web search settings, credentials, and endpoints across the system.
/// </summary>
public interface IWebSearchSettingsService
{
    /// <summary>
    /// Gets the current in-memory cached web search settings.
    /// </summary>
    WebSearchSettings GetSettings();

    /// <summary>
    /// Loads settings from the persistent store (or environment/defaults).
    /// </summary>
    Task<WebSearchSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates and persists web search settings.
    /// </summary>
    Task UpdateSettingsAsync(WebSearchSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Event triggered when web search settings are modified.
    /// </summary>
    event Action<WebSearchSettings>? OnSettingsChanged;
}
