using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Web.Components.Backlog;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskDetailDrawerPriorityAndMergeModeTests : BunitContext
{
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly MockDevQueueService _devQueueService;
    private readonly ToastService _toastService;

    public TaskDetailDrawerPriorityAndMergeModeTests()
    {
        _taskRepository = new InMemoryTaskRepository();
        _devQueueService = new MockDevQueueService();
        _toastService = TestServices.CreateToastService();

        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<IDevQueueService>(_devQueueService);
        Services.AddSingleton(_toastService);
    }

    [Fact]
    public async Task TaskDetailDrawer_ClickingPriorityChip_UpdatesPriorityAndHistory()
    {
        var task = new ForgeTask
        {
            Id = 101,
            WorkspaceId = 1,
            Title = "Task with priority",
            Priority = TaskPriority.Normal,
            MergeMode = MergeMode.ManualReviewRequired,
            Status = TaskOverallStatus.Open
        };
        await _taskRepository.CreateAsync(task);

        bool updatedCallbackFired = false;
        var cut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, task)
            .Add(d => d.OnTaskUpdated, (t) => { updatedCallbackFired = true; }));

        // Find priority chip for P2
        var p2Btn = cut.FindAll(".priority-chip-btn").First(b => b.TextContent.Contains("P2"));
        p2Btn.Click();

        Assert.Equal(TaskPriority.High, task.Priority);
        Assert.True(updatedCallbackFired);

        var history = await _taskRepository.GetHistoryAsync(101);
        Assert.NotEmpty(history);
        var last = history.Last();
        Assert.Contains("Updated Priority from P3 to P2", last.ChangeReason);
        Assert.Equal("User", last.ChangedBy);
    }

    [Fact]
    public async Task TaskDetailDrawer_EscalatingToP1_CallsPromoteToUrgentAsync()
    {
        var task = new ForgeTask
        {
            Id = 102,
            WorkspaceId = 1,
            Title = "Escalation Candidate",
            Priority = TaskPriority.Normal,
            Status = TaskOverallStatus.Open
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, task));

        var p1Btn = cut.FindAll(".priority-chip-btn").First(b => b.TextContent.Contains("P1"));
        p1Btn.Click();

        Assert.Equal(TaskPriority.Critical, task.Priority);
        Assert.Equal(102, _devQueueService.LastPromotedTaskId);
    }

    [Fact]
    public async Task TaskDetailDrawer_ChangingP2ToP5_CallsReorderInferenceQueue()
    {
        var task = new ForgeTask
        {
            Id = 103,
            WorkspaceId = 1,
            Title = "Deprioritization Candidate",
            Priority = TaskPriority.High,
            Status = TaskOverallStatus.Open
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, task));

        var p5Btn = cut.FindAll(".priority-chip-btn").First(b => b.TextContent.Contains("P5"));
        p5Btn.Click();

        Assert.Equal(TaskPriority.Trivial, task.Priority);
        Assert.True(_devQueueService.ReorderCallsCount > 0);
    }

    [Fact]
    public async Task TaskDetailDrawer_ChangingMergeMode_UpdatesMergeModeAndHistory()
    {
        var task = new ForgeTask
        {
            Id = 104,
            WorkspaceId = 1,
            Title = "MergeMode Test Task",
            Priority = TaskPriority.Normal,
            MergeMode = MergeMode.ManualReviewRequired,
            Status = TaskOverallStatus.Open
        };
        await _taskRepository.CreateAsync(task);

        bool updatedCallbackFired = false;
        var cut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, task)
            .Add(d => d.OnTaskUpdated, (t) => { updatedCallbackFired = true; }));

        // Click Auto merge button
        var autoBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Auto"));
        autoBtn.Click();

        Assert.Equal(MergeMode.Automatic, task.MergeMode);
        Assert.True(updatedCallbackFired);

        var history = await _taskRepository.GetHistoryAsync(104);
        Assert.NotEmpty(history);
        var last = history.Last();
        Assert.Contains("Updated MergeMode from ManualReviewRequired to Automatic", last.ChangeReason);
    }
}
