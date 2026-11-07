using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Inference;

public class NoOpModelPreloadServiceTests
{
    [Fact]
    public async Task PreloadModelAsync_ReturnsTrueWithoutNetworkCall()
    {
        var service = new NoOpModelPreloadService();
        var result = await service.PreloadModelAsync("test-model", new ModelPreloadOptions());
        Assert.True(result.Success);
        Assert.True(result.LoadAccepted);
    }

    [Theory]
    [InlineData(LlmProviderType.Ollama, typeof(OllamaModelPreloadService))]
    [InlineData(LlmProviderType.OllamaRouter, typeof(OllamaRouterModelPreloadService))]
    [InlineData(LlmProviderType.OpenAiCompatible, typeof(NoOpModelPreloadService))]
    public void AddNightCodeForgeInference_RegistersExpectedPreloadServiceForProvider(LlmProviderType providerType, Type expectedImplementationType)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new Mock<IOperationsControlService>().Object);
        services.AddSingleton(new Mock<IAgentActivityStreamer>().Object);
        services.AddSingleton(new Mock<IOperationsTelemetryService>().Object);
        services.AddNightCodeForgeInference(providerType);

        using var provider = services.BuildServiceProvider();
        var preloadService = provider.GetRequiredService<ILlmModelPreloadService>();

        Assert.IsType(expectedImplementationType, preloadService);
    }
}

