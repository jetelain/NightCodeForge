using Microsoft.Extensions.Caching.Memory;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

/// <summary>
/// Singleton in-memory cache for discovered model context windows backed by <see cref="IMemoryCache"/>.
/// </summary>
public class LlmContextWindowCache : ILlmContextWindowCache
{
    private const string KeyPrefix = "llm-ctx-window:";
    private const string ThinkingKeyPrefix = "llm-thinking-cap:";
    private readonly IMemoryCache _memoryCache;

    public LlmContextWindowCache(IMemoryCache memoryCache)
    {
        _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
    }

    public bool TryGetContextWindow(string modelName, out int tokens)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            tokens = 0;
            return false;
        }

        var key = BuildKey(modelName);
        if (_memoryCache.TryGetValue(key, out int value) && value > 0)
        {
            tokens = value;
            return true;
        }

        tokens = 0;
        return false;
    }

    public void SetContextWindow(string modelName, int tokens)
    {
        if (string.IsNullOrWhiteSpace(modelName) || tokens <= 0)
        {
            return;
        }

        var key = BuildKey(modelName);
        _memoryCache.Set(key, tokens);
    }

    public bool TryGetThinkingCapability(string modelName, out ModelThinkingCapability capability)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            capability = ModelThinkingCapability.None;
            return false;
        }

        var key = BuildThinkingKey(modelName);
        if (_memoryCache.TryGetValue(key, out ModelThinkingCapability? value) && value != null)
        {
            capability = value;
            return true;
        }

        capability = ModelThinkingCapability.None;
        return false;
    }

    public void SetThinkingCapability(string modelName, ModelThinkingCapability capability)
    {
        if (string.IsNullOrWhiteSpace(modelName) || capability == null)
        {
            return;
        }

        var key = BuildThinkingKey(modelName);
        _memoryCache.Set(key, capability);
    }

    private static string BuildKey(string modelName) => $"{KeyPrefix}{modelName.Trim().ToLowerInvariant()}";

    private static string BuildThinkingKey(string modelName) => $"{ThinkingKeyPrefix}{modelName.Trim().ToLowerInvariant()}";
}
