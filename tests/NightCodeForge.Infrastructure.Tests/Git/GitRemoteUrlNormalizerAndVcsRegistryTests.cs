using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Services;
using NightCodeForge.Infrastructure.Git;
using NightCodeForge.Infrastructure.GitHub;

namespace NightCodeForge.Infrastructure.Tests.Git;

public class GitRemoteUrlNormalizerAndVcsRegistryTests
{
    [Theory]
    [InlineData("https://github.com/Owner/Repo.git", "github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo", "github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo/", "github.com/owner/repo")]
    [InlineData("https://x-access-token:secret@github.com/Owner/Repo.git", "github.com/owner/repo")]
    [InlineData("git@github.com:Owner/Repo.git", "github.com/owner/repo")]
    [InlineData("ssh://git@github.com/Owner/Repo.git", "github.com/owner/repo")]
    [InlineData("https://gitlab.example.com/Team/Subgroup/Service.git", "gitlab.example.com/team/subgroup/service")]
    public void Normalize_ProducesCanonicalHostAndPath(string rawUrl, string expected)
    {
        Assert.Equal(expected, GitRemoteUrlNormalizer.Normalize(rawUrl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-valid-url")]
    [InlineData(@"C:\Users\Dev\source\repos\app")]
    [InlineData("D:/repos/my-app")]
    [InlineData(@"\\server\share\repo")]
    [InlineData("/home/user/repo")]
    public void Normalize_InvalidOrBlankUrl_ReturnsNull(string? rawUrl)
    {
        Assert.Null(GitRemoteUrlNormalizer.Normalize(rawUrl));
    }

    [Fact]
    public void AreSameRepository_MatchesEquivalentHttpsAndSshUrls()
    {
        Assert.True(GitRemoteUrlNormalizer.AreSameRepository(
            "git@github.com:Octocat/Hello-World.git",
            "https://github.com/octocat/hello-world"));

        Assert.False(GitRemoteUrlNormalizer.AreSameRepository(
            "https://github.com/octocat/hello-world.git",
            "https://github.com/octocat/other-repo.git"));
    }

    [Fact]
    public async Task VcsHostingProviderRegistry_ResolvesPrimaryAuthenticatedProvider_EvenIfEarlierProviderThrows()
    {
        var failingProvider = new Mock<IVcsHostingProvider>();
        failingProvider.SetupGet(p => p.ProviderType).Returns(VcsHostingProviderType.GitLab);
        failingProvider.Setup(p => p.IsAuthenticatedAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Keyring unreachable"));

        var workingProvider = new Mock<IVcsHostingProvider>();
        workingProvider.SetupGet(p => p.ProviderType).Returns(VcsHostingProviderType.GitHub);
        workingProvider.Setup(p => p.IsAuthenticatedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var registry = new VcsHostingProviderRegistry(
            [failingProvider.Object, workingProvider.Object],
            NullLogger<VcsHostingProviderRegistry>.Instance);

        Assert.Equal(2, registry.GetAvailableProviders().Count);
        Assert.Same(workingProvider.Object, registry.GetProvider(VcsHostingProviderType.GitHub));
        Assert.Null(registry.GetProvider(VcsHostingProviderType.Bitbucket));

        var primary = await registry.GetPrimaryAuthenticatedProviderAsync();
        Assert.Same(workingProvider.Object, primary);
    }

    [Fact]
    public async Task GitHubVcsHostingProvider_MapsRepositories_AndTranslatesForbiddenCreateToClearException()
    {
        var clientMock = new Mock<IGitHubClientService>();
        var authMock = new Mock<IGitHubDeviceAuthService>();

        var updatedAt = new DateTimeOffset(2026, 11, 20, 8, 30, 0, TimeSpan.Zero);
        clientMock.Setup(c => c.ListAccessibleRepositoriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new GitHubRepository
                {
                    Id = 777,
                    Name = "hello-world",
                    FullName = "octocat/hello-world",
                    Owner = "octocat",
                    Description = "Sample repository",
                    Private = true,
                    DefaultBranch = "main",
                    HtmlUrl = "https://github.com/octocat/hello-world",
                    CloneUrl = "https://github.com/octocat/hello-world.git",
                    SshUrl = "git@github.com:octocat/hello-world.git",
                    UpdatedAt = updatedAt
                }
            ]);

        clientMock.Setup(c => c.CreateRepositoryAsync("blocked-repo", null, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException(HttpStatusCode.Forbidden, "Forbidden"));

        authMock.Setup(a => a.IsConnectedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        authMock.Setup(a => a.GetValidAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("ghu_secret_token");

        var provider = new GitHubVcsHostingProvider(
            clientMock.Object,
            authMock.Object,
            Options.Create(new SecurityOptions { AirGappedMode = false }),
            NullLogger<GitHubVcsHostingProvider>.Instance);

        var repos = await provider.ListRepositoriesAsync();
        var item = Assert.Single(repos);
        Assert.Equal("777", item.Id);
        Assert.Equal("octocat/hello-world", item.FullName);
        Assert.Equal("Sample repository", item.Description);
        Assert.True(item.IsPrivate);
        Assert.Equal("https://github.com/octocat/hello-world.git", item.HttpsCloneUrl);
        Assert.Equal("git@github.com:octocat/hello-world.git", item.SshCloneUrl);
        Assert.Equal(updatedAt, item.UpdatedAt);

        var authHeader = await provider.GetGitHttpAuthHeaderAsync();
        var expectedEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:ghu_secret_token"));
        Assert.Equal($"Authorization: Basic {expectedEncoded}", authHeader);

        var forbiddenEx = await Assert.ThrowsAsync<VcsHostingProviderException>(
            () => provider.CreateRepositoryAsync("blocked-repo", null, isPrivate: true));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenEx.StatusCode);
        Assert.Contains("Administration: Read & write", forbiddenEx.Message);
    }

    [Fact]
    public async Task GitHubVcsHostingProvider_RespectsAirGappedMode()
    {
        var clientMock = new Mock<IGitHubClientService>();
        var authMock = new Mock<IGitHubDeviceAuthService>();
        authMock.Setup(a => a.IsConnectedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var provider = new GitHubVcsHostingProvider(
            clientMock.Object,
            authMock.Object,
            Options.Create(new SecurityOptions { AirGappedMode = true }),
            NullLogger<GitHubVcsHostingProvider>.Instance);

        Assert.False(await provider.IsAuthenticatedAsync());
        Assert.Null(await provider.GetAuthenticatedUsernameAsync());
        Assert.Null(await provider.GetGitHttpAuthHeaderAsync());
        await Assert.ThrowsAsync<VcsHostingProviderException>(() => provider.ListRepositoriesAsync());
    }
}
