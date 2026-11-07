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
        _workerState = WorkerStateTestHelper.Create(new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsTelemetryService>().Object);
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
    public async Task TaskIdQuery_DeepLinksToTaskQuestionCard_HighlightsAndScrolls()
    {
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 301,
            Title = "Other task",
            Questions = new List<Question>
            {
                new() { Id = 401, TaskId = 301, Prompt = "Other question", Status = QuestionStatus.Pending }
            }
        });
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 302,
            Title = "Deep-linked task",
            Questions = new List<Question>
            {
                new() { Id = 402, TaskId = 302, Prompt = "Deep-linked question", Status = QuestionStatus.Pending }
            }
        });
        JSInterop.SetupVoid("NightCodeForge.scrollToElement", "question-card-task-302");

        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/inbox?taskId=302");
        var cut = Render<Inbox>();

        var anchor = cut.Find("#question-card-task-302");
        Assert.Contains("deep-link-highlight", anchor.ClassName);
        Assert.Contains("Deep-linked question", anchor.TextContent);
        Assert.Single(cut.FindAll(".deep-link-highlight"));
        JSInterop.VerifyInvoke("NightCodeForge.scrollToElement");
    }

    [Fact]
    public async Task TaskIdQuery_OverridesActiveWorkspaceFilter_SoDeepLinkedQuestionIsVisible()
    {
        _workspaceState.SetActiveWorkspace(new Workspace { Id = 1, Name = "Workspace One" });
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 303,
            WorkspaceId = 2,
            Title = "Task in other workspace",
            Questions = new List<Question>
            {
                new() { Id = 403, TaskId = 303, Prompt = "Cross-workspace question", Status = QuestionStatus.Pending }
            }
        });
        JSInterop.SetupVoid("NightCodeForge.scrollToElement", "question-card-task-303");

        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/inbox?taskId=303");
        var cut = Render<Inbox>();

        Assert.Contains("Cross-workspace question", cut.Find("#question-card-task-303").TextContent);
    }

    [Fact]
    public async Task TaskIdQuery_NavigatedAfterFilterChecked_ResetsWorkspaceFilterAndReloadsDeepLinkedQuestion()
    {
        var ws1 = new Workspace { Id = 1, Name = "Workspace One" };
        var ws2 = new Workspace { Id = 2, Name = "Workspace Two" };
        _workspaceState.SetActiveWorkspace(ws1);

        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 304,
            WorkspaceId = 1,
            Title = "Task in WS 1",
            Questions = new List<Question>
            {
                new() { Id = 404, TaskId = 304, Prompt = "WS 1 question", Status = QuestionStatus.Pending }
            }
        });
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 305,
            WorkspaceId = 2,
            Title = "Task in WS 2",
            Questions = new List<Question>
            {
                new() { Id = 405, TaskId = 305, Prompt = "WS 2 deep-linked question", Status = QuestionStatus.Pending }
            }
        });
        JSInterop.SetupVoid("NightCodeForge.scrollToElement", "question-card-task-305");

        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var cut = Render<Inbox>();

        // User checks "Filter to Active Workspace Only" -> WS 2 question is hidden
        cut.Find("#filter-workspace-checkbox").Change(true);
        Assert.DoesNotContain("WS 2 deep-linked question", cut.Markup);

        // Navigating to /inbox?taskId=305 on the already-mounted Inbox component must reset the filter and load WS 2 question
        nav.NavigateTo("/inbox?taskId=305");
        cut.WaitForAssertion(() =>
        {
            var anchor = cut.Find("#question-card-task-305");
            Assert.Contains("WS 2 deep-linked question", anchor.TextContent);
        });
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

    [Fact]
    public async Task WorkspaceFilter_DoesNotOverwriteGlobalPendingQuestionCount()
    {
        var ws1 = new Workspace { Id = 1, Name = "Workspace One" };
        var ws2 = new Workspace { Id = 2, Name = "Workspace Two" };
        _workspaceState.SetActiveWorkspace(ws1);

        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 601,
            WorkspaceId = 1,
            Title = "Task in WS 1",
            Questions = new List<Question>
            {
                new() { Id = 701, TaskId = 601, Prompt = "WS 1 question", Status = QuestionStatus.Pending }
            }
        });
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 602,
            WorkspaceId = 2,
            Title = "Task in WS 2",
            Questions = new List<Question>
            {
                new() { Id = 702, TaskId = 602, Prompt = "WS 2 question", Status = QuestionStatus.Pending }
            }
        });

        var cut = Render<Inbox>();

        // Total questions across workspaces = 2
        Assert.Equal(2, _workerState.Current.PendingQuestionCount);
        Assert.Equal(2, cut.FindAll(".question-card").Count);

        // Check filter workspace
        cut.Find("#filter-workspace-checkbox").Change(true);

        // Only 1 question card rendered in filtered view
        Assert.Single(cut.FindAll(".question-card"));

        // Global PendingQuestionCount must remain 2
        Assert.Equal(2, _workerState.Current.PendingQuestionCount);
    }

    [Fact]
    public async Task ExternalPendingQuestionCountChange_ReloadsInboxAutomatically()
    {
        var ws1 = new Workspace { Id = 1, Name = "Workspace One" };
        _workspaceState.SetActiveWorkspace(ws1);

        var task = new ForgeTask
        {
            Id = 801,
            WorkspaceId = 1,
            Title = "Task 801",
            Questions = new List<Question>()
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<Inbox>();
        Assert.NotNull(cut.Find("#inbox-empty-state"));

        // New question added externally to DB
        task.Questions.Add(new Question
        {
            Id = 901,
            TaskId = 801,
            Prompt = "External Question?",
            Status = QuestionStatus.Pending
        });
        await _taskRepository.UpdateAsync(task);

        // Signal external question count change
        await cut.InvokeAsync(() => _workerState.UpdatePendingQuestions(1));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("External Question?", cut.Markup);
            Assert.Single(cut.FindAll(".question-card"));
        });
    }

    [Fact]
    public async Task AnswerSubmitted_WhenMultipleQuestionsExist_DecrementsCountByExactAmount_WithoutDoubleDecrement()
    {
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 910,
            WorkspaceId = 1,
            Title = "Task One",
            Status = TaskOverallStatus.Open,
            Questions = new List<Question>
            {
                new() { Id = 1001, TaskId = 910, Prompt = "First question?", Status = QuestionStatus.Pending }
            }
        });
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 911,
            WorkspaceId = 1,
            Title = "Task Two",
            Status = TaskOverallStatus.Open,
            Questions = new List<Question>
            {
                new() { Id = 1002, TaskId = 911, BatchId = "batch-911", Prompt = "Batch Q1?", Status = QuestionStatus.Pending },
                new() { Id = 1003, TaskId = 911, BatchId = "batch-911", Prompt = "Batch Q2?", Status = QuestionStatus.Pending }
            }
        });

        var cut = Render<Inbox>();
        Assert.Equal(3, _workerState.Current.PendingQuestionCount);
        Assert.Equal(2, cut.FindAll(".question-card").Count);

        // Answer the single question (1001): count must drop from 3 to 2 (not 1 or 0)
        cut.Find("#clarification-input-1001").Input("Single answer");
        cut.Find("#submit-answer-btn-1001").Click();

        Assert.Equal(2, _workerState.Current.PendingQuestionCount);
        Assert.Single(cut.FindAll(".question-card"));

        // Add another pending question to verify batch submission also decrements by exact batch size (2), leaving 1
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 912,
            WorkspaceId = 2,
            Title = "Task Three",
            Status = TaskOverallStatus.Open,
            Questions = new List<Question>
            {
                new() { Id = 1004, TaskId = 912, Prompt = "Remaining question in WS2?", Status = QuestionStatus.Pending }
            }
        });
        await cut.InvokeAsync(() => _workerState.UpdatePendingQuestions(3));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".question-card").Count));

        cut.Find("#clarification-input-1002").Input("Batch answer 1");
        cut.Find("#clarification-input-1003").Input("Batch answer 2");
        cut.Find("#submit-all-btn-batch-911").Click();

        Assert.Equal(1, _workerState.Current.PendingQuestionCount);
        Assert.Single(cut.FindAll(".question-card"));
    }

    [Fact]
    public async Task LoadQuestions_ExcludesPendingQuestionsFromTerminalTasks()
    {
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 920,
            WorkspaceId = 1,
            Title = "Cancelled Task With Orphaned Question",
            Status = TaskOverallStatus.Cancelled,
            Questions = new List<Question>
            {
                new() { Id = 1010, TaskId = 920, Prompt = "Orphaned cancelled question?", Status = QuestionStatus.Pending }
            }
        });
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 921,
            WorkspaceId = 1,
            Title = "Closed Task With Orphaned Question",
            Status = TaskOverallStatus.Closed,
            Questions = new List<Question>
            {
                new() { Id = 1011, TaskId = 921, Prompt = "Orphaned closed question?", Status = QuestionStatus.Pending }
            }
        });
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 922,
            WorkspaceId = 1,
            Title = "Active Open Task",
            Status = TaskOverallStatus.Open,
            Questions = new List<Question>
            {
                new() { Id = 1012, TaskId = 922, Prompt = "Valid pending question?", Status = QuestionStatus.Pending }
            }
        });

        var cut = Render<Inbox>();

        Assert.Equal(1, _workerState.Current.PendingQuestionCount);
        Assert.Single(cut.FindAll(".question-card"));
        Assert.Contains("Valid pending question?", cut.Markup);
        Assert.DoesNotContain("Orphaned cancelled question?", cut.Markup);
        Assert.DoesNotContain("Orphaned closed question?", cut.Markup);
    }
}
