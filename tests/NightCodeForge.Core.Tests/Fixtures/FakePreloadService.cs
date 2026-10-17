namespace NightCodeForge.Core.Tests.Fixtures;

using NightCodeForge.Core.Interfaces;

public class FakePreloadService : IOllamaModelPreloadService
{
    public List<(string ModelName, string KeepAlive)> PreloadedModels { get; } = new();

    public Task<bool> PreloadModelAsync(string modelName, string keepAlive = "15m", CancellationToken cancellationToken = default)
    {
        PreloadedModels.Add((modelName, keepAlive));
        return Task.FromResult(true);
    }

    public Task<bool> EvictModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }
}
