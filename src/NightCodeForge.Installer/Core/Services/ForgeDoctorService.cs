namespace NightCodeForge.Installer.Core.Services;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Serialization;

/// <summary>
/// Executes Forge Doctor self-tests for WSL2, Docker, OllamaRouter, Web server port 5270,
/// companion .NET global tools, and SQLite database integrity.
/// </summary>
public sealed class ForgeDoctorService : IForgeDoctorService
{
    private const int DatabaseReadBufferBytes = 4096;
    private readonly HttpClient _httpClient;
    private readonly IPlatformEnvironment _platform;
    private readonly IEnvironmentProvisioner _environmentProvisioner;
    private readonly ICompanionNuGetProvisioner _nuGetProvisioner;
    private readonly IInstallationDetector _installationDetector;
    private readonly IProcessControlService _processControl;

    public ForgeDoctorService(
        HttpClient httpClient,
        IPlatformEnvironment platform,
        IEnvironmentProvisioner environmentProvisioner,
        ICompanionNuGetProvisioner nuGetProvisioner,
        IInstallationDetector installationDetector,
        IProcessControlService processControl)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _environmentProvisioner = environmentProvisioner ?? throw new ArgumentNullException(nameof(environmentProvisioner));
        _nuGetProvisioner = nuGetProvisioner ?? throw new ArgumentNullException(nameof(nuGetProvisioner));
        _installationDetector = installationDetector ?? throw new ArgumentNullException(nameof(installationDetector));
        _processControl = processControl ?? throw new ArgumentNullException(nameof(processControl));
    }

    public async Task<ForgeDoctorReport> RunDiagnosticsAsync(
        string? installDirectory,
        string? wslDistroName,
        string? routerBaseUrl,
        CancellationToken cancellationToken = default)
    {
        var effectiveDistro = string.IsNullOrWhiteSpace(wslDistroName)
            ? InstallerConstants.DefaultWslDistro
            : wslDistroName.Trim();

        var effectiveInstallDir = string.IsNullOrWhiteSpace(installDirectory)
            ? _installationDetector.Detect().NightCodeForge.InstallDirectory
            : NormalizeExplicitInstallDirectory(installDirectory);

        var rawRouterUrl = string.IsNullOrWhiteSpace(routerBaseUrl)
            ? InstallerConstants.DefaultOllamaRouterEndpoint
            : routerBaseUrl.Trim().TrimEnd('/');

        var probeTask = _environmentProvisioner.ProbeAsync(effectiveDistro, cancellationToken);
        var routerTask = CheckOllamaRouterAsync(rawRouterUrl, cancellationToken);
        var webTask = CheckWebPortAsync(effectiveInstallDir, cancellationToken);
        var dbTask = CheckDatabaseHealthAsync(effectiveInstallDir, cancellationToken);

        var probe = await probeTask.ConfigureAwait(false);
        var companionTask = CheckCompanionToolsAsync(probe, effectiveDistro, cancellationToken);

        await Task.WhenAll(routerTask, webTask, companionTask, dbTask).ConfigureAwait(false);

        var checks = new List<ForgeDoctorCheckItem>(6)
        {
            CheckWsl2(probe, effectiveDistro),
            CheckDocker(probe),
            await routerTask.ConfigureAwait(false),
            await webTask.ConfigureAwait(false),
            await companionTask.ConfigureAwait(false),
            await dbTask.ConfigureAwait(false)
        };

        var passed = checks.Count(static c => c.Status == DoctorCheckStatus.Pass);
        var warnings = checks.Count(static c => c.Status == DoctorCheckStatus.Warning);
        var failed = checks.Count(static c => c.Status == DoctorCheckStatus.Fail);

        return new ForgeDoctorReport(
            CheckedAtUtc: DateTimeOffset.UtcNow,
            Checks: checks,
            PassedCount: passed,
            WarningCount: warnings,
            FailedCount: failed);
    }

    private static string NormalizeExplicitInstallDirectory(string installDirectory)
    {
        var trimmed = installDirectory.Trim();
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Justification: Invalid explicit path is preserved so individual checks fail deterministically without throwing out of RunDiagnosticsAsync.
            _ = ex;
            return trimmed;
        }
    }

    private ForgeDoctorCheckItem CheckWsl2(EnvironmentProbeResult probe, string requestedDistro)
    {
        if (_platform.Platform != HostPlatformType.Windows)
        {
            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckWsl2,
                DoctorCheckStatus.Pass,
                "Native Unix host; WSL2 not required.",
                $"Running on {_platform.Platform} ({_platform.CurrentRuntimeIdentifier}).");
        }

        if (!probe.WslInstalled)
        {
            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckWsl2,
                DoctorCheckStatus.Fail,
                "WSL2 is not enabled or installed.",
                "Run Setup Wizard Step 2 to enable WSL2 and install Ubuntu.");
        }

        if (!probe.WslDistroInstalled)
        {
            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckWsl2,
                DoctorCheckStatus.Fail,
                $"WSL2 distro '{requestedDistro}' is missing.",
                "Run Setup Wizard Step 2 to provision the Linux distribution.");
        }

        var activeDistro = probe.ResolvedWslDistroName ?? requestedDistro;
        return probe.WslDotNet10Installed
            ? new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckWsl2,
                DoctorCheckStatus.Pass,
                $"WSL2 active ({activeDistro}).",
                ".NET 10 SDK present inside WSL2.")
            : new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckWsl2,
                DoctorCheckStatus.Warning,
                $"WSL2 active ({activeDistro}), .NET 10 SDK missing.",
                "Run Setup Wizard Step 2 to install .NET 10 SDK inside WSL2.");
    }

    private ForgeDoctorCheckItem CheckDocker(EnvironmentProbeResult probe)
    {
        var dockerReady = _platform.Platform == HostPlatformType.Windows
            ? probe.WslDockerInstalled
            : probe.HostDockerAvailable;

        return dockerReady
            ? new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckDocker,
                DoctorCheckStatus.Pass,
                "Docker daemon/CLI available.",
                _platform.Platform == HostPlatformType.Windows
                    ? $"Available inside WSL2 ({probe.ResolvedWslDistroName ?? InstallerConstants.DefaultWslDistro})."
                    : "Host Docker runtime responded.")
            : new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckDocker,
                DoctorCheckStatus.Fail,
                "Docker daemon not detected.",
                "Ensure Docker Engine inside WSL2 or Docker Desktop/Colima is running.");
    }

    private async Task<ForgeDoctorCheckItem> CheckOllamaRouterAsync(
        string routerBaseUrl,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(routerBaseUrl, UriKind.Absolute, out var parsedUri) ||
            (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(parsedUri.Host) ||
            !string.IsNullOrEmpty(parsedUri.Query) ||
            !string.IsNullOrEmpty(parsedUri.Fragment))
        {
            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckOllamaRouter,
                DoctorCheckStatus.Fail,
                $"Invalid OllamaRouter base URL '{routerBaseUrl}'.",
                "Expected an absolute HTTP or HTTPS URL without query or fragment.");
        }

        var infoUri = $"{routerBaseUrl}{InstallerConstants.OllamaRouterInfoApiPath}";
        var probeUri = $"{routerBaseUrl}{InstallerConstants.OllamaRouterRecommendationsApiPath}";
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(InstallerConstants.DoctorProbeTimeoutSeconds));

        try
        {
            using var infoRequest = new HttpRequestMessage(HttpMethod.Get, infoUri);
            using var infoResponse = await _httpClient
                .SendAsync(infoRequest, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                .ConfigureAwait(false);

            if (!infoResponse.IsSuccessStatusCode)
            {
                return new ForgeDoctorCheckItem(
                    InstallerConstants.DoctorCheckOllamaRouter,
                    DoctorCheckStatus.Fail,
                    $"Endpoint at {routerBaseUrl} did not identify as OllamaRouter (HTTP {(int)infoResponse.StatusCode}).",
                    $"GET {infoUri} failed; ensure OllamaRouter (not standalone Ollama) is running on this port.");
            }

            RouterInfoProbeDto? infoDto;
            try
            {
                await using var infoStream = await infoResponse.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
                infoDto = await JsonSerializer.DeserializeAsync(
                    infoStream,
                    InstallerJsonContext.Default.RouterInfoProbeDto,
                    linkedCts.Token).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return new ForgeDoctorCheckItem(
                    InstallerConstants.DoctorCheckOllamaRouter,
                    DoctorCheckStatus.Fail,
                    $"Invalid OllamaRouter identity response from {routerBaseUrl}.",
                    ex.Message);
            }

            if (infoDto is not { IsRouter: true })
            {
                return new ForgeDoctorCheckItem(
                    InstallerConstants.DoctorCheckOllamaRouter,
                    DoctorCheckStatus.Fail,
                    $"Service at {routerBaseUrl} is not OllamaRouter.",
                    $"GET {infoUri} returned isRouter=false.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, probeUri);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return new ForgeDoctorCheckItem(
                    InstallerConstants.DoctorCheckOllamaRouter,
                    DoctorCheckStatus.Pass,
                    $"OllamaRouter reachable at {routerBaseUrl}.",
                    "Verified OllamaRouter identity and role recommendations endpoint.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ForgeDoctorCheckItem(
                    InstallerConstants.DoctorCheckOllamaRouter,
                    DoctorCheckStatus.Warning,
                    "Reachable; run Setup Wizard to generate role recommendations.",
                    $"GET {probeUri} returned HTTP 404 (no recommendations saved yet).");
            }

            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckOllamaRouter,
                DoctorCheckStatus.Fail,
                $"OllamaRouter returned HTTP {(int)response.StatusCode}.",
                $"Endpoint: {probeUri}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckOllamaRouter,
                DoctorCheckStatus.Fail,
                $"Timed out connecting to {routerBaseUrl}.",
                "Start OllamaRouter server or run the OllamaRouter Setup Wizard.");
        }
        catch (HttpRequestException ex)
        {
            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckOllamaRouter,
                DoctorCheckStatus.Fail,
                $"Unreachable at {routerBaseUrl}.",
                ex.Message);
        }
    }

    private async Task<ForgeDoctorCheckItem> CheckWebPortAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var isProcessRunning = _processControl.IsAnyProcessRunning(InstallerConstants.NightCodeForgeWebProcesses) ||
                               _processControl.IsAnyProcessRunning(InstallerConstants.NightCodeForgeLauncherProcesses);

        var configuredRawUrls = Directory.Exists(installDirectory)
            ? InstalledAppConfigReader.ReadFirstNonEmptyString(installDirectory, InstallerConstants.KeyUrls)
            : null;
        var configuredUrl = string.IsNullOrWhiteSpace(configuredRawUrls)
            ? InstallerConstants.DefaultWebUrls
            : HostAppControlService.NormalizeListeningUrlToLocalhost(configuredRawUrls);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(InstallerConstants.DoctorProbeTimeoutSeconds));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, configuredUrl);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                .ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;

            if ((statusCode >= InstallerConstants.HttpStatusSuccessMin &&
                 statusCode < InstallerConstants.HttpStatusSuccessMaxExclusive) ||
                response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return isProcessRunning
                    ? new ForgeDoctorCheckItem(
                        InstallerConstants.DoctorCheckWebPort,
                        DoctorCheckStatus.Pass,
                        $"Listening at {configuredUrl} (HTTP {statusCode}).",
                        "NightCodeForge.Web process active.")
                    : new ForgeDoctorCheckItem(
                        InstallerConstants.DoctorCheckWebPort,
                        DoctorCheckStatus.Warning,
                        $"Port responded HTTP {statusCode}, but NightCodeForge.Web process was not detected.",
                        $"Endpoint {configuredUrl} may be served by another process.");
            }

            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckWebPort,
                isProcessRunning ? DoctorCheckStatus.Warning : DoctorCheckStatus.Fail,
                $"Unexpected HTTP {statusCode} from {configuredUrl}.",
                "Inspect NightCodeForge Web logs in logs/.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckWebPort,
                isProcessRunning ? DoctorCheckStatus.Warning : DoctorCheckStatus.Fail,
                isProcessRunning
                    ? $"Process running, but {configuredUrl} timed out."
                    : $"NightCodeForge Web is stopped ({configuredUrl} timed out).",
                "Click Start Server in Maintenance Hub.");
        }
        catch (HttpRequestException ex)
        {
            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckWebPort,
                isProcessRunning ? DoctorCheckStatus.Warning : DoctorCheckStatus.Fail,
                isProcessRunning
                    ? $"Process running, but {configuredUrl} is not yet accepting HTTP requests."
                    : $"Stopped / unreachable at {configuredUrl}.",
                ex.Message);
        }
    }

    private async Task<ForgeDoctorCheckItem> CheckCompanionToolsAsync(
        EnvironmentProbeResult probe,
        string requestedDistro,
        CancellationToken cancellationToken)
    {
        var wslDistro = _platform.Platform == HostPlatformType.Windows &&
                        probe.WslInstalled &&
                        probe.WslDistroInstalled
            ? probe.ResolvedWslDistroName ?? requestedDistro
            : null;

        var statuses = await _nuGetProvisioner.ProbeToolsAsync(wslDistro, cancellationToken).ConfigureAwait(false);
        var missing = new List<string>();

        foreach (var status in statuses)
        {
            if (status.Spec.InstallOnHost && !status.InstalledOnHost)
            {
                missing.Add($"{status.Spec.ToolCommand} (Host)");
            }

            if (wslDistro is not null && status.Spec.InstallInWsl && !status.InstalledInWsl)
            {
                missing.Add($"{status.Spec.ToolCommand} (WSL2)");
            }
        }

        if (missing.Count == 0)
        {
            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckCompanionTools,
                DoctorCheckStatus.Pass,
                $"All {statuses.Count} companion tools installed.",
                wslDistro is not null ? $"Verified on Host and WSL2 ({wslDistro})." : "Verified on Host.");
        }

        return new ForgeDoctorCheckItem(
            InstallerConstants.DoctorCheckCompanionTools,
            DoctorCheckStatus.Fail,
            $"Missing {missing.Count} companion tool installation(s).",
            string.Join(", ", missing));
    }

    private static async Task<ForgeDoctorCheckItem> CheckDatabaseHealthAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        string dbPath;
        try
        {
            dbPath = InstalledAppConfigReader.ReadDefaultDatabasePath(installDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckDatabase,
                DoctorCheckStatus.Fail,
                "Invalid database configuration or install path.",
                ex.Message);
        }

        var dbDirectory = Path.GetDirectoryName(dbPath) ?? Path.Combine(installDirectory, InstallerConstants.DataDirectoryName);
        if (!Directory.Exists(dbDirectory))
        {
            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckDatabase,
                DoctorCheckStatus.Fail,
                "Data directory is missing.",
                $"Expected directory: {dbDirectory}");
        }

        if (!File.Exists(dbPath))
        {
            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckDatabase,
                DoctorCheckStatus.Warning,
                "Will initialize on first server launch.",
                $"Data directory exists ({dbDirectory}); database file not created yet.");
        }

        try
        {
            var expectedMagic = Encoding.ASCII.GetBytes(InstallerConstants.SqliteFileHeaderMagic);
            var buffer = new byte[expectedMagic.Length];

            await using var stream = new FileStream(
                dbPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: DatabaseReadBufferBytes,
                useAsync: true);
            var bytesRead = await stream
                .ReadAtLeastAsync(buffer.AsMemory(0, expectedMagic.Length), expectedMagic.Length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);

            if (bytesRead < expectedMagic.Length || !buffer.AsSpan().SequenceEqual(expectedMagic))
            {
                return new ForgeDoctorCheckItem(
                    InstallerConstants.DoctorCheckDatabase,
                    DoctorCheckStatus.Fail,
                    "SQLite database header is invalid or corrupt.",
                    $"File: {dbPath}");
            }

            var sizeKb = Math.Max(1L, stream.Length / InstallerConstants.BytesPerKilobyte);
            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckDatabase,
                DoctorCheckStatus.Pass,
                string.Format(CultureInfo.InvariantCulture, "SQLite database valid ({0:N0} KB).", sizeKb),
                dbPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckDatabase,
                DoctorCheckStatus.Fail,
                "Unable to read SQLite database file.",
                ex.Message);
        }
    }
}
