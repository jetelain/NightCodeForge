using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;
using NightCodeForge.Infrastructure.GitHub;
using NightCodeForge.Infrastructure.Hubs;

namespace NightCodeForge.Infrastructure.Tests;

public class Step1238TriageInfrastructureTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public Step1238TriageInfrastructureTests()
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
    public async Task AppDbContext_ReconcilesGlobalAndWorkspaceScopedTriageProfiles_ToDefaultAgentToolsTriage()
    {
        await using var context = new AppDbContext(_options);
        await context.EnsureDatabaseCreatedAndWalAsync();

        var ws = new Workspace
        {
            Name = "Scoped Workspace",
            RepositoryPath = "/repos/scoped",
            DefaultBaseBranch = "main"
        };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        context.AgentProfiles.Add(new AgentProfile
        {
            Id = $"{ws.Id}:{StandardStageIds.Triage}",
            WorkspaceId = ws.Id,
            Name = "Workspace Scoped Triage",
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = "test-model",
            Mode = AgentMode.ReadOnly,
            SecurityTier = SandboxSecurityTier.RestrictedReadOnly,
            PermittedTools =
            [
                CanonicalToolNames.Read,
                CanonicalToolNames.WebSearch,
                CanonicalToolNames.Ask,
                CanonicalToolNames.SetTaskPriority,
                CanonicalToolNames.ReworkPriorities,
                CanonicalToolNames.Write
            ]
        });
        await context.SaveChangesAsync();

        await context.EnsureDefaultGlobalAgentProfilesAsync("test-model");

        var globalTriage = await context.AgentProfiles.FirstAsync(p => p.Id == StandardStageIds.Triage);
        var scopedTriage = await context.AgentProfiles.FirstAsync(p => p.Id == $"{ws.Id}:{StandardStageIds.Triage}");

        foreach (var profile in new[] { globalTriage, scopedTriage })
        {
            Assert.NotNull(profile.PermittedTools);
            foreach (var tool in CanonicalToolNames.TriageBacklogTools)
            {
                Assert.Contains(tool, profile.PermittedTools!);
            }

            Assert.Contains(CanonicalToolNames.Read, profile.PermittedTools!);
            Assert.DoesNotContain(CanonicalToolNames.WebSearch, profile.PermittedTools!);
            Assert.DoesNotContain(CanonicalToolNames.Ask, profile.PermittedTools!);
            Assert.DoesNotContain(CanonicalToolNames.SetTaskPriority, profile.PermittedTools!);
            Assert.DoesNotContain(CanonicalToolNames.ReworkPriorities, profile.PermittedTools!);
            Assert.DoesNotContain(CanonicalToolNames.Write, profile.PermittedTools!);
            Assert.DoesNotContain(CanonicalToolNames.Edit, profile.PermittedTools!);
        }
    }

    [Fact]
    public async Task SignalRActivityBridgeService_ClearsActiveTaskOnTriageCompleted_AndBroadcastsProposeTasksNotice()
    {
        var allClients = new Mock<IOrchestratorHubClient>();
        var groupClients = new Mock<IOrchestratorHubClient>();
        var clients = new Mock<IHubClients<IOrchestratorHubClient>>();
        clients.SetupGet(c => c.All).Returns(allClients.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupClients.Object);

        var hubContext = new Mock<IHubContext<OrchestratorHub, IOrchestratorHubClient>>();
        hubContext.SetupGet(h => h.Clients).Returns(clients.Object);

        var telemetry = new Mock<IOperationsTelemetryService>();
        var control = new Mock<IOperationsControlService>();
        var streamer = new Mock<IAgentActivityStreamer>();

        using var bridge = new SignalRActivityBridgeService(
            streamer.Object,
            hubContext.Object,
            telemetry.Object,
            control.Object,
            NullLogger<SignalRActivityBridgeService>.Instance);

        await bridge.DispatchActivityEventAsync(new AgentActivityEvent(
            SessionId: "session-triage",
            TaskId: 77,
            StageId: StandardStageIds.Triage,
            Kind: AgentActivityKind.StageCompleted,
            Content: "Triage completed",
            Timestamp: DateTime.UtcNow));

        telemetry.Verify(t => t.ClearActiveTask(77), Times.Once);

        await bridge.DispatchActivityEventAsync(new AgentActivityEvent(
            SessionId: "triage-backlog-1",
            TaskId: 88,
            StageId: StandardStageIds.Triage,
            Kind: AgentActivityKind.Notice,
            Content: "Triage agent proposed suggested task #88",
            Timestamp: DateTime.UtcNow,
            Metadata: new Dictionary<string, object?> { [AgentActivityMetadataKeys.ToolName] = CanonicalToolNames.ProposeTasks }));

        allClients.Verify(
            c => c.ReceiveBacklogMutation(88, CanonicalToolNames.ProposeTasks, "Triage agent proposed suggested task #88"),
            Times.Once);
    }

    [Fact]
    public async Task GitHubIssueSyncService_IgnoresTriageSessionTask_WhenCheckingExistingIssueDeduplication()
    {
        var workspace = new Workspace
        {
            Id = 5,
            Name = "GitHub Sync Repo",
            RepositoryPath = "/repos/gh",
            DefaultBaseBranch = "main",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                RepositoryOwner = "acme",
                RepositoryName = "demo",
                IssueTriageEnabled = true,
                IssueTriageScope = GitHubIssueTriageScope.AllOpenIssues
            }
        };

        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(workspace);

        var taskRepo = new InMemoryTaskRepository();
        // Seed an existing Triage execution session referencing GitHub issue #123.
        await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Triage: GitHub Issue #123",
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Triage,
            Origin = TaskOrigin.GitHubIssue,
            ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
            ExternalReferenceId = "123",
            BaseBranch = "main"
        });

        var gitHubClient = new Mock<IGitHubClientService>();
        gitHubClient.Setup(c => c.ListIssuesAsync("acme", "demo", It.IsAny<GitHubIssueFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new GitHubIssue
                {
                    Number = 123,
                    Title = "Bug #123",
                    Body = "Reproduction steps",
                    State = "open",
                    IsPullRequest = false,
                    HtmlUrl = "https://github.com/acme/demo/issues/123",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]);

        var tokenStore = new Mock<IGitHubTokenStore>();
        tokenStore.Setup(s => s.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubOAuthTokenPayload
            {
                AccessToken = "tok",
                AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
            });

        var authService = new Mock<IGitHubDeviceAuthService>();
        authService.Setup(a => a.IsConnectedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        authService.Setup(a => a.GetValidAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("tok");

        var triageService = new Mock<ITriageService>();
        triageService.Setup(t => t.TriageExternalReportAsync(
                It.IsAny<Workspace>(),
                It.IsAny<TriageReportRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ForgeTask
                {
                    Id = 200,
                    WorkspaceId = workspace.Id,
                    Title = "Fix bug #123",
                    Status = TaskOverallStatus.Suggested,
                    Origin = TaskOrigin.GitHubIssue,
                    ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
                    ExternalReferenceId = "123"
                }
            ]);

        var control = new Mock<IOperationsControlService>();
        control.SetupGet(c => c.IsPaused).Returns(false);

        var service = new GitHubIssueSyncService(
            workspaceRepo,
            taskRepo,
            gitHubClient.Object,
            tokenStore.Object,
            authService.Object,
            triageService.Object,
            control.Object,
            Options.Create(new SecurityOptions { AirGappedMode = false }),
            NullLogger<GitHubIssueSyncService>.Instance);

        await service.SyncWorkspaceIssuesAsync(workspace.Id);

        triageService.Verify(
            t => t.TriageExternalReportAsync(
                It.Is<Workspace>(w => w.Id == workspace.Id),
                It.Is<TriageReportRequest>(r => r.ExternalReferenceId == "123" && r.Origin == TaskOrigin.GitHubIssue),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GitHubPullRequestDeliveryService_IgnoresTriageSessionSibling_WhenBuildingClosingPrBody()
    {
        var workspace = new Workspace
        {
            Id = 9,
            Name = "PR Repo",
            RepositoryPath = "/repos/pr",
            DefaultBaseBranch = "main",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                RepositoryOwner = "acme",
                RepositoryName = "demo",
                PullRequestDeliveryEnabled = true
            }
        };

        var taskRepo = new InMemoryTaskRepository();
        var deliveredTask = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Implement fix #45",
            Description = "Fix details",
            Status = TaskOverallStatus.Ready,
            Origin = TaskOrigin.GitHubIssue,
            ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
            ExternalReferenceId = "45",
            BaseBranch = "main",
            FeatureBranch = "feature/task-1"
        });

        await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Triage: GitHub Issue #45",
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Triage,
            Origin = TaskOrigin.GitHubIssue,
            ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
            ExternalReferenceId = "45",
            BaseBranch = "main"
        });

        var deliveryService = new GitHubPullRequestDeliveryService(
            new Mock<IGitCliRunner>().Object,
            new Mock<IGitWorkspaceManager>().Object,
            new Mock<IGitMirrorSyncService>().Object,
            new Mock<IGitHubDeviceAuthService>().Object,
            new Mock<IGitHubTokenStore>().Object,
            new Mock<IGitHubClientService>().Object,
            taskRepo,
            Options.Create(new SecurityOptions { AirGappedMode = false }),
            NullLogger<GitHubPullRequestDeliveryService>.Instance);

        var body = await deliveryService.BuildPullRequestBodyAsync(deliveredTask, workspace, "main");
        Assert.Contains("Resolves #45", body);
    }

    [Fact]
    public async Task GitHubTaskMirrorService_MirrorTaskClosedAsync_ClosesIssueEvenWhenTriageSessionSiblingExists()
    {
        var workspace = new Workspace
        {
            Id = 11,
            Name = "Mirror Close Repo",
            RepositoryPath = "/repos/mirror-close",
            DefaultBaseBranch = "main",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                RepositoryOwner = "acme",
                RepositoryName = "demo",
                TaskMirroringEnabled = true,
                SyncStatusLabels = true,
                PostMilestoneComments = false
            }
        };

        var taskRepo = new InMemoryTaskRepository();
        var closedTask = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Fix #42",
            Status = TaskOverallStatus.Closed,
            Origin = TaskOrigin.GitHubIssue,
            ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
            ExternalReferenceId = "42",
            BaseBranch = "main"
        });

        await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Triage: Issue #42",
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Triage,
            Origin = TaskOrigin.GitHubIssue,
            ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
            ExternalReferenceId = "42",
            BaseBranch = "main"
        });

        var gitHubClient = new Mock<IGitHubClientService>();
        gitHubClient.Setup(c => c.GetIssueAsync("acme", "demo", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue { Number = 42, Title = "Issue 42", State = "open", Labels = [] });

        var tokenStore = new Mock<IGitHubTokenStore>();
        tokenStore.Setup(s => s.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubOAuthTokenPayload { AccessToken = "tok", AuthenticatedUserLogin = "octocat" });
        var authService = new Mock<IGitHubDeviceAuthService>();
        authService.Setup(a => a.IsConnectedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var mirrorService = new GitHubTaskMirrorService(
            taskRepo,
            new InMemoryTaskDependencyStore(),
            gitHubClient.Object,
            tokenStore.Object,
            authService.Object,
            new InMemoryAgentActivityStreamer(),
            Mock.Of<IOperationsTelemetryService>(),
            Options.Create(new SecurityOptions { AirGappedMode = false }),
            NullLogger<GitHubTaskMirrorService>.Instance);

        await mirrorService.MirrorTaskClosedAsync(closedTask, workspace);

        gitHubClient.Verify(
            c => c.UpdateIssueAsync(
                "acme",
                "demo",
                42,
                It.Is<UpdateGitHubIssueRequest>(r => r.State == "closed"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GitHubTaskMirrorService_TryClaimIssueForExecutionAsync_DoesNotTreatTriageSessionAsActiveLocalSibling()
    {
        var workspace = new Workspace
        {
            Id = 12,
            Name = "Mirror Claim Repo",
            RepositoryPath = "/repos/mirror-claim",
            DefaultBaseBranch = "main",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                RepositoryOwner = "acme",
                RepositoryName = "demo",
                TaskMirroringEnabled = true,
                AutoAssignIssueOnStart = true,
                SyncStatusLabels = true
            }
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Triage: Issue #42",
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Triage,
            Origin = TaskOrigin.GitHubIssue,
            ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
            ExternalReferenceId = "42",
            BaseBranch = "main"
        });

        var openTask = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Implement #42",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = false,
            Origin = TaskOrigin.GitHubIssue,
            ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
            ExternalReferenceId = "42",
            BaseBranch = "main"
        });

        var gitHubClient = new Mock<IGitHubClientService>();
        gitHubClient.Setup(c => c.GetIssueAsync("acme", "demo", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue
            {
                Number = 42,
                Title = "Issue 42",
                State = "open",
                Assignees = [],
                Labels = [GitHubSyncConstants.LabelInProgress]
            });

        var tokenStore = new Mock<IGitHubTokenStore>();
        tokenStore.Setup(s => s.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubOAuthTokenPayload { AccessToken = "tok", AuthenticatedUserLogin = "octocat" });
        var authService = new Mock<IGitHubDeviceAuthService>();
        authService.Setup(a => a.IsConnectedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var mirrorService = new GitHubTaskMirrorService(
            taskRepo,
            new InMemoryTaskDependencyStore(),
            gitHubClient.Object,
            tokenStore.Object,
            authService.Object,
            new InMemoryAgentActivityStreamer(),
            Mock.Of<IOperationsTelemetryService>(),
            Options.Create(new SecurityOptions { AirGappedMode = false }),
            NullLogger<GitHubTaskMirrorService>.Instance);

        var claimed = await mirrorService.TryClaimIssueForExecutionAsync(openTask, workspace);

        Assert.False(claimed);
        var updated = await taskRepo.GetByIdAsync(openTask.Id);
        Assert.NotNull(updated);
        Assert.True(updated.IsWaitingForHuman);
    }

    [Fact]
    public async Task EfCoreTaskRepository_CreateWithBaselineAsync_PreservesEmptyFeatureBranchForTriageStatus()
    {
        await using (var initContext = new AppDbContext(_options))
        {
            await initContext.EnsureDatabaseCreatedAndWalAsync();
            initContext.Workspaces.Add(new Workspace
            {
                Id = 1,
                Name = "SQLite WS",
                RepositoryPath = "/repos/sqlite",
                DefaultBaseBranch = "main"
            });
            await initContext.SaveChangesAsync();
        }

        var repo = new EfCoreTaskRepository(new SqliteDbContextFactory(_options));

        var triageTask = await repo.CreateWithBaselineAsync(
            new ForgeTask
            {
                WorkspaceId = 1,
                Title = "Triage: incoming report",
                Description = "Verbatim",
                Status = TaskOverallStatus.Triage,
                HasExecutionRun = true,
                CurrentStageId = StandardStageIds.Triage,
                BaseBranch = "main",
                FeatureBranch = string.Empty,
                Origin = TaskOrigin.UntrustedIntake
            },
            dependsOn: null,
            createdBy: AuditActors.TriageAgent,
            reason: "Start triage");

        var openTask = await repo.CreateWithBaselineAsync(
            new ForgeTask
            {
                WorkspaceId = 1,
                Title = "Open Task",
                Description = "Work",
                Status = TaskOverallStatus.Open,
                BaseBranch = "main",
                FeatureBranch = string.Empty,
                Origin = TaskOrigin.Manual
            },
            dependsOn: null,
            createdBy: AuditActors.User,
            reason: "Init");

        var reloadedTriage = await repo.GetByIdAsync(triageTask.Id);
        var reloadedOpen = await repo.GetByIdAsync(openTask.Id);

        Assert.NotNull(reloadedTriage);
        Assert.Equal(string.Empty, reloadedTriage.FeatureBranch);
        Assert.NotNull(reloadedOpen);
        Assert.Equal($"feature/task-{reloadedOpen.Id}", reloadedOpen.FeatureBranch);
    }

    private sealed class SqliteDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public SqliteDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;

        public AppDbContext CreateDbContext() => new(_options);
    }
}
