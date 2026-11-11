using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class GitHubSettingsUiServiceTests
{
    [Fact]
    public async Task LoadSettingsAsync_ReturnsWorkspaceSettings()
    {
        var repoMock = new Mock<IWorkspaceRepository>();
        var workspace = new Workspace
        {
            Id = 1,
            Name = "Test WS",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                RepositoryOwner = "octocat",
                RepositoryName = "repo"
            }
        };

        repoMock.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

        var service = new GitHubSettingsUiService(
            repoMock.Object,
            Mock.Of<IGitHubDeviceAuthService>(),
            Mock.Of<IGitHubClientService>(),
            NullLogger<GitHubSettingsUiService>.Instance);

        var settings = await service.LoadSettingsAsync(1);

        Assert.NotNull(settings);
        Assert.True(settings.Enabled);
        Assert.Equal("octocat", settings.RepositoryOwner);
    }

    [Fact]
    public async Task SaveSettingsAsync_PersistsUpdatedSettings()
    {
        var repoMock = new Mock<IWorkspaceRepository>();
        var workspace = new Workspace
        {
            Id = 2,
            Name = "Test WS 2"
        };

        repoMock.Setup(r => r.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

        var service = new GitHubSettingsUiService(
            repoMock.Object,
            Mock.Of<IGitHubDeviceAuthService>(),
            Mock.Of<IGitHubClientService>(),
            NullLogger<GitHubSettingsUiService>.Instance);

        var newSettings = new GitHubIntegrationSettings
        {
            Enabled = true,
            RepositoryOwner = "my-org",
            RepositoryName = "my-repo",
            PullRequestDeliveryEnabled = true
        };

        await service.SaveSettingsAsync(2, newSettings);

        repoMock.Verify(r => r.UpdateAsync(It.Is<Workspace>(w => w.GitHubSettings == newSettings), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetConnectionStateAsync_WhenConnected_ReturnsUser()
    {
        var authMock = new Mock<IGitHubDeviceAuthService>();
        authMock.Setup(a => a.IsConnectedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        authMock.Setup(a => a.GetAuthenticatedUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubUser { Id = 10, Login = "octocat" });

        var service = new GitHubSettingsUiService(
            Mock.Of<IWorkspaceRepository>(),
            authMock.Object,
            Mock.Of<IGitHubClientService>(),
            NullLogger<GitHubSettingsUiService>.Instance);

        var state = await service.GetConnectionStateAsync();

        Assert.True(state.IsConnected);
        Assert.NotNull(state.AuthenticatedUser);
        Assert.Equal("octocat", state.AuthenticatedUser.Login);
    }

    [Fact]
    public async Task TestRepositoryAccessAsync_WhenSuccessful_ReturnsRepository()
    {
        var clientMock = new Mock<IGitHubClientService>();
        clientMock.Setup(c => c.GetRepositoryAsync("octocat", "hello-world", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubRepository
            {
                Id = 123,
                Name = "hello-world",
                FullName = "octocat/hello-world",
                Owner = "octocat",
                Permissions = new GitHubRepositoryPermissions { Push = true, Admin = true }
            });

        var service = new GitHubSettingsUiService(
            Mock.Of<IWorkspaceRepository>(),
            Mock.Of<IGitHubDeviceAuthService>(),
            clientMock.Object,
            NullLogger<GitHubSettingsUiService>.Instance);

        var result = await service.TestRepositoryAccessAsync("octocat", "hello-world");

        Assert.True(result.Success);
        Assert.NotNull(result.Repository);
        Assert.True(result.HasPushAccess);
        Assert.True(result.HasAdminAccess);
    }

    [Fact]
    public async Task TestRepositoryAccessAsync_WhenUnauthorized_ReturnsFriendlyErrorMessage()
    {
        var clientMock = new Mock<IGitHubClientService>();
        clientMock.Setup(c => c.GetRepositoryAsync("octocat", "hello-world", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubAuthenticationException("Token expired"));

        var service = new GitHubSettingsUiService(
            Mock.Of<IWorkspaceRepository>(),
            Mock.Of<IGitHubDeviceAuthService>(),
            clientMock.Object,
            NullLogger<GitHubSettingsUiService>.Instance);

        var result = await service.TestRepositoryAccessAsync("octocat", "hello-world");

        Assert.False(result.Success);
        Assert.Contains("Authentication error", result.ErrorMessage);
    }

    [Fact]
    public async Task TestRepositoryAccessAsync_WhenNotFoundOrNotInstalled_ReturnsActionableGuidance()
    {
        var clientMock = new Mock<IGitHubClientService>();
        clientMock.Setup(c => c.GetRepositoryAsync("octocat", "hello-world", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException(HttpStatusCode.NotFound, "Not Found", "{\"message\":\"Not Found\"}"));

        var service = new GitHubSettingsUiService(
            Mock.Of<IWorkspaceRepository>(),
            Mock.Of<IGitHubDeviceAuthService>(),
            clientMock.Object,
            NullLogger<GitHubSettingsUiService>.Instance);

        var result = await service.TestRepositoryAccessAsync("octocat", "hello-world");

        Assert.False(result.Success);
        Assert.Contains("NightCodeForge GitHub App is installed", result.ErrorMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1441)]
    public async Task SaveSettingsAsync_WithOutOfRangePollingInterval_ThrowsArgumentOutOfRangeException(int invalidInterval)
    {
        var service = new GitHubSettingsUiService(
            Mock.Of<IWorkspaceRepository>(),
            Mock.Of<IGitHubDeviceAuthService>(),
            Mock.Of<IGitHubClientService>(),
            NullLogger<GitHubSettingsUiService>.Instance);

        var settings = new GitHubIntegrationSettings
        {
            PollingIntervalMinutes = invalidInterval
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SaveSettingsAsync(1, settings));
    }

    [Fact]
    public async Task SaveSettingsAsync_TrimsWhitespaceAndDefaultsBlankClientId()
    {
        var repoMock = new Mock<IWorkspaceRepository>();
        var workspace = new Workspace { Id = 1, Name = "WS" };
        repoMock.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

        var service = new GitHubSettingsUiService(
            repoMock.Object,
            Mock.Of<IGitHubDeviceAuthService>(),
            Mock.Of<IGitHubClientService>(),
            NullLogger<GitHubSettingsUiService>.Instance);

        var settings = new GitHubIntegrationSettings
        {
            ClientId = "   ",
            RepositoryOwner = "  octocat  ",
            RepositoryName = "  hello-world  ",
            IssueFilterLabel = "   "
        };

        await service.SaveSettingsAsync(1, settings);

        Assert.Equal(GitHubIntegrationSettings.DefaultClientId, settings.ClientId);
        Assert.Equal("octocat", settings.RepositoryOwner);
        Assert.Equal("hello-world", settings.RepositoryName);
        Assert.Null(settings.IssueFilterLabel);
    }

    [Fact]
    public void ShouldDisplayAvatar_WhenEnableContentSecurityPolicyIsTrue_ReturnsFalse()
    {
        var service = new GitHubSettingsUiService(
            Mock.Of<IWorkspaceRepository>(),
            Mock.Of<IGitHubDeviceAuthService>(),
            Mock.Of<IGitHubClientService>(),
            NullLogger<GitHubSettingsUiService>.Instance);

        var user = new GitHubUser
        {
            Id = 1,
            Login = "octocat",
            AvatarUrl = "https://avatars.githubusercontent.com/u/1?v=4"
        };

        Assert.False(service.ShouldDisplayAvatar(user, new SecurityOptions { EnableContentSecurityPolicy = true }));
        Assert.True(service.ShouldDisplayAvatar(user, new SecurityOptions { EnableContentSecurityPolicy = false }));
        Assert.False(service.ShouldDisplayAvatar(new GitHubUser { Id = 1, Login = "octocat", AvatarUrl = null }, new SecurityOptions { EnableContentSecurityPolicy = false }));
    }

    [Fact]
    public async Task LoadAccessibleRepositoriesAsync_ReturnsDiscoveredRepositories()
    {
        var clientMock = new Mock<IGitHubClientService>();
        clientMock.Setup(c => c.ListAccessibleRepositoriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GitHubRepository>
            {
                new() { Id = 1, Owner = "octocat", Name = "repo-b" },
                new() { Id = 2, Owner = "octocat", Name = "repo-a" },
                new() { Id = 3, Owner = "my-org", Name = "org-repo" }
            });

        var service = new GitHubSettingsUiService(
            Mock.Of<IWorkspaceRepository>(),
            Mock.Of<IGitHubDeviceAuthService>(),
            clientMock.Object,
            NullLogger<GitHubSettingsUiService>.Instance);

        var repos = await service.LoadAccessibleRepositoriesAsync();

        Assert.Equal(3, repos.Count);

        var owners = service.GetAvailableOwners(repos, authenticatedUserLogin: "octocat", currentOwner: null);
        Assert.Equal(new[] { "octocat", "my-org" }, owners);

        var octocatRepos = service.GetRepositoriesForOwner(repos, "octocat");
        Assert.Equal(new[] { "repo-a", "repo-b" }, octocatRepos.Select(r => r.Name).ToArray());

        var orgReposWithSavedCustom = service.GetRepositoriesForOwner(repos, "my-org", currentRepoName: "custom-saved-repo");
        Assert.Equal(new[] { "custom-saved-repo", "org-repo" }, orgReposWithSavedCustom.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void GetAppInstallationUrl_ReturnsConnectionsApplicationUrlWithClientId()
    {
        var service = new GitHubSettingsUiService(
            Mock.Of<IWorkspaceRepository>(),
            Mock.Of<IGitHubDeviceAuthService>(),
            Mock.Of<IGitHubClientService>(),
            NullLogger<GitHubSettingsUiService>.Instance);

        Assert.Equal("https://github.com/settings/connections/applications/Iv23lienpd2ZF3rkKyfg", service.GetAppInstallationUrl());
        Assert.Equal("https://github.com/settings/connections/applications/Iv23lienpd2ZF3rkKyfg", service.GetAppInstallationUrl("   "));
        Assert.Equal("https://github.com/settings/connections/applications/Iv23customAppId", service.GetAppInstallationUrl("Iv23customAppId"));
    }
}

