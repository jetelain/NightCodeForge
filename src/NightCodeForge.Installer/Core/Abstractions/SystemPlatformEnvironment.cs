namespace NightCodeForge.Installer.Core.Abstractions;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Real host environment implementation of <see cref="IPlatformEnvironment"/>.
/// </summary>
public sealed partial class SystemPlatformEnvironment : IPlatformEnvironment
{
    private const nint HwndBroadcast = 0xffff;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint SettingChangeTimeoutMs = 1000;

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

            if (OperatingSystem.IsLinux())
            {
                return HostPlatformType.Linux;
            }

            throw new PlatformNotSupportedException($"Unsupported operating system: '{RuntimeInformation.OSDescription}'.");
        }
    }

    public string CurrentRuntimeIdentifier =>
        GetRuntimeIdentifier(Platform, RuntimeInformation.OSArchitecture);

    internal static string GetRuntimeIdentifier(HostPlatformType platform, Architecture architecture) =>
        (platform, architecture) switch
        {
            (HostPlatformType.Windows, Architecture.X64) => InstallerConstants.RuntimeWinX64,
            (HostPlatformType.Windows, Architecture.Arm64) => InstallerConstants.RuntimeWinArm64,
            (HostPlatformType.MacOS, Architecture.X64) => InstallerConstants.RuntimeOsxX64,
            (HostPlatformType.MacOS, Architecture.Arm64) => InstallerConstants.RuntimeOsxArm64,
            (HostPlatformType.Linux, Architecture.X64) => InstallerConstants.RuntimeLinuxX64,
            (HostPlatformType.Linux, Architecture.Arm64) => InstallerConstants.RuntimeLinuxArm64,
            _ => throw new PlatformNotSupportedException(
                $"Unsupported platform/architecture combination: {platform} ({architecture}).")
        };

    public string UserHomeDirectory
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
            if (!string.IsNullOrWhiteSpace(home))
            {
                return home;
            }

            return Environment.GetEnvironmentVariable("HOME")
                   ?? Environment.GetEnvironmentVariable("USERPROFILE")
                   ?? Path.GetTempPath();
        }
    }

    public string LocalApplicationDataDirectory
    {
        get
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return localAppData;
            }

            return Platform switch
            {
                HostPlatformType.Windows => Path.Combine(UserHomeDirectory, "AppData", "Local"),
                HostPlatformType.MacOS => Path.Combine(UserHomeDirectory, "Library", "Application Support"),
                _ => Path.Combine(UserHomeDirectory, ".local", "share")
            };
        }
    }

    public string ApplicationDataDirectory
    {
        get
        {
            var appData = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);
            if (!string.IsNullOrWhiteSpace(appData))
            {
                return appData;
            }

            return Platform switch
            {
                HostPlatformType.Windows => Path.Combine(UserHomeDirectory, "AppData", "Roaming"),
                HostPlatformType.MacOS => Path.Combine(UserHomeDirectory, "Library", "Application Support"),
                _ => Path.Combine(UserHomeDirectory, ".config")
            };
        }
    }

    public string DesktopDirectory
    {
        get
        {
            var desktop = Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolderOption.DoNotVerify);
            return !string.IsNullOrWhiteSpace(desktop)
                ? desktop
                : Path.Combine(UserHomeDirectory, "Desktop");
        }
    }

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
            return GetWindowsUserPathRaw();
        }

        return Environment.GetEnvironmentVariable(InstallerConstants.EnvPathUnix);
    }

    public void SetUserPathVariable(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (OperatingSystem.IsWindows())
        {
            SetWindowsUserPathPreservingKind(value);
            RefreshProcessPath();
        }
        else
        {
            Environment.SetEnvironmentVariable(InstallerConstants.EnvPathUnix, value);
        }
    }

    public void RefreshProcessPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var machinePath = Environment.GetEnvironmentVariable(InstallerConstants.EnvPathWindows, EnvironmentVariableTarget.Machine);
        var userPath = Environment.GetEnvironmentVariable(InstallerConstants.EnvPathWindows, EnvironmentVariableTarget.User);
        var currentProcessPath = Environment.GetEnvironmentVariable(InstallerConstants.EnvPathWindows, EnvironmentVariableTarget.Process);
        var effectiveProcessPath = BuildEffectiveWindowsProcessPath(machinePath, userPath, currentProcessPath);
        Environment.SetEnvironmentVariable(InstallerConstants.EnvPathWindows, effectiveProcessPath, EnvironmentVariableTarget.Process);
    }

    internal static string BuildEffectiveWindowsProcessPath(string? machinePath, string? userPath) =>
        BuildEffectiveWindowsProcessPath(machinePath, userPath, currentProcessPath: null);

    internal static string BuildEffectiveWindowsProcessPath(
        string? machinePath,
        string? userPath,
        string? currentProcessPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<string>();

        AppendPathEntries(machinePath, seen, entries);
        AppendPathEntries(userPath, seen, entries);
        AppendPathEntries(currentProcessPath, seen, entries);

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

            var normalizedKey = trimmed.Trim('"').Replace('/', '\\').TrimEnd('\\');
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

    public bool IsCommandOnPath(string commandName) =>
        IsCommandOnPath(
            commandName,
            Environment.GetEnvironmentVariable(InstallerConstants.EnvPathUnix),
            OperatingSystem.IsWindows(),
            GetWindowsPathExtensions());

    internal static bool IsCommandOnPath(
        string commandName,
        string? pathValue,
        bool isWindows,
        IReadOnlyList<string> windowsExtensions)
    {
        if (string.IsNullOrWhiteSpace(commandName) ||
            Path.IsPathRooted(commandName) ||
            commandName.IndexOfAny(['/', '\\']) >= 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return false;
        }

        var pathSeparator = isWindows ? ';' : ':';
        foreach (var rawDir in pathValue.Split(pathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = rawDir.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            if (isWindows)
            {
                var hasKnownExtension = windowsExtensions.Any(ext =>
                    commandName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
                if (hasKnownExtension)
                {
                    if (File.Exists(Path.Combine(dir, commandName)))
                    {
                        return true;
                    }

                    continue;
                }

                foreach (var ext in windowsExtensions)
                {
                    if (File.Exists(Path.Combine(dir, commandName + ext)))
                    {
                        return true;
                    }
                }
            }
            else
            {
                var directCandidate = Path.Combine(dir, commandName);
                if (File.Exists(directCandidate) &&
                    (OperatingSystem.IsWindows() || HasUnixExecutePermission(directCandidate)))
                {
                    return true;
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
    private static bool HasUnixExecutePermission(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var mode = File.GetUnixFileMode(filePath);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Justification: Inaccessible file during PATH probing is treated as non-executable.
            _ = ex;
            return false;
        }
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

    [SupportedOSPlatform("windows")]
    private static string? GetWindowsUserPathRaw()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InstallerConstants.WindowsEnvironmentRegistrySubKey, writable: false);
        return key?.GetValue(InstallerConstants.EnvPathWindows, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindowsUserPathPreservingKind(string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InstallerConstants.WindowsEnvironmentRegistrySubKey, writable: true)
            ?? throw new InvalidOperationException($"Could not open HKCU\\{InstallerConstants.WindowsEnvironmentRegistrySubKey}.");

        var valueKind = RegistryValueKind.String;
        try
        {
            if (key.GetValue(InstallerConstants.EnvPathWindows, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not null)
            {
                valueKind = key.GetValueKind(InstallerConstants.EnvPathWindows);
            }
        }
        catch (IOException)
        {
            // Justification: Value does not exist yet; default to String/ExpandString below.
        }

        if (value.Contains('%', StringComparison.Ordinal))
        {
            valueKind = RegistryValueKind.ExpandString;
        }
        else if (valueKind != RegistryValueKind.ExpandString)
        {
            valueKind = RegistryValueKind.String;
        }

        key.SetValue(InstallerConstants.EnvPathWindows, value, valueKind);
        TryBroadcastEnvironmentChange();
    }

    [SupportedOSPlatform("windows")]
    private static void TryBroadcastEnvironmentChange()
    {
        try
        {
            _ = SendMessageTimeoutW(
                HwndBroadcast,
                WmSettingChange,
                0,
                InstallerConstants.WindowsEnvironmentRegistrySubKey,
                SmtoAbortIfHung,
                SettingChangeTimeoutMs,
                out _);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Justification: Headless or restricted Windows subsystems may omit user32.dll broadcast APIs.
            _ = ex;
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeoutW(
        nint hWnd,
        uint msg,
        nuint wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out nuint lpdwResult);

    private static string[] GetWindowsPathExtensions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var pathExt = Environment.GetEnvironmentVariable(InstallerConstants.EnvPathExtWindows);
        if (string.IsNullOrWhiteSpace(pathExt))
        {
            return [InstallerConstants.ExeExtension, InstallerConstants.CmdExtension, InstallerConstants.BatExtension];
        }

        return pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
