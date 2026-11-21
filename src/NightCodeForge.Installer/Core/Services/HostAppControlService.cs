namespace NightCodeForge.Installer.Core.Services;

using System.ComponentModel;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Cross-platform launcher and process control helper for NightCodeForge Web, CLI terminals,
/// NightCodeAssay, OllamaRouter.App, and air-gapped local bundle inspection.
/// </summary>
public sealed class HostAppControlService : IHostAppControlService
{
    private static readonly string[] LinuxTerminalCandidates =
    [
        "x-terminal-emulator",
        "gnome-terminal",
        "konsole",
        "xfce4-terminal",
        "ptyxis",
        "kitty",
        "alacritty",
        "xterm"
    ];

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
            var urlsValue = InstalledAppConfigReader.ReadFirstNonEmptyString(installDirectory, InstallerConstants.KeyUrls);
            if (!string.IsNullOrWhiteSpace(urlsValue))
            {
                return NormalizeListeningUrlToLocalhost(urlsValue);
            }
        }

        return InstallerConstants.DefaultWebUrls;
    }

    public async Task<bool> StartWebServerAsync(string installDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);

        if (IsWebServerRunning())
        {
            return true;
        }

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

        return await RunDetachedSafeAsync(
            new ProcessRunRequest
            {
                FileName = executable,
                WorkingDirectory = installDirectory,
                Detached = true
            },
            cancellationToken).ConfigureAwait(false);
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

        return !IsWebServerRunning() && (stoppedLaunchers || stoppedWeb);
    }

    public Task<bool> OpenWebUiInBrowserAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return Task.FromResult(false);
        }

        return OpenTargetWithShellAsync(trimmed, cancellationToken);
    }

    public async Task<bool> OpenCliTerminalAsync(string installDirectory, CancellationToken cancellationToken = default)
    {
        var workingDir = ResolveExistingWorkingDirectory(installDirectory);

        ProcessRunRequest? request;
        switch (_platform.Platform)
        {
            case HostPlatformType.Windows:
                request = new ProcessRunRequest
                {
                    FileName = InstallerConstants.CmdExecutable,
                    Arguments = ["/k"],
                    WorkingDirectory = workingDir,
                    UseShellExecute = true,
                    Detached = true
                };
                break;

            case HostPlatformType.MacOS:
                request = new ProcessRunRequest
                {
                    FileName = InstallerConstants.OpenExecutable,
                    Arguments = ["-a", "Terminal", workingDir],
                    WorkingDirectory = workingDir,
                    Detached = true
                };
                break;

            default:
                var terminal = LinuxTerminalCandidates.FirstOrDefault(_platform.IsCommandOnPath);
                if (terminal is null)
                {
                    return false;
                }

                request = new ProcessRunRequest
                {
                    FileName = terminal,
                    WorkingDirectory = workingDir,
                    Detached = true
                };
                break;
        }

        return await RunDetachedSafeAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> LaunchNightCodeAssayAsync(CancellationToken cancellationToken = default)
    {
        var isWindows = _platform.Platform == HostPlatformType.Windows;
        var toolFileName = isWindows
            ? $"{CompanionToolCatalog.CommandNightCodeAssay}{InstallerConstants.ExeExtension}"
            : CompanionToolCatalog.CommandNightCodeAssay;
        var globalToolPath = Path.Combine(
            _platform.UserHomeDirectory,
            InstallerConstants.DotNetHomeDirectoryName,
            InstallerConstants.DotNetToolsDirectoryName,
            toolFileName);

        var fileName = File.Exists(globalToolPath)
            ? globalToolPath
            : toolFileName;

        return await RunDetachedSafeAsync(
            new ProcessRunRequest
            {
                FileName = fileName,
                Detached = true
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> OpenOllamaRouterAppAsync(CancellationToken cancellationToken = default)
    {
        var discovery = _installationDetector.Detect();
        var isWindows = _platform.Platform == HostPlatformType.Windows;
        var appFileName = isWindows
            ? InstallerConstants.OllamaRouterAppWindows
            : InstallerConstants.OllamaRouterAppUnix;

        if (!string.IsNullOrWhiteSpace(discovery.OllamaRouter.InstallDirectory))
        {
            var appPath = Path.Combine(discovery.OllamaRouter.InstallDirectory, appFileName);
            if (File.Exists(appPath))
            {
                return await RunDetachedSafeAsync(
                    new ProcessRunRequest
                    {
                        FileName = appPath,
                        WorkingDirectory = discovery.OllamaRouter.InstallDirectory,
                        Detached = true
                    },
                    cancellationToken).ConfigureAwait(false);
            }
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

        try
        {
            return Directory.EnumerateFiles(bundleDirectory)
                .Select(Path.GetFileName)
                .Where(static name => !string.IsNullOrWhiteSpace(name) && IsKnownBundleAsset(name!))
                .Select(static name => name!)
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Justification: Unreadable or transient bundle folder during browse returns an empty asset list.
            _ = ex;
            return [];
        }
    }

    internal static string NormalizeListeningUrlToLocalhost(string rawUrls)
    {
        if (string.IsNullOrWhiteSpace(rawUrls))
        {
            return InstallerConstants.DefaultWebUrls;
        }

        foreach (var rawEntry in rawUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var rewritten = RewriteWildcardHost(rawEntry);
            if (rewritten is null)
            {
                continue;
            }

            if (Uri.TryCreate(rewritten, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                !string.IsNullOrWhiteSpace(uri.Host))
            {
                return rewritten.TrimEnd('/');
            }
        }

        return InstallerConstants.DefaultWebUrls;
    }

    private string ResolveExistingWorkingDirectory(string installDirectory)
    {
        if (!string.IsNullOrWhiteSpace(installDirectory))
        {
            try
            {
                var full = Path.GetFullPath(installDirectory.Trim());
                if (Directory.Exists(full))
                {
                    return full;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Justification: Malformed install path falls back to user home directory when launching a terminal.
                _ = ex;
            }
        }

        return _platform.UserHomeDirectory;
    }

    private async Task<bool> RunDetachedSafeAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            return result.ExitCode == InstallerConstants.ExitCodeSuccess;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            // Justification: Launcher UI helper methods return false when a target binary or shell handler fails to spawn.
            _ = ex;
            return false;
        }
    }

    private async Task<bool> OpenTargetWithShellAsync(string target, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps &&
             !string.Equals(uri.Scheme, InstallerConstants.OllamaRouterProtocolScheme, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

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
                FileName = InstallerConstants.OpenExecutable,
                Arguments = [target],
                Detached = true
            },
            _ => new ProcessRunRequest
            {
                FileName = InstallerConstants.XdgOpenExecutable,
                Arguments = [target],
                Detached = true
            }
        };

        return await RunDetachedSafeAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static string? RewriteWildcardHost(string rawUrl)
    {
        const string httpPrefix = "http://";
        const string httpsPrefix = "https://";

        string prefix;
        if (rawUrl.StartsWith(httpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            prefix = httpPrefix;
        }
        else if (rawUrl.StartsWith(httpsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            prefix = httpsPrefix;
        }
        else
        {
            return null;
        }

        var authorityAndRest = rawUrl[prefix.Length..];
        string[] wildcardHosts = ["[::]", "[0:0:0:0:0:0:0:0]", "0.0.0.0", "*", "+"];
        foreach (var wildcard in wildcardHosts)
        {
            if (authorityAndRest.StartsWith(wildcard, StringComparison.OrdinalIgnoreCase))
            {
                var suffix = authorityAndRest[wildcard.Length..];
                if (suffix.Length == 0 || suffix[0] is ':' or '/' or '?' or '#')
                {
                    return $"{prefix}localhost{suffix}";
                }
            }
        }

        return rawUrl;
    }

    private static bool IsKnownBundleAsset(string fileName) =>
        InstallationDetector.IsBundleAssetFile(fileName) ||
        string.Equals(fileName, InstallerConstants.Sha256SumsFileName, StringComparison.OrdinalIgnoreCase);
}
