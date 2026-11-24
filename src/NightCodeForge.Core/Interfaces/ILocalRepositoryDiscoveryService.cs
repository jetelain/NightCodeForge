namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Represents a Git repository discovered on the local filesystem during workspace scanning.
/// </summary>
public record DiscoveredRepository(
    string Name,
    string FullPath,
    string? CurrentBranch,
    DateTimeOffset LastModified,
    bool HasRemoteOrigin,
    string? RemoteOriginUrl);

/// <summary>
/// Discovers local Git repositories across standard developer workspace roots and resolves default clone locations.
/// </summary>
public interface ILocalRepositoryDiscoveryService
{
    /// <summary>
    /// Scans standard and configured repository directories up to the configured depth.
    /// Results are cached in memory unless <paramref name="forceRefresh"/> is true.
    /// </summary>
    Task<IReadOnlyList<DiscoveredRepository>> GetDiscoveredRepositoriesAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the existing standard root directories configured or detected for the current host OS.
    /// </summary>
    Task<IReadOnlyList<string>> GetStandardRootsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the default directory path for cloning or initializing a repository with the given name.
    /// </summary>
    string ResolveDefaultNewRepositoryPath(string repositoryName);
}
