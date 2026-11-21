namespace NightCodeForge.Installer.Core.Abstractions;

using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Abstraction over host platform identity, special folders, environment variables, PATH probing,
/// and POSIX file permission manipulation.
/// </summary>
public interface IPlatformEnvironment
{
    HostPlatformType Platform { get; }
    string CurrentRuntimeIdentifier { get; }
    string UserHomeDirectory { get; }
    string LocalApplicationDataDirectory { get; }
    string ApplicationDataDirectory { get; }
    string DesktopDirectory { get; }
    string InstallerBaseDirectory { get; }
    string? GetEnvironmentVariable(string name);
    string? GetUserPathVariable();
    void SetUserPathVariable(string value);
    bool IsCommandOnPath(string commandName);
    void SetUnixExecutablePermissions(string filePath);
}
