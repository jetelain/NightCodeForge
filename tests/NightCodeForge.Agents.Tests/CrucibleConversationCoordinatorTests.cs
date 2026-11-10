using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Agents.Tests;

public class CrucibleConversationCoordinatorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ncf-coord-" + Guid.NewGuid().ToString("N"));

    public CrucibleConversationCoordinatorTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static AgentSessionContext StageContext(
        int taskId,
        string bootstrapPrompt = "Do the thing",
        SyntheticPrimingExecution? priming = null,
        string? pendingAnswer = null,
        string? serializedState = null,
        Workspace? workspace = null) => new()
    {
        ForgeTask = new ForgeTask { Id = taskId, Title = "T", Description = "D" },
        StageId = StandardStageIds.Implementation,
        Workspace = workspace ?? new Workspace { Id = 1 },
        Profile = new AgentProfile { Id = "dev", Name = "Dev", ModelName = "m" },
        BootstrapPrompt = bootstrapPrompt,
        SyntheticPriming = priming,
        PendingHumanAnswer = pendingAnswer,
        SerializedStateJson = serializedState
    };
    private static CrucibleConversationCoordinator CreateCoordinator(
        ITaskRepository repository,
        SessionTranscriptService? transcripts = null,
        ILlmGatewayService? gateway = null) =>
        new(
            repository,
            transcripts ?? new SessionTranscriptService(),
            gateway ?? new Mock<ILlmGatewayService>().Object,
            NullLogger<CrucibleConversationCoordinator>.Instance);

    [Fact]
    public async Task Coordinator_Begin_StartsConversationAndSeedsBootstrapHistory()
    {
        var repository = new InMemoryTaskRepository();
        var coordinator = CreateCoordinator(repository);

        var state = await coordinator.BeginAsync(StageContext(21), StandardHarnessIds.Crucible, CancellationToken.None);

        Assert.Equal(StageExecutionStatus.Running, state.Conversation.Status);
        Assert.Equal(1, state.AttemptIndex);
        var message = Assert.Single(state.ChatHistory);
        Assert.Equal(ChatRole.User, message.Role);
        Assert.Equal("Do the thing", message.Text);
        var persisted = await repository.GetConversationsAsync(21);
        Assert.Single(persisted);
    }

    [Fact]
    public async Task Coordinator_Begin_WithoutBootstrapPromptUsesDefaultPrompt()
    {
        var context = StageContext(22, bootstrapPrompt: string.Empty);
        var coordinator = CreateCoordinator(new InMemoryTaskRepository());

        var state = await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);

        Assert.Equal(CrucibleConversationCoordinator.DefaultBootstrapPrompt, Assert.Single(state.ChatHistory).Text);
    }

    [Fact]
    public async Task Coordinator_Begin_SyntheticPrimingAddsToolExchangeAndPersistsTurns()
    {
        var repository = new InMemoryTaskRepository();
        var coordinator = CreateCoordinator(repository);
        var context = StageContext(23, priming: new SyntheticPrimingExecution("stage-2", CanonicalToolNames.Bash, "dotnet build", "error CS1002", IsSuccess: false));

        var state = await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);

        Assert.Contains(state.ChatHistory, m => m.Contents.OfType<FunctionCallContent>().Any(c => c.Name == CanonicalToolNames.Bash));
        Assert.Equal(CrucibleConversationCoordinator.FixDirectivePrompt, state.ChatHistory.Last().Text);
        var conversation = Assert.Single(await repository.GetConversationsAsync(23));
        var turns = (await repository.GetConversationWithTurnsAsync(conversation.Id))!.Turns!;
        Assert.Contains(turns, t => t.Role == StageTurnRole.Tool && t.ToolName == CanonicalToolNames.Bash);
        Assert.Contains(turns, t => t.Role == StageTurnRole.ToolResult && t.IsError);
    }

    [Fact]
    public async Task Coordinator_Begin_ResumingWaitingConversationCompletesPendingAskWithHumanAnswer()
    {
        var repository = new InMemoryTaskRepository();
        var coordinator = CreateCoordinator(repository);
        var first = await coordinator.BeginAsync(StageContext(24), StandardHarnessIds.Crucible, CancellationToken.None);
        var callId = "call-ask-1";
        first.TryRecordToolCall(callId);
        first.EnqueueTurn(
            StageTurnRole.Tool,
            "ask failed",
            toolName: CanonicalToolNames.Ask,
            toolArguments: "{}",
            metadataJson: System.Text.Json.JsonSerializer.Serialize(new { CallId = callId }));
        await first.DrainAsync();
        await repository.CompleteConversationAsync(first.Conversation.Id, StageExecutionStatus.WaitingForHuman, summary: "waiting");

        var resumeContext = StageContext(
            24,
            pendingAnswer: "Use SQLite",
            serializedState: CrucibleAgentHarness.SerializeSession(new List<ChatMessage>
            {
                new(ChatRole.User, "Do the thing"),
                new(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent(callId, CanonicalToolNames.Ask, new Dictionary<string, object?>()) })
            }));
        var resumed = await coordinator.BeginAsync(resumeContext, StandardHarnessIds.Crucible, CancellationToken.None);

        Assert.Equal(first.Conversation.Id, resumed.Conversation.Id);
        Assert.Contains(resumed.ChatHistory, m => m.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == callId && r.Result?.ToString() == "Use SQLite"));
        Assert.Equal(CrucibleConversationCoordinator.HumanAnswerFollowUpPrompt, resumed.ChatHistory.Last().Text);
        var turns = (await repository.GetConversationWithTurnsAsync(resumed.Conversation.Id))!.Turns!;
        Assert.Contains(turns, t => t.Role == StageTurnRole.ToolResult && t.ToolName == CanonicalToolNames.Ask && t.ToolResult == "Use SQLite");
    }

    [Fact]
    public async Task Coordinator_Begin_WaitingConversationWithoutAnswerIsCancelledAndSupersededByNewAttempt()
    {
        var repository = new InMemoryTaskRepository();
        var coordinator = CreateCoordinator(repository);
        var first = await coordinator.BeginAsync(StageContext(25), StandardHarnessIds.Crucible, CancellationToken.None);
        await repository.CompleteConversationAsync(first.Conversation.Id, StageExecutionStatus.WaitingForHuman, summary: "waiting");

        var second = await coordinator.BeginAsync(StageContext(25), StandardHarnessIds.Crucible, CancellationToken.None);

        Assert.NotEqual(first.Conversation.Id, second.Conversation.Id);
        Assert.Equal(2, second.AttemptIndex);
        var old = await repository.GetConversationWithTurnsAsync(first.Conversation.Id);
        Assert.Equal(StageExecutionStatus.Cancelled, old!.Status);
    }

    [Fact]
    public async Task Coordinator_SaveTranscript_WritesFileAndCompletesConversation()
    {
        var repository = new InMemoryTaskRepository();
        var transcripts = new SessionTranscriptService(Path.Combine(_tempDir, "docs", "transcripts"));
        var coordinator = CreateCoordinator(repository, transcripts);
        var context = StageContext(26, workspace: new Workspace { Id = 1, RepositoryPath = _tempDir });
        var state = await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);

        var fileName = await coordinator.SaveTranscriptAsync(
            state,
            new CrucibleTranscriptRequest(context, _tempDir, state.ChatHistory, 10, 1, 2, AgentRunStatus.Completed, "done", null, null, null),
            CancellationToken.None);

        Assert.NotNull(fileName);
        Assert.True(File.Exists(Path.Combine(_tempDir, "docs", "transcripts", fileName!)));
        var completed = await repository.GetConversationWithTurnsAsync(state.Conversation.Id);
        Assert.Equal(StageExecutionStatus.Completed, completed!.Status);
        Assert.Equal("done", completed.Summary);
    }

    [Fact]
    public async Task ConversationState_WritesTurnsInSubmissionOrderAndDeduplicatesToolCalls()
    {
        var repository = new InMemoryTaskRepository();
        var coordinator = CreateCoordinator(repository);
        var state = await coordinator.BeginAsync(StageContext(27), StandardHarnessIds.Crucible, CancellationToken.None);

        state.EnqueueTurn(StageTurnRole.Assistant, "f1", content: "one");
        state.EnqueueTurn(StageTurnRole.Assistant, "f2", content: "two");
        await state.DrainAsync();

        Assert.True(state.TryRecordToolCall("c1"));
        Assert.False(state.TryRecordToolCall("c1"));
        Assert.True(state.IsToolCallRecorded("c1"));
        var turns = (await repository.GetConversationWithTurnsAsync(state.Conversation.Id))!.Turns!;
        var assistant = turns.Where(t => t.Role == StageTurnRole.Assistant).OrderBy(t => t.TurnIndex).Select(t => t.Content).ToList();
        Assert.Equal(new[] { "one", "two" }, assistant);
    }

    [Fact]
    public async Task Begin_InterruptedConversationIsReopenedWithPersistedHistory()
    {
        var repository = new InMemoryTaskRepository();
        var coordinator = CreateCoordinator(repository);
        var first = await coordinator.BeginAsync(StageContext(30), StandardHarnessIds.Crucible, CancellationToken.None);
        first.EnqueueTurn(StageTurnRole.Assistant, "f", content: "partial work");
        await first.DrainAsync();
        await repository.CompleteConversationAsync(first.Conversation.Id, StageExecutionStatus.Interrupted, summary: "paused");

        var resumed = await coordinator.BeginAsync(StageContext(30), StandardHarnessIds.Crucible, CancellationToken.None);

        Assert.Equal(first.Conversation.Id, resumed.Conversation.Id);
        Assert.Equal(StageExecutionStatus.Running, resumed.Conversation.Status);
        Assert.Contains(resumed.ChatHistory, m => m.Text == "partial work");
    }

    [Fact]
    public async Task Begin_SuccessfulPrimingAddsNoFixDirective()
    {
        var coordinator = CreateCoordinator(new InMemoryTaskRepository());
        var context = StageContext(31, priming: new SyntheticPrimingExecution("stage-1", CanonicalToolNames.Bash, "dotnet test", "ok", IsSuccess: true));

        var state = await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);

        Assert.DoesNotContain(state.ChatHistory, m => m.Text == CrucibleConversationCoordinator.FixDirectivePrompt);
        Assert.Contains(state.ChatHistory, m => m.Contents.OfType<FunctionResultContent>().Any(r => r.Result?.ToString() == "ok"));
    }

    [Fact]
    public async Task Begin_AfterCompletedAttempt_StartsNextAttemptIndex()
    {
        var repository = new InMemoryTaskRepository();
        var coordinator = CreateCoordinator(repository);
        var first = await coordinator.BeginAsync(StageContext(32), StandardHarnessIds.Crucible, CancellationToken.None);
        await repository.CompleteConversationAsync(first.Conversation.Id, StageExecutionStatus.Completed, summary: "done");

        var second = await coordinator.BeginAsync(StageContext(32), StandardHarnessIds.Crucible, CancellationToken.None);

        Assert.NotEqual(first.Conversation.Id, second.Conversation.Id);
        Assert.Equal(2, second.AttemptIndex);
    }

    [Fact]
    public async Task Begin_ResumingSecondAttemptWithoutRetryCountInPipelineContext_PreservesAttemptIndex()
    {
        var repository = new InMemoryTaskRepository();
        var coordinator = CreateCoordinator(repository);
        var first = await coordinator.BeginAsync(StageContext(37), StandardHarnessIds.Crucible, CancellationToken.None);
        await repository.CompleteConversationAsync(first.Conversation.Id, StageExecutionStatus.Completed, summary: "first done");

        var second = await coordinator.BeginAsync(StageContext(37), StandardHarnessIds.Crucible, CancellationToken.None);
        Assert.Equal(2, second.AttemptIndex);
        await repository.CompleteConversationAsync(second.Conversation.Id, StageExecutionStatus.Interrupted, summary: "paused on attempt 2");

        var resumed = await coordinator.BeginAsync(StageContext(37), StandardHarnessIds.Crucible, CancellationToken.None);

        Assert.Equal(second.Conversation.Id, resumed.Conversation.Id);
        Assert.Equal(2, resumed.AttemptIndex);
    }

    [Fact]
    public async Task State_FindLastAskCallId_ReadsCallIdFromLastAskTurn()
    {
        var repository = new InMemoryTaskRepository();
        var coordinator = CreateCoordinator(repository);
        var first = await coordinator.BeginAsync(StageContext(33), StandardHarnessIds.Crucible, CancellationToken.None);
        first.EnqueueTurn(StageTurnRole.Tool, "f", toolName: CanonicalToolNames.Ask, metadataJson: "{\"CallId\":\"abc\"}");
        await first.DrainAsync();
        await repository.CompleteConversationAsync(first.Conversation.Id, StageExecutionStatus.Interrupted);

        var resumed = await coordinator.BeginAsync(StageContext(33), StandardHarnessIds.Crucible, CancellationToken.None);

        Assert.Equal("abc", resumed.FindLastAskCallId());
        Assert.True(resumed.IsToolCallRecorded("abc"));
    }

    [Fact]
    public async Task State_FailedWriteIsLoggedAndDoesNotBreakLaterWrites()
    {
        var coordinator = CreateCoordinator(new InMemoryTaskRepository());
        var state = await coordinator.BeginAsync(StageContext(34), StandardHarnessIds.Crucible, CancellationToken.None);
        var ran = false;

        state.EnqueueWrite(() => throw new InvalidOperationException("boom"), "write failed");
        state.EnqueueWrite(() => { ran = true; return Task.CompletedTask; }, "unused");
        await state.DrainAsync();

        Assert.True(ran);
    }

    [Fact]
    public async Task State_EnqueueTaskUpdate_PersistsTask()
    {
        var repository = new Mock<ITaskRepository>();
        var inner = new InMemoryTaskRepository();
        repository.Setup(r => r.GetResumableConversationAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns<int, string, CancellationToken>(inner.GetResumableConversationAsync);
        repository.Setup(r => r.GetConversationsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns<int, CancellationToken>(inner.GetConversationsAsync);
        repository.Setup(r => r.StartConversationAsync(
                It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<WorkflowStageKind>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<ConversationType>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Conversation { Id = 1, StageId = StandardStageIds.Implementation });        var coordinator = CreateCoordinator(repository.Object);
        var context = StageContext(35, bootstrapPrompt: string.Empty);
        var state = await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);

        state.EnqueueTaskUpdate(context.ForgeTask, CancellationToken.None);
        await state.DrainAsync();

        repository.Verify(r => r.UpdateAsync(context.ForgeTask, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void BuildTranscriptEntries_MapsReasoningToolCallsAndResultsWithToolNames()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new TextReasoningContent("thinking hard"),
                new FunctionCallContent("c1", CanonicalToolNames.Bash, new Dictionary<string, object?> { ["command"] = "ls" })
            }),
            new(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("c1", "file.txt") })
        };

        var entries = CrucibleConversationCoordinator.BuildTranscriptEntries(history);

        Assert.Contains(entries, e => e.Role == "THINKING" && e.Content == "thinking hard");
        Assert.Contains(entries, e => e.ToolName == CanonicalToolNames.Bash && e.ToolArguments!.Contains("ls"));
        Assert.Contains(entries, e => e.ToolName == CanonicalToolNames.Bash && e.ToolResult == "file.txt");
        Assert.Contains(entries, e => e.Content == "go");
    }

    [Fact]
    public void BuildTranscriptEntries_MarksCompactionCheckpoints()
    {
        var history = new List<ChatMessage> { new(ChatRole.User, "[CONTEXT COMPACTION CHECKPOINT] summary") };

        var entry = Assert.Single(CrucibleConversationCoordinator.BuildTranscriptEntries(history));

        Assert.Equal("COMPACTION", entry.Role);
    }

    [Fact]
    public void ExtractCallId_HandlesNullMissingAndMalformedMetadata()
    {
        Assert.Null(CrucibleConversationCoordinator.ExtractCallId(null));
        Assert.Null(CrucibleConversationCoordinator.ExtractCallId(new ConversationTurn { MetadataJson = "{}" }));
        Assert.Null(CrucibleConversationCoordinator.ExtractCallId(new ConversationTurn { MetadataJson = "not json" }));
        Assert.Equal("z", CrucibleConversationCoordinator.ExtractCallId(new ConversationTurn { MetadataJson = "{\"CallId\":\"z\"}" }));
    }

    [Theory]
    [InlineData(AgentRunStatus.Failed, StageExecutionStatus.Failed)]
    [InlineData(AgentRunStatus.Cancelled, StageExecutionStatus.Cancelled)]
    [InlineData(AgentRunStatus.Interrupted, StageExecutionStatus.Interrupted)]
    [InlineData(AgentRunStatus.WaitingForHuman, StageExecutionStatus.WaitingForHuman)]
    public async Task SaveTranscript_MapsRunStatusToConversationStatus(AgentRunStatus run, StageExecutionStatus expected)
    {
        var repository = new InMemoryTaskRepository();
        var coordinator = CreateCoordinator(repository, new SessionTranscriptService(Path.Combine(_tempDir, "t")));
        var context = StageContext(36, workspace: new Workspace { Id = 1, RepositoryPath = _tempDir });
        var state = await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);

        await coordinator.SaveTranscriptAsync(state, new CrucibleTranscriptRequest(context, _tempDir, state.ChatHistory, 1, 0, 0, run, "s", null, null, null), CancellationToken.None);

        Assert.Equal(expected, (await repository.GetConversationWithTurnsAsync(state.Conversation.Id))!.Status);
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var repo = new InMemoryTaskRepository();
        var transcripts = new SessionTranscriptService();
        var gateway = new Mock<ILlmGatewayService>().Object;
        var logger = NullLogger<CrucibleConversationCoordinator>.Instance;

        Assert.Throws<ArgumentNullException>(() => new CrucibleConversationCoordinator(null!, transcripts, gateway, logger));
        Assert.Throws<ArgumentNullException>(() => new CrucibleConversationCoordinator(repo, null!, gateway, logger));
        Assert.Throws<ArgumentNullException>(() => new CrucibleConversationCoordinator(repo, transcripts, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new CrucibleConversationCoordinator(repo, transcripts, gateway, null!));
    }
}
