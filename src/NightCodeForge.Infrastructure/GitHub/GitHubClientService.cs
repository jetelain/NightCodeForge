using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.GitHub;

/// <summary>
/// Typed REST client for GitHub API with automatic reactive 401 token refresh recovery.
/// </summary>
public class GitHubClientService : IGitHubClientService
{
    private const string GitHubApiBaseUrl = "https://api.github.com";
    private const string GitHubApiVersion = "2022-11-28";
    private const string UserAgent = "NightCodeForge";

    private readonly HttpClient _httpClient;
    private readonly IGitHubDeviceAuthService _authService;
    private readonly ILogger<GitHubClientService> _logger;

    public GitHubClientService(
        HttpClient httpClient,
        IGitHubDeviceAuthService authService,
        ILogger<GitHubClientService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ==========================================
    // Pull Requests
    // ==========================================

    public async Task<GitHubPullRequest> CreatePullRequestAsync(
        string owner,
        string repo,
        CreateGitHubPullRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Head);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Base);

        var url = $"{GitHubApiBaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls";
        var payload = new
        {
            title = request.Title,
            head = request.Head,
            @base = request.Base,
            body = request.Body,
            draft = request.Draft
        };

        var jsonBody = JsonSerializer.Serialize(payload);
        using var response = await SendWithAuthAsync(
            () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            },
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(content);
        return ParsePullRequest(doc.RootElement);
    }

    public async Task<GitHubPullRequest> UpdatePullRequestAsync(
        string owner,
        string repo,
        int prNumber,
        UpdateGitHubPullRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);
        ArgumentNullException.ThrowIfNull(request);

        var url = $"{GitHubApiBaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{prNumber}";
        var payload = new Dictionary<string, object?>();
        if (request.Title != null) payload["title"] = request.Title;
        if (request.Body != null) payload["body"] = request.Body;
        if (request.State != null) payload["state"] = request.State;
        if (request.Base != null) payload["base"] = request.Base;

        var jsonBody = JsonSerializer.Serialize(payload);
        using var response = await SendWithAuthAsync(
            () => new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            },
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(content);
        return ParsePullRequest(doc.RootElement);
    }

    public async Task<GitHubPullRequest> GetPullRequestAsync(
        string owner,
        string repo,
        int prNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);

        var url = $"{GitHubApiBaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{prNumber}";
        using var response = await SendWithAuthAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(content);
        return ParsePullRequest(doc.RootElement);
    }

    // ==========================================
    // Issues, Labels & Comments
    // ==========================================

    public async Task<IReadOnlyList<GitHubIssue>> ListIssuesAsync(
        string owner,
        string repo,
        GitHubIssueFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentNullException.ThrowIfNull(filter);

        var queryParams = new List<string>();
        if (!string.IsNullOrWhiteSpace(filter.State))
        {
            queryParams.Add($"state={Uri.EscapeDataString(filter.State)}");
        }
        if (!string.IsNullOrWhiteSpace(filter.Labels))
        {
            queryParams.Add($"labels={Uri.EscapeDataString(filter.Labels)}");
        }
        if (!string.IsNullOrWhiteSpace(filter.Assignee))
        {
            queryParams.Add($"assignee={Uri.EscapeDataString(filter.Assignee)}");
        }
        if (filter.Since.HasValue)
        {
            queryParams.Add($"since={Uri.EscapeDataString(filter.Since.Value.ToString("o"))}");
        }

        var queryString = queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : string.Empty;
        var url = $"{GitHubApiBaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues{queryString}";

        using var response = await SendWithAuthAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(content);
        var issues = new List<GitHubIssue>();

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var issue = ParseIssue(element);
            // GitHub issues endpoint includes pull requests; filter them out
            if (!issue.IsPullRequest)
            {
                issues.Add(issue);
            }
        }

        return issues;
    }

    public async Task<GitHubIssue> GetIssueAsync(
        string owner,
        string repo,
        int issueNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issueNumber);

        var url = $"{GitHubApiBaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{issueNumber}";
        using var response = await SendWithAuthAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(content);
        return ParseIssue(doc.RootElement);
    }

    public async Task<GitHubIssue> CreateIssueAsync(
        string owner,
        string repo,
        CreateGitHubIssueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);

        var url = $"{GitHubApiBaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues";
        var payload = new Dictionary<string, object?>
        {
            ["title"] = request.Title
        };
        if (request.Body != null) payload["body"] = request.Body;
        if (request.Labels != null && request.Labels.Count > 0) payload["labels"] = request.Labels;
        if (request.Assignees != null && request.Assignees.Count > 0) payload["assignees"] = request.Assignees;

        var jsonBody = JsonSerializer.Serialize(payload);
        using var response = await SendWithAuthAsync(
            () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            },
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(content);
        return ParseIssue(doc.RootElement);
    }

    public async Task<GitHubIssue> UpdateIssueAsync(
        string owner,
        string repo,
        int issueNumber,
        UpdateGitHubIssueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issueNumber);
        ArgumentNullException.ThrowIfNull(request);

        var url = $"{GitHubApiBaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{issueNumber}";
        var payload = new Dictionary<string, object?>();
        if (request.Title != null) payload["title"] = request.Title;
        if (request.Body != null) payload["body"] = request.Body;
        if (request.State != null) payload["state"] = request.State;
        if (request.StateReason != null) payload["state_reason"] = request.StateReason;
        if (request.Labels != null) payload["labels"] = request.Labels;
        if (request.Assignees != null) payload["assignees"] = request.Assignees;

        var jsonBody = JsonSerializer.Serialize(payload);
        using var response = await SendWithAuthAsync(
            () => new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            },
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(content);
        return ParseIssue(doc.RootElement);
    }

    public async Task<GitHubIssueComment> AddIssueCommentAsync(
        string owner,
        string repo,
        int issueNumber,
        string body,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issueNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var url = $"{GitHubApiBaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{issueNumber}/comments";
        var payload = new { body };
        var jsonBody = JsonSerializer.Serialize(payload);

        using var response = await SendWithAuthAsync(
            () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            },
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        return new GitHubIssueComment
        {
            Id = root.GetProperty("id").GetInt64(),
            Body = root.GetProperty("body").GetString() ?? string.Empty,
            HtmlUrl = root.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() ?? string.Empty : string.Empty,
            CreatedAt = root.TryGetProperty("created_at", out var createdEl) && createdEl.ValueKind == JsonValueKind.String
                ? createdEl.GetDateTimeOffset()
                : DateTimeOffset.UtcNow,
            UserLogin = root.TryGetProperty("user", out var userEl) &&
                        userEl.ValueKind == JsonValueKind.Object &&
                        userEl.TryGetProperty("login", out var loginEl)
                ? loginEl.GetString()
                : null
        };
    }

    // ==========================================
    // Repository Probe / Connection Test & Discovery
    // ==========================================

    public async Task<GitHubRepository> GetRepositoryAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        var url = $"{GitHubApiBaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
        using var response = await SendWithAuthAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(content);
        return ParseRepository(doc.RootElement);
    }

    public async Task<IReadOnlyList<GitHubRepository>> ListAccessibleRepositoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var url = $"{GitHubApiBaseUrl}/user/repos?sort=updated&per_page=100";
        using var response = await SendWithAuthAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(content);
        var repositories = new List<GitHubRepository>();

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    repositories.Add(ParseRepository(element));
                }
            }
        }

        return repositories;
    }

    // ==========================================
    // Internal HTTP Helper with 401 Reactive Refresh
    // ==========================================

    private async Task<HttpResponseMessage> SendWithAuthAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        var token = await _authService.GetValidAccessTokenAsync(cancellationToken);

        using var request = requestFactory();
        ApplyStandardHeaders(request, token);

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("GitHub request returned 401 Unauthorized. Attempting single reactive refresh.");
            response.Dispose();

            var refreshedPayload = await _authService.RefreshAccessTokenAsync(cancellationToken);
            var refreshedToken = !string.IsNullOrWhiteSpace(refreshedPayload?.AccessToken)
                ? refreshedPayload.AccessToken
                : await _authService.GetValidAccessTokenAsync(cancellationToken);

            using var retryRequest = requestFactory();
            ApplyStandardHeaders(retryRequest, refreshedToken);

            response = await _httpClient.SendAsync(retryRequest, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new GitHubAuthenticationException("GitHub authentication failed (HTTP 401 Unauthorized). Please re-authenticate in Settings.");
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = response.StatusCode;
            response.Dispose();
            throw new GitHubApiException(status, $"GitHub API returned HTTP {(int)status} ({status}): {errorBody}", errorBody);
        }

        return response;
    }

    private static void ApplyStandardHeaders(HttpRequestMessage request, string token)
    {
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Remove("X-GitHub-Api-Version");
        request.Headers.Add("X-GitHub-Api-Version", GitHubApiVersion);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, "1.0"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static GitHubRepository ParseRepository(JsonElement root)
    {
        var repoModel = new GitHubRepository
        {
            Id = root.GetProperty("id").GetInt64(),
            Name = root.GetProperty("name").GetString() ?? string.Empty,
            FullName = root.GetProperty("full_name").GetString() ?? string.Empty,
            Owner = root.TryGetProperty("owner", out var ownerEl) &&
                    ownerEl.ValueKind == JsonValueKind.Object &&
                    ownerEl.TryGetProperty("login", out var ownerLoginEl)
                ? ownerLoginEl.GetString() ?? string.Empty
                : string.Empty,
            Private = root.TryGetProperty("private", out var privEl) && privEl.ValueKind == JsonValueKind.True,
            HtmlUrl = root.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() ?? string.Empty : string.Empty,
            DefaultBranch = root.TryGetProperty("default_branch", out var branchEl) ? branchEl.GetString() ?? "main" : "main"
        };

        if (root.TryGetProperty("permissions", out var permEl) && permEl.ValueKind == JsonValueKind.Object)
        {
            repoModel.Permissions = new GitHubRepositoryPermissions
            {
                Admin = permEl.TryGetProperty("admin", out var aEl) && aEl.ValueKind == JsonValueKind.True,
                Push = permEl.TryGetProperty("push", out var pushEl) && pushEl.ValueKind == JsonValueKind.True,
                Pull = permEl.TryGetProperty("pull", out var pullEl) && pullEl.ValueKind == JsonValueKind.True
            };
        }

        return repoModel;
    }

    private static GitHubPullRequest ParsePullRequest(JsonElement root)
    {
        return new GitHubPullRequest
        {
            Number = root.GetProperty("number").GetInt32(),
            Title = root.GetProperty("title").GetString() ?? string.Empty,
            Body = root.TryGetProperty("body", out var bEl) ? bEl.GetString() : null,
            HtmlUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? string.Empty : string.Empty,
            State = root.TryGetProperty("state", out var stEl) ? stEl.GetString() ?? "open" : "open",
            Draft = root.TryGetProperty("draft", out var drEl) && drEl.ValueKind == JsonValueKind.True,
            Merged = root.TryGetProperty("merged", out var mgEl) && mgEl.ValueKind == JsonValueKind.True,
            MergeCommitSha = root.TryGetProperty("merge_commit_sha", out var shaEl) ? shaEl.GetString() : null,
            CreatedAt = root.TryGetProperty("created_at", out var crEl) && crEl.ValueKind == JsonValueKind.String
                ? crEl.GetDateTimeOffset()
                : DateTimeOffset.UtcNow,
            ClosedAt = root.TryGetProperty("closed_at", out var clEl) && clEl.ValueKind == JsonValueKind.String ? clEl.GetDateTimeOffset() : null,
            MergedAt = root.TryGetProperty("merged_at", out var maEl) && maEl.ValueKind == JsonValueKind.String ? maEl.GetDateTimeOffset() : null
        };
    }

    private static GitHubIssue ParseIssue(JsonElement root)
    {
        var labels = new List<string>();
        if (root.TryGetProperty("labels", out var labelsEl) && labelsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var labelEl in labelsEl.EnumerateArray())
            {
                if (labelEl.ValueKind == JsonValueKind.Object &&
                    labelEl.TryGetProperty("name", out var nameEl) &&
                    nameEl.GetString() is { } name)
                {
                    labels.Add(name);
                }
                else if (labelEl.ValueKind == JsonValueKind.String && labelEl.GetString() is { } strName)
                {
                    labels.Add(strName);
                }
            }
        }

        var assignees = new List<string>();
        if (root.TryGetProperty("assignees", out var assigneesEl) && assigneesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var assigneeEl in assigneesEl.EnumerateArray())
            {
                if (assigneeEl.ValueKind == JsonValueKind.Object &&
                    assigneeEl.TryGetProperty("login", out var loginEl) &&
                    loginEl.GetString() is { } login)
                {
                    assignees.Add(login);
                }
            }
        }

        var isPullRequest = root.TryGetProperty("pull_request", out var prEl) && prEl.ValueKind != JsonValueKind.Null;

        return new GitHubIssue
        {
            Number = root.GetProperty("number").GetInt32(),
            Title = root.GetProperty("title").GetString() ?? string.Empty,
            Body = root.TryGetProperty("body", out var bEl) ? bEl.GetString() : null,
            HtmlUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? string.Empty : string.Empty,
            State = root.TryGetProperty("state", out var stEl) ? stEl.GetString() ?? "open" : "open",
            StateReason = root.TryGetProperty("state_reason", out var srEl) ? srEl.GetString() : null,
            Labels = labels,
            Assignees = assignees,
            CreatedAt = root.TryGetProperty("created_at", out var crEl) && crEl.ValueKind == JsonValueKind.String
                ? crEl.GetDateTimeOffset()
                : DateTimeOffset.UtcNow,
            UpdatedAt = root.TryGetProperty("updated_at", out var upEl) && upEl.ValueKind == JsonValueKind.String ? upEl.GetDateTimeOffset() : null,
            ClosedAt = root.TryGetProperty("closed_at", out var clEl) && clEl.ValueKind == JsonValueKind.String ? clEl.GetDateTimeOffset() : null,
            IsPullRequest = isPullRequest
        };
    }
}

