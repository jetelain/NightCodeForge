namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Service for JIT model preload / warmup triggers to hide model swapping latency.
/// </summary>
public interface ILlmModelPreloadService
{
    Task<bool> PreloadModelAsync(string modelName, string keepAlive = "15m", CancellationToken cancellationToken = default);
}
