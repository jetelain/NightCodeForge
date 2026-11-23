namespace NightCodeForge.Installer.Core.Services;

using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Manages OS-native integration: Windows Uninstall registry entries, Start Menu / Desktop shortcuts,
/// Linux XDG <c>.desktop</c> entries, macOS <c>.app</c> wrapper, user <c>PATH</c>, and <c>ollamarouter://</c> URI handler.
/// </summary>
public interface IOsIntegrationService
{
    WindowsUninstallRegistryPlan CreateWindowsUninstallRegistryPlan(
        string installDirectory,
        string version,
        int estimatedSizeKb,
        DateTimeOffset installDate);

    WindowsProtocolRegistrationPlan CreateWindowsOllamaRouterProtocolPlan(string appExecutablePath);

    LinuxProtocolRegistrationPlan CreateLinuxOllamaRouterProtocolPlan(
        string appExecutablePath,
        string homeDirectory,
        bool xdgMimeAvailable);

    LinuxDesktopEntryPlan CreateLinuxDesktopEntryPlans(string installDirectory, string homeDirectory);

    MacOsAppWrapperPlan CreateMacOsAppWrapperPlan(string installDirectory, string homeDirectory);

    Task RegisterNightCodeForgeInstallationAsync(
        string installDirectory,
        string version,
        bool createDesktopShortcut,
        CancellationToken cancellationToken = default);

    Task ApplyShortcutPreferencesAsync(
        string installDirectory,
        bool createStartMenuShortcut,
        bool createDesktopShortcut,
        CancellationToken cancellationToken = default);

    Task UnregisterNightCodeForgeInstallationAsync(
        string installDirectory,
        CancellationToken cancellationToken = default);

    Task RegisterOllamaRouterProtocolAsync(
        string appExecutablePath,
        CancellationToken cancellationToken = default);

    Task UnregisterOllamaRouterProtocolAsync(CancellationToken cancellationToken = default);

    void AddDirectoryToUserPath(string directoryPath, string profileBlockLabel);

    void RemoveDirectoryFromUserPath(string directoryPath, string profileBlockLabel);
}
