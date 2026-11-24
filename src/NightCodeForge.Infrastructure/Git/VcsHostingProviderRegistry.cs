using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Git;

/// <summary>
/// Resolves registered <see cref="IVcsHostingProvider"/> implementations and determines the primary authenticated provider.
/// </summary>
public class VcsHostingProviderRegistry : IVcsHostingProviderRegistry
{
    private readonly IReadOnlyList<IVcsHostingProvider> _providers;
    private readonly ILogger<VcsHostingProviderRegistry> _logger;

    public VcsHostingProviderRegistry(
        IEnumerable<IVcsHostingProvider> providers,
        ILogger<VcsHostingProviderRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _providers = providers.ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<IVcsHostingProvider> GetAvailableProviders() => _providers;

    /// <inheritdoc />
    public IVcsHostingProvider? GetProvider(VcsHostingProviderType type) =>
        _providers.FirstOrDefault(p => p.ProviderType == type);

    /// <inheritdoc />
    public async Task<IVcsHostingProvider?> GetPrimaryAuthenticatedProviderAsync(CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await provider.IsAuthenticatedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return provider;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query authentication status for VCS provider {ProviderType}.", provider.ProviderType);
            }
        }

        return null;
    }
}
