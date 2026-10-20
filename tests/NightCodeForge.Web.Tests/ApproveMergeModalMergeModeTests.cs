using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Review;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class ApproveMergeModalMergeModeTests : BunitContext
{
    private readonly MockTaskOrchestrator _orchestrator;
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly ToastService _toastService;

    public ApproveMergeModalMergeModeTests()
    {
        _orchestrator = new MockTaskOrchestrator();
        _taskRepository = new InMemoryTaskRepository();
        _toastService = TestServices.CreateToastService();

        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton(_toastService);
    }

    [Fact]
    public async Task ApproveMergeModal_RendersCurrentMergeModeAndAllowsSwitching()
    {
        var task = new ForgeTask
        {
            Id = 99,
            Title = "Task to Merge",
            MergeMode = MergeMode.ManualReviewRequired,
            Status = TaskOverallStatus.Ready
        };
        await _taskRepository.CreateAsync(task);

        MergeMode? changedMode = null;

        var cut = Render<ApproveMergeModal>(p => p
            .Add(m => m.TaskId, 99)
            .Add(m => m.CurrentMergeMode, MergeMode.ManualReviewRequired)
            .Add(m => m.OnMergeModeChanged, (mode) => { changedMode = mode; }));

        var manualBtn = cut.Find("#btn-mergemode-manual");
        var autoBtn = cut.Find("#btn-mergemode-auto");

        Assert.Contains("forge-btn-primary", manualBtn.ClassName);
        Assert.Contains("forge-btn-secondary", autoBtn.ClassName);

        // Click auto merge
        autoBtn.Click();

        Assert.Equal(MergeMode.Automatic, changedMode);

        var updatedTask = await _taskRepository.GetByIdAsync(99);
        Assert.NotNull(updatedTask);
        Assert.Equal(MergeMode.Automatic, updatedTask.MergeMode);

        // UI updates to reflect new mode
        Assert.Contains("forge-btn-primary", autoBtn.ClassName);
        Assert.Contains("forge-btn-secondary", manualBtn.ClassName);

        // Verify history snapshot records AuditActors.User
        var history = await _taskRepository.GetHistoryAsync(99);
        Assert.NotEmpty(history);
        var lastRev = history.Last();
        Assert.Equal(AuditActors.User, lastRev.ChangedBy);
        Assert.Contains("Updated MergeMode to Automatic via ApproveMergeModal", lastRev.ChangeReason);
    }
}
