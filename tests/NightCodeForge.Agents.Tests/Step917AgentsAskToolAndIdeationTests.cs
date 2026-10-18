using Microsoft.Extensions.AI;
using Moq;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Ideation;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using System.Text.Json;

namespace NightCodeForge.Agents.Tests;

public class Step917AgentsAskToolAndIdeationTests
{
    [Fact]
    public async Task AskTool_NullOrEmptyList_ReturnsModelVisibleError()
    {
        var tools = TestCanonicalToolsBuilder.Create(AppContext.BaseDirectory, pathResolver: TestPathResolver.Create());

        var nullResult = await tools.AskAsync(null);
        Assert.StartsWith("Error: At least one question must be provided", nullResult);

        var emptyResult = await tools.AskAsync(Array.Empty<QuestionItem>());
        Assert.StartsWith("Error: At least one question must be provided", emptyResult);

        // Also verify via AIFunction invocation
        var aiTools = CanonicalAIFunctionFactory.CreateTools(tools);
        var askFunc = aiTools.First(t => t.Name == "ask");

        var invocationResult = await askFunc.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["questions"] = null }));
        Assert.StartsWith("Error: At least one question must be provided", invocationResult?.ToString());
    }

    [Fact]
    public async Task AskTool_WithJsonElementPayload_DeserializesAndInvokes()
    {
        var tools = TestCanonicalToolsBuilder.Create(AppContext.BaseDirectory, pathResolver: TestPathResolver.Create());
        var aiTools = CanonicalAIFunctionFactory.CreateTools(tools);
        var askFunc = aiTools.First(t => t.Name == "ask");

        var payload = """
        {"questions":[{"options":["Keep scope limited: full XML docs on Multiply only (recommended default)","Also upgrade Add and Subtract to full <param>/<returns> docs for consistency"],"prompt":"Should the existing Add and Subtract methods' XML docs also be upgraded to include <param> and <returns> elements, or should scope stay limited to giving the new Multiply method full XML docs?","rationale":"Current Add/Subtract only have <summary>. AGENTS.md requires XML docs on public APIs, which <summary> technically satisfies. Upgrading them would improve consistency but expands scope beyond the stated task."}]}
        """;

        using var doc = JsonDocument.Parse(payload);
        var questionsElement = doc.RootElement.GetProperty("questions");
        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["questions"] = questionsElement });

        var ex = await Record.ExceptionAsync(async () => await askFunc.InvokeAsync(args));
        // Without an AskHandler, this should throw HumanInterruptionException
        Assert.IsType<HumanInterruptionException>(ex);
    }

    [Fact]
    public async Task AskTool_BlankPrompt_ReturnsModelVisibleError()
    {
        var tools = TestCanonicalToolsBuilder.Create(AppContext.BaseDirectory, pathResolver: TestPathResolver.Create());

        var result1 = await tools.AskAsync(new[] { new QuestionItem("   ") });
        Assert.Equal("Error: Question at index 0 has a blank or empty prompt.", result1);

        var result2 = await tools.AskAsync(new[]
        {
            new QuestionItem("Valid question?"),
            new QuestionItem("")
        });
        Assert.Equal("Error: Question at index 1 has a blank or empty prompt.", result2);
    }

    [Fact]
    public async Task AskTool_ValidQuestionsWithoutHandler_ThrowsHumanInterruptionExceptionWithAllQuestions()
    {
        var tools = TestCanonicalToolsBuilder.Create(AppContext.BaseDirectory, pathResolver: TestPathResolver.Create());
        tools.AskHandler = null;

        var questions = new[]
        {
            new QuestionItem("Which database should we use?", "Needed for persistence design", new[] { "PostgreSQL", "SQLite" }),
            new QuestionItem("Do we support multi-tenancy?")
        };

        var ex = await Assert.ThrowsAsync<HumanInterruptionException>(() => tools.AskAsync(questions));

        Assert.Equal(2, ex.Questions.Count);
        Assert.Equal("Which database should we use?", ex.Questions[0].Prompt);
        Assert.Equal("Needed for persistence design", ex.Questions[0].Rationale);
        Assert.Equal(QuestionType.SingleChoice, ex.Questions[0].QuestionType);
        Assert.NotNull(ex.Questions[0].OptionsJson);
        Assert.Contains("PostgreSQL", ex.Questions[0].OptionsJson);

        Assert.Equal("Do we support multi-tenancy?", ex.Questions[1].Prompt);
        Assert.Equal(QuestionType.FreeText, ex.Questions[1].QuestionType);
        Assert.Null(ex.Questions[1].OptionsJson);
    }

    [Fact]
    public async Task Ideation_AskHandler_BatchedQuestions_AwaitedAndNumberedAnswersReturned()
    {
        var tools = TestCanonicalToolsBuilder.Create(AppContext.BaseDirectory, pathResolver: TestPathResolver.Create());

        var receivedQuestions = new List<IdeationQuestion>();
        tools.AskHandler = async (questions, ct) =>
        {
            var registrations = new List<CancellationTokenRegistration>();
            try
            {
                var tasks = new List<Task<string>>();
                var questionList = new List<IdeationQuestion>();
                foreach (var q in questions)
                {
                    var question = new IdeationQuestion(q.Prompt, q.Rationale, q.Options);
                    questionList.Add(question);
                    registrations.Add(ct.Register(() => question.Cancel()));
                    tasks.Add(question.AnswerTask);
                    receivedQuestions.Add(question);
                }

                var answers = await Task.WhenAll(tasks).ConfigureAwait(false);

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < questionList.Count; i++)
                {
                    if (i > 0) sb.AppendLine();
                    sb.AppendLine($"{i + 1}. Question: {questionList[i].Prompt}");
                    sb.AppendLine($"   Answer: {answers[i]}");
                }
                return sb.ToString().TrimEnd();
            }
            finally
            {
                foreach (var reg in registrations)
                {
                    reg.Dispose();
                }
            }
        };

        var askTask = tools.AskAsync(new[]
        {
            new QuestionItem("Question A"),
            new QuestionItem("Question B", "Rationale B", new[] { "1", "2" })
        });

        // Simulate UI answering questions
        Assert.Equal(2, receivedQuestions.Count);
        receivedQuestions[0].Submit("Answer A");
        receivedQuestions[1].Submit("Answer B");

        var result = await askTask;

        Assert.Contains("1. Question: Question A", result);
        Assert.Contains("   Answer: Answer A", result);
        Assert.Contains("2. Question: Question B", result);
        Assert.Contains("   Answer: Answer B", result);
    }

    [Fact]
    public async Task Ideation_AskHandler_Cancellation_DisposesRegistrationsAndCancelsQuestions()
    {
        var tools = TestCanonicalToolsBuilder.Create(AppContext.BaseDirectory, pathResolver: TestPathResolver.Create());

        var receivedQuestions = new List<IdeationQuestion>();
        using var cts = new CancellationTokenSource();

        tools.AskHandler = async (questions, ct) =>
        {
            var registrations = new List<CancellationTokenRegistration>();
            try
            {
                var tasks = new List<Task<string>>();
                var questionList = new List<IdeationQuestion>();
                foreach (var q in questions)
                {
                    var question = new IdeationQuestion(q.Prompt, q.Rationale, q.Options);
                    questionList.Add(question);
                    registrations.Add(ct.Register(() => question.Cancel()));
                    tasks.Add(question.AnswerTask);
                    receivedQuestions.Add(question);
                }

                var answers = await Task.WhenAll(tasks).ConfigureAwait(false);
                return string.Join("\n", answers);
            }
            finally
            {
                foreach (var reg in registrations)
                {
                    reg.Dispose();
                }
            }
        };

        var askTask = tools.AskAsync(new[] { new QuestionItem("Question 1") }, cts.Token);

        // Cancel before answering
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => askTask);
        Assert.True(receivedQuestions[0].AnswerTask.IsCanceled);
    }

    [Fact]
    public void CrucibleResumption_CompletePendingAskToolCall_RestoresToolResultAndUserInstruction()
    {
        // Construct conversation turns with an unanswered 'ask' tool call
        var turns = new List<ConversationTurn>
        {
            new()
            {
                TurnIndex = 0,
                Role = StageTurnRole.User,
                Content = "Implement feature X."
            },
            new()
            {
                TurnIndex = 1,
                Role = StageTurnRole.Tool,
                ToolName = "ask",
                ToolArguments = "{\"questions\":[{\"prompt\":\"Postgres or SQLite?\"}]}",
                MetadataJson = "{\"callId\":\"call-ask-123\"}"
            }
        };

        var serializedHistory = ConversationHistorySerializer.Serialize(turns);
        Assert.Contains("call-ask-123", serializedHistory);

        var formattedQna = "1. Question: Postgres or SQLite?\n   Answer: PostgreSQL";
        var completedHistory = ConversationHistorySerializer.CompletePendingAskToolCall(serializedHistory, formattedQna);

        // Verify deserialized items
        var items = ConversationHistorySerializer.Deserialize(completedHistory);
        Assert.Equal(4, items.Count);

        // Item 1: User
        Assert.Equal("user", items[0].Role);
        Assert.Equal("Implement feature X.", items[0].Text);

        // Item 2: Assistant tool call
        Assert.Equal("assistant", items[1].Role);
        Assert.NotNull(items[1].FunctionCalls);
        Assert.Equal("call-ask-123", items[1].FunctionCalls![0].CallId);

        // Item 3: Tool result
        Assert.Equal("tool", items[2].Role);
        Assert.NotNull(items[2].FunctionResults);
        Assert.Equal("call-ask-123", items[2].FunctionResults![0].CallId);
        Assert.Equal(formattedQna, items[2].FunctionResults![0].Result?.ToString());

        // Item 4: User proceed instruction
        Assert.Equal("user", items[3].Role);
        Assert.Contains("Please proceed with your plan based on the clarified answers.", items[3].Text);
    }

    [Fact]
    public void CrucibleResumption_CompletePendingAskToolCall_AppendsResult_WhenToolResultWasOmitted()
    {
        // Construct session directly via CrucibleAgentHarness where ask was called and interrupted (no tool result turn)
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Design the architecture."),
            new(ChatRole.Assistant, new AIContent[]
            {
                new FunctionCallContent("call-ask-999", "ask", new Dictionary<string, object?>
                {
                    ["questions"] = new[] { new { prompt = "Synchronous or asynchronous queue?" } }
                })
            })
        };

        var serializedHistory = CrucibleAgentHarness.SerializeSession(messages);
        var formattedQna = "1. Question: Synchronous or asynchronous queue?\n   Answer: Asynchronous";

        var completedHistory = ConversationHistorySerializer.CompletePendingAskToolCall(serializedHistory, formattedQna);
        var items = ConversationHistorySerializer.Deserialize(completedHistory);

        Assert.Equal(4, items.Count);
        Assert.Equal("user", items[0].Role);
        Assert.Equal("assistant", items[1].Role);
        Assert.Equal("tool", items[2].Role);
        Assert.Equal("call-ask-999", items[2].FunctionResults![0].CallId);
        Assert.Equal(formattedQna, items[2].FunctionResults![0].Result?.ToString());
        Assert.Equal("user", items[3].Role);
        Assert.Contains("Please proceed with your plan", items[3].Text);
    }

    [Fact]
    public async Task CrucibleAgentHarness_WhenAskCalled_PausesWithWaitingForHuman()
    {
        var chatClientMock = new Mock<IChatClient>();
        var functionCall = new FunctionCallContent("call-ask-1", "ask", new Dictionary<string, object?>
        {
            ["questions"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["prompt"] = "Should we support PostgreSQL or SQLite?",
                    ["rationale"] = "Need to decide DB architecture",
                    ["options"] = new[] { "PostgreSQL", "SQLite" }
                }
            }
        });

        chatClientMock
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new ChatResponseUpdate
                {
                    Contents = { functionCall }
                }
            }.ToAsyncEnumerable());

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = chatClientMock.Object
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 1, Title = "Test Task" },
            Workspace = new Workspace { Id = 1, Name = "Test WS", RepositoryPath = AppContext.BaseDirectory },
            Profile = new AgentProfile { Id = "test-prof", HarnessId = "crucible" },
            StageId = StandardStageIds.TaskReview,
            StageDirective = "Investigate requirements",
            RequiredMode = AgentMode.Full,
            WorkspacePath = AppContext.BaseDirectory
        };

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.WaitingForHuman, result.Status);
        Assert.NotNull(result.PendingQuestions);
        Assert.Single(result.PendingQuestions);
        Assert.Equal("Should we support PostgreSQL or SQLite?", result.PendingQuestions[0].Prompt);
    }

    [Fact]
    public async Task CrucibleAgentHarness_WhenRequestChangesCalled_CompletesWithRejection()
    {
        var chatClientMock = new Mock<IChatClient>();
        var functionCall = new FunctionCallContent("call-rc-1", "request_changes", new Dictionary<string, object?>
        {
            ["feedback"] = "Missing unit tests for edge cases",
            ["reason"] = "Incomplete implementation"
        });

        chatClientMock
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new ChatResponseUpdate
                {
                    Contents = { functionCall }
                }
            }.ToAsyncEnumerable());

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = chatClientMock.Object
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 1, Title = "Test Task" },
            Workspace = new Workspace { Id = 1, Name = "Test WS", RepositoryPath = AppContext.BaseDirectory },
            Profile = new AgentProfile { Id = "test-prof", HarnessId = "crucible" },
            StageId = StandardStageIds.CodeReviewAndFix,
            StageDirective = "Review pull request",
            RequiredMode = AgentMode.Review,
            WorkspacePath = AppContext.BaseDirectory
        };

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(StandardStageIds.Implementation, result.TargetRouting);
        Assert.Equal("Missing unit tests for edge cases", result.ReviewerFeedback);
    }

    [Fact]
    public async Task CanonicalAIFunctionFactory_WhenToolThrows_ReturnsDetailedErrorMessage()
    {
        var mockEnv = new Mock<IExecutionEnvironment>();
        mockEnv
            .Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Docker daemon socket not responding"));

        var tools = TestCanonicalToolsBuilder.Create(
            AppContext.BaseDirectory,
            pathResolver: TestPathResolver.Create(),
            executionEnv: mockEnv.Object);

        var aiTools = CanonicalAIFunctionFactory.CreateTools(tools);
        var bashFunc = aiTools.First(t => t.Name == "bash");

        var invocationResult = await bashFunc.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["command"] = "dotnet build" }));
        var resultText = invocationResult?.ToString();

        Assert.NotNull(resultText);
        Assert.StartsWith("Error: ", resultText);
        Assert.Contains("Docker daemon socket not responding", resultText);
    }

    [Fact]
    public async Task LenientToolDispatcher_Ask_HandlesPascalCaseAndSingleObject()
    {
        var tools = TestCanonicalToolsBuilder.Create(AppContext.BaseDirectory);
        var dispatcher = new LenientToolDispatcher(tools);

        // 1. PascalCase in JSON array
        var pascalJson = """
        {
          "Questions": [
            {
              "Prompt": "PascalCase prompt?",
              "Rationale": "Testing PascalCase",
              "Options": ["OptA", "OptB"]
            }
          ]
        }
        """;

        var ex = await Assert.ThrowsAsync<HumanInterruptionException>(() =>
            dispatcher.DispatchAsync("ask", pascalJson));

        Assert.Single(ex.Questions);
        Assert.Equal("PascalCase prompt?", ex.Questions[0].Prompt);
        Assert.Equal("Testing PascalCase", ex.Questions[0].Rationale);
        Assert.Equal(QuestionType.SingleChoice, ex.Questions[0].QuestionType);

        // 2. Single object for Questions
        var singleObjJson = """
        {
          "questions": {
            "prompt": "Single object question?",
            "rationale": "Single object rationale"
          }
        }
        """;

        var exSingle = await Assert.ThrowsAsync<HumanInterruptionException>(() =>
            dispatcher.DispatchAsync("ask", singleObjJson));

        Assert.Single(exSingle.Questions);
        Assert.Equal("Single object question?", exSingle.Questions[0].Prompt);
        Assert.Equal("Single object rationale", exSingle.Questions[0].Rationale);
    }

    [Fact]
    public async Task CrucibleAgentHarness_ReAskInSameStage_CapturesNewInterruptionWithoutBeingBlockedByPreviousAnsweredAsk()
    {
        var chatClientMock = new Mock<IChatClient>();
        var functionCall = new FunctionCallContent("call-ask-2", "ask", new Dictionary<string, object?>
        {
            ["questions"] = new[]
            {
                new QuestionItem("Second question in same stage?", "Follow-up rationale")
            }
        });

        chatClientMock
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new ChatResponseUpdate
                {
                    Contents = { functionCall }
                }
            }.ToAsyncEnumerable());

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = chatClientMock.Object
        }.Build();

        // History already contains a previous ask and its answered tool result
        var existingHistoryJson = """
        [
          {"Role":"user","Contents":[{"Type":"text","Text":"Initial prompt"}]},
          {"Role":"assistant","Contents":[{"Type":"call","CallId":"call-ask-1","Name":"ask","Arguments":{"questions":[{"Prompt":"First question?"}]}}]},
          {"Role":"tool","Contents":[{"Type":"result","CallId":"call-ask-1","Result":"First answer"}]}
        ]
        """;

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 1, Title = "Test Task" },
            Workspace = new Workspace { Id = 1, Name = "Test WS", RepositoryPath = AppContext.BaseDirectory },
            Profile = new AgentProfile { Id = "test-prof", HarnessId = "crucible" },
            StageId = StandardStageIds.TaskReview,
            StageDirective = "Investigate requirements",
            RequiredMode = AgentMode.Full,
            WorkspacePath = AppContext.BaseDirectory,
            SerializedStateJson = existingHistoryJson
        };

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.WaitingForHuman, result.Status);
        Assert.NotNull(result.PendingQuestions);
        Assert.Single(result.PendingQuestions);
        Assert.Equal("Second question in same stage?", result.PendingQuestions[0].Prompt);
    }
}

