using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.GitHub;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.GitHub;

public class GitHubClientServiceTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Handler(request));
        }
    }

    [Fact]
    public async Task CreatePullRequestAsync_SendsCorrectPayload_AndParsesResponse()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal("https://api.github.com/repos/octocat/hello-world/pulls", req.RequestUri?.ToString());
                Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
                Assert.Equal("ghu_valid_token", req.Headers.Authorization?.Parameter);

                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        number = 42,
                        title = "New Feature PR",
                        body = "PR description",
                        html_url = "https://github.com/octocat/hello-world/pull/42",
                        state = "open",
                        draft = true,
                        merged = false,
                        created_at = DateTimeOffset.UtcNow
                    }))
                };
            }
        };

        var authMock = new Mock<IGitHubDeviceAuthService>();
        authMock.Setup(a => a.GetValidAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("ghu_valid_token");

        var client = new GitHubClientService(new HttpClient(handler), authMock.Object, NullLogger<GitHubClientService>.Instance);

        var pr = await client.CreatePullRequestAsync("octocat", "hello-world", new CreateGitHubPullRequestRequest
        {
            Title = "New Feature PR",
            Head = "feature-branch",
            Base = "main",
            Body = "PR description",
            Draft = true
        });

        Assert.Equal(42, pr.Number);
        Assert.Equal("New Feature PR", pr.Title);
        Assert.True(pr.Draft);
        Assert.False(pr.Merged);
    }

    [Fact]
    public async Task ListIssuesAsync_FiltersOutPullRequests()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                Assert.Contains("/repos/octocat/hello-world/issues", req.RequestUri?.ToString());

                // Return a mix of real issues and PRs (GitHub returns both on /issues)
                var rawItems = new object[]
                {
                    new
                    {
                        number = 101,
                        title = "Pure Issue",
                        body = "An issue description",
                        html_url = "https://github.com/octocat/hello-world/issues/101",
                        state = "open",
                        labels = new[] { new { name = "bug" } },
                        assignees = new[] { new { login = "octocat" } },
                        created_at = DateTimeOffset.UtcNow,
                        pull_request = (object?)null // Not a PR
                    },
                    new
                    {
                        number = 102,
                        title = "Pull Request Masquerading as Issue",
                        body = "PR disguised as issue",
                        html_url = "https://github.com/octocat/hello-world/pull/102",
                        state = "open",
                        labels = Array.Empty<object>(),
                        assignees = Array.Empty<object>(),
                        created_at = DateTimeOffset.UtcNow,
                        pull_request = new { url = "https://api.github.com/repos/octocat/hello-world/pulls/102" } // IS a PR
                    }
                };

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(rawItems))
                };
            }
        };

        var authMock = new Mock<IGitHubDeviceAuthService>();
        authMock.Setup(a => a.GetValidAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("ghu_valid_token");

        var client = new GitHubClientService(new HttpClient(handler), authMock.Object, NullLogger<GitHubClientService>.Instance);

        var issues = await client.ListIssuesAsync("octocat", "hello-world", new GitHubIssueFilter());

        // The PR must be filtered out; only the pure issue should remain
        Assert.Single(issues);
        Assert.Equal(101, issues[0].Number);
        Assert.Equal("Pure Issue", issues[0].Title);
        Assert.Contains("bug", issues[0].Labels);
    }

    [Fact]
    public async Task AddIssueCommentAsync_SendsPostAndReturnsParsedComment()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal("https://api.github.com/repos/octocat/hello-world/issues/5/comments", req.RequestUri?.ToString());

                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        id = 99991L,
                        body = "Automated milestone update comment",
                        html_url = "https://github.com/octocat/hello-world/issues/5#issuecomment-99991",
                        created_at = DateTimeOffset.UtcNow,
                        user = new { login = "nightcodeforge-bot" }
                    }))
                };
            }
        };

        var authMock = new Mock<IGitHubDeviceAuthService>();
        authMock.Setup(a => a.GetValidAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("ghu_valid_token");

        var client = new GitHubClientService(new HttpClient(handler), authMock.Object, NullLogger<GitHubClientService>.Instance);

        var comment = await client.AddIssueCommentAsync("octocat", "hello-world", 5, "Automated milestone update comment");

        Assert.Equal(99991L, comment.Id);
        Assert.Equal("Automated milestone update comment", comment.Body);
        Assert.Equal("nightcodeforge-bot", comment.UserLogin);
    }

    [Fact]
    public async Task GetRepositoryAsync_ParsesRepositoryAndPermissions()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                Assert.Equal("https://api.github.com/repos/octocat/hello-world", req.RequestUri?.ToString());

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        id = 1296269L,
                        name = "hello-world",
                        full_name = "octocat/hello-world",
                        owner = new { login = "octocat" },
                        @private = false,
                        html_url = "https://github.com/octocat/hello-world",
                        default_branch = "main",
                        permissions = new
                        {
                            admin = true,
                            push = true,
                            pull = true
                        }
                    }))
                };
            }
        };

        var authMock = new Mock<IGitHubDeviceAuthService>();
        authMock.Setup(a => a.GetValidAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("ghu_valid_token");

        var client = new GitHubClientService(new HttpClient(handler), authMock.Object, NullLogger<GitHubClientService>.Instance);

        var repo = await client.GetRepositoryAsync("octocat", "hello-world");

        Assert.Equal("hello-world", repo.Name);
        Assert.Equal("octocat/hello-world", repo.FullName);
        Assert.Equal("octocat", repo.Owner);
        Assert.NotNull(repo.Permissions);
        Assert.True(repo.Permissions.Admin);
        Assert.True(repo.Permissions.Push);
        Assert.True(repo.Permissions.Pull);
    }

    [Fact]
    public async Task SendWithAuthAsync_On401Unauthorized_TriggersReactiveRefreshAndRetries()
    {
        var requestCount = 0;
        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                requestCount++;
                if (requestCount == 1)
                {
                    Assert.Equal("ghu_expired_token", req.Headers.Authorization?.Parameter);
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("{\"message\":\"Bad credentials\"}")
                    };
                }

                Assert.Equal("ghu_refreshed_token", req.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        id = 1L,
                        name = "repo",
                        full_name = "octocat/repo",
                        owner = new { login = "octocat" },
                        @private = false,
                        html_url = "https://github.com/octocat/repo",
                        default_branch = "main"
                    }))
                };
            }
        };

        var authMock = new Mock<IGitHubDeviceAuthService>();
        var currentToken = "ghu_expired_token";
        authMock.Setup(a => a.GetValidAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => currentToken);
        authMock.Setup(a => a.RefreshAccessTokenAsync(It.IsAny<CancellationToken>()))
            .Callback(() => currentToken = "ghu_refreshed_token")
            .ReturnsAsync(new GitHubOAuthTokenPayload { AccessToken = "ghu_refreshed_token" });

        var client = new GitHubClientService(new HttpClient(handler), authMock.Object, NullLogger<GitHubClientService>.Instance);

        var repo = await client.GetRepositoryAsync("octocat", "repo");

        Assert.NotNull(repo);
        Assert.Equal(2, requestCount);
        authMock.Verify(a => a.RefreshAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendWithAuthAsync_WhenApiReturnsForbidden_ThrowsGitHubApiException()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"message\":\"Resource not accessible by integration\"}")
            }
        };

        var authMock = new Mock<IGitHubDeviceAuthService>();
        authMock.Setup(a => a.GetValidAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("ghu_token");

        var client = new GitHubClientService(new HttpClient(handler), authMock.Object, NullLogger<GitHubClientService>.Instance);

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetRepositoryAsync("octocat", "private-repo"));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("Resource not accessible", ex.ResponseBody);
    }

    [Fact]
    public async Task GetPullRequestAsync_WhenNullableJsonFieldsAreNull_ParsesSafelyWithoutException()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                    "number": 7,
                    "title": "PR with nulls",
                    "body": null,
                    "html_url": "https://github.com/octocat/repo/pull/7",
                    "state": "closed",
                    "draft": null,
                    "merged": true,
                    "merge_commit_sha": "abc1234",
                    "created_at": null,
                    "closed_at": null,
                    "merged_at": "2026-11-11T12:00:00Z"
                }
                """)
            }
        };

        var authMock = new Mock<IGitHubDeviceAuthService>();
        authMock.Setup(a => a.GetValidAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("ghu_token");

        var client = new GitHubClientService(new HttpClient(handler), authMock.Object, NullLogger<GitHubClientService>.Instance);

        var pr = await client.GetPullRequestAsync("octocat", "repo", 7);

        Assert.Equal(7, pr.Number);
        Assert.False(pr.Draft);
        Assert.True(pr.Merged);
        Assert.Equal("abc1234", pr.MergeCommitSha);
        Assert.Null(pr.ClosedAt);
        Assert.NotNull(pr.MergedAt);
    }

    [Fact]
    public async Task GetRepositoryAsync_WhenOwnerOrPermissionsAreNullInJson_ParsesSafely()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                    "id": 99,
                    "name": "repo",
                    "full_name": "octocat/repo",
                    "owner": null,
                    "private": null,
                    "permissions": null
                }
                """)
            }
        };

        var authMock = new Mock<IGitHubDeviceAuthService>();
        authMock.Setup(a => a.GetValidAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("ghu_token");

        var client = new GitHubClientService(new HttpClient(handler), authMock.Object, NullLogger<GitHubClientService>.Instance);

        var repo = await client.GetRepositoryAsync("octocat", "repo");

        Assert.Equal(99, repo.Id);
        Assert.Equal(string.Empty, repo.Owner);
        Assert.False(repo.Private);
        Assert.Null(repo.Permissions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetIssueAsync_WithInvalidIssueNumber_ThrowsArgumentOutOfRangeException(int invalidIssueNumber)
    {
        var client = new GitHubClientService(new HttpClient(), Mock.Of<IGitHubDeviceAuthService>(), NullLogger<GitHubClientService>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetIssueAsync("octocat", "repo", invalidIssueNumber));
    }

    [Fact]
    public async Task ListAccessibleRepositoriesAsync_QueriesUserReposEndpoint_AndParsesList()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                Assert.Equal("https://api.github.com/user/repos?sort=updated&per_page=100", req.RequestUri?.ToString());

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new[]
                    {
                        new
                        {
                            id = 10L,
                            name = "NightCodeForge",
                            full_name = "jetelain/NightCodeForge",
                            owner = new { login = "jetelain" },
                            @private = true,
                            html_url = "https://github.com/jetelain/NightCodeForge",
                            default_branch = "main"
                        },
                        new
                        {
                            id = 20L,
                            name = "shared-lib",
                            full_name = "acme-org/shared-lib",
                            owner = new { login = "acme-org" },
                            @private = false,
                            html_url = "https://github.com/acme-org/shared-lib",
                            default_branch = "develop"
                        }
                    }))
                };
            }
        };

        var authMock = new Mock<IGitHubDeviceAuthService>();
        authMock.Setup(a => a.GetValidAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("ghu_token");

        var client = new GitHubClientService(new HttpClient(handler), authMock.Object, NullLogger<GitHubClientService>.Instance);

        var repos = await client.ListAccessibleRepositoriesAsync();

        Assert.Equal(2, repos.Count);
        Assert.Equal("jetelain", repos[0].Owner);
        Assert.Equal("NightCodeForge", repos[0].Name);
        Assert.True(repos[0].Private);
        Assert.Equal("acme-org", repos[1].Owner);
        Assert.Equal("shared-lib", repos[1].Name);
        Assert.False(repos[1].Private);
    }
}

