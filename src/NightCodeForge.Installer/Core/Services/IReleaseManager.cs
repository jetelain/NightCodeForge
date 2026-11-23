namespace NightCodeForge.Installer.Core.Services;

using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Resolves, verifies, extracts, atomically swaps, and post-configures NightCodeForge and OllamaRouter releases
/// from either GitHub Releases (<see cref="InstallSourceMode.Online"/>) or an air-gapped local bundle.
/// </summary>
public interface IReleaseManager
{
    Task<ComponentReleaseAsset> ResolveLatestNightCodeForgeReleaseAsync(
        InstallSourceOptions source,
        CancellationToken cancellationToken = default);

    Task<ComponentReleaseAsset> ResolveLatestOllamaRouterReleaseAsync(
        InstallSourceOptions source,
        CancellationToken cancellationToken = default);

    Task VerifyChecksumAsync(
        string filePath,
        string expectedSha256,
        CancellationToken cancellationToken = default);

    Task<ReleaseDeploymentResult> DeployNightCodeForgeAsync(
        ComponentReleaseAsset asset,
        string? targetInstallDirectory,
        bool createDesktopShortcut,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ReleaseDeploymentResult> DeployOllamaRouterAsync(
        ComponentReleaseAsset asset,
        string? targetInstallDirectory,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default);
}
