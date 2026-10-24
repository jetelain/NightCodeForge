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

    private AgentSessionContext CreateContext(ForgeTask task, string? serializedState = null)
    {
        return new AgentSessionContext
        {
            ForgeTask = task,
            StageId = "task-implementation",
            BootstrapPrompt = "Implement the requested feature",
            SerializedStateJson = serializedState,
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
}
