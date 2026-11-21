namespace NightCodeForge.Installer.Core.Services;

using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Checks release/bundle availability and orchestrates 1-click atomic in-place upgrades of
/// NightCodeForge Web, Native AOT <c>ncf-web</c>, OllamaRouter, and all companion NuGet global tools.
/// </summary>
public interface IAllComponentUpgradeService
{
    Task<UpgradeAvailabilityResult> CheckAvailableUpdatesAsync(
        InstallSourceOptions source,
        string? explicitInstallDirectory,
        CancellationToken cancellationToken = default);

    Task<AllComponentUpgradeResult> UpgradeAllAsync(
        InstallSourceOptions source,
        string? targetInstallDirectory,
        string? wslDistroName,
        bool createDesktopShortcut,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default);
}
