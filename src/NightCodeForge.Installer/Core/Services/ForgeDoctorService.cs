namespace NightCodeForge.Installer.Core.Services;

using System.Globalization;
using System.Net;
using System.Text;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Executes Forge Doctor self-tests for WSL2, Docker, OllamaRouter, Web server port 5270,
/// companion .NET global tools, and SQLite database integrity.
/// </summary>
public sealed class ForgeDoctorService : IForgeDoctorService
{
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
        string wslDistroName,
        string? routerBaseUrl,
        CancellationToken cancellationToken = default)
    {
        var effectiveDistro = string.IsNullOrWhiteSpace(wslDistroName)
            ? InstallerConstants.DefaultWslDistro
            : wslDistroName.Trim();

        var discovery = _installationDetector.Detect();
        var effectiveInstallDir = string.IsNullOrWhiteSpace(installDirectory)
            ? discovery.NightCodeForge.InstallDirectory
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory.Trim()));

        var effectiveRouterUrl = string.IsNullOrWhiteSpace(routerBaseUrl)
            ? InstallerConstants.DefaultOllamaRouterEndpoint
            : routerBaseUrl.Trim().TrimEnd('/');

        var probe = await _environmentProvisioner.ProbeAsync(effectiveDistro, cancellationToken).ConfigureAwait(false);
        var checks = new List<ForgeDoctorCheckItem>(6)
        {
            CheckWsl2(probe, effectiveDistro),
            CheckDocker(probe),
            await CheckOllamaRouterAsync(effectiveRouterUrl, cancellationToken).ConfigureAwait(false),
            await CheckWebPortAsync(effectiveInstallDir, cancellationToken).ConfigureAwait(false),
            await CheckCompanionToolsAsync(probe, effectiveDistro, cancellationToken).ConfigureAwait(false),
            await CheckDatabaseHealthAsync(effectiveInstallDir, cancellationToken).ConfigureAwait(false)
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
        return new ForgeDoctorCheckItem(
            InstallerConstants.DoctorCheckWsl2,
            DoctorCheckStatus.Pass,
            $"WSL2 active ({activeDistro}).",
            probe.WslDotNet10Installed ? ".NET 10 SDK present inside WSL2." : ".NET 10 SDK missing inside WSL2.");
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
        var probeUri = $"{routerBaseUrl}{InstallerConstants.OllamaRouterRecommendationsApiPath}";
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(InstallerConstants.DoctorProbeTimeoutSeconds));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, probeUri);
            using var response = await _httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return new ForgeDoctorCheckItem(
                    InstallerConstants.DoctorCheckOllamaRouter,
                    DoctorCheckStatus.Pass,
                    $"OllamaRouter reachable at {routerBaseUrl}.",
                    "Role recommendations endpoint responded HTTP 200.");
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

        var configuredUrl = ReadConfiguredWebUrl(installDirectory);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(InstallerConstants.DoctorProbeTimeoutSeconds));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, configuredUrl);
            using var response = await _httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;

            if ((statusCode >= 200 && statusCode < 400) || response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new ForgeDoctorCheckItem(
                    InstallerConstants.DoctorCheckWebPort,
                    DoctorCheckStatus.Pass,
                    $"Listening at {configuredUrl} (HTTP {statusCode}).",
                    isProcessRunning ? "NightCodeForge.Web process active." : "HTTP endpoint responded.");
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
        var dataDir = Path.Combine(installDirectory, InstallerConstants.DataDirectoryName);
        if (!Directory.Exists(dataDir))
        {
            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckDatabase,
                DoctorCheckStatus.Fail,
                "Data directory is missing.",
                $"Expected directory: {dataDir}");
        }

        var dbPath = Path.Combine(dataDir, InstallerConstants.MainDatabaseFileName);
        if (!File.Exists(dbPath))
        {
            return new ForgeDoctorCheckItem(
                InstallerConstants.DoctorCheckDatabase,
                DoctorCheckStatus.Warning,
                "Will initialize on first server launch.",
                $"Data directory exists ({dataDir}); database file not created yet.");
        }

        try
        {
            var expectedMagic = Encoding.ASCII.GetBytes(InstallerConstants.SqliteFileHeaderMagic);
            var buffer = new byte[expectedMagic.Length];

            await using var stream = new FileStream(
                dbPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, expectedMagic.Length), cancellationToken)
                .ConfigureAwait(false);

            if (bytesRead < expectedMagic.Length || !buffer.AsSpan().SequenceEqual(expectedMagic))
            {
                return new ForgeDoctorCheckItem(
                    InstallerConstants.DoctorCheckDatabase,
                    DoctorCheckStatus.Fail,
                    "SQLite database header is invalid or corrupt.",
                    $"File: {dbPath}");
            }

            var sizeKb = Math.Max(1L, stream.Length / 1024L);
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

    private static string ReadConfiguredWebUrl(string installDirectory)
    {
        foreach (var fileName in new[] { InstallerConstants.AppSettingsProductionFileName, InstallerConstants.AppSettingsFileName })
        {
            var configPath = Path.Combine(installDirectory, fileName);
            if (!File.Exists(configPath))
            {
                continue;
            }

            try
            {
                var raw = File.ReadAllText(configPath);
                var docOptions = new System.Text.Json.JsonDocumentOptions
                {
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                var root = System.Text.Json.Nodes.JsonNode.Parse(raw, nodeOptions: null, documentOptions: docOptions)
                    as System.Text.Json.Nodes.JsonObject;
                var urls = root?["Urls"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(urls))
                {
                    return HostAppControlService.NormalizeListeningUrlToLocalhost(urls);
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // Justification: Fall back to default port 5270 URL if config is temporarily unreadable or invalid.
                _ = ex;
            }
        }

        return InstallerConstants.DefaultWebUrls;
    }
}
