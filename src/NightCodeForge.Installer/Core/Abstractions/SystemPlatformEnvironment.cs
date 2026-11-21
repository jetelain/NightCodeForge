namespace NightCodeForge.Installer.Core.Abstractions;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Real host environment implementation of <see cref="IPlatformEnvironment"/>.
/// </summary>
public sealed class SystemPlatformEnvironment : IPlatformEnvironment
{
    public HostPlatformType Platform
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return HostPlatformType.Windows;
            }

            if (OperatingSystem.IsMacOS())
            {
                return HostPlatformType.MacOS;
            }

            return HostPlatformType.Linux;
        }
    }

    public string CurrentRuntimeIdentifier
    {
        get
        {
            var isArm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            return Platform switch
            {
                HostPlatformType.Windows => isArm64 ? InstallerConstants.RuntimeWinArm64 : InstallerConstants.RuntimeWinX64,
                HostPlatformType.MacOS => isArm64 ? InstallerConstants.RuntimeOsxArm64 : InstallerConstants.RuntimeOsxX64,
                _ => isArm64 ? InstallerConstants.RuntimeLinuxArm64 : InstallerConstants.RuntimeLinuxX64
            };
        }
    }

    public string UserHomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string LocalApplicationDataDirectory
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return localAppData;
            }

            return Path.Combine(UserHomeDirectory, ".local", "share");
        }
    }

    public string ApplicationDataDirectory => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string DesktopDirectory => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public string InstallerBaseDirectory
    {
        get
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var dir = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
                }
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        }
    }

    public string? GetEnvironmentVariable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Environment.GetEnvironmentVariable(name);
    }

    public string? GetUserPathVariable()
    {
        if (OperatingSystem.IsWindows())
        {
            return Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
        }

        return Environment.GetEnvironmentVariable("PATH");
    }

    public void SetUserPathVariable(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (OperatingSystem.IsWindows())
        {
            Environment.SetEnvironmentVariable("Path", value, EnvironmentVariableTarget.User);
            var machinePath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
            var effectiveProcessPath = BuildEffectiveWindowsProcessPath(machinePath, value);
            Environment.SetEnvironmentVariable("Path", effectiveProcessPath, EnvironmentVariableTarget.Process);
        }
        else
        {
            Environment.SetEnvironmentVariable("PATH", value);
        }
    }

    internal static string BuildEffectiveWindowsProcessPath(string? machinePath, string? userPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<string>();

        AppendPathEntries(machinePath, seen, entries);
        AppendPathEntries(userPath, seen, entries);

        return string.Join(';', entries);
    }

    private static void AppendPathEntries(string? pathValue, HashSet<string> seen, List<string> entries)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return;
        }

        foreach (var rawEntry in pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = rawEntry.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var normalizedKey = Path.TrimEndingDirectorySeparator(trimmed.Trim('"'));
            if (string.IsNullOrWhiteSpace(normalizedKey))
            {
                continue;
            }

            if (seen.Add(normalizedKey))
            {
                entries.Add(trimmed);
            }
        }
    }

    public bool IsCommandOnPath(string commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return false;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return false;
        }

        var extensions = GetWindowsPathExtensions();
        foreach (var rawDir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = rawDir.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var directCandidate = Path.Combine(dir, commandName);
            if (File.Exists(directCandidate))
            {
                return true;
            }

            if (OperatingSystem.IsWindows() && !Path.HasExtension(commandName))
            {
                foreach (var ext in extensions)
                {
                    if (File.Exists(Path.Combine(dir, commandName + ext)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public void SetUnixExecutablePermissions(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (OperatingSystem.IsWindows() || !File.Exists(filePath))
        {
            return;
        }

        SetUnixExecutablePermissionsCore(filePath);
    }

    [UnsupportedOSPlatform("windows")]
    private static void SetUnixExecutablePermissionsCore(string filePath)
    {
        File.SetUnixFileMode(
            filePath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute);
    }

    private static string[] GetWindowsPathExtensions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExt))
        {
            return [".exe", ".cmd", ".bat"];
        }

        return pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
