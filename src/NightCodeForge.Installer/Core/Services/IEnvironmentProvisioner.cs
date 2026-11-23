namespace NightCodeForge.Installer.Core.Services;

using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Probes and provisions host and WSL2 subsystem prerequisites (.NET 10 SDK, WSL2, Ubuntu, Docker, ripgrep, git, ICU/locales).
/// </summary>
public interface IEnvironmentProvisioner
{
    Task<EnvironmentProbeResult> ProbeAsync(
        string wslDistroName,
        CancellationToken cancellationToken = default);

    Task<ProvisioningReport> ProvisionAsync(
        ProvisioningOptions options,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default);
}
