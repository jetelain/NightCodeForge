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
        _workerState = WorkerStateTestHelper.Create(new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsTelemetryService>().Object);

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

    private static IReadOnlyList<Question> CreateBatch() => new List<Question>
    {
        new() { Id = 101, TaskId = 20, BatchId = "b1", Prompt = "First?" },
        new() { Id = 102, TaskId = 20, BatchId = "b1", Prompt = "Second?" }
    };

    [Fact]
    public void BatchSubmit_WithPartialAnswers_ShowsWarning_AndDoesNotCallOrchestrator()
    {
        var cut = Render<QuestionCard>(p => p.Add(c => c.Questions, CreateBatch()));

        cut.Find("#clarification-input-101").Input("only first");
        cut.Find("#submit-all-btn-b1").Click();

        Assert.Null(_orchestrator.LastSubmitAnswersCall);
        Assert.Contains(_toastService.Toasts, t => t.Title == "Incomplete Answers");
    }

    [Fact]
    public void BatchSubmit_WithAllAnswers_CallsOrchestratorWithAllQuestionIds()
    {
        var cut = Render<QuestionCard>(p => p.Add(c => c.Questions, CreateBatch()));

        cut.Find("#clarification-input-101").Input("a1");
        cut.Find("#clarification-input-102").Input("a2");
        cut.Find("#submit-all-btn-b1").Click();

        Assert.NotNull(_orchestrator.LastSubmitAnswersCall);
        Assert.Equal(20, _orchestrator.LastSubmitAnswersCall.Value.TaskId);
        Assert.Equal("a1", _orchestrator.LastSubmitAnswersCall.Value.Answers[101]);
        Assert.Equal("a2", _orchestrator.LastSubmitAnswersCall.Value.Answers[102]);
    }

    [Fact]
    public void AgentQuestionTurnCard_WhenTaskIdProvided_RendersTaskScopedDataTestIds()
    {
        var cut = Render<NightCodeForge.Web.Components.Monitoring.AgentQuestionTurnCard>(p => p
            .Add(c => c.TaskId, 77)
            .Add(c => c.QuestionIds, new[] { 301, 302 })
            .Add(c => c.BatchIdentifier, "batch-77")
            .Add(c => c.IsWaitingForAnswer, true)
            .Add(c => c.Questions, new[]
            {
                new QuestionItem("First question?", "Rationale 1", new[] { "Opt A" }),
                new QuestionItem("Second question?", "Rationale 2")
            }));

        Assert.NotNull(cut.Find("[data-testid='question-card-77']"));
        Assert.NotNull(cut.Find("[data-testid='input-answer-301']"));
        Assert.NotNull(cut.Find("[data-testid='input-answer-302']"));
        Assert.NotNull(cut.Find("[data-testid='btn-submit-batch-77']"));
    }

    [Fact]
    public void AgentQuestionTurnCard_WhenTaskIdNull_FallsBackToCleanDomIdsWithoutTrailingHyphens()
    {
        var cut = Render<NightCodeForge.Web.Components.Monitoring.AgentQuestionTurnCard>(p => p
            .Add(c => c.TaskId, null)
            .Add(c => c.IsWaitingForAnswer, true)
            .Add(c => c.Questions, new[]
            {
                new QuestionItem("Inline turn question?")
            }));

        var card = cut.Find(".agent-question-turn-card");
        Assert.Equal("agent-question-card", card.GetAttribute("data-testid"));

        var submitBtn = cut.Find("button.submit-answer-btn");
        Assert.Equal("submit-answer-btn-0", submitBtn.GetAttribute("data-testid"));
    }
}
