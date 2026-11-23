namespace NightCodeForge.Installer.Core.Services;

using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Discovers existing NightCodeForge (including legacy <c>publish.ps1</c> deployments),
/// OllamaRouter installations, and adjacent/explicit air-gapped bundle directories.
/// </summary>
public interface IInstallationDetector
{
    string GetDefaultNightCodeForgeInstallDirectory();
    string GetDefaultOllamaRouterInstallDirectory();
    InstallationDiscoveryResult Detect(string? explicitBundleDirectory = null, string? explicitInstallDirectory = null);
}
