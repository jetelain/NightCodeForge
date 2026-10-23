using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class InboxPageTests : BunitContext
{
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly WorkspaceStateService _workspaceState;
    private readonly WorkerStateService _workerState;
    private readonly ToastService _toastService;
    private readonly MockTaskOrchestrator _orchestrator;

    public InboxPageTests()
    {
        _taskRepository = new InMemoryTaskRepository();
        _workspaceState = new WorkspaceStateService();
        _workerState = new WorkerStateService(new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsTelemetryService>().Object);
        _toastService = TestServices.CreateToastService();
        _orchestrator = new MockTaskOrchestrator();

        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton(_workspaceState);
        Services.AddSingleton(_workerState);
        Services.AddSingleton(_toastService);
        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
    }

    [Fact]
    public void EmptyInbox_RendersAllClearState()
    {
        var cut = Render<Inbox>();

        Assert.NotNull(cut.Find("#inbox-empty-state"));
        Assert.Contains("All clear! No questions pending.", cut.Find("#inbox-empty-state").TextContent);
        Assert.Equal(0, _workerState.Current.PendingQuestionCount);
    }

    [Fact]
    public async Task PendingQuestions_RendersQuestionCards()
    {
        var task = new ForgeTask
        {
            Id = 1,
            Title = "Implement API Gateway",
            Questions = new List<Question>
            {
                new()
                {
                    Id = 10,
                    TaskId = 1,
                    Prompt = "Which rate limiting algorithm to use?",
                    Status = QuestionStatus.Pending
                },
                new()
                {
                    Id = 11,
                    TaskId = 1,
                    Prompt = "What should the JWT expiration window be?",
                    Status = QuestionStatus.Pending
                }
            }
        };

        await _taskRepository.CreateAsync(task);

        var cut = Render<Inbox>();

        var cards = cut.FindAll(".question-card");
        Assert.Equal(2, cards.Count);
        Assert.Contains("Which rate limiting algorithm to use?", cut.Markup);
        Assert.Contains("What should the JWT expiration window be?", cut.Markup);
        Assert.Equal(2, _workerState.Current.PendingQuestionCount);
    }

    [Fact]
    public async Task WorkspaceFilter_FiltersQuestionsToActiveWorkspace()
    {
        var ws1 = new Workspace { Id = 1, Name = "Workspace One" };
        var ws2 = new Workspace { Id = 2, Name = "Workspace Two" };
        _workspaceState.SetActiveWorkspace(ws1);

        var task1 = new ForgeTask
        {
            Id = 101,
            WorkspaceId = 1,
            Title = "Task in WS 1",
            Questions = new List<Question>
            {
                new()
                {
                    Id = 201,
                    TaskId = 101,
                    Prompt = "Question for WS 1",
                    Status = QuestionStatus.Pending
                }
            }
        };

        var task2 = new ForgeTask
        {
            Id = 102,
            WorkspaceId = 2,
            Title = "Task in WS 2",
            Questions = new List<Question>
            {
                new()
                {
                    Id = 202,
                    TaskId = 102,
                    Prompt = "Question for WS 2",
                    Status = QuestionStatus.Pending
                }
            }
        };

        await _taskRepository.CreateAsync(task1);
        await _taskRepository.CreateAsync(task2);

        var cut = Render<Inbox>();

        // Initially unchecked: both questions render
        var cards = cut.FindAll(".question-card");
        Assert.Equal(2, cards.Count);

        // Check filter checkbox
        var checkbox = cut.Find("#filter-workspace-checkbox");
        checkbox.Change(true);

        // Now only Question for WS 1 renders
        cards = cut.FindAll(".question-card");
        Assert.Single(cards);
        Assert.Contains("Question for WS 1", cut.Markup);
        Assert.DoesNotContain("Question for WS 2", cut.Markup);
    }

    [Fact]
    public async Task AnswerSubmitted_RemovesCardAndUpdatesCount()
    {
        var task = new ForgeTask
        {
            Id = 50,
            Title = "Implement OAuth Integration",
            Questions = new List<Question>
            {
                new()
                {
                    Id = 501,
                    TaskId = 50,
                    Prompt = "Which OAuth provider is required?",
                    Status = QuestionStatus.Pending
                }
            }
        };

        await _taskRepository.CreateAsync(task);

        var cut = Render<Inbox>();

        Assert.Single(cut.FindAll(".question-card"));

        var textarea = cut.Find("textarea");
        textarea.Input("GitHub and Google OAuth");

        var submitBtn = cut.Find("#submit-answer-btn-501");
        submitBtn.Click();

        // Card is removed and empty state is rendered
        Assert.Empty(cut.FindAll(".question-card"));
        Assert.NotNull(cut.Find("#inbox-empty-state"));
        Assert.Equal(0, _workerState.Current.PendingQuestionCount);
    }

    [Fact]
    public async Task BatchedQuestions_RenderGroupedCard_SubmitAllAnswersBatch()
    {
        var batchId = "batch-xyz-123";
        var task = new ForgeTask
        {
            Id = 1,
            Title = "Implement Microservices",
            Questions = new List<Question>
            {
                new()
                {
                    Id = 20,
                    TaskId = 1,
                    BatchId = batchId,
                    Prompt = "Which service discovery to use?",
                    OptionsJson = "[\"Consul\",\"Eureka\"]",
                    QuestionType = QuestionType.SingleChoice,
                    Status = QuestionStatus.Pending
                },
                new()
                {
                    Id = 21,
                    TaskId = 1,
                    BatchId = batchId,
                    Prompt = "What should the health check interval be?",
                    QuestionType = QuestionType.FreeText,
                    Status = QuestionStatus.Pending
                }
            }
        };

        await _taskRepository.CreateAsync(task);

        var cut = Render<Inbox>();

        // Grouped under 1 card for the batch
        var cards = cut.FindAll(".question-card");
        Assert.Single(cards);

        // Header shows batched questions count badge
        Assert.Contains("Batch (2 questions)", cut.Markup);
        Assert.Contains("Q1", cut.Markup);
        Assert.Contains("Q2", cut.Markup);
        Assert.Contains("Which service discovery to use?", cut.Markup);
        Assert.Contains("What should the health check interval be?", cut.Markup);

        // Option buttons rendered for question 20
        var optionBtns = cut.FindAll(".option-btn");
        Assert.Equal(2, optionBtns.Count);
        Assert.Contains("Consul", optionBtns[0].TextContent);
        Assert.Contains("Eureka", optionBtns[1].TextContent);

        // Click option button for Question 20
        optionBtns[0].Click();

        // Type answer for Question 21 using its specific input ID
        var textarea21 = cut.Find("#clarification-input-21");
        textarea21.Input("15 seconds");

        // Submit all button
        var submitAllBtn = cut.Find("#submit-all-btn-" + batchId);
        Assert.False(submitAllBtn.HasAttribute("disabled"));
        submitAllBtn.Click();

        // Verify orchestrator received answers
        Assert.NotNull(_orchestrator.LastSubmitAnswersCall);
        Assert.Equal(1, _orchestrator.LastSubmitAnswersCall.Value.TaskId);
        Assert.Equal("Consul", _orchestrator.LastSubmitAnswersCall.Value.Answers[20]);
        Assert.Equal("15 seconds", _orchestrator.LastSubmitAnswersCall.Value.Answers[21]);

        // Card is removed and empty state rendered
        Assert.Empty(cut.FindAll(".question-card"));
        Assert.NotNull(cut.Find("#inbox-empty-state"));
        Assert.Equal(0, _workerState.Current.PendingQuestionCount);
    }
}
