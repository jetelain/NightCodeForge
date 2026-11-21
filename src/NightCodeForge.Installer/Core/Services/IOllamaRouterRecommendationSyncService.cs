namespace NightCodeForge.Installer.Core.Services;

using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Serialization;

/// <summary>
/// Queries OllamaRouter's <c>GET /api/router/recommendations</c> endpoint and atomically updates
/// NightCodeForge's <c>config/config.json</c> user overlay without referencing
/// <c>NightCodeForge.Infrastructure</c>, preserving Native AOT compatibility.
/// </summary>
public interface IOllamaRouterRecommendationSyncService
{
    Task<RouterRecommendationsDto?> FetchRecommendationsAsync(
        string? routerBaseUrl = null,
        CancellationToken cancellationToken = default);

    Task<RouterRoleRecommendationsSummary> SyncRecommendationsToConfigAsync(
        string? installDirectory = null,
        string? routerBaseUrl = null,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default);
}
