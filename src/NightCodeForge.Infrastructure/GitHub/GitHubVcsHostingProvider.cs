using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Security;

namespace NightCodeForge.Infrastructure.GitHub;

/// <summary>
/// Provider-agnostic <see cref="IVcsHostingProvider"/> adapter backed by GitHub REST client and Device Flow auth.
/// </summary>
public class GitHubVcsHostingProvider : IVcsHostingProvider
{
    private const string ProviderDisplayName = "GitHub";
    private const string GitBasicUsername = "x-access-token";

    private readonly IGitHubClientService _gitHubClient;
    private readonly IGitHubDeviceAuthService _authService;
    private readonly IOptions<SecurityOptions> _securityOptions;
    private readonly ILogger<GitHubVcsHostingProvider> _logger;

    public GitHubVcsHostingProvider(
        IGitHubClientService gitHubClient,
        IGitHubDeviceAuthService authService,
        IOptions<SecurityOptions> securityOptions,
        ILogger<GitHubVcsHostingProvider> logger)
    {
        _gitHubClient = gitHubClient ?? throw new ArgumentNullException(nameof(gitHubClient));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _securityOptions = securityOptions ?? throw new ArgumentNullException(nameof(securityOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public VcsHostingProviderType ProviderType => VcsHostingProviderType.GitHub;

    /// <inheritdoc />
    public string DisplayName => ProviderDisplayName;

    /// <inheritdoc />
    public async Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken = default)
    {
        if (_securityOptions.Value.AirGappedMode)
        {
            return false;
        }

        return await _authService.IsConnectedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> GetAuthenticatedUsernameAsync(CancellationToken cancellationToken = default)
    {
        if (_securityOptions.Value.AirGappedMode)
        {
            return null;
        }

        var user = await _authService.GetAuthenticatedUserAsync(cancellationToken).ConfigureAwait(false);
        return user?.Login;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VcsRepositorySummary>> ListRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        EnsureNetworkAllowed();

        try
        {
            var repositories = await _gitHubClient.ListAccessibleRepositoriesAsync(cancellationToken).ConfigureAwait(false);
            return repositories.Select(MapToSummary).ToList();
        }
        catch (GitHubAuthenticationException ex)
        {
            throw new VcsHostingProviderException(ProviderType, ex.Message, HttpStatusCode.Unauthorized, ex);
        }
        catch (GitHubApiException ex)
        {
            throw new VcsHostingProviderException(ProviderType, ex.Message, ex.StatusCode, ex);
        }
    }

    /// <inheritdoc />
    public async Task<VcsRepositorySummary> CreateRepositoryAsync(
        string name,
        string? description,
        bool isPrivate,
        CancellationToken cancellationToken = default)
    {
        EnsureNetworkAllowed();

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new VcsHostingProviderException(ProviderType, "Repository name cannot be empty.");
        }

        try
        {
            var created = await _gitHubClient.CreateRepositoryAsync(name, description, isPrivate, cancellationToken).ConfigureAwait(false);
            return MapToSummary(created);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new VcsHostingProviderException(
                ProviderType,
                "GitHub denied repository creation (HTTP 403 Forbidden). Ensure the NightCodeForge GitHub App registration and installation include 'Administration: Read & write' repository permission and allow creating repositories for your account.",
                ex.StatusCode,
                ex);
        }
        catch (GitHubAuthenticationException ex)
        {
            throw new VcsHostingProviderException(ProviderType, ex.Message, HttpStatusCode.Unauthorized, ex);
        }
        catch (GitHubApiException ex)
        {
            throw new VcsHostingProviderException(ProviderType, ex.Message, ex.StatusCode, ex);
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetGitHttpAuthHeaderAsync(CancellationToken cancellationToken = default)
    {
        if (_securityOptions.Value.AirGappedMode)
        {
            return null;
        }

        if (!await _authService.IsConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var accessToken = await _authService.GetValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return null;
            }

            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{GitBasicUsername}:{accessToken}"));
            return $"Authorization: Basic {encoded}";
        }
        catch (GitHubAuthenticationException ex)
        {
            _logger.LogWarning(ex, "Unable to obtain valid GitHub access token for ephemeral Git HTTP authentication.");
            return null;
        }
    }

    private void EnsureNetworkAllowed()
    {
        if (_securityOptions.Value.AirGappedMode)
        {
            throw new VcsHostingProviderException(
                ProviderType,
                "Remote GitHub operations are disabled while Air-Gapped Mode is enabled.");
        }
    }

    private static VcsRepositorySummary MapToSummary(GitHubRepository repo)
    {
        var httpsUrl = !string.IsNullOrWhiteSpace(repo.CloneUrl)
            ? repo.CloneUrl
            : (!string.IsNullOrWhiteSpace(repo.HtmlUrl) ? $"{repo.HtmlUrl.TrimEnd('/')}.git" : string.Empty);

        return new VcsRepositorySummary(
            Id: repo.Id.ToString(CultureInfo.InvariantCulture),
            Name: repo.Name,
            FullName: repo.FullName,
            Owner: repo.Owner,
            Description: repo.Description,
            IsPrivate: repo.Private,
            DefaultBranch: repo.DefaultBranch,
            HttpsCloneUrl: httpsUrl,
            SshCloneUrl: repo.SshUrl,
            UpdatedAt: repo.UpdatedAt);
    }
}
