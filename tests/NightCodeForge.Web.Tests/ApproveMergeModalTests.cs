using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Review;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class ApproveMergeModalTests : BunitContext
{
    private readonly MockTaskOrchestrator _orchestrator;
    private readonly ToastService _toastService;

    public ApproveMergeModalTests()
    {
        _orchestrator = new MockTaskOrchestrator();
        _toastService = TestServices.CreateToastService();

        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
        Services.AddSingleton(_toastService);
        Services.AddSingleton<ITaskMutationService>(new Moq.Mock<ITaskMutationService>().Object);
    }

    [Fact]
    public void ApproveMergeModal_RendersTaskIdAndDefaultCommitMessage()
    {
        var cut = Render<ApproveMergeModal>(parameters => parameters
            .Add(p => p.TaskId, 77)
            .Add(p => p.BaseBranch, "develop")
            .Add(p => p.TaskTitle, "Feature X"));

        Assert.Contains("ForgeTask #77", cut.Markup);
        Assert.Contains("develop", cut.Markup);

        var input = cut.Find("#merge-commit-message");
        Assert.Equal("Merge task #77: Feature X", input.GetAttribute("value"));
    }

    [Fact]
    public void ApproveMergeModal_ConfirmMerge_CallsCompleteTaskAndCascade()
    {
        bool approved = false;

        var cut = Render<ApproveMergeModal>(parameters => parameters
            .Add(p => p.TaskId, 77)
            .Add(p => p.BaseBranch, "main")
            .Add(p => p.TaskTitle, "Feature X")
            .Add(p => p.OnApproved, () => approved = true));

        var confirmBtn = cut.Find("#btn-confirm-approve-merge");
        confirmBtn.Click();

        Assert.True(approved);
        Assert.NotNull(_orchestrator.LastCompleteTaskCall);
        Assert.Equal(77, _orchestrator.LastCompleteTaskCall.Value.TaskId);
        Assert.Equal("Merge task #77: Feature X", _orchestrator.LastCompleteTaskCall.Value.FinalCommitMessage);
        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Success);
    }

    [Fact]
    public void ApproveMergeModal_Cancel_InvokesOnClosed()
    {
        bool closed = false;

        var cut = Render<ApproveMergeModal>(parameters => parameters
            .Add(p => p.TaskId, 77)
            .Add(p => p.OnClosed, () => closed = true));

        cut.Find("#btn-cancel-approve").Click();
        Assert.True(closed);
    }

    [Fact]
    public void ApproveMergeModal_ConflictResolutionDispatched_ShowsToastAndClosesAndTriggersOnApproved()
    {
        bool approved = false;
        bool closed = false;

        _orchestrator.CompleteResult = new CompleteTaskResult(CompleteTaskStatus.ConflictResolutionDispatched);

        var cut = Render<ApproveMergeModal>(parameters => parameters
            .Add(p => p.TaskId, 77)
            .Add(p => p.BaseBranch, "main")
            .Add(p => p.TaskTitle, "Feature X")
            .Add(p => p.OnApproved, () => approved = true)
            .Add(p => p.OnClosed, () => closed = true));

        cut.Find("#btn-confirm-approve-merge").Click();

        Assert.True(approved);
        Assert.True(closed);
        Assert.Contains(_toastService.Toasts, t =>
            t.Level == ToastLevel.Warning &&
            t.Title == ConflictResolutionConstants.ConflictDispatchedToastTitle &&
            t.Message.Contains("main"));
    }

    [Fact]
    public void ApproveMergeModal_MirrorPushRejected_ShowsErrorToastAndDoesNotCloseOrApprove()
    {
        bool approved = false;
        bool closed = false;

        _orchestrator.CompleteTaskException = new MirrorPushRejectedException("main", "/test/repo", "[remote rejected] main -> main (fetch first)");

        var cut = Render<ApproveMergeModal>(parameters => parameters
            .Add(p => p.TaskId, 77)
            .Add(p => p.BaseBranch, "main")
            .Add(p => p.TaskTitle, "Feature X")
            .Add(p => p.OnApproved, () => approved = true)
            .Add(p => p.OnClosed, () => closed = true));

        cut.Find("#btn-confirm-approve-merge").Click();

        Assert.False(approved);
        Assert.False(closed);
        Assert.Contains(_toastService.Toasts, t =>
            t.Level == ToastLevel.Error &&
            t.Title == "Host Push Rejected" &&
            t.Message.Contains("fetch first"));
    }
}

