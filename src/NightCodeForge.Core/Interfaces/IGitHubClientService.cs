using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Typed REST client for GitHub API operations with reactive 401 token refresh recovery.
/// </summary>
public interface IGitHubClientService
{
    // Pull Requests
    Task<GitHubPullRequest> CreatePullRequestAsync(string owner, string repo, CreateGitHubPullRequestRequest request, CancellationToken cancellationToken = default);
    Task<GitHubPullRequest> UpdatePullRequestAsync(string owner, string repo, int prNumber, UpdateGitHubPullRequestRequest request, CancellationToken cancellationToken = default);
    Task<GitHubPullRequest> GetPullRequestAsync(string owner, string repo, int prNumber, CancellationToken cancellationToken = default);

    // Issues, Labels & Comments
    Task<IReadOnlyList<GitHubIssue>> ListIssuesAsync(string owner, string repo, GitHubIssueFilter filter, CancellationToken cancellationToken = default);
    Task<GitHubIssue> GetIssueAsync(string owner, string repo, int issueNumber, CancellationToken cancellationToken = default);
    Task<GitHubIssue> CreateIssueAsync(string owner, string repo, CreateGitHubIssueRequest request, CancellationToken cancellationToken = default);
    Task<GitHubIssue> UpdateIssueAsync(string owner, string repo, int issueNumber, UpdateGitHubIssueRequest request, CancellationToken cancellationToken = default);
    Task<GitHubIssueComment> AddIssueCommentAsync(string owner, string repo, int issueNumber, string body, CancellationToken cancellationToken = default);

    // Repository Probe / Connection Test & Discovery
    Task<GitHubRepository> GetRepositoryAsync(string owner, string repo, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitHubRepository>> ListAccessibleRepositoriesAsync(CancellationToken cancellationToken = default);
}

