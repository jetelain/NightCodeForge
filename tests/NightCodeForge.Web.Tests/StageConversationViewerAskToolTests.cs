using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Monitoring;
using NightCodeForge.Web.Components.Transcripts;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class StageConversationViewerAskToolTests : BunitContext
{
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly MockTaskOrchestrator _orchestrator;

    public StageConversationViewerAskToolTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _taskRepository = new InMemoryTaskRepository();
        _orchestrator = new MockTaskOrchestrator();

        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
    }

    [Fact]
    public void StageConversationViewer_RendersAgentQuestionTurnCard_ForAskToolTurns()
    {
        var conv = new Conversation
        {
            Id = 1,
            TaskId = 10,
            StageId = "task-review",
            Status = StageExecutionStatus.WaitingForHuman,
            Turns = new List<ConversationTurn>
            {
                new()
                {
                    Id = 101,
                    TurnIndex = 0,
                    Role = StageTurnRole.User,
                    Content = "Review task requirements"
                },
                new()
                {
                    Id = 102,
                    TurnIndex = 1,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Ask,
                    ToolArguments = "{\"questions\":[{\"prompt\":\"Should we use PostgreSQL or SQLite?\",\"rationale\":\"Needed for DB design\",\"options\":[\"PostgreSQL\",\"SQLite\"]}]}",
                    MetadataJson = "{\"CallId\":\"call-123\"}"
                }
            }
        };

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv)
            .Add(x => x.IsActiveStage, false));

        // Must render the specialized AgentQuestionTurnCard component
        var card = cut.FindComponent<AgentQuestionTurnCard>();
        Assert.NotNull(card);

        // Header badge must display "Waiting for Answer"
        Assert.Contains("Waiting for Answer", cut.Markup);
        Assert.Contains("Agent Clarification Request", cut.Markup);
        Assert.Contains("Should we use PostgreSQL or SQLite?", cut.Markup);
        Assert.Contains("Needed for DB design", cut.Markup);
        Assert.Contains("PostgreSQL", cut.Markup);
        Assert.Contains("SQLite", cut.Markup);
    }

    [Fact]
    public void StageConversationViewer_FailedAskTool_RendersToolCallCardInsteadOfQuestionTurnCard()
    {
        var conv = new Conversation
        {
            Id = 3,
            TaskId = 10,
            StageId = "task-review",
            Status = StageExecutionStatus.Failed,
            Turns = new List<ConversationTurn>
            {
                new()
                {
                    Id = 301,
                    TurnIndex = 0,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Ask,
                    ToolArguments = "{\"questions\":[]}",
                    MetadataJson = "{\"CallId\":\"call-err-1\"}"
                },
                new()
                {
                    Id = 302,
                    TurnIndex = 1,
                    Role = StageTurnRole.ToolResult,
                    ToolName = CanonicalToolNames.Ask,
                    ToolResult = "Error: At least one question must be provided in 'questions'. Consolidate all clarifying questions into a list.",
                    IsError = true,
                    MetadataJson = "{\"CallId\":\"call-err-1\"}"
                }
            }
        };

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv)
            .Add(x => x.IsActiveStage, false));

        // Must NOT render AgentQuestionTurnCard
        var questionCards = cut.FindComponents<AgentQuestionTurnCard>();
        Assert.Empty(questionCards);

        // Must render standard ToolCallCard with error state
        var toolCard = cut.FindComponent<ToolCallCard>();
        Assert.NotNull(toolCard);
        Assert.True(toolCard.Instance.IsError);
        Assert.Contains("Error: At least one question must be provided", cut.Markup);
    }

    [Fact]
    public void StageConversationViewer_AnsweredQuestions_RendersSelectedOptionAndAnswerText()
    {
        var conv = new Conversation
        {
            Id = 2,
            TaskId = 10,
            StageId = "task-review",
            Status = StageExecutionStatus.Completed,
            Turns = new List<ConversationTurn>
            {
                new()
                {
                    Id = 201,
                    TurnIndex = 0,
                    Role = StageTurnRole.User,
                    Content = "Review task requirements"
                },
                new()
                {
                    Id = 202,
                    TurnIndex = 1,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Ask,
                    ToolArguments = "{\"questions\":[{\"prompt\":\"Choose a database:\",\"options\":[\"PostgreSQL\",\"SQLite\"]}]}",
                    MetadataJson = "{\"CallId\":\"call-456\"}"
                },
                new()
                {
                    Id = 203,
                    TurnIndex = 2,
                    Role = StageTurnRole.ToolResult,
                    ToolName = CanonicalToolNames.Ask,
                    ToolResult = "PostgreSQL",
                    MetadataJson = "{\"CallId\":\"call-456\",\"Answers\":[{\"QuestionIndex\":0,\"SelectedOptionIndex\":0,\"SelectedOption\":\"PostgreSQL\",\"AnswerText\":\"PostgreSQL\"}]}"
                }
            }
        };

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv)
            .Add(x => x.IsActiveStage, false));

        var card = cut.FindComponent<AgentQuestionTurnCard>();
        Assert.NotNull(card);

        Assert.Contains("Clarification Provided", cut.Markup);
        Assert.Contains("Choose a database:", cut.Markup);
        Assert.Contains("User Response", cut.Markup);
        Assert.Contains("PostgreSQL", cut.Markup);
        Assert.Contains("answered-selected", cut.Markup);
    }

    [Fact]
    public void AgentQuestionTurnCard_SelectingOptionAndSubmitting_InvokesCallback()
    {
        var questions = new[]
        {
            new QuestionItem("Pick a color:", "Visual preference", new[] { "Indigo", "Teal", "Crimson" })
        };

        string? submittedAnswer = null;

        var cut = Render<AgentQuestionTurnCard>(p => p
            .Add(x => x.Questions, questions)
            .Add(x => x.IsWaitingForAnswer, true)
            .Add(x => x.OnAnswerSubmitted, (string ans) => submittedAnswer = ans));

        // Click the "Teal" chip
        var tealBtn = cut.FindAll("button.agent-option-chip").First(b => b.TextContent.Trim() == "Teal");
        tealBtn.Click();

        // Submit button should now be enabled
        var submitBtn = cut.Find("button.forge-btn-primary");
        Assert.False(submitBtn.HasAttribute("disabled"));

        submitBtn.Click();

        Assert.Equal("Teal", submittedAnswer);
    }

    [Fact]
    public async Task StageConversationViewer_SubmittingAnswer_InvokesOrchestratorWithResolvedQuestionId()
    {
        var task = new ForgeTask
        {
            Id = 15,
            Title = "Task with ask question",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            CurrentStageId = "task-review"
        };
        var q = new Question
        {
            Id = 105,
            TaskId = task.Id,
            StageId = "task-review",
            Prompt = "Target database?",
            Status = QuestionStatus.Pending,
            BatchId = "batch-105"
        };
        task.Questions.Add(q);
        _taskRepository.AddQuestion(q);
        await _taskRepository.CreateAsync(task);

        var conv = await _taskRepository.StartConversationAsync(task.Id, "task-review", WorkflowStageKind.Agentic, 1);
        await _taskRepository.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Tool,
            toolName: CanonicalToolNames.Ask,
            toolArguments: "{\"questions\":[{\"prompt\":\"Target database?\",\"options\":[\"PostgreSQL\",\"SQLite\"]}]}",
            metadataJson: "{\"CallId\":\"call-db-1\"}");
        await _taskRepository.CompleteConversationAsync(conv.Id, StageExecutionStatus.WaitingForHuman);

        var loadedConv = await _taskRepository.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(loadedConv);

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, loadedConv)
            .Add(x => x.IsActiveStage, false));

        // Click SQLite option chip
        var sqliteBtn = cut.FindAll("button.agent-option-chip").First(b => b.TextContent.Trim() == "SQLite");
        sqliteBtn.Click();

        // Submit clarification
        var submitBtn = cut.Find("button.forge-btn-primary");
        submitBtn.Click();

        // Verify orchestrator received taskId 15 and mapped question ID 105 -> SQLite
        Assert.NotNull(_orchestrator.LastSubmitAnswersCall);
        Assert.Equal(15, _orchestrator.LastSubmitAnswersCall.Value.TaskId);
        var submittedAnswers = _orchestrator.LastSubmitAnswersCall.Value.Answers;
        Assert.Single(submittedAnswers);
        Assert.Equal("SQLite", submittedAnswers[105]);
    }

    [Fact]
    public async Task StageConversationViewer_MultiQuestionBatch_HandlesSequentialPartialAnswers()
    {
        var task = new ForgeTask
        {
            Id = 16,
            Title = "Task with 2 questions",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            CurrentStageId = "task-review"
        };
        var q1 = new Question
        {
            Id = 201,
            TaskId = task.Id,
            StageId = "task-review",
            Prompt = "Question 1",
            Status = QuestionStatus.Pending,
            BatchId = "batch-200"
        };
        var q2 = new Question
        {
            Id = 202,
            TaskId = task.Id,
            StageId = "task-review",
            Prompt = "Question 2",
            Status = QuestionStatus.Pending,
            BatchId = "batch-200"
        };
        task.Questions.Add(q1);
        task.Questions.Add(q2);
        _taskRepository.AddQuestion(q1);
        _taskRepository.AddQuestion(q2);
        await _taskRepository.CreateAsync(task);

        var conv = await _taskRepository.StartConversationAsync(task.Id, "task-review", WorkflowStageKind.Agentic, 1);
        await _taskRepository.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Tool,
            toolName: CanonicalToolNames.Ask,
            toolArguments: "{\"questions\":[{\"prompt\":\"Question 1\",\"options\":[\"Option A\",\"Option B\"]},{\"prompt\":\"Question 2\",\"options\":[\"Option C\",\"Option D\"]}]}",
            metadataJson: "{\"CallId\":\"call-multi-1\"}");
        await _taskRepository.CompleteConversationAsync(conv.Id, StageExecutionStatus.WaitingForHuman);

        var loadedConv = await _taskRepository.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(loadedConv);

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, loadedConv)
            .Add(x => x.IsActiveStage, false));

        // Answer Q1 only
        var optionABtn = cut.FindAll("button.agent-option-chip").First(b => b.TextContent.Trim() == "Option A");
        optionABtn.Click();

        var submitBtn = cut.Find("button.forge-btn-primary");
        submitBtn.Click();

        Assert.NotNull(_orchestrator.LastSubmitAnswersCall);
        Assert.Single(_orchestrator.LastSubmitAnswersCall.Value.Answers);
        Assert.Equal("Option A", _orchestrator.LastSubmitAnswersCall.Value.Answers[201]);

        // Simulate Q1 becoming Answered in repository
        q1.Status = QuestionStatus.Answered;
        q1.Answer = "Option A";
        await _taskRepository.UpdateAsync(task);

        // Now answer Q2 (at index 1)
        var optionCBtn = cut.FindAll("button.agent-option-chip").First(b => b.TextContent.Trim() == "Option C");
        optionCBtn.Click();

        submitBtn = cut.Find("button.forge-btn-primary");
        submitBtn.Click();

        // Must submit question 202 without attempting to re-submit 201 or throwing index out of bounds
        Assert.NotNull(_orchestrator.LastSubmitAnswersCall);
        Assert.True(_orchestrator.LastSubmitAnswersCall.Value.Answers.ContainsKey(202));
        Assert.Equal("Option C", _orchestrator.LastSubmitAnswersCall.Value.Answers[202]);
    }

    [Fact]
    public void AgentQuestionTurnCard_AnswerSelection_DoesNotFalseMatchSubstring()
    {
        var questions = new[]
        {
            new QuestionItem("Do you want extra logging?", null, new[] { "No", "Yes" })
        };

        // If the answer is "Nothing to add", "No" should NOT be highlighted as answered-selected
        var cut = Render<AgentQuestionTurnCard>(p => p
            .Add(x => x.Questions, questions)
            .Add(x => x.IsWaitingForAnswer, false)
            .Add(x => x.Answer, "Nothing to add"));

        var noBtn = cut.FindAll("button.agent-option-chip").First(b => b.TextContent.Trim() == "No");
        Assert.DoesNotContain("answered-selected", noBtn.ClassName);

        // If the answer is "No", it MUST be highlighted as answered-selected
        var cut2 = Render<AgentQuestionTurnCard>(p => p
            .Add(x => x.Questions, questions)
            .Add(x => x.IsWaitingForAnswer, false)
            .Add(x => x.Answer, "No"));

        var noBtn2 = cut2.FindAll("button.agent-option-chip").First(b => b.TextContent.Trim() == "No");
        Assert.Contains("answered-selected", noBtn2.ClassName);
    }

    [Fact]
    public void AgentQuestionTurnCard_SubmittingMultipleAnswers_WithOnAnswerSubmitted_JoinsAllAnswers()
    {
        var questions = new[]
        {
            new QuestionItem("First question?", null, new[] { "Alpha", "Beta" }),
            new QuestionItem("Second question?", null, new[] { "Gamma", "Delta" })
        };

        string? submittedAnswer = null;

        var cut = Render<AgentQuestionTurnCard>(p => p
            .Add(x => x.Questions, questions)
            .Add(x => x.IsWaitingForAnswer, true)
            .Add(x => x.OnAnswerSubmitted, (string ans) => submittedAnswer = ans));

        // Select Alpha for Q1
        var alphaBtn = cut.FindAll("button.agent-option-chip").First(b => b.TextContent.Trim() == "Alpha");
        alphaBtn.Click();

        // Select Gamma for Q2
        var gammaBtn = cut.FindAll("button.agent-option-chip").First(b => b.TextContent.Trim() == "Gamma");
        gammaBtn.Click();

        var submitBtn = cut.Find("button.forge-btn-primary");
        submitBtn.Click();

        // Must join both questions, NOT drop Q2!
        Assert.NotNull(submittedAnswer);
        Assert.Contains("Q1: Alpha", submittedAnswer);
        Assert.Contains("Q2: Gamma", submittedAnswer);
    }

    [Fact]
    public void AgentQuestionTurnCard_WithDirectAnswer_SelectsMatchingOption()
    {
        var questions = new[]
        {
            new QuestionItem("Choose database:", null, new[] { "PostgreSQL", "SQLite" })
        };

        var cut = Render<AgentQuestionTurnCard>(p => p
            .Add(x => x.Questions, questions)
            .Add(x => x.IsWaitingForAnswer, false)
            .Add(x => x.Answer, "PostgreSQL"));

        var pgBtn = cut.FindAll("button.agent-option-chip").First(b => b.TextContent.Trim() == "PostgreSQL");
        var sqliteBtn = cut.FindAll("button.agent-option-chip").First(b => b.TextContent.Trim() == "SQLite");

        Assert.Contains("answered-selected", pgBtn.ClassName);
        Assert.DoesNotContain("answered-selected", sqliteBtn.ClassName);
    }

    [Fact]
    public void StageConversationViewer_ExtractQuestions_ParsesQuestionItemsAndSingleQuestionSchemas()
    {
        // 1. Test question_items key
        var conv1 = new Conversation
        {
            Id = 3,
            TaskId = 10,
            StageId = "task-review",
            Status = StageExecutionStatus.WaitingForHuman,
            Turns = new List<ConversationTurn>
            {
                new()
                {
                    Id = 301,
                    TurnIndex = 0,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Ask,
                    ToolArguments = "{\"question_items\":[{\"prompt\":\"Lenient question?\",\"options\":[\"Yes\",\"No\"]}]}"
                }
            }
        };

        var cut1 = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv1)
            .Add(x => x.IsActiveStage, false));

        Assert.Contains("Lenient question?", cut1.Markup);
        Assert.Contains("Yes", cut1.Markup);

        // 2. Test single question object
        var conv2 = new Conversation
        {
            Id = 4,
            TaskId = 10,
            StageId = "task-review",
            Status = StageExecutionStatus.WaitingForHuman,
            Turns = new List<ConversationTurn>
            {
                new()
                {
                    Id = 401,
                    TurnIndex = 0,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Ask,
                    ToolArguments = "{\"prompt\":\"Single question object?\",\"rationale\":\"Quick check\",\"options\":[\"Option X\"]}"
                }
            }
        };

        var cut2 = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv2)
            .Add(x => x.IsActiveStage, false));

        Assert.Contains("Single question object?", cut2.Markup);
        Assert.Contains("Quick check", cut2.Markup);
        Assert.Contains("Option X", cut2.Markup);
    }

    [Fact]
    public void AgentQuestionTurnCard_WithStructuredMetadata_SelectsOptionsDeterministicallyWithoutGuessing()
    {
        // Two questions with identical option sets to verify zero cross-question leakage
        var questions = new[]
        {
            new QuestionItem("Choose staging branch:", null, new[] { "main", "staging" }),
            new QuestionItem("Choose prod branch:", null, new[] { "main", "staging" })
        };

        var structuredMeta = "{\"CallId\":\"call-123\",\"Answers\":[" +
            "{\"QuestionIndex\":0,\"SelectedOptionIndex\":1,\"SelectedOption\":\"staging\",\"AnswerText\":\"staging\"}," +
            "{\"QuestionIndex\":1,\"SelectedOptionIndex\":0,\"SelectedOption\":\"main\",\"AnswerText\":\"main\"}" +
            "]}";

        var cut = Render<AgentQuestionTurnCard>(p => p
            .Add(x => x.Questions, questions)
            .Add(x => x.IsWaitingForAnswer, false)
            .Add(x => x.MetadataJson, structuredMeta)
            .Add(x => x.Answer, "Q1: staging\nQ2: main"));

        var buttons = cut.FindAll("button.agent-option-chip").ToList();
        Assert.Equal(4, buttons.Count);

        // Question 0: buttons[0] is "main", buttons[1] is "staging"
        Assert.DoesNotContain("answered-selected", buttons[0].ClassName);
        Assert.Contains("answered-selected", buttons[1].ClassName);

        // Question 1: buttons[2] is "main", buttons[3] is "staging"
        Assert.Contains("answered-selected", buttons[2].ClassName);
        Assert.DoesNotContain("answered-selected", buttons[3].ClassName);
    }

    [Fact]
    public void StageConversationViewer_PassesMetadataJsonToAgentQuestionTurnCard()
    {
        var conv = new Conversation
        {
            Id = 5,
            TaskId = 11,
            StageId = "task-review",
            Status = StageExecutionStatus.Completed,
            Turns = new List<ConversationTurn>
            {
                new()
                {
                    Id = 501,
                    TurnIndex = 0,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Ask,
                    ToolArguments = "{\"questions\":[{\"prompt\":\"Target environment?\",\"options\":[\"Staging\",\"Production\"]}]}",
                    MetadataJson = "{\"CallId\":\"call-ask-env-1\"}"
                },
                new()
                {
                    Id = 502,
                    TurnIndex = 1,
                    Role = StageTurnRole.ToolResult,
                    ToolName = CanonicalToolNames.Ask,
                    ToolResult = "Staging",
                    MetadataJson = "{\"CallId\":\"call-ask-env-1\",\"Answers\":[{\"QuestionIndex\":0,\"SelectedOptionIndex\":0,\"SelectedOption\":\"Staging\",\"AnswerText\":\"Staging\"}]}"
                }
            }
        };

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv)
            .Add(x => x.IsActiveStage, false));

        var chips = cut.FindAll("button.agent-option-chip");
        var stagingChip = chips.First(c => c.TextContent.Trim() == "Staging");
        var prodChip = chips.First(c => c.TextContent.Trim() == "Production");

        Assert.Contains("answered-selected", stagingChip.ClassName);
        Assert.DoesNotContain("answered-selected", prodChip.ClassName);
    }

    [Fact]
    public void StageConversationViewer_WhenTaskQuestionsAnsweredInInbox_RendersClarificationProvidedAndHidesForm()
    {
        var conv = new Conversation
        {
            Id = 6,
            TaskId = 20,
            StageId = "task-review",
            Status = StageExecutionStatus.WaitingForHuman,
            Turns = new List<ConversationTurn>
            {
                new()
                {
                    Id = 601,
                    TurnIndex = 0,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Ask,
                    ToolArguments = "{\"questions\":[{\"prompt\":\"Choose a database:\",\"options\":[\"PostgreSQL\",\"SQLite\"]}]}",
                    MetadataJson = "{\"CallId\":\"call-db-99\"}"
                }
            }
        };

        var task = new ForgeTask
        {
            Id = 20,
            Title = "Task with answered questions in Inbox",
            IsWaitingForHuman = false,
            CurrentStageId = "task-review",
            Questions = new List<Question>
            {
                new()
                {
                    Id = 991,
                    TaskId = 20,
                    StageId = "task-review",
                    Prompt = "Choose a database:",
                    Answer = "PostgreSQL",
                    OptionsJson = "[\"PostgreSQL\",\"SQLite\"]",
                    Status = QuestionStatus.Answered
                }
            }
        };

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv)
            .Add(x => x.Task, task)
            .Add(x => x.IsActiveStage, false));

        // Must NOT render input textarea or submit button
        Assert.Empty(cut.FindAll("textarea.agent-question-input"));
        Assert.Empty(cut.FindAll("button.submit-answer-btn"));

        // Header badge must reflect answered/queued state
        Assert.DoesNotContain("Waiting for Answer", cut.Markup);
        Assert.Contains("Clarification Provided (Queued)", cut.Markup);

        // Turn card must display Clarification Provided and user response
        Assert.Contains("Clarification Provided", cut.Markup);
        Assert.Contains("User Response", cut.Markup);
        Assert.Contains("PostgreSQL", cut.Markup);

        // Option chip must be selected
        var chips = cut.FindAll("button.agent-option-chip");
        var pgChip = chips.First(c => c.TextContent.Trim() == "PostgreSQL");
        Assert.Contains("answered-selected", pgChip.ClassName);
    }

    [Fact]
    public void StageConversationViewer_WithPersistedToolResultTurn_EvenIfWaitingForHuman_RendersAnsweredState()
    {
        var conv = new Conversation
        {
            Id = 7,
            TaskId = 25,
            StageId = "task-review",
            Status = StageExecutionStatus.WaitingForHuman,
            Turns = new List<ConversationTurn>
            {
                new()
                {
                    Id = 701,
                    TurnIndex = 0,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Ask,
                    ToolArguments = "{\"questions\":[{\"prompt\":\"Choose cache:\",\"options\":[\"Redis\",\"Memory\"]}]}",
                    MetadataJson = "{\"CallId\":\"call-cache-77\"}"
                },
                new()
                {
                    Id = 702,
                    TurnIndex = 1,
                    Role = StageTurnRole.ToolResult,
                    ToolName = CanonicalToolNames.Ask,
                    ToolResult = "Redis",
                    MetadataJson = "{\"CallId\":\"call-cache-77\",\"Answers\":[{\"QuestionIndex\":0,\"SelectedOptionIndex\":0,\"SelectedOption\":\"Redis\",\"AnswerText\":\"Redis\"}]}"
                }
            }
        };

        var task = new ForgeTask
        {
            Id = 25,
            Title = "Task with persisted ToolResult turn",
            IsWaitingForHuman = false,
            CurrentStageId = "task-review"
        };

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv)
            .Add(x => x.Task, task)
            .Add(x => x.IsActiveStage, false));

        // Must NOT render input textarea or submit button
        Assert.Empty(cut.FindAll("textarea.agent-question-input"));
        Assert.Empty(cut.FindAll("button.submit-answer-btn"));

        // Header badge must reflect answered/queued state
        Assert.DoesNotContain("Waiting for Answer", cut.Markup);
        Assert.Contains("Clarification Provided (Queued)", cut.Markup);

        // Turn card must display Clarification Provided and user response
        Assert.Contains("Clarification Provided", cut.Markup);
        Assert.Contains("User Response", cut.Markup);
        Assert.Contains("Redis", cut.Markup);

        var chips = cut.FindAll("button.agent-option-chip");
        var redisChip = chips.First(c => c.TextContent.Trim() == "Redis");
        Assert.Contains("answered-selected", redisChip.ClassName);
    }

    [Fact]
    public void StageConversationViewer_WhenDifferentStageHasAnsweredQuestions_NewStagePendingQuestionRendersInputForm()
    {
        var conv = new Conversation
        {
            Id = 8,
            TaskId = 30,
            StageId = "implementation",
            Status = StageExecutionStatus.WaitingForHuman,
            Turns = new List<ConversationTurn>
            {
                new()
                {
                    Id = 801,
                    TurnIndex = 0,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Ask,
                    ToolArguments = "{\"questions\":[{\"prompt\":\"Choose message broker:\",\"options\":[\"RabbitMQ\",\"Kafka\"]}]}",
                    MetadataJson = "{\"CallId\":\"call-broker-1\"}"
                }
            }
        };

        var task = new ForgeTask
        {
            Id = 30,
            Title = "Task with answered questions in prior stage",
            IsWaitingForHuman = true,
            CurrentStageId = "implementation",
            Questions = new List<Question>
            {
                new()
                {
                    Id = 991,
                    TaskId = 30,
                    StageId = "task-review",
                    Prompt = "Choose a database:",
                    Answer = "PostgreSQL",
                    OptionsJson = "[\"PostgreSQL\",\"SQLite\"]",
                    Status = QuestionStatus.Answered
                },
                new()
                {
                    Id = 992,
                    TaskId = 30,
                    StageId = "implementation",
                    Prompt = "Choose message broker:",
                    OptionsJson = "[\"RabbitMQ\",\"Kafka\"]",
                    Status = QuestionStatus.Pending
                }
            }
        };

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv)
            .Add(x => x.Task, task)
            .Add(x => x.IsActiveStage, true));

        // Must render input textarea and submit button because implementation question is pending
        Assert.NotEmpty(cut.FindAll("textarea.agent-question-input"));
        Assert.NotEmpty(cut.FindAll("button.submit-answer-btn"));

        // Header badge must reflect active waiting state
        Assert.Contains("Waiting for Answer", cut.Markup);
        Assert.DoesNotContain("Clarification Provided (Queued)", cut.Markup);
        Assert.DoesNotContain("PostgreSQL", cut.Markup);
    }

    [Fact]
    public void StageConversationViewer_WhenSameStageHasSubsequentPendingQuestion_RendersInputForm()
    {
        var conv = new Conversation
        {
            Id = 9,
            TaskId = 35,
            StageId = "task-review",
            Status = StageExecutionStatus.WaitingForHuman,
            Turns = new List<ConversationTurn>
            {
                new()
                {
                    Id = 901,
                    TurnIndex = 0,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Ask,
                    ToolArguments = "{\"questions\":[{\"prompt\":\"Choose database:\",\"options\":[\"PostgreSQL\",\"SQLite\"]}]}",
                    MetadataJson = "{\"CallId\":\"call-db-1\"}"
                },
                new()
                {
                    Id = 902,
                    TurnIndex = 1,
                    Role = StageTurnRole.ToolResult,
                    ToolName = CanonicalToolNames.Ask,
                    ToolResult = "PostgreSQL",
                    MetadataJson = "{\"CallId\":\"call-db-1\",\"Answers\":[{\"QuestionIndex\":0,\"SelectedOptionIndex\":0,\"SelectedOption\":\"PostgreSQL\",\"AnswerText\":\"PostgreSQL\"}]}"
                },
                new()
                {
                    Id = 903,
                    TurnIndex = 2,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Ask,
                    ToolArguments = "{\"questions\":[{\"prompt\":\"Choose cache:\",\"options\":[\"Redis\",\"Memory\"]}]}",
                    MetadataJson = "{\"CallId\":\"call-cache-2\"}"
                }
            }
        };

        var task = new ForgeTask
        {
            Id = 35,
            Title = "Task with second pending question in same stage",
            IsWaitingForHuman = true,
            CurrentStageId = "task-review",
            Questions = new List<Question>
            {
                new()
                {
                    Id = 951,
                    TaskId = 35,
                    StageId = "task-review",
                    Prompt = "Choose database:",
                    Answer = "PostgreSQL",
                    Status = QuestionStatus.Answered
                },
                new()
                {
                    Id = 952,
                    TaskId = 35,
                    StageId = "task-review",
                    Prompt = "Choose cache:",
                    Status = QuestionStatus.Pending
                }
            }
        };

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv)
            .Add(x => x.Task, task)
            .Add(x => x.IsActiveStage, true));

        // The second pending question must render input textarea and submit button
        Assert.NotEmpty(cut.FindAll("textarea.agent-question-input"));
        Assert.NotEmpty(cut.FindAll("button.submit-answer-btn"));

        // Header badge must reflect waiting state
        Assert.Contains("Waiting for Answer", cut.Markup);
        Assert.DoesNotContain("Clarification Provided (Queued)", cut.Markup);
    }

    [Fact]
    public void StageConversationViewer_DownstreamStage_DoesNotRenderRogueAskCard()
    {
        // Simulate a downstream stage (e.g. implementation) conversation containing an orphan ask ToolResult
        var conv = new Conversation
        {
            Id = 99,
            TaskId = 50,
            StageId = StandardStageIds.Implementation,
            Status = StageExecutionStatus.Running,
            Turns = new List<ConversationTurn>
            {
                new()
                {
                    Id = 901,
                    TurnIndex = 0,
                    Role = StageTurnRole.User,
                    Content = "Implement the feature"
                },
                new()
                {
                    Id = 902,
                    TurnIndex = 1,
                    Role = StageTurnRole.ToolResult,
                    ToolName = CanonicalToolNames.Ask,
                    ToolResult = "Q: Which DB?\nA: PostgreSQL"
                },
                new()
                {
                    Id = 903,
                    TurnIndex = 2,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Bash,
                    ToolArguments = "dotnet test",
                    MetadataJson = "{\"CallId\":\"prime_123\",\"IsPrimed\":true,\"SourceStageId\":\"deterministic-pre-impl\",\"PrimingType\":\"ForwardPass\"}"
                }
            }
        };

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv)
            .Add(x => x.IsActiveStage, true));

        // Must not render AgentQuestionTurnCard or any tool card for "ask"
        Assert.Empty(cut.FindComponents<AgentQuestionTurnCard>());
        Assert.DoesNotContain("❓", cut.Markup);
        Assert.DoesNotContain("ask", cut.Markup);

        // Primed bash turn must be rendered with badge
        Assert.Contains("Quality Gate Handover", cut.Markup);
        Assert.Contains("dotnet test", cut.Markup);
    }

    [Fact]
    public void StageConversationViewer_PrimedTurn_DoesNotShowRunningOrOverwrittenOutput_WhenLiveToolIsStreaming()
    {
        const string primedCallId = "prime_abc123";
        var primedMeta = $"{{\"CallId\":\"{primedCallId}\",\"IsPrimed\":true,\"SourceStageId\":\"deterministic-pre-impl\",\"PrimingType\":\"ForwardPass\"}}";

        var conv = new Conversation
        {
            Id = 100,
            TaskId = 51,
            StageId = StandardStageIds.Implementation,
            Status = StageExecutionStatus.Running,
            Turns = new List<ConversationTurn>
            {
                new()
                {
                    Id = 1001,
                    TurnIndex = 0,
                    Role = StageTurnRole.User,
                    Content = "Begin implementation"
                },
                new()
                {
                    Id = 1002,
                    TurnIndex = 1,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Bash,
                    ToolArguments = "{\"command\":\"dotnet test\"}",
                    MetadataJson = primedMeta
                },
                new()
                {
                    Id = 1003,
                    TurnIndex = 2,
                    Role = StageTurnRole.ToolResult,
                    ToolName = CanonicalToolNames.Bash,
                    ToolResult = "Exit Code: 0\nPassed: 42 tests.",
                    MetadataJson = primedMeta
                }
            }
        };

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv)
            .Add(x => x.IsActiveStage, true)
            .Add(x => x.LiveRunningToolName, CanonicalToolNames.Bash)
            .Add(x => x.LiveRunningToolArguments, "git status")
            .Add(x => x.LiveRunningToolOutput, "On branch feature/123, active changes"));

        // Primed card must retain its deterministic test output and not be overwritten by live streaming output
        Assert.Contains("Passed: 42 tests.", cut.Markup);
        Assert.Contains("Quality Gate Handover", cut.Markup);

        // Finding the tool call cards: the primed card should not have "status-dot running"
        var toolCards = cut.FindComponents<ToolCallCard>();
        var primedCard = toolCards.FirstOrDefault(c => c.Instance.BadgeText == "⚡ Quality Gate Handover");
        Assert.NotNull(primedCard);
        Assert.False(primedCard.Instance.IsRunning);
        Assert.Equal("Exit Code: 0\nPassed: 42 tests.", primedCard.Instance.Result);
    }
}

