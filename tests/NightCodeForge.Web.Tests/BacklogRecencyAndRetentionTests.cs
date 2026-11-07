using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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

public class BacklogRecencyAndRetentionTests : BunitContext
{
    public BacklogRecencyAndRetentionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void KanbanCard_RendersRecentBadgeForTerminalTaskWithin24h_AndOmitsForOlder()
    {
        var now = DateTime.UtcNow;

        var recentFailed = new ForgeTask
        {
            Id = 10,
            Title = "Recent Failure",
            Status = TaskOverallStatus.Failed,
            Priority = 1,
            UpdatedAt = now.AddHours(-2)
        };

        var oldFailed = new ForgeTask
        {
            Id = 20,
            Title = "Older Failure",
            Status = TaskOverallStatus.Failed,
            Priority = 1,
            UpdatedAt = now.AddHours(-26)
        };

        var cutRecent = Render<KanbanCard>(parameters => parameters
            .Add(p => p.Task, recentFailed)
            .Add(p => p.AllTasks, new[] { recentFailed }));

        var cutOld = Render<KanbanCard>(parameters => parameters
            .Add(p => p.Task, oldFailed)
            .Add(p => p.AllTasks, new[] { oldFailed }));

        Assert.NotNull(cutRecent.Find($"#badge-recent-{recentFailed.Id}"));
        Assert.Contains("⚡ <24h", cutRecent.Find($"#badge-recent-{recentFailed.Id}").TextContent);

        Assert.Empty(cutOld.FindAll($"#badge-recent-{oldFailed.Id}"));
    }

    [Fact]
    public void DagLayoutEngine_PopulatesIsRecentTerminal_AndVisualizerRendersSvgBadge()
    {
        var now = DateTime.UtcNow;
        var engine = new DagLayoutEngine();

        var recentClosed = new ForgeTask
        {
            Id = 30,
            Title = "Recently Completed Task",
            Status = TaskOverallStatus.Closed,
            Priority = 2,
            UpdatedAt = now.AddHours(-3)
        };

        var oldClosed = new ForgeTask
        {
            Id = 40,
            Title = "Ancient Completed Task",
            Status = TaskOverallStatus.Closed,
            Priority = 2,
            UpdatedAt = now.AddDays(-10)
        };

        var tasks = new[] { recentClosed, oldClosed };
        var layout = engine.ComputeLayout(tasks, Array.Empty<(int, int)>());

        var nodeRecent = layout.Nodes.First(n => n.Task.Id == 30);
        var nodeOld = layout.Nodes.First(n => n.Task.Id == 40);

        Assert.True(nodeRecent.IsRecentTerminal);
        Assert.Equal("⚡ <24h", nodeRecent.RecentBadgeText);
        Assert.False(nodeOld.IsRecentTerminal);

        Services.AddSingleton<IDagLayoutEngine>(engine);
        var cut = Render<DagVisualizer>(parameters => parameters
            .Add(p => p.Tasks, tasks)
            .Add(p => p.Dependencies, Array.Empty<(int, int)>()));

        Assert.NotNull(cut.Find("#dag-node-recent-30"));
        Assert.Empty(cut.FindAll("#dag-node-recent-40"));
    }

    [Fact]
    public void PrerequisiteIntegrity_OpenTaskDependingOnOldClosedTask_IsReadyForInferenceNotBlocked()
    {
        var now = DateTime.UtcNow;

        // Prerequisite task closed 12 days ago (excluded by 7-day retention)
        var oldPrereq = new ForgeTask
        {
            Id = 1,
            Title = "Database Migration",
            Status = TaskOverallStatus.Closed,
            UpdatedAt = now.AddDays(-12)
        };

        var dependentTask = new ForgeTask
        {
            Id = 2,
            Title = "Write Repository Layer",
            Status = TaskOverallStatus.Open,
            UpdatedAt = now
        };

        var allWorkspaceTasks = new[] { oldPrereq, dependentTask };
        var visibleBacklogTasks = new[] { dependentTask }; // oldPrereq is filtered out of visible backlog
        var dependencies = new[] { (TaskId: 2, DependsOnTaskId: 1) };

        // 1. In KanbanCard: evaluate with allWorkspaceTasks passed to AllTasks
        var cutCard = Render<KanbanCard>(parameters => parameters
            .Add(p => p.Task, dependentTask)
            .Add(p => p.AllTasks, allWorkspaceTasks)
            .Add(p => p.PrerequisiteIds, new[] { 1 }));

        Assert.Contains("🟢 Ready for Inference", cutCard.Markup);
        Assert.DoesNotContain("🔒 Blocked", cutCard.Markup);

        // 2. In DagLayoutEngine: evaluate with allWorkspaceTasks
        var engine = new DagLayoutEngine();
        var layout = engine.ComputeLayout(visibleBacklogTasks, dependencies, allWorkspaceTasks: allWorkspaceTasks);
        var node = layout.Nodes.First(n => n.Task.Id == 2);

        Assert.False(node.IsBlocked);
        Assert.True(node.IsReadyToExecute);
        Assert.Equal(0, node.BlockerCount);
    }

    [Fact]
    public void PrerequisiteIntegrity_OpenTaskDependingOnUnresolvedPrereqOutsideVisibleSet_RemainsBlockedInDag()
    {
        var now = DateTime.UtcNow;

        // Prerequisite task rejected 10 days ago (excluded from visible tasks by 7-day retention, or filtered out by UI filters)
        var unresolvedPrereq = new ForgeTask
        {
            Id = 1,
            Title = "Rejected Prerequisite",
            Status = TaskOverallStatus.Rejected,
            UpdatedAt = now.AddDays(-10)
        };

        var dependentTask = new ForgeTask
        {
            Id = 2,
            Title = "Dependent Open Task",
            Status = TaskOverallStatus.Open,
            UpdatedAt = now
        };

        var allWorkspaceTasks = new[] { unresolvedPrereq, dependentTask };
        var visibleBacklogTasks = new[] { dependentTask };
        var dependencies = new[] { (TaskId: 2, DependsOnTaskId: 1) };

        var engine = new DagLayoutEngine();
        var layout = engine.ComputeLayout(visibleBacklogTasks, dependencies, allWorkspaceTasks: allWorkspaceTasks);
        var node = layout.Nodes.First(n => n.Task.Id == 2);

        Assert.True(node.IsBlocked);
        Assert.False(node.IsReadyToExecute);
        Assert.Equal(1, node.BlockerCount);
    }

    [Fact]
    public async Task BacklogPage_ExcludesTasksClosedOlderThan7Days_AndShowsViewAllTasksLinkWithArchivedCount()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var workspaceState = new WorkspaceStateService();
        var workerState = WorkerStateTestHelper.Create(new Mock<IOperationsTelemetryService>().Object);
        var hubConnection = TestHubConnectionHelper.Create(workerStateService: workerState);

        var ws = await workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Backlog Repo",
            RepositoryPath = "/repos/backlog"
        });
        workspaceState.SetActiveWorkspace(ws);

        var now = DateTime.UtcNow;

        // 1. Open task
        await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Open Feature",
            Status = TaskOverallStatus.Open,
            UpdatedAt = now
        });

        // 2. Closed task within 7 days (visible)
        await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Closed Recently",
            Status = TaskOverallStatus.Closed,
            UpdatedAt = now.AddDays(-2)
        });

        // 3. Closed task older than 7 days (hidden from Kanban & DAG)
        await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Closed Ancient",
            Status = TaskOverallStatus.Closed,
            UpdatedAt = now.AddDays(-10)
        });

        Services.AddSingleton<IWorkspaceRepository>(workspaceRepo);
        Services.AddSingleton<ITaskRepository>(taskRepo);
        Services.AddSingleton<ITaskDependencyStore>(depStore);
        Services.AddSingleton<IWorkflowRepository>(new InMemoryWorkflowRepository(new[] { TestWorkflowFixtures.CreateStandardWorkflow() }));
        Services.AddSingleton<ITaskOrchestrator>(new MockTaskOrchestrator());
        Services.AddSingleton<IDependencyResolutionEngine>(new DependencyResolutionEngine(depStore));
        Services.AddSingleton<ITaskMarkdownSyncService>(new MockTaskMarkdownSyncService());
        Services.AddSingleton<ITaskMutationService>(new Mock<ITaskMutationService>().Object);
        Services.AddSingleton(workspaceState);
        Services.AddSingleton(workerState);
        Services.AddSingleton(hubConnection);
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(TestServices.CreateOperationsControlService());
        Services.AddSingleton<ITaskDiskSyncCoordinator>(new Mock<ITaskDiskSyncCoordinator>().Object);
        Services.AddSingleton<IDagLayoutEngine>(new DagLayoutEngine());
        Services.AddSingleton(NullLogger<Backlog>.Instance);

        var cut = Render<Backlog>(parameters => parameters.Add(p => p.WorkspaceId, ws.Id));

        cut.WaitForState(() => cut.Markup.Contains("Open Feature"));

        // Open Feature and Closed Recently are visible
        Assert.Contains("Open Feature", cut.Markup);
        Assert.Contains("Closed Recently", cut.Markup);

        // Closed Ancient is NOT displayed in the Kanban cards
        Assert.DoesNotContain("Closed Ancient", cut.Markup);

        // Stats bar contains the link to All Tasks with the 1 archived task count
        var allTasksLink = cut.Find("#backlog-all-tasks-link");
        Assert.NotNull(allTasksLink);
        Assert.Contains("View All Tasks (1 archived >7d)", allTasksLink.TextContent);
        Assert.Contains($"/w/{ws.Id}/tasks", allTasksLink.GetAttribute("href"));
    }
}

