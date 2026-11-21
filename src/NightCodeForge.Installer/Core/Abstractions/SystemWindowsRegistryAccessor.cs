namespace NightCodeForge.Installer.Core.Abstractions;

using System.Runtime.Versioning;
using Microsoft.Win32;

/// <summary>
/// Real <c>HKEY_CURRENT_USER</c> registry accessor guarded cleanly for cross-platform builds.
/// </summary>
public sealed class SystemWindowsRegistryAccessor : IWindowsRegistryAccessor
{
    public string? ReadCurrentUserStringValue(string subKeyPath, string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subKeyPath);
        ArgumentNullException.ThrowIfNull(valueName);

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return ReadCurrentUserStringValueWindows(subKeyPath, valueName);
    }

    public void WriteCurrentUserValues(string subKeyPath, IReadOnlyDictionary<string, object> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subKeyPath);
        ArgumentNullException.ThrowIfNull(values);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        WriteCurrentUserValuesWindows(subKeyPath, values);
    }

    public void DeleteCurrentUserSubKeyTree(string subKeyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subKeyPath);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DeleteCurrentUserSubKeyTreeWindows(subKeyPath);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadCurrentUserStringValueWindows(string subKeyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    [SupportedOSPlatform("windows")]
    private static void WriteCurrentUserValuesWindows(string subKeyPath, IReadOnlyDictionary<string, object> values)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Could not open or create HKCU\\{subKeyPath}.");

        foreach (var (name, value) in values)
        {
            if (value is int intValue)
            {
                key.SetValue(name, intValue, RegistryValueKind.DWord);
            }
            else
            {
                key.SetValue(name, value?.ToString() ?? string.Empty, RegistryValueKind.String);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteCurrentUserSubKeyTreeWindows(string subKeyPath)
    {
        Registry.CurrentUser.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
    }
}
