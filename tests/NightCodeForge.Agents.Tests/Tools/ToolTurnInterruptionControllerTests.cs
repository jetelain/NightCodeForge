using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tests;

public class ToolTurnInterruptionControllerTests
{
    [Fact]
    public void DefaultConstructor_InitializesUncancelledTokenAndNoCapturedInterruption()
    {
        using var controller = new ToolTurnInterruptionController();

        Assert.False(controller.HasInterruption);
        Assert.Null(controller.CapturedInterruption);
        Assert.False(controller.InterruptionToken.IsCancellationRequested);
    }

    [Fact]
    public void LinkedTokenConstructor_PropagatesExternalCancellation_WithoutRecordingToolInterruption()
    {
        using var externalCts = new CancellationTokenSource();
        using var controller = new ToolTurnInterruptionController(externalCts.Token);

        Assert.False(controller.InterruptionToken.IsCancellationRequested);

        externalCts.Cancel();

        Assert.True(controller.InterruptionToken.IsCancellationRequested);
        Assert.False(controller.HasInterruption);
        Assert.Null(controller.CapturedInterruption);
    }

    [Fact]
    public void TriggerTerminalInterruption_WithNullInterruption_ThrowsArgumentNullException()
    {
        using var controller = new ToolTurnInterruptionController();

        Assert.Throws<ArgumentNullException>(() => controller.TriggerTerminalInterruption(null!));
        Assert.False(controller.HasInterruption);
        Assert.False(controller.InterruptionToken.IsCancellationRequested);
    }

    [Fact]
    public void TriggerTerminalInterruption_CapturesHumanClarification_CancelsToken_AndThrowsOperationCanceledException()
    {
        using var controller = new ToolTurnInterruptionController();
        var questions = new List<Question>
        {
            new() { Prompt = "Which auth flow?", Rationale = "OAuth2 vs OIDC" }
        };
        var interruption = new HumanClarificationInterruption(questions);

        var ex = Assert.Throws<OperationCanceledException>(() => controller.TriggerTerminalInterruption(interruption));

        Assert.Equal(controller.InterruptionToken, ex.CancellationToken);
        Assert.True(controller.InterruptionToken.IsCancellationRequested);
        Assert.True(controller.HasInterruption);
        var captured = Assert.IsType<HumanClarificationInterruption>(controller.CapturedInterruption);
        Assert.Same(questions, captured.Questions);
    }

    [Fact]
    public void TriggerTerminalInterruption_CapturesReviewerChangesRequested_WithDefaultAndCustomTargetStage()
    {
        using var controller1 = new ToolTurnInterruptionController();
        var defaultTarget = new ReviewerChangesRequestedInterruption("Fix race condition", "Concurrency bug");

        Assert.Throws<OperationCanceledException>(() => controller1.TriggerTerminalInterruption(defaultTarget));
        var captured1 = Assert.IsType<ReviewerChangesRequestedInterruption>(controller1.CapturedInterruption);
        Assert.Equal("Fix race condition", captured1.Feedback);
        Assert.Equal("Concurrency bug", captured1.Reason);
        Assert.Equal(StandardStageIds.Implementation, captured1.TargetStageId);

        using var controller2 = new ToolTurnInterruptionController();
        var customTarget = new ReviewerChangesRequestedInterruption("Revise spec", "Ambiguous scope", StandardStageIds.TaskReview);

        Assert.Throws<OperationCanceledException>(() => controller2.TriggerTerminalInterruption(customTarget));
        var captured2 = Assert.IsType<ReviewerChangesRequestedInterruption>(controller2.CapturedInterruption);
        Assert.Equal(StandardStageIds.TaskReview, captured2.TargetStageId);
    }

    [Fact]
    public void TriggerTerminalInterruption_PreservesFirstInterruptionWhenTriggeredMultipleTimes_AndRemainsSafeAfterDispose()
    {
        var controller = new ToolTurnInterruptionController();
        var first = new ReviewerChangesRequestedInterruption("First feedback", "First reason");
        var second = new ReviewerChangesRequestedInterruption("Second feedback", "Second reason");

        Assert.Throws<OperationCanceledException>(() => controller.TriggerTerminalInterruption(first));
        Assert.Same(first, controller.CapturedInterruption);

        // Second call before Dispose keeps the first captured interruption
        Assert.Throws<OperationCanceledException>(() => controller.TriggerTerminalInterruption(second));
        Assert.Same(first, controller.CapturedInterruption);

        // Dispose is idempotent and subsequent Cancel / TriggerTerminalInterruption do not throw ObjectDisposedException
        controller.Dispose();
        controller.Dispose();
        controller.Cancel();

        Assert.Throws<OperationCanceledException>(() => controller.TriggerTerminalInterruption(second));
        Assert.Same(first, controller.CapturedInterruption);
    }

    [Fact]
    public void Cancel_CancelsInterruptionTokenWithoutSettingCapturedInterruption()
    {
        using var controller = new ToolTurnInterruptionController();

        controller.Cancel();

        Assert.True(controller.InterruptionToken.IsCancellationRequested);
        Assert.False(controller.HasInterruption);
        Assert.Null(controller.CapturedInterruption);
    }
}
