using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Monitoring;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class IdeationPageAskToolTests : BunitContext
{
    private readonly WorkspaceStateService _workspaceState;
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly MockTaskMarkdownSyncService _markdownSync;
    private readonly MockIdeationService _ideationService;
    private readonly ToastService _toastService;
    private readonly IdeationSessionState _sessionState;
    private readonly InMemoryWorkflowRepository _workflowRepository;
    private readonly InMemoryWorkspaceRepository _workspaceRepository;

    public IdeationPageAskToolTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _workspaceState = new WorkspaceStateService();
        _taskRepository = new InMemoryTaskRepository();
        _workflowRepository = new InMemoryWorkflowRepository();
        _workspaceRepository = new InMemoryWorkspaceRepository();
        _workspaceRepository.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        _markdownSync = new MockTaskMarkdownSyncService();
        _ideationService = new MockIdeationService();
        _toastService = TestServices.CreateToastService();
        _sessionState = new IdeationSessionState();

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<IWorkflowRepository>(_workflowRepository);
        Services.AddSingleton<IWorkspaceRepository>(_workspaceRepository);
        Services.AddSingleton<ITaskMarkdownSyncService>(_markdownSync);
        Services.AddSingleton<IIdeationService>(_ideationService);
        Services.AddSingleton(_toastService);
        Services.AddSingleton(_sessionState);
    }

    [Fact]
    public async Task SingleQuestion_OptionClickDoesNotSubmitImmediately_SubmitButtonCompletesAnswer()
    {
        var ws = new Workspace { Id = 1, Name = "Repo", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(ws);
        _sessionState.BoundWorkspaceId = 1;

        var question = new IdeationQuestion(
            "Which authentication scheme should we use?",
            "Needed for API security design",
            new[] { "JWT Bearer", "Cookie Authentication" });

        _ideationService.StreamUpdatesToYield = new List<IdeationStreamUpdate>
        {
            new(IdeationStreamUpdateKind.ThinkingDelta, TextDelta: "Deciding on auth..."),
            new(IdeationStreamUpdateKind.QuestionAsked, Question: question, Questions: new[] { question })
        };

        var cut = Render<Ideation>();

        var textarea = cut.Find("textarea.chat-textarea");
        textarea.Input("How should we secure endpoints?");
        var sendBtn = cut.Find("button.send-btn");
        _ = cut.InvokeAsync(() => sendBtn.Click());

        // Wait for question card to appear
        cut.WaitForAssertion(() =>
        {
            var card = cut.FindComponent<AgentQuestionTurnCard>();
            Assert.NotNull(card);
            Assert.Contains("Which authentication scheme should we use?", cut.Markup);
        });

        // Verify question is not yet answered
        Assert.False(question.AnswerTask.IsCompleted);

        // Click option chip for "JWT Bearer"
        await cut.InvokeAsync(() =>
        {
            var optionBtns = cut.FindAll("button.option-btn");
            Assert.Equal(2, optionBtns.Count);
            Assert.Contains("JWT Bearer", optionBtns[0].TextContent);
            optionBtns[0].Click();
        });

        // CRITICAL CHECK: Option click should NOT submit immediately
        Assert.False(question.AnswerTask.IsCompleted);

        // Active option chip should have active class and textarea should contain the option text
        var answerInput = cut.Find("textarea.question-textarea");
        Assert.Equal("JWT Bearer", answerInput.GetAttribute("value"));

        // Click submit button
        await cut.InvokeAsync(() =>
        {
            var submitBtn = cut.Find("button.submit-answer-btn");
            Assert.False(submitBtn.HasAttribute("disabled"));
            submitBtn.Click();
        });

        // Verification: Question answer task completed with chosen option
        cut.WaitForAssertion(() => Assert.True(question.AnswerTask.IsCompleted));
        var answer = await question.AnswerTask;
        Assert.Equal("JWT Bearer", answer);

        // Question card updates to answered state
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Clarification Provided", cut.Markup);
            Assert.Contains("JWT Bearer", cut.Markup);
        });
    }

    [Fact]
    public async Task MultiQuestionBatch_RequiresAllAnswers_AndCompletesAllTasksWithoutHanging()
    {
        var ws = new Workspace { Id = 2, Name = "Repo2", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(ws);
        _sessionState.BoundWorkspaceId = 2;

        var q1 = new IdeationQuestion("Select database:", null, new[] { "Postgres", "SQLite" });
        var q2 = new IdeationQuestion("Select caching layer:", null, new[] { "Redis", "MemoryCache" });
        var batch = new[] { q1, q2 };

        _ideationService.StreamUpdatesToYield = new List<IdeationStreamUpdate>
        {
            new(IdeationStreamUpdateKind.QuestionAsked, Question: q1, Questions: batch)
        };

        var cut = Render<Ideation>();

        var textarea = cut.Find("textarea.chat-textarea");
        textarea.Input("Architect the backend");
        var sendBtn = cut.Find("button.send-btn");
        _ = cut.InvokeAsync(() => sendBtn.Click());

        cut.WaitForAssertion(() =>
        {
            var card = cut.FindComponent<AgentQuestionTurnCard>();
            Assert.NotNull(card);
            Assert.Contains("Q1", cut.Markup);
            Assert.Contains("Q2", cut.Markup);
        });

        // Submit button should be disabled when answers are missing (since RequireAllAnswers=true)
        var submitBtn = cut.Find("button.submit-answer-btn");
        Assert.True(submitBtn.HasAttribute("disabled"));

        // Options: Q1 (Postgres, SQLite), Q2 (Redis, MemoryCache)
        await cut.InvokeAsync(() =>
        {
            var allOptionBtns = cut.FindAll("button.option-btn");
            Assert.Equal(4, allOptionBtns.Count);
            Assert.Contains("Postgres", allOptionBtns[0].TextContent);
            allOptionBtns[0].Click();
        });

        // Still disabled because Q2 is not answered yet
        submitBtn = cut.Find("button.submit-answer-btn");
        Assert.True(submitBtn.HasAttribute("disabled"));

        // Select Redis for Q2
        await cut.InvokeAsync(() =>
        {
            var allOptionBtns = cut.FindAll("button.option-btn");
            Assert.Contains("Redis", allOptionBtns[2].TextContent);
            allOptionBtns[2].Click();
        });

        // Now submit button is enabled
        submitBtn = cut.Find("button.submit-answer-btn");
        Assert.False(submitBtn.HasAttribute("disabled"));

        // Click submit
        await cut.InvokeAsync(() =>
        {
            var btn = cut.Find("button.submit-answer-btn");
            btn.Click();
        });

        // Both question answer tasks MUST be completed immediately without hanging
        cut.WaitForAssertion(() =>
        {
            Assert.True(q1.AnswerTask.IsCompleted);
            Assert.True(q2.AnswerTask.IsCompleted);
        });

        var a1 = await q1.AnswerTask;
        var a2 = await q2.AnswerTask;

        Assert.Equal("Postgres", a1);
        Assert.Equal("Redis", a2);
    }

    [Fact]
    public async Task PersistedAskToolTurns_RenderAnsweredAgentQuestionTurnCard()
    {
        var ws = new Workspace { Id = 3, Name = "Repo3", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(ws);
        _sessionState.BoundWorkspaceId = 3;

        var conv = await _taskRepository.StartIdeationConversationAsync(3, "Prior Architecture Discussion");

        // Record User Turn
        await _taskRepository.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Can you configure the DB?");

        // Record Ask Tool Turn
        var askJson = "{\"questions\":[{\"prompt\":\"Which database engine?\",\"options\":[\"PostgreSQL\",\"MySQL\"],\"rationale\":\"Required for docker-compose\"}]}";
        var metadata = "{\"CallId\":\"call-db-select\"}";
        await _taskRepository.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Tool,
            toolName: CanonicalToolNames.Ask,
            toolArguments: askJson,
            metadataJson: metadata);

        // Record Tool Response
        var answerJson = "{\"answers\":{\"0\":\"PostgreSQL\"}}";
        await _taskRepository.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.ToolResult,
            toolName: CanonicalToolNames.Ask,
            toolResult: answerJson,
            metadataJson: metadata);

        await _taskRepository.CompleteConversationAsync(conv.Id, StageExecutionStatus.Completed);

        // Active conversation
        await _taskRepository.StartIdeationConversationAsync(3, "Active Discovery");

        var cut = Render<Ideation>();

        // Switch session select to prior conversation
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("select.session-select")));
        var select = cut.Find("select.session-select");
        await cut.InvokeAsync(() => select.Change(conv.Id.ToString()));

        // Assert that the AgentQuestionTurnCard is rendered with Answered state and PostgreSQL
        cut.WaitForAssertion(() =>
        {
            var card = cut.FindComponent<AgentQuestionTurnCard>();
            Assert.NotNull(card);
            Assert.Contains("Clarification Provided", cut.Markup);
            Assert.Contains("Which database engine?", cut.Markup);
            Assert.Contains("PostgreSQL", cut.Markup);
        });
    }

    [Fact]
    public async Task PersistedAskToolTurns_WithToolContentLog_DoesNotCorruptAnswerAndRendersSingleCard()
    {
        var ws = new Workspace { Id = 4, Name = "Repo4", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(ws);
        _sessionState.BoundWorkspaceId = 4;

        var conv = await _taskRepository.StartIdeationConversationAsync(4, "Architecture Review");

        await _taskRepository.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Configure logging system");

        // Record Ask Tool Turn with Content log (e.g. from orchestrator / harness)
        var askJson = "{\"questions\":[{\"prompt\":\"Which logging provider?\",\"options\":[\"Serilog\",\"NLog\"]}]}";
        var metadata = "{\"CallId\":\"call-log-select\"}";
        await _taskRepository.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Tool,
            content: "ask: Which logging provider?",
            toolName: CanonicalToolNames.Ask,
            toolArguments: askJson,
            metadataJson: metadata);

        // Record Tool Result
        var answerJson = "{\"answers\":{\"0\":\"Serilog\"}}";
        await _taskRepository.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.ToolResult,
            toolName: CanonicalToolNames.Ask,
            toolResult: answerJson,
            metadataJson: metadata);

        await _taskRepository.CompleteConversationAsync(conv.Id, StageExecutionStatus.Completed);
        await _taskRepository.StartIdeationConversationAsync(4, "Active Session");

        var cut = Render<Ideation>();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("select.session-select")));
        var select = cut.Find("select.session-select");
        await cut.InvokeAsync(() => select.Change(conv.Id.ToString()));

        cut.WaitForAssertion(() =>
        {
            var cards = cut.FindComponents<AgentQuestionTurnCard>();
            Assert.Single(cards);
            Assert.Contains("Clarification Provided", cut.Markup);
            Assert.Contains("Which logging provider?", cut.Markup);
            Assert.Contains("Serilog", cut.Markup);
            Assert.DoesNotContain("ask: Which logging provider?", cut.Markup);
        });
    }

    [Fact]
    public async Task CancelGeneration_WhileMultiQuestionBatchPending_CancelsAllPendingQuestions()
    {
        var ws = new Workspace { Id = 5, Name = "Repo5", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(ws);
        _sessionState.BoundWorkspaceId = 5;

        var q1 = new IdeationQuestion("Select queue:", null, new[] { "RabbitMQ", "Kafka" });
        var q2 = new IdeationQuestion("Select cache:", null, new[] { "Redis", "Memcached" });
        var batch = new[] { q1, q2 };

        _ideationService.StreamUpdatesToYield = new List<IdeationStreamUpdate>
        {
            new(IdeationStreamUpdateKind.QuestionAsked, Question: q1, Questions: batch)
        };

        var cut = Render<Ideation>();

        var textarea = cut.Find("textarea.chat-textarea");
        textarea.Input("Design message flow");
        var sendBtn = cut.Find("button.send-btn");
        _ = cut.InvokeAsync(() => sendBtn.Click());

        cut.WaitForAssertion(() =>
        {
            var card = cut.FindComponent<AgentQuestionTurnCard>();
            Assert.NotNull(card);
            Assert.Contains("Select queue:", cut.Markup);
        });

        Assert.False(q1.AnswerTask.IsCompleted);
        Assert.False(q2.AnswerTask.IsCompleted);

        // Click cancel button on active ideation generation
        var cancelBtn = cut.Find("button.cancel-btn");
        await cut.InvokeAsync(() => cancelBtn.Click());

        cut.WaitForAssertion(() =>
        {
            Assert.True(q1.AnswerTask.IsCompleted);
            Assert.True(q2.AnswerTask.IsCompleted);
            Assert.True(q1.AnswerTask.IsCanceled);
            Assert.True(q2.AnswerTask.IsCanceled);
        });
    }

    [Fact]
    public async Task MultipleAskToolCallsInSingleTurn_PersistsSequentialMetadataJson()
    {
        var ws = new Workspace { Id = 6, Name = "Repo6", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(ws);
        _sessionState.BoundWorkspaceId = 6;

        var q1 = new IdeationQuestion("Database?", null, new[] { "Postgres" });
        var q2 = new IdeationQuestion("Cache?", null, new[] { "Redis" });

        var tc1 = new IdeationToolCallInfo("call-1", CanonicalToolNames.Ask, "{\"questions\":[{\"prompt\":\"Database?\"}]}", "Postgres");
        var tc2 = new IdeationToolCallInfo("call-2", CanonicalToolNames.Ask, "{\"questions\":[{\"prompt\":\"Cache?\"}]}", "Redis");

        _ideationService.StreamUpdatesToYield = new List<IdeationStreamUpdate>
        {
            new(IdeationStreamUpdateKind.ToolCallStarted, ToolCall: tc1),
            new(IdeationStreamUpdateKind.QuestionAsked, Question: q1, Questions: new[] { q1 }),
            new(IdeationStreamUpdateKind.ToolCallCompleted, ToolCall: tc1),
            new(IdeationStreamUpdateKind.ToolCallStarted, ToolCall: tc2),
            new(IdeationStreamUpdateKind.QuestionAsked, Question: q2, Questions: new[] { q2 }),
            new(IdeationStreamUpdateKind.ToolCallCompleted, ToolCall: tc2),
            new(IdeationStreamUpdateKind.MessageDelta, TextDelta: "Architecture formulated.")
        };

        var cut = Render<Ideation>();

        var textarea = cut.Find("textarea.chat-textarea");
        textarea.Input("Start architecture");
        var sendBtn = cut.Find("button.send-btn");
        _ = cut.InvokeAsync(() => sendBtn.Click());

        // Answer Q1
        cut.WaitForAssertion(() => Assert.Contains("Database?", cut.Markup));
        var waitingCard1 = cut.Find(".agent-question-turn-card.waiting");
        var opt1 = waitingCard1.QuerySelector("button.option-btn");
        Assert.NotNull(opt1);
        await cut.InvokeAsync(() => opt1.Click());
        var submit1 = waitingCard1.QuerySelector("button.submit-answer-btn");
        Assert.NotNull(submit1);
        await cut.InvokeAsync(() => submit1.Click());

        // Answer Q2
        cut.WaitForAssertion(() => Assert.Contains("Cache?", cut.Markup));
        var waitingCard2 = cut.Find(".agent-question-turn-card.waiting");
        var opt2 = waitingCard2.QuerySelector("button.option-btn");
        Assert.NotNull(opt2);
        await cut.InvokeAsync(() => opt2.Click());
        var submit2 = waitingCard2.QuerySelector("button.submit-answer-btn");
        Assert.NotNull(submit2);
        await cut.InvokeAsync(() => submit2.Click());

        // Wait for generation to complete and turns to persist
        cut.WaitForAssertion(() =>
        {
            Assert.False(_sessionState.IsGenerating);
            Assert.Equal(2, _sessionState.Messages.Count);
        });

        // Verify turns in repository
        var convId = _sessionState.ActiveConversationId!.Value;
        var loaded = await _taskRepository.GetConversationWithTurnsAsync(convId);
        Assert.NotNull(loaded);

        var toolResultTurns = loaded.Turns.Where(t => t.Role == StageTurnRole.ToolResult && t.ToolName == CanonicalToolNames.Ask).ToList();
        Assert.Equal(2, toolResultTurns.Count);
        Assert.Contains("Postgres", toolResultTurns[0].MetadataJson ?? "");
        Assert.Contains("Redis", toolResultTurns[1].MetadataJson ?? "");
    }
}
