using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

/// <summary>
/// No-op model preload service for OpenAI-compatible endpoints where local PCIe model swapping
/// is not applicable or controllable by the client.
/// </summary>
public sealed class NoOpModelPreloadService : ILlmModelPreloadService
{
    public Task<ModelPreloadResult> PreloadModelAsync(
        string modelName,
        ModelPreloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ModelPreloadResult(
            Success: true,
            Model: modelName,
            SelectedTarget: "Cloud / OpenAI-Compatible",
            LoadAccepted: true,
            Reason: "OpenAI-compatible endpoints do not require local model preloading."));
    }
}
