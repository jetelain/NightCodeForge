namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Thread-safe in-memory cache of models discovered from an OpenAI-compatible endpoint.
/// Allows singleton services (such as chat client factories) to validate requested model IDs
/// synchronously without captive HTTP client dependencies.
/// </summary>
public interface IOpenAiModelAllowlistCache
{
    /// <summary>
    /// Checks whether the model identifier is present in the discovered model cache.
    /// </summary>
    bool IsModelAllowed(string modelId);

    /// <summary>
    /// Attempts to retrieve discovered model metadata for the given model identifier.
    /// </summary>
    bool TryGetModel(string modelId, out AvailableModelInfo? modelInfo);

    /// <summary>
    /// Atomically replaces the cache with the newly discovered models.
    /// </summary>
    void SetDiscoveredModels(IEnumerable<AvailableModelInfo> discoveredModels);

    /// <summary>
    /// Returns a snapshot of all discovered models currently in the cache.
    /// </summary>
    IReadOnlyList<AvailableModelInfo> GetDiscoveredModels();
}
