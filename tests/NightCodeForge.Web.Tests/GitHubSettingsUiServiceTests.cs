using System.Net;
using System.Net.Http;
using Bunit;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Infrastructure.BackgroundJobs;
using NightCodeForge.Infrastructure.GitHub;
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

        var service = CreateService(workspaceRepository: repoMock.Object);

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

        var service = CreateService(workspaceRepository: repoMock.Object);

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

        var service = CreateService(authService: authMock.Object);

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

        var service = CreateService(clientService: clientMock.Object);

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

        var service = CreateService(clientService: clientMock.Object);

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

        var service = CreateService(clientService: clientMock.Object);

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
        var service = CreateService();

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

        var service = CreateService(workspaceRepository: repoMock.Object);

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
        var service = CreateService();

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

        var service = CreateService(clientService: clientMock.Object);

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
        var service = CreateService();

        Assert.Equal("https://github.com/settings/connections/applications/Iv23lienpd2ZF3rkKyfg", service.GetAppInstallationUrl());
        Assert.Equal("https://github.com/settings/connections/applications/Iv23lienpd2ZF3rkKyfg", service.GetAppInstallationUrl("   "));
        Assert.Equal("https://github.com/settings/connections/applications/Iv23customAppId", service.GetAppInstallationUrl("Iv23customAppId"));
    }

    [Fact]
    public async Task SaveSettingsAsync_WhenIssueTriageEnabled_RegistersOrUpdatesRecurringPollJob()
    {
        var repoMock = new Mock<IWorkspaceRepository>();
        var workspace = new Workspace { Id = 14, Name = "Polling WS" };
        repoMock.Setup(r => r.GetByIdAsync(14, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

        var recurringJobMock = new Mock<IRecurringJobManager>();
        var service = CreateService(workspaceRepository: repoMock.Object, recurringJobManager: recurringJobMock.Object);

        var settings = new GitHubIntegrationSettings
        {
            Enabled = true,
            IssueTriageEnabled = true,
            RepositoryOwner = "octo-org",
            RepositoryName = "app",
            PollingIntervalMinutes = 10
        };

        await service.SaveSettingsAsync(14, settings);

        recurringJobMock.Verify(
            m => m.AddOrUpdate(
                GitHubIssueSyncService.GetWorkspaceRecurringJobId(14),
                It.Is<Job>(j =>
                    j.Type == typeof(IGitHubIssueSyncService)
                    && j.Method.Name == nameof(IGitHubIssueSyncService.SyncWorkspaceIssuesAsync)
                    && j.Queue == HangfireQueues.Default),
                "*/10 * * * *",
                It.IsAny<RecurringJobOptions>()),
            Times.Once);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task SaveSettingsAsync_WhenIssueTriageOrIntegrationDisabled_RemovesRecurringPollJob(bool enabled, bool issueTriageEnabled)
    {
        var repoMock = new Mock<IWorkspaceRepository>();
        var workspace = new Workspace { Id = 15, Name = "Disabled Polling WS" };
        repoMock.Setup(r => r.GetByIdAsync(15, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

        var recurringJobMock = new Mock<IRecurringJobManager>();
        var service = CreateService(workspaceRepository: repoMock.Object, recurringJobManager: recurringJobMock.Object);

        var settings = new GitHubIntegrationSettings
        {
            Enabled = enabled,
            IssueTriageEnabled = issueTriageEnabled,
            RepositoryOwner = "octo-org",
            RepositoryName = "app",
            PollingIntervalMinutes = 5
        };

        await service.SaveSettingsAsync(15, settings);

        recurringJobMock.Verify(
            m => m.RemoveIfExists(GitHubIssueSyncService.GetWorkspaceRecurringJobId(15)),
            Times.Once);
        recurringJobMock.Verify(
            m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task SaveSettingsAsync_PreservesAndDeduplicatesExistingTriagedIssueNumbers()
    {
        var repoMock = new Mock<IWorkspaceRepository>();
        var workspace = new Workspace
        {
            Id = 16,
            Name = "Watermark WS",
            GitHubSettings = new GitHubIntegrationSettings
            {
                TriagedIssueNumbers = [10, 20]
            }
        };
        repoMock.Setup(r => r.GetByIdAsync(16, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

        var service = CreateService(workspaceRepository: repoMock.Object);
        var updatedSettings = new GitHubIntegrationSettings
        {
            Enabled = true,
            IssueTriageEnabled = true,
            RepositoryOwner = "octo-org",
            RepositoryName = "app",
            TriagedIssueNumbers = [20, 30]
        };

        await service.SaveSettingsAsync(16, updatedSettings);

        Assert.Equal([10, 20, 30], updatedSettings.TriagedIssueNumbers);
    }

    [Fact]
    public async Task SaveSettingsAsync_WhenPullRequestDeliveryAndCloseOnMergeEnabled_RegistersPrMergeRecurringJob()
    {
        var repoMock = new Mock<IWorkspaceRepository>();
        var workspace = new Workspace { Id = 17, Name = "PR Merge WS" };
        repoMock.Setup(r => r.GetByIdAsync(17, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

        var recurringJobMock = new Mock<IRecurringJobManager>();
        var service = CreateService(workspaceRepository: repoMock.Object, recurringJobManager: recurringJobMock.Object);

        var settings = new GitHubIntegrationSettings
        {
            Enabled = true,
            IssueTriageEnabled = false,
            PullRequestDeliveryEnabled = true,
            CloseTaskOnPrMerge = true,
            RepositoryOwner = "octo-org",
            RepositoryName = "app",
            PollingIntervalMinutes = 10
        };

        await service.SaveSettingsAsync(17, settings);

        recurringJobMock.Verify(
            m => m.AddOrUpdate(
                GitHubTaskMirrorService.GetWorkspacePrMergeRecurringJobId(17),
                It.Is<Job>(j =>
                    j.Type == typeof(ITaskOrchestrator)
                    && j.Method.Name == nameof(ITaskOrchestrator.SyncMergedPullRequestsAsync)
                    && j.Queue == HangfireQueues.Default),
                "*/10 * * * *",
                It.IsAny<RecurringJobOptions>()),
            Times.Once);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task SaveSettingsAsync_WhenPrMergePollingDisabled_RemovesPrMergeRecurringJob(
        bool enabled,
        bool pullRequestDeliveryEnabled,
        bool closeTaskOnPrMerge)
    {
        var repoMock = new Mock<IWorkspaceRepository>();
        var workspace = new Workspace { Id = 18, Name = "Disabled PR Merge WS" };
        repoMock.Setup(r => r.GetByIdAsync(18, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

        var recurringJobMock = new Mock<IRecurringJobManager>();
        var service = CreateService(workspaceRepository: repoMock.Object, recurringJobManager: recurringJobMock.Object);

        var settings = new GitHubIntegrationSettings
        {
            Enabled = enabled,
            IssueTriageEnabled = false,
            PullRequestDeliveryEnabled = pullRequestDeliveryEnabled,
            CloseTaskOnPrMerge = closeTaskOnPrMerge,
            RepositoryOwner = "octo-org",
            RepositoryName = "app",
            PollingIntervalMinutes = 10
        };

        await service.SaveSettingsAsync(18, settings);

        recurringJobMock.Verify(
            m => m.RemoveIfExists(GitHubTaskMirrorService.GetWorkspacePrMergeRecurringJobId(18)),
            Times.Once);
        recurringJobMock.Verify(
            m => m.AddOrUpdate(
                GitHubTaskMirrorService.GetWorkspacePrMergeRecurringJobId(18),
                It.IsAny<Job>(),
                It.IsAny<string>(),
                It.IsAny<RecurringJobOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task MirrorTaskOpenedSafeAsync_MirrorsOpenTaskAndSkipsNonOpenTask()
    {
        var mirrorMock = new Mock<IGitHubTaskMirrorService>();
        var wsRepoMock = new Mock<IWorkspaceRepository>();
        var workspace = new Workspace { Id = 21, Name = "Mirror WS" };
        wsRepoMock.Setup(r => r.GetByIdAsync(21, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

        var services = new ServiceCollection()
            .AddSingleton(mirrorMock.Object)
            .AddSingleton(wsRepoMock.Object)
            .BuildServiceProvider();

        var openTask = new ForgeTask { Id = 1, WorkspaceId = 21, Status = TaskOverallStatus.Open };
        var suggestedTask = new ForgeTask { Id = 2, WorkspaceId = 21, Status = TaskOverallStatus.Suggested };

        await GitHubPullRequestUiHelper.MirrorTaskOpenedSafeAsync(services, openTask, 21, NullLogger.Instance);
        await GitHubPullRequestUiHelper.MirrorTaskOpenedSafeAsync(services, suggestedTask, 21, NullLogger.Instance);

        mirrorMock.Verify(m => m.MirrorTaskOpenedAsync(openTask, workspace, It.IsAny<CancellationToken>()), Times.Once);
        mirrorMock.Verify(m => m.MirrorTaskOpenedAsync(suggestedTask, It.IsAny<Workspace>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MirrorTaskOpenedSafeAsync_SwallowsErrorsWithWarningAndPropagatesCancellation()
    {
        var mirrorMock = new Mock<IGitHubTaskMirrorService>();
        var loggerMock = new Mock<ILogger>();
        var workspace = new Workspace { Id = 22, Name = "Error WS" };
        var task = new ForgeTask { Id = 3, WorkspaceId = 22, Status = TaskOverallStatus.Open };

        var services = new ServiceCollection()
            .AddSingleton(mirrorMock.Object)
            .BuildServiceProvider();

        mirrorMock
            .Setup(m => m.MirrorTaskOpenedAsync(task, workspace, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Transient GitHub failure"));

        await GitHubPullRequestUiHelper.MirrorTaskOpenedSafeAsync(services, task, workspace, loggerMock.Object);

        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        mirrorMock
            .Setup(m => m.MirrorTaskOpenedAsync(task, workspace, cts.Token))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GitHubPullRequestUiHelper.MirrorTaskOpenedSafeAsync(services, task, workspace, loggerMock.Object, cts.Token));
    }

    [Fact]
    public async Task SaveSettingsAsync_WhenRepositoryChanges_ClearsTriagedIssueNumbers()
    {
        var repoMock = new Mock<IWorkspaceRepository>();
        var workspace = new Workspace
        {
            Id = 23,
            Name = "Repo Switch WS",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                RepositoryOwner = "old-org",
                RepositoryName = "old-repo",
                TriagedIssueNumbers = [10, 20]
            }
        };
        repoMock.Setup(r => r.GetByIdAsync(23, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

        var service = CreateService(workspaceRepository: repoMock.Object);
        var updatedSettings = new GitHubIntegrationSettings
        {
            Enabled = true,
            IssueTriageEnabled = true,
            RepositoryOwner = "new-org",
            RepositoryName = "new-repo",
            TriagedIssueNumbers = [20, 30]
        };

        await service.SaveSettingsAsync(23, updatedSettings);

        Assert.Empty(updatedSettings.TriagedIssueNumbers);
    }

    [Fact]
    public void RemoveWorkspaceRecurringJobs_RemovesBothIssueSyncAndPrMergeJobs()
    {
        var recurringJobMock = new Mock<IRecurringJobManager>();
        var service = CreateService(recurringJobManager: recurringJobMock.Object);

        service.RemoveWorkspaceRecurringJobs(25);

        recurringJobMock.Verify(
            m => m.RemoveIfExists(GitHubIssueSyncService.GetWorkspaceRecurringJobId(25)),
            Times.Once);
        recurringJobMock.Verify(
            m => m.RemoveIfExists(GitHubTaskMirrorService.GetWorkspacePrMergeRecurringJobId(25)),
            Times.Once);
    }

    private static GitHubSettingsUiService CreateService(
        IWorkspaceRepository? workspaceRepository = null,
        IGitHubDeviceAuthService? authService = null,
        IGitHubClientService? clientService = null,
        IRecurringJobManager? recurringJobManager = null)
    {
        return new GitHubSettingsUiService(
            workspaceRepository ?? Mock.Of<IWorkspaceRepository>(),
            authService ?? Mock.Of<IGitHubDeviceAuthService>(),
            clientService ?? Mock.Of<IGitHubClientService>(),
            recurringJobManager ?? Mock.Of<IRecurringJobManager>(),
            NullLogger<GitHubSettingsUiService>.Instance);
    }
}

public class GitHubSettingsTabTests : BunitContext
{
    [Fact]
    public async Task GitHubSettingsTab_EditingInputs_DoesNotMutateActiveWorkspaceSettingsBeforeSave()
    {
        var workspace = new Workspace
        {
            Id = 1,
            Name = "Tab WS",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = false,
                RepositoryOwner = "orig-owner",
                RepositoryName = "orig-repo"
            }
        };

        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(workspace);

        var authMock = new Mock<IGitHubDeviceAuthService>();
        authMock.Setup(a => a.IsConnectedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        authMock.Setup(a => a.GetAuthenticatedUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubUser { Id = 1, Login = "orig-owner" });

        var clientMock = new Mock<IGitHubClientService>();
        clientMock.Setup(c => c.ListAccessibleRepositoriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GitHubRepository>
            {
                new() { Id = 1, Owner = "orig-owner", Name = "orig-repo" },
                new() { Id = 2, Owner = "edited-owner", Name = "edited-repo" }
            });

        var workspaceState = new WorkspaceStateService();
        workspaceState.SetActiveWorkspace(workspace);
        var toastService = TestServices.CreateToastService();
        var uiService = new GitHubSettingsUiService(
            workspaceRepo,
            authMock.Object,
            clientMock.Object,
            Mock.Of<IRecurringJobManager>(),
            NullLogger<GitHubSettingsUiService>.Instance);

        Services.AddSingleton(uiService);
        Services.AddSingleton(workspaceState);
        Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new SecurityOptions()));
        Services.AddSingleton(toastService);
        Services.AddSingleton<Microsoft.Extensions.Logging.ILogger<NightCodeForge.Web.Components.Settings.GitHubSettingsTab>>(
            NullLogger<NightCodeForge.Web.Components.Settings.GitHubSettingsTab>.Instance);

        var cut = Render<NightCodeForge.Web.Components.Settings.GitHubSettingsTab>(p => p.Add(c => c.WorkspaceId, 1));

        cut.Find("#github-repo-owner").Change("edited-owner");
        cut.Find("#enableGitHub").Change(true);

        Assert.Equal("orig-owner", workspace.GitHubSettings.RepositoryOwner);
        Assert.False(workspace.GitHubSettings.Enabled);

        var saveBtn = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Save GitHub Settings", StringComparison.Ordinal));
        saveBtn.Click();

        Assert.Equal("edited-owner", workspaceState.ActiveWorkspace!.GitHubSettings!.RepositoryOwner);
        Assert.True(workspaceState.ActiveWorkspace.GitHubSettings.Enabled);

        // Editing again after save still does not mutate the saved ActiveWorkspace snapshot
        cut.Find("#github-repo-owner").Change("orig-owner");
        Assert.Equal("edited-owner", workspaceState.ActiveWorkspace.GitHubSettings.RepositoryOwner);
    }

    [Fact]
    public async Task GitHubSettingsTab_RefreshRepositoriesFailure_ShowsErrorToast()
    {
        var workspace = new Workspace
        {
            Id = 1,
            Name = "Refresh WS",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                RepositoryOwner = "octocat",
                RepositoryName = "demo"
            }
        };

        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(workspace);

        var authMock = new Mock<IGitHubDeviceAuthService>();
        authMock.Setup(a => a.IsConnectedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        authMock.Setup(a => a.GetAuthenticatedUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubUser { Id = 1, Login = "octocat" });

        var clientMock = new Mock<IGitHubClientService>();
        clientMock.SetupSequence(c => c.ListAccessibleRepositoriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GitHubRepository> { new() { Id = 1, Owner = "octocat", Name = "demo" } })
            .ThrowsAsync(new HttpRequestException("GitHub 503"));

        var workspaceState = new WorkspaceStateService();
        workspaceState.SetActiveWorkspace(workspace);
        var toastService = TestServices.CreateToastService();
        var uiService = new GitHubSettingsUiService(
            workspaceRepo,
            authMock.Object,
            clientMock.Object,
            Mock.Of<IRecurringJobManager>(),
            NullLogger<GitHubSettingsUiService>.Instance);

        Services.AddSingleton(uiService);
        Services.AddSingleton(workspaceState);
        Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new SecurityOptions()));
        Services.AddSingleton(toastService);
        Services.AddSingleton<Microsoft.Extensions.Logging.ILogger<NightCodeForge.Web.Components.Settings.GitHubSettingsTab>>(
            NullLogger<NightCodeForge.Web.Components.Settings.GitHubSettingsTab>.Instance);

        var cut = Render<NightCodeForge.Web.Components.Settings.GitHubSettingsTab>(p => p.Add(c => c.WorkspaceId, 1));
        Assert.Contains("demo", cut.Find("#github-repo-name").InnerHtml);

        var refreshBtn = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Refresh Repositories", StringComparison.Ordinal));
        refreshBtn.Click();

        Assert.Contains(toastService.Toasts, t => t.Title == "Repository Refresh Failed" && t.Level == ToastLevel.Error);
        Assert.DoesNotContain(toastService.Toasts, t => t.Title == "Repositories Refreshed");
        Assert.Contains("demo", cut.Find("#github-repo-name").InnerHtml);
    }
}

