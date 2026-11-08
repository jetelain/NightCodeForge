using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace NightCodeForge.Web.Tailscale;

/// <summary>Manages the <c>tailscale serve</c> route exposing this app on the tailnet.</summary>
public interface ITailscaleServeService
{
    /// <summary>Returns a clone of the currently effective Tailscale options (including any in-memory hot-applied override).</summary>
    TailscaleOptions GetCurrentOptions();

    /// <summary>Applies the current options: configures the route when enabled, tears it down when disabled.</summary>
    Task ApplyConfigurationAsync(CancellationToken cancellationToken = default);

    /// <summary>Immediately applies overridden options in-memory and reconfigures the route.</summary>
    Task ApplyConfigurationAsync(TailscaleOptions overrideOptions, CancellationToken cancellationToken = default);

    /// <summary>Queries the live Tailscale status and route state.</summary>
    Task<TailscaleStatusSnapshot> GetStatusSnapshotAsync(CancellationToken cancellationToken = default);
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

    private TailscaleOptions? _optionsOverride;
    private TailscaleOptions? _lastAppliedOptions;
    private volatile TailscaleDaemonState _currentState = TailscaleDaemonState.Inactive;
    private volatile string? _lastErrorMessage;

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

    public TailscaleOptions GetCurrentOptions() => (_optionsOverride ?? _options.CurrentValue).Clone();

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

    public async Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ApplyUnderGateAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyConfigurationAsync(TailscaleOptions overrideOptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(overrideOptions);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _optionsOverride = overrideOptions.Clone();
            await ApplyUnderGateAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyUnderGateAsync(CancellationToken cancellationToken)
    {
        try
        {
            _currentState = TailscaleDaemonState.Applying;
            _lastErrorMessage = null;
            await ApplyCoreAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _currentState = TailscaleDaemonState.Inactive;
            _lastErrorMessage = ex.Message;
            throw;
        }
    }

    public async Task<TailscaleStatusSnapshot> GetStatusSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var loopback = TailscaleTargetResolver.TryResolve(ResolveAddresses(), out var resolvedTarget)
            ? resolvedTarget?.ToServeTarget()
            : null;

        if (_currentState == TailscaleDaemonState.Applying)
        {
            return new TailscaleStatusSnapshot(
                TailscaleDaemonState.Applying,
                null, null, null, null, loopback, null, null);
        }

        var versionResult = await _cli.RunAsync(["version"], ProbeTimeoutSeconds, cancellationToken);
        var cliVersion = versionResult.Succeeded
            ? versionResult.StandardOutput.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            : null;

        var statusResult = await _cli.RunAsync(["status", "--json"], ProbeTimeoutSeconds, cancellationToken);
        var isAuthenticated = statusResult.Succeeded && TailscaleStatusParser.IsAuthenticated(statusResult.StandardOutput);

        string? nodeDns = isAuthenticated ? TailscaleStatusParser.GetNodeDnsName(statusResult.StandardOutput) : null;
        string? tailnetDomain = isAuthenticated ? TailscaleStatusParser.GetTailnetDomain(statusResult.StandardOutput) : null;
        string? tailscaleIp = isAuthenticated ? TailscaleStatusParser.GetTailscaleIpv4(statusResult.StandardOutput) : null;

        TailscaleDaemonState state;
        if (!versionResult.Succeeded || !isAuthenticated)
        {
            state = TailscaleDaemonState.DaemonOfflineOrUnauthenticated;
        }
        else if (_routeActive)
        {
            state = TailscaleDaemonState.Active;
        }
        else
        {
            state = TailscaleDaemonState.Inactive;
        }

        var options = _optionsOverride ?? _options.CurrentValue;
        var serviceName = options.GetNormalizedServiceName();
        var host = state == TailscaleDaemonState.Active
            ? TailscaleStatusParser.GetServeHostName(statusResult.StandardOutput, serviceName)
            : null;
        string? magicDnsUrl = null;
        if (host is not null)
        {
            var protocol = options.GetNormalizedProtocol();
            var portSuffix = IsDefaultPort(protocol, options.ListenPort) ? string.Empty : $":{options.ListenPort}";
            magicDnsUrl = $"{protocol}://{host}{portSuffix}";
        }

        var errorMessage = _lastErrorMessage;
        if (state == TailscaleDaemonState.DaemonOfflineOrUnauthenticated && string.IsNullOrWhiteSpace(errorMessage))
        {
            errorMessage = !versionResult.Succeeded
                ? "Tailscale CLI is not available in PATH."
                : "Tailscale is not connected to a tailnet (run 'tailscale up').";
        }

        return new TailscaleStatusSnapshot(
            state,
            cliVersion,
            nodeDns,
            tailnetDomain,
            tailscaleIp,
            loopback,
            magicDnsUrl,
            errorMessage);
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
        if (!_serverStarted)
        {
            return;
        }

        try
        {
            await _gate.WaitAsync(_lifetime.ApplicationStopping);
            try
            {
                var monitorOptions = _options.CurrentValue;
                if (_lastAppliedOptions is not null
                    && _lastAppliedOptions.ConfigurationEquals(monitorOptions)
                    && _routeActive == monitorOptions.AutoServe)
                {
                    _optionsOverride = null;
                    return;
                }

                _optionsOverride = null;
                await ApplyUnderGateAsync(_lifetime.ApplicationStopping);
            }
            finally
            {
                _gate.Release();
            }
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
        var options = _optionsOverride ?? _options.CurrentValue;

        if (_routeActive)
        {
            await TeardownAsync(ct);
        }

        if (!options.AutoServe)
        {
            _lastAppliedOptions = options.Clone();
            _currentState = TailscaleDaemonState.Inactive;
            return;
        }

        var version = await _cli.RunAsync(["version"], ProbeTimeoutSeconds, ct);
        if (!version.Succeeded)
        {
            _currentState = TailscaleDaemonState.DaemonOfflineOrUnauthenticated;
            _lastErrorMessage = $"Tailscale CLI is not available in PATH ({version.StandardError})";
            _logger.LogWarning(
                "Tailscale:AutoServe is enabled but the 'tailscale' CLI is not available in PATH ({Error}); skipping activation.",
                version.StandardError);
            return;
        }

        var status = await _cli.RunAsync(["status", "--json"], ProbeTimeoutSeconds, ct);
        if (!status.Succeeded || !TailscaleStatusParser.IsAuthenticated(status.StandardOutput))
        {
            _currentState = TailscaleDaemonState.DaemonOfflineOrUnauthenticated;
            _lastErrorMessage = "Tailscale is not connected to a tailnet (run 'tailscale up').";
            _logger.LogWarning(
                "Tailscale is not connected to a tailnet (run 'tailscale up'); skipping activation. {Error}",
                status.StandardError);
            return;
        }

        var target = TailscaleTargetResolver.Resolve(ResolveAddresses());
        var serviceName = options.GetNormalizedServiceName();
        var protocol = options.GetNormalizedProtocol();

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

        _lastAppliedOptions = options.Clone();
        _currentState = TailscaleDaemonState.Active;
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
            if (_currentState == TailscaleDaemonState.Active)
            {
                _currentState = TailscaleDaemonState.Inactive;
            }
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

    private static bool IsDefaultPort(string normalizedProtocol, int listenPort) =>
        listenPort == (normalizedProtocol == TailscaleOptions.HttpsProtocol ? TailscaleOptions.DefaultListenPort : DefaultHttpPort);
}
