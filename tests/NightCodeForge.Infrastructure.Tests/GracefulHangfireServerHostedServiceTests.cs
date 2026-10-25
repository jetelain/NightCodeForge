using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Infrastructure.BackgroundJobs;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests;

public class GracefulHangfireServerHostedServiceTests
{
    private readonly Mock<IHostedService> _innerMock = new();
    private readonly NullLogger<GracefulHangfireServerHostedService> _logger = NullLogger<GracefulHangfireServerHostedService>.Instance;

    [Fact]
    public async Task StartAsync_DelegatesToInner()
    {
        var wrapper = new GracefulHangfireServerHostedService(_innerMock.Object, _logger);
        using var cts = new CancellationTokenSource();

        await wrapper.StartAsync(cts.Token);

        _innerMock.Verify(s => s.StartAsync(cts.Token), Times.Once);
    }

    [Fact]
    public async Task StopAsync_DelegatesToInner()
    {
        var wrapper = new GracefulHangfireServerHostedService(_innerMock.Object, _logger);
        using var cts = new CancellationTokenSource();

        await wrapper.StopAsync(cts.Token);

        _innerMock.Verify(s => s.StopAsync(cts.Token), Times.Once);
    }

    [Fact]
    public async Task StopAsync_WhenInnerThrowsOperationCanceledException_DoesNotRethrow()
    {
        _innerMock
            .Setup(s => s.StopAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("The operation was canceled."));

        var wrapper = new GracefulHangfireServerHostedService(_innerMock.Object, _logger);
        using var cts = new CancellationTokenSource();

        var exception = await Record.ExceptionAsync(() => wrapper.StopAsync(cts.Token));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StopAsync_WhenInnerThrowsAggregateExceptionOfOperationCanceledException_DoesNotRethrow()
    {
        _innerMock
            .Setup(s => s.StopAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AggregateException(
                new OperationCanceledException("The operation was canceled."),
                new OperationCanceledException("The operation was canceled.")));

        var wrapper = new GracefulHangfireServerHostedService(_innerMock.Object, _logger);
        using var cts = new CancellationTokenSource();

        var exception = await Record.ExceptionAsync(() => wrapper.StopAsync(cts.Token));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StopAsync_WhenInnerThrowsNonCancellationException_Rethrows()
    {
        _innerMock
            .Setup(s => s.StopAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unexpected failure"));

        var wrapper = new GracefulHangfireServerHostedService(_innerMock.Object, _logger);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<InvalidOperationException>(() => wrapper.StopAsync(cts.Token));
    }

    [Fact]
    public void Dispose_WhenInnerIsDisposable_DisposesInner()
    {
        var disposableInnerMock = new Mock<IHostedService>();
        var disposableInterface = disposableInnerMock.As<IDisposable>();

        var wrapper = new GracefulHangfireServerHostedService(disposableInnerMock.Object, _logger);
        wrapper.Dispose();

        disposableInterface.Verify(d => d.Dispose(), Times.Once);
    }

    [Fact]
    public void AddNightCodeForgeHangfireServers_WrapsHangfireHostedServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNightCodeForgeHangfire("Data Source=:memory:;");
        services.AddNightCodeForgeHangfireServers();

        var sp = services.BuildServiceProvider();
        var hostedServices = sp.GetServices<IHostedService>().ToList();

        Assert.Equal(2, hostedServices.Count);
        Assert.All(hostedServices, s => Assert.IsType<GracefulHangfireServerHostedService>(s));
    }
}
