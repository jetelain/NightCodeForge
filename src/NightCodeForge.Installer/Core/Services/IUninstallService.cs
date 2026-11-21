namespace NightCodeForge.Installer.Core.Services;

using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Orchestrates complete or data-preserving uninstallation of NightCodeForge, optional companion NuGet tools,
/// optional OllamaRouter, OS registrations, user <c>PATH</c> entries, and deferred Windows self-cleanup.
/// </summary>
public interface IUninstallService
{
    Task<UninstallResult> UninstallAsync(
        UninstallOptions options,
        string? targetInstallDirectory = null,
        string? wslDistroName = null,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default);
}
