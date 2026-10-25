using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NightCodeForge.Infrastructure.BackgroundJobs;

/// <summary>
/// Hosted service wrapper around Hangfire's BackgroundJobServerHostedService.
/// Catches OperationCanceledException during shutdown to prevent unhandled AggregateException crashes
/// when the host terminates or shuts down before all Hangfire dispatchers exit.
/// </summary>
public sealed class GracefulHangfireServerHostedService : IHostedService, IDisposable
{
    private readonly IHostedService _inner;
    private readonly ILogger<GracefulHangfireServerHostedService> _logger;

    public GracefulHangfireServerHostedService(
        IHostedService inner,
        ILogger<GracefulHangfireServerHostedService> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken) => _inner.StartAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _inner.StopAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Hangfire server stop was canceled or timed out during host shutdown.");
        }
        catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(e => e is OperationCanceledException))
        {
            _logger.LogInformation("Hangfire server stop was canceled or timed out during host shutdown.");
        }
    }

    public void Dispose()
    {
        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
