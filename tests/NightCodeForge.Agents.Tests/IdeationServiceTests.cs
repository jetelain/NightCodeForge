using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Agents.Ideation;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents.Tests;

public class IdeationServiceTests
{
    private IdeationService CreateService(
        IChatClient? client = null,
        IOllamaChatClientFactory? chatClientFactory = null,
        IWorkspacePathResolver? pathResolver = null,
        IAgentActivityStreamer? activityStreamer = null,
        SessionTranscriptService? transcriptService = null,
        ILogger<IdeationService>? logger = null) =>
        new TestIdeationServiceBuilder
        {
            BaseChatClient = client,
            ChatClientFactory = chatClientFactory,
            PathResolver = pathResolver ?? NightCodeForge.Agents.Tests.Fixtures.TestPathResolver.Create(),
            ActivityStreamer = activityStreamer ?? new InMemoryAgentActivityStreamer(),
            TranscriptService = transcriptService ?? new SessionTranscriptService(),
            Logger = logger ?? NullLogger<IdeationService>.Instance
        }.Build();

    private static Workspace CreateWorkspace(int id = 1, string name = "Test WS", string? path = null)
    {
        var ws = new Workspace
        {
            Id = id,
            Name = name,
            RepositoryPath = path ?? Directory.GetCurrentDirectory()
        };
        ws.AgentProfiles.Add(AgentProfile.CreateDefaultIdeationProfile(id));
        return ws;
    }
    private class FakeChatClient : IChatClient
    {
        public string ResponseToReturn { get; set; } = string.Empty;
        public ChatClientMetadata Metadata => new("FakeClient");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ResponseToReturn)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task ConverseAsync_ExtractsProposals_FromMarkdownJsonCodeBlock()
    {
        var jsonResponse = @"I recommend splitting this into two tasks:

```json
[
  {
    ""title"": ""Implement Token Cache"",
    ""description"": ""Add LRU cache for prompt tokens"",
    ""priority"": 1,
    ""dependsOnTitles"": []
  },
  {
    ""title"": ""Integrate Token Cache into Pipeline"",
    ""description"": ""Wire cache into TaskOrchestrator"",
    ""priority"": 2,
    ""dependsOnTitles"": [""Implement Token Cache""]
  }
]
```

Let me know what you think!";

        var fakeClient = new FakeChatClient { ResponseToReturn = jsonResponse };
        var service = CreateService(fakeClient);

        var workspace = CreateWorkspace(1, "Test WS");
        var result = await service.ConverseAsync(workspace, "How should we optimize prompt token usage?");

        Assert.NotNull(result);
        Assert.Contains("I recommend splitting this", result.AssistantMessage);
        Assert.Equal(2, result.ProposedTasks.Count);

        var p1 = result.ProposedTasks[0];
        Assert.Equal("Implement Token Cache", p1.Title);
        Assert.Equal("Add LRU cache for prompt tokens", p1.Description);
        Assert.Equal(1, p1.Priority);

        var p2 = result.ProposedTasks[1];
        Assert.Equal("Integrate Token Cache into Pipeline", p2.Title);
        Assert.Equal(2, p2.Priority);
        Assert.NotNull(p2.DependsOnTitles);
        Assert.Contains("Implement Token Cache", p2.DependsOnTitles);
    }

    [Fact]
    public async Task ConverseAsync_UsesWorkspaceIdeationProfile_WhenPresent()
    {
        ChatOptions? capturedOptions = null;
        var fakeClient = new CapturingChatClient(opts => capturedOptions = opts)
        {
            ResponseToReturn = "Discovery complete."
        };

        var service = CreateService(fakeClient);

        var workspace = new Workspace
        {
            Id = 2,
            Name = "Workspace With Custom Profile",
            RepositoryPath = Directory.GetCurrentDirectory(),
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = "ideation",
                    Name = "Architectural Ideator",
                    ModelName = "deepseek-r1:14b",
                    Temperature = 0.5,
                    SystemPrompt = "Custom architectural persona"
                }
            }
        };

        var result = await service.ConverseAsync(workspace, "Suggest architecture improvements");

        Assert.NotNull(result);
        Assert.NotNull(capturedOptions);
        Assert.Equal("deepseek-r1:14b", capturedOptions.ModelId);
        Assert.Equal(0.5f, capturedOptions.Temperature);
    }

    [Fact]
    public async Task ConverseAsync_ResolvesModelViaChatClientFactory()
    {
        AgentProfile? resolvedProfile = null;
        var fakeClient = new FakeChatClient { ResponseToReturn = "OK" };

        var mockFactory = new MockChatClientFactory((p) =>
        {
            resolvedProfile = p;
            return fakeClient;
        });

        var service = CreateService(chatClientFactory: mockFactory);

        var customProfile = new AgentProfile
        {
            Id = "custom-ideator",
            Name = "Custom Explorer",
            ModelName = "qwen2.5-coder:32b"
        };

        var workspace = new Workspace { Id = 3, RepositoryPath = Directory.GetCurrentDirectory() };
        var result = await service.ConverseAsync(workspace, "Hello", agentProfile: customProfile);

        Assert.NotNull(result);
        Assert.NotNull(resolvedProfile);
        Assert.Equal("custom-ideator", resolvedProfile.Id);
        Assert.Equal("qwen2.5-coder:32b", resolvedProfile.ModelName);
    }

    [Fact]
    public void ResolveIdeationProfile_ThrowsAgentProfileNotFoundException_WhenNoProfileConfigured()
    {
        var workspace = new Workspace { Id = 4, RepositoryPath = Directory.GetCurrentDirectory() };
        var ex = Assert.Throws<AgentProfileNotFoundException>(() => IdeationService.ResolveIdeationProfile(workspace));
        Assert.Equal("ideation", ex.ProfileId);
        Assert.Equal(4, ex.WorkspaceId);
    }

    [Fact]
    public async Task ConverseStreamingAsync_StreamsStructuredTextReasoningContent_AndExtractsProposals()
    {
        var updates = new List<ChatResponseUpdate>
        {
            new() { Contents = [new TextReasoningContent("Reasoning about architectural design.")] },
            new() { Contents = [new TextReasoningContent(" Selecting appropriate components.")] },
            new() { Contents = [new TextContent("Here is the resulting plan:\n\n")] },
            new() { Contents = [new TextContent("```json\n[\n  {\n    \"title\": \"Native Reasoning Task\",\n    \"description\": \"MAF aligned thinking\",\n    \"priority\": 1\n  }\n]\n```")] }
        };

        var streamer = new CapturingActivityStreamer();
        var client = new MockUpdateStreamingChatClient(updates);
        var service = CreateService(client, activityStreamer: streamer);

        var workspace = CreateWorkspace(10);

        var streamUpdates = new List<NightCodeForge.Core.Interfaces.IdeationStreamUpdate>();
        await foreach (var update in service.ConverseStreamingAsync(workspace, "Plan with native reasoning"))
        {
            streamUpdates.Add(update);
        }

        // 1. Verify stream updates
        Assert.Contains(streamUpdates, u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.ThinkingDelta && u.TextDelta!.Contains("Reasoning about"));
        Assert.Contains(streamUpdates, u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.ThinkingDelta && u.TextDelta!.Contains("Selecting appropriate"));
        Assert.Contains(streamUpdates, u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.MessageDelta && u.TextDelta!.Contains("Here is the resulting plan"));
        Assert.Contains(streamUpdates, u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.ProposalDiscovered && u.Proposal?.Title == "Native Reasoning Task");

        // 2. Verify live feedback via IAgentActivityStreamer
        Assert.Contains(streamer.Events, e => e.Kind == NightCodeForge.Core.Streaming.AgentActivityKind.ThinkingDelta && e.Content.Contains("Reasoning about"));
        Assert.Contains(streamer.Events, e => e.Kind == NightCodeForge.Core.Streaming.AgentActivityKind.ThinkingDelta && e.Content.Contains("Selecting appropriate"));
        Assert.Contains(streamer.Events, e => e.Kind == NightCodeForge.Core.Streaming.AgentActivityKind.MessageDelta && e.Content.Contains("Here is the resulting plan"));
    }

    [Fact]
    public async Task ConverseAsync_WithNonStreamingClient_FallsBackGracefullyAndBroadcastsFeedback()
    {
        var streamer = new CapturingActivityStreamer();
        var fakeClient = new FakeChatClient
        {
            ResponseToReturn = "Finished proposals:\n```json\n[{\"title\": \"Fallback Task\", \"description\": \"Test desc\"}]\n```"
        };

        var service = CreateService(fakeClient, activityStreamer: streamer);
        var workspace = CreateWorkspace(11);

        var result = await service.ConverseAsync(workspace, "Suggest a fallback task");

        Assert.NotNull(result);
        Assert.Single(result.ProposedTasks);
        Assert.Equal("Fallback Task", result.ProposedTasks[0].Title);

        Assert.Contains(streamer.Events, e => e.Kind == NightCodeForge.Core.Streaming.AgentActivityKind.StageStarted && e.StageId == "ideation");
        Assert.Contains(streamer.Events, e => e.Kind == NightCodeForge.Core.Streaming.AgentActivityKind.MessageDelta && e.Content.Contains("Finished proposals"));
        Assert.Contains(streamer.Events, e => e.Kind == NightCodeForge.Core.Streaming.AgentActivityKind.StageCompleted && e.StageId == "ideation");
    }

    [Fact]
    public async Task ConverseStreamingAsync_ExecutesToolCalls_AndIncorporatesPendingProposals()
    {
        var streamer = new CapturingActivityStreamer();
        int callCount = 0;

        var client = new MultiTurnChatClient((messages) =>
        {
            callCount++;
            if (callCount == 1)
            {
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "<tool_call>{\"name\": \"propose_tasks\", \"arguments\": {\"tasks\": [{\"title\": \"Tool Generated ForgeTask\", \"description\": \"From tool call\"}]}}</tool_call>"));
            }
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Proposals are submitted and finalized."));
        });

        var service = CreateService(client, activityStreamer: streamer);
        var workspace = CreateWorkspace(12);

        var result = await service.ConverseAsync(workspace, "Create backlog tasks");

        Assert.NotNull(result);
        Assert.Single(result.ProposedTasks);
        Assert.Equal("Tool Generated ForgeTask", result.ProposedTasks[0].Title);

        Assert.Contains(streamer.Events, e => e.Kind == NightCodeForge.Core.Streaming.AgentActivityKind.ToolStarted && e.Content.Contains("propose_tasks"));
        Assert.Contains(streamer.Events, e => e.Kind == NightCodeForge.Core.Streaming.AgentActivityKind.ToolCompleted);
    }

    private class MultiTurnChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatResponse> _handler;

        public MultiTurnChatClient(Func<IEnumerable<ChatMessage>, ChatResponse> handler) => _handler = handler;

        public ChatClientMetadata Metadata => new("MultiTurnMock");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_handler(chatMessages));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private class CapturingActivityStreamer : NightCodeForge.Core.Streaming.IAgentActivityStreamer
    {
        public List<NightCodeForge.Core.Streaming.AgentActivityEvent> Events { get; } = new();

        public Task BroadcastAsync(NightCodeForge.Core.Streaming.AgentActivityEvent activityEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(activityEvent);
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<NightCodeForge.Core.Streaming.AgentActivityEvent> SubscribeAsync(int? taskId = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private class MockStreamingChatClient : IChatClient
    {
        private readonly IReadOnlyList<string> _chunks;

        public MockStreamingChatClient(IReadOnlyList<string> chunks)
        {
            _chunks = chunks;
        }

        public ChatClientMetadata Metadata => new("MockStreamingClient");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var full = string.Join("", _chunks);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, full)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var chunk in _chunks)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private class MockUpdateStreamingChatClient : IChatClient
    {
        private readonly IReadOnlyList<ChatResponseUpdate> _updates;

        public MockUpdateStreamingChatClient(IReadOnlyList<ChatResponseUpdate> updates)
        {
            _updates = updates;
        }

        public ChatClientMetadata Metadata => new("MockUpdateStreamingClient");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var msg = new ChatMessage(ChatRole.Assistant, "");
            foreach (var u in _updates)
            {
                foreach (var c in u.Contents)
                {
                    msg.Contents.Add(c);
                }
            }
            return Task.FromResult(new ChatResponse(msg));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in _updates)
            {
                yield return update;
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private class CapturingChatClient : IChatClient
    {
        private readonly Action<ChatOptions?> _onCall;
        public string ResponseToReturn { get; set; } = string.Empty;
        public ChatClientMetadata Metadata => new("CapturingClient");

        public CapturingChatClient(Action<ChatOptions?> onCall) => _onCall = onCall;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _onCall(options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ResponseToReturn)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private class MockChatClientFactory : IOllamaChatClientFactory
    {
        private readonly Func<AgentProfile?, IChatClient> _createFunc;
        public MockChatClientFactory(Func<AgentProfile?, IChatClient> createFunc) => _createFunc = createFunc;
        public IChatClient CreateClient(string? modelId = null) => _createFunc(null);
        public IChatClient CreateClient(string? modelId, OllamaRoutingTarget routingTarget) => _createFunc(null);
        public IChatClient CreateClient(AgentProfile? profile) => _createFunc(profile);
    }

    private class FakePathResolver : NightCodeForge.Core.Interfaces.IWorkspacePathResolver
    {
        public string GetAgentWorkingClonePath(int workspaceId) => $"/home/user/.nightcodeforge/wks/{workspaceId}";
        public string GetHostRemoteUrl(string repositoryPath) => $"/mnt/c/{repositoryPath}";
        public string ResolveCruciblePath(int workspaceId, string relativeOrContainerPath) => $@"\\wsl.localhost\Ubuntu\home\user\.nightcodeforge\wks\{workspaceId}\{relativeOrContainerPath}";
        public string GetWslDistributionName() => "Ubuntu";
        public string GetWslUserName() => "user";
    }

    [Fact]
    public async Task ConverseAsync_WithWorkspacePathResolver_InstantiatesCanonicalToolsAndExecutesSuccessfully()
    {
        var fakeClient = new FakeChatClient { ResponseToReturn = "Exploration complete." };
        var pathResolver = new FakePathResolver();
        var service = CreateService(fakeClient, pathResolver: pathResolver);

        var workspace = CreateWorkspace(42, "Test WS");
        var result = await service.ConverseAsync(workspace, "Explore the architecture");

        Assert.NotNull(result);
        Assert.Equal("Exploration complete.", result.AssistantMessage);
    }

    [Fact]
    public async Task ConverseStreamingAsync_YieldsToolCallStartedAndCompletedUpdates()
    {
        var streamer = new CapturingActivityStreamer();
        int turn = 0;

        var client = new MultiTurnStreamingChatClient((messages) =>
        {
            turn++;
            if (turn == 1)
            {
                return "<tool_call>{\"name\": \"propose_tasks\", \"arguments\": {\"tasks\": [{\"title\": \"Streaming Tool ForgeTask\", \"description\": \"Discovered via tool\"}]}}</tool_call>";
            }
            return "Task proposal has been registered.";
        });

        var service = CreateService(client, activityStreamer: streamer);
        var workspace = CreateWorkspace(50);

        var updates = new List<NightCodeForge.Core.Interfaces.IdeationStreamUpdate>();
        await foreach (var update in service.ConverseStreamingAsync(workspace, "Suggest tasks"))
        {
            updates.Add(update);
        }

        var startedUpdate = updates.FirstOrDefault(u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.ToolCallStarted);
        Assert.NotNull(startedUpdate);
        Assert.NotNull(startedUpdate.ToolCall);
        Assert.Equal("propose_tasks", startedUpdate.ToolCall.ToolName);

        var completedUpdate = updates.FirstOrDefault(u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.ToolCallCompleted);
        Assert.NotNull(completedUpdate);
        Assert.NotNull(completedUpdate.ToolCall);
        Assert.Equal("propose_tasks", completedUpdate.ToolCall.ToolName);
        Assert.False(completedUpdate.ToolCall.IsError);
        Assert.NotNull(completedUpdate.ToolCall.Result);

        var completed = updates.FirstOrDefault(u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.Completed);
        Assert.NotNull(completed?.FinalResult?.ToolCalls);
        Assert.Single(completed.FinalResult.ToolCalls);
        Assert.Equal("propose_tasks", completed.FinalResult.ToolCalls[0].ToolName);
    }

    [Fact]
    public async Task ConverseStreamingAsync_LogsAndYieldsToolFailure_WhenToolFails()
    {
        var logger = new TestLogger<IdeationService>();
        int turn = 0;

        var client = new MultiTurnStreamingChatClient((messages) =>
        {
            turn++;
            if (turn == 1)
            {
                return "<tool_call>{\"name\": \"unknown_magic_tool\", \"arguments\": {\"param\": 123}}</tool_call>";
            }
            return "Continuing after tool failure.";
        });

        var service = CreateService(client, logger: logger);
        var workspace = CreateWorkspace(51);

        var updates = new List<NightCodeForge.Core.Interfaces.IdeationStreamUpdate>();
        await foreach (var update in service.ConverseStreamingAsync(workspace, "Run invalid tool"))
        {
            updates.Add(update);
        }

        var startedUpdate = updates.FirstOrDefault(u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.ToolCallStarted);
        Assert.NotNull(startedUpdate);
        Assert.Equal("unknown_magic_tool", startedUpdate.ToolCall?.ToolName);

        var completedUpdate = updates.FirstOrDefault(u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.ToolCallCompleted);
        Assert.NotNull(completedUpdate);
        Assert.NotNull(completedUpdate.ToolCall);
        Assert.Equal("unknown_magic_tool", completedUpdate.ToolCall.ToolName);
        Assert.True(completedUpdate.ToolCall.IsError);
        Assert.Contains("Error executing tool", completedUpdate.ToolCall.Result, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(logger.Logs, l => l.Level >= Microsoft.Extensions.Logging.LogLevel.Warning && l.Message.Contains("unknown_magic_tool"));
    }

    private class MultiTurnStreamingChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, string> _responseGenerator;

        public MultiTurnStreamingChatClient(Func<IEnumerable<ChatMessage>, string> responseGenerator)
        {
            _responseGenerator = responseGenerator;
        }

        public ChatClientMetadata Metadata => new("MultiTurnStreamingMock");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var text = _responseGenerator(chatMessages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var text = _responseGenerator(chatMessages);
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private class TestLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Logs { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public async Task ConverseStreamingAsync_SavesTranscript_WhenTranscriptServiceProvided()
    {
        var tempTranscriptsDir = Path.Combine(Path.GetTempPath(), "ncf-transcripts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var transcriptService = new NightCodeForge.Agents.Transcripts.SessionTranscriptService(tempTranscriptsDir);
            var client = new MultiTurnStreamingChatClient(_ => "Here is the recommendation.");
            var service = CreateService(client, transcriptService: transcriptService);

            var workspace = CreateWorkspace(77);
            var updates = new List<NightCodeForge.Core.Interfaces.IdeationStreamUpdate>();

            await foreach (var update in service.ConverseStreamingAsync(workspace, "Plan new feature"))
            {
                updates.Add(update);
            }

            var completed = updates.FirstOrDefault(u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.Completed);
            Assert.NotNull(completed);
            Assert.NotNull(completed.FinalResult);
            Assert.NotNull(completed.FinalResult.TranscriptPath);
            Assert.True(File.Exists(completed.FinalResult.TranscriptPath));
            Assert.NotNull(completed.FinalResult.FullTranscriptMarkdown);
            Assert.Contains("Plan new feature", completed.FinalResult.FullTranscriptMarkdown);
            Assert.Contains("Here is the recommendation.", completed.FinalResult.FullTranscriptMarkdown);
        }
        finally
        {
            if (Directory.Exists(tempTranscriptsDir))
            {
                try { Directory.Delete(tempTranscriptsDir, true); } catch { }
            }
        }
    }

    private class MessageCapturingChatClient : IChatClient
    {
        private readonly Action<IEnumerable<ChatMessage>> _onCall;
        public string ResponseToReturn { get; set; } = string.Empty;
        public ChatClientMetadata Metadata => new("MessageCapturingClient");

        public MessageCapturingChatClient(Action<IEnumerable<ChatMessage>> onCall) => _onCall = onCall;

        public List<ChatMessage> LastRawMessages { get; private set; } = new();
        public ChatOptions? LastOptions { get; private set; }

        /// <summary>Reports the effective prompt: raw messages plus options.Instructions surfaced as a leading System message.</summary>
        private IEnumerable<ChatMessage> Observe(IEnumerable<ChatMessage> chatMessages, ChatOptions? options)
        {
            LastRawMessages = chatMessages.ToList();
            LastOptions = options;
            var effective = new List<ChatMessage>(LastRawMessages);
            if (!string.IsNullOrEmpty(options?.Instructions))
            {
                effective.Insert(0, new ChatMessage(ChatRole.System, options.Instructions));
            }
            return effective;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _onCall(Observe(chatMessages, options));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ResponseToReturn)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _onCall(Observe(chatMessages, options));
            yield return new ChatResponseUpdate(ChatRole.Assistant, ResponseToReturn);
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void IdeationService_ExposesExplicitMaxTurnsConstants()
    {
        Assert.Equal(500, IdeationService.DefaultMaxTurns);
        Assert.Equal(500, AgentProfile.DefaultMaxTurns);
        Assert.Equal(500, Crucible.CrucibleHarnessConfig.DefaultMaxTurns);

        var profile = AgentProfile.CreateDefaultIdeationProfile();
        Assert.Equal(300, profile.MaxTurns);
    }

    [Fact]
    public async Task ConverseStreamingAsync_InjectsEnvironmentSectionWithPwd()
    {
        List<ChatMessage>? capturedMessages = null;
        var fakeClient = new MessageCapturingChatClient(msgs => capturedMessages = msgs.ToList())
        {
            ResponseToReturn = "I am ready."
        };

        var service = CreateService(fakeClient);
        var workspace = new Workspace
        {
            Id = 42,
            Name = "Env Test Workspace",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            RepositoryPath = Directory.GetCurrentDirectory()
        };
        workspace.AgentProfiles.Add(AgentProfile.CreateDefaultIdeationProfile(42));

        var updates = new List<IdeationStreamUpdate>();
        await foreach (var update in service.ConverseStreamingAsync(workspace, "Hello"))
        {
            updates.Add(update);
        }

        Assert.NotNull(capturedMessages);
        var systemMsg = capturedMessages.FirstOrDefault(m => m.Role == ChatRole.System);
        Assert.NotNull(systemMsg);
        Assert.Contains("<environment>", systemMsg.Text);
        Assert.Contains("Current Working Directory (PWD):", systemMsg.Text);
        Assert.Contains("Repository Root:", systemMsg.Text);
        Assert.Contains("ContainerSandbox", systemMsg.Text);
    }

    [Fact]
    public async Task ConverseStreamingAsync_ProposeTasks_EstablishesSequentialDependencies_WhenMultipleTasksProposed()
    {
        int turn = 0;
        var client = new MultiTurnStreamingChatClient((messages) =>
        {
            turn++;
            if (turn == 1)
            {
                return "<tool_call>{\"name\": \"propose_tasks\", \"arguments\": {\"tasks\": [" +
                       "{\"title\": \"Setup DB migrations\", \"description\": \"Configure schema\"}," +
                       "{\"title\": \"Implement repository layer\", \"description\": \"Build repositories\"}," +
                       "{\"title\": \"Expose endpoints\", \"description\": \"Add Minimal API endpoints\"}" +
                       "]}}</tool_call>";
            }
            return "I have proposed 3 sequential tasks for your review.";
        });

        var service = CreateService(client);
        var workspace = CreateWorkspace(10);

        var streamUpdates = new List<IdeationStreamUpdate>();
        await foreach (var update in service.ConverseStreamingAsync(workspace, "Plan feature implementation"))
        {
            streamUpdates.Add(update);
        }

        var completed = streamUpdates.FirstOrDefault(u => u.Kind == IdeationStreamUpdateKind.Completed);
        Assert.NotNull(completed?.FinalResult);
        var proposals = completed.FinalResult.ProposedTasks;

        Assert.Equal(3, proposals.Count);

        // Task 0: No predecessor
        Assert.Equal("Setup DB migrations", proposals[0].Title);
        Assert.Empty(proposals[0].DependsOnTitles ?? Array.Empty<string>());

        // Task 1: Implicit dependency on Task 0
        Assert.Equal("Implement repository layer", proposals[1].Title);
        Assert.NotNull(proposals[1].DependsOnTitles);
        Assert.Contains("Setup DB migrations", proposals[1].DependsOnTitles!);

        // Task 2: Implicit dependency on Task 1
        Assert.Equal("Expose endpoints", proposals[2].Title);
        Assert.NotNull(proposals[2].DependsOnTitles);
        Assert.Contains("Implement repository layer", proposals[2].DependsOnTitles!);

        // Verify real-time stream events
        var discoveredUpdates = streamUpdates
            .Where(u => u.Kind == IdeationStreamUpdateKind.ProposalDiscovered && u.Proposal != null)
            .Select(u => u.Proposal!)
            .ToList();

        Assert.Equal(3, discoveredUpdates.Count);
        Assert.Contains("Setup DB migrations", discoveredUpdates[1].DependsOnTitles!);
        Assert.Contains("Implement repository layer", discoveredUpdates[2].DependsOnTitles!);
    }

    [Fact]
    public async Task ConverseStreamingAsync_ProposeTasks_HandlesSingleTaskWithoutImplicitDependency()
    {
        int turn = 0;
        var client = new MultiTurnStreamingChatClient((messages) =>
        {
            turn++;
            if (turn == 1)
            {
                return "<tool_call>{\"name\": \"propose_tasks\", \"arguments\": {\"title\": \"Single Standalone Task\", \"description\": \"Independent task\", \"priority\": 4}}</tool_call>";
            }
            return "Task registered.";
        });

        var service = CreateService(client);
        var workspace = CreateWorkspace(11);

        var result = await service.ConverseAsync(workspace, "Suggest a task");

        Assert.Single(result.ProposedTasks);
        var p = result.ProposedTasks[0];
        Assert.Equal("Single Standalone Task", p.Title);
        Assert.Equal("Independent task", p.Description);
        Assert.Equal(4, p.Priority);
        Assert.Empty(p.DependsOnTitles ?? Array.Empty<string>());
    }

    [Fact]
    public async Task ConverseStreamingAsync_ProposeTasks_HandlesSerializedJsonStringArgument()
    {
        int turn = 0;
        var client = new MultiTurnStreamingChatClient((messages) =>
        {
            turn++;
            if (turn == 1)
            {
                var innerJson = "[{\\\"title\\\": \\\"Step Alpha\\\", \\\"description\\\": \\\"Alpha details\\\"}, {\\\"title\\\": \\\"Step Beta\\\", \\\"description\\\": \\\"Beta details\\\"}]";
                return $"<tool_call>{{\"name\": \"propose_tasks\", \"arguments\": {{\"tasks\": \"{innerJson}\"}}}}</tool_call>";
            }
            return "Sequential tasks parsed.";
        });

        var service = CreateService(client);
        var workspace = CreateWorkspace(12);

        var result = await service.ConverseAsync(workspace, "Build pipeline");

        Assert.Equal(2, result.ProposedTasks.Count);
        Assert.Equal("Step Alpha", result.ProposedTasks[0].Title);
        Assert.Equal("Step Beta", result.ProposedTasks[1].Title);
        Assert.Contains("Step Alpha", result.ProposedTasks[1].DependsOnTitles!);
    }

    [Fact]
    public async Task ConverseStreamingAsync_SystemPrompt_InstructsProposeTasksInsteadOfMarkdownJson()
    {
        List<ChatMessage>? capturedMessages = null;
        var fakeClient = new MessageCapturingChatClient(msgs => capturedMessages = msgs.ToList())
        {
            ResponseToReturn = "Ready."
        };

        var service = CreateService(fakeClient);
        var workspace = CreateWorkspace(13);

        await service.ConverseAsync(workspace, "Help me plan");

        Assert.NotNull(capturedMessages);
        var systemMsg = capturedMessages.FirstOrDefault(m => m.Role == ChatRole.System);
        Assert.NotNull(systemMsg);

        // Must instruct using the propose_tasks tool
        Assert.Contains("propose_tasks", systemMsg.Text);
        Assert.Contains("sequentially with dependencies established automatically", systemMsg.Text);
        Assert.Contains("do NOT output markdown JSON codeblocks", systemMsg.Text);

        // Must NOT ask the model for markdown JSON codeblocks
        Assert.DoesNotContain("```json", systemMsg.Text);
    }

    [Fact]
    public async Task ConverseStreamingAsync_SystemPrompt_InstructsPullRequestSizingSubTasksAndOpenQuestions()
    {
        List<ChatMessage>? capturedMessages = null;
        var fakeClient = new MessageCapturingChatClient(msgs => capturedMessages = msgs.ToList())
        {
            ResponseToReturn = "Ready."
        };

        var service = CreateService(fakeClient);
        var workspace = CreateWorkspace(14);

        await service.ConverseAsync(workspace, "Plan new features");

        Assert.NotNull(capturedMessages);
        var systemMsg = capturedMessages.FirstOrDefault(m => m.Role == ChatRole.System);
        Assert.NotNull(systemMsg);

        // PR / Merge Request sizing with build & code review gates, and own tests
        Assert.Contains("pull-request", systemMsg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("merge-request", systemMsg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gates", systemMsg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tests", systemMsg.Text, StringComparison.OrdinalIgnoreCase);

        // Sub-tasks within a task
        Assert.Contains("sub-tasks", systemMsg.Text, StringComparison.OrdinalIgnoreCase);

        // Open questions answered in task review
        Assert.Contains("open questions", systemMsg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("task review", systemMsg.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateDefaultIdeationProfile_DirectsOnPullRequestSizingAndTaskReview()
    {
        var profile = AgentProfile.CreateDefaultIdeationProfile();

        Assert.NotNull(profile.SystemPrompt);
        Assert.Contains("Pull Request / Merge Request", profile.SystemPrompt);
        Assert.Contains("sub-tasks", profile.SystemPrompt);
        Assert.Contains("open questions", profile.SystemPrompt);
        Assert.Contains("task review", profile.SystemPrompt);
    }
}


