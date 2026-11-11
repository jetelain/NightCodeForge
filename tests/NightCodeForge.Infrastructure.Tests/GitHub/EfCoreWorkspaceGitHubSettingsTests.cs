using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.GitHub;

public class EfCoreWorkspaceGitHubSettingsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfCoreWorkspaceGitHubSettingsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task Workspace_GitHubSettings_PersistsAndLoadsCorrectly()
    {
        using (var initContext = new AppDbContext(_options))
        {
            await initContext.EnsureDatabaseCreatedAndWalAsync();

            var workspace = new Workspace
            {
                Name = "GitHub Workspace",
                RepositoryPath = "/test/repo",
                DefaultBaseBranch = "main",
                GitHubSettings = new GitHubIntegrationSettings
                {
                    Enabled = true,
                    RepositoryOwner = "octocat",
                    RepositoryName = "hello-world",
                    IssueTriageEnabled = true,
                    IssueTriageScope = GitHubIssueTriageScope.AssignedToMeOnly,
                    IssueFilterLabel = "triage",
                    PollingIntervalMinutes = 30,
                    TriagedIssueNumbers = new List<int> { 1, 2, 3 },
                    PullRequestDeliveryEnabled = true,
                    CreateAsDraft = true,
                    CloseTaskOnPrMerge = false,
                    TaskMirroringEnabled = true,
                    CreateGitHubIssuesForLocalTasks = true,
                    AutoAssignIssueOnStart = true,
                    SyncStatusLabels = true,
                    PostMilestoneComments = true
                }
            };

            initContext.Workspaces.Add(workspace);
            await initContext.SaveChangesAsync();
        }

        using (var readContext = new AppDbContext(_options))
        {
            var loaded = await readContext.Workspaces.FirstOrDefaultAsync(w => w.Name == "GitHub Workspace");

            Assert.NotNull(loaded);
            Assert.NotNull(loaded.GitHubSettings);
            Assert.True(loaded.GitHubSettings.Enabled);
            Assert.Equal("octocat", loaded.GitHubSettings.RepositoryOwner);
            Assert.Equal("hello-world", loaded.GitHubSettings.RepositoryName);
            Assert.True(loaded.GitHubSettings.IssueTriageEnabled);
            Assert.Equal(GitHubIssueTriageScope.AssignedToMeOnly, loaded.GitHubSettings.IssueTriageScope);
            Assert.Equal("triage", loaded.GitHubSettings.IssueFilterLabel);
            Assert.Equal(30, loaded.GitHubSettings.PollingIntervalMinutes);
            Assert.Equal(new[] { 1, 2, 3 }, loaded.GitHubSettings.TriagedIssueNumbers);
            Assert.True(loaded.GitHubSettings.PullRequestDeliveryEnabled);
            Assert.True(loaded.GitHubSettings.CreateAsDraft);
            Assert.False(loaded.GitHubSettings.CloseTaskOnPrMerge);
            Assert.True(loaded.GitHubSettings.TaskMirroringEnabled);
        }
    }

    [Fact]
    public async Task EfCoreWorkspaceRepository_UpdateAsync_UpdatesGitHubSettingsCorrectly()
    {
        var factoryMock = new Mock<IDbContextFactory<AppDbContext>>();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(_options));

        int workspaceId;
        using (var initContext = new AppDbContext(_options))
        {
            await initContext.EnsureDatabaseCreatedAndWalAsync();

            var workspace = new Workspace
            {
                Name = "Repository Update Test",
                RepositoryPath = "/repo/update",
                DefaultBaseBranch = "main",
                GitHubSettings = new GitHubIntegrationSettings
                {
                    Enabled = false
                }
            };

            initContext.Workspaces.Add(workspace);
            await initContext.SaveChangesAsync();
            workspaceId = workspace.Id;
        }

        var repo = new EfCoreWorkspaceRepository(factoryMock.Object);
        var existing = await repo.GetByIdAsync(workspaceId);
        Assert.NotNull(existing);

        existing.GitHubSettings = new GitHubIntegrationSettings
        {
            Enabled = true,
            RepositoryOwner = "updated-owner",
            RepositoryName = "updated-repo",
            PullRequestDeliveryEnabled = true
        };

        await repo.UpdateAsync(existing);

        var reloaded = await repo.GetByIdAsync(workspaceId);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.GitHubSettings.Enabled);
        Assert.Equal("updated-owner", reloaded.GitHubSettings.RepositoryOwner);
        Assert.Equal("updated-repo", reloaded.GitHubSettings.RepositoryName);
        Assert.True(reloaded.GitHubSettings.PullRequestDeliveryEnabled);
    }
}

