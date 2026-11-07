using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Monitoring;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class HomePendingReviewTests : BunitContext
{
    private static DevQueueSnapshot EmptyQueue() => new(
        UrgentJobs: Array.Empty<QueuedJobItem>(),
        CodeReviewJobs: Array.Empty<QueuedJobItem>(),
        ImplementationJobs: Array.Empty<QueuedJobItem>(),
        TaskReviewJobs: Array.Empty<QueuedJobItem>(),
        InferenceJobs: Array.Empty<QueuedJobItem>(),
        UtilityJobs: Array.Empty<QueuedJobItem>(),
        ProcessingJobs: Array.Empty<QueuedJobItem>(),
        FailedJobs: Array.Empty<QueuedJobItem>(),
        ActiveLocks: Array.Empty<ActiveLockSnapshot>(),
        SkippedJobs: Array.Empty<SkippedJobItem>());

    private static Mock<IDevQueueService> EmptyDevQueue()
    {
        var mock = new Mock<IDevQueueService>();
        mock.Setup(s => s.GetSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(EmptyQueue());
        return mock;
    }

    private static string ConflictJson()
    {
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.HasResolvedConflict, true);
        return bag.ToJson();
    }

    private static async Task<(InMemoryWorkspaceRepository, InMemoryTaskRepository, Workspace, ForgeTask, ForgeTask, ForgeTask)> SeedAsync()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var taskRepo = new InMemoryTaskRepository();
        var ws = await workspaceRepo.CreateAsync(new Workspace { Name = "Review Repo", RepositoryPath = "/repos/review" });

        var older = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Normal older",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true,
            Priority = TaskPriority.Normal,
            FeatureBranch = "feature/older",
            BaseBranch = "main",
            UpdatedAt = DateTime.UtcNow.AddHours(-2)
        });
        var critical = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Critical conflict",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true,
            Priority = TaskPriority.Critical,
            FeatureBranch = "feature/critical",
            BaseBranch = "main",
            PipelineDataJson = ConflictJson(),
            UpdatedAt = DateTime.UtcNow.AddHours(-3)
        });
        var newer = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Normal newer",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true,
            Priority = TaskPriority.Normal,
            FeatureBranch = "feature/newer",
            BaseBranch = "main",
            UpdatedAt = DateTime.UtcNow.AddHours(-1)
        });
        await taskRepo.CreateAsync(new ForgeTask { WorkspaceId = ws.Id, Title = "Ready no run", Status = TaskOverallStatus.Ready, HasExecutionRun = false });
        await taskRepo.CreateAsync(new ForgeTask { WorkspaceId = ws.Id, Title = "Open", Status = TaskOverallStatus.Open, HasExecutionRun = true });
        await taskRepo.CreateAsync(new ForgeTask { WorkspaceId = ws.Id, Title = "Closed", Status = TaskOverallStatus.Closed, HasExecutionRun = true });
        return (workspaceRepo, taskRepo, ws, older, critical, newer);
    }

    [Fact]
    public async Task BuildSnapshotAsync_PopulatesPendingReviewTasks_OrderedAndExcludingNonPending()
    {
        var (workspaceRepo, taskRepo, ws, older, critical, newer) = await SeedAsync();

        var snapshot = await HomeDashboardBuilder.BuildSnapshotAsync(
            workspaceRepo, taskRepo, EmptyDevQueue().Object, NullLogger.Instance);

        Assert.Equal(3, snapshot.PendingReviewCount);
        Assert.Equal(new[] { critical.Id, newer.Id, older.Id }, snapshot.PendingReviewTasks.Select(t => t.TaskId).ToArray());
        Assert.True(snapshot.PendingReviewTasks[0].HasResolvedConflict);
        Assert.False(snapshot.PendingReviewTasks[1].HasResolvedConflict);
        Assert.Equal($"/w/{ws.Id}/review/{critical.Id}", snapshot.PendingReviewTasks[0].ReviewUrl);
        Assert.Equal($"/w/{ws.Id}/inspector/{critical.Id}", snapshot.PendingReviewTasks[0].InspectorUrl);
        Assert.Equal($"/w/{ws.Id}/tasks/{critical.Id}", snapshot.PendingReviewTasks[0].TaskDetailUrl);
        Assert.Equal("feature/critical", snapshot.PendingReviewTasks[0].FeatureBranch);
        Assert.Equal(3, snapshot.RecentWorkspaces[0].PendingReviewCount);
        Assert.Equal(3, snapshot.RecentWorkspaces[0].WaitingOrReviewTaskCount);
        Assert.Equal($"/w/{ws.Id}/review", snapshot.RecentWorkspaces[0].ReviewUrl);
    }

    [Fact]
    public async Task BuildSnapshotAsync_ReadyWithoutExecutionRun_DoesNotTriggerAttentionBadge()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var taskRepo = new InMemoryTaskRepository();
        var ws = await workspaceRepo.CreateAsync(new Workspace { Name = "Clean Repo", RepositoryPath = "/repos/clean" });
        await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Ready without execution run",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = false
        });

        var snapshot = await HomeDashboardBuilder.BuildSnapshotAsync(
            workspaceRepo, taskRepo, EmptyDevQueue().Object, NullLogger.Instance);

        Assert.Equal(0, snapshot.RecentWorkspaces[0].PendingReviewCount);
        Assert.Equal(0, snapshot.RecentWorkspaces[0].WaitingOrReviewTaskCount);
    }

    [Fact]
    public async Task HomePage_RendersReviewButton_WorkspaceBadgeAlongsideQuestions_AndPendingReviewRows()
    {
        var (workspaceRepo, taskRepo, ws, _, critical, newer) = await SeedAsync();
        taskRepo.AddQuestion(new Question
        {
            TaskId = critical.Id,
            ForgeTask = critical,
            Prompt = "Q?",
            Status = QuestionStatus.Pending
        });

        var workspaceState = new WorkspaceStateService();
        var telemetryServiceMock = new Mock<IOperationsTelemetryService>();
        telemetryServiceMock.Setup(t => t.GetRecentLlmCalls(It.IsAny<int>())).Returns(new List<LlmCallRecord>());
        telemetryServiceMock.Setup(t => t.GetRecentShellCommands(It.IsAny<int>())).Returns(new List<ShellCommandRecord>());
        var workerState = WorkerStateTestHelper.Create(telemetryServiceMock.Object);
        var hubConnection = TestHubConnectionHelper.Create(workerStateService: workerState);
        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ProviderType).Returns(LlmProviderType.OllamaRouter);

        Services.AddSingleton<IWorkspaceRepository>(workspaceRepo);
        Services.AddSingleton<ITaskRepository>(taskRepo);
        Services.AddSingleton(EmptyDevQueue().Object);
        Services.AddSingleton(workspaceState);
        Services.AddSingleton(workerState);
        Services.AddSingleton(new OperationsTelemetryUiService(hubConnection, telemetryServiceMock.Object));
        Services.AddSingleton(TestServices.CreateOperationsControlService());
        Services.AddSingleton(gatewayMock.Object);
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(NullLogger<Home>.Instance);
        Services.AddSingleton(NullLogger<ActiveOperationsHero>.Instance);

        var cut = Render<Home>();

        Assert.Equal("/review", cut.Find("#home-review-btn").GetAttribute("href"));
        Assert.Equal("3", cut.Find("#home-review-count-badge").TextContent.Trim());
        Assert.NotNull(cut.Find($"#home-ws-inbox-{ws.Id}"));
        Assert.Equal($"/w/{ws.Id}/review", cut.Find($"#home-ws-review-{ws.Id}").GetAttribute("href"));

        Assert.NotNull(cut.Find("#home-pending-reviews-section"));
        Assert.Contains("Critical conflict", cut.Find($"#home-pending-review-{critical.Id}").TextContent);
        Assert.Contains("Conflict Resolved", cut.Find($"#home-pending-review-{critical.Id}").TextContent);
        Assert.DoesNotContain("Conflict Resolved", cut.Find($"#home-pending-review-{newer.Id}").TextContent);
        Assert.Equal($"/w/{ws.Id}/review/{newer.Id}", cut.Find($"#home-review-task-btn-{newer.Id}").GetAttribute("href"));
        Assert.Equal(3, workerState.Current.PendingReviewCount);
        Assert.Equal(1, workerState.Current.PendingQuestionCount);
    }
}

