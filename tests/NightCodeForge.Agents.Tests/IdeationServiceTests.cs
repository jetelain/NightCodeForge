using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Ideation;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Tests;

public class IdeationServiceTests
{
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
        var service = new IdeationService(fakeClient);

        var workspace = new Workspace { Id = 1, Name = "Test WS", RepositoryPath = Directory.GetCurrentDirectory() };
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

        var service = new IdeationService(fakeClient);

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

        var service = new IdeationService(chatClientFactory: mockFactory);

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
    public void ResolveIdeationProfile_FallsBackToDefault_WhenNoProfileConfigured()
    {
        var workspace = new Workspace { Id = 4, RepositoryPath = Directory.GetCurrentDirectory() };
        var profile = IdeationService.ResolveIdeationProfile(workspace);

        Assert.NotNull(profile);
        Assert.Equal("ideation", profile.Id);
        Assert.Equal(AgentMode.ReadOnly, profile.Mode);
        Assert.Contains("read", profile.PermittedTools!);
        Assert.Contains("propose_tasks", profile.PermittedTools!);
    }

    [Fact]
    public async Task ConverseStreamingAsync_StreamsThinkingAndMessageDeltas_AndExtractsProposals()
    {
        var chunks = new List<string>
        {
            "<think>Analyzing repo",
            " structure</think>Here is my proposal:\n\n",
            "```json\n[\n  {\n    \"title\": \"Live Stream Feature\",\n    \"description\": \"Real-time UI streaming\",\n    \"priority\": 1\n  }\n]\n```"
        };

        var streamer = new CapturingActivityStreamer();
        var client = new MockStreamingChatClient(chunks);
        var service = new IdeationService(defaultChatClient: client, activityStreamer: streamer);

        var workspace = new Workspace { Id = 10, RepositoryPath = Directory.GetCurrentDirectory() };

        var updates = new List<NightCodeForge.Core.Interfaces.IdeationStreamUpdate>();
        await foreach (var update in service.ConverseStreamingAsync(workspace, "Plan the streaming feature"))
        {
            updates.Add(update);
        }

        // 1. Verify stream updates
        Assert.Contains(updates, u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.ThinkingDelta && u.TextDelta!.Contains("Analyzing repo"));
        Assert.Contains(updates, u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.ThinkingDelta && u.TextDelta!.Contains("structure"));
        Assert.Contains(updates, u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.MessageDelta && u.TextDelta!.Contains("Here is my proposal"));
        Assert.Contains(updates, u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.ProposalDiscovered && u.Proposal?.Title == "Live Stream Feature");
        
        var completed = updates.FirstOrDefault(u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.Completed);
        Assert.NotNull(completed);
        Assert.NotNull(completed.FinalResult);
        Assert.Single(completed.FinalResult.ProposedTasks);
        Assert.Equal("Live Stream Feature", completed.FinalResult.ProposedTasks[0].Title);

        // 2. Verify live feedback via IAgentActivityStreamer
        Assert.Contains(streamer.Events, e => e.Kind == NightCodeForge.Core.Streaming.AgentActivityKind.StageStarted && e.StageId == "ideation");
        Assert.Contains(streamer.Events, e => e.Kind == NightCodeForge.Core.Streaming.AgentActivityKind.ThinkingDelta && e.Content.Contains("Analyzing repo"));
        Assert.Contains(streamer.Events, e => e.Kind == NightCodeForge.Core.Streaming.AgentActivityKind.MessageDelta && e.Content.Contains("Here is my proposal"));
        Assert.Contains(streamer.Events, e => e.Kind == NightCodeForge.Core.Streaming.AgentActivityKind.StageCompleted && e.StageId == "ideation");

        var startedEvent = streamer.Events.First(e => e.Kind == NightCodeForge.Core.Streaming.AgentActivityKind.StageStarted);
        Assert.NotNull(startedEvent.Metadata);
        Assert.Equal(10, startedEvent.Metadata["workspaceId"]);
        Assert.Equal("ideation", startedEvent.Metadata["agentProfile"]);
    }

    [Fact]
    public async Task ConverseAsync_WithNonStreamingClient_FallsBackGracefullyAndBroadcastsFeedback()
    {
        var streamer = new CapturingActivityStreamer();
        var fakeClient = new FakeChatClient
        {
            ResponseToReturn = "<think>Fast thinking</think>Finished proposals:\n```json\n[{\"title\": \"Fallback Task\", \"description\": \"Test desc\"}]\n```"
        };

        var service = new IdeationService(defaultChatClient: fakeClient, activityStreamer: streamer);
        var workspace = new Workspace { Id = 11, RepositoryPath = Directory.GetCurrentDirectory() };

        var result = await service.ConverseAsync(workspace, "Suggest a fallback task");

        Assert.NotNull(result);
        Assert.Single(result.ProposedTasks);
        Assert.Equal("Fallback Task", result.ProposedTasks[0].Title);

        Assert.Contains(streamer.Events, e => e.Kind == NightCodeForge.Core.Streaming.AgentActivityKind.StageStarted && e.StageId == "ideation");
        Assert.Contains(streamer.Events, e => e.Kind == NightCodeForge.Core.Streaming.AgentActivityKind.ThinkingDelta && e.Content.Contains("Fast thinking"));
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

        var service = new IdeationService(defaultChatClient: client, activityStreamer: streamer);
        var workspace = new Workspace { Id = 12, RepositoryPath = Directory.GetCurrentDirectory() };

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

    private class MockChatClientFactory : Inference.IOllamaChatClientFactory
    {
        private readonly Func<AgentProfile?, IChatClient> _createFunc;
        public MockChatClientFactory(Func<AgentProfile?, IChatClient> createFunc) => _createFunc = createFunc;
        public IChatClient CreateClient(string? modelId = null) => _createFunc(null);
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
        var service = new IdeationService(fakeClient, pathResolver: pathResolver);

        var workspace = new Workspace { Id = 42, Name = "Test WS", RepositoryPath = Directory.GetCurrentDirectory() };
        var result = await service.ConverseAsync(workspace, "Explore the architecture");

        Assert.NotNull(result);
        Assert.Equal("Exploration complete.", result.AssistantMessage);
    }
}


