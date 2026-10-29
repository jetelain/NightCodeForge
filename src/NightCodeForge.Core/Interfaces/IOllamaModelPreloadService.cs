namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Service for JIT model preload / warmup triggers to hide PCIe model swapping latency.
/// </summary>
public interface IOllamaModelPreloadService
{
    Task<bool> PreloadModelAsync(string modelName, string keepAlive = "15m", CancellationToken cancellationToken = default);
}
