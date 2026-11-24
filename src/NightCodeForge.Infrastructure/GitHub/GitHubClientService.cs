using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.GitHub;

/// <summary>
/// Typed REST client for GitHub API with automatic reactive 401 token refresh recovery.
/// </summary>
public partial class GitHubClientService : IGitHubClientService
{
    private const string GitHubApiBaseUrl = "https://api.github.com";
    private const string GitHubApiHost = "api.github.com";
    private const string GitHubApiVersion = "2022-11-28";
    private const string UserAgent = "NightCodeForge";
    private const int DefaultPerPage = 100;
    private const int MaxPaginationPages = 25;

    [GeneratedRegex(@"<([^>]+)>\s*;\s*[^,]*\brel=""?next""?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NextLinkRegex();

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

    public async Task<GitHubPullRequest?> FindOpenPullRequestByHeadAsync(
        string owner,
        string repo,
        string headBranch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(headBranch);

        var qualifiedHead = headBranch.Contains(':', StringComparison.Ordinal)
            ? headBranch
            : $"{owner}:{headBranch}";
        var url = $"{GitHubApiBaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls?state=open&head={Uri.EscapeDataString(qualifiedHead)}&per_page=1";
        using var response = await SendWithAuthAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            return ParsePullRequest(element);
        }

        return null;
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

        var queryParams = new List<string> { $"per_page={DefaultPerPage}" };
        if (!string.IsNullOrWhiteSpace(filter.State))
        {
            queryParams.Add($"state={Uri.EscapeDataString(filter.State)}");
        }
        if (!string.IsNullOrWhiteSpace(filter.Labels))
        {
            var normalizedLabels = string.Join(
                ",",
                filter.Labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (!string.IsNullOrWhiteSpace(normalizedLabels))
            {
                queryParams.Add($"labels={Uri.EscapeDataString(normalizedLabels)}");
            }
        }
        if (!string.IsNullOrWhiteSpace(filter.Assignee))
        {
            queryParams.Add($"assignee={Uri.EscapeDataString(filter.Assignee)}");
        }
        if (filter.Since.HasValue)
        {
            queryParams.Add($"since={Uri.EscapeDataString(filter.Since.Value.ToString("o"))}");
        }

        var queryString = "?" + string.Join("&", queryParams);
        var url = $"{GitHubApiBaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues{queryString}";

        return await GetPaginatedListAsync(
            url,
            element =>
            {
                var issue = ParseIssue(element);
                // GitHub issues endpoint includes pull requests; filter them out
                return issue.IsPullRequest ? null : issue;
            },
            cancellationToken);
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

    public async Task<IReadOnlyList<GitHubIssueComment>> ListIssueCommentsAsync(
        string owner,
        string repo,
        int issueNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issueNumber);

        var url = $"{GitHubApiBaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{issueNumber}/comments?per_page={DefaultPerPage}";
        return await GetPaginatedListAsync<GitHubIssueComment>(
            url,
            element => ParseIssueComment(element),
            cancellationToken);
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
        return ParseIssueComment(doc.RootElement);
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
        var url = $"{GitHubApiBaseUrl}/user/repos?sort=updated&per_page={DefaultPerPage}";
        return await GetPaginatedListAsync<GitHubRepository>(
            url,
            element => ParseRepository(element),
            cancellationToken);
    }

    public async Task<GitHubRepository> CreateRepositoryAsync(
        string name,
        string? description,
        bool isPrivate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var url = $"{GitHubApiBaseUrl}/user/repos";
        var payload = new Dictionary<string, object?>
        {
            ["name"] = name.Trim(),
            ["private"] = isPrivate
        };
        if (!string.IsNullOrWhiteSpace(description))
        {
            payload["description"] = description.Trim();
        }

        var jsonBody = JsonSerializer.Serialize(payload);
        using var response = await SendWithAuthAsync(
            () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            },
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(content);
        return ParseRepository(doc.RootElement);
    }

    private async Task<IReadOnlyList<T>> GetPaginatedListAsync<T>(
        string initialUrl,
        Func<JsonElement, T?> mapItem,
        CancellationToken cancellationToken) where T : class
    {
        var items = new List<T>();
        string? nextUrl = initialUrl;

        for (var page = 0; page < MaxPaginationPages && !string.IsNullOrWhiteSpace(nextUrl); page++)
        {
            var pageUrl = nextUrl;
            using var response = await SendWithAuthAsync(
                () => new HttpRequestMessage(HttpMethod.Get, pageUrl),
                cancellationToken);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var mapped = mapItem(element);
                    if (mapped != null)
                    {
                        items.Add(mapped);
                    }
                }
            }

            nextUrl = ExtractNextPageUrl(response.Headers, _httpClient.BaseAddress);
        }

        return items;
    }

    private string? ExtractNextPageUrl(HttpResponseHeaders headers, Uri? clientBaseAddress)
    {
        if (!headers.TryGetValues("Link", out var linkValues))
        {
            return null;
        }

        foreach (var headerValue in linkValues)
        {
            var match = NextLinkRegex().Match(headerValue);
            if (!match.Success)
            {
                continue;
            }

            var rawNextUrl = match.Groups[1].Value.Trim();
            if (!Uri.TryCreate(rawNextUrl, UriKind.RelativeOrAbsolute, out var nextUri))
            {
                return null;
            }

            if (!nextUri.IsAbsoluteUri)
            {
                if (clientBaseAddress == null || !Uri.TryCreate(clientBaseAddress, nextUri, out nextUri))
                {
                    return null;
                }
            }

            var isOfficialGitHubOrigin =
                string.Equals(nextUri.Host, GitHubApiHost, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(nextUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

            var matchesBaseAddressOrigin =
                clientBaseAddress != null &&
                string.Equals(nextUri.Host, clientBaseAddress.Host, StringComparison.OrdinalIgnoreCase) &&
                nextUri.Port == clientBaseAddress.Port &&
                string.Equals(nextUri.Scheme, clientBaseAddress.Scheme, StringComparison.OrdinalIgnoreCase);

            if (!isOfficialGitHubOrigin && !matchesBaseAddressOrigin)
            {
                _logger.LogWarning(
                    "Ignoring paginated GitHub Link next URL '{NextUrl}' because its origin does not match expected GitHub API origin.",
                    nextUri.GetLeftPart(UriPartial.Authority));
                return null;
            }

            return nextUri.ToString();
        }

        return null;
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
            Description = GetOptionalString(root, "description") ?? string.Empty,
            Private = root.TryGetProperty("private", out var privEl) && privEl.ValueKind == JsonValueKind.True,
            HtmlUrl = GetOptionalString(root, "html_url") ?? string.Empty,
            CloneUrl = GetOptionalString(root, "clone_url") ?? string.Empty,
            SshUrl = GetOptionalString(root, "ssh_url") ?? string.Empty,
            DefaultBranch = GetOptionalString(root, "default_branch") ?? "main",
            UpdatedAt = GetOptionalDateTimeOffset(root, "updated_at")
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
                    GetOptionalString(labelEl, "name") is { } name)
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
                    GetOptionalString(assigneeEl, "login") is { } login)
                {
                    assignees.Add(login);
                }
            }
        }

        var isPullRequest = root.TryGetProperty("pull_request", out var prEl)
            && prEl.ValueKind != JsonValueKind.Null
            && prEl.ValueKind != JsonValueKind.Undefined;

        return new GitHubIssue
        {
            Number = GetOptionalInt32(root, "number") ?? 0,
            Title = GetOptionalString(root, "title") ?? string.Empty,
            Body = GetOptionalString(root, "body"),
            HtmlUrl = GetOptionalString(root, "html_url") ?? string.Empty,
            State = GetOptionalString(root, "state") ?? "open",
            StateReason = GetOptionalString(root, "state_reason"),
            Labels = labels,
            Assignees = assignees,
            CreatedAt = GetOptionalDateTimeOffset(root, "created_at") ?? DateTimeOffset.UtcNow,
            UpdatedAt = GetOptionalDateTimeOffset(root, "updated_at"),
            ClosedAt = GetOptionalDateTimeOffset(root, "closed_at"),
            IsPullRequest = isPullRequest
        };
    }

    private static GitHubIssueComment ParseIssueComment(JsonElement root)
    {
        return new GitHubIssueComment
        {
            Id = GetOptionalInt64(root, "id") ?? 0L,
            Body = GetOptionalString(root, "body") ?? string.Empty,
            HtmlUrl = GetOptionalString(root, "html_url") ?? string.Empty,
            CreatedAt = GetOptionalDateTimeOffset(root, "created_at") ?? DateTimeOffset.UtcNow,
            UserLogin = root.TryGetProperty("user", out var userEl) && userEl.ValueKind == JsonValueKind.Object
                ? GetOptionalString(userEl, "login")
                : null
        };
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static int? GetOptionalInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop)
               && prop.ValueKind == JsonValueKind.Number
               && prop.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static long? GetOptionalInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop)
               && prop.ValueKind == JsonValueKind.Number
               && prop.TryGetInt64(out var value)
            ? value
            : null;
    }

    private static DateTimeOffset? GetOptionalDateTimeOffset(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop)
               && prop.ValueKind == JsonValueKind.String
               && prop.TryGetDateTimeOffset(out var value)
            ? value
            : null;
    }
}

