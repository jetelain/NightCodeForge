using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Web.Services;

public record LocalRepositoryOption(
    string Name,
    string FullPath,
    string DisplayPath,
    string? CurrentBranch,
    DateTimeOffset LastModified,
    bool HasRemoteOrigin,
    string? RemoteOriginUrl);

public record RemoteRepositoryItem(
    VcsHostingProviderType ProviderType,
    VcsRepositorySummary Repository,
    LocalRepositoryOption? LocalMatch);

public record RemoteForgeState(
    bool HasAvailableProvider,
    bool IsAuthenticated,
    VcsHostingProviderType? ActiveProviderType,
    string? ProviderDisplayName,
    string? AuthenticatedUsername,
    IReadOnlyList<RemoteRepositoryItem> Repositories,
    string? ErrorMessage);

public record TargetPathValidationResult(
    bool IsValid,
    string? ErrorMessage,
    bool DirectoryExists,
    bool IsNonEmpty);

public record RemoteCloneResult(
    bool Success,
    string TargetPath,
    string SuggestedWorkspaceName,
    string DefaultBaseBranch,
    GitHubIntegrationSettings? GitHubSettings,
    string? ErrorMessage);

/// <summary>
/// Encapsulates repository discovery, remote forge browsing, canonical URL matching,
/// clone target validation, and ephemeral-auth cloning for Step 1 of the workspace creation wizard.
/// </summary>
public interface IWorkspaceIntakeUiService
{
    Task<IReadOnlyList<LocalRepositoryOption>> LoadLocalRepositoriesAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    IReadOnlyList<LocalRepositoryOption> FilterLocalRepositories(
        IReadOnlyList<LocalRepositoryOption> repositories,
        string? searchQuery);

    string FormatLocalRepositoryLabel(LocalRepositoryOption repository);

    Task<RemoteForgeState> LoadRemoteForgeStateAsync(
        IReadOnlyList<LocalRepositoryOption> localRepositories,
        CancellationToken cancellationToken = default);

    IReadOnlyList<RemoteRepositoryItem> FilterRemoteRepositories(
        IReadOnlyList<RemoteRepositoryItem> repositories,
        string? searchQuery);

    string ResolveDefaultClonePath(string repositoryName);

    TargetPathValidationResult ValidateCloneTargetPath(string? targetPath);

    Task<RemoteCloneResult> CloneRemoteRepositoryAsync(
        VcsHostingProviderType providerType,
        VcsRepositorySummary repository,
        string targetPath,
        CancellationToken cancellationToken = default);

    GitHubIntegrationSettings? BuildGitHubSettingsIfApplicable(
        VcsHostingProviderType? providerType,
        VcsRepositorySummary repository);

    string DeriveWorkspaceNameFromPath(string? repositoryPath);
}
