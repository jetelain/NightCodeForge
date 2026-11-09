namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Null-object implementation of <see cref="ILlmContextWindowCache"/> for testing and un-cached environments.
/// </summary>
public sealed class EmptyLlmContextWindowCache : ILlmContextWindowCache
{
    public static EmptyLlmContextWindowCache Instance { get; } = new();

    public bool TryGetContextWindow(string modelName, out int tokens)
    {
        tokens = 0;
        return false;
    }

    public void SetContextWindow(string modelName, int tokens)
    {
    }

    public bool TryGetThinkingCapability(string modelName, out ModelThinkingCapability capability)
    {
        capability = ModelThinkingCapability.None;
        return false;
    }

    public void SetThinkingCapability(string modelName, ModelThinkingCapability capability)
    {
    }
}

