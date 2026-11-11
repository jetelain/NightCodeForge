using System.Text.Json.Serialization;

namespace NightCodeForge.Core.Domain;

/// <summary>
/// OAuth token payload received from GitHub App Device Flow.
/// Kept strictly separated from database entities and persisted only in the OS keyring token store (Pmad.KeySharp).
/// </summary>
public class GitHubOAuthTokenPayload
{
    public string AccessToken { get; set; } = string.Empty;
    public DateTimeOffset AccessTokenExpiresAtUtc { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset? RefreshTokenExpiresAtUtc { get; set; }
    public string? TokenType { get; set; }
    public string? Scope { get; set; }
    public string? AuthenticatedUserLogin { get; set; }
    public string? ClientId { get; set; }
}

/// <summary>
/// Response returned by GitHub when initiating the Device Flow.
/// </summary>
public class GitHubDeviceCodeResponse
{
    public string DeviceCode { get; set; } = string.Empty;
    public string UserCode { get; set; } = string.Empty;
    public string VerificationUri { get; set; } = "https://github.com/login/device";
    public int ExpiresIn { get; set; }
    public int Interval { get; set; } = 5;
}

/// <summary>
/// Represents a GitHub user profile.
/// </summary>
public class GitHubUser
{
    public long Id { get; set; }
    public string Login { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? AvatarUrl { get; set; }
}

/// <summary>
/// Represents a GitHub repository metadata record.
/// </summary>
public class GitHubRepository
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public bool Private { get; set; }
    public string HtmlUrl { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";
    public GitHubRepositoryPermissions? Permissions { get; set; }
}

/// <summary>
/// Fine-grained permission flags for a GitHub repository.
/// </summary>
public class GitHubRepositoryPermissions
{
    public bool Admin { get; set; }
    public bool Push { get; set; }
    public bool Pull { get; set; }
}

/// <summary>
/// Represents a GitHub Pull Request.
/// </summary>
public class GitHubPullRequest
{
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }
    public string HtmlUrl { get; set; } = string.Empty;
    public string State { get; set; } = "open";
    public bool Draft { get; set; }
    public bool Merged { get; set; }
    public string? MergeCommitSha { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? MergedAt { get; set; }
}

/// <summary>
/// Request parameters to create a new Pull Request on GitHub.
/// </summary>
public class CreateGitHubPullRequestRequest
{
    public string Title { get; set; } = string.Empty;
    public string Head { get; set; } = string.Empty;
    public string Base { get; set; } = string.Empty;
    public string? Body { get; set; }
    public bool Draft { get; set; }
}

/// <summary>
/// Request parameters to update an existing Pull Request on GitHub.
/// </summary>
public class UpdateGitHubPullRequestRequest
{
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? State { get; set; }
    public string? Base { get; set; }
}

/// <summary>
/// Represents a GitHub Issue.
/// </summary>
public class GitHubIssue
{
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }
    public string HtmlUrl { get; set; } = string.Empty;
    public string State { get; set; } = "open";
    public string? StateReason { get; set; }
    public IReadOnlyList<string> Labels { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Assignees { get; set; } = Array.Empty<string>();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public bool IsPullRequest { get; set; }
}

/// <summary>
/// Filter criteria when querying issues from a repository.
/// </summary>
public class GitHubIssueFilter
{
    public string? State { get; set; } = "open";
    public string? Labels { get; set; }
    public string? Assignee { get; set; }
    public DateTimeOffset? Since { get; set; }
}

/// <summary>
/// Request parameters to create a new Issue on GitHub.
/// </summary>
public class CreateGitHubIssueRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }
    public IReadOnlyList<string>? Labels { get; set; }
    public IReadOnlyList<string>? Assignees { get; set; }
}

/// <summary>
/// Request parameters to update an existing Issue on GitHub.
/// </summary>
public class UpdateGitHubIssueRequest
{
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? State { get; set; }
    public string? StateReason { get; set; }
    public IReadOnlyList<string>? Labels { get; set; }
    public IReadOnlyList<string>? Assignees { get; set; }
}

/// <summary>
/// Represents a comment on a GitHub Issue or Pull Request.
/// </summary>
public class GitHubIssueComment
{
    public long Id { get; set; }
    public string Body { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string? UserLogin { get; set; }
}

