namespace NightCodeForge.Installer.Core.Services;

using NightCodeForge.Installer.Core.Models;

public sealed record OllamaRouterWizardHandoffResult(
    bool Succeeded,
    int ExitCode,
    string? ErrorMessage,
    RouterRoleRecommendationsSummary? SyncedRecommendations);

/// <summary>
/// Launches OllamaRouter's standalone Setup Wizard (<c>ollamarouter setup --wizard --wait</c>)
/// and synchronizes the generated role recommendations into NightCodeForge's <c>config/config.json</c>.
/// </summary>
public interface IOllamaRouterSetupHandoffService
{
    bool IsOllamaRouterInstalled();

    string? ResolveOllamaRouterCliPath();

    Task<OllamaRouterWizardHandoffResult> RunSetupWizardAndSyncAsync(
        CancellationToken cancellationToken = default);

    Task<OllamaRouterWizardHandoffResult> RunSetupWizardAndSyncAsync(
        string? installDirectory,
        string? routerBaseUrl = null,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default);
}
