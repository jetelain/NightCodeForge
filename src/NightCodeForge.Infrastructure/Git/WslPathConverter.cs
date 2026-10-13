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

    [GeneratedRegex(@"^[\\/]{2}wsl(\.localhost|\$)?([\\/].*)$", RegexOptions.IgnoreCase)]
    private static partial Regex WslUncPrefixRegex();

    /// <summary>
    /// Checks if the path represents a Windows UNC path to a WSL distribution (e.g. \\wsl.localhost\Ubuntu\... or \\wsl$\Ubuntu\...).
    /// </summary>
    public static bool IsWslUncPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return WslUncPrefixRegex().IsMatch(path);
    }

    /// <summary>
    /// Extracts the WSL distribution name and the inner Linux path from a WSL UNC path.
    /// </summary>
    public static bool TryExtractWslUnc(string path, out string distro, out string linuxPath)
    {
        distro = string.Empty;
        linuxPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path)) return false;

        var normalized = path.Replace('/', '\\');
        string remainder;

        if (normalized.StartsWith(@"\\wsl.localhost\", StringComparison.OrdinalIgnoreCase))
        {
            remainder = normalized.Substring(@"\\wsl.localhost\".Length);
        }
        else if (normalized.StartsWith(@"\\wsl$\", StringComparison.OrdinalIgnoreCase))
        {
            remainder = normalized.Substring(@"\\wsl$\".Length);
        }
        else
        {
            return false;
        }

        var slashIndex = remainder.IndexOf('\\');
        if (slashIndex < 0)
        {
            distro = remainder;
            linuxPath = "/";
            return true;
        }

        distro = remainder.Substring(0, slashIndex);
        var rest = remainder.Substring(slashIndex).Replace('\\', '/');
        linuxPath = string.IsNullOrWhiteSpace(rest) ? "/" : rest;
        return true;
    }

    /// <summary>
    /// Converts a WSL Linux path (/home/user/...) to a Windows UNC path (\\wsl.localhost\Ubuntu\home\user\...).
    /// </summary>
    public static string ToWslUncPath(string linuxPath, string distro = "Ubuntu")
    {
        if (string.IsNullOrWhiteSpace(linuxPath)) return linuxPath;
        var trimmed = linuxPath.TrimStart('/', '\\');
        return $@"\\wsl.localhost\{distro}\{trimmed.Replace('/', '\\')}";
    }
}
