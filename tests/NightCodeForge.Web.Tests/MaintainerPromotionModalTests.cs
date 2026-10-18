using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Backlog;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class MaintainerPromotionModalTests : BunitContext
{
    private readonly MockTaskOrchestrator _orchestrator;
    private readonly ToastService _toastService;

    public MaintainerPromotionModalTests()
    {
        _orchestrator = new MockTaskOrchestrator();
        _toastService = TestServices.CreateToastService();

        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
        Services.AddSingleton(_toastService);
    }

    [Fact]
    public void WhenNotOpen_DoesNotRenderDialog()
    {
        var task = new ForgeTask { Id = 10, Title = "Test Task" };
        var cut = Render<MaintainerPromotionModal>(p => p
            .Add(m => m.IsOpen, false)
            .Add(m => m.TargetTask, task));

        Assert.Empty(cut.FindAll(".forge-modal"));
    }

    [Fact]
    public void WhenOpen_RendersTaskDetailsAndInputs()
    {
        var task = new ForgeTask
        {
            Id = 42,
            Title = "Implement Deterministic Test Gate",
            Description = "Verify all bUnit tests pass deterministically.",
            Priority = 2,
            MergeMode = MergeMode.Automatic
        };

        var cut = Render<MaintainerPromotionModal>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.TargetTask, task));

        Assert.Contains("Promote ForgeTask #42: Implement Deterministic Test Gate", cut.Find("#promote-modal-title").TextContent);
        Assert.Contains("Verify all bUnit tests pass deterministically.", cut.Find(".task-description-box").TextContent);

        var prioritySelect = cut.Find("#priority-select");
        Assert.Equal("2", prioritySelect.GetAttribute("value"));

        var mergeModeSelect = cut.Find("#merge-mode-select");
        Assert.Equal(nameof(MergeMode.Automatic), mergeModeSelect.GetAttribute("value"));
    }

    [Fact]
    public void Promote_CallsOrchestratorWithOpenStatusAndSelectedOptions()
    {
        var task = new ForgeTask
        {
            Id = 15,
            Title = "Add Swagger Endpoints",
            Description = "Expose REST endpoints for telemetry.",
            Priority = 1,
            MergeMode = MergeMode.ManualReviewRequired
        };

        bool promotedInvoked = false;
        bool closedInvoked = false;

        var cut = Render<MaintainerPromotionModal>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.TargetTask, task)
            .Add(m => m.OnPromoted, () => { promotedInvoked = true; })
            .Add(m => m.OnClosed, () => { closedInvoked = true; }));

        // Change priority to 4
        var prioritySelect = cut.Find("#priority-select");
        prioritySelect.Change("4");

        // Click promote button
        var promoteBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Promote to Open"));
        promoteBtn.Click();

        Assert.NotNull(_orchestrator.LastPromotedCall);
        Assert.Equal(15, _orchestrator.LastPromotedCall.Value.TaskId);
        Assert.Equal(TaskOverallStatus.Open, _orchestrator.LastPromotedCall.Value.TargetStatus);
        Assert.Equal(4, _orchestrator.LastPromotedCall.Value.Priority);
        Assert.Equal(MergeMode.ManualReviewRequired, _orchestrator.LastPromotedCall.Value.MergeMode);

        Assert.True(promotedInvoked);
        Assert.True(closedInvoked);
    }

    [Fact]
    public void Reject_CallsOrchestratorWithRejectedStatus()
    {
        var task = new ForgeTask
        {
            Id = 99,
            Title = "Flaky Suggestion",
            Description = "Out of scope proposal.",
            Priority = 1
        };

        bool promotedInvoked = false;
        bool closedInvoked = false;

        var cut = Render<MaintainerPromotionModal>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.TargetTask, task)
            .Add(m => m.OnPromoted, () => { promotedInvoked = true; })
            .Add(m => m.OnClosed, () => { closedInvoked = true; }));

        var rejectBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Reject ForgeTask"));
        rejectBtn.Click();

        Assert.NotNull(_orchestrator.LastPromotedCall);
        Assert.Equal(99, _orchestrator.LastPromotedCall.Value.TaskId);
        Assert.Equal(TaskOverallStatus.Rejected, _orchestrator.LastPromotedCall.Value.TargetStatus);

        Assert.True(promotedInvoked);
        Assert.True(closedInvoked);
    }
}

