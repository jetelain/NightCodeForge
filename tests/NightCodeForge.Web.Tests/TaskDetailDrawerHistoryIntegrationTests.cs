using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Web.Components.Backlog;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskDetailDrawerHistoryIntegrationTests : BunitContext
{
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly MockDevQueueService _devQueueService;
    private readonly ToastService _toastService;
    private readonly TaskHistoryDiffService _diffService;

    public TaskDetailDrawerHistoryIntegrationTests()
    {
        _taskRepository = new InMemoryTaskRepository();
        _devQueueService = new MockDevQueueService();
        _toastService = TestServices.CreateToastService();
        _diffService = new TaskHistoryDiffService();

        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<IDevQueueService>(_devQueueService);
        Services.AddSingleton(_toastService);
        Services.AddSingleton<ITaskHistoryDiffService>(_diffService);
        Services.AddSingleton<ITaskMutationService>(new TaskMutationService(
            _taskRepository,
            new InMemoryWorkspaceRepository(),
            new MockTaskMarkdownSyncService(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskMutationService>.Instance));
    }

    [Fact]
    public async Task TaskDetailDrawer_SwitchingToHistoryTab_RendersTaskHistoryTimeline()
    {
        var task = new ForgeTask
        {
            Id = 501,
            WorkspaceId = 1,
            Title = "Task with history tab",
            Description = "Original description",
            Priority = TaskPriority.Normal,
            MergeMode = MergeMode.ManualReviewRequired,
            Status = TaskOverallStatus.Open
        };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "Created baseline");

        var cut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, task));

        // Find the history tab button
        var historyTabBtn = cut.FindAll(".drawer-nav-tabs button")
            .First(b => b.TextContent.Contains("History") || b.TextContent.Contains("Revisions"));
        historyTabBtn.Click();

        // Should render TaskHistoryTimeline inside drawer
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Revision History", cut.Markup);
            Assert.Contains("Version 1", cut.Markup);
            Assert.Contains("Created baseline", cut.Markup);
        });
    }

    [Fact]
    public async Task TaskDetailDrawer_EditingDescription_SavesNewVersionWithHistoryAndAttribution()
    {
        var task = new ForgeTask
        {
            Id = 502,
            WorkspaceId = 1,
            Title = "Task to edit description",
            Description = "Original description line 1",
            Priority = TaskPriority.Normal,
            MergeMode = MergeMode.ManualReviewRequired,
            Status = TaskOverallStatus.Open
        };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "Created baseline");

        bool updatedCallbackFired = false;
        var cut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, task)
            .Add(d => d.OnTaskUpdated, (t) => { updatedCallbackFired = true; }));

        // Click Edit on Description section
        var editBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Edit"));
        editBtn.Click();

        // Textarea should now be visible
        var textarea = cut.Find("textarea");
        Assert.NotNull(textarea);

        // Change description
        textarea.Change("Updated description line 1\nAdded line 2 with new criteria");

        // Click Save Changes
        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Changes"));
        saveBtn.Click();

        Assert.True(updatedCallbackFired);
        Assert.Contains("Added line 2 with new criteria", task.Description);

        // Verify history in repository
        var history = await _taskRepository.GetHistoryAsync(502);
        Assert.Equal(2, history.Count);

        var latest = history.Last();
        Assert.Equal(2, latest.Version);
        Assert.Equal(AuditActors.User, latest.ChangedBy);
        Assert.Contains("Updated task description and specification", latest.ChangeReason);
        Assert.Contains("Added line 2 with new criteria", latest.Description);
    }

    [Fact]
    public async Task TaskDetailDrawer_EditingTitle_SavesNewVersionWithHistoryAndAttribution()
    {
        var task = new ForgeTask
        {
            Id = 503,
            WorkspaceId = 1,
            Title = "Original Title",
            Description = "Description",
            Priority = TaskPriority.Normal,
            MergeMode = MergeMode.ManualReviewRequired,
            Status = TaskOverallStatus.Open
        };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "Created baseline");

        bool updatedCallbackFired = false;
        var cut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, task)
            .Add(d => d.OnTaskUpdated, (t) => { updatedCallbackFired = true; }));

        // Click edit title button
        var editBtn = cut.Find(".edit-title-btn");
        editBtn.Click();

        // Input should be visible
        var input = cut.Find(".edit-title-box input");
        Assert.NotNull(input);
        input.Change("Brand New Renamed Title");

        // Save
        var saveBtn = cut.FindAll(".edit-title-box button").First(b => b.TextContent.Contains("Save"));
        saveBtn.Click();

        Assert.True(updatedCallbackFired);
        Assert.Equal("Brand New Renamed Title", task.Title);

        var history = await _taskRepository.GetHistoryAsync(503);
        Assert.Equal(2, history.Count);

        var latest = history.Last();
        Assert.Equal(2, latest.Version);
        Assert.Equal(AuditActors.User, latest.ChangedBy);
        Assert.Contains("Renamed task from \"Original Title\" to \"Brand New Renamed Title\"", latest.ChangeReason);
    }

    [Fact]
    public async Task TaskDetailDrawer_UpdatingPriority_AttributesToAuditActorsUser()
    {
        var task = new ForgeTask
        {
            Id = 504,
            WorkspaceId = 1,
            Title = "Task for priority change",
            Priority = TaskPriority.Normal,
            Status = TaskOverallStatus.Open
        };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "Created baseline");

        var cut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, task));

        // Click P1 chip button
        var p1Btn = cut.FindAll(".priority-chip-btn").First(b => b.TextContent.Contains("P1"));
        p1Btn.Click();

        var history = await _taskRepository.GetHistoryAsync(504);
        Assert.Equal(2, history.Count);

        var latest = history.Last();
        Assert.Equal(AuditActors.User, latest.ChangedBy);
        Assert.Contains("Updated Priority from P3 to P1", latest.ChangeReason);
        Assert.Equal(TaskPriority.Critical, latest.Priority);
    }

    [Fact]
    public async Task TaskDetailDrawer_UpdatingMergeMode_AttributesToAuditActorsUser()
    {
        var task = new ForgeTask
        {
            Id = 505,
            WorkspaceId = 1,
            Title = "Task for merge mode change",
            Priority = TaskPriority.Normal,
            MergeMode = MergeMode.ManualReviewRequired,
            Status = TaskOverallStatus.Open
        };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "Created baseline");

        var cut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, task));

        // Click Auto button
        var autoBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Auto"));
        autoBtn.Click();

        var history = await _taskRepository.GetHistoryAsync(505);
        Assert.Equal(2, history.Count);

        var latest = history.Last();
        Assert.Equal(AuditActors.User, latest.ChangedBy);
        Assert.Contains("Updated MergeMode from ManualReviewRequired to Automatic", latest.ChangeReason);
        Assert.Equal(MergeMode.Automatic, latest.MergeMode);
    }
}
