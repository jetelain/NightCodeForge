using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

/// <summary>
/// No-op model preload service for OpenAI-compatible endpoints where local PCIe model swapping
/// is not applicable or controllable by the client.
/// </summary>
public sealed class NoOpModelPreloadService : ILlmModelPreloadService
{
    public Task<bool> PreloadModelAsync(string modelName, string keepAlive = "15m", CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}

