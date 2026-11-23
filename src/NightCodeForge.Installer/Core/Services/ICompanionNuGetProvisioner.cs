namespace NightCodeForge.Installer.Core.Services;

using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Probes, pre-installs, updates, and removes the 5 companion NuGet global tools
/// (<c>ncf</c>, <c>nightcodeassay</c>, <c>csnav</c>, <c>mdfetch</c>, <c>codecondenser</c>)
/// across the host OS and WSL2 (supporting both Online NuGet.org and Air-Gapped Bundle sources).
/// </summary>
public interface ICompanionNuGetProvisioner
{
    IReadOnlyList<CompanionNuGetToolSpec> Catalog { get; }

    Task<IReadOnlyList<CompanionToolStatus>> ProbeToolsAsync(
        string? wslDistroName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CompanionToolInstallOutcome>> ProvisionAllAsync(
        InstallSourceOptions source,
        string? wslDistroName,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CompanionToolInstallOutcome>> UninstallAllAsync(
        string? wslDistroName,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default);
}
