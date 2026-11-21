namespace NightCodeForge.Installer.Core.Services;

using System.Text.Json;
using System.Text.Json.Nodes;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Cross-platform launcher and process control helper for NightCodeForge Web, CLI terminals,
/// NightCodeAssay, OllamaRouter.App, and air-gapped local bundle inspection.
/// </summary>
public sealed class HostAppControlService : IHostAppControlService
{
    private const string KeyUrls = "Urls";

    private readonly IPlatformEnvironment _platform;
    private readonly IProcessRunner _processRunner;
    private readonly IProcessControlService _processControl;
    private readonly IInstallationDetector _installationDetector;

    public HostAppControlService(
        IPlatformEnvironment platform,
        IProcessRunner processRunner,
        IProcessControlService processControl,
        IInstallationDetector installationDetector)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _processControl = processControl ?? throw new ArgumentNullException(nameof(processControl));
        _installationDetector = installationDetector ?? throw new ArgumentNullException(nameof(installationDetector));
    }

    public bool IsWebServerRunning() =>
        _processControl.IsAnyProcessRunning(InstallerConstants.NightCodeForgeWebProcesses) ||
        _processControl.IsAnyProcessRunning(InstallerConstants.NightCodeForgeLauncherProcesses);

    public string GetConfiguredWebUrl(string installDirectory)
    {
        if (!string.IsNullOrWhiteSpace(installDirectory) && Directory.Exists(installDirectory))
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
                    var docOptions = new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };
                    var root = JsonNode.Parse(raw, nodeOptions: null, documentOptions: docOptions) as JsonObject;
                    var urlsValue = root?[KeyUrls]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(urlsValue))
                    {
                        return NormalizeListeningUrlToLocalhost(urlsValue);
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    // Justification: Unreadable or malformed appsettings falls back to default NightCodeForge URL.
                    _ = ex;
                }
            }
        }

        return InstallerConstants.DefaultWebUrls;
    }

    public async Task<bool> StartWebServerAsync(string installDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);

        var isWindows = _platform.Platform == HostPlatformType.Windows;
        var launcherShort = isWindows ? InstallerConstants.LauncherShortWindows : InstallerConstants.LauncherShortUnix;
        var launcherLong = isWindows ? InstallerConstants.LauncherLongWindows : InstallerConstants.LauncherLongUnix;
        var webExe = isWindows ? InstallerConstants.WebExeWindows : InstallerConstants.WebExeUnix;

        string[] candidates =
        [
            Path.Combine(installDirectory, InstallerConstants.BinDirectoryName, launcherShort),
            Path.Combine(installDirectory, InstallerConstants.BinDirectoryName, launcherLong),
            Path.Combine(installDirectory, launcherLong),
            Path.Combine(installDirectory, webExe)
        ];

        var executable = candidates.FirstOrDefault(File.Exists);
        if (executable is null)
        {
            return false;
        }

        var result = await _processRunner.RunAsync(
            new ProcessRunRequest
            {
                FileName = executable,
                WorkingDirectory = installDirectory,
                Detached = true
            },
            cancellationToken).ConfigureAwait(false);

        return result.ExitCode == InstallerConstants.ExitCodeSuccess;
    }

    public async Task<bool> StopWebServerAsync(CancellationToken cancellationToken = default)
    {
        var stoppedLaunchers = await _processControl.StopProcessesAsync(
            InstallerConstants.NightCodeForgeLauncherProcesses,
            TimeSpan.FromSeconds(InstallerConstants.ProcessStopTimeoutSeconds),
            cancellationToken).ConfigureAwait(false);

        if (stoppedLaunchers)
        {
            await Task.Delay(InstallerConstants.LauncherStopDelayMs, cancellationToken).ConfigureAwait(false);
        }

        var stoppedWeb = await _processControl.StopProcessesAsync(
            InstallerConstants.NightCodeForgeWebProcesses,
            TimeSpan.FromSeconds(InstallerConstants.ProcessStopTimeoutSeconds),
            cancellationToken).ConfigureAwait(false);

        if (stoppedWeb)
        {
            await Task.Delay(InstallerConstants.WebStopDelayMs, cancellationToken).ConfigureAwait(false);
        }

        return stoppedLaunchers || stoppedWeb;
    }

    public Task<bool> OpenWebUiInBrowserAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return OpenTargetWithShellAsync(url.Trim(), cancellationToken);
    }

    public async Task<bool> OpenCliTerminalAsync(string installDirectory, CancellationToken cancellationToken = default)
    {
        var workingDir = !string.IsNullOrWhiteSpace(installDirectory) && Directory.Exists(installDirectory)
            ? installDirectory
            : _platform.UserHomeDirectory;

        ProcessRunRequest request = _platform.Platform switch
        {
            HostPlatformType.Windows => new ProcessRunRequest
            {
                FileName = InstallerConstants.CmdExecutable,
                Arguments = ["/k"],
                WorkingDirectory = workingDir,
                UseShellExecute = true,
                Detached = true
            },
            HostPlatformType.MacOS => new ProcessRunRequest
            {
                FileName = "open",
                Arguments = ["-a", "Terminal", workingDir],
                WorkingDirectory = workingDir,
                Detached = true
            },
            _ => new ProcessRunRequest
            {
                FileName = _platform.IsCommandOnPath("x-terminal-emulator") ? "x-terminal-emulator" : "gnome-terminal",
                WorkingDirectory = workingDir,
                Detached = true
            }
        };

        var result = await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == InstallerConstants.ExitCodeSuccess;
    }

    public async Task<bool> LaunchNightCodeAssayAsync(CancellationToken cancellationToken = default)
    {
        var isWindows = _platform.Platform == HostPlatformType.Windows;
        var toolFileName = isWindows
            ? $"{CompanionToolCatalog.CommandNightCodeAssay}.exe"
            : CompanionToolCatalog.CommandNightCodeAssay;
        var globalToolPath = Path.Combine(_platform.UserHomeDirectory, ".dotnet", "tools", toolFileName);

        var fileName = File.Exists(globalToolPath)
            ? globalToolPath
            : CompanionToolCatalog.CommandNightCodeAssay;

        var result = await _processRunner.RunAsync(
            new ProcessRunRequest
            {
                FileName = fileName,
                Detached = true
            },
            cancellationToken).ConfigureAwait(false);

        return result.ExitCode == InstallerConstants.ExitCodeSuccess;
    }

    public async Task<bool> OpenOllamaRouterAppAsync(CancellationToken cancellationToken = default)
    {
        var discovery = _installationDetector.Detect();
        var isWindows = _platform.Platform == HostPlatformType.Windows;
        var appFileName = isWindows
            ? InstallerConstants.OllamaRouterAppWindows
            : InstallerConstants.OllamaRouterAppUnix;

        var appPath = Path.Combine(discovery.OllamaRouter.InstallDirectory, appFileName);
        if (File.Exists(appPath))
        {
            var result = await _processRunner.RunAsync(
                new ProcessRunRequest
                {
                    FileName = appPath,
                    WorkingDirectory = discovery.OllamaRouter.InstallDirectory,
                    Detached = true
                },
                cancellationToken).ConfigureAwait(false);

            return result.ExitCode == InstallerConstants.ExitCodeSuccess;
        }

        return await OpenTargetWithShellAsync(InstallerConstants.OllamaRouterProtocolOpenUri, cancellationToken)
            .ConfigureAwait(false);
    }

    public IReadOnlyList<string> ScanBundleDirectoryAssets(string? bundleDirectory)
    {
        if (string.IsNullOrWhiteSpace(bundleDirectory) || !Directory.Exists(bundleDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(bundleDirectory)
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrWhiteSpace(name) && IsKnownBundleAsset(name!))
            .Select(static name => name!)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string NormalizeListeningUrlToLocalhost(string rawUrls)
    {
        var first = rawUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
        {
            return InstallerConstants.DefaultWebUrls;
        }

        var normalized = first
            .Replace("://+:", "://localhost:", StringComparison.OrdinalIgnoreCase)
            .Replace("://*:", "://localhost:", StringComparison.OrdinalIgnoreCase)
            .Replace("://0.0.0.0:", "://localhost:", StringComparison.OrdinalIgnoreCase)
            .Replace("://[::]:", "://localhost:", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        return Uri.TryCreate(normalized, UriKind.Absolute, out _)
            ? normalized
            : InstallerConstants.DefaultWebUrls;
    }

    private async Task<bool> OpenTargetWithShellAsync(string target, CancellationToken cancellationToken)
    {
        ProcessRunRequest request = _platform.Platform switch
        {
            HostPlatformType.Windows => new ProcessRunRequest
            {
                FileName = target,
                UseShellExecute = true,
                Detached = true
            },
            HostPlatformType.MacOS => new ProcessRunRequest
            {
                FileName = "open",
                Arguments = [target],
                Detached = true
            },
            _ => new ProcessRunRequest
            {
                FileName = "xdg-open",
                Arguments = [target],
                Detached = true
            }
        };

        var result = await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == InstallerConstants.ExitCodeSuccess;
    }

    private static bool IsKnownBundleAsset(string fileName) =>
        fileName.EndsWith(InstallerConstants.ZipExtension, StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(InstallerConstants.TarGzExtension, StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(InstallerConstants.NupkgExtension, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, InstallerConstants.Sha256SumsFileName, StringComparison.OrdinalIgnoreCase);
}
