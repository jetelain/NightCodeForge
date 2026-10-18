using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Inbox;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class QuestionCardTests : BunitContext
{
    private readonly MockTaskOrchestrator _orchestrator;
    private readonly ToastService _toastService;
    private readonly WorkerStateService _workerState;

    public QuestionCardTests()
    {
        _orchestrator = new MockTaskOrchestrator();
        _toastService = TestServices.CreateToastService();
        _workerState = new WorkerStateService();

        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
        Services.AddSingleton(_toastService);
        Services.AddSingleton(_workerState);
    }

    [Fact]
    public void RendersCardWithPromptRationaleStageAndProfile()
    {
        var question = new Question
        {
            Id = 5,
            TaskId = 12,
            StageId = "ImplementationStage",
            AgentProfileId = "agent-backend-lead",
            Prompt = "Should we use EF Core migrations or raw SQL scripts?",
            Rationale = "Migration scripts provide precise control over production DDL changes.",
            ForgeTask = new ForgeTask
            {
                Id = 12,
                Title = "Implement Database Schema",
                Workspace = new Workspace { Id = 1, Name = "AlphaProject" }
            }
        };

        var cut = Render<QuestionCard>(p => p.Add(c => c.Question, question));

        Assert.Contains("Should we use EF Core migrations or raw SQL scripts?", cut.Find(".prompt-title").TextContent);
        Assert.Contains("Migration scripts provide precise control over production DDL changes.", cut.Find(".rationale-text").TextContent);
        Assert.Contains("ImplementationStage", cut.Find("#badge-stage-5").TextContent);
        Assert.Contains("agent-backend-lead", cut.Find("#badge-profile-5").TextContent);
        Assert.Contains("Implement Database Schema", cut.Find("#task-link-5").TextContent);
        Assert.Contains("AlphaProject", cut.Find(".badge-workspace").TextContent);
    }

    [Fact]
    public void MultipleChoiceOptions_RenderButtons_AndClickingSelectsOption()
    {
        var question = new Question
        {
            Id = 7,
            TaskId = 14,
            StageId = "ReviewStage",
            Prompt = "Choose cache persistence store:",
            OptionsJson = "[\"In-Memory Cache\", \"Redis Distributed Cache\"]"
        };

        var cut = Render<QuestionCard>(p => p.Add(c => c.Question, question));

        var optionButtons = cut.FindAll(".option-btn");
        Assert.Equal(2, optionButtons.Count);
        Assert.Contains("In-Memory Cache", optionButtons[0].TextContent);
        Assert.Contains("Redis Distributed Cache", optionButtons[1].TextContent);

        // Click Redis option
        optionButtons[1].Click();

        // Option 1 is active, textarea populated
        var updatedButtons = cut.FindAll(".option-btn");
        Assert.Contains("active", updatedButtons[1].ClassName);
        var textarea = cut.Find("textarea");
        Assert.Equal("Redis Distributed Cache", textarea.GetAttribute("value"));

        // Submit button enabled
        var submitBtn = cut.Find("#submit-answer-btn-7");
        Assert.False(submitBtn.HasAttribute("disabled"));
    }

    [Fact]
    public void CodeSnippetButton_InsertsMarkdownCodeSnippet()
    {
        var question = new Question
        {
            Id = 8,
            TaskId = 15,
            Prompt = "Provide example method signature"
        };

        var cut = Render<QuestionCard>(p => p.Add(c => c.Question, question));

        var snippetBtn = cut.Find(".snippet-helper-btn");
        snippetBtn.Click();

        var textarea = cut.Find("textarea");
        Assert.Contains("```csharp", textarea.GetAttribute("value") ?? "");
    }

    [Fact]
    public void SubmitButton_DisabledWhenEmpty_EnabledWhenAnswerProvided()
    {
        var question = new Question
        {
            Id = 9,
            TaskId = 16,
            Prompt = "Confirm package version"
        };

        var cut = Render<QuestionCard>(p => p.Add(c => c.Question, question));

        var submitBtn = cut.Find("#submit-answer-btn-9");
        Assert.True(submitBtn.HasAttribute("disabled"));

        var textarea = cut.Find("textarea");
        textarea.Input("Use version 10.0.0");

        Assert.False(submitBtn.HasAttribute("disabled"));
    }

    [Fact]
    public void SubmitAnswer_CallsOrchestrator_ShowsToast_UpdatesWorkerState_AndInvokesCallback()
    {
        var question = new Question
        {
            Id = 42,
            TaskId = 10,
            Prompt = "Which port should be opened?"
        };

        _workerState.UpdatePendingQuestions(3);

        int submittedQuestionId = 0;
        var cut = Render<QuestionCard>(p => p
            .Add(c => c.Question, question)
            .Add(c => c.OnAnswerSubmitted, id => submittedQuestionId = id));

        var textarea = cut.Find("textarea");
        textarea.Input("Port 5001 with HTTPS");

        var submitBtn = cut.Find("#submit-answer-btn-42");
        submitBtn.Click();

        // Orchestrator called
        Assert.NotNull(_orchestrator.LastSubmitHumanAnswerCall);
        Assert.Equal(42, _orchestrator.LastSubmitHumanAnswerCall.Value.QuestionId);
        Assert.Equal("Port 5001 with HTTPS", _orchestrator.LastSubmitHumanAnswerCall.Value.Answer);

        // Toast shown
        Assert.Contains(_toastService.Toasts, t => t.Title == "Clarification Submitted" && t.Message.Contains("llm-urgent"));

        // WorkerState decremented
        Assert.Equal(2, _workerState.Current.PendingQuestionCount);

        // Callback invoked
        Assert.Equal(42, submittedQuestionId);
    }
}
