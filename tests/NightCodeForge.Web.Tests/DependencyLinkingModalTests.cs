using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Web.Components.Backlog;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class DependencyLinkingModalTests : BunitContext
{
    private readonly InMemoryTaskDependencyStore _depStore;
    private readonly DependencyResolutionEngine _dependencyEngine;
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly MockTaskMarkdownSyncService _markdownSync;
    private readonly WorkspaceStateService _workspaceState;
    private readonly ToastService _toastService;

    public DependencyLinkingModalTests()
    {
        _depStore = new InMemoryTaskDependencyStore();
        _dependencyEngine = new DependencyResolutionEngine(_depStore);
        _taskRepository = new InMemoryTaskRepository();
        _markdownSync = new MockTaskMarkdownSyncService();
        _workspaceState = new WorkspaceStateService();
        _toastService = new ToastService();

        Services.AddSingleton<IDependencyResolutionEngine>(_dependencyEngine);
        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<ITaskMarkdownSyncService>(_markdownSync);
        Services.AddSingleton(_workspaceState);
        Services.AddSingleton(_toastService);
    }

    [Fact]
    public void WhenOpen_RendersCandidateTasksExcludingTargetTask()
    {
        var targetTask = new ForgeTask { Id = 1, WorkspaceId = 10, Title = "Task Alpha" };
        var task2 = new ForgeTask { Id = 2, WorkspaceId = 10, Title = "Task Beta" };
        var task3 = new ForgeTask { Id = 3, WorkspaceId = 10, Title = "Task Gamma" };

        var allTasks = new List<ForgeTask> { targetTask, task2, task3 };
        var allDeps = new List<(int TaskId, int DependsOnTaskId)>();

        var cut = Render<DependencyLinkingModal>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.TargetTask, targetTask)
            .Add(m => m.AllTasks, allTasks)
            .Add(m => m.AllDependencies, allDeps));

        var rows = cut.FindAll(".candidate-task-row");
        Assert.Equal(2, rows.Count);
        Assert.Contains("Task Beta", cut.Markup);
        Assert.Contains("Task Gamma", cut.Markup);
        // Target task should NOT be in candidate list
        Assert.DoesNotContain("#1", rows[0].TextContent);
        Assert.DoesNotContain("#1", rows[1].TextContent);
    }

    [Fact]
    public void WhenCycleDetected_BlocksSaveAndRendersDeadlockWarning()
    {
        // Setup existing: Task 1 depends on Task 2 (1 -> 2)
        var task1 = new ForgeTask { Id = 1, WorkspaceId = 10, Title = "Task 1" };
        var task2 = new ForgeTask { Id = 2, WorkspaceId = 10, Title = "Task 2" };

        var allTasks = new List<ForgeTask> { task1, task2 };
        var allDeps = new List<(int TaskId, int DependsOnTaskId)>
        {
            (TaskId: 1, DependsOnTaskId: 2)
        };

        // Maintainer opens modal for Task 2 and tries to make Task 2 depend on Task 1 (2 -> 1)!
        var cut = Render<DependencyLinkingModal>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.TargetTask, task2)
            .Add(m => m.AllTasks, allTasks)
            .Add(m => m.AllDependencies, allDeps));

        // Select task 1 as prerequisite for task 2
        var checkbox = cut.Find("input[type='checkbox']");
        checkbox.Change(true);

        // Click save
        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Dependencies"));
        saveBtn.Click();

        // Verify deadlock warning
        var alert = cut.Find(".alert-error");
        Assert.Contains("Circular dependency detected! This configuration would create a deadlock.", alert.TextContent);

        // Verify modal remains open
        Assert.NotEmpty(cut.FindAll(".dependency-linking-modal"));
    }

    [Fact]
    public async Task WhenValidDependencies_SavesAndInvokesCallback()
    {
        var targetTask = new ForgeTask { Id = 2, WorkspaceId = 10, Title = "Task Beta" };
        await _taskRepository.CreateAsync(targetTask);

        var task1 = new ForgeTask { Id = 1, WorkspaceId = 10, Title = "Task Alpha" };
        await _taskRepository.CreateAsync(task1);

        var allTasks = new List<ForgeTask> { task1, targetTask };
        var allDeps = new List<(int TaskId, int DependsOnTaskId)>();

        bool savedInvoked = false;
        bool closedInvoked = false;

        var cut = Render<DependencyLinkingModal>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.TargetTask, targetTask)
            .Add(m => m.AllTasks, allTasks)
            .Add(m => m.AllDependencies, allDeps)
            .Add(m => m.OnSaved, () => { savedInvoked = true; })
            .Add(m => m.OnClosed, () => { closedInvoked = true; }));

        // Select task 1
        var checkbox = cut.Find("input[type='checkbox']");
        checkbox.Change(true);

        // Save
        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Dependencies"));
        saveBtn.Click();

        Assert.True(savedInvoked);
        Assert.True(closedInvoked);

        // Verify task dependencies persisted in repository
        var deps = await _taskRepository.GetDependenciesAsync(2);
        Assert.Contains(1, deps);
    }
}
