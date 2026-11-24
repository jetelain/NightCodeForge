using System.Security;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Presentation helper service for Step 1 of the workspace onboarding wizard:
/// coordinates local discovery, remote forge repository matching, path validation, and ephemeral-auth git cloning.
/// </summary>
public class WorkspaceIntakeUiService : IWorkspaceIntakeUiService
{
    private const string DefaultBranchFallback = "main";
    private const string GitTerminalPromptEnvVar = "GIT_TERMINAL_PROMPT";
    private const string GitTerminalPromptDisabledValue = "0";

    private readonly ILocalRepositoryDiscoveryService _localDiscovery;
    private readonly IVcsHostingProviderRegistry _providerRegistry;
    private readonly IGitCliRunner _gitCliRunner;
    private readonly ILogger<WorkspaceIntakeUiService> _logger;

    public WorkspaceIntakeUiService(
        ILocalRepositoryDiscoveryService localDiscovery,
        IVcsHostingProviderRegistry providerRegistry,
        IGitCliRunner gitCliRunner,
        ILogger<WorkspaceIntakeUiService> logger)
    {
        _localDiscovery = localDiscovery ?? throw new ArgumentNullException(nameof(localDiscovery));
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LocalRepositoryOption>> LoadLocalRepositoriesAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var discovered = await _localDiscovery.GetDiscoveredRepositoriesAsync(forceRefresh, cancellationToken)
            .ConfigureAwait(false);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return discovered
            .Select(repo => new LocalRepositoryOption(
                Name: repo.Name,
                FullPath: repo.FullPath,
                DisplayPath: FormatDisplayPath(repo.FullPath, userProfile),
                CurrentBranch: repo.CurrentBranch,
                LastModified: repo.LastModified,
                HasRemoteOrigin: repo.HasRemoteOrigin,
                RemoteOriginUrl: repo.RemoteOriginUrl))
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<LocalRepositoryOption> FilterLocalRepositories(
        IReadOnlyList<LocalRepositoryOption> repositories,
        string? searchQuery)
    {
        ArgumentNullException.ThrowIfNull(repositories);

        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return repositories;
        }

        var query = searchQuery.Trim();
        return repositories
            .Where(repo =>
                repo.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                repo.DisplayPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                repo.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(repo.CurrentBranch) &&
                 repo.CurrentBranch.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <inheritdoc />
    public string FormatLocalRepositoryLabel(LocalRepositoryOption repository)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var branchText = string.IsNullOrWhiteSpace(repository.CurrentBranch)
            ? "detached"
            : repository.CurrentBranch;

        return $"{repository.Name} (branch: {branchText}) — {repository.DisplayPath}";
    }

    /// <inheritdoc />
    public async Task<RemoteForgeState> LoadRemoteForgeStateAsync(
        IReadOnlyList<LocalRepositoryOption> localRepositories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localRepositories);

        var providers = _providerRegistry.GetAvailableProviders();
        var primaryProvider = await _providerRegistry.GetPrimaryAuthenticatedProviderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (primaryProvider is null)
        {
            return new RemoteForgeState(
                HasAvailableProvider: providers.Count > 0,
                IsAuthenticated: false,
                ActiveProviderType: null,
                ProviderDisplayName: providers.FirstOrDefault()?.DisplayName,
                AuthenticatedUsername: null,
                Repositories: Array.Empty<RemoteRepositoryItem>(),
                ErrorMessage: null);
        }

        try
        {
            var username = await primaryProvider.GetAuthenticatedUsernameAsync(cancellationToken).ConfigureAwait(false);
            var remoteRepositories = await primaryProvider.ListRepositoriesAsync(cancellationToken).ConfigureAwait(false);

            var items = remoteRepositories
                .Select(remote => new RemoteRepositoryItem(
                    ProviderType: primaryProvider.ProviderType,
                    Repository: remote,
                    LocalMatch: FindMatchingLocalRepository(remote, localRepositories)))
                .ToList();

            return new RemoteForgeState(
                HasAvailableProvider: true,
                IsAuthenticated: true,
                ActiveProviderType: primaryProvider.ProviderType,
                ProviderDisplayName: primaryProvider.DisplayName,
                AuthenticatedUsername: username,
                Repositories: items,
                ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load remote repositories from VCS provider {ProviderType}.", primaryProvider.ProviderType);
            return new RemoteForgeState(
                HasAvailableProvider: true,
                IsAuthenticated: true,
                ActiveProviderType: primaryProvider.ProviderType,
                ProviderDisplayName: primaryProvider.DisplayName,
                AuthenticatedUsername: null,
                Repositories: Array.Empty<RemoteRepositoryItem>(),
                ErrorMessage: ex.Message);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RemoteRepositoryItem> FilterRemoteRepositories(
        IReadOnlyList<RemoteRepositoryItem> repositories,
        string? searchQuery)
    {
        ArgumentNullException.ThrowIfNull(repositories);

        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return repositories;
        }

        var query = searchQuery.Trim();
        return repositories
            .Where(item =>
                item.Repository.FullName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Repository.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Repository.Owner.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(item.Repository.Description) &&
                 item.Repository.Description.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <inheritdoc />
    public string ResolveDefaultClonePath(string repositoryName)
    {
        return _localDiscovery.ResolveDefaultNewRepositoryPath(repositoryName);
    }

    /// <inheritdoc />
    public TargetPathValidationResult ValidateCloneTargetPath(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return new TargetPathValidationResult(
                IsValid: false,
                ErrorMessage: "Target clone directory path is required.",
                DirectoryExists: false,
                IsNonEmpty: false);
        }

        var trimmed = targetPath.Trim();
        if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return new TargetPathValidationResult(
                IsValid: false,
                ErrorMessage: "Target clone path contains invalid characters.",
                DirectoryExists: false,
                IsNonEmpty: false);
        }

        if (!Path.IsPathFullyQualified(trimmed))
        {
            return new TargetPathValidationResult(
                IsValid: false,
                ErrorMessage: "Target clone path must be an absolute directory path.",
                DirectoryExists: false,
                IsNonEmpty: false);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return new TargetPathValidationResult(
                IsValid: false,
                ErrorMessage: $"Invalid target directory path: {ex.Message}",
                DirectoryExists: false,
                IsNonEmpty: false);
        }

        if (File.Exists(fullPath))
        {
            return new TargetPathValidationResult(
                IsValid: false,
                ErrorMessage: $"A file already exists at '{fullPath}'. Choose a new directory path.",
                DirectoryExists: false,
                IsNonEmpty: true);
        }

        if (Directory.Exists(fullPath))
        {
            try
            {
                var isNonEmpty = Directory.EnumerateFileSystemEntries(fullPath).Any();
                if (isNonEmpty)
                {
                    return new TargetPathValidationResult(
                        IsValid: false,
                        ErrorMessage: $"Target directory '{fullPath}' already exists and is not empty. Choose an empty or new directory.",
                        DirectoryExists: true,
                        IsNonEmpty: true);
                }

                return new TargetPathValidationResult(
                    IsValid: true,
                    ErrorMessage: null,
                    DirectoryExists: true,
                    IsNonEmpty: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return new TargetPathValidationResult(
                    IsValid: false,
                    ErrorMessage: $"Cannot inspect target directory '{fullPath}': {ex.Message}",
                    DirectoryExists: true,
                    IsNonEmpty: true);
            }
        }

        return new TargetPathValidationResult(
            IsValid: true,
            ErrorMessage: null,
            DirectoryExists: false,
            IsNonEmpty: false);
    }

    /// <inheritdoc />
    public async Task<RemoteCloneResult> CloneRemoteRepositoryAsync(
        VcsHostingProviderType providerType,
        VcsRepositorySummary repository,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var defaultBranch = string.IsNullOrWhiteSpace(repository.DefaultBranch)
            ? DefaultBranchFallback
            : repository.DefaultBranch.Trim();

        var validation = ValidateCloneTargetPath(targetPath);
        if (!validation.IsValid)
        {
            return new RemoteCloneResult(
                Success: false,
                TargetPath: targetPath ?? string.Empty,
                SuggestedWorkspaceName: repository.Name,
                DefaultBaseBranch: defaultBranch,
                GitHubSettings: null,
                ErrorMessage: validation.ErrorMessage);
        }

        var fullTargetPath = Path.GetFullPath(targetPath.Trim());
        var provider = _providerRegistry.GetProvider(providerType);
        if (provider is null)
        {
            return new RemoteCloneResult(
                Success: false,
                TargetPath: fullTargetPath,
                SuggestedWorkspaceName: repository.Name,
                DefaultBaseBranch: defaultBranch,
                GitHubSettings: null,
                ErrorMessage: $"Remote VCS provider '{providerType}' is not available.");
        }

        if (string.IsNullOrWhiteSpace(repository.HttpsCloneUrl) ||
            !Uri.TryCreate(repository.HttpsCloneUrl.Trim(), UriKind.Absolute, out var cloneUri) ||
            (cloneUri.Scheme != Uri.UriSchemeHttps && cloneUri.Scheme != Uri.UriSchemeHttp))
        {
            return new RemoteCloneResult(
                Success: false,
                TargetPath: fullTargetPath,
                SuggestedWorkspaceName: repository.Name,
                DefaultBaseBranch: defaultBranch,
                GitHubSettings: null,
                ErrorMessage: $"Repository '{repository.FullName}' does not expose a valid HTTP/HTTPS clone URL.");
        }

        var parentDir = Path.GetDirectoryName(fullTargetPath);
        if (string.IsNullOrWhiteSpace(parentDir))
        {
            return new RemoteCloneResult(
                Success: false,
                TargetPath: fullTargetPath,
                SuggestedWorkspaceName: repository.Name,
                DefaultBaseBranch: defaultBranch,
                GitHubSettings: null,
                ErrorMessage: $"Target path '{fullTargetPath}' must reside inside a parent directory.");
        }

        try
        {
            if (!Directory.Exists(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            var authHeader = await provider.GetGitHttpAuthHeaderAsync(cancellationToken).ConfigureAwait(false);
            var gitArgs = new List<string>();
            if (!string.IsNullOrWhiteSpace(authHeader))
            {
                gitArgs.Add("-c");
                gitArgs.Add($"http.extraHeader={authHeader}");
            }

            gitArgs.Add("clone");
            gitArgs.Add("--");
            gitArgs.Add(repository.HttpsCloneUrl.Trim());
            gitArgs.Add(fullTargetPath);

            var environmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GitTerminalPromptEnvVar] = GitTerminalPromptDisabledValue
            };

            await _gitCliRunner.ExecuteAsync(
                parentDir,
                gitArgs,
                timeoutSeconds: null,
                environmentVariables: environmentVariables,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            try
            {
                await _localDiscovery.GetDiscoveredRepositoriesAsync(forceRefresh: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Local repository discovery refresh failed after cloning '{RepositoryFullName}'.", repository.FullName);
            }

            var gitHubSettings = BuildGitHubSettingsIfApplicable(providerType, repository);

            return new RemoteCloneResult(
                Success: true,
                TargetPath: fullTargetPath,
                SuggestedWorkspaceName: repository.Name,
                DefaultBaseBranch: defaultBranch,
                GitHubSettings: gitHubSettings,
                ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var redactedMessage = GitCredentialRedactor.Redact(ex.Message);
            _logger.LogError(
                "Failed to clone remote repository '{RepositoryFullName}' into '{TargetPath}': {ErrorMessage}",
                repository.FullName,
                fullTargetPath,
                redactedMessage);
            return new RemoteCloneResult(
                Success: false,
                TargetPath: fullTargetPath,
                SuggestedWorkspaceName: repository.Name,
                DefaultBaseBranch: defaultBranch,
                GitHubSettings: null,
                ErrorMessage: redactedMessage);
        }
    }

    /// <inheritdoc />
    public GitHubIntegrationSettings? BuildGitHubSettingsIfApplicable(
        VcsHostingProviderType? providerType,
        VcsRepositorySummary repository)
    {
        ArgumentNullException.ThrowIfNull(repository);

        if (providerType != VcsHostingProviderType.GitHub)
        {
            return null;
        }

        var owner = repository.Owner;
        var name = repository.Name;

        if ((string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name)) &&
            repository.FullName.Contains('/', StringComparison.Ordinal))
        {
            var parts = repository.FullName.Split('/', 2, StringSplitOptions.TrimEntries);
            if (string.IsNullOrWhiteSpace(owner))
            {
                owner = parts[0];
            }

            if (string.IsNullOrWhiteSpace(name) && parts.Length > 1)
            {
                name = parts[1];
            }
        }

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new GitHubIntegrationSettings
        {
            Enabled = true,
            RepositoryOwner = owner.Trim(),
            RepositoryName = name.Trim()
        };
    }

    /// <inheritdoc />
    public string DeriveWorkspaceNameFromPath(string? repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            return string.Empty;
        }

        try
        {
            var trimmed = repositoryPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFileName(trimmed) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static LocalRepositoryOption? FindMatchingLocalRepository(
        VcsRepositorySummary remote,
        IReadOnlyList<LocalRepositoryOption> localRepositories)
    {
        foreach (var local in localRepositories)
        {
            if (!local.HasRemoteOrigin || string.IsNullOrWhiteSpace(local.RemoteOriginUrl))
            {
                continue;
            }

            if (GitRemoteUrlNormalizer.AreSameRepository(local.RemoteOriginUrl, remote.HttpsCloneUrl) ||
                GitRemoteUrlNormalizer.AreSameRepository(local.RemoteOriginUrl, remote.SshCloneUrl))
            {
                return local;
            }
        }

        return null;
    }

    private static string FormatDisplayPath(string fullPath, string? userProfile)
    {
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return fullPath;
        }

        try
        {
            var normalizedHome = Path.GetFullPath(userProfile)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            var homePrefixWithSep = normalizedHome + Path.DirectorySeparatorChar;
            var homePrefixWithAlt = normalizedHome + Path.AltDirectorySeparatorChar;

            if (fullPath.StartsWith(homePrefixWithSep, comparison) ||
                fullPath.StartsWith(homePrefixWithAlt, comparison))
            {
                return Path.GetRelativePath(normalizedHome, fullPath).Replace('\\', '/');
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return fullPath;
        }

        return fullPath;
    }
}
