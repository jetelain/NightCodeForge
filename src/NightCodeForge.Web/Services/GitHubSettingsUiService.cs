using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Result of probing repository access via GitHub REST API.
/// </summary>
public class GitHubRepositoryAccessResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public GitHubRepository? Repository { get; set; }
    public bool HasPushAccess => Repository?.Permissions?.Push == true;
    public bool HasAdminAccess => Repository?.Permissions?.Admin == true;
}

/// <summary>
/// Status of the GitHub connection and authenticated user profile.
/// </summary>
public class GitHubConnectionState
{
    public bool IsConnected { get; set; }
    public GitHubUser? AuthenticatedUser { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Presentation service isolating GitHub settings business logic, Device Flow polling orchestration,
/// and repository probe calls away from Blazor Razor code blocks.
/// </summary>
public class GitHubSettingsUiService
{
    public const string GitHubAppConnectionsBaseUrl = "https://github.com/settings/connections/applications/";
    public const int MinPollingIntervalMinutes = 1;
    public const int MaxPollingIntervalMinutes = 1440;

    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IGitHubDeviceAuthService _authService;
    private readonly IGitHubClientService _clientService;
    private readonly ILogger<GitHubSettingsUiService> _logger;

    public GitHubSettingsUiService(
        IWorkspaceRepository workspaceRepository,
        IGitHubDeviceAuthService authService,
        IGitHubClientService clientService,
        ILogger<GitHubSettingsUiService> logger)
    {
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _clientService = clientService ?? throw new ArgumentNullException(nameof(clientService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GitHubIntegrationSettings> LoadSettingsAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId, cancellationToken);
        if (workspace == null)
        {
            throw new KeyNotFoundException($"Workspace #{workspaceId} not found.");
        }

        return workspace.GitHubSettings ?? new GitHubIntegrationSettings();
    }

    public async Task SaveSettingsAsync(int workspaceId, GitHubIntegrationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.PollingIntervalMinutes < MinPollingIntervalMinutes || settings.PollingIntervalMinutes > MaxPollingIntervalMinutes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                $"Polling interval must be between {MinPollingIntervalMinutes} and {MaxPollingIntervalMinutes} minutes.");
        }

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId, cancellationToken);
        if (workspace == null)
        {
            throw new KeyNotFoundException($"Workspace #{workspaceId} not found.");
        }

        settings.ClientId = string.IsNullOrWhiteSpace(settings.ClientId)
            ? GitHubIntegrationSettings.DefaultClientId
            : settings.ClientId.Trim();
        settings.RepositoryOwner = string.IsNullOrWhiteSpace(settings.RepositoryOwner)
            ? null
            : settings.RepositoryOwner.Trim();
        settings.RepositoryName = string.IsNullOrWhiteSpace(settings.RepositoryName)
            ? null
            : settings.RepositoryName.Trim();
        settings.IssueFilterLabel = string.IsNullOrWhiteSpace(settings.IssueFilterLabel)
            ? null
            : settings.IssueFilterLabel.Trim();

        workspace.GitHubSettings = settings;
        await _workspaceRepository.UpdateAsync(workspace, cancellationToken);
        _logger.LogInformation("Saved GitHub integration settings for workspace #{WorkspaceId}.", workspaceId);
    }

    public async Task<GitHubConnectionState> GetConnectionStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var isConnected = await _authService.IsConnectedAsync(cancellationToken);
            if (!isConnected)
            {
                return new GitHubConnectionState { IsConnected = false };
            }

            var user = await _authService.GetAuthenticatedUserAsync(cancellationToken);
            return new GitHubConnectionState
            {
                IsConnected = user != null,
                AuthenticatedUser = user
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve GitHub connection state.");
            return new GitHubConnectionState
            {
                IsConnected = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public Task<GitHubDeviceCodeResponse> StartDeviceFlowAsync(string? clientId = null, CancellationToken cancellationToken = default)
    {
        return _authService.RequestDeviceCodeAsync(clientId, cancellationToken);
    }

    public Task<GitHubOAuthTokenPayload> PollForAccessTokenAsync(
        string deviceCode,
        int interval,
        DateTimeOffset expiresAt,
        string? clientId = null,
        CancellationToken cancellationToken = default)
    {
        return _authService.PollForAccessTokenAsync(deviceCode, interval, expiresAt, clientId, cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        return _authService.DisconnectAsync(cancellationToken);
    }

    public async Task<GitHubRepositoryAccessResult> TestRepositoryAccessAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            return new GitHubRepositoryAccessResult
            {
                Success = false,
                ErrorMessage = "Repository owner and name must be specified."
            };
        }

        try
        {
            var repository = await _clientService.GetRepositoryAsync(owner.Trim(), repo.Trim(), cancellationToken);
            return new GitHubRepositoryAccessResult
            {
                Success = true,
                Repository = repository
            };
        }
        catch (GitHubAuthenticationException authEx)
        {
            return new GitHubRepositoryAccessResult
            {
                Success = false,
                ErrorMessage = $"Authentication error: {authEx.Message}"
            };
        }
        catch (GitHubApiException apiEx)
        {
            return new GitHubRepositoryAccessResult
            {
                Success = false,
                ErrorMessage = $"GitHub API HTTP {(int)apiEx.StatusCode} ({apiEx.StatusCode}): Ensure the NightCodeForge GitHub App is installed on {owner.Trim()}/{repo.Trim()}."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Repository probe failed for {Owner}/{Repo}", owner, repo);
            return new GitHubRepositoryAccessResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public string GetAppInstallationUrl(string? clientId = null)
    {
        var effectiveClientId = string.IsNullOrWhiteSpace(clientId)
            ? GitHubIntegrationSettings.DefaultClientId
            : clientId.Trim();

        return $"{GitHubAppConnectionsBaseUrl}{Uri.EscapeDataString(effectiveClientId)}";
    }

    public bool ShouldDisplayAvatar(GitHubUser? user, SecurityOptions? securityOptions)
    {
        if (securityOptions == null || securityOptions.EnableContentSecurityPolicy)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(user?.AvatarUrl);
    }

    public async Task<IReadOnlyList<GitHubRepository>> LoadAccessibleRepositoriesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _clientService.ListAccessibleRepositoriesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load accessible GitHub repositories for settings dropdowns.");
            return Array.Empty<GitHubRepository>();
        }
    }

    public IReadOnlyList<string> GetAvailableOwners(
        IReadOnlyList<GitHubRepository>? repositories,
        string? authenticatedUserLogin = null,
        string? currentOwner = null)
    {
        var owners = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddOwner(string? candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                var trimmed = candidate.Trim();
                if (seen.Add(trimmed))
                {
                    owners.Add(trimmed);
                }
            }
        }

        AddOwner(currentOwner);
        AddOwner(authenticatedUserLogin);

        if (repositories != null)
        {
            foreach (var repo in repositories.OrderBy(r => r.Owner, StringComparer.OrdinalIgnoreCase))
            {
                AddOwner(repo.Owner);
            }
        }

        return owners;
    }

    public IReadOnlyList<GitHubRepository> GetRepositoriesForOwner(
        IReadOnlyList<GitHubRepository>? repositories,
        string? owner,
        string? currentRepoName = null)
    {
        var list = new List<GitHubRepository>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (repositories != null && !string.IsNullOrWhiteSpace(owner))
        {
            var trimmedOwner = owner.Trim();
            foreach (var repo in repositories
                .Where(r => string.Equals(r.Owner, trimmedOwner, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(repo.Name) && seen.Add(repo.Name))
                {
                    list.Add(repo);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(currentRepoName))
        {
            var trimmedRepo = currentRepoName.Trim();
            if (seen.Add(trimmedRepo))
            {
                var effectiveOwner = string.IsNullOrWhiteSpace(owner) ? string.Empty : owner.Trim();
                list.Insert(0, new GitHubRepository
                {
                    Name = trimmedRepo,
                    Owner = effectiveOwner,
                    FullName = string.IsNullOrEmpty(effectiveOwner) ? trimmedRepo : $"{effectiveOwner}/{trimmedRepo}"
                });
            }
        }

        return list;
    }
}

