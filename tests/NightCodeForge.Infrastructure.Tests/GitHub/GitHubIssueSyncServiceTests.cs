using System.Collections.Concurrent;
using Hangfire;
using Hangfire.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.BackgroundJobs;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;
using NightCodeForge.Infrastructure.GitHub;

namespace NightCodeForge.Infrastructure.Tests.GitHub;

public sealed class GitHubIssueSyncServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteConnection _sqliteConnection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public GitHubIssueSyncServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ncf-gh-sync-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _sqliteConnection = new SqliteConnection("DataSource=:memory:");
        _sqliteConnection.Open();

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_sqliteConnection)
            .Options;

        using var context = new AppDbContext(_dbOptions);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sqliteConnection.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SyncOpenIssuesAsync_WhenAirGappedModeEnabled_SkipsPolling()
    {
        var harness = CreateUnitHarness(airGappedMode: true);
        harness.WorkspaceRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateWorkspace(1)]);

        await harness.Service.SyncOpenIssuesAsync();

        harness.GitHubClientMock.Verify(
            c => c.ListIssuesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.TriageServiceMock.Verify(
            t => t.TriageExternalReportAsync(It.IsAny<Workspace>(), It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SyncOpenIssuesAsync_WhenOperationsPaused_SkipsPolling()
    {
        var harness = CreateUnitHarness(isPaused: true);
        harness.WorkspaceRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateWorkspace(2)]);

        await harness.Service.SyncOpenIssuesAsync();

        harness.GitHubClientMock.Verify(
            c => c.ListIssuesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SyncOpenIssuesAsync_FiltersOutPullRequestsAndNonOpenIssues()
    {
        var workspace = CreateWorkspace(3);
        var harness = CreateUnitHarness();
        harness.WorkspaceRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([workspace]);
        harness.TaskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspace.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());
        harness.GitHubClientMock.Setup(c => c.ListIssuesAsync("octo-org", "app", It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new GitHubIssue { Number = 1, Title = "PR disguised as issue", State = "open", IsPullRequest = true },
                new GitHubIssue { Number = 2, Title = "Closed bug", State = "closed", IsPullRequest = false },
                new GitHubIssue { Number = 3, Title = "Valid open bug", Body = "Details", State = "open", IsPullRequest = false }
            ]);
        harness.GitHubClientMock.Setup(c => c.ListIssueCommentsAsync("octo-org", "app", 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GitHubIssueComment>());
        harness.TriageServiceMock.Setup(t => t.TriageExternalReportAsync(workspace, It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());

        await harness.Service.SyncOpenIssuesAsync();

        harness.TriageServiceMock.Verify(
            t => t.TriageExternalReportAsync(
                workspace,
                It.Is<TriageReportRequest>(r => r.ExternalReferenceId == "3"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        harness.TriageServiceMock.Verify(
            t => t.TriageExternalReportAsync(It.IsAny<Workspace>(), It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(GitHubIssueTriageScope.AssignedToMeOrUnassigned, new[] { 10, 11 })]
    [InlineData(GitHubIssueTriageScope.AssignedToMeOnly, new[] { 11 })]
    [InlineData(GitHubIssueTriageScope.AllOpenIssues, new[] { 10, 11, 12 })]
    public async Task SyncOpenIssuesAsync_AppliesAssigneeScopeFiltering(
        GitHubIssueTriageScope scope,
        int[] expectedTriagedIssueNumbers)
    {
        var workspace = CreateWorkspace(4, settings => settings.IssueTriageScope = scope);
        var harness = CreateUnitHarness(authenticatedLogin: "octocat");
        harness.WorkspaceRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([workspace]);
        harness.TaskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspace.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());
        harness.GitHubClientMock.Setup(c => c.ListIssuesAsync("octo-org", "app", It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new GitHubIssue { Number = 10, Title = "Unassigned", State = "open", Assignees = [] },
                new GitHubIssue { Number = 11, Title = "Mine", State = "open", Assignees = ["OctoCat"] },
                new GitHubIssue { Number = 12, Title = "Teammate", State = "open", Assignees = ["teammate"] }
            ]);
        harness.GitHubClientMock.Setup(c => c.ListIssueCommentsAsync("octo-org", "app", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GitHubIssueComment>());

        var triagedRefs = new List<string?>();
        harness.TriageServiceMock.Setup(t => t.TriageExternalReportAsync(workspace, It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Workspace, TriageReportRequest, CancellationToken>((_, req, _) => triagedRefs.Add(req.ExternalReferenceId))
            .ReturnsAsync(Array.Empty<ForgeTask>());

        await harness.Service.SyncOpenIssuesAsync();

        var actualNumbers = triagedRefs.Select(r => int.Parse(r!)).OrderBy(n => n).ToArray();
        Assert.Equal(expectedTriagedIssueNumbers, actualNumbers);
    }

    [Fact]
    public async Task SyncOpenIssuesAsync_SkipsExistingForgeTaskAndDeduplicatesZeroProposalIssueWatermark()
    {
        var workspace = CreateWorkspace(5, settings =>
        {
            settings.SyncStatusLabels = false;
            settings.PostMilestoneComments = false;
        });

        var harness = CreateUnitHarness();
        harness.WorkspaceRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([workspace]);
        harness.TaskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspace.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ForgeTask
                {
                    Id = 99,
                    WorkspaceId = workspace.Id,
                    Title = "Existing task",
                    Origin = TaskOrigin.GitHubIssue,
                    ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
                    ExternalReferenceId = "20"
                }
            ]);

        var issues = new[]
        {
            new GitHubIssue { Number = 20, Title = "Already tracked in DB", State = "open" },
            new GitHubIssue { Number = 21, Title = "Non-actionable feature", Body = "Invalid report", State = "open" }
        };

        harness.GitHubClientMock.Setup(c => c.ListIssuesAsync("octo-org", "app", It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(issues);
        harness.GitHubClientMock.Setup(c => c.ListIssueCommentsAsync("octo-org", "app", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GitHubIssueComment>());
        harness.TriageServiceMock.Setup(t => t.TriageExternalReportAsync(workspace, It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());

        // First poll cycle triages #21 (0 proposals) and skips #20 (existing ForgeTask)
        await harness.Service.SyncOpenIssuesAsync();

        Assert.Contains(21, workspace.GitHubSettings.TriagedIssueNumbers);
        harness.WorkspaceRepoMock.Verify(r => r.UpdateAsync(workspace, It.IsAny<CancellationToken>()), Times.Once);
        harness.GitHubClientMock.Verify(
            c => c.UpdateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<UpdateGitHubIssueRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.GitHubClientMock.Verify(
            c => c.AddIssueCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Second poll cycle must not re-triage #21 thanks to TriagedIssueNumbers local watermark
        await harness.Service.SyncOpenIssuesAsync();

        harness.TriageServiceMock.Verify(
            t => t.TriageExternalReportAsync(It.IsAny<Workspace>(), It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SyncOpenIssuesAsync_SkipsIssuesMarkedByNcfLabelsOrCommentMarkerAcrossInstances()
    {
        var workspace = CreateWorkspace(6);
        var harness = CreateUnitHarness();
        harness.WorkspaceRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([workspace]);
        harness.TaskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspace.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());
        harness.GitHubClientMock.Setup(c => c.ListIssuesAsync("octo-org", "app", It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new GitHubIssue { Number = 30, Title = "Triaged by teammate", State = "open", Labels = [GitHubSyncConstants.LabelTriaged] },
                new GitHubIssue { Number = 31, Title = "In progress by teammate", State = "open", Labels = [GitHubSyncConstants.LabelInProgress] },
                new GitHubIssue { Number = 32, Title = "Body marker", State = "open", Body = $"Some issue\n{GitHubSyncConstants.TriagedCommentMarker}" },
                new GitHubIssue { Number = 33, Title = "Comment marker", State = "open", Labels = ["bug"] },
                new GitHubIssue { Number = 34, Title = "Untriaged new bug", State = "open", Labels = ["bug"] }
            ]);
        harness.GitHubClientMock.Setup(c => c.ListIssueCommentsAsync("octo-org", "app", 33, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new GitHubIssueComment { Id = 1, Body = $"{GitHubSyncConstants.TriagedCommentMarker}\nHandled by @teammate" }
            ]);
        harness.GitHubClientMock.Setup(c => c.ListIssueCommentsAsync("octo-org", "app", 34, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GitHubIssueComment>());
        harness.TriageServiceMock.Setup(t => t.TriageExternalReportAsync(workspace, It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ForgeTask { Id = 500, WorkspaceId = workspace.Id, Title = "Fix new bug" }]);

        await harness.Service.SyncOpenIssuesAsync();

        harness.TriageServiceMock.Verify(
            t => t.TriageExternalReportAsync(
                workspace,
                It.Is<TriageReportRequest>(r => r.ExternalReferenceId == "34"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        harness.TriageServiceMock.Verify(
            t => t.TriageExternalReportAsync(It.IsAny<Workspace>(), It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SyncOpenIssuesAsync_DispatchesProvenanceAndWritesTriagedLabelAndAttributedComment()
    {
        var workspace = CreateWorkspace(7, settings =>
        {
            settings.SyncStatusLabels = true;
            settings.PostMilestoneComments = true;
        });

        var harness = CreateUnitHarness(authenticatedLogin: "julien-dev");
        harness.WorkspaceRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([workspace]);
        harness.TaskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspace.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());
        harness.GitHubClientMock.Setup(c => c.ListIssuesAsync("octo-org", "app", It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new GitHubIssue
                {
                    Number = 55,
                    Title = "Race condition in sync",
                    Body = "Occurs during concurrent runs",
                    HtmlUrl = "https://github.com/octo-org/app/issues/55",
                    State = "open",
                    Labels = ["bug", "priority:high"]
                }
            ]);
        harness.GitHubClientMock.Setup(c => c.ListIssueCommentsAsync("octo-org", "app", 55, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GitHubIssueComment>());
        harness.TriageServiceMock.Setup(t => t.TriageExternalReportAsync(workspace, It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ForgeTask { Id = 701, WorkspaceId = workspace.Id, Title = "Fix race" }]);

        await harness.Service.SyncOpenIssuesAsync();

        harness.TriageServiceMock.Verify(
            t => t.TriageExternalReportAsync(
                workspace,
                It.Is<TriageReportRequest>(r =>
                    r.Origin == TaskOrigin.GitHubIssue
                    && r.ExternalSource == GitHubSyncConstants.ExternalSourceGitHub
                    && r.ExternalReferenceId == "55"
                    && r.BaseBranch == "main"
                    && r.Title.Contains("#55")
                    && r.Description.Contains("Occurs during concurrent runs")),
                It.IsAny<CancellationToken>()),
            Times.Once);

        harness.GitHubClientMock.Verify(
            c => c.UpdateIssueAsync(
                "octo-org",
                "app",
                55,
                It.Is<UpdateGitHubIssueRequest>(u =>
                    u.Labels != null
                    && u.Labels.Contains("bug")
                    && u.Labels.Contains("priority:high")
                    && u.Labels.Contains(GitHubSyncConstants.LabelTriaged)),
                It.IsAny<CancellationToken>()),
            Times.Once);

        harness.GitHubClientMock.Verify(
            c => c.AddIssueCommentAsync(
                "octo-org",
                "app",
                55,
                It.Is<string>(body =>
                    body.Contains(GitHubSyncConstants.TriagedCommentMarker)
                    && body.Contains("@julien-dev")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SyncWorkspaceIssuesAsync_SerializesConcurrentExecutionsForSameWorkspace()
    {
        var workspace = CreateWorkspace(8);
        var harness = CreateUnitHarness();
        harness.WorkspaceRepoMock.Setup(r => r.GetByIdAsync(workspace.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workspace);
        harness.TaskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspace.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());
        harness.GitHubClientMock.Setup(c => c.ListIssuesAsync("octo-org", "app", It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GitHubIssue { Number = 88, Title = "Concurrent issue", State = "open" }]);
        harness.GitHubClientMock.Setup(c => c.ListIssueCommentsAsync("octo-org", "app", 88, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GitHubIssueComment>());

        var activeTriageCount = 0;
        var maxConcurrent = 0;
        var enteredGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.TriageServiceMock.Setup(t => t.TriageExternalReportAsync(workspace, It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (Workspace _, TriageReportRequest _, CancellationToken _) =>
            {
                var current = Interlocked.Increment(ref activeTriageCount);
                maxConcurrent = Math.Max(maxConcurrent, current);
                enteredGate.TrySetResult();
                await releaseGate.Task;
                Interlocked.Decrement(ref activeTriageCount);
                return (IReadOnlyList<ForgeTask>)Array.Empty<ForgeTask>();
            });

        var run1 = harness.Service.SyncWorkspaceIssuesAsync(workspace.Id);
        await enteredGate.Task;
        var run2 = harness.Service.SyncWorkspaceIssuesAsync(workspace.Id);

        await Task.Delay(50);
        Assert.False(run2.IsCompleted);

        releaseGate.SetResult();
        await Task.WhenAll(run1, run2);

        Assert.Equal(1, maxConcurrent);
        harness.TriageServiceMock.Verify(
            t => t.TriageExternalReportAsync(It.IsAny<Workspace>(), It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(1, "* * * * *")]
    [InlineData(5, "*/5 * * * *")]
    [InlineData(15, "*/15 * * * *")]
    [InlineData(60, "0 * * * *")]
    [InlineData(90, "0 */2 * * *")]
    [InlineData(120, "0 */2 * * *")]
    [InlineData(1440, "0 0 * * *")]
    public void BuildCronExpression_ProducesValidCron(int pollingIntervalMinutes, string expectedCron)
    {
        Assert.Equal(expectedCron, GitHubIssueSyncService.BuildCronExpression(pollingIntervalMinutes));
    }

    [Fact]
    public async Task SynchronizeAllRecurringJobsAsync_RegistersEnabledAndRemovesDisabledWorkspaces()
    {
        var enabledWorkspace = CreateWorkspace(10, settings => settings.PollingIntervalMinutes = 15);
        var disabledWorkspace = CreateWorkspace(11, settings => settings.IssueTriageEnabled = false);

        var managerMock = new Mock<IRecurringJobManager>();

        await GitHubIssueSyncService.SynchronizeAllRecurringJobsAsync(
            managerMock.Object,
            [enabledWorkspace, disabledWorkspace]);

        managerMock.Verify(
            m => m.AddOrUpdate(
                GitHubIssueSyncService.GetWorkspaceRecurringJobId(10),
                It.Is<Job>(j =>
                    j.Type == typeof(IGitHubIssueSyncService)
                    && j.Method.Name == nameof(IGitHubIssueSyncService.SyncWorkspaceIssuesAsync)
                    && j.Queue == HangfireQueues.Default),
                "*/15 * * * *",
                It.IsAny<RecurringJobOptions>()),
            Times.Once);

        managerMock.Verify(
            m => m.RemoveIfExists(GitHubIssueSyncService.GetWorkspaceRecurringJobId(11)),
            Times.Once);
    }

    [Fact]
    public async Task SyncWorkspaceIssuesAsync_SqliteIntegration_PersistsSuggestedTaskMirrorsMarkdownAndRoundTripsWatermark()
    {
        var repoPath = Path.Combine(_tempDir, "repo-integration");
        Directory.CreateDirectory(repoPath);

        var factory = new TestDbContextFactory(_dbOptions);
        var workspaceRepo = new EfCoreWorkspaceRepository(factory);
        var taskRepo = new EfCoreTaskRepository(factory);

        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceRepository>(workspaceRepo);
        services.AddSingleton<ITaskRepository>(taskRepo);
        using var serviceProvider = services.BuildServiceProvider();

        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        var markdownSync = new TaskMarkdownSyncService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            pathResolverMock.Object,
            NullLogger<TaskMarkdownSyncService>.Instance);

        var workspace = await workspaceRepo.CreateAsync(new Workspace
        {
            Name = "IntegrationWorkspace",
            RepositoryPath = repoPath,
            DefaultBaseBranch = "main",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                IssueTriageEnabled = true,
                RepositoryOwner = "octo-org",
                RepositoryName = "app",
                IssueTriageScope = GitHubIssueTriageScope.AllOpenIssues,
                SyncStatusLabels = false,
                PostMilestoneComments = false
            }
        });

        var gitHubClientMock = new Mock<IGitHubClientService>();
        gitHubClientMock.Setup(c => c.ListIssuesAsync("octo-org", "app", It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new GitHubIssue { Number = 101, Title = "Actionable bug", Body = "Steps to reproduce", State = "open" },
                new GitHubIssue { Number = 102, Title = "Duplicate question", Body = "Not a bug", State = "open" }
            ]);
        gitHubClientMock.Setup(c => c.ListIssueCommentsAsync("octo-org", "app", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GitHubIssueComment>());

        var tokenStoreMock = new Mock<IGitHubTokenStore>();
        tokenStoreMock.Setup(t => t.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubOAuthTokenPayload
            {
                AccessToken = "gho_test",
                AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(4),
                AuthenticatedUserLogin = "octocat"
            });

        var triageServiceMock = new Mock<ITriageService>();
        triageServiceMock.Setup(t => t.TriageExternalReportAsync(
                It.IsAny<Workspace>(),
                It.IsAny<TriageReportRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (Workspace ws, TriageReportRequest req, CancellationToken ct) =>
            {
                if (req.ExternalReferenceId == "101")
                {
                    var created = await taskRepo.CreateWithBaselineAsync(
                        new ForgeTask
                        {
                            WorkspaceId = ws.Id,
                            Title = "Fix actionable bug from #101",
                            Description = req.Description,
                            BaseBranch = req.BaseBranch ?? "main",
                            Status = TaskOverallStatus.Suggested,
                            Origin = req.Origin,
                            ExternalSource = req.ExternalSource,
                            ExternalReferenceId = req.ExternalReferenceId
                        },
                        dependsOn: null,
                        createdBy: "GitHubTriage",
                        reason: "Triage proposed task from GitHub issue",
                        cancellationToken: ct);

                    await markdownSync.SyncTaskToDiskAsync(created, ws.RepositoryPath, cancellationToken: ct);
                    return (IReadOnlyList<ForgeTask>)[created];
                }

                return (IReadOnlyList<ForgeTask>)Array.Empty<ForgeTask>();
            });

        var opsMock = new Mock<IOperationsControlService>();
        opsMock.SetupGet(o => o.IsPaused).Returns(false);

        var syncService = new GitHubIssueSyncService(
            workspaceRepo,
            taskRepo,
            gitHubClientMock.Object,
            tokenStoreMock.Object,
            Mock.Of<IGitHubDeviceAuthService>(),
            triageServiceMock.Object,
            opsMock.Object,
            Options.Create(new SecurityOptions { AirGappedMode = false }),
            NullLogger<GitHubIssueSyncService>.Instance);

        // Cycle 1: #101 creates a Suggested task + markdown mirror, #102 yields 0 proposals
        await syncService.SyncWorkspaceIssuesAsync(workspace.Id);

        var persistedTasks = await taskRepo.GetByWorkspaceIdAsync(workspace.Id);
        var suggestedTask = Assert.Single(persistedTasks);
        Assert.Equal(TaskOverallStatus.Suggested, suggestedTask.Status);
        Assert.Equal(TaskOrigin.GitHubIssue, suggestedTask.Origin);
        Assert.Equal(GitHubSyncConstants.ExternalSourceGitHub, suggestedTask.ExternalSource);
        Assert.Equal("101", suggestedTask.ExternalReferenceId);

        var docsTasksDir = Path.Combine(repoPath, "docs", "tasks");
        var markdownFiles = Directory.GetFiles(docsTasksDir, "*.md");
        var mdFile = Assert.Single(markdownFiles);
        var (frontmatter, _) = markdownSync.ParseMarkdown(await File.ReadAllTextAsync(mdFile), mdFile);
        Assert.Equal("github_issue", frontmatter.Origin);
        Assert.Equal(GitHubSyncConstants.ExternalSourceGitHub, frontmatter.ExternalSource);
        Assert.Equal("101", frontmatter.ExternalReferenceId);

        var reloadedWorkspace = await workspaceRepo.GetByIdAsync(workspace.Id);
        Assert.NotNull(reloadedWorkspace);
        Assert.Contains(101, reloadedWorkspace!.GitHubSettings.TriagedIssueNumbers);
        Assert.Contains(102, reloadedWorkspace.GitHubSettings.TriagedIssueNumbers);

        triageServiceMock.Verify(
            t => t.TriageExternalReportAsync(It.IsAny<Workspace>(), It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        // Cycle 2: neither #101 nor #102 is re-triaged after SQLite JSON round-trip
        await syncService.SyncWorkspaceIssuesAsync(workspace.Id);

        triageServiceMock.Verify(
            t => t.TriageExternalReportAsync(It.IsAny<Workspace>(), It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task SyncOpenIssuesAsync_WhenTriageThrows_DoesNotRecordWatermarkOrAcknowledgeOnGitHubAndRetriesNextPoll()
    {
        var workspace = CreateWorkspace(12);
        var harness = CreateUnitHarness();
        harness.WorkspaceRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([workspace]);
        harness.TaskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspace.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());
        harness.GitHubClientMock.Setup(c => c.ListIssuesAsync("octo-org", "app", It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GitHubIssue { Number = 40, Title = "Transient failure issue", State = "open" }]);
        harness.GitHubClientMock.Setup(c => c.ListIssueCommentsAsync("octo-org", "app", 40, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GitHubIssueComment>());

        var attempt = 0;
        harness.TriageServiceMock.Setup(t => t.TriageExternalReportAsync(workspace, It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()))
            .Returns((Workspace _, TriageReportRequest _, CancellationToken _) =>
            {
                attempt++;
                if (attempt == 1)
                {
                    throw new InvalidOperationException("Untrusted intake triage failed (Failed): LLM timeout");
                }

                return Task.FromResult<IReadOnlyList<ForgeTask>>(
                    [new ForgeTask { Id = 801, WorkspaceId = workspace.Id, Title = "Fix issue #40" }]);
            });

        // Cycle 1: triage fails, so nothing is persisted or acknowledged on GitHub
        await harness.Service.SyncOpenIssuesAsync();

        Assert.DoesNotContain(40, workspace.GitHubSettings.TriagedIssueNumbers);
        harness.WorkspaceRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Workspace>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.GitHubClientMock.Verify(
            c => c.UpdateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<UpdateGitHubIssueRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.GitHubClientMock.Verify(
            c => c.AddIssueCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Cycle 2: retried and succeeds
        await harness.Service.SyncOpenIssuesAsync();

        Assert.Contains(40, workspace.GitHubSettings.TriagedIssueNumbers);
        harness.TriageServiceMock.Verify(
            t => t.TriageExternalReportAsync(workspace, It.Is<TriageReportRequest>(r => r.ExternalReferenceId == "40"), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        harness.WorkspaceRepoMock.Verify(r => r.UpdateAsync(workspace, It.IsAny<CancellationToken>()), Times.Once);
        harness.GitHubClientMock.Verify(
            c => c.UpdateIssueAsync("octo-org", "app", 40, It.IsAny<UpdateGitHubIssueRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.GitHubClientMock.Verify(
            c => c.AddIssueCommentAsync("octo-org", "app", 40, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SyncOpenIssuesAsync_WhenOneWorkspaceFails_ContinuesSyncingSubsequentWorkspaces()
    {
        var brokenWorkspace = CreateWorkspace(13, s =>
        {
            s.RepositoryOwner = "octo-org";
            s.RepositoryName = "broken-repo";
        });
        var healthyWorkspace = CreateWorkspace(14, s =>
        {
            s.RepositoryOwner = "octo-org";
            s.RepositoryName = "healthy-repo";
        });

        var harness = CreateUnitHarness();
        harness.WorkspaceRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([brokenWorkspace, healthyWorkspace]);
        harness.TaskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(healthyWorkspace.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());

        harness.GitHubClientMock.Setup(c => c.ListIssuesAsync("octo-org", "broken-repo", It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException(System.Net.HttpStatusCode.NotFound, "Not Found", "{}"));
        harness.GitHubClientMock.Setup(c => c.ListIssuesAsync("octo-org", "healthy-repo", It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GitHubIssue { Number = 77, Title = "Healthy workspace bug", State = "open" }]);
        harness.GitHubClientMock.Setup(c => c.ListIssueCommentsAsync("octo-org", "healthy-repo", 77, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GitHubIssueComment>());
        harness.TriageServiceMock.Setup(t => t.TriageExternalReportAsync(healthyWorkspace, It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ForgeTask { Id = 901, WorkspaceId = healthyWorkspace.Id, Title = "Fix #77" }]);

        await harness.Service.SyncOpenIssuesAsync();

        harness.TriageServiceMock.Verify(
            t => t.TriageExternalReportAsync(
                healthyWorkspace,
                It.Is<TriageReportRequest>(r => r.ExternalReferenceId == "77"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SyncOpenIssuesAsync_WhenIssueHasTriagedComment_PersistsWatermarkAndSkipsCommentFetchOnNextPoll()
    {
        var workspace = CreateWorkspace(15, s =>
        {
            s.SyncStatusLabels = false;
            s.PostMilestoneComments = false;
        });

        var harness = CreateUnitHarness();
        harness.WorkspaceRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([workspace]);
        harness.TaskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspace.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());
        harness.GitHubClientMock.Setup(c => c.ListIssuesAsync("octo-org", "app", It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GitHubIssue { Number = 60, Title = "Already commented by teammate", State = "open" }]);
        harness.GitHubClientMock.Setup(c => c.ListIssueCommentsAsync("octo-org", "app", 60, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GitHubIssueComment { Id = 12, Body = $"{GitHubSyncConstants.TriagedCommentMarker}\nHandled." }]);

        await harness.Service.SyncOpenIssuesAsync();
        await harness.Service.SyncOpenIssuesAsync();

        Assert.Contains(60, workspace.GitHubSettings.TriagedIssueNumbers);
        harness.WorkspaceRepoMock.Verify(r => r.UpdateAsync(workspace, It.IsAny<CancellationToken>()), Times.Once);
        harness.GitHubClientMock.Verify(
            c => c.ListIssueCommentsAsync("octo-org", "app", 60, It.IsAny<CancellationToken>()),
            Times.Once);
        harness.TriageServiceMock.Verify(
            t => t.TriageExternalReportAsync(It.IsAny<Workspace>(), It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SyncWorkspaceIssuesAsync_WhenSettingsModifiedDuringTriage_PreservesUpdatedSettingsWhenPersistingWatermark()
    {
        var initialWorkspace = CreateWorkspace(16, s => s.PollingIntervalMinutes = 15);
        var concurrentlyUpdatedWorkspace = CreateWorkspace(16, s => s.PollingIntervalMinutes = 45);

        var harness = CreateUnitHarness();
        harness.WorkspaceRepoMock.SetupSequence(r => r.GetByIdAsync(16, It.IsAny<CancellationToken>()))
            .ReturnsAsync(initialWorkspace)
            .ReturnsAsync(concurrentlyUpdatedWorkspace);
        harness.TaskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(16, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());
        harness.GitHubClientMock.Setup(c => c.ListIssuesAsync("octo-org", "app", It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GitHubIssue { Number = 61, Title = "Concurrent edit test", State = "open" }]);
        harness.GitHubClientMock.Setup(c => c.ListIssueCommentsAsync("octo-org", "app", 61, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GitHubIssueComment>());
        harness.TriageServiceMock.Setup(t => t.TriageExternalReportAsync(initialWorkspace, It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());

        await harness.Service.SyncWorkspaceIssuesAsync(16);

        harness.WorkspaceRepoMock.Verify(
            r => r.UpdateAsync(
                It.Is<Workspace>(w =>
                    w.GitHubSettings.PollingIntervalMinutes == 45
                    && w.GitHubSettings.TriagedIssueNumbers.Contains(61)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SyncOpenIssuesAsync_TriagesIssuesInAscendingNumberOrder()
    {
        var workspace = CreateWorkspace(17);
        var harness = CreateUnitHarness();
        harness.WorkspaceRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([workspace]);
        harness.TaskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspace.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());
        harness.GitHubClientMock.Setup(c => c.ListIssuesAsync("octo-org", "app", It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new GitHubIssue { Number = 30, Title = "Newest", State = "open" },
                new GitHubIssue { Number = 10, Title = "Oldest", State = "open" },
                new GitHubIssue { Number = 20, Title = "Middle", State = "open" }
            ]);
        harness.GitHubClientMock.Setup(c => c.ListIssueCommentsAsync("octo-org", "app", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GitHubIssueComment>());

        var dispatchOrder = new List<string?>();
        harness.TriageServiceMock.Setup(t => t.TriageExternalReportAsync(workspace, It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Workspace, TriageReportRequest, CancellationToken>((_, req, _) => dispatchOrder.Add(req.ExternalReferenceId))
            .ReturnsAsync(Array.Empty<ForgeTask>());

        await harness.Service.SyncOpenIssuesAsync();

        Assert.Equal(["10", "20", "30"], dispatchOrder);
    }

    [Fact]
    public async Task SyncOpenIssuesAsync_WhenLabelsChangeDuringTriage_MergesFreshLabelsAndSkipsIfNcfLabelAppliedConcurrently()
    {
        var workspace = CreateWorkspace(18, s =>
        {
            s.SyncStatusLabels = true;
            s.PostMilestoneComments = false;
        });
        var harness = CreateUnitHarness();
        harness.WorkspaceRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([workspace]);
        harness.TaskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspace.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());
        harness.GitHubClientMock.Setup(c => c.ListIssuesAsync("octo-org", "app", It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new GitHubIssue { Number = 50, Title = "Label added concurrently", State = "open", Labels = ["bug"] },
                new GitHubIssue { Number = 51, Title = "Claimed concurrently", State = "open", Labels = ["bug"] }
            ]);
        harness.GitHubClientMock.Setup(c => c.ListIssueCommentsAsync("octo-org", "app", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GitHubIssueComment>());
        harness.TriageServiceMock.Setup(t => t.TriageExternalReportAsync(workspace, It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());

        harness.GitHubClientMock.Setup(c => c.GetIssueAsync("octo-org", "app", 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue { Number = 50, State = "open", Labels = ["bug", "priority:high"] });
        harness.GitHubClientMock.Setup(c => c.GetIssueAsync("octo-org", "app", 51, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue { Number = 51, State = "open", Labels = ["bug", GitHubSyncConstants.LabelInProgress] });

        await harness.Service.SyncOpenIssuesAsync();

        harness.GitHubClientMock.Verify(
            c => c.UpdateIssueAsync(
                "octo-org",
                "app",
                50,
                It.Is<UpdateGitHubIssueRequest>(u =>
                    u.Labels != null
                    && u.Labels.Count == 3
                    && u.Labels.Contains("bug")
                    && u.Labels.Contains("priority:high")
                    && u.Labels.Contains(GitHubSyncConstants.LabelTriaged)),
                It.IsAny<CancellationToken>()),
            Times.Once);

        harness.GitHubClientMock.Verify(
            c => c.UpdateIssueAsync(
                "octo-org",
                "app",
                51,
                It.IsAny<UpdateGitHubIssueRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SyncOpenIssuesAsync_WhenTriagedCommentPostedDuringTriage_SkipsDuplicateCommentWriteback()
    {
        var workspace = CreateWorkspace(19, s =>
        {
            s.SyncStatusLabels = false;
            s.PostMilestoneComments = true;
        });
        var harness = CreateUnitHarness();
        harness.WorkspaceRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([workspace]);
        harness.TaskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspace.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());
        harness.GitHubClientMock.Setup(c => c.ListIssuesAsync("octo-org", "app", It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GitHubIssue { Number = 65, Title = "Concurrent comment race", State = "open" }]);

        harness.GitHubClientMock.SetupSequence(c => c.ListIssueCommentsAsync("octo-org", "app", 65, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GitHubIssueComment>())
            .ReturnsAsync([new GitHubIssueComment { Id = 200, Body = $"{GitHubSyncConstants.TriagedCommentMarker}\nTriaged by peer instance." }]);

        harness.TriageServiceMock.Setup(t => t.TriageExternalReportAsync(workspace, It.IsAny<TriageReportRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ForgeTask { Id = 123, WorkspaceId = workspace.Id, Title = "Fix #65" }]);

        await harness.Service.SyncOpenIssuesAsync();

        harness.GitHubClientMock.Verify(
            c => c.AddIssueCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EfCoreWorkspaceRepository_UpdateAsync_PreservesAccumulatedTriagedIssueNumbersFromStaleSnapshot()
    {
        var factory = new TestDbContextFactory(_dbOptions);
        var workspaceRepo = new EfCoreWorkspaceRepository(factory);

        var created = await workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Concurrent WS",
            RepositoryPath = Path.Combine(_tempDir, "concurrent-ws"),
            DefaultBaseBranch = "main",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                IssueTriageEnabled = true,
                RepositoryOwner = "octo-org",
                RepositoryName = "app",
                TriagedIssueNumbers = [1]
            }
        });

        var staleSnapshot = await workspaceRepo.GetByIdAsync(created.Id);
        Assert.NotNull(staleSnapshot);

        var backgroundSynced = await workspaceRepo.GetByIdAsync(created.Id);
        Assert.NotNull(backgroundSynced);
        backgroundSynced!.GitHubSettings.TriagedIssueNumbers = [1, 2, 3];
        await workspaceRepo.UpdateAsync(backgroundSynced);

        staleSnapshot!.Name = "Renamed Concurrent WS";
        await workspaceRepo.UpdateAsync(staleSnapshot);

        var reloaded = await workspaceRepo.GetByIdAsync(created.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Renamed Concurrent WS", reloaded!.Name);
        Assert.Equal([1, 2, 3], reloaded.GitHubSettings.TriagedIssueNumbers.OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task InMemoryWorkspaceRepository_UpdateAsync_PreservesAccumulatedTriagedIssueNumbersFromStaleSnapshot()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var initial = await workspaceRepo.CreateAsync(new Workspace
        {
            Id = 50,
            Name = "InMemory WS",
            RepositoryPath = _tempDir,
            DefaultBaseBranch = "main",
            GitHubSettings = new GitHubIntegrationSettings
            {
                TriagedIssueNumbers = [1, 2, 3]
            }
        });

        var staleSnapshot = new Workspace
        {
            Id = initial.Id,
            Name = "Updated InMemory WS",
            RepositoryPath = _tempDir,
            DefaultBaseBranch = "main",
            GitHubSettings = new GitHubIntegrationSettings
            {
                TriagedIssueNumbers = [1, 4]
            }
        };

        await workspaceRepo.UpdateAsync(staleSnapshot);

        var reloaded = await workspaceRepo.GetByIdAsync(initial.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Updated InMemory WS", reloaded!.Name);
        Assert.Equal([1, 2, 3, 4], reloaded.GitHubSettings.TriagedIssueNumbers.OrderBy(n => n).ToArray());
    }

    private static Workspace CreateWorkspace(int id, Action<GitHubIntegrationSettings>? configure = null)
    {
        var settings = new GitHubIntegrationSettings
        {
            Enabled = true,
            IssueTriageEnabled = true,
            RepositoryOwner = "octo-org",
            RepositoryName = "app",
            IssueTriageScope = GitHubIssueTriageScope.AllOpenIssues,
            SyncStatusLabels = true,
            PostMilestoneComments = true
        };
        configure?.Invoke(settings);

        return new Workspace
        {
            Id = id,
            Name = $"Workspace {id}",
            RepositoryPath = Path.Combine(Path.GetTempPath(), $"ws-{id}"),
            DefaultBaseBranch = "main",
            GitHubSettings = settings
        };
    }

    private static UnitHarness CreateUnitHarness(
        bool airGappedMode = false,
        bool isPaused = false,
        string? authenticatedLogin = "octocat")
    {
        var workspaceRepoMock = new Mock<IWorkspaceRepository>();
        var taskRepoMock = new Mock<ITaskRepository>();
        var gitHubClientMock = new Mock<IGitHubClientService>();
        var tokenStoreMock = new Mock<IGitHubTokenStore>();
        var authServiceMock = new Mock<IGitHubDeviceAuthService>();
        var triageServiceMock = new Mock<ITriageService>();
        var opsControlMock = new Mock<IOperationsControlService>();

        opsControlMock.SetupGet(o => o.IsPaused).Returns(isPaused);

        if (!string.IsNullOrWhiteSpace(authenticatedLogin))
        {
            tokenStoreMock.Setup(t => t.GetTokenAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubOAuthTokenPayload
                {
                    AccessToken = "gho_test",
                    AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(4),
                    AuthenticatedUserLogin = authenticatedLogin
                });
        }

        var service = new GitHubIssueSyncService(
            workspaceRepoMock.Object,
            taskRepoMock.Object,
            gitHubClientMock.Object,
            tokenStoreMock.Object,
            authServiceMock.Object,
            triageServiceMock.Object,
            opsControlMock.Object,
            Options.Create(new SecurityOptions { AirGappedMode = airGappedMode }),
            NullLogger<GitHubIssueSyncService>.Instance);

        return new UnitHarness(
            service,
            workspaceRepoMock,
            taskRepoMock,
            gitHubClientMock,
            tokenStoreMock,
            authServiceMock,
            triageServiceMock,
            opsControlMock);
    }

    private sealed record UnitHarness(
        GitHubIssueSyncService Service,
        Mock<IWorkspaceRepository> WorkspaceRepoMock,
        Mock<ITaskRepository> TaskRepoMock,
        Mock<IGitHubClientService> GitHubClientMock,
        Mock<IGitHubTokenStore> TokenStoreMock,
        Mock<IGitHubDeviceAuthService> AuthServiceMock,
        Mock<ITriageService> TriageServiceMock,
        Mock<IOperationsControlService> OpsControlMock);

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
