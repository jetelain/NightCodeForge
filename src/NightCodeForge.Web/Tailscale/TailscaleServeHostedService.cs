using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace NightCodeForge.Web.Tailscale;

/// <summary>Manages the <c>tailscale serve</c> route exposing this app on the tailnet.</summary>
public interface ITailscaleServeService
{
    /// <summary>Applies the current options: configures the route when enabled, tears it down when disabled.</summary>
    Task ApplyConfigurationAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Hosted service driving <c>tailscale serve</c>: configures the route once Kestrel is listening,
/// hot-applies option changes, and drains/clears/resets on shutdown.
/// </summary>
public class TailscaleServeHostedService : IHostedService, ITailscaleServeService, IDisposable
{
    private const int ProbeTimeoutSeconds = 10;
    private const int ConfigureTimeoutSeconds = 30;
    private const int DrainTimeoutSeconds = 3;
    private const int DefaultHttpPort = 80;
    private const string UrlsConfigurationKey = "Urls";
    private const string ServiceFlagPrefix = "--service=";

    private readonly IOptionsMonitor<TailscaleOptions> _options;
    private readonly IServer _server;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ITailscaleCliRunner _cli;
    private readonly ILogger<TailscaleServeHostedService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IDisposable? _optionsSubscription;
    private CancellationTokenRegistration _startedRegistration;
    private bool _serverStarted;
    private bool _routeActive;
    private string? _activeServiceName;

    public TailscaleServeHostedService(
        IOptionsMonitor<TailscaleOptions> options,
        IServer server,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ITailscaleCliRunner cli,
        ILogger<TailscaleServeHostedService> logger)
    {
        _options = options;
        _server = server;
        _configuration = configuration;
        _lifetime = lifetime;
        _cli = cli;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Kestrel addresses are only known once the server is listening.
        _startedRegistration = _lifetime.ApplicationStarted.Register(() => _ = OnServerStartedAsync());
        _optionsSubscription = _options.OnChange(changed => { _ = OnOptionsChangedAsync(); });
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _optionsSubscription?.Dispose();
        _startedRegistration.Dispose();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await TeardownAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ApplyCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _optionsSubscription?.Dispose();
        _startedRegistration.Dispose();
        _gate.Dispose();
    }

    private async Task OnServerStartedAsync()
    {
        _serverStarted = true;
        await ApplySafelyAsync();
    }

    private async Task OnOptionsChangedAsync()
    {
        if (_serverStarted)
        {
            await ApplySafelyAsync();
        }
    }

    private async Task ApplySafelyAsync()
    {
        try
        {
            await ApplyConfigurationAsync(_lifetime.ApplicationStopping);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Tailscale serve configuration cancelled (application stopping).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tailscale serve configuration failed; remote access over the tailnet is unavailable.");
        }
    }

    private async Task ApplyCoreAsync(CancellationToken ct)
    {
        var options = _options.CurrentValue;

        if (_routeActive)
        {
            await TeardownAsync(ct);
        }

        if (!options.AutoServe)
        {
            return;
        }

        var version = await _cli.RunAsync(["version"], ProbeTimeoutSeconds, ct);
        if (!version.Succeeded)
        {
            _logger.LogWarning(
                "Tailscale:AutoServe is enabled but the 'tailscale' CLI is not available in PATH ({Error}); skipping activation.",
                version.StandardError);
            return;
        }

        var status = await _cli.RunAsync(["status", "--json"], ProbeTimeoutSeconds, ct);
        if (!status.Succeeded || !TailscaleStatusParser.IsAuthenticated(status.StandardOutput))
        {
            _logger.LogWarning(
                "Tailscale is not connected to a tailnet (run 'tailscale up'); skipping activation. {Error}",
                status.StandardError);
            return;
        }

        var target = TailscaleTargetResolver.Resolve(ResolveAddresses());
        var serviceName = string.IsNullOrWhiteSpace(options.ServiceName) ? null : options.ServiceName.Trim();
        var protocol = NormalizeProtocol(options.Protocol);

        // Defensive pre-flight cleanup of stale routes left by abnormal terminations.
        await RunRequiredAsync(["serve", "reset"], ConfigureTimeoutSeconds, ct);

        var listen = $"--{protocol}={options.ListenPort}";
        if (serviceName is not null)
        {
            await RunRequiredAsync(
                ["serve", $"{ServiceFlagPrefix}{serviceName}", listen, target.ToServeTarget()],
                ConfigureTimeoutSeconds,
                ct);
            _routeActive = true;
            _activeServiceName = serviceName;

            if (options.Advertise)
            {
                await RunRequiredAsync(["serve", "advertise", serviceName], ConfigureTimeoutSeconds, ct);
            }
        }
        else
        {
            await RunRequiredAsync(["serve", listen, target.ToServeTarget()], ConfigureTimeoutSeconds, ct);
            _routeActive = true;
            _activeServiceName = null;
        }

        var host = TailscaleStatusParser.GetServeHostName(status.StandardOutput, serviceName);
        _logger.LogInformation(
            "NightCodeForge is available on your tailnet at {Protocol}://{Host}{Port} (proxying to {Target})",
            protocol,
            host ?? "<unknown MagicDNS name>",
            IsDefaultPort(protocol, options.ListenPort) ? string.Empty : $":{options.ListenPort}",
            target.ToServeTarget());
    }

    private async Task TeardownAsync(CancellationToken ct)
    {
        if (!_routeActive)
        {
            return;
        }

        try
        {
            if (_activeServiceName is not null)
            {
                await _cli.RunAsync(["serve", "drain", _activeServiceName], DrainTimeoutSeconds, ct);
                await _cli.RunAsync(["serve", "clear", _activeServiceName], ConfigureTimeoutSeconds, ct);
            }

            var reset = await _cli.RunAsync(["serve", "reset"], ConfigureTimeoutSeconds, ct);
            if (reset.Succeeded)
            {
                _logger.LogInformation("Tailscale serve route removed.");
            }
            else
            {
                _logger.LogWarning("'tailscale serve reset' failed during teardown: {Error}", reset.StandardError);
            }
        }
        finally
        {
            _routeActive = false;
            _activeServiceName = null;
        }
    }

    private async Task RunRequiredAsync(IReadOnlyList<string> args, int timeoutSeconds, CancellationToken ct)
    {
        var result = await _cli.RunAsync(args, timeoutSeconds, ct);
        if (!result.Succeeded)
        {
            throw new TailscaleServeException(
                $"'tailscale {string.Join(' ', args)}' failed with exit code {result.ExitCode}: {result.StandardError}".Trim());
        }
    }

    private IEnumerable<string> ResolveAddresses()
    {
        var bound = _server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        if (bound.Count > 0)
        {
            return bound;
        }

        var configured = _configuration[UrlsConfigurationKey];
        return string.IsNullOrWhiteSpace(configured) ? [] : [configured];
    }

    private static string NormalizeProtocol(string? protocol) =>
        string.Equals(protocol?.Trim(), TailscaleOptions.HttpProtocol, StringComparison.OrdinalIgnoreCase)
            ? TailscaleOptions.HttpProtocol
            : TailscaleOptions.HttpsProtocol;

    private static bool IsDefaultPort(string normalizedProtocol, int listenPort) =>
        listenPort == (normalizedProtocol == TailscaleOptions.HttpsProtocol ? TailscaleOptions.DefaultListenPort : DefaultHttpPort);
}
