namespace NightCodeForge.Installer.Core.Services;

using System.Globalization;
using System.Text;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Cross-platform OS integration service using pure deterministic plan builders and safe non-elevated user scopes.
/// </summary>
public sealed class OsIntegrationService : IOsIntegrationService
{
    private readonly IPlatformEnvironment _platform;
    private readonly IWindowsRegistryAccessor _registry;
    private readonly IProcessRunner _processRunner;
    private readonly TimeProvider _timeProvider;

    public OsIntegrationService(
        IPlatformEnvironment platform,
        IWindowsRegistryAccessor registry,
        IProcessRunner processRunner,
        TimeProvider timeProvider)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public WindowsUninstallRegistryPlan CreateWindowsUninstallRegistryPlan(
        string installDirectory,
        string version,
        int estimatedSizeKb,
        DateTimeOffset installDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var normalizedInstallDir = NormalizeWindowsPath(installDirectory);
        var separator = normalizedInstallDir.EndsWith('\\') ? string.Empty : "\\";
        var installerExePath = $"{normalizedInstallDir}{separator}{InstallerConstants.InstallerExeWindows}";

        return new WindowsUninstallRegistryPlan(
            RegistryKeyPath: InstallerConstants.WindowsUninstallRegistrySubKey,
            DisplayName: InstallerConstants.ApplicationDisplayName,
            DisplayVersion: version.Trim(),
            Publisher: InstallerConstants.PublisherName,
            InstallLocation: normalizedInstallDir,
            DisplayIcon: $"{installerExePath},0",
            UninstallString: $"\"{installerExePath}\" uninstall",
            QuietUninstallString: $"\"{installerExePath}\" uninstall --quiet",
            ModifyPath: $"\"{installerExePath}\"",
            NoModify: 0,
            NoRepair: 1,
            EstimatedSizeKb: Math.Max(0, estimatedSizeKb),
            InstallDate: installDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
    }

    public WindowsProtocolRegistrationPlan CreateWindowsOllamaRouterProtocolPlan(string appExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appExecutablePath);

        var normalizedExePath = NormalizeWindowsPath(appExecutablePath);

        return new WindowsProtocolRegistrationPlan(
            SchemeKeyPath: InstallerConstants.WindowsOllamaRouterProtocolSubKey,
            DisplayName: InstallerConstants.OllamaRouterProtocolDisplayName,
            UrlProtocolValue: string.Empty,
            IconKeyPath: $@"{InstallerConstants.WindowsOllamaRouterProtocolSubKey}\DefaultIcon",
            IconValue: $"\"{normalizedExePath}\",0",
            CommandKeyPath: $@"{InstallerConstants.WindowsOllamaRouterProtocolSubKey}\shell\open\command",
            CommandValue: $"\"{normalizedExePath}\" \"%1\"");
    }

    public LinuxProtocolRegistrationPlan CreateLinuxOllamaRouterProtocolPlan(
        string appExecutablePath,
        string homeDirectory,
        bool xdgMimeAvailable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);

        var posixExePath = NormalizePosixPath(appExecutablePath);
        var desktopEntryPath = Path.Combine(
            homeDirectory,
            ".local",
            "share",
            "applications",
            InstallerConstants.OllamaRouterDesktopEntryFileName);

        var content =
            $"[Desktop Entry]\n" +
            $"Version=1.0\n" +
            $"Type=Application\n" +
            $"Name=OllamaRouter\n" +
            $"Comment=OllamaRouter dashboard and setup wizard\n" +
            $"Exec=\"{posixExePath}\" %u\n" +
            $"Terminal=false\n" +
            $"MimeType={InstallerConstants.OllamaRouterMimeType};\n" +
            $"StartupNotify=true\n";

        var xdgMimeCommand = xdgMimeAvailable
            ? $"xdg-mime default {InstallerConstants.OllamaRouterDesktopEntryFileName} {InstallerConstants.OllamaRouterMimeType}"
            : null;

        return new LinuxProtocolRegistrationPlan(
            DesktopEntryPath: desktopEntryPath,
            DesktopEntryContent: content,
            MimeType: InstallerConstants.OllamaRouterMimeType,
            XdgMimeCommand: xdgMimeCommand);
    }

    public LinuxDesktopEntryPlan CreateLinuxDesktopEntryPlans(string installDirectory, string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);

        var posixInstallDir = NormalizePosixPath(installDirectory);
        var applicationsDir = Path.Combine(homeDirectory, ".local", "share", "applications");
        var webLauncherPath = $"{posixInstallDir}/{InstallerConstants.BinDirectoryName}/{InstallerConstants.LauncherShortUnix}";
        var installerBinaryPath = $"{posixInstallDir}/{InstallerConstants.InstallerExeUnix}";

        var webDesktopContent =
            $"[Desktop Entry]\n" +
            $"Version=1.0\n" +
            $"Type=Application\n" +
            $"Name={InstallerConstants.ApplicationDisplayName}\n" +
            $"Comment=NightCodeForge Autonomous Coding Agent Server\n" +
            $"Exec=\"{webLauncherPath}\"\n" +
            $"Path={posixInstallDir}\n" +
            $"Terminal=true\n" +
            $"Categories=Development;IDE;\n";

        var installerDesktopContent =
            $"[Desktop Entry]\n" +
            $"Version=1.0\n" +
            $"Type=Application\n" +
            $"Name=NightCodeForge Maintenance & Setup\n" +
            $"Comment=Manage, update, or uninstall NightCodeForge\n" +
            $"Exec=\"{installerBinaryPath}\"\n" +
            $"Path={posixInstallDir}\n" +
            $"Terminal=false\n" +
            $"Categories=Development;Utility;\n";

        return new LinuxDesktopEntryPlan(
            WebAppDesktopPath: Path.Combine(applicationsDir, InstallerConstants.NightCodeForgeDesktopEntryFileName),
            WebAppDesktopContent: webDesktopContent,
            InstallerDesktopPath: Path.Combine(applicationsDir, InstallerConstants.NightCodeForgeInstallerDesktopEntryFileName),
            InstallerDesktopContent: installerDesktopContent);
    }

    public MacOsAppWrapperPlan CreateMacOsAppWrapperPlan(string installDirectory, string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);

        var posixInstallDir = NormalizePosixPath(installDirectory);
        var appBundleDir = Path.Combine(homeDirectory, "Applications", InstallerConstants.MacOsAppBundleName);
        var contentsDir = Path.Combine(appBundleDir, "Contents");
        var macOsDir = Path.Combine(contentsDir, "MacOS");
        var installerPath = $"{posixInstallDir}/{InstallerConstants.InstallerExeUnix}";

        var infoPlist =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n" +
            "<dict>\n" +
            "  <key>CFBundleName</key>\n" +
            "  <string>NightCodeForge</string>\n" +
            "  <key>CFBundleIdentifier</key>\n" +
            "  <string>io.github.jetelain.nightcodeforge</string>\n" +
            "  <key>CFBundleExecutable</key>\n" +
            "  <string>NightCodeForge</string>\n" +
            "  <key>CFBundlePackageType</key>\n" +
            "  <string>APPL</string>\n" +
            "</dict>\n" +
            "</plist>\n";

        var launcherScript =
            "#!/usr/bin/env bash\n" +
            $"exec \"{installerPath}\" \"$@\"\n";

        return new MacOsAppWrapperPlan(
            AppBundleDirectory: appBundleDir,
            InfoPlistPath: Path.Combine(contentsDir, "Info.plist"),
            InfoPlistContent: infoPlist,
            LauncherScriptPath: Path.Combine(macOsDir, "NightCodeForge"),
            LauncherScriptContent: launcherScript);
    }

    public async Task RegisterNightCodeForgeInstallationAsync(
        string installDirectory,
        string version,
        bool createDesktopShortcut,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        switch (_platform.Platform)
        {
            case HostPlatformType.Windows:
                await RegisterWindowsNightCodeForgeAsync(installDirectory, version, createDesktopShortcut, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case HostPlatformType.Linux:
                await RegisterLinuxNightCodeForgeAsync(installDirectory, cancellationToken).ConfigureAwait(false);
                break;

            case HostPlatformType.MacOS:
                await RegisterMacOsNightCodeForgeAsync(installDirectory, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    public Task UnregisterNightCodeForgeInstallationAsync(
        string installDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (_platform.Platform)
        {
            case HostPlatformType.Windows:
                _registry.DeleteCurrentUserSubKeyTree(InstallerConstants.WindowsUninstallRegistrySubKey);
                DeleteFileIfExists(GetWindowsStartMenuShortcutPath());
                DeleteFileIfExists(GetWindowsDesktopShortcutPath());
                break;

            case HostPlatformType.Linux:
                var linuxPlan = CreateLinuxDesktopEntryPlans(installDirectory, _platform.UserHomeDirectory);
                DeleteFileIfExists(linuxPlan.WebAppDesktopPath);
                DeleteFileIfExists(linuxPlan.InstallerDesktopPath);
                break;

            case HostPlatformType.MacOS:
                var macPlan = CreateMacOsAppWrapperPlan(installDirectory, _platform.UserHomeDirectory);
                if (Directory.Exists(macPlan.AppBundleDirectory))
                {
                    Directory.Delete(macPlan.AppBundleDirectory, recursive: true);
                }

                break;
        }

        return Task.CompletedTask;
    }

    public async Task RegisterOllamaRouterProtocolAsync(
        string appExecutablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appExecutablePath);

        if (_platform.Platform == HostPlatformType.Windows)
        {
            var plan = CreateWindowsOllamaRouterProtocolPlan(appExecutablePath);
            _registry.WriteCurrentUserValues(
                plan.SchemeKeyPath,
                new Dictionary<string, object>
                {
                    [string.Empty] = plan.DisplayName,
                    ["URL Protocol"] = plan.UrlProtocolValue
                });
            _registry.WriteCurrentUserValues(
                plan.IconKeyPath,
                new Dictionary<string, object>
                {
                    [string.Empty] = plan.IconValue
                });
            _registry.WriteCurrentUserValues(
                plan.CommandKeyPath,
                new Dictionary<string, object>
                {
                    [string.Empty] = plan.CommandValue
                });
            return;
        }

        if (_platform.Platform == HostPlatformType.Linux)
        {
            var xdgMimeAvailable = _platform.IsCommandOnPath("xdg-mime");
            var plan = CreateLinuxOllamaRouterProtocolPlan(appExecutablePath, _platform.UserHomeDirectory, xdgMimeAvailable);
            var parent = Path.GetDirectoryName(plan.DesktopEntryPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await File.WriteAllTextAsync(plan.DesktopEntryPath, plan.DesktopEntryContent, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            if (plan.XdgMimeCommand is not null)
            {
                await _processRunner.RunAsync(
                    new ProcessRunRequest
                    {
                        FileName = "xdg-mime",
                        Arguments = ["default", InstallerConstants.OllamaRouterDesktopEntryFileName, InstallerConstants.OllamaRouterMimeType],
                        Timeout = TimeSpan.FromMilliseconds(InstallerConstants.XdgMimeTimeoutMs)
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task UnregisterOllamaRouterProtocolAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_platform.Platform == HostPlatformType.Windows)
        {
            _registry.DeleteCurrentUserSubKeyTree(InstallerConstants.WindowsOllamaRouterProtocolSubKey);
        }
        else if (_platform.Platform == HostPlatformType.Linux)
        {
            var desktopEntryPath = Path.Combine(
                _platform.UserHomeDirectory,
                ".local",
                "share",
                "applications",
                InstallerConstants.OllamaRouterDesktopEntryFileName);
            DeleteFileIfExists(desktopEntryPath);
        }

        return Task.CompletedTask;
    }

    public void AddDirectoryToUserPath(string directoryPath, string profileBlockLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileBlockLabel);

        var normalizedDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));

        if (_platform.Platform == HostPlatformType.Windows)
        {
            var current = _platform.GetUserPathVariable() ?? string.Empty;
            var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(static p => p.Trim())
                .Where(static p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            var alreadyPresent = parts.Any(entry =>
                string.Equals(NormalizeWindowsPathEntry(entry), normalizedDir, StringComparison.OrdinalIgnoreCase));

            if (!alreadyPresent)
            {
                parts.Add(normalizedDir);
                _platform.SetUserPathVariable(string.Join(';', parts));
            }

            return;
        }

        UpdateUnixShellProfiles(normalizedDir, profileBlockLabel, removeOnly: false);
    }

    public void RemoveDirectoryFromUserPath(string directoryPath, string profileBlockLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileBlockLabel);

        var normalizedDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));

        if (_platform.Platform == HostPlatformType.Windows)
        {
            var current = _platform.GetUserPathVariable();
            if (string.IsNullOrWhiteSpace(current))
            {
                return;
            }

            var filtered = current.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(static p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p) &&
                            !string.Equals(NormalizeWindowsPathEntry(p), normalizedDir, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            _platform.SetUserPathVariable(string.Join(';', filtered));
            return;
        }

        UpdateUnixShellProfiles(normalizedDir, profileBlockLabel, removeOnly: true);
    }

    internal static string BuildWindowsShortcutPowerShellScript(
        string shortcutPath,
        string targetPath,
        string workingDirectory,
        string description,
        string iconLocation)
    {
        static string Esc(string value) => value.Replace("'", "''", StringComparison.Ordinal);

        return
            $"$ErrorActionPreference = 'Stop'\n" +
            $"$parent = Split-Path -Parent '{Esc(shortcutPath)}'\n" +
            $"if (-not (Test-Path $parent)) {{ New-Item -ItemType Directory -Path $parent -Force | Out-Null }}\n" +
            $"$ws = New-Object -ComObject WScript.Shell\n" +
            $"$sc = $ws.CreateShortcut('{Esc(shortcutPath)}')\n" +
            $"$sc.TargetPath = '{Esc(targetPath)}'\n" +
            $"$sc.WorkingDirectory = '{Esc(workingDirectory)}'\n" +
            $"$sc.Description = '{Esc(description)}'\n" +
            $"$sc.IconLocation = '{Esc(iconLocation)}'\n" +
            $"$sc.Save()\n";
    }

    internal static string ApplyManagedUnixPathBlock(
        string existingContent,
        string directoryPath,
        string blockLabel,
        bool removeOnly)
    {
        var startMarker = $"{InstallerConstants.UnixPathBlockStartPrefix}{blockLabel}{InstallerConstants.UnixPathBlockSuffix}";
        var endMarker = $"{InstallerConstants.UnixPathBlockEndPrefix}{blockLabel}{InstallerConstants.UnixPathBlockEndSuffix}";

        var lines = existingContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var filtered = new List<string>(lines.Count);

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim() == startMarker)
            {
                var endIdx = -1;
                for (var j = i + 1; j < lines.Count; j++)
                {
                    if (lines[j].Trim() == endMarker)
                    {
                        endIdx = j;
                        break;
                    }
                }

                if (endIdx >= 0)
                {
                    i = endIdx;
                    continue;
                }
            }

            filtered.Add(lines[i]);
        }

        while (filtered.Count > 0 && string.IsNullOrWhiteSpace(filtered[^1]))
        {
            filtered.RemoveAt(filtered.Count - 1);
        }

        if (!removeOnly)
        {
            if (filtered.Count > 0)
            {
                filtered.Add(string.Empty);
            }

            var escapedDir = EscapeDoubleQuotedShellValue(directoryPath);
            filtered.Add(startMarker);
            filtered.Add($"export PATH=\"$PATH:{escapedDir}\"");
            filtered.Add(endMarker);
        }

        return filtered.Count == 0
            ? string.Empty
            : string.Join('\n', filtered) + '\n';
    }

    private async Task RegisterWindowsNightCodeForgeAsync(
        string installDirectory,
        string version,
        bool createDesktopShortcut,
        CancellationToken cancellationToken)
    {
        var estimatedSizeKb = ComputeDirectorySizeKb(installDirectory);
        var plan = CreateWindowsUninstallRegistryPlan(
            installDirectory,
            version,
            estimatedSizeKb,
            _timeProvider.GetUtcNow());

        _registry.WriteCurrentUserValues(
            plan.RegistryKeyPath,
            new Dictionary<string, object>
            {
                ["DisplayName"] = plan.DisplayName,
                ["DisplayVersion"] = plan.DisplayVersion,
                ["Publisher"] = plan.Publisher,
                ["InstallLocation"] = plan.InstallLocation,
                ["DisplayIcon"] = plan.DisplayIcon,
                ["UninstallString"] = plan.UninstallString,
                ["QuietUninstallString"] = plan.QuietUninstallString,
                ["ModifyPath"] = plan.ModifyPath,
                ["NoModify"] = plan.NoModify,
                ["NoRepair"] = plan.NoRepair,
                ["EstimatedSize"] = plan.EstimatedSizeKb,
                ["InstallDate"] = plan.InstallDate
            });

        var normalizedInstallDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
        var installerExe = Path.Combine(normalizedInstallDir, InstallerConstants.InstallerExeWindows);

        await CreateWindowsShortcutAsync(
            GetWindowsStartMenuShortcutPath(),
            installerExe,
            normalizedInstallDir,
            InstallerConstants.ApplicationDisplayName,
            $"{installerExe},0",
            cancellationToken).ConfigureAwait(false);

        if (createDesktopShortcut)
        {
            await CreateWindowsShortcutAsync(
                GetWindowsDesktopShortcutPath(),
                installerExe,
                normalizedInstallDir,
                InstallerConstants.ApplicationDisplayName,
                $"{installerExe},0",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CreateWindowsShortcutAsync(
        string shortcutPath,
        string targetPath,
        string workingDirectory,
        string description,
        string iconLocation,
        CancellationToken cancellationToken)
    {
        var script = BuildWindowsShortcutPowerShellScript(
            shortcutPath,
            targetPath,
            workingDirectory,
            description,
            iconLocation);

        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var result = await _processRunner.RunAsync(
            new ProcessRunRequest
            {
                FileName = InstallerConstants.PowerShellExecutable,
                Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encodedCommand]
            },
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != InstallerConstants.ExitCodeSuccess)
        {
            var err = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new InstallerException(
                $"Failed to create Windows shortcut '{shortcutPath}' (exit code {result.ExitCode}): {err}".Trim());
        }
    }

    private async Task RegisterLinuxNightCodeForgeAsync(string installDirectory, CancellationToken cancellationToken)
    {
        var plan = CreateLinuxDesktopEntryPlans(installDirectory, _platform.UserHomeDirectory);
        var appsDir = Path.GetDirectoryName(plan.WebAppDesktopPath);
        if (!string.IsNullOrWhiteSpace(appsDir))
        {
            Directory.CreateDirectory(appsDir);
        }

        var utf8NoBom = new UTF8Encoding(false);
        await File.WriteAllTextAsync(plan.WebAppDesktopPath, plan.WebAppDesktopContent, utf8NoBom, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(plan.InstallerDesktopPath, plan.InstallerDesktopContent, utf8NoBom, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RegisterMacOsNightCodeForgeAsync(string installDirectory, CancellationToken cancellationToken)
    {
        var plan = CreateMacOsAppWrapperPlan(installDirectory, _platform.UserHomeDirectory);
        var macOsDir = Path.GetDirectoryName(plan.LauncherScriptPath);
        if (!string.IsNullOrWhiteSpace(macOsDir))
        {
            Directory.CreateDirectory(macOsDir);
        }

        var utf8NoBom = new UTF8Encoding(false);
        await File.WriteAllTextAsync(plan.InfoPlistPath, plan.InfoPlistContent, utf8NoBom, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(plan.LauncherScriptPath, plan.LauncherScriptContent, utf8NoBom, cancellationToken)
            .ConfigureAwait(false);
        _platform.SetUnixExecutablePermissions(plan.LauncherScriptPath);
    }

    private void UpdateUnixShellProfiles(string directoryPath, string blockLabel, bool removeOnly)
    {
        var home = _platform.UserHomeDirectory;
        var profileCandidates = _platform.Platform == HostPlatformType.MacOS
            ? new[]
            {
                Path.Combine(home, ".zprofile"),
                Path.Combine(home, ".zshrc"),
                Path.Combine(home, ".profile"),
                Path.Combine(home, ".bashrc")
            }
            : new[]
            {
                Path.Combine(home, ".profile"),
                Path.Combine(home, ".bashrc")
            };

        var primaryProfile = profileCandidates[0];
        var utf8NoBom = new UTF8Encoding(false);

        foreach (var profilePath in profileCandidates)
        {
            var isPrimary = string.Equals(profilePath, primaryProfile, StringComparison.Ordinal);
            if (!File.Exists(profilePath))
            {
                if (removeOnly || !isPrimary)
                {
                    continue;
                }
            }

            var existing = File.Exists(profilePath) ? File.ReadAllText(profilePath) : string.Empty;
            var updated = ApplyManagedUnixPathBlock(existing, directoryPath, blockLabel, removeOnly);
            if (!string.Equals(existing, updated, StringComparison.Ordinal))
            {
                File.WriteAllText(profilePath, updated, utf8NoBom);
            }
        }
    }

    private string GetWindowsStartMenuShortcutPath() => Path.Combine(
        _platform.ApplicationDataDirectory,
        "Microsoft",
        "Windows",
        "Start Menu",
        "Programs",
        InstallerConstants.WindowsShortcutFileName);

    private string GetWindowsDesktopShortcutPath() => Path.Combine(
        _platform.DesktopDirectory,
        InstallerConstants.WindowsShortcutFileName);

    private static int ComputeDirectorySizeKb(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long totalBytes = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                totalBytes += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Justification: Transient locked file during size estimation should not block uninstall registry update.
                _ = ex;
            }
        }

        return (int)Math.Min(int.MaxValue, totalBytes / 1024L);
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string NormalizeWindowsPath(string path)
    {
        var trimmed = path.Trim().Replace('/', '\\');
        if (OperatingSystem.IsWindows() && !Path.IsPathRooted(trimmed))
        {
            trimmed = Path.GetFullPath(trimmed);
        }

        if (trimmed.Length == 3 && trimmed[1] == ':' && trimmed[2] == '\\')
        {
            return trimmed;
        }

        return trimmed.TrimEnd('\\');
    }

    private static string NormalizePosixPath(string path)
    {
        var trimmed = path.Trim().Replace('\\', '/');
        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }

    private static string NormalizeWindowsPathEntry(string entry) =>
        Path.TrimEndingDirectorySeparator(entry.Trim().Trim('"'));

    private static string EscapeDoubleQuotedShellValue(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);
}
