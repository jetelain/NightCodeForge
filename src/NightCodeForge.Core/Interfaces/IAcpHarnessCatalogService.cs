using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Discovers, probes, and caches status and available models for external ACP harness providers (such as Antigravity CLI).
/// </summary>
public interface IAcpHarnessCatalogService
{
    Task<IReadOnlyList<AcpCatalogProviderStatus>> GetCatalogStatusAsync(
        CancellationToken cancellationToken = default);

    Task<AcpCatalogProviderStatus> ProbeProviderAsync(
        string catalogId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}
