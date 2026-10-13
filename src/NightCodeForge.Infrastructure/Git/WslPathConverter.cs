using System.Text.RegularExpressions;

namespace NightCodeForge.Infrastructure.Git;

/// <summary>
/// Utility for bidirectional path conversions between Windows NTFS drive paths and WSL2 / Linux ext4 mount paths.
/// </summary>
public static partial class WslPathConverter
{
    [GeneratedRegex(@"^([a-zA-Z]):[\\/](.*)$")]
    private static partial Regex WindowsDriveRegex();

    [GeneratedRegex(@"^/mnt/([a-zA-Z])/(.*)$")]
    private static partial Regex WslMountRegex();

    /// <summary>
    /// Converts a Windows NTFS path (e.g. C:\repo\app) to a WSL2 mount path (/mnt/c/repo/app).
    /// If the path is already a Linux/WSL path, returns normalized Linux slashes.
    /// </summary>
    public static string ToWslPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var match = WindowsDriveRegex().Match(path);
        if (match.Success)
        {
            var driveLetter = match.Groups[1].Value.ToLowerInvariant();
            var relativePath = match.Groups[2].Value.Replace('\\', '/');
            return $"/mnt/{driveLetter}/{relativePath}";
        }

        return path.Replace('\\', '/');
    }

    /// <summary>
    /// Converts a WSL2 mount path (/mnt/c/repo/app) to a Windows drive path (C:\repo\app).
    /// If the path is a native Linux path (e.g. /var/ncf/workspaces), returns the path unchanged.
    /// </summary>
    public static string ToWindowsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var normalized = path.Replace('\\', '/');
        var match = WslMountRegex().Match(normalized);
        if (match.Success)
        {
            var driveLetter = char.ToUpperInvariant(match.Groups[1].Value[0]);
            var relativePath = match.Groups[2].Value.Replace('/', Path.DirectorySeparatorChar);
            return $"{driveLetter}:{Path.DirectorySeparatorChar}{relativePath}";
        }

        return path;
    }

    /// <summary>
    /// Checks if the path represents a Windows NTFS path with drive letter.
    /// </summary>
    public static bool IsWindowsDrivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return WindowsDriveRegex().IsMatch(path);
    }

    /// <summary>
    /// Checks if the path represents a WSL mount path (/mnt/x/...).
    /// </summary>
    public static bool IsWslMountPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return WslMountRegex().IsMatch(path.Replace('\\', '/'));
    }
}
