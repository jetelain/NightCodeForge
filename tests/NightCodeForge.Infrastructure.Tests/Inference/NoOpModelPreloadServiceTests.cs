using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Inference;

public class NoOpModelPreloadServiceTests
{
    [Fact]
    public async Task PreloadModelAsync_ReturnsTrueWithoutNetworkCall()
    {
        var service = new NoOpModelPreloadService();
        var result = await service.PreloadModelAsync("test-model", "10m");
        Assert.True(result);
    }
}

