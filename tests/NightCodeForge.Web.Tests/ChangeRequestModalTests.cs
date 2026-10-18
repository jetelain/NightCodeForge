using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Review;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class ChangeRequestModalTests : BunitContext
{
    private readonly MockTaskOrchestrator _orchestrator;
    private readonly ReviewCommentStateService _commentState;
    private readonly ToastService _toastService;

    public ChangeRequestModalTests()
    {
        _orchestrator = new MockTaskOrchestrator();
        _commentState = new ReviewCommentStateService();
        _toastService = TestServices.CreateToastService();

        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
        Services.AddSingleton(_commentState);
        Services.AddSingleton(_toastService);
    }

    [Fact]
    public void ChangeRequestModal_RendersTaskIdAndDraftedComments()
    {
        _commentState.AddComment("src/Api.cs", 15, "public void Auth()", "Missing null check");

        var cut = Render<ChangeRequestModal>(parameters => parameters
            .Add(p => p.TaskId, 42));

        Assert.Contains("ForgeTask #42", cut.Markup);
        Assert.Contains("src/Api.cs:15", cut.Markup);
        Assert.Contains("Missing null check", cut.Markup);
        Assert.Contains("Agent Feedback Payload Preview:", cut.Markup);
    }

    [Fact]
    public void ChangeRequestModal_ValidationFails_WhenEmpty()
    {
        var cut = Render<ChangeRequestModal>(parameters => parameters
            .Add(p => p.TaskId, 42));

        var submitBtn = cut.Find("#btn-submit-change-request");
        submitBtn.Click();

        Assert.Null(_orchestrator.LastRequestChangesCall);
        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Warning);
    }

    [Fact]
    public void ChangeRequestModal_SubmitsFeedback_AndDispatchesToOrchestrator()
    {
        _commentState.AddComment("src/Api.cs", 15, "public void Auth()", "Missing null check");
        bool submitted = false;

        var cut = Render<ChangeRequestModal>(parameters => parameters
            .Add(p => p.TaskId, 42)
            .Add(p => p.OnSubmitted, () => submitted = true));

        // Add general note
        var textarea = cut.Find("#review-general-notes");
        textarea.Change("Overall architecture needs refactoring.");

        // Click submit
        var submitBtn = cut.Find("#btn-submit-change-request");
        submitBtn.Click();

        Assert.True(submitted);
        Assert.NotNull(_orchestrator.LastRequestChangesCall);
        Assert.Equal(42, _orchestrator.LastRequestChangesCall.Value.TaskId);

        var feedback = _orchestrator.LastRequestChangesCall.Value.RevisionNotes;
        Assert.Contains("### Reviewer Overall Instructions:", feedback);
        Assert.Contains("Overall architecture needs refactoring.", feedback);
        Assert.Contains("### Inline Code Review Comments:", feedback);
        Assert.Contains("Missing null check", feedback);

        // Verify comments were cleared after successful submission
        Assert.Equal(0, _commentState.CommentCount);
    }

    [Fact]
    public void ChangeRequestModal_Close_InvokesOnClosed()
    {
        bool closed = false;
        var cut = Render<ChangeRequestModal>(parameters => parameters
            .Add(p => p.TaskId, 42)
            .Add(p => p.OnClosed, () => closed = true));

        cut.Find("#btn-modal-close").Click();
        Assert.True(closed);
    }
}
