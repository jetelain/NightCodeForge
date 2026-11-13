using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tests;

public class WorkflowInteractionToolModuleTests
{
    [Fact]
    public void Constructors_And_IAgentToolModuleContract_ExposeInterruptionControllerAndFunctions()
    {
        var defaultModule = new WorkflowInteractionToolModule();
        Assert.Null(defaultModule.InterruptionController);

        Assert.Throws<ArgumentNullException>(() => new WorkflowInteractionToolModule(null!));

        using var controller = new ToolTurnInterruptionController();
        var moduleWithController = new WorkflowInteractionToolModule(controller);
        Assert.Same(controller, moduleWithController.InterruptionController);

        IAgentToolModule asInterface = defaultModule;
        asInterface.InterruptionController = controller;
        Assert.Same(controller, defaultModule.InterruptionController);

        var functions = defaultModule.CreateFunctions();
        Assert.Equal([CanonicalToolNames.Ask, CanonicalToolNames.RequestChanges], functions.Select(f => f.Name).ToArray());
    }

    [Fact]
    public async Task CreateFunctions_SchemaOnlyWithNullModule_ThrowsMissingModuleInstanceMessageOnInvoke()
    {
        var functions = WorkflowInteractionToolModule.CreateFunctions(module: null);
        var askFn = functions.Single(f => f.Name == CanonicalToolNames.Ask);
        var rcFn = functions.Single(f => f.Name == CanonicalToolNames.RequestChanges);

        var askEx = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await askFn.InvokeAsync(new AIFunctionArguments()));
        Assert.Equal(WorkflowInteractionToolModule.MissingModuleInstanceMessage, askEx.Message);

        var rcEx = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rcFn.InvokeAsync(new AIFunctionArguments
            {
                ["feedback"] = "Needs tests",
                ["reason"] = "Coverage"
            }));
        Assert.Equal(WorkflowInteractionToolModule.MissingModuleInstanceMessage, rcEx.Message);
    }

    [Fact]
    public async Task AskAsync_ValidatesEmptyAndBlankQuestions_BeforeInvokingHandlersOrThrowing()
    {
        var module = new WorkflowInteractionToolModule();

        var nullResult = await module.AskAsync(null);
        Assert.StartsWith("Error: At least one question must be provided", nullResult);

        var emptyResult = await module.AskAsync([]);
        Assert.StartsWith("Error: At least one question must be provided", emptyResult);

        var blankPromptResult = await module.AskAsync(
        [
            new QuestionItem("Valid question?"),
            new QuestionItem("   ")
        ]);
        Assert.Equal("Error: Question at index 1 has a blank or empty prompt.", blankPromptResult);
    }

    [Fact]
    public async Task AskAsync_SupportsAskHandler_InterruptionController_AndFallbackException()
    {
        var questions = new[] { new QuestionItem("Use gRPC or REST?", "Transport protocol", ["gRPC", "REST"]) };
        var module = new WorkflowInteractionToolModule();

        // 1. Neither AskHandler nor InterruptionController -> throws HumanInterruptionException
        var domainEx = await Assert.ThrowsAsync<HumanInterruptionException>(() => module.AskAsync(questions));
        Assert.Single(domainEx.Questions);
        Assert.Equal("Use gRPC or REST?", domainEx.Questions[0].Prompt);

        // 2. InterruptionController set (no AskHandler) -> triggers HumanClarificationInterruption and throws OperationCanceledException
        using var controller = new ToolTurnInterruptionController();
        module.InterruptionController = controller;
        await Assert.ThrowsAsync<OperationCanceledException>(() => module.AskAsync(questions));
        var captured = Assert.IsType<HumanClarificationInterruption>(controller.CapturedInterruption);
        Assert.Single(captured.Questions);
        Assert.Equal("Use gRPC or REST?", captured.Questions[0].Prompt);

        // 3. AskHandler set -> executes in-place without triggering InterruptionController
        using var controller2 = new ToolTurnInterruptionController();
        module.InterruptionController = controller2;
        module.AskHandler = (qs, _) => Task.FromResult($"Answered: {qs[0].Prompt} = gRPC");

        var askFn = module.CreateFunctions().Single(f => f.Name == CanonicalToolNames.Ask);
        var fnResult = await askFn.InvokeAsync(new AIFunctionArguments { ["questions"] = questions });
        Assert.Equal("Answered: Use gRPC or REST? = gRPC", fnResult?.ToString());
        Assert.False(controller2.HasInterruption);
    }

    [Fact]
    public async Task RequestChanges_InvokesCallback_AndSupportsControllerOrFallbackException()
    {
        var module = new WorkflowInteractionToolModule();
        string? callbackFeedback = null;
        string? callbackReason = null;
        module.RequestChangesHandler = (fb, r) =>
        {
            callbackFeedback = fb;
            callbackReason = r;
        };

        // 1. Without InterruptionController -> invokes callback and throws RequestChangesException
        var rcEx = Assert.Throws<RequestChangesException>(() => module.RequestChanges("Add null check", "NRE risk"));
        Assert.Equal("Add null check", rcEx.Feedback);
        Assert.Equal("NRE risk", rcEx.Reason);
        Assert.Equal("Add null check", callbackFeedback);
        Assert.Equal("NRE risk", callbackReason);

        // 2. With InterruptionController via AIFunction -> invokes callback and unwinds with ReviewerChangesRequestedInterruption
        using var controller = new ToolTurnInterruptionController();
        module.InterruptionController = controller;
        var rcFn = module.CreateFunctions().Single(f => f.Name == CanonicalToolNames.RequestChanges);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await rcFn.InvokeAsync(new AIFunctionArguments
            {
                ["feedback"] = "Fix SQL injection",
                ["reason"] = "Security"
            }));

        Assert.Equal("Fix SQL injection", callbackFeedback);
        Assert.Equal("Security", callbackReason);
        var captured = Assert.IsType<ReviewerChangesRequestedInterruption>(controller.CapturedInterruption);
        Assert.Equal("Fix SQL injection", captured.Feedback);
        Assert.Equal("Security", captured.Reason);
    }
}
