using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Backlog;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskDependencyBlockerUiTests : BunitContext
{
    private readonly InMemoryTaskRepository _taskRepository = new();
    private readonly InMemoryWorkspaceRepository _workspaceRepository = new();
    private readonly InMemoryTaskDependencyStore _depStore = new();
    private readonly DependencyResolutionEngine _depEngine;
    private readonly MockTaskMarkdownSyncService _markdownSync = new();
    private readonly MockTaskOrchestrator _orchestrator = new();
    private readonly WorkspaceStateService _workspaceState = new();
    private readonly OrchestratorHubClientConnection _hubConnection;
    private readonly ToastService _toastService;

    public TaskDependencyBlockerUiTests()
    {
        _depEngine = new DependencyResolutionEngine(_depStore);
        _hubConnection = TestHubConnectionHelper.Create();
        _toastService = TestServices.CreateToastService();

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<IWorkspaceRepository>(_workspaceRepository);
        Services.AddSingleton<IWorkflowRepository>(new InMemoryWorkflowRepository());
        Services.AddSingleton<ITaskDependencyStore>(_depStore);
        Services.AddSingleton<IDependencyResolutionEngine>(_depEngine);
        Services.AddSingleton<ITaskMarkdownSyncService>(_markdownSync);
        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
        Services.AddSingleton(_hubConnection);
        Services.AddSingleton(_toastService);
        Services.AddSingleton<IDevQueueService>(new MockDevQueueService());
        Services.AddSingleton<ITaskHistoryDiffService, TaskHistoryDiffService>();
        Services.AddSingleton<ITaskDiskSyncCoordinator>(new MockTaskDiskSyncCoordinator());
        Services.AddSingleton<IDagLayoutEngine, DagLayoutEngine>();
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        Services.AddSingleton(WorkerStateTestHelper.Create(telemetryMock.Object));
        Services.AddSingleton(TestServices.CreateOperationsControlService());
        Services.AddSingleton<ITaskMutationService>(new TaskMutationService(
            _taskRepository,
            _workspaceRepository,
            _depStore,
            _markdownSync,
            _orchestrator,
            telemetryMock.Object,
            NullLogger<TaskMutationService>.Instance));

        var reviewService = new TaskReviewService(
            _taskRepository,
            _workspaceRepository,
            new WorkspaceLockProvider(),
            new Mock<IGitWorkspaceManager>().Object,
            new Mock<IGitHubPullRequestDeliveryService>().Object,
            _orchestrator,
            _markdownSync,
            NullLogger<TaskReviewService>.Instance);
        Services.AddSingleton<ITaskReviewService>(reviewService);
        Services.AddSingleton<ITaskDiffService>(new TaskDiffService(reviewService));
        Services.AddSingleton<ISyntaxHighlightingService>(new SimpleSyntaxHighlighter());
        Services.AddSingleton(new ReviewCommentStateService());
        TestServices.AddDefaultWebUiServices(Services);
    }

    [Fact]
    public void TaskDependencyPresentationHelper_ResolvesBlockersStalledStatesAndWorkspaceUrls()
    {
        var failedBlocker = new ForgeTask { Id = 1, WorkspaceId = 5, Title = "Broken API", Status = TaskOverallStatus.Failed };
        var hitlBlocker = new ForgeTask { Id = 2, WorkspaceId = 5, Title = "HITL Spec", Status = TaskOverallStatus.Open, IsWaitingForHuman = true };
        var deferredBlocker = new ForgeTask { Id = 3, WorkspaceId = 5, Title = "Paused Task", Status = TaskOverallStatus.Open, IsDeferredByPause = true };
        var manualReadyBlocker = new ForgeTask { Id = 4, WorkspaceId = 5, Title = "Needs Merge", Status = TaskOverallStatus.Ready, MergeMode = MergeMode.ManualReviewRequired };
        var closedPrereq = new ForgeTask { Id = 5, WorkspaceId = 5, Title = "Done Task", Status = TaskOverallStatus.Closed };
        var normalOpenBlocker = new ForgeTask { Id = 6, WorkspaceId = 5, Title = "Queued Task", Status = TaskOverallStatus.Open };

        var target = new ForgeTask
        {
            Id = 10,
            WorkspaceId = 5,
            Title = "Consumer",
            Status = TaskOverallStatus.Open,
            Dependencies = new List<TaskDependency>
            {
                new() { TaskId = 10, DependsOnTaskId = 1 },
                new() { TaskId = 10, DependsOnTaskId = 2 },
                new() { TaskId = 10, DependsOnTaskId = 5 }
            }
        };

        var allTasks = new[] { failedBlocker, hitlBlocker, deferredBlocker, manualReadyBlocker, closedPrereq, normalOpenBlocker, target };

        Assert.Equal(new[] { 1, 2, 5 }, TaskDependencyPresentationHelper.ResolvePrerequisiteIds(target));
        Assert.Equal(new[] { 3 }, TaskDependencyPresentationHelper.ResolvePrerequisiteIds(target, new[] { 3, 3 }));

        var blockers = TaskDependencyPresentationHelper.GetUnclosedBlockers(target, allTasks);
        Assert.Equal(new[] { 1, 2 }, blockers.Select(b => b.Id).ToArray());
        Assert.True(TaskDependencyPresentationHelper.IsSuspendedByBlockers(target, allTasks));
        Assert.False(TaskDependencyPresentationHelper.IsSuspendedByBlockers(target, allTasks, isRunning: true));
        Assert.True(TaskDependencyPresentationHelper.HasStalledOrFailedBlocker(target, allTasks));

        Assert.True(TaskDependencyPresentationHelper.IsStalledOrFailedBlocker(failedBlocker));
        Assert.True(TaskDependencyPresentationHelper.IsStalledOrFailedBlocker(hitlBlocker));
        Assert.True(TaskDependencyPresentationHelper.IsStalledOrFailedBlocker(deferredBlocker));
        Assert.True(TaskDependencyPresentationHelper.IsStalledOrFailedBlocker(manualReadyBlocker));
        Assert.False(TaskDependencyPresentationHelper.IsStalledOrFailedBlocker(closedPrereq));
        Assert.False(TaskDependencyPresentationHelper.IsStalledOrFailedBlocker(normalOpenBlocker));

        Assert.Equal("⏸️ Blocked on #1", TaskDependencyPresentationHelper.FormatSuspendedBadgeText(new[] { failedBlocker }));
        Assert.Equal("⏸️ Blocked on #1, #2", TaskDependencyPresentationHelper.FormatSuspendedBadgeText(blockers));
        Assert.Equal("⏸️ Blocked on #1, #2, #3 (+1)", TaskDependencyPresentationHelper.FormatSuspendedBadgeText(new[] { failedBlocker, hitlBlocker, deferredBlocker, manualReadyBlocker }));

        var banner = TaskDependencyPresentationHelper.FormatSuspensionBannerText(new[] { failedBlocker });
        Assert.Contains("#1", banner);
        Assert.Contains("Worktree will automatically initialize", banner);

        var warning = TaskDependencyPresentationHelper.FormatStalledOrFailedWarningText(new[] { hitlBlocker, deferredBlocker });
        Assert.Contains("waiting for human", warning);
        Assert.Contains("deferred by pause", warning);

        var items = TaskDependencyPresentationHelper.ResolvePrerequisiteItems(target, allTasks, workspaceId: 9);
        Assert.Equal("/w/9/tasks/1", items[0].TaskUrl);
        Assert.True(items[0].RequiresUpstreamAttention);
        Assert.True(items[2].IsClosed);
    }

    [Fact]
    public void Classifiers_NonRunningBlockedOpenWithStaleExecutionFields_RemainsOpenOrBlocked_NotInFlight()
    {
        var prereq = new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Prereq", Status = TaskOverallStatus.Draft };
        var task = new ForgeTask
        {
            Id = 2,
            WorkspaceId = 1,
            Title = "Blocked Open",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            HasExecutionRun = true,
            LastHangfireJobId = "stale-job-1",
            Dependencies = new List<TaskDependency> { new() { TaskId = 2, DependsOnTaskId = 1 } }
        };
        var allTasks = new[] { prereq, task };

        Assert.Equal(BacklogKanbanColumn.Open, BacklogStatusClassifier.GetColumn(task, runningTaskIds: Array.Empty<int>(), allTasks: allTasks));
        Assert.False(BacklogStatusClassifier.IsInFlight(task, runningTaskIds: Array.Empty<int>(), allTasks: allTasks));
        Assert.True(BacklogStatusClassifier.IsOpenBacklog(task, runningTaskIds: Array.Empty<int>(), allTasks: allTasks));

        // While actively executing Stage 1 before suspension finishes, remains InFlight
        Assert.Equal(BacklogKanbanColumn.InFlight, BacklogStatusClassifier.GetColumn(task, runningTaskIds: new[] { 2 }, allTasks: allTasks));

        var dag = new DagLayoutEngine().ComputeLayout(
            new[] { task },
            new[] { (TaskId: 2, DependsOnTaskId: 1) },
            allWorkspaceTasks: allTasks);
        var node = Assert.Single(dag.Nodes);
        Assert.True(node.IsBlocked);
        Assert.False(node.IsInFlight);
        Assert.Equal("🔒 Blocked (1)", node.StatusLabel);

        var depMap = new Dictionary<int, IReadOnlyList<int>> { [2] = new[] { 1 } };
        var filteredOpen = TaskTableFilterService.FilterAndSort(
            new[] { task }, null, TaskStatusFilter.Open, null, TaskRecencyFilter.AllTime,
            TaskSortColumn.Id, true, allTasksForDependencyLookup: allTasks, taskDependencies: depMap);
        var filteredInFlight = TaskTableFilterService.FilterAndSort(
            new[] { task }, null, TaskStatusFilter.InFlight, null, TaskRecencyFilter.AllTime,
            TaskSortColumn.Id, true, allTasksForDependencyLookup: allTasks, taskDependencies: depMap);

        Assert.Single(filteredOpen);
        Assert.Empty(filteredInFlight);
    }

    [Fact]
    public async Task TaskMutationService_UpdateDependenciesAsync_SyncsDependencyStoreBeforeScheduling()
    {
        var callSequence = new List<string>();
        var depStoreMock = new Mock<ITaskDependencyStore>();
        depStoreMock
            .Setup(s => s.SetTaskDependenciesAsync(1, 20, It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callSequence.Add("SetTaskDependenciesAsync"))
            .Returns(Task.CompletedTask);

        var orchestratorMock = new Mock<ITaskOrchestrator>();
        orchestratorMock
            .Setup(o => o.TryScheduleOpenTaskIfReadyAsync(20, It.IsAny<CancellationToken>()))
            .Callback(() => callSequence.Add("TryScheduleOpenTaskIfReadyAsync"))
            .ReturnsAsync(true);

        var taskRepo = new InMemoryTaskRepository();
        var prereq = await taskRepo.CreateAsync(new ForgeTask { Id = 10, WorkspaceId = 1, Title = "Done", Status = TaskOverallStatus.Closed });
        var task = await taskRepo.CreateAsync(new ForgeTask { Id = 20, WorkspaceId = 1, Title = "Target", Status = TaskOverallStatus.Open });
        await _workspaceRepository.CreateAsync(new Workspace { Id = 1, Name = "WS", RepositoryPath = "C:/repo" });

        var svc = new TaskMutationService(
            taskRepo,
            _workspaceRepository,
            depStoreMock.Object,
            _markdownSync,
            orchestratorMock.Object,
            new Mock<IOperationsTelemetryService>().Object,
            NullLogger<TaskMutationService>.Instance);

        await svc.UpdateDependenciesAsync(task, new[] { prereq.Id });

        Assert.Equal(new[] { "SetTaskDependenciesAsync", "TryScheduleOpenTaskIfReadyAsync" }, callSequence);
    }

    [Fact]
    public async Task BacklogPage_OpenTaskDependingOnDraft_ExcludesDraftCardFromBoardAndShowsBlockedBadge()
    {
        var workspace = new Workspace { Id = 7, Name = "Draft Blocker WS", RepositoryPath = "C:/repo", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(workspace);

        var draft = await _taskRepository.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = 7, Title = "Unconfirmed Draft Prereq", Status = TaskOverallStatus.Draft });
        var open = await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = 7,
            Title = "Blocked Open Task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            HasExecutionRun = true
        }, dependsOn: new[] { draft.Id });
        _depStore.AddTask(draft.Id, 7, TaskOverallStatus.Draft);
        _depStore.AddTask(open.Id, 7, TaskOverallStatus.Open);
        _depStore.AddDependency(7, open.Id, draft.Id);

        var cut = Render<Backlog>();

        var cards = cut.FindAll(".kanban-card");
        var singleCard = Assert.Single(cards);
        Assert.Contains("Blocked Open Task", singleCard.TextContent);
        Assert.DoesNotContain("Unconfirmed Draft Prereq", singleCard.TextContent);
        var blockedBadge = singleCard.QuerySelector(".badge-blocked");
        Assert.NotNull(blockedBadge);
        Assert.Contains("⏸️ Blocked on #1", blockedBadge.TextContent);
        var chip = singleCard.QuerySelector(".prereq-chip");
        Assert.NotNull(chip);
        Assert.Equal("/w/7/tasks/1", chip.GetAttribute("href"));
    }

    [Fact]
    public void KanbanCard_RendersSuspensionBadgeBeforeCurrentStage_UpstreamWarning_AndInteractiveChips()
    {
        var failedPrereq = new ForgeTask { Id = 1, WorkspaceId = 7, Title = "Failed Core", Status = TaskOverallStatus.Failed };
        var consumer = new ForgeTask
        {
            Id = 2,
            WorkspaceId = 7,
            Title = "Suspended Card",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            HasExecutionRun = true,
            Dependencies = new List<TaskDependency> { new() { TaskId = 2, DependsOnTaskId = 1 } }
        };

        var cardClicks = 0;
        int? clickedPrereqId = null;

        var cut = Render<KanbanCard>(p => p
            .Add(c => c.Task, consumer)
            .Add(c => c.AllTasks, new[] { failedPrereq, consumer })
            .Add(c => c.OnClick, _ => cardClicks++)
            .Add(c => c.OnPrerequisiteClicked, id => clickedPrereqId = id));

        var blockedBadge = cut.Find(".badge-blocked");
        Assert.Contains("⏸️ Blocked on #1", blockedBadge.TextContent);
        Assert.DoesNotContain("task-review", blockedBadge.TextContent);

        var warningBadge = cut.Find(".badge-upstream-warning");
        Assert.Contains("Upstream Attention", warningBadge.TextContent);

        var chip = cut.Find(".prereq-chip");
        Assert.Equal("/w/7/tasks/1", chip.GetAttribute("href"));
        Assert.Contains("⚠️ Prereq: #1", chip.TextContent);

        chip.Click();
        Assert.Equal(1, clickedPrereqId);
        Assert.Equal(0, cardClicks);

        // When OnPrerequisiteClicked is unbound, clicking chip navigates using NavigationManager
        var navCut = Render<KanbanCard>(p => p
            .Add(c => c.Task, consumer)
            .Add(c => c.AllTasks, new[] { failedPrereq, consumer }));
        navCut.Find(".prereq-chip").Click();
        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith("/w/7/tasks/1", nav.Uri);
    }

    [Fact]
    public async Task TaskDetailPage_AndDrawer_RenderSuspensionBannerUpstreamWarningAndLinks()
    {
        var ws = await _workspaceRepository.CreateAsync(new Workspace { Id = 7, Name = "Banner WS", RepositoryPath = "C:/repo" });
        _workspaceState.SetActiveWorkspace(ws);

        var failedPrereq = await _taskRepository.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = 7, Title = "Failed Prereq", Status = TaskOverallStatus.Failed });
        var target = await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = 7,
            Title = "Blocked Target",
            Status = TaskOverallStatus.Open
        }, dependsOn: new[] { 1 });
        var downstream = await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 3,
            WorkspaceId = 7,
            Title = "Downstream Task",
            Status = TaskOverallStatus.Open
        }, dependsOn: new[] { 2 });

        _depStore.AddTask(1, 7, TaskOverallStatus.Failed);
        _depStore.AddTask(2, 7, TaskOverallStatus.Open);
        _depStore.AddTask(3, 7, TaskOverallStatus.Open);
        _depStore.AddDependency(7, 2, 1);
        _depStore.AddDependency(7, 3, 2);

        var pageCut = Render<TaskDetail>(p => p
            .Add(t => t.WorkspaceId, 7)
            .Add(t => t.TaskId, 2));

        var banner = pageCut.Find("#task-suspension-banner");
        Assert.Contains("⏸️ Blocked on #1", banner.TextContent);
        Assert.Contains("Failed Prereq", banner.TextContent);
        var pageWarning = pageCut.Find("#task-suspension-upstream-warning");
        Assert.Contains("Upstream attention required", pageWarning.TextContent);

        var drawerCut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, target)
            .Add(d => d.AllTasks, new[] { failedPrereq, target, downstream })
            .Add(d => d.PrerequisiteIds, new[] { 1 })
            .Add(d => d.DependentIds, new[] { 3 }));

        var drawerBanner = drawerCut.Find("#drawer-suspension-banner");
        Assert.Contains("⏸️ Blocked on #1", drawerBanner.TextContent);
        var drawerWarning = drawerCut.Find("#drawer-suspension-upstream-warning");
        Assert.Contains("Upstream attention required", drawerWarning.TextContent);

        var depLinks = drawerCut.FindAll(".dependency-list a.dependency-item");
        Assert.Equal(2, depLinks.Count);
        Assert.Contains(depLinks, a => a.GetAttribute("href") == "/w/7/tasks/1");
        Assert.Contains(depLinks, a => a.GetAttribute("href") == "/w/7/tasks/3");
    }

    [Fact]
    public void TaskDependencyPresentationHelper_FiltersInvalidIds_AndFormatsManualReadyWarningDetails()
    {
        var target = new ForgeTask
        {
            Id = 50,
            WorkspaceId = 3,
            Title = "Target",
            Status = TaskOverallStatus.Open,
            Dependencies = new List<TaskDependency>
            {
                new() { TaskId = 50, DependsOnTaskId = 0 },
                new() { TaskId = 50, DependsOnTaskId = -2 },
                new() { TaskId = 50, DependsOnTaskId = 51 }
            }
        };

        Assert.Equal(new[] { 51 }, TaskDependencyPresentationHelper.ResolvePrerequisiteIds(target));
        Assert.Equal(new[] { 52 }, TaskDependencyPresentationHelper.ResolvePrerequisiteIds(target, new[] { 0, -1, 52, 52 }));

        var dependentItems = TaskDependencyPresentationHelper.ResolveDependentItems(
            new[] { 0, -3, 53, 53 },
            Array.Empty<ForgeTask>(),
            workspaceId: 3);
        var depItem = Assert.Single(dependentItems);
        Assert.Equal(53, depItem.TaskId);

        var manualReady = new ForgeTask { Id = 60, WorkspaceId = 3, Title = "Manual PR", Status = TaskOverallStatus.Ready, MergeMode = MergeMode.ManualReviewRequired };
        var externalPrReady = new ForgeTask { Id = 61, WorkspaceId = 3, Title = "External PR", Status = TaskOverallStatus.Ready, MergeMode = MergeMode.ExternalPullRequest };

        var warning = TaskDependencyPresentationHelper.FormatStalledOrFailedWarningText(new[] { manualReady, externalPrReady });
        Assert.Contains("manual review required", warning);
        Assert.Contains("external PR merge required", warning);
    }

    [Fact]
    public void KanbanCard_AndTaskDetailDrawer_RespectExplicitEmptyPrerequisiteIds_AndOmittedNullPrerequisiteIds()
    {
        var openBlocker = new ForgeTask { Id = 1, WorkspaceId = 7, Title = "Open Prereq", Status = TaskOverallStatus.Open };
        var taskWithStaleNavDeps = new ForgeTask
        {
            Id = 2,
            WorkspaceId = 7,
            Title = "Task With Stale Nav Deps",
            Status = TaskOverallStatus.Open,
            Dependencies = new List<TaskDependency> { new() { TaskId = 2, DependsOnTaskId = 1 } }
        };

        // Explicit empty PrerequisiteIds must override stale Task.Dependencies (no blocker pill / no prereq chips / drawer shows 0 prereqs)
        var cardExplicitEmpty = Render<KanbanCard>(p => p
            .Add(c => c.Task, taskWithStaleNavDeps)
            .Add(c => c.AllTasks, new[] { openBlocker, taskWithStaleNavDeps })
            .Add(c => c.PrerequisiteIds, Array.Empty<int>()));
        Assert.Empty(cardExplicitEmpty.FindAll(".badge-suspended"));
        Assert.Empty(cardExplicitEmpty.FindAll(".prereq-chip"));

        var drawerExplicitEmpty = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, taskWithStaleNavDeps)
            .Add(d => d.AllTasks, new[] { openBlocker, taskWithStaleNavDeps })
            .Add(d => d.PrerequisiteIds, Array.Empty<int>()));
        Assert.Empty(drawerExplicitEmpty.FindAll("#drawer-suspension-banner"));
        Assert.Contains("Prerequisites (0)", drawerExplicitEmpty.Markup);

        // When PrerequisiteIds is omitted (null), TaskDetailDrawer falls back consistently for BOTH banner and Prerequisites list
        var drawerOmitted = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, taskWithStaleNavDeps)
            .Add(d => d.AllTasks, new[] { openBlocker, taskWithStaleNavDeps }));
        Assert.NotNull(drawerOmitted.Find("#drawer-suspension-banner"));
        Assert.Contains("Prerequisites (1)", drawerOmitted.Markup);
        Assert.Single(drawerOmitted.FindAll(".dependency-list a.dependency-item"));
    }

    [Fact]
    public async Task TaskDetailPage_AndDrawer_SuppressSuspensionBannerWhenActivelyRunning_AndRenderNeutralUnknownBadgeForMissingTasks()
    {
        var ws = await _workspaceRepository.CreateAsync(new Workspace { Id = 7, Name = "Running & Unknown WS", RepositoryPath = "C:/repo" });
        _workspaceState.SetActiveWorkspace(ws);

        var openBlocker = await _taskRepository.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = 7, Title = "Open Blocker", Status = TaskOverallStatus.Open });
        // Task #2 depends on #1 (Open) and #999 (missing/deleted from AllTasks)
        var target = await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = 7,
            Title = "Active Stage 1 Task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            HasExecutionRun = true
        }, dependsOn: new[] { 1, 999 });

        _depStore.AddTask(1, 7, TaskOverallStatus.Open);
        _depStore.AddTask(2, 7, TaskOverallStatus.Open);
        _depStore.AddDependency(7, 2, 1);
        _depStore.AddDependency(7, 2, 999);

        // Simulate Task #2 actively executing via WorkerStateService
        var workerState = Services.GetRequiredService<WorkerStateService>();
        workerState.HandleWorkerStatus(WorkerStatusConstants.Running, 2, StandardStageIds.TaskReview, "Local GPU");

        var pageCut = Render<TaskDetail>(p => p
            .Add(t => t.WorkspaceId, 7)
            .Add(t => t.TaskId, 2));

        // Running task must NOT render suspension banner while Stage 1 is executing
        Assert.Empty(pageCut.FindAll("#task-suspension-banner"));

        // Unknown prerequisite #999 must render neutral Unknown badge (not "Blocking (Unknown)")
        var unknownPageRow = pageCut.Find("a.dependency-item[href='/w/7/tasks/999']");
        Assert.Contains("Unknown", unknownPageRow.TextContent);
        Assert.DoesNotContain("Blocking", unknownPageRow.TextContent);

        // Drawer with IsRunning = true suppresses suspension banner and renders Unknown cleanly
        var drawerCut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, target)
            .Add(d => d.AllTasks, new[] { openBlocker, target })
            .Add(d => d.PrerequisiteIds, new[] { 1, 999 })
            .Add(d => d.IsRunning, true));

        Assert.Empty(drawerCut.FindAll("#drawer-suspension-banner"));
        var unknownDrawerRow = drawerCut.Find("a.dependency-item[href='/w/7/tasks/999']");
        Assert.Contains("Unknown", unknownDrawerRow.TextContent);
        Assert.DoesNotContain("Blocking", unknownDrawerRow.TextContent);
        Assert.DoesNotContain("Open", unknownDrawerRow.TextContent);
    }

    [Fact]
    public async Task BacklogPage_DependencyModal_IncludesDraftPrerequisitesForUnlinking()
    {
        var workspace = new Workspace { Id = 7, Name = "Draft Modal WS", RepositoryPath = "C:/repo", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(workspace);

        var draft = await _taskRepository.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = 7, Title = "Draft Prereq Visible In Modal", Status = TaskOverallStatus.Draft });
        var open = await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = 7,
            Title = "Blocked Open Task",
            Status = TaskOverallStatus.Open
        }, dependsOn: new[] { draft.Id });
        _depStore.AddTask(draft.Id, 7, TaskOverallStatus.Draft);
        _depStore.AddTask(open.Id, 7, TaskOverallStatus.Open);
        _depStore.AddDependency(7, open.Id, draft.Id);

        var cut = Render<Backlog>();

        cut.Find("button[title='Manage Dependencies']").Click();

        var modal = cut.Find(".dependency-linking-modal");
        Assert.Contains("Draft Prereq Visible In Modal", modal.TextContent);
        var draftCheckbox = modal.QuerySelector("input[type='checkbox']");
        Assert.NotNull(draftCheckbox);
        Assert.True(draftCheckbox.HasAttribute("checked"));
    }
}
