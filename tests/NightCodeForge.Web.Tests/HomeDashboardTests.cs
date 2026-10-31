using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.BackgroundJobs;
using NightCodeForge.Web.Components.Monitoring;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class HomeDashboardTests : BunitContext
{
    [Fact]
    public async Task BuildSnapshotAsync_OrdersWorkspacesByActivity_LimitsTasksToFive_AndCountsPendingQuestions()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var taskRepo = new InMemoryTaskRepository();

        var ws1 = await workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Older Repo",
            RepositoryPath = "/repos/older",
            CreatedAt = DateTime.UtcNow.AddDays(-5)
        });
        var ws2 = await workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Active Repo",
            RepositoryPath = "/repos/active",
            CreatedAt = DateTime.UtcNow.AddDays(-2)
        });

        var runningTask = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws2.Id,
            Title = "Running Task 1",
            Status = TaskOverallStatus.Open,
            CurrentStageId = "implementation",
            Priority = TaskPriority.High,
            UpdatedAt = DateTime.UtcNow
        });

        for (int i = 2; i <= 7; i++)
        {
            await taskRepo.CreateAsync(new ForgeTask
            {
                WorkspaceId = ws1.Id,
                Title = $"Queued Task {i}",
                Status = TaskOverallStatus.Open,
                CurrentStageId = "task-review",
                Priority = TaskPriority.Normal,
                UpdatedAt = DateTime.UtcNow.AddMinutes(-i)
            });
        }

        taskRepo.AddQuestion(new Question
        {
            TaskId = runningTask.Id,
            ForgeTask = runningTask,
            Prompt = "Should we use async streaming?",
            Status = QuestionStatus.Pending
        });
        taskRepo.AddQuestion(new Question
        {
            TaskId = runningTask.Id,
            ForgeTask = runningTask,
            Prompt = "Which serializer should be used?",
            Status = QuestionStatus.Pending
        });

        var devQueueMock = new Mock<IDevQueueService>();
        devQueueMock
            .Setup(s => s.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DevQueueSnapshot(
                UrgentJobs: new[]
                {
                    new QueuedJobItem("job-2", HangfireQueues.LlmUrgent, 2, "Queued Task 2", "task-review", DateTime.UtcNow.AddMinutes(-5), "Urgent", TaskPriority.Critical)
                },
                CodeReviewJobs: new[]
                {
                    new QueuedJobItem("job-3", HangfireQueues.LlmCodeReview, 3, "Queued Task 3", "code-review", DateTime.UtcNow.AddMinutes(-4), "Code Review", TaskPriority.High)
                },
                ImplementationJobs: new[]
                {
                    new QueuedJobItem("job-4", HangfireQueues.LlmImplementation, 4, "Queued Task 4", "implementation", DateTime.UtcNow.AddMinutes(-3), "Implementation", TaskPriority.Normal),
                    new QueuedJobItem("job-5", HangfireQueues.LlmImplementation, 5, "Queued Task 5", "implementation", DateTime.UtcNow.AddMinutes(-2), "Implementation", TaskPriority.Normal),
                    new QueuedJobItem("job-6", HangfireQueues.LlmImplementation, 6, "Queued Task 6", "implementation", DateTime.UtcNow.AddMinutes(-1), "Implementation", TaskPriority.Low)
                },
                TaskReviewJobs: Array.Empty<QueuedJobItem>(),
                InferenceJobs: Array.Empty<QueuedJobItem>(),
                UtilityJobs: Array.Empty<QueuedJobItem>(),
                ProcessingJobs: new[]
                {
                    new QueuedJobItem("job-1", HangfireQueues.LlmImplementation, runningTask.Id, runningTask.Title, "implementation", DateTime.UtcNow.AddMinutes(-6), "Running", TaskPriority.High)
                },
                FailedJobs: Array.Empty<QueuedJobItem>(),
                ActiveLocks: Array.Empty<ActiveLockSnapshot>(),
                SkippedJobs: Array.Empty<SkippedJobItem>()));

        var snapshot = await HomeDashboardBuilder.BuildSnapshotAsync(
            workspaceRepo,
            taskRepo,
            devQueueMock.Object,
            NullLogger.Instance);

        Assert.Equal(2, snapshot.RecentWorkspaces.Count);
        Assert.Equal(ws2.Id, snapshot.RecentWorkspaces[0].Workspace.Id);
        Assert.Equal($"/w/{ws2.Id}/backlog", snapshot.RecentWorkspaces[0].BacklogUrl);
        Assert.Equal($"/w/{ws2.Id}/ideation", snapshot.RecentWorkspaces[0].IdeationUrl);
        Assert.Equal($"/w/{ws2.Id}/inbox", snapshot.RecentWorkspaces[0].InboxUrl);
        Assert.Equal(2, snapshot.RecentWorkspaces[0].PendingQuestionCount);
        Assert.Equal(2, snapshot.PendingQuestionCount);

        Assert.Equal(HomeDashboardBuilder.MaxScheduledTasks, snapshot.RunningAndNextTasks.Count);
        Assert.True(snapshot.RunningAndNextTasks[0].IsRunning);
        Assert.Equal(runningTask.Id, snapshot.RunningAndNextTasks[0].TaskId);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, snapshot.RunningAndNextTasks.Select(t => t.TaskId).ToArray());
    }

    [Fact]
    public async Task HomePage_RendersWorkspaces_QuestionInboxCount_ScheduledTasks_Telemetry_AndSetupButton()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var taskRepo = new InMemoryTaskRepository();

        var ws = await workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Core Engine Repo",
            RepositoryPath = "/repos/core-engine",
            DefaultBaseBranch = "main"
        });

        var task1 = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Implement Vector Index",
            Status = TaskOverallStatus.Open,
            CurrentStageId = "implementation",
            Priority = TaskPriority.Critical
        });

        var task2 = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Add Unit Tests",
            Status = TaskOverallStatus.Open,
            CurrentStageId = "task-review",
            Priority = TaskPriority.Normal
        });

        taskRepo.AddQuestion(new Question
        {
            TaskId = task1.Id,
            ForgeTask = task1,
            Prompt = "Confirm vector dimension size?",
            Status = QuestionStatus.Pending
        });

        var devQueueMock = new Mock<IDevQueueService>();
        devQueueMock
            .Setup(s => s.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DevQueueSnapshot(
                UrgentJobs: Array.Empty<QueuedJobItem>(),
                CodeReviewJobs: Array.Empty<QueuedJobItem>(),
                ImplementationJobs: Array.Empty<QueuedJobItem>(),
                TaskReviewJobs: new[]
                {
                    new QueuedJobItem("job-2", HangfireQueues.LlmTaskReview, task2.Id, task2.Title, "task-review", DateTime.UtcNow, "Task Review", TaskPriority.Normal)
                },
                InferenceJobs: Array.Empty<QueuedJobItem>(),
                UtilityJobs: Array.Empty<QueuedJobItem>(),
                ProcessingJobs: new[]
                {
                    new QueuedJobItem("job-1", HangfireQueues.LlmImplementation, task1.Id, task1.Title, "implementation", DateTime.UtcNow, "Running", TaskPriority.Critical)
                },
                FailedJobs: Array.Empty<QueuedJobItem>(),
                ActiveLocks: Array.Empty<ActiveLockSnapshot>(),
                SkippedJobs: Array.Empty<SkippedJobItem>()));

        var workspaceState = new WorkspaceStateService();
        var telemetryServiceMock = new Mock<IOperationsTelemetryService>();
        telemetryServiceMock.Setup(t => t.GetRecentLlmCalls(It.IsAny<int>())).Returns(new List<LlmCallRecord>());
        telemetryServiceMock.Setup(t => t.GetRecentShellCommands(It.IsAny<int>())).Returns(new List<ShellCommandRecord>());

        var workerState = new WorkerStateService(telemetryServiceMock.Object);
        var hubConnection = TestHubConnectionHelper.Create(workerStateService: workerState);
        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ProviderType).Returns(LlmProviderType.OllamaRouter);

        Services.AddSingleton<IWorkspaceRepository>(workspaceRepo);
        Services.AddSingleton<ITaskRepository>(taskRepo);
        Services.AddSingleton(devQueueMock.Object);
        Services.AddSingleton(workspaceState);
        Services.AddSingleton(workerState);
        Services.AddSingleton(new OperationsTelemetryUiService(hubConnection, telemetryServiceMock.Object));
        Services.AddSingleton(TestServices.CreateOperationsControlService());
        Services.AddSingleton(gatewayMock.Object);
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(NullLogger<Home>.Instance);
        Services.AddSingleton(NullLogger<ActiveOperationsHero>.Instance);

        var cut = Render<Home>();

        // 1. Recent workspace card with Backlog & Ideation links
        Assert.Contains("Core Engine Repo", cut.Markup);
        Assert.Equal($"/w/{ws.Id}/backlog", cut.Find($"#home-ws-backlog-{ws.Id}").GetAttribute("href"));
        Assert.Equal($"/w/{ws.Id}/ideation", cut.Find($"#home-ws-ideation-{ws.Id}").GetAttribute("href"));

        // 2. Question Inbox access + count badge + alert banner
        var inboxBtn = cut.Find("#home-inbox-btn");
        Assert.Equal("/inbox", inboxBtn.GetAttribute("href"));
        Assert.Equal("1", cut.Find("#home-inbox-count-badge").TextContent.Trim());
        Assert.NotNull(cut.Find("#home-pending-questions-banner"));

        // 3. Running & next tasks
        Assert.Contains("Implement Vector Index", cut.Find($"#home-scheduled-task-{task1.Id}").TextContent);
        Assert.Contains("Add Unit Tests", cut.Find($"#home-scheduled-task-{task2.Id}").TextContent);

        // 4. Live telemetry access
        Assert.Equal("/monitoring", cut.Find("#home-live-telemetry-btn").GetAttribute("href"));
        Assert.NotNull(cut.Find("#active-operations-hero"));

        // 5. Setup new workspace button triggers wizard
        bool wizardRequested = false;
        workspaceState.OnOpenWizardRequested += () => wizardRequested = true;
        await cut.Find("#home-setup-workspace-btn").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Assert.True(wizardRequested);
    }

    [Fact]
    public async Task BuildSnapshotAsync_WhenDevQueueThrows_FallsBackToRepositoryTasksOrderedByPriority()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var taskRepo = new InMemoryTaskRepository();

        var ws = await workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Fallback Repo",
            RepositoryPath = "/repos/fallback"
        });

        var lowPriorityTask = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Low Priority Open",
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.Low,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10)
        });
        var criticalTask = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Critical Reviewed",
            Status = TaskOverallStatus.Reviewed,
            Priority = TaskPriority.Critical,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        });
        await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Waiting Task Should Be Excluded",
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.Critical,
            IsWaitingForHuman = true
        });
        await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Suggested Task Should Be Excluded",
            Status = TaskOverallStatus.Suggested,
            Priority = TaskPriority.Critical
        });

        var devQueueMock = new Mock<IDevQueueService>();
        devQueueMock
            .Setup(s => s.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Queue storage offline"));

        var snapshot = await HomeDashboardBuilder.BuildSnapshotAsync(
            workspaceRepo,
            taskRepo,
            devQueueMock.Object,
            NullLogger.Instance);

        Assert.Single(snapshot.RecentWorkspaces);
        Assert.Equal(3, snapshot.RecentWorkspaces[0].OpenTaskCount);
        Assert.Equal(1, snapshot.RecentWorkspaces[0].SuggestedTaskCount);
        Assert.Equal(1, snapshot.RecentWorkspaces[0].WaitingOrReviewTaskCount);

        Assert.Equal(2, snapshot.RunningAndNextTasks.Count);
        Assert.Equal(criticalTask.Id, snapshot.RunningAndNextTasks[0].TaskId);
        Assert.False(snapshot.RunningAndNextTasks[0].IsRunning);
        Assert.Equal(lowPriorityTask.Id, snapshot.RunningAndNextTasks[1].TaskId);
    }

    [Fact]
    public void BuildRunningAndNextTasks_IncludesActiveLockTasks_AndHandlesUnmappedJobTasks()
    {
        var ws = new Workspace { Id = 3, Name = "Locked Repo", RepositoryPath = "/repos/locked" };
        var lockedTask = new ForgeTask
        {
            Id = 10,
            WorkspaceId = ws.Id,
            Title = "Locked Execution Task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = "code-review",
            Priority = TaskPriority.High
        };

        var queueSnapshot = new DevQueueSnapshot(
            UrgentJobs: new[]
            {
                new QueuedJobItem("job-orphan", HangfireQueues.LlmUrgent, 999, "Orphan Queued Task", "triage", DateTime.UtcNow, "Urgent", TaskPriority.Critical)
            },
            CodeReviewJobs: Array.Empty<QueuedJobItem>(),
            ImplementationJobs: Array.Empty<QueuedJobItem>(),
            TaskReviewJobs: Array.Empty<QueuedJobItem>(),
            InferenceJobs: Array.Empty<QueuedJobItem>(),
            UtilityJobs: Array.Empty<QueuedJobItem>(),
            ProcessingJobs: Array.Empty<QueuedJobItem>(),
            FailedJobs: Array.Empty<QueuedJobItem>(),
            ActiveLocks: new[]
            {
                new ActiveLockSnapshot(WorkspaceId: ws.Id, TaskId: lockedTask.Id, LockType: "TaskExclusive", AcquiredAt: DateTime.UtcNow.AddMinutes(-1), DurationHeld: TimeSpan.FromMinutes(1))
            },
            SkippedJobs: Array.Empty<SkippedJobItem>());

        var items = HomeDashboardBuilder.BuildRunningAndNextTasks(
            queueSnapshot,
            new Dictionary<int, ForgeTask> { [lockedTask.Id] = lockedTask },
            new Dictionary<int, Workspace> { [ws.Id] = ws },
            out var totalQueued);

        Assert.Equal(1, totalQueued);
        Assert.Equal(2, items.Count);

        Assert.Equal(lockedTask.Id, items[0].TaskId);
        Assert.True(items[0].IsRunning);
        Assert.Equal($"/w/{ws.Id}/inspector/{lockedTask.Id}", items[0].InspectorUrl);

        Assert.Equal(999, items[1].TaskId);
        Assert.False(items[1].IsRunning);
        Assert.Equal("Unknown Workspace", items[1].WorkspaceName);
        Assert.Equal("/inspector/999", items[1].InspectorUrl);
        Assert.Equal("/tasks/999", items[1].TaskDetailUrl);
    }

    [Fact]
    public async Task BuildSnapshotAsync_IgnoresStaleProcessingJobsForClosedAndSupersededTasks()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var taskRepo = new InMemoryTaskRepository();

        var ws1 = await workspaceRepo.CreateAsync(new Workspace { Id = 1, Name = "OllamaRouter", RepositoryPath = "/repos/router" });
        var ws2 = await workspaceRepo.CreateAsync(new Workspace { Id = 2, Name = "ComicGenCLI", RepositoryPath = "/repos/comic" });

        // Task #30: genuinely running on job 302 (also has stale processing job 299)
        var task30 = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 30,
            WorkspaceId = ws2.Id,
            Title = "Support external LLM provider via OpenAI-compatible API",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            Priority = TaskPriority.High,
            LastHangfireJobId = "302"
        });

        // Task #38: already closed on job 298, but stale job 295 remained in Processing state
        var task38 = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 38,
            WorkspaceId = ws2.Id,
            Title = "Externalize hardcoded strings and magic numbers",
            Status = TaskOverallStatus.Closed,
            CurrentStageId = string.Empty,
            Priority = TaskPriority.High,
            LastHangfireJobId = "298"
        });

        // Task #40: waiting for a worker slot in llm-task-review
        var task40 = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 40,
            WorkspaceId = ws1.Id,
            Title = "Extract Razor @code logic into dedicated presentation services",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            Priority = TaskPriority.High,
            LastHangfireJobId = "301"
        });

        var queueSnapshot = new DevQueueSnapshot(
            UrgentJobs: Array.Empty<QueuedJobItem>(),
            CodeReviewJobs: Array.Empty<QueuedJobItem>(),
            ImplementationJobs: Array.Empty<QueuedJobItem>(),
            TaskReviewJobs: new[]
            {
                new QueuedJobItem("301", HangfireQueues.LlmTaskReview, task40.Id, task40.Title, StandardStageIds.TaskReview, DateTime.UtcNow, "Task Review", TaskPriority.High)
            },
            InferenceJobs: Array.Empty<QueuedJobItem>(),
            UtilityJobs: Array.Empty<QueuedJobItem>(),
            ProcessingJobs: new[]
            {
                new QueuedJobItem("295", "processing", task38.Id, task38.Title, "Processing", DateTime.UtcNow.AddHours(-1), "Running", TaskPriority.High),
                new QueuedJobItem("299", "processing", task30.Id, task30.Title, StandardStageIds.Implementation, DateTime.UtcNow.AddMinutes(-20), "Running", TaskPriority.High),
                new QueuedJobItem("302", "processing", task30.Id, task30.Title, StandardStageIds.Implementation, DateTime.UtcNow.AddMinutes(-2), "Running", TaskPriority.High)
            },
            FailedJobs: Array.Empty<QueuedJobItem>(),
            ActiveLocks: Array.Empty<ActiveLockSnapshot>(),
            SkippedJobs: Array.Empty<SkippedJobItem>());

        var devQueueMock = new Mock<IDevQueueService>();
        devQueueMock.Setup(s => s.GetSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(queueSnapshot);

        var snapshot = await HomeDashboardBuilder.BuildSnapshotAsync(
            workspaceRepo,
            taskRepo,
            devQueueMock.Object,
            NullLogger.Instance);

        var comicSummary = Assert.Single(snapshot.RecentWorkspaces, w => w.Workspace.Id == ws2.Id);
        Assert.Equal(1, comicSummary.RunningTaskCount);

        var routerSummary = Assert.Single(snapshot.RecentWorkspaces, w => w.Workspace.Id == ws1.Id);
        Assert.Equal(0, routerSummary.RunningTaskCount);

        Assert.Equal(2, snapshot.RunningAndNextTasks.Count);
        Assert.Equal(task30.Id, snapshot.RunningAndNextTasks[0].TaskId);
        Assert.True(snapshot.RunningAndNextTasks[0].IsRunning);

        Assert.Equal(task40.Id, snapshot.RunningAndNextTasks[1].TaskId);
        Assert.False(snapshot.RunningAndNextTasks[1].IsRunning);
        Assert.DoesNotContain(snapshot.RunningAndNextTasks, t => t.TaskId == task38.Id);
    }

    [Fact]
    public void HomePage_WhenEmpty_RendersWorkspaceEmptyHero_NoScheduledTasksMessage_AndHidesQuestionsBanner()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var taskRepo = new InMemoryTaskRepository();
        var devQueueMock = new Mock<IDevQueueService>();
        devQueueMock
            .Setup(s => s.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DevQueueSnapshot(
                Array.Empty<QueuedJobItem>(),
                Array.Empty<QueuedJobItem>(),
                Array.Empty<QueuedJobItem>(),
                Array.Empty<QueuedJobItem>(),
                Array.Empty<QueuedJobItem>(),
                Array.Empty<QueuedJobItem>(),
                Array.Empty<QueuedJobItem>(),
                Array.Empty<QueuedJobItem>(),
                Array.Empty<ActiveLockSnapshot>(),
                Array.Empty<SkippedJobItem>()));

        var workspaceState = new WorkspaceStateService();
        var telemetryServiceMock = new Mock<IOperationsTelemetryService>();
        telemetryServiceMock.Setup(t => t.GetRecentLlmCalls(It.IsAny<int>())).Returns(new List<LlmCallRecord>());
        telemetryServiceMock.Setup(t => t.GetRecentShellCommands(It.IsAny<int>())).Returns(new List<ShellCommandRecord>());

        var workerState = new WorkerStateService(telemetryServiceMock.Object);
        var hubConnection = TestHubConnectionHelper.Create(workerStateService: workerState);
        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ProviderType).Returns(LlmProviderType.OllamaRouter);

        Services.AddSingleton<IWorkspaceRepository>(workspaceRepo);
        Services.AddSingleton<ITaskRepository>(taskRepo);
        Services.AddSingleton(devQueueMock.Object);
        Services.AddSingleton(workspaceState);
        Services.AddSingleton(workerState);
        Services.AddSingleton(new OperationsTelemetryUiService(hubConnection, telemetryServiceMock.Object));
        Services.AddSingleton(TestServices.CreateOperationsControlService());
        Services.AddSingleton(gatewayMock.Object);
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(NullLogger<Home>.Instance);
        Services.AddSingleton(NullLogger<ActiveOperationsHero>.Instance);

        var cut = Render<Home>();

        Assert.NotEmpty(cut.FindAll(".workspace-empty-hero"));
        Assert.NotNull(cut.Find("#home-no-scheduled-tasks"));
        Assert.Equal("0", cut.Find("#home-inbox-count-badge").TextContent.Trim());
        Assert.Empty(cut.FindAll("#home-pending-questions-banner"));
    }

    [Fact]
    public void WorkerStateService_UpdatePendingQuestions_OnlyFiresEventWhenCountChanges()
    {
        var telemetryServiceMock = new Mock<IOperationsTelemetryService>();
        var workerState = new WorkerStateService(telemetryServiceMock.Object);

        int eventCount = 0;
        workerState.OnWorkerStateChanged += () => eventCount++;

        workerState.UpdatePendingQuestions(0);
        Assert.Equal(0, eventCount);

        workerState.UpdatePendingQuestions(3);
        Assert.Equal(1, eventCount);
        Assert.Equal(3, workerState.Current.PendingQuestionCount);

        workerState.UpdatePendingQuestions(3);
        Assert.Equal(1, eventCount);
    }

    [Fact]
    public async Task HomeStateChangeDebouncer_CoalescesBurstNotifications_AndCancelsOnDispose()
    {
        int refreshCount = 0;
        var refreshTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var debouncer = new HomeStateChangeDebouncer(
            TimeSpan.FromMilliseconds(80),
            ct =>
            {
                Interlocked.Increment(ref refreshCount);
                refreshTcs.TrySetResult();
                return Task.CompletedTask;
            },
            NullLogger.Instance);

        for (int i = 0; i < 10; i++)
        {
            debouncer.NotifyStateChanged();
        }

        Assert.Equal(0, Volatile.Read(ref refreshCount));

        await refreshTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref refreshCount));

        debouncer.NotifyStateChanged();
        debouncer.Dispose();

        var tokenAfterDispose = Record.Exception(() => debouncer.DisposalToken);
        Assert.Null(tokenAfterDispose);
        Assert.True(debouncer.DisposalToken.IsCancellationRequested);

        await Task.Delay(120);
        Assert.Equal(1, Volatile.Read(ref refreshCount));
    }

    [Fact]
    public async Task HomeStateChangeDebouncer_RapidNotifyAndCancelPending_NeverLogsErrorOrThrowsObjectDisposed()
    {
        var loggerMock = new Mock<ILogger>();
        var debouncer = new HomeStateChangeDebouncer(
            TimeSpan.FromMilliseconds(5),
            _ => Task.CompletedTask,
            loggerMock.Object);

        for (int i = 0; i < 50; i++)
        {
            debouncer.NotifyStateChanged();
            debouncer.CancelPending();
        }

        debouncer.NotifyStateChanged();
        debouncer.Dispose();

        await Task.Delay(50);

        Assert.True(debouncer.DisposalToken.IsCancellationRequested);
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task HomePage_WhenStateChangeExpiresDuringInFlightRefresh_ReQueuesFollowUpRefreshAfterLoadCompletes()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var taskRepo = new InMemoryTaskRepository();
        var firstLoadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLoadCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int snapshotCallCount = 0;

        var emptyQueueSnapshot = new DevQueueSnapshot(
            Array.Empty<QueuedJobItem>(),
            Array.Empty<QueuedJobItem>(),
            Array.Empty<QueuedJobItem>(),
            Array.Empty<QueuedJobItem>(),
            Array.Empty<QueuedJobItem>(),
            Array.Empty<QueuedJobItem>(),
            Array.Empty<QueuedJobItem>(),
            Array.Empty<QueuedJobItem>(),
            Array.Empty<ActiveLockSnapshot>(),
            Array.Empty<SkippedJobItem>());

        var devQueueMock = new Mock<IDevQueueService>();
        devQueueMock
            .Setup(s => s.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                var call = Interlocked.Increment(ref snapshotCallCount);
                if (call == 1)
                {
                    await firstLoadGate.Task.WaitAsync(ct);
                }
                else if (call >= 2)
                {
                    secondLoadCompleted.TrySetResult();
                }

                return emptyQueueSnapshot;
            });

        var workspaceState = new WorkspaceStateService();
        var telemetryServiceMock = new Mock<IOperationsTelemetryService>();
        telemetryServiceMock.Setup(t => t.GetRecentLlmCalls(It.IsAny<int>())).Returns(new List<LlmCallRecord>());
        telemetryServiceMock.Setup(t => t.GetRecentShellCommands(It.IsAny<int>())).Returns(new List<ShellCommandRecord>());

        var workerState = new WorkerStateService(telemetryServiceMock.Object);
        var hubConnection = TestHubConnectionHelper.Create(workerStateService: workerState);
        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ProviderType).Returns(LlmProviderType.OllamaRouter);

        Services.AddSingleton<IWorkspaceRepository>(workspaceRepo);
        Services.AddSingleton<ITaskRepository>(taskRepo);
        Services.AddSingleton(devQueueMock.Object);
        Services.AddSingleton(workspaceState);
        Services.AddSingleton(workerState);
        Services.AddSingleton(new OperationsTelemetryUiService(hubConnection, telemetryServiceMock.Object));
        Services.AddSingleton(TestServices.CreateOperationsControlService());
        Services.AddSingleton(gatewayMock.Object);
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(NullLogger<Home>.Instance);
        Services.AddSingleton(NullLogger<ActiveOperationsHero>.Instance);

        var cut = Render<Home>(parameters => parameters
            .Add(p => p.StateChangeDebounceInterval, TimeSpan.FromMilliseconds(20)));

        // While initial RefreshDashboardAsync is still in-flight (_isLoading == true),
        // fire a state change and wait long enough for its debounce timer to expire during _isLoading.
        await workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Added During In-Flight Load",
            RepositoryPath = "/repo/inflight",
            DefaultBaseBranch = "main"
        });
        workerState.HandleWorkerStatus(WorkerStatusConstants.Running, 42, StandardStageIds.Implementation, "Local GPU", "In-Flight Task");
        await Task.Delay(60);

        // Now allow the first load to complete; the follow-up refresh must be re-queued and executed.
        firstLoadGate.TrySetResult();
        await secondLoadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Added During In-Flight Load", cut.Markup);
        });
    }
}
