namespace NightCodeForge.Core.Tests.Fixtures;

using NightCodeForge.Core.Interfaces;

public class FakePreloadService : ILlmModelPreloadService
{
    public List<(string ModelName, ModelPreloadOptions? Options)> PreloadedModels { get; } = new();

    public Task<ModelPreloadResult> PreloadModelAsync(string modelName, ModelPreloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        PreloadedModels.Add((modelName, options));
        return Task.FromResult(new ModelPreloadResult(true, modelName, LoadAccepted: true));
    }
}
