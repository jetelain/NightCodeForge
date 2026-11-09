namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Thread-safe in-memory cache for discovered LLM model context window token limits.
/// </summary>
public interface ILlmContextWindowCache
{
    /// <summary>
    /// Attempts to retrieve the cached context window (in tokens) for the specified model.
    /// </summary>
    bool TryGetContextWindow(string modelName, out int tokens);

    /// <summary>
    /// Stores the context window (in tokens) for the specified model.
    /// </summary>
    void SetContextWindow(string modelName, int tokens);

    /// <summary>
    /// Attempts to retrieve the cached thinking capability for the specified model.
    /// </summary>
    bool TryGetThinkingCapability(string modelName, out ModelThinkingCapability capability);

    /// <summary>
    /// Stores the thinking capability for the specified model.
    /// </summary>
    void SetThinkingCapability(string modelName, ModelThinkingCapability capability);
}
