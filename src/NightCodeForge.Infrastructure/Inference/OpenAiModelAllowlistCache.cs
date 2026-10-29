using System;
using System.Collections.Generic;
using System.Linq;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

/// <summary>
/// Thread-safe in-memory cache storing models discovered from an OpenAI-compatible endpoint.
/// Uses an atomic reference swap on updates so concurrent readers never observe a cleared or partial cache.
/// </summary>
public class OpenAiModelAllowlistCache : IOpenAiModelAllowlistCache
{
    private volatile IReadOnlyDictionary<string, AvailableModelInfo> _models =
        new Dictionary<string, AvailableModelInfo>(StringComparer.OrdinalIgnoreCase);

    public bool IsModelAllowed(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        return _models.ContainsKey(modelId.Trim());
    }

    public bool TryGetModel(string modelId, out AvailableModelInfo? modelInfo)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelInfo = null;
            return false;
        }

        return _models.TryGetValue(modelId.Trim(), out modelInfo);
    }

    public void SetDiscoveredModels(IEnumerable<AvailableModelInfo> discoveredModels)
    {
        ArgumentNullException.ThrowIfNull(discoveredModels);

        var next = new Dictionary<string, AvailableModelInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in discoveredModels)
        {
            if (model != null && !string.IsNullOrWhiteSpace(model.ModelId))
            {
                var trimmedId = model.ModelId.Trim();
                var normalized = model.ModelId == trimmedId ? model : model with { ModelId = trimmedId };
                next[trimmedId] = normalized;
            }
        }

        // Atomic reference swap: readers never observe an empty or partially populated dictionary.
        _models = next;
    }

    public IReadOnlyList<AvailableModelInfo> GetDiscoveredModels() =>
        _models.Values.ToList();
}
