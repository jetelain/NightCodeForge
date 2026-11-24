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
using NightCodeForge.Core.Workflow;

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

    [Fact]
    public void BuildTranscriptEntries_PreservesRealPerTurnTimestamps_InMonotonicOrder()
    {
        var t0 = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddSeconds(5);
        var t2 = t0.AddSeconds(8);
        var t3 = t0.AddSeconds(14);
        var t4 = t0.AddSeconds(21);

        var chatHistory = new List<ChatMessage>
        {
            new(ChatRole.User, "Inspect codebase"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new TextReasoningContent("Let me read Program.cs"),
                new FunctionCallContent("c1", CanonicalToolNames.Read, new Dictionary<string, object?> { ["path"] = "Program.cs" })
            }),
            new(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("c1", "class Program {}") }),
            new(ChatRole.Assistant, "Done.")
        };

        var trackedTurns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.User, Timestamp = t0 },
            new() { TurnIndex = 1, Role = StageTurnRole.Thinking, Timestamp = t1 },
            new() { TurnIndex = 2, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Read, MetadataJson = "{\"CallId\":\"c1\"}", Timestamp = t2 },
            new() { TurnIndex = 3, Role = StageTurnRole.ToolResult, ToolName = CanonicalToolNames.Read, MetadataJson = "{\"CallId\":\"c1\"}", Timestamp = t3 },
            new() { TurnIndex = 4, Role = StageTurnRole.Assistant, Timestamp = t4 }
        };

        var entries = CrucibleConversationCoordinator.BuildTranscriptEntries(
            chatHistory,
            trackedTurns: trackedTurns,
            stageStartedAtUtc: t0);

        Assert.Equal(5, entries.Count);
        Assert.Equal(t0, entries[0].Timestamp);
        Assert.Equal(t1, entries[1].Timestamp);
        Assert.Equal(t2, entries[2].Timestamp);
        Assert.Equal(t3, entries[3].Timestamp);
        Assert.Equal(t4, entries[4].Timestamp);

        for (int i = 1; i < entries.Count; i++)
        {
            Assert.True(entries[i].Timestamp >= entries[i - 1].Timestamp);
        }
    }

    [Fact]
    public async Task SaveTranscript_WithInMemoryAndDatabaseCoordinators_RendersNonZeroElapsedOffsets()
    {
        var t0 = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddSeconds(7);
        var t2 = t0.AddSeconds(19);

        var chatHistory = new List<ChatMessage>
        {
            new(ChatRole.User, "Start stage"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("call-1", CanonicalToolNames.Bash, new Dictionary<string, object?> { ["command"] = "dotnet test" })
            }),
            new(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("call-1", "Passed") }),
            new(ChatRole.Assistant, "All green.")
        };

        // 1. InMemoryCrucibleConversationCoordinator path
        var inMemDir = Path.Combine(_tempDir, "inmem-transcripts");
        var targetPath = Path.Combine(inMemDir, "transcript.md");
        var capture = new InMemoryConversationCapture(targetPath);
        var inMemCoordinator = new InMemoryCrucibleConversationCoordinator(
            new SessionTranscriptService(inMemDir),
            NullLogger<InMemoryCrucibleConversationCoordinator>.Instance);
        var context = StageContext(88, workspace: new Workspace { Id = 1, RepositoryPath = _tempDir });

        using (InMemoryCrucibleConversationCoordinator.BeginCapture(capture))
        {
            var state = await inMemCoordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);
            await state.DrainAsync();
            state.Conversation.StartedAt = t0;
            var turns = state.GetTrackedTurnsSnapshot();
            turns[0].Timestamp = t0;
            state.TrackPersistedTurn(new ConversationTurn
            {
                Id = 9001,
                TurnIndex = 1,
                Role = StageTurnRole.Tool,
                ToolName = CanonicalToolNames.Bash,
                MetadataJson = "{\"CallId\":\"call-1\"}",
                Timestamp = t1
            });
            state.TrackPersistedTurn(new ConversationTurn
            {
                Id = 9002,
                TurnIndex = 2,
                Role = StageTurnRole.ToolResult,
                ToolName = CanonicalToolNames.Bash,
                MetadataJson = "{\"CallId\":\"call-1\"}",
                Timestamp = t1.AddSeconds(3)
            });
            state.TrackPersistedTurn(new ConversationTurn
            {
                Id = 9003,
                TurnIndex = 3,
                Role = StageTurnRole.Assistant,
                Timestamp = t2
            });

            await inMemCoordinator.SaveTranscriptAsync(
                state,
                new CrucibleTranscriptRequest(context, _tempDir, chatHistory, 19_000, 10, 5, AgentRunStatus.Completed, "done", null, null, null),
                CancellationToken.None);
        }

        var inMemMarkdown = await File.ReadAllTextAsync(targetPath);
        Assert.Contains("### [ 00:00 ]", inMemMarkdown);
        Assert.Contains("### [ 00:07 ]", inMemMarkdown);
        Assert.Contains("### [ 00:10 ]", inMemMarkdown);
        Assert.Contains("### [ 00:19 ]", inMemMarkdown);

        // 2. Database CrucibleConversationCoordinator path
        var dbDir = Path.Combine(_tempDir, "db-transcripts");
        var repository = new InMemoryTaskRepository();
        var dbCoordinator = CreateCoordinator(repository, new SessionTranscriptService(dbDir));
        var dbState = await dbCoordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);
        dbState.Conversation.StartedAt = t0;
        dbState.GetTrackedTurnsSnapshot()[0].Timestamp = t0;
        dbState.TrackPersistedTurn(new ConversationTurn
        {
            Id = 9101,
            TurnIndex = 1,
            Role = StageTurnRole.Tool,
            ToolName = CanonicalToolNames.Bash,
            MetadataJson = "{\"CallId\":\"call-1\"}",
            Timestamp = t1
        });
        dbState.TrackPersistedTurn(new ConversationTurn
        {
            Id = 9102,
            TurnIndex = 2,
            Role = StageTurnRole.ToolResult,
            ToolName = CanonicalToolNames.Bash,
            MetadataJson = "{\"CallId\":\"call-1\"}",
            Timestamp = t1.AddSeconds(3)
        });
        dbState.TrackPersistedTurn(new ConversationTurn
        {
            Id = 9103,
            TurnIndex = 3,
            Role = StageTurnRole.Assistant,
            Timestamp = t2
        });

        var fileName = await dbCoordinator.SaveTranscriptAsync(
            dbState,
            new CrucibleTranscriptRequest(context, _tempDir, chatHistory, 19_000, 10, 5, AgentRunStatus.Completed, "done", null, null, null),
            CancellationToken.None);
        var dbMarkdown = await File.ReadAllTextAsync(Path.Combine(_tempDir, "docs", "transcripts", fileName!));

        Assert.Contains("### [ 00:00 ]", dbMarkdown);
        Assert.Contains("### [ 00:07 ]", dbMarkdown);
        Assert.Contains("### [ 00:10 ]", dbMarkdown);
        Assert.Contains("### [ 00:19 ]", dbMarkdown);
    }

    [Fact]
    public void BuildTranscriptEntries_WithOnlyChatMessageCreatedAt_PreservesHistoricalTimestamps()
    {
        var t0 = new DateTimeOffset(2024, 3, 15, 8, 30, 0, TimeSpan.Zero);
        var t1 = t0.AddSeconds(12);
        var t2 = t0.AddSeconds(25);

        var chatHistory = new List<ChatMessage>
        {
            new(ChatRole.User, "First") { CreatedAt = t0 },
            new(ChatRole.Assistant, [new FunctionCallContent("c1", CanonicalToolNames.Read)]) { CreatedAt = t1 },
            new(ChatRole.Assistant, "Final") { CreatedAt = t2 }
        };

        var entries = CrucibleConversationCoordinator.BuildTranscriptEntries(chatHistory);

        Assert.Equal(3, entries.Count);
        Assert.Equal(t0.UtcDateTime, entries[0].Timestamp);
        Assert.Equal(t1.UtcDateTime, entries[1].Timestamp);
        Assert.Equal(t2.UtcDateTime, entries[2].Timestamp);
    }

    [Fact]
    public void BuildTranscriptEntries_AssistantTextWithToolCall_DoesNotStealFutureAssistantTimestamp()
    {
        var t0 = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddSeconds(4);
        var t2 = t0.AddSeconds(9);
        var t3 = t0.AddSeconds(15);

        var chatHistory = new List<ChatMessage>
        {
            new(ChatRole.User, "Go"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new TextContent("Calling tool now..."),
                new FunctionCallContent("c1", CanonicalToolNames.Read)
            }),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "ok")]),
            new(ChatRole.Assistant, "All done")
        };

        var trackedTurns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.User, Timestamp = t0 },
            new() { TurnIndex = 1, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Read, MetadataJson = "{\"CallId\":\"c1\"}", Timestamp = t1 },
            new() { TurnIndex = 2, Role = StageTurnRole.ToolResult, ToolName = CanonicalToolNames.Read, MetadataJson = "{\"CallId\":\"c1\"}", Timestamp = t2 },
            new() { TurnIndex = 3, Role = StageTurnRole.Assistant, Timestamp = t3 }
        };

        var entries = CrucibleConversationCoordinator.BuildTranscriptEntries(chatHistory, trackedTurns: trackedTurns, stageStartedAtUtc: t0);

        Assert.Equal(4, entries.Count);
        Assert.Equal(t0, entries[0].Timestamp);
        Assert.Equal(t1, entries[1].Timestamp);
        Assert.Equal(t2, entries[2].Timestamp);
        Assert.Equal(t3, entries[3].Timestamp);
    }

    [Fact]
    public void BuildTranscriptEntries_WithTracedEntriesFallback_UsesTrackedTurnsPrecedenceAndResolvesAllRoles()
    {
        var t0 = new DateTime(2026, 11, 24, 11, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddSeconds(3);
        var t2 = t0.AddSeconds(6);
        var t3 = t0.AddSeconds(10);
        var t4 = t0.AddSeconds(14);

        var chatHistory = new List<ChatMessage>
        {
            new(ChatRole.User, "Run"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new TextReasoningContent("Thinking..."),
                new FunctionCallContent("c1", CanonicalToolNames.Bash)
            }),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "ok")]),
            new(ChatRole.Assistant, "Done")
        };

        var tracedEntries = new List<TranscriptEntry>
        {
            new(t0, "USER", "Run"),
            new(t1, "THINKING", "Thinking..."),
            new(t2, "TOOL", string.Empty, CanonicalToolNames.Bash, CallId: "c1"),
            new(t3, "TOOL_RESULT", string.Empty, CanonicalToolNames.Bash, CallId: "c1"),
            new(t4, "ASSISTANT", "Done")
        };

        var fromTracedOnly = CrucibleConversationCoordinator.BuildTranscriptEntries(
            chatHistory,
            tracedEntries: tracedEntries,
            stageStartedAtUtc: t0);

        Assert.Equal(5, fromTracedOnly.Count);
        Assert.Equal([t0, t1, t2, t3, t4], fromTracedOnly.Select(e => e.Timestamp).ToArray());

        // When trackedTurns provides a different timestamp for CallId "c1", trackedTurns takes precedence over tracedEntries.
        var trackedOverrideTs = t2.AddSeconds(1);
        var trackedTurns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Bash, MetadataJson = "{\"CallId\":\"c1\"}", Timestamp = trackedOverrideTs }
        };

        var withBoth = CrucibleConversationCoordinator.BuildTranscriptEntries(
            chatHistory,
            trackedTurns: trackedTurns,
            tracedEntries: tracedEntries,
            stageStartedAtUtc: t0);

        Assert.Equal(trackedOverrideTs, withBoth[2].Timestamp);
    }

    [Fact]
    public void BuildTranscriptEntries_WithCompactedEarlierToolTurn_DrainsStaleQueueWithoutStealingFutureTimestamps()
    {
        var t0 = new DateTime(2026, 11, 24, 13, 0, 0, DateTimeKind.Utc);
        var tCompacted = t0.AddSeconds(2);
        var tCall1 = t0.AddSeconds(10);
        var tCall2 = t0.AddSeconds(20);

        var chatHistory = new List<ChatMessage>
        {
            new(ChatRole.User, "Continue"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", CanonicalToolNames.Read)]),
            new(ChatRole.Assistant, [new FunctionCallContent(string.Empty, CanonicalToolNames.Bash)])
        };

        var trackedTurns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.User, Timestamp = t0 },
            new() { TurnIndex = 1, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Read, MetadataJson = "{\"CallId\":\"c0\"}", Timestamp = tCompacted },
            new() { TurnIndex = 2, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Read, MetadataJson = "{\"CallId\":\"c1\"}", Timestamp = tCall1 },
            new() { TurnIndex = 3, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Bash, Timestamp = tCall2 }
        };

        var entries = CrucibleConversationCoordinator.BuildTranscriptEntries(chatHistory, trackedTurns: trackedTurns, stageStartedAtUtc: t0);

        Assert.Equal(3, entries.Count);
        Assert.Equal(t0, entries[0].Timestamp);
        Assert.Equal(tCall1, entries[1].Timestamp);
        Assert.Equal(tCall2, entries[2].Timestamp);
    }

    [Fact]
    public void BuildTranscriptEntries_HostTurnTimestampsTakePrecedenceOverSkewedMessageCreatedAt_AndIgnoresUnixEpoch()
    {
        var t0 = new DateTime(2026, 11, 24, 14, 0, 0, DateTimeKind.Utc);
        var skewedEarly = new DateTimeOffset(t0.AddSeconds(-20), TimeSpan.Zero);
        var batchedToolMsgCreated = new DateTimeOffset(t0.AddSeconds(2), TimeSpan.Zero);

        var chatHistory = new List<ChatMessage>
        {
            new(ChatRole.User, "Run suite") { CreatedAt = DateTimeOffset.UnixEpoch },
            new(ChatRole.Assistant, new List<AIContent>
            {
                new TextReasoningContent("Planning two commands"),
                new FunctionCallContent("c1", CanonicalToolNames.Bash),
                new FunctionCallContent("c2", CanonicalToolNames.Read)
            })
            {
                CreatedAt = skewedEarly
            },
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("c1", "ok-1"),
                new FunctionResultContent("c2", "ok-2")
            })
            {
                CreatedAt = batchedToolMsgCreated
            }
        };

        var trackedTurns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.User, Content = "Run suite", Timestamp = t0 },
            new() { TurnIndex = 1, Role = StageTurnRole.Thinking, ThinkingTrace = "Planning two commands", Timestamp = t0.AddSeconds(12) },
            new() { TurnIndex = 2, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Bash, MetadataJson = "{\"CallId\":\"c1\"}", Timestamp = t0.AddSeconds(13) },
            new() { TurnIndex = 3, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Read, MetadataJson = "{\"CallId\":\"c2\"}", Timestamp = t0.AddSeconds(14) },
            new() { TurnIndex = 4, Role = StageTurnRole.ToolResult, ToolName = CanonicalToolNames.Bash, MetadataJson = "{\"CallId\":\"c1\"}", Timestamp = t0.AddSeconds(35) },
            new() { TurnIndex = 5, Role = StageTurnRole.ToolResult, ToolName = CanonicalToolNames.Read, MetadataJson = "{\"CallId\":\"c2\"}", Timestamp = t0.AddSeconds(38) }
        };

        var entries = CrucibleConversationCoordinator.BuildTranscriptEntries(chatHistory, trackedTurns: trackedTurns);

        Assert.Equal(6, entries.Count);
        Assert.Equal(
            [t0, t0.AddSeconds(12), t0.AddSeconds(13), t0.AddSeconds(14), t0.AddSeconds(35), t0.AddSeconds(38)],
            entries.Select(e => e.Timestamp).ToArray());
    }

    [Fact]
    public void BuildTranscriptEntries_WithUnpersistedBootstrapUserPrompt_DoesNotStealLaterContinuationUserTimestamp()
    {
        var t0 = new DateTime(2026, 11, 24, 15, 0, 0, DateTimeKind.Utc);
        var tTool = t0.AddSeconds(9);
        var tResult = t0.AddSeconds(14);
        var tNudge = t0.AddSeconds(25);
        var tFinal = t0.AddSeconds(33);

        var chatHistory = new List<ChatMessage>
        {
            new(ChatRole.User, CrucibleConversationCoordinator.DefaultBootstrapPrompt),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", CanonicalToolNames.Read)]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "contents")]),
            new(ChatRole.User, CrucibleAgentHarness.TruncationContinuationPrompt),
            new(ChatRole.Assistant, "Completed.")
        };

        // Initial DefaultBootstrapPrompt was not persisted in trackedTurns, only the later continuation prompt.
        var trackedTurns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Read, MetadataJson = "{\"CallId\":\"c1\"}", Timestamp = tTool },
            new() { TurnIndex = 1, Role = StageTurnRole.ToolResult, ToolName = CanonicalToolNames.Read, MetadataJson = "{\"CallId\":\"c1\"}", Timestamp = tResult },
            new() { TurnIndex = 2, Role = StageTurnRole.User, Content = CrucibleAgentHarness.TruncationContinuationPrompt, Timestamp = tNudge },
            new() { TurnIndex = 3, Role = StageTurnRole.Assistant, Content = "Completed.", Timestamp = tFinal }
        };

        var entries = CrucibleConversationCoordinator.BuildTranscriptEntries(
            chatHistory,
            trackedTurns: trackedTurns,
            stageStartedAtUtc: t0);

        Assert.Equal(5, entries.Count);
        Assert.Equal([t0, tTool, tResult, tNudge, tFinal], entries.Select(e => e.Timestamp).ToArray());
    }

    [Fact]
    public async Task BuildTranscriptEntries_WithMidStageCompactionAndKeptRecentTurns_PreservesKeptAndPostCompactionTimestamps()
    {
        var t0 = new DateTime(2026, 11, 24, 16, 0, 0, DateTimeKind.Utc);

        var chatHistory = new List<ChatMessage>
        {
            new(ChatRole.User, "Implement feature"),
            new(ChatRole.User, "[CONTEXT COMPACTION CHECKPOINT]\nSummarized earlier exploration."),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new TextReasoningContent("Kept recent reasoning"),
                new FunctionCallContent("c2", CanonicalToolNames.Bash)
            }),
            new(ChatRole.Tool, [new FunctionResultContent("c2", "Build succeeded")]),
            new(ChatRole.Assistant, "Feature complete.")
        };

        var trackedTurns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.User, Content = "Implement feature", Timestamp = t0 },
            new() { TurnIndex = 1, Role = StageTurnRole.Thinking, ThinkingTrace = "Old compacted thought", Timestamp = t0.AddSeconds(5) },
            new() { TurnIndex = 2, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Read, MetadataJson = "{\"CallId\":\"c1\"}", Timestamp = t0.AddSeconds(6) },
            new() { TurnIndex = 3, Role = StageTurnRole.ToolResult, ToolName = CanonicalToolNames.Read, MetadataJson = "{\"CallId\":\"c1\"}", Timestamp = t0.AddSeconds(8) },
            new() { TurnIndex = 4, Role = StageTurnRole.Assistant, Content = "Old interim note", Timestamp = t0.AddSeconds(12) },
            new() { TurnIndex = 5, Role = StageTurnRole.Thinking, ThinkingTrace = "Kept recent reasoning", Timestamp = t0.AddSeconds(40) },
            new() { TurnIndex = 6, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Bash, MetadataJson = "{\"CallId\":\"c2\"}", Timestamp = t0.AddSeconds(42) },
            new() { TurnIndex = 7, Role = StageTurnRole.ToolResult, ToolName = CanonicalToolNames.Bash, MetadataJson = "{\"CallId\":\"c2\"}", Timestamp = t0.AddSeconds(50) },
            new() { TurnIndex = 8, Role = StageTurnRole.Optimization, Content = "[CONTEXT COMPACTION CHECKPOINT]\nSummarized earlier exploration.", Timestamp = t0.AddSeconds(55) },
            new() { TurnIndex = 9, Role = StageTurnRole.Assistant, Content = "Feature complete.", Timestamp = t0.AddSeconds(65) }
        };

        var entries = CrucibleConversationCoordinator.BuildTranscriptEntries(
            chatHistory,
            trackedTurns: trackedTurns,
            stageStartedAtUtc: t0);

        Assert.Equal(6, entries.Count);
        Assert.Equal(t0, entries[0].Timestamp);
        Assert.Equal(t0.AddSeconds(40), entries[1].Timestamp); // Compaction divider capped at first kept turn
        Assert.Equal(t0.AddSeconds(40), entries[2].Timestamp); // Kept recent Thinking
        Assert.Equal(t0.AddSeconds(42), entries[3].Timestamp); // Kept recent Tool c2
        Assert.Equal(t0.AddSeconds(50), entries[4].Timestamp); // Kept recent ToolResult c2
        Assert.Equal(t0.AddSeconds(65), entries[5].Timestamp); // Post-compaction Assistant

        // Also verify InMemoryCrucibleConversationCoordinator synthetic priming stamps CallId metadata
        var inMemCoordinator = new InMemoryCrucibleConversationCoordinator(
            new SessionTranscriptService(Path.Combine(_tempDir, "inmem-prime")),
            NullLogger<InMemoryCrucibleConversationCoordinator>.Instance);
        var primedContext = StageContext(
            89,
            priming: new SyntheticPrimingExecution("stage-1", CanonicalToolNames.Bash, "dotnet build", "ok", IsSuccess: true));
        var primedState = await inMemCoordinator.BeginAsync(primedContext, StandardHarnessIds.Crucible, CancellationToken.None);
        await primedState.DrainAsync();
        var primedTurns = primedState.GetTrackedTurnsSnapshot();
        var primedToolTurn = Assert.Single(primedTurns, t => t.Role == StageTurnRole.Tool);
        var primedResultTurn = Assert.Single(primedTurns, t => t.Role == StageTurnRole.ToolResult);
        var primeCallId = CrucibleConversationCoordinator.ExtractCallId(primedToolTurn);
        Assert.False(string.IsNullOrWhiteSpace(primeCallId));
        Assert.Equal(primeCallId, CrucibleConversationCoordinator.ExtractCallId(primedResultTurn));
    }

    [Fact]
    public async Task Coordinator_Begin_CombinedPreFilteredPrimingPreservesAllSectionsWithoutDoubleFiltering()
    {
        var repository = new InMemoryTaskRepository();
        var coordinator = CreateCoordinator(repository);
        var combinedOutput = string.Join("\n",
            "Exit Code: 0",
            "=== [build] dotnet build --no-incremental ===",
            "Build succeeded.",
            "    0 Warning(s)",
            "",
            "=== [test] dotnet test --no-build ===",
            "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12");

        var priming = new SyntheticPrimingExecution(
            SourceStageId: StandardStageIds.PreImplementationBaseline,
            ToolName: CanonicalToolNames.Bash,
            Command: "dotnet build --no-incremental && dotnet test --no-build",
            Output: combinedOutput,
            IsSuccess: true,
            DurationMs: 500,
            IsPreFiltered: true);

        var context = StageContext(38, priming: priming);
        var state = await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);

        var primedResult = state.ChatHistory
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Single().Result?.ToString();
        var primedCall = state.ChatHistory
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Single();

        Assert.Equal("dotnet build --no-incremental && dotnet test --no-build", primedCall.Arguments?["command"]?.ToString());
        Assert.Equal(combinedOutput, primedResult);
        Assert.Contains("=== [build] dotnet build --no-incremental ===", primedResult);
        Assert.Contains("=== [test] dotnet test --no-build ===", primedResult);
        Assert.Contains("Build succeeded.", primedResult);
        Assert.Contains("Passed!  - Failed:     0", primedResult);
    }

    [Fact]
    public async Task Coordinator_Begin_CombinedPreFilteredPrimingStillEnforcesMaxToolInlineCharsCap()
    {
        var repository = new InMemoryTaskRepository();
        var coordinator = CreateCoordinator(repository);
        var oversizedBody = new string('x', WorkflowDefaults.MaxToolInlineChars + 2000);
        var combinedOutput = $"Exit Code: 0\n=== [build] dotnet build --no-incremental ===\nBuild succeeded.\n\n=== [test] dotnet test --no-build ===\n{oversizedBody}";

        var priming = new SyntheticPrimingExecution(
            SourceStageId: StandardStageIds.PreImplementationBaseline,
            ToolName: CanonicalToolNames.Bash,
            Command: "dotnet build --no-incremental && dotnet test --no-build",
            Output: combinedOutput,
            IsSuccess: true,
            DurationMs: 500,
            IsPreFiltered: true);

        var context = StageContext(39, priming: priming);
        var state = await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);

        var primedResult = state.ChatHistory
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Single().Result?.ToString();

        Assert.NotNull(primedResult);
        Assert.True(primedResult.Length <= WorkflowDefaults.MaxToolInlineChars);
        Assert.Contains("=== [build] dotnet build --no-incremental ===", primedResult);
        Assert.Contains("... additional ", primedResult);
    }

    [Fact]
    public async Task Coordinator_Begin_CombinedPrimingWithLegacyFalseIsPreFiltered_StillSkipsSmartFilterViaSectionHeaderAndHandlesEmptyOutput()
    {
        var repository = new InMemoryTaskRepository();
        var coordinator = CreateCoordinator(repository);
        var combinedOutput = string.Join("\n",
            "Exit Code: 0",
            "=== [build] dotnet build --no-incremental ===",
            "Build succeeded.",
            "",
            "=== [test] dotnet test --no-build ===",
            "Passed Suite.Test_1 [2 ms]",
            "Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1");

        var priming = new SyntheticPrimingExecution(
            SourceStageId: StandardStageIds.PreImplementationBaseline,
            ToolName: CanonicalToolNames.Bash,
            Command: "dotnet build --no-incremental && dotnet test --no-build",
            Output: combinedOutput,
            IsSuccess: true,
            DurationMs: 250,
            IsPreFiltered: false);

        var state = await coordinator.BeginAsync(StageContext(40, priming: priming), StandardHarnessIds.Crucible, CancellationToken.None);
        var primedResult = state.ChatHistory
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Single().Result?.ToString();

        Assert.Equal(combinedOutput, primedResult);
        Assert.Contains("=== [build] dotnet build --no-incremental ===", primedResult);
        Assert.Contains("=== [test] dotnet test --no-build ===", primedResult);
        Assert.DoesNotContain("[This is a simplified output]", primedResult);

        var nullOutputPriming = new SyntheticPrimingExecution(
            SourceStageId: StandardStageIds.PreImplementationBaseline,
            ToolName: CanonicalToolNames.Bash,
            Command: "dotnet build --no-incremental",
            Output: null!,
            IsSuccess: true,
            IsPreFiltered: false);

        var nullState = await coordinator.BeginAsync(StageContext(41, priming: nullOutputPriming), StandardHarnessIds.Crucible, CancellationToken.None);
        Assert.Contains(nullState.ChatHistory, m => m.Contents.OfType<FunctionResultContent>().Any());
    }

    [Fact]
    public void BuildTranscriptEntries_AssistantInterimTextWithToolCall_DoesNotConsumeDifferentFinalAssistantTurn()
    {
        var t0 = new DateTime(2026, 11, 24, 18, 0, 0, DateTimeKind.Utc);
        var tTool = t0.AddSeconds(5);
        var tResult = t0.AddSeconds(7);
        var tFinal = t0.AddSeconds(15);

        var chatHistory = new List<ChatMessage>
        {
            new(ChatRole.User, "Inspect files"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new TextContent("Let me inspect Program.cs first."),
                new FunctionCallContent("c1", CanonicalToolNames.Read)
            }),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "class Program {}")]),
            new(ChatRole.Assistant, "All checks passed.")
        };

        // Only the final assistant message was persisted as an Assistant turn (not the interim text attached to tool call c1)
        var trackedTurns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.User, Content = "Inspect files", Timestamp = t0 },
            new() { TurnIndex = 1, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Read, MetadataJson = "{\"CallId\":\"c1\"}", Timestamp = tTool },
            new() { TurnIndex = 2, Role = StageTurnRole.ToolResult, ToolName = CanonicalToolNames.Read, MetadataJson = "{\"CallId\":\"c1\"}", Timestamp = tResult },
            new() { TurnIndex = 3, Role = StageTurnRole.Assistant, Content = "All checks passed.", Timestamp = tFinal }
        };

        var entries = CrucibleConversationCoordinator.BuildTranscriptEntries(
            chatHistory,
            trackedTurns: trackedTurns,
            stageStartedAtUtc: t0);

        Assert.Equal(4, entries.Count);
        Assert.Equal([t0, tTool, tResult, tFinal], entries.Select(e => e.Timestamp).ToArray());
    }
}
