namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Supported remote Git hosting forge types.
/// </summary>
public enum VcsHostingProviderType
{
    GitHub = 1,
    GitLab = 2,
    Gitea = 3,
    Bitbucket = 4,
    Custom = 99
}

/// <summary>
/// Provider-agnostic summary of a remote repository hosted on a VCS forge.
/// </summary>
public record VcsRepositorySummary(
    string Id,
    string Name,
    string FullName,
    string Owner,
    string Description,
    bool IsPrivate,
    string DefaultBranch,
    string HttpsCloneUrl,
    string SshCloneUrl,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// Provider-agnostic abstraction over a remote Git hosting forge (GitHub, GitLab, Gitea, etc.).
/// </summary>
public interface IVcsHostingProvider
{
    VcsHostingProviderType ProviderType { get; }
    string DisplayName { get; }
    Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken = default);
    Task<string?> GetAuthenticatedUsernameAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VcsRepositorySummary>> ListRepositoriesAsync(
        CancellationToken cancellationToken = default);

    Task<VcsRepositorySummary> CreateRepositoryAsync(
        string name,
        string? description,
        bool isPrivate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a short-lived HTTP Authorization header value (e.g. "Authorization: Basic &lt;base64&gt;")
    /// for ephemeral <c>git -c http.extraHeader=...</c> clone and push operations without persisting
    /// credentials into <c>.git/config</c>, or null if unauthenticated.
    /// </summary>
    Task<string?> GetGitHttpAuthHeaderAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Registry resolving configured remote VCS hosting providers and active primary authentication state.
/// </summary>
public interface IVcsHostingProviderRegistry
{
    IReadOnlyList<IVcsHostingProvider> GetAvailableProviders();
    IVcsHostingProvider? GetProvider(VcsHostingProviderType type);
    Task<IVcsHostingProvider?> GetPrimaryAuthenticatedProviderAsync(
        CancellationToken cancellationToken = default);
}
