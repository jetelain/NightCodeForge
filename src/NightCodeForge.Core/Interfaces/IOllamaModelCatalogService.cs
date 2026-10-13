namespace NightCodeForge.Core.Interfaces;

public record OllamaModelInfo(
    string Name,
    string? Model,
    long Size,
    DateTime? ModifiedAt,
    string? Digest,
    IReadOnlyDictionary<string, object?>? Details = null);

/// <summary>
/// Service for dynamically querying OllamaRouter (/api/tags) for local and remote models.
/// </summary>
public interface IOllamaModelCatalogService
{
    Task<IReadOnlyList<OllamaModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Service for JIT model preload / warmup triggers to hide PCIe model swapping latency.
/// </summary>
public interface IOllamaModelPreloadService
{
    Task<bool> PreloadModelAsync(string modelName, string keepAlive = "15m", CancellationToken cancellationToken = default);
}
