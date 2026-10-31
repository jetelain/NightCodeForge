using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Streaming;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public class CrucibleAgentHarnessPauseTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "crucible-pause-" + Guid.NewGuid().ToString("N"));

    public CrucibleAgentHarnessPauseTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> _streamHandler;

        public ScriptedChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> streamHandler)
        {
            _streamHandler = streamHandler ?? throw new ArgumentNullException(nameof(streamHandler));
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => _streamHandler(chatMessages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RecordingStreamer : IAgentActivityStreamer
    {
        private readonly List<AgentActivityEvent> _events = new();
        public IReadOnlyList<AgentActivityEvent> Events
        {
            get { lock (_events) return _events.ToList(); }
        }

        public Task BroadcastAsync(AgentActivityEvent activityEvent, CancellationToken cancellationToken = default)
        {
            lock (_events) _events.Add(activityEvent);
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<AgentActivityEvent> SubscribeAsync(int? taskId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private AgentSessionContext CreateContext(ForgeTask task, string? serializedState = null, SyntheticPrimingExecution? syntheticPriming = null)
    {
        return new AgentSessionContext
        {
            ForgeTask = task,
            StageId = "task-implementation",
            BootstrapPrompt = "Implement the requested feature",
            SerializedStateJson = serializedState,
            SyntheticPriming = syntheticPriming,
            WorkspacePath = _tempDir,
            Workspace = new Workspace { Id = 1, Name = "TestWorkspace", RepositoryPath = _tempDir },
            Profile = new AgentProfile
            {
                Id = "dev",
                Name = "Developer Agent",
                ModelName = "qwen2.5-coder:32b",
                Mode = AgentMode.Full,
                PermittedTools = new[] { CanonicalToolNames.Read, CanonicalToolNames.Write }
            }
        };
    }

    [Fact]
    public async Task RunStageAsync_WhenPauseRequested_SavesInterruptedStatusWithoutError()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 42, Title = "Pause Test Task", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        var cancellationManager = new TaskCancellationManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskCancellationManager>.Instance);
        var streamer = new RecordingStreamer();

        async IAsyncEnumerable<ChatResponseUpdate> Handler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            cancellationManager.RequestPause(task.Id);
            throw new OperationCanceledException();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        var client = new ScriptedChatClient(Handler);

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo,
            CancellationManager = cancellationManager,
            ActivityStreamer = streamer
        }.Build();

        var context = CreateContext(task);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.RunStageAsync(context));

        // Verify activity broadcast is Interrupted, and NOT Error
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.Interrupted);
        Assert.DoesNotContain(streamer.Events, e => e.Kind == AgentActivityKind.Error);

        // Verify conversation is Interrupted
        var convs = await repo.GetConversationsAsync(task.Id);
        var conv = Assert.Single(convs);
        Assert.Equal(StageExecutionStatus.Interrupted, conv.Status);
    }

    [Fact]
    public void SanitizeInFlightTurns_WhenAssistantTurnHasUnfulfilledToolCall_RollsBackAssistantTurn()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.User, "Do something"),
            new(ChatRole.Assistant, [
                new FunctionCallContent("call-1", "read", new Dictionary<string, object?> { ["path"] = "foo.cs" })
            ])
        };

        CrucibleAgentHarness.SanitizeInFlightTurns(messages);

        // Incomplete assistant message must be pruned
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal(ChatRole.User, messages[1].Role);
    }

    [Fact]
    public void SanitizeInFlightTurns_WhenAssistantTurnHasMatchingToolResult_PreservesTurns()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.User, "Do something"),
            new(ChatRole.Assistant, [
                new FunctionCallContent("call-1", "read", new Dictionary<string, object?> { ["path"] = "foo.cs" })
            ]),
            new(ChatRole.Tool, [
                new FunctionResultContent("call-1", "file content")
            ])
        };

        CrucibleAgentHarness.SanitizeInFlightTurns(messages);

        // All 4 messages must remain intact since tool result was recorded
        Assert.Equal(4, messages.Count);
        Assert.Equal(ChatRole.Tool, messages[3].Role);
    }

    [Fact]
    public void SanitizeInFlightTurns_WhenAssistantTurnHasOnlyText_PreservesTurns()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hello there! How can I help?")
        };

        CrucibleAgentHarness.SanitizeInFlightTurns(messages);

        Assert.Equal(3, messages.Count);
        Assert.Equal(ChatRole.Assistant, messages[2].Role);
        Assert.Equal("Hello there! How can I help?", messages[2].Text);
    }

    [Fact]
    public void SanitizeInFlightTurns_WhenMultipleToolCallsAndOneUnfulfilled_RollsBackAssistantTurn()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Do multiple things"),
            new(ChatRole.Assistant, [
                new FunctionCallContent("call-1", "read", new Dictionary<string, object?> { ["path"] = "foo.cs" }),
                new FunctionCallContent("call-2", "write", new Dictionary<string, object?> { ["path"] = "bar.cs" })
            ]),
            new(ChatRole.Tool, [
                new FunctionResultContent("call-1", "foo content")
            ])
        };

        // call-2 was never answered
        CrucibleAgentHarness.SanitizeInFlightTurns(messages);

        // Assistant turn must be removed because call-2 has no result
        Assert.Single(messages);
        Assert.Equal(ChatRole.User, messages[0].Role);
    }

    [Fact]
    public async Task RunStageAsync_AfterPauseAndResume_ResumesSameConversationInPlace_PreservesPriorTurns_AndSkipsDuplicatePriming()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 43, Title = "Pause and Resume Task", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        var cancellationManager = new TaskCancellationManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskCancellationManager>.Instance);
        int callCount = 0;
        var capturedIncomingMessages = new List<List<ChatMessage>>();

        async IAsyncEnumerable<ChatResponseUpdate> Handler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            callCount++;
            capturedIncomingMessages.Add(messages.ToList());
            await Task.Yield();

            if (callCount == 1)
            {
                cancellationManager.RequestPause(task.Id);
                throw new OperationCanceledException();
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "Implementation completed after resume.");
        }

        var client = new ScriptedChatClient(Handler);
        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo,
            CancellationManager = cancellationManager
        }.Build();

        // 1. First run: primes a synthetic bash execution, then gets paused during LLM streaming
        var priming = new SyntheticPrimingExecution(
            SourceStageId: "pre-implementation-baseline",
            Command: "dotnet build",
            Output: "Build succeeded.",
            IsSuccess: true,
            DurationMs: 120);
        var context1 = CreateContext(task, syntheticPriming: priming);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.RunStageAsync(context1));

        var convsAfterPause = await repo.GetConversationsAsync(task.Id);
        var pausedConv = Assert.Single(convsAfterPause);
        Assert.Equal(StageExecutionStatus.Interrupted, pausedConv.Status);
        Assert.Null(pausedConv.CompletedAt);

        var turnsAfterPause = (await repo.GetConversationWithTurnsAsync(pausedConv.Id))!.Turns;
        Assert.Equal(3, turnsAfterPause.Count); // Turn 0: Bootstrap, Turn 1: Primed Tool, Turn 2: Primed ToolResult

        // 2. Clear pause and resume without passing SerializedStateJson (reproduces TaskOrchestrator pause/resume)
        cancellationManager.ClearPause(task.Id);
        var context2 = CreateContext(task, serializedState: null, syntheticPriming: priming); // Should be skipped because resumed conversation already has turns!

        var resumeResult = await harness.RunStageAsync(context2);
        Assert.Equal(AgentRunStatus.Completed, resumeResult.Status);

        // Verify the exact same Conversation row was resumed and completed (no second Conversation created)
        var convsAfterResume = await repo.GetConversationsAsync(task.Id);
        var completedConv = Assert.Single(convsAfterResume);
        Assert.Equal(pausedConv.Id, completedConv.Id);
        Assert.Equal(StageExecutionStatus.Completed, completedConv.Status);
        Assert.NotNull(completedConv.CompletedAt);

        var turnsAfterResume = (await repo.GetConversationWithTurnsAsync(completedConv.Id))!.Turns.OrderBy(t => t.TurnIndex).ToList();
        Assert.Equal(4, turnsAfterResume.Count);
        Assert.Equal(StageTurnRole.User, turnsAfterResume[0].Role);
        Assert.Equal(StageTurnRole.Tool, turnsAfterResume[1].Role);
        Assert.Equal(StageTurnRole.ToolResult, turnsAfterResume[2].Role);
        Assert.Equal(StageTurnRole.Assistant, turnsAfterResume[3].Role);
        Assert.Equal("Implementation completed after resume.", turnsAfterResume[3].Content);
    }

    [Fact]
    public async Task RunStageAsync_WhenResumingInterruptedConversationWithZeroTurns_WritesBootstrapPromptAndCompletesInSameConversation()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 44, Title = "Zero-Turn Interrupted Task", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        // Pre-create an Interrupted conversation with 0 turns (e.g., paused before Turn 0 was written)
        var stubConv = await repo.StartConversationAsync(task.Id, "task-implementation", WorkflowStageKind.Agentic, 1);
        await repo.CompleteConversationAsync(stubConv.Id, StageExecutionStatus.Interrupted, summary: "Paused early");

        async IAsyncEnumerable<ChatResponseUpdate> Handler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Completed from zero-turn stub.");
        }

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = new ScriptedChatClient(Handler),
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        var result = await harness.RunStageAsync(CreateContext(task));
        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var convs = await repo.GetConversationsAsync(task.Id);
        var completed = Assert.Single(convs);
        Assert.Equal(stubConv.Id, completed.Id);
        Assert.Equal(StageExecutionStatus.Completed, completed.Status);

        var turns = (await repo.GetConversationWithTurnsAsync(completed.Id))!.Turns.OrderBy(t => t.TurnIndex).ToList();
        Assert.Equal(2, turns.Count);
        Assert.Equal(StageTurnRole.User, turns[0].Role);
        Assert.Equal("Implement the requested feature", turns[0].Content);
        Assert.Equal(StageTurnRole.Assistant, turns[1].Role);
    }

    [Fact]
    public async Task RunStageAsync_WhenResumingInterruptedConversationWithStaleCheckpoint_PrefersNewerPersistedTurns()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 45, Title = "Stale Checkpoint Task", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        var conv = await repo.StartConversationAsync(task.Id, "task-implementation", WorkflowStageKind.Agentic, 1);
        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Turn 0 Bootstrap");
        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.Assistant, content: "Turn 1 Progress");
        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Turn 2 Follow-up");
        await repo.CompleteConversationAsync(conv.Id, StageExecutionStatus.Interrupted, summary: "Paused at Turn 2");

        // Stale checkpoint only has 1 message
        var staleCheckpointJson = NightCodeForge.Core.Workflow.ConversationHistorySerializer.Serialize(new[]
        {
            new ConversationTurn { TurnIndex = 0, Role = StageTurnRole.User, Content = "Stale Turn 0 Only" }
        });

        List<ChatMessage>? capturedMessages = null;
        async IAsyncEnumerable<ChatResponseUpdate> Handler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            capturedMessages = messages.ToList();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Finished.");
        }

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = new ScriptedChatClient(Handler),
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        var result = await harness.RunStageAsync(CreateContext(task, serializedState: staleCheckpointJson));
        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.NotNull(capturedMessages);
        Assert.Contains(capturedMessages!, m => m.Text != null && m.Text.Contains("Turn 2 Follow-up"));
    }

    [Fact]
    public async Task RunStageAsync_WhenWaitingForHumanResumedFromPersistedTurns_ReplacesInterruptedAskResultAndAppendsFollowUp()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 46, Title = "Waiting For Human Turns Rehydration", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        // Simulate a WaitingForHuman conversation whose ask turn was already answered in SQLite by SubmitAnswersAsync
        var conv = await repo.StartConversationAsync(task.Id, "task-implementation", WorkflowStageKind.Agentic, 1);
        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Implement feature");
        await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Tool,
            toolName: CanonicalToolNames.Ask,
            toolArguments: "{\"questions\":[{\"prompt\":\"Which DB?\"}]}",
            metadataJson: "{\"CallId\":\"ask_call_1\"}");
        await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.ToolResult,
            toolName: CanonicalToolNames.Ask,
            toolResult: "PostgreSQL",
            metadataJson: "{\"CallId\":\"ask_call_1\"}");
        await repo.CompleteConversationAsync(conv.Id, StageExecutionStatus.WaitingForHuman, summary: "Waiting for human");

        List<ChatMessage>? capturedMessages = null;
        async IAsyncEnumerable<ChatResponseUpdate> Handler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            capturedMessages = messages.ToList();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Proceeding with PostgreSQL.");
        }

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = new ScriptedChatClient(Handler),
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = task,
            Workspace = new Workspace { Id = 1, Name = "TestWorkspace", RepositoryPath = _tempDir, ExecutionMode = ExecutionMode.BareHost },
            Profile = new AgentProfile { Id = "test-profile", Name = "TestAgent", HarnessId = "crucible", ModelName = "qwen3-coder:30b", Mode = AgentMode.Full },
            StageId = "task-implementation",
            StageDirective = "Implement",
            RequiredMode = AgentMode.Full,
            WorkspacePath = _tempDir,
            BootstrapPrompt = "Implement feature",
            PendingHumanAnswer = "PostgreSQL"
        };

        var result = await harness.RunStageAsync(context);
        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.NotNull(capturedMessages);

        // Verify the LLM received both the ToolResult ("PostgreSQL") and the trailing User follow-up prompt
        Assert.Contains(capturedMessages!, m =>
            m.Role == ChatRole.Tool &&
            m.Contents.OfType<FunctionResultContent>().Any(frc => frc.CallId == "ask_call_1" && frc.Result?.ToString() == "PostgreSQL"));
        Assert.Equal(ChatRole.User, capturedMessages![^1].Role);
        Assert.Equal("Please proceed with your plan based on the clarified answers.", capturedMessages[^1].Text);

        var reloadedConv = await repo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(reloadedConv);
        Assert.Equal(StageExecutionStatus.Completed, reloadedConv.Status);
        // Exactly one ToolResult for ask_call_1 (not duplicated)
        Assert.Single(reloadedConv.Turns, t => t.Role == StageTurnRole.ToolResult && t.ToolName == CanonicalToolNames.Ask);
    }

    [Fact]
    public async Task RunStageAsync_WhenWaitingForHumanResumedWithUnansweredAskTurn_ReplacesSynthesizedInterruptedResultWithHumanAnswer()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 47, Title = "Waiting For Human Unanswered Ask Turn", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        // Simulate a WaitingForHuman conversation whose ask turn has NOT yet received a ToolResult row in SQLite
        var conv = await repo.StartConversationAsync(task.Id, "task-implementation", WorkflowStageKind.Agentic, 1);
        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Implement feature");
        await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Tool,
            toolName: CanonicalToolNames.Ask,
            toolArguments: "{\"questions\":[{\"prompt\":\"Which cache?\"}]}",
            metadataJson: "{\"CallId\":\"ask_call_2\"}");
        await repo.CompleteConversationAsync(conv.Id, StageExecutionStatus.WaitingForHuman, summary: "Waiting for human");

        List<ChatMessage>? capturedMessages = null;
        async IAsyncEnumerable<ChatResponseUpdate> Handler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            capturedMessages = messages.ToList();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Proceeding with Redis.");
        }

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = new ScriptedChatClient(Handler),
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = task,
            Workspace = new Workspace { Id = 1, Name = "TestWorkspace", RepositoryPath = _tempDir, ExecutionMode = ExecutionMode.BareHost },
            Profile = new AgentProfile { Id = "test-profile", Name = "TestAgent", HarnessId = "crucible", ModelName = "qwen3-coder:30b", Mode = AgentMode.Full },
            StageId = "task-implementation",
            StageDirective = "Implement",
            RequiredMode = AgentMode.Full,
            WorkspacePath = _tempDir,
            BootstrapPrompt = "Implement feature",
            PendingHumanAnswer = "Use Redis"
        };

        var result = await harness.RunStageAsync(context);
        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.NotNull(capturedMessages);

        // Ensure InterruptedToolResult was replaced with "Use Redis" and not left as an error
        var toolResults = capturedMessages!
            .Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Where(frc => frc.CallId == "ask_call_2")
            .ToList();
        var singleResult = Assert.Single(toolResults);
        Assert.Equal("Use Redis", singleResult.Result?.ToString());

        Assert.Equal(ChatRole.User, capturedMessages![^1].Role);
        Assert.Equal("Please proceed with your plan based on the clarified answers.", capturedMessages[^1].Text);

        var reloadedConv = await repo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(reloadedConv);
        Assert.Equal(StageExecutionStatus.Completed, reloadedConv.Status);
        Assert.Single(reloadedConv.Turns, t => t.Role == StageTurnRole.ToolResult && t.ToolName == CanonicalToolNames.Ask && t.ToolResult == "Use Redis");
    }

    [Fact]
    public async Task RunStageAsync_WhenInterruptedAfterEarlierAnsweredAskAndSubsequentToolTurn_DoesNotAppendDuplicateFollowUpPrompt()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 48, Title = "Paused After Ask Was Already Followed Up", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        const string followUpPrompt = "Please proceed with your plan based on the clarified answers.";

        var conv = await repo.StartConversationAsync(task.Id, "task-implementation", WorkflowStageKind.Agentic, 1);
        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Implement feature");
        await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Tool,
            toolName: CanonicalToolNames.Ask,
            toolArguments: "{\"questions\":[{\"prompt\":\"Which cache?\"}]}",
            metadataJson: "{\"CallId\":\"ask_call_early\"}");
        await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.ToolResult,
            toolName: CanonicalToolNames.Ask,
            toolResult: "Use Redis",
            metadataJson: "{\"CallId\":\"ask_call_early\"}");
        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: followUpPrompt);
        await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Tool,
            toolName: CanonicalToolNames.Read,
            toolArguments: "{\"path\":\"Startup.cs\"}",
            metadataJson: "{\"CallId\":\"read_call_later\"}");
        await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.ToolResult,
            toolName: CanonicalToolNames.Read,
            toolResult: "public class Startup {}",
            metadataJson: "{\"CallId\":\"read_call_later\"}");
        await repo.CompleteConversationAsync(conv.Id, StageExecutionStatus.Interrupted, summary: "Paused after read tool");

        List<ChatMessage>? capturedMessages = null;
        async IAsyncEnumerable<ChatResponseUpdate> Handler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            capturedMessages = messages.ToList();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Completed implementation.");
        }

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = new ScriptedChatClient(Handler),
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = task,
            Workspace = new Workspace { Id = 1, Name = "TestWorkspace", RepositoryPath = _tempDir, ExecutionMode = ExecutionMode.BareHost },
            Profile = new AgentProfile { Id = "test-profile", Name = "TestAgent", HarnessId = "crucible", ModelName = "qwen3-coder:30b", Mode = AgentMode.Full },
            StageId = "task-implementation",
            StageDirective = "Implement",
            RequiredMode = AgentMode.Full,
            WorkspacePath = _tempDir,
            BootstrapPrompt = "Implement feature",
            PendingHumanAnswer = "Use Redis"
        };

        var result = await harness.RunStageAsync(context);
        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.NotNull(capturedMessages);

        // Must have only the single original followUpPrompt message, and the last message sent to the LLM must be the read ToolResult
        Assert.Equal(1, capturedMessages!.Count(m => m.Role == ChatRole.User && m.Text == followUpPrompt));
        Assert.Equal(ChatRole.Tool, capturedMessages[^1].Role);
    }
}
