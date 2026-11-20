using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Ideation;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Mcp;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tests;

public class IdeationConversationCoordinatorTests
{
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

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public AdjustableTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class TrackingBackgroundTaskRunner : IBackgroundTaskRunner
    {
        private readonly ConcurrentQueue<Task> _tasks = new();

        public void RunTracked(Func<CancellationToken, Task> work, string description, CancellationToken cancellationToken = default)
        {
            var t = Task.Run(async () =>
            {
                try
                {
                    await work(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }, CancellationToken.None);
            _tasks.Enqueue(t);
        }

        public void RunTracked(Func<Task> work, string description) =>
            RunTracked(_ => work(), description);

        public async Task WaitForAllAsync(TimeSpan? timeout = null)
        {
            var pending = new List<Task>();
            while (_tasks.TryDequeue(out var t))
            {
                pending.Add(t);
            }

            if (pending.Count > 0)
            {
                await Task.WhenAll(pending).WaitAsync(timeout ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
    }

    private sealed class FakeChatClient : IChatClient
    {
        public string ResponseToReturn { get; set; } = string.Empty;
        public ChatClientMetadata Metadata => new("FakeClient");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ResponseToReturn)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CapturingChatClient : IChatClient
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

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class MessageCapturingChatClient : IChatClient
    {
        private readonly Action<IEnumerable<ChatMessage>> _onCall;
        public string ResponseToReturn { get; set; } = string.Empty;
        public ChatClientMetadata Metadata => new("MessageCapturingClient");
        public List<ChatMessage> LastRawMessages { get; private set; } = new();
        public ChatOptions? LastOptions { get; private set; }

        public MessageCapturingChatClient(Action<IEnumerable<ChatMessage>> onCall) => _onCall = onCall;

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
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _onCall(Observe(chatMessages, options));
            yield return new ChatResponseUpdate(ChatRole.Assistant, ResponseToReturn);
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class MockStreamingChatClient : IChatClient
    {
        private readonly IReadOnlyList<string> _chunks;
        public MockStreamingChatClient(IReadOnlyList<string> chunks) => _chunks = chunks;
        public ChatClientMetadata Metadata => new("MockStreamingClient");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Join("", _chunks))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
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

    private sealed class MockUpdateStreamingChatClient : IChatClient
    {
        private readonly IReadOnlyList<ChatResponseUpdate> _updates;
        public MockUpdateStreamingChatClient(IReadOnlyList<ChatResponseUpdate> updates) => _updates = updates;
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
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
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

    private sealed class MultiTurnChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatResponse> _handler;
        public MultiTurnChatClient(Func<IEnumerable<ChatMessage>, ChatResponse> handler) => _handler = handler;
        public ChatClientMetadata Metadata => new("MultiTurnMock");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_handler(chatMessages));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class MultiTurnStreamingChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatResponseUpdate> _responseGenerator;
        public MultiTurnStreamingChatClient(Func<IEnumerable<ChatMessage>, string> responseGenerator) =>
            _responseGenerator = msgs => new ChatResponseUpdate(ChatRole.Assistant, responseGenerator(msgs));
        public MultiTurnStreamingChatClient(Func<IEnumerable<ChatMessage>, ChatResponseUpdate> responseGenerator) =>
            _responseGenerator = responseGenerator;

        public ChatClientMetadata Metadata => new("MultiTurnStreamingMock");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return _responseGenerator(chatMessages);
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CapturingActivityStreamer : IAgentActivityStreamer
    {
        public List<AgentActivityEvent> Events { get; } = new();

        public Task BroadcastAsync(AgentActivityEvent activityEvent, CancellationToken cancellationToken = default)
        {
            lock (Events)
            {
                Events.Add(activityEvent);
            }
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<AgentActivityEvent> SubscribeAsync(int? taskId = null, CancellationToken cancellationToken = default) =>
            EmptyAsync();

        private static async IAsyncEnumerable<AgentActivityEvent> EmptyAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Logs { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Logs)
            {
                Logs.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed class MockChatClientFactory : ILlmChatClientFactory
    {
        private readonly Func<AgentProfile?, IChatClient> _createFunc;
        public MockChatClientFactory(Func<AgentProfile?, IChatClient> createFunc) => _createFunc = createFunc;
        public IChatClient CreateClient(string? modelId = null) => _createFunc(null);
        public IChatClient CreateClient(string? modelId, OllamaRoutingTarget routingTarget) => _createFunc(null);
        public IChatClient CreateClient(AgentProfile? profile) => _createFunc(profile);
        public IChatClient CreateClient(AgentProfile? profile, string? sessionId) => _createFunc(profile);
        public IChatClient CreateClient(string? modelId, OllamaRoutingTarget routingTarget, string? sessionId) => _createFunc(null);
    }

    private sealed class FakePathResolver : IWorkspacePathResolver
    {
        public string GetAgentWorkspaceRootPath(int workspaceId) => $"/home/user/.nightcodeforge/wks/{workspaceId}";
        public string GetAgentWorkingClonePath(int workspaceId) => $"/home/user/.nightcodeforge/wks/{workspaceId}";
        public string GetAgentWorktreePath(int workspaceId, int taskId) => $"/home/user/.nightcodeforge/wks/{workspaceId}/task-{taskId}";
        public string GetAttachmentsPath(int workspaceId) => $"/home/user/.nightcodeforge/wks/{workspaceId}/attachments";
        public string GetHostRemoteUrl(string repositoryPath) => $"/mnt/c/{repositoryPath}";
        public string ResolveCruciblePath(int workspaceId, string relativeOrContainerPath) => $@"\\wsl.localhost\Ubuntu\home\user\.nightcodeforge\wks\{workspaceId}\{relativeOrContainerPath}";
        public string ResolveCruciblePath(int workspaceId, string relativeOrContainerPath, int? taskId) => ResolveCruciblePath(workspaceId, relativeOrContainerPath);
        public string ResolveCruciblePath(string workspaceRoot, string relativeOrContainerPath, int? taskId = null) => Path.Combine(workspaceRoot, relativeOrContainerPath);
        public string GetWslDistributionName() => "Ubuntu";
        public string GetWslUserName() => "user";
        public string ToHostPath(string path) => path;
    }

    private static AgentProfile CreatePreloadProfile(int workspaceId, int? contextWindowTokens = null, OllamaRoutingTarget target = OllamaRoutingTarget.Auto) =>
        new()
        {
            Id = StandardStageIds.Ideation,
            Name = "Ideation",
            WorkspaceId = workspaceId,
            ModelName = "qwen3-coder:30b",
            Mode = AgentMode.ReadOnly,
            ContextWindowTokens = contextWindowTokens,
            RoutingTarget = target,
            OptimizationFlags = new LocalOptimizationFlags { JitModelPreload = true }
        };

    // -------------------------------------------------------------------------
    // Migrated behavioral coverage (Step 12.31 parity)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendMessageAsync_ExtractsProposals_FromMarkdownJsonCodeBlock()
    {
        var streamer = new CapturingActivityStreamer();
        var fakeClient = new FakeChatClient
        {
            ResponseToReturn = """
                I have reviewed the codebase and found a few improvement areas.

                ```json
                [
                  {
                    "title": "Add Caching Layer",
                    "description": "Cache prompt templates in memory.",
                    "priority": 1
                  },
                  {
                    "title": "Add Rate Limiter",
                    "description": "Prevent token bursts.",
                    "priority": 3
                  }
                ]
                ```
                """
        };

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = fakeClient,
            ActivityStreamer = streamer,
            PathResolver = TestPathResolver.Create()
        };
        var coordinator = builder.Build();
        var workspace = CreateWorkspace(1, "Test WS");

        var result = await builder.ConverseAsync(coordinator, workspace, "How should we optimize prompt token usage?");

        Assert.NotNull(result);
        Assert.Equal(2, result.ProposedTasks.Count);
        Assert.Equal("Add Caching Layer", result.ProposedTasks[0].Title);
        Assert.Equal(1, result.ProposedTasks[0].Priority);
        Assert.Equal("Add Rate Limiter", result.ProposedTasks[1].Title);
        Assert.Equal(3, result.ProposedTasks[1].Priority);
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.StageStarted);
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.StageCompleted);
    }

    [Fact]
    public async Task SendMessageAsync_ExtractsProposals_FromMarkdownJsonCodeBlock_DoesNotInjectImplicitDependencies()
    {
        var fakeClient = new FakeChatClient
        {
            ResponseToReturn = """
                ```json
                [
                  { "title": "First Independent Feature", "description": "Details A" },
                  { "title": "Second Independent Feature", "description": "Details B" },
                  { "title": "Third Dependent Feature", "description": "Details C", "dependsOnTitles": ["First Independent Feature"], "dependsOnIds": [10] }
                ]
                ```
                """
        };

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = fakeClient,
            PathResolver = TestPathResolver.Create()
        };
        var coordinator = builder.Build();

        var result = await builder.ConverseAsync(coordinator, CreateWorkspace(1, "Test WS"), "Plan tasks");

        Assert.Equal(3, result.ProposedTasks.Count);
        Assert.Empty(result.ProposedTasks[0].DependsOnTitles ?? Array.Empty<string>());
        Assert.Empty(result.ProposedTasks[1].DependsOnTitles ?? Array.Empty<string>());
        Assert.Equal(["First Independent Feature"], result.ProposedTasks[2].DependsOnTitles);
        Assert.Equal([10], result.ProposedTasks[2].DependsOnIds);
    }

    [Fact]
    public async Task SendMessageAsync_UsesWorkspaceIdeationProfile_WhenPresent()
    {
        ChatOptions? capturedOptions = null;
        var capturingClient = new CapturingChatClient(opts => capturedOptions = opts)
        {
            ResponseToReturn = "Architectural advice ready."
        };

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = capturingClient,
            PathResolver = TestPathResolver.Create()
        };
        var coordinator = builder.Build();
        var workspace = new Workspace { Id = 2, Name = "Arch WS", RepositoryPath = Directory.GetCurrentDirectory() };
        workspace.AgentProfiles.Add(new AgentProfile
        {
            Id = "ideation-architect",
            Name = "Ideation Architect",
            ModelName = "custom-architect-model:32b",
            SystemPrompt = "Focus on clean DDD boundaries.",
            Mode = AgentMode.ReadOnly
        });

        var result = await builder.ConverseAsync(coordinator, workspace, "Suggest architecture improvements");

        Assert.NotNull(result);
        Assert.NotNull(capturedOptions);
        Assert.Contains("Focus on clean DDD boundaries.", capturedOptions.Instructions);
        Assert.Contains("Architectural advice ready.", result.AssistantMessage);
    }

    [Fact]
    public async Task SendMessageAsync_ResolvesModelViaChatClientFactory()
    {
        AgentProfile? requestedProfile = null;
        var fakeClient = new FakeChatClient { ResponseToReturn = "Factory resolved successfully." };
        var factory = new MockChatClientFactory(profile =>
        {
            requestedProfile = profile;
            return fakeClient;
        });

        using var builder = new TestIdeationCoordinatorBuilder
        {
            ChatClientFactory = factory,
            PathResolver = TestPathResolver.Create()
        };
        var coordinator = builder.Build();
        var workspace = CreateWorkspace(3);
        var customProfile = new AgentProfile
        {
            Id = "custom",
            Name = "Custom Ideation",
            ModelName = "gemma2:27b",
            Mode = AgentMode.ReadOnly
        };

        var result = await builder.ConverseAsync(coordinator, workspace, "Hello", agentProfile: customProfile);

        Assert.NotNull(requestedProfile);
        Assert.Equal("gemma2:27b", requestedProfile.ModelName);
        Assert.Equal("Factory resolved successfully.", result.AssistantMessage);
    }

    [Fact]
    public void ResolveIdeationProfile_ThrowsAgentProfileNotFoundException_WhenNoProfileConfigured()
    {
        var workspace = new Workspace { Id = 99, Name = "Empty Profiles WS" };
        var ex = Assert.Throws<AgentProfileNotFoundException>(() => IdeationConversationCoordinator.ResolveIdeationProfile(workspace));
        Assert.Equal("ideation", ex.ProfileId);
        Assert.Equal(99, ex.WorkspaceId);
    }

    [Fact]
    public async Task Streaming_StreamsStructuredTextReasoningContent_AndExtractsProposals()
    {
        var streamer = new CapturingActivityStreamer();
        var streamingClient = new MockUpdateStreamingChatClient(
        [
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("Reasoning about domain...\n")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("Selecting appropriate pattern.\n")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Here is the resulting plan.\n")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("```json\n[{\"title\": \"Test Task\", \"description\": \"Valid\", \"priority\": 1}]\n```")])
        ]);

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = streamingClient,
            ActivityStreamer = streamer,
            PathResolver = TestPathResolver.Create()
        };
        var coordinator = builder.Build();
        var workspace = CreateWorkspace(10);

        var updates = new List<IdeationStreamUpdate>();
        await foreach (var update in builder.ConverseStreamingAsync(coordinator, workspace, "Design module"))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count(u => u.Kind == IdeationStreamUpdateKind.ThinkingDelta));
        Assert.Contains(updates, u => u.Kind == IdeationStreamUpdateKind.ThinkingDelta && u.TextDelta!.Contains("Reasoning about domain"));
        Assert.Contains(updates, u => u.Kind == IdeationStreamUpdateKind.MessageDelta && u.TextDelta!.Contains("Here is the resulting plan"));
        Assert.Contains(updates, u => u.Kind == IdeationStreamUpdateKind.ProposalDiscovered && u.Proposal?.Title == "Test Task");

        var completed = updates.FirstOrDefault(u => u.Kind == IdeationStreamUpdateKind.Completed);
        Assert.NotNull(completed?.FinalResult);
        Assert.Single(completed.FinalResult.ProposedTasks);

        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.ThinkingDelta && e.Content.Contains("Reasoning about"));
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.MessageDelta && e.Content.Contains("Here is the resulting plan"));
    }

    [Fact]
    public async Task SendMessageAsync_WithNonStreamingClient_FallsBackGracefullyAndBroadcastsFeedback()
    {
        var streamer = new CapturingActivityStreamer();
        var fakeClient = new FakeChatClient
        {
            ResponseToReturn = "Finished proposals:\n```json\n[{\"title\": \"Fallback Task\", \"description\": \"Test desc\"}]\n```"
        };

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = fakeClient,
            ActivityStreamer = streamer,
            PathResolver = TestPathResolver.Create()
        };
        var coordinator = builder.Build();

        var result = await builder.ConverseAsync(coordinator, CreateWorkspace(11), "Suggest a fallback task");

        Assert.Single(result.ProposedTasks);
        Assert.Equal("Fallback Task", result.ProposedTasks[0].Title);
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.StageStarted && e.StageId == StandardStageIds.Ideation);
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.MessageDelta && e.Content.Contains("Finished proposals"));
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.StageCompleted && e.StageId == StandardStageIds.Ideation);
    }

    [Fact]
    public async Task Streaming_ExecutesToolCalls_AndIncorporatesPendingProposals()
    {
        var streamer = new CapturingActivityStreamer();
        int callCount = 0;

        var client = new MultiTurnChatClient(_ =>
        {
            callCount++;
            if (callCount == 1)
            {
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, [
                    new FunctionCallContent("call-1", "propose_tasks", new Dictionary<string, object?>
                    {
                        ["tasks"] = new List<object?> { new Dictionary<string, object?> { ["title"] = "Tool Generated ForgeTask", ["description"] = "From tool call" } }
                    })
                ]));
            }
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Proposals are submitted and finalized."));
        });

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = client,
            ActivityStreamer = streamer,
            PathResolver = TestPathResolver.Create()
        };
        var coordinator = builder.Build();

        var result = await builder.ConverseAsync(coordinator, CreateWorkspace(12), "Create backlog tasks");

        Assert.Single(result.ProposedTasks);
        Assert.Equal("Tool Generated ForgeTask", result.ProposedTasks[0].Title);
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.ToolStarted && e.Content.Contains("propose_tasks"));
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.ToolCompleted);
    }

    [Fact]
    public async Task SendMessageAsync_WithWorkspacePathResolver_InstantiatesCanonicalToolsAndExecutesSuccessfully()
    {
        var fakeClient = new FakeChatClient { ResponseToReturn = "Exploration complete." };
        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = fakeClient,
            PathResolver = new FakePathResolver()
        };
        var coordinator = builder.Build();

        var result = await builder.ConverseAsync(coordinator, CreateWorkspace(42, "Test WS"), "Explore the architecture");

        Assert.Equal("Exploration complete.", result.AssistantMessage);
    }

    [Fact]
    public async Task Streaming_YieldsToolCallStartedAndCompletedUpdates()
    {
        var streamer = new CapturingActivityStreamer();
        int turn = 0;

        var client = new MultiTurnStreamingChatClient(_ =>
        {
            turn++;
            if (turn == 1)
            {
                return new ChatResponseUpdate(ChatRole.Assistant, [
                    new FunctionCallContent("call-1", "propose_tasks", new Dictionary<string, object?>
                    {
                        ["tasks"] = new List<object?> { new Dictionary<string, object?> { ["title"] = "Streaming Tool ForgeTask", ["description"] = "Discovered via tool" } }
                    })
                ]);
            }
            return new ChatResponseUpdate(ChatRole.Assistant, "Task proposal has been registered.");
        });

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = client,
            ActivityStreamer = streamer,
            PathResolver = TestPathResolver.Create()
        };
        var coordinator = builder.Build();

        var updates = new List<IdeationStreamUpdate>();
        await foreach (var update in builder.ConverseStreamingAsync(coordinator, CreateWorkspace(50), "Suggest tasks"))
        {
            updates.Add(update);
        }

        var startedUpdate = updates.FirstOrDefault(u => u.Kind == IdeationStreamUpdateKind.ToolCallStarted);
        Assert.NotNull(startedUpdate?.ToolCall);
        Assert.Equal("propose_tasks", startedUpdate.ToolCall.ToolName);

        var completedUpdate = updates.FirstOrDefault(u => u.Kind == IdeationStreamUpdateKind.ToolCallCompleted);
        Assert.NotNull(completedUpdate?.ToolCall);
        Assert.Equal("propose_tasks", completedUpdate.ToolCall.ToolName);
        Assert.False(completedUpdate.ToolCall.IsError);
        Assert.NotNull(completedUpdate.ToolCall.Result);

        var completed = updates.FirstOrDefault(u => u.Kind == IdeationStreamUpdateKind.Completed);
        Assert.NotNull(completed?.FinalResult?.ToolCalls);
        Assert.Single(completed.FinalResult.ToolCalls);
        Assert.Equal("propose_tasks", completed.FinalResult.ToolCalls[0].ToolName);
    }

    [Fact]
    public async Task Streaming_LogsAndYieldsToolFailure_WhenToolFails()
    {
        var logger = new TestLogger<IdeationConversationCoordinator>();
        int turn = 0;

        var client = new MultiTurnStreamingChatClient(_ =>
        {
            turn++;
            if (turn == 1)
            {
                return new ChatResponseUpdate(ChatRole.Assistant, [
                    new FunctionCallContent("call-err", "unknown_magic_tool", new Dictionary<string, object?> { ["param"] = 123 })
                ]);
            }
            return new ChatResponseUpdate(ChatRole.Assistant, "Continuing after tool failure.");
        });

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = client,
            Logger = logger,
            PathResolver = TestPathResolver.Create()
        };
        var coordinator = builder.Build();

        var updates = new List<IdeationStreamUpdate>();
        await foreach (var update in builder.ConverseStreamingAsync(coordinator, CreateWorkspace(51), "Run invalid tool"))
        {
            updates.Add(update);
        }

        var startedUpdate = updates.FirstOrDefault(u => u.Kind == IdeationStreamUpdateKind.ToolCallStarted);
        Assert.Equal("unknown_magic_tool", startedUpdate?.ToolCall?.ToolName);

        var completedUpdate = updates.FirstOrDefault(u => u.Kind == IdeationStreamUpdateKind.ToolCallCompleted);
        Assert.Equal("unknown_magic_tool", completedUpdate?.ToolCall?.ToolName);
        Assert.True(completedUpdate!.ToolCall!.IsError);
        Assert.NotNull(completedUpdate.ToolCall.Result);
        Assert.Contains(logger.Logs, l => l.Level >= LogLevel.Warning && l.Message.Contains("unknown_magic_tool"));
    }

    [Fact]
    public async Task SavesPerConversationTranscript_WithConversationIdFrontmatter()
    {
        var tempTranscriptsDir = Path.Combine(Path.GetTempPath(), "ncf-transcripts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var transcriptService = new SessionTranscriptService(tempTranscriptsDir);
            var client = new MultiTurnStreamingChatClient(_ => "Here is the recommendation.");

            using var builder = new TestIdeationCoordinatorBuilder
            {
                BaseChatClient = client,
                TranscriptService = transcriptService,
                PathResolver = TestPathResolver.Create()
            };
            var workspace = CreateWorkspace(77);
            await builder.SeedWorkspaceAsync(workspace);
            var coordinator = builder.Build();
            var handle = await coordinator.StartConversationAsync(workspace.Id, "Feature Plan");

            var result = await coordinator.SendMessageAsync(new IdeationSendRequest(workspace.Id, handle.Conversation.Id, "Plan new feature"));

            Assert.NotNull(result.TranscriptPath);
            Assert.True(File.Exists(result.TranscriptPath));
            var filename = Path.GetFileName(result.TranscriptPath);
            Assert.Contains($"-ideation-conv-{handle.Conversation.Id}-", filename);
            Assert.NotNull(result.FullTranscriptMarkdown);
            Assert.Contains($"conversation_id: {handle.Conversation.Id}", result.FullTranscriptMarkdown);
            Assert.Contains("Plan new feature", result.FullTranscriptMarkdown);
            Assert.Contains("Here is the recommendation.", result.FullTranscriptMarkdown);
        }
        finally
        {
            if (Directory.Exists(tempTranscriptsDir))
            {
                try
                {
                    Directory.Delete(tempTranscriptsDir, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort temp cleanup
                }
            }
        }
    }

    [Fact]
    public void Coordinator_ExposesExplicitConstantsAndTimeouts()
    {
        Assert.Equal(500, IdeationConversationCoordinator.DefaultMaxTurns);
        Assert.Equal(49_152, IIdeationConversationCoordinator.DefaultContextIntentTokens);
        Assert.Equal(TimeSpan.FromMinutes(30), IdeationConversationCoordinator.DefaultSessionIdleTimeout);
        Assert.Equal(500, AgentProfile.DefaultMaxTurns);

        var profile = AgentProfile.CreateDefaultIdeationProfile();
        Assert.Equal(300, profile.MaxTurns);
    }

    [Fact]
    public async Task Streaming_InjectsEnvironmentSectionWithPwd()
    {
        List<ChatMessage>? capturedMessages = null;
        var fakeClient = new MessageCapturingChatClient(msgs => capturedMessages = msgs.ToList())
        {
            ResponseToReturn = "I am ready."
        };

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = fakeClient,
            PathResolver = TestPathResolver.Create()
        };
        var coordinator = builder.Build();
        var workspace = new Workspace
        {
            Id = 42,
            Name = "Env Test Workspace",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            RepositoryPath = Directory.GetCurrentDirectory()
        };
        workspace.AgentProfiles.Add(AgentProfile.CreateDefaultIdeationProfile(42));

        await builder.ConverseAsync(coordinator, workspace, "Hello");

        Assert.NotNull(capturedMessages);
        var systemMsg = capturedMessages.FirstOrDefault(m => m.Role == ChatRole.System);
        Assert.NotNull(systemMsg);
        Assert.Contains("<environment>", systemMsg.Text);
        Assert.Contains("Current Working Directory (PWD):", systemMsg.Text);
        Assert.Contains("Repository Root:", systemMsg.Text);
        Assert.Contains("ContainerSandbox", systemMsg.Text);
    }

    [Fact]
    public async Task ProposeTasks_KeepsMultipleTasksIndependent_AndPreservesExplicitDependencies()
    {
        int turn = 0;
        var client = new MultiTurnStreamingChatClient(_ =>
        {
            turn++;
            if (turn == 1)
            {
                return new ChatResponseUpdate(ChatRole.Assistant, [
                    new FunctionCallContent("call-1", "propose_tasks", new Dictionary<string, object?>
                    {
                        ["tasks"] = new List<object?>
                        {
                            new Dictionary<string, object?> { ["title"] = "Setup DB migrations", ["description"] = "Configure schema" },
                            new Dictionary<string, object?> { ["title"] = "Add metrics endpoint", ["description"] = "Independent observability work" },
                            new Dictionary<string, object?>
                            {
                                ["title"] = "Expose domain API",
                                ["description"] = "Depends on schema and task #42",
                                ["dependsOnTitles"] = new List<object?> { "Setup DB migrations" },
                                ["depends_on_ids"] = new List<object?> { 42 }
                            }
                        }
                    })
                ]);
            }
            return new ChatResponseUpdate(ChatRole.Assistant, "Proposed tasks with explicit dependencies.");
        });

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create()
        };
        var coordinator = builder.Build();

        var result = await builder.ConverseAsync(coordinator, CreateWorkspace(10), "Plan feature implementation");
        Assert.Equal(3, result.ProposedTasks.Count);
        Assert.Empty(result.ProposedTasks[0].DependsOnTitles ?? Array.Empty<string>());
        Assert.Empty(result.ProposedTasks[1].DependsOnTitles ?? Array.Empty<string>());
        Assert.Equal(["Setup DB migrations"], result.ProposedTasks[2].DependsOnTitles);
        Assert.Equal([42], result.ProposedTasks[2].DependsOnIds);
    }

    [Fact]
    public async Task ProposeTasks_HandlesSingleTaskAndSerializedJsonString()
    {
        int turn = 0;
        var client = new MultiTurnStreamingChatClient(_ =>
        {
            turn++;
            if (turn == 1)
            {
                var innerJson = "[{\"title\": \"Step Alpha\", \"description\": \"Alpha details\"}, {\"title\": \"Step Beta\", \"description\": \"Beta details\"}]";
                return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-1", "propose_tasks", new Dictionary<string, object?> { ["tasks"] = innerJson })]);
            }
            return new ChatResponseUpdate(ChatRole.Assistant, "Independent tasks parsed.");
        });

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create()
        };
        var coordinator = builder.Build();

        var result = await builder.ConverseAsync(coordinator, CreateWorkspace(12), "Build pipeline");
        Assert.Equal(2, result.ProposedTasks.Count);
        Assert.Equal("Step Alpha", result.ProposedTasks[0].Title);
        Assert.Equal("Step Beta", result.ProposedTasks[1].Title);
    }

    [Fact]
    public async Task SystemPrompt_InstructsProposeTasksPRSizingAndBacklogManagement()
    {
        List<ChatMessage>? capturedMessages = null;
        var fakeClient = new MessageCapturingChatClient(msgs => capturedMessages = msgs.ToList()) { ResponseToReturn = "Ready." };

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = fakeClient,
            PathResolver = TestPathResolver.Create()
        };
        var coordinator = builder.Build();

        await builder.ConverseAsync(coordinator, CreateWorkspace(14), "Plan new features");

        Assert.NotNull(capturedMessages);
        var systemMsg = capturedMessages.FirstOrDefault(m => m.Role == ChatRole.System);
        Assert.NotNull(systemMsg);
        Assert.Contains("propose_tasks", systemMsg.Text);
        Assert.Contains("independent by default", systemMsg.Text);
        Assert.Contains("depends_on_ids", systemMsg.Text);
        Assert.Contains("edit_task", systemMsg.Text);
        Assert.DoesNotContain("```json", systemMsg.Text);
        Assert.Contains("pull-request", systemMsg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("merge-request", systemMsg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ZERO OPEN BUSINESS QUESTIONS", systemMsg.Text);
        Assert.Contains("Stage 1 (ForgeTask Review)", systemMsg.Text);
        Assert.Contains("<backlog_management_guidelines>", systemMsg.Text);
    }

    [Fact]
    public void CreateDefaultIdeationProfile_PermitsBacklogMutationToolsAndDirectsTaskReview()
    {
        var profile = AgentProfile.CreateDefaultIdeationProfile();
        Assert.Equal(AgentMode.ReadOnly, profile.Mode);
        Assert.NotNull(profile.PermittedTools);
        Assert.Contains(CanonicalToolNames.ProposeTasks, profile.PermittedTools);
        Assert.Contains(CanonicalToolNames.EditTask, profile.PermittedTools);
        Assert.NotNull(profile.SystemPrompt);
        Assert.Contains("Pull Request", profile.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenContextWindowNotSetOnProfile_ResolvesDynamicContextWindow_AndSkipsWhenSet()
    {
        var mockGateway = new Mock<ILlmGatewayService>();
        mockGateway.Setup(g => g.ResolveContextWindowAsync("dyn-model:7b", It.IsAny<CancellationToken>())).ReturnsAsync(32768);

        using var builder1 = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = new FakeChatClient { ResponseToReturn = "ok" },
            GatewayService = mockGateway.Object,
            PathResolver = TestPathResolver.Create()
        };
        var coord1 = builder1.Build();
        var dynProfile = new AgentProfile { Id = StandardStageIds.Ideation, Name = "Ideation", ModelName = "dyn-model:7b", ContextWindowTokens = null };
        var ws1 = new Workspace { Id = 61, Name = "WS1", RepositoryPath = Directory.GetCurrentDirectory(), AgentProfiles = [dynProfile] };
        await builder1.ConverseAsync(coord1, ws1, "Hello");
        mockGateway.Verify(g => g.ResolveContextWindowAsync("dyn-model:7b", It.IsAny<CancellationToken>()), Times.Once);

        mockGateway.Invocations.Clear();
        using var builder2 = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = new FakeChatClient { ResponseToReturn = "ok" },
            GatewayService = mockGateway.Object,
            PathResolver = TestPathResolver.Create()
        };
        var coord2 = builder2.Build();
        var fixedProfile = new AgentProfile { Id = StandardStageIds.Ideation, Name = "Ideation", ModelName = "fixed-model:7b", ContextWindowTokens = 65536 };
        var ws2 = new Workspace { Id = 62, Name = "WS2", RepositoryPath = Directory.GetCurrentDirectory(), AgentProfiles = [fixedProfile] };
        await builder2.ConverseAsync(coord2, ws2, "Hello");
        mockGateway.Verify(g => g.ResolveContextWindowAsync("fixed-model:7b", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhenWorkspaceHasTranscriptFriction_InjectsHotspotsIntoSystemPrompt_AndOmitsWhenEmpty()
    {
        var captured = new List<string>();
        var workspaceContextMock = new Mock<IAgentWorkspaceContextService>();
        workspaceContextMock.Setup(c => c.ResolveLocations(It.IsAny<Workspace>(), null, null))
            .Returns((Workspace ws, int? _, string? __) => new AgentWorkspaceLocations(ws.RepositoryPath, ws.RepositoryPath, ws.RepositoryPath));
        workspaceContextMock.Setup(c => c.BuildPromptContextAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IWorkspaceSkillsService>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentPromptContext(string.Empty, string.Empty, Array.Empty<string>()));
        workspaceContextMock.Setup(c => c.BuildBacklogDigestAsync(It.IsAny<int>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        workspaceContextMock.Setup(c => c.BuildWorkspaceAttachmentsDigestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        workspaceContextMock.SetupSequence(c => c.BuildFrictionHotspotsDigestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<workspace_friction_hotspots>\n- src/Hotspot.cs (modified in 4 failed runs)\n</workspace_friction_hotspots>")
            .ReturnsAsync(string.Empty);

        var client = new MessageCapturingChatClient(msgs =>
        {
            var sys = msgs.FirstOrDefault(m => m.Role == ChatRole.System);
            if (sys?.Text != null) captured.Add(sys.Text);
        })
        { ResponseToReturn = "ok" };

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = client,
            WorkspaceContextService = workspaceContextMock.Object,
            PathResolver = TestPathResolver.Create()
        };
        var coordinator = builder.Build();

        await builder.ConverseAsync(coordinator, CreateWorkspace(71), "Turn A");
        await builder.ConverseAsync(coordinator, CreateWorkspace(72), "Turn B");

        Assert.Equal(2, captured.Count);
        Assert.Contains("<workspace_friction_hotspots>", captured[0]);
        Assert.Contains("src/Hotspot.cs", captured[0]);
        Assert.DoesNotContain("<workspace_friction_hotspots>", captured[1]);
    }

    // -------------------------------------------------------------------------
    // Step 12.31 Coordinator Tests (§4.3)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task WarmupSessionAsync_PreloadsWithDefault49152ContextAndExplicitWindow_AndDeduplicates()
    {
        var preloadTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preloadCalls = new List<(string Model, ModelPreloadOptions? Options)>();
        var preloadMock = new Mock<ILlmModelPreloadService>();
        preloadMock.Setup(p => p.PreloadModelAsync(It.IsAny<string>(), It.IsAny<ModelPreloadOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, ModelPreloadOptions?, CancellationToken>((m, o, _) =>
            {
                preloadCalls.Add((m, o));
                preloadTcs.TrySetResult(true);
            })
            .ReturnsAsync((string m, ModelPreloadOptions? o, CancellationToken _) => new ModelPreloadResult(true, m));

        var tracker = new TrackingBackgroundTaskRunner();
        using var builder = new TestIdeationCoordinatorBuilder
        {
            PreloadService = preloadMock.Object,
            BackgroundTaskRunner = tracker,
            PathResolver = TestPathResolver.Create()
        };
        var ws1 = new Workspace { Id = 101, Name = "Preload Default", RepositoryPath = Directory.GetCurrentDirectory() };
        ws1.AgentProfiles.Add(CreatePreloadProfile(ws1.Id, contextWindowTokens: null, target: OllamaRoutingTarget.Remote));
        await builder.SeedWorkspaceAsync(ws1);

        var coordinator = builder.Build();
        var handle = await coordinator.GetOrCreateActiveConversationAsync(ws1.Id);
        await preloadTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.WarmupSessionAsync(ws1.Id, handle.Conversation.Id);
        await tracker.WaitForAllAsync();

        var firstPreload = Assert.Single(preloadCalls);
        Assert.Equal("qwen3-coder:30b", firstPreload.Model);
        Assert.Equal(49_152, firstPreload.Options?.ContextIntentTokens);
        Assert.Equal("Remote", firstPreload.Options?.Target);
        Assert.Equal(StandardStageIds.Ideation, firstPreload.Options?.StageId);

        // Explicit profile context window overrides default 49_152
        preloadCalls.Clear();
        var ws2 = new Workspace { Id = 102, Name = "Preload Explicit", RepositoryPath = Directory.GetCurrentDirectory() };
        ws2.AgentProfiles.Add(CreatePreloadProfile(ws2.Id, contextWindowTokens: 98_304));
        await builder.SeedWorkspaceAsync(ws2);
        var handle2 = await coordinator.StartConversationAsync(ws2.Id, "Explicit");
        await tracker.WaitForAllAsync();

        var secondPreload = Assert.Single(preloadCalls);
        Assert.Equal(98_304, secondPreload.Options?.ContextIntentTokens);
    }

    [Fact]
    public async Task WarmupSessionAsync_WhenPausedSkips_AndStaleWarmupDiscardsObsoleteSetup()
    {
        bool isPaused = true;
        var opsMock = new Mock<IOperationsControlService>();
        opsMock.SetupGet(o => o.IsPaused).Returns(() => isPaused);

        var preloadMock = new Mock<ILlmModelPreloadService>();
        using var builderPaused = new TestIdeationCoordinatorBuilder
        {
            OperationsControlService = opsMock.Object,
            PreloadService = preloadMock.Object,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(103);
        ws.AgentProfiles.First().OptimizationFlags = new LocalOptimizationFlags { JitModelPreload = true };
        await builderPaused.SeedWorkspaceAsync(ws);
        var pausedCoord = builderPaused.Build();
        var pausedConv = await pausedCoord.StartConversationAsync(ws.Id, "Paused");
        await pausedCoord.WarmupSessionAsync(ws.Id, pausedConv.Conversation.Id);
        preloadMock.Verify(p => p.PreloadModelAsync(It.IsAny<string>(), It.IsAny<ModelPreloadOptions?>(), It.IsAny<CancellationToken>()), Times.Never);

        // Now verify stale warmup discards obsolete HarnessAgentSetup when UpdateSessionOptionsAsync bumps ConfigGeneration mid-flight
        isPaused = false;
        var warmupStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstToolsetToFinish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var createdClients = new List<TrackingDisposableChatClient>();
        int createCall = 0;

        var toolsetFactoryMock = new Mock<ICanonicalToolsetFactory>();
        toolsetFactoryMock.Setup(f => f.CreateAsync(It.IsAny<CanonicalToolsetRequest>(), It.IsAny<CancellationToken>()))
            .Returns<CanonicalToolsetRequest, CancellationToken>(async (req, ct) =>
            {
                int call = Interlocked.Increment(ref createCall);
                if (call == 1)
                {
                    warmupStarted.TrySetResult(true);
                    await allowFirstToolsetToFinish.Task.WaitAsync(ct);
                }

                var realFactory = new CanonicalToolsetFactory(
                    TestPathResolver.Create(),
                    new SingleExecutionEnvironmentProvider(new Mock<IExecutionEnvironment>().Object),
                    new EditorConfigService(),
                    new EditorConfigFileApplier(),
                    new Mock<IWebSearchService>().Object,
                    new WorkspaceSkillsServiceFactory(new SkillsService(), new BundledSkillsProvider(), NullLogger<WorkspaceSkillsService>.Instance),
                    new Mock<IMcpClientManager>().Object,
                    TestBacklogContext.CreateDefault(),
                    NightCodeForge.Core.Workflow.Filtering.CliOutputFilterRegistry.Default);
                return await realFactory.CreateAsync(req, ct);
            });

        var factoryMock = new Mock<ILlmChatClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<AgentProfile?>(), It.IsAny<string?>()))
            .Returns<AgentProfile?, string?>((p, s) =>
            {
                var c = new TrackingDisposableChatClient(p?.ModelName ?? "default", s);
                createdClients.Add(c);
                return c;
            });

        using var builder = new TestIdeationCoordinatorBuilder
        {
            OperationsControlService = opsMock.Object,
            ToolsetFactory = toolsetFactoryMock.Object,
            ChatClientFactory = factoryMock.Object,
            PathResolver = TestPathResolver.Create()
        };
        await builder.SeedWorkspaceAsync(ws);
        var coord = builder.Build();
        var handle = await coord.StartConversationAsync(ws.Id, "Race");

        await warmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Mutate options while warmup #1 is suspended mid-toolset creation
        var updateTask = coord.UpdateSessionOptionsAsync(
            ws.Id,
            handle.Conversation.Id,
            new IdeationSessionOptions(ModelOverride: "new-model:14b"));
        allowFirstToolsetToFinish.TrySetResult(true);
        await updateTask;

        Assert.Equal(2, createdClients.Count);
        Assert.Equal("new-model:14b", createdClients[1].ModelName);
        Assert.False(createdClients[1].IsDisposed);
        // The stale setup #1 was discarded or never attached as active setup when gen mismatched, OR disposed when superseded:
        Assert.True(createdClients[0].IsDisposed);
    }

    private sealed class TrackingDisposableChatClient : IChatClient
    {
        public TrackingDisposableChatClient(string modelName, string? sessionId)
        {
            ModelName = modelName;
            SessionId = sessionId;
        }

        public string ModelName { get; }
        public string? SessionId { get; }
        public bool IsDisposed { get; private set; }
        public ChatClientMetadata Metadata => new("Tracking");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"reply:{ModelName}");
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => IsDisposed = true;
    }

    [Fact]
    public async Task SendMessageAsync_ReusesWarmToolsetAndAgentAcrossTurns_PreservesHistoryAndRouterAffinity()
    {
        int toolsetCreations = 0;
        int agentCreations = 0;
        var seenHistories = new List<List<ChatMessage>>();
        var capturedSessionIds = new List<string?>();

        var client = new MessageCapturingChatClient(msgs => seenHistories.Add(msgs.ToList()))
        {
            ResponseToReturn = "Reply from warm agent"
        };

        var chatFactoryMock = new Mock<ILlmChatClientFactory>();
        chatFactoryMock.Setup(f => f.CreateClient(It.IsAny<AgentProfile?>(), It.IsAny<string?>()))
            .Callback<AgentProfile?, string?>((_, sid) => capturedSessionIds.Add(sid))
            .Returns(client);

        var toolsetFactoryMock = new Mock<ICanonicalToolsetFactory>();
        toolsetFactoryMock.Setup(f => f.CreateAsync(It.IsAny<CanonicalToolsetRequest>(), It.IsAny<CancellationToken>()))
            .Returns<CanonicalToolsetRequest, CancellationToken>(async (req, ct) =>
            {
                Interlocked.Increment(ref toolsetCreations);
                var real = new CanonicalToolsetFactory(
                    TestPathResolver.Create(),
                    new SingleExecutionEnvironmentProvider(new Mock<IExecutionEnvironment>().Object),
                    new EditorConfigService(),
                    new EditorConfigFileApplier(),
                    new Mock<IWebSearchService>().Object,
                    new WorkspaceSkillsServiceFactory(new SkillsService(), new BundledSkillsProvider(), NullLogger<WorkspaceSkillsService>.Instance),
                    new Mock<IMcpClientManager>().Object,
                    TestBacklogContext.CreateDefault(),
                    NightCodeForge.Core.Workflow.Filtering.CliOutputFilterRegistry.Default);
                return await real.CreateAsync(req, ct);
            });

        HarnessAgentSetup? createdSetup = null;
        var realAgentFactory = new HarnessAgentFactory(
            chatFactoryMock.Object,
            Mock.Of<ILlmGatewayService>(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()) == Task.FromResult(131072)),
            new OllamaCapabilityResolver(Microsoft.Extensions.Options.Options.Create(new LlmProviderOptions()), EmptyLlmContextWindowCache.Instance),
            new Mock<IGlobalOptimizationSettingsService>().Object,
            Microsoft.Extensions.Options.Options.Create(new LlmProviderOptions()),
            NullLogger<HarnessAgentFactory>.Instance);

        var agentFactoryMock = new Mock<IHarnessAgentFactory>();
        agentFactoryMock.Setup(f => f.ResolveOptimizationFlags(It.IsAny<AgentProfile>(), It.IsAny<LocalOptimizationFlags?>()))
            .Returns<AgentProfile, LocalOptimizationFlags?>((p, f) => realAgentFactory.ResolveOptimizationFlags(p, f));
        agentFactoryMock.Setup(f => f.CreateAsync(It.IsAny<HarnessAgentRequest>(), It.IsAny<CancellationToken>()))
            .Returns<HarnessAgentRequest, CancellationToken>(async (req, ct) =>
            {
                Interlocked.Increment(ref agentCreations);
                createdSetup = await realAgentFactory.CreateAsync(req, ct);
                return createdSetup;
            });

        using var builder = new TestIdeationCoordinatorBuilder
        {
            ToolsetFactory = toolsetFactoryMock.Object,
            HarnessAgentFactory = agentFactoryMock.Object,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(104);
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Warm MultiTurn");
        await coordinator.WarmupSessionAsync(ws.Id, handle.Conversation.Id);

        await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Turn 1 prompt"));
        client.ResponseToReturn = "Reply #2";
        await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Turn 2 prompt"));

        Assert.Equal(1, toolsetCreations);
        Assert.Equal(1, agentCreations);
        Assert.Equal(LlmSessionIds.ForIdeation(ws.Id, handle.Conversation.Id), Assert.Single(capturedSessionIds));
        Assert.Equal(2, seenHistories.Count);
        Assert.Contains(seenHistories[1], m => m.Role == ChatRole.User && m.Text!.Contains("Turn 1 prompt"));
        Assert.Contains(seenHistories[1], m => m.Role == ChatRole.Assistant && m.Text!.Contains("Reply from warm agent"));
        Assert.Contains(seenHistories[1], m => m.Role == ChatRole.User && m.Text!.Contains("Turn 2 prompt"));
    }

    [Fact]
    public async Task SendMessageAsync_Over300CumulativeModelCalls_CapturesTurn2CallsUsingTotalModelCallsBaseline()
    {
        var tempTranscripts = Path.Combine(Path.GetTempPath(), "ncf-300calls-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repo = new InMemoryWorkspaceRepository();
            var taskRepo = new InMemoryTaskRepository();
            int callSeq = 0;

            var client = new MultiTurnStreamingChatClient(_ =>
            {
                int n = Interlocked.Increment(ref callSeq);
                if (n <= 301)
                {
                    return new ChatResponseUpdate(ChatRole.Assistant, [
                        new FunctionCallContent($"call_{n}", CanonicalToolNames.Read, new Dictionary<string, object?> { ["path"] = "missing.txt" })
                    ]);
                }
                if (n == 302)
                {
                    return new ChatResponseUpdate(ChatRole.Assistant, "Finished turn 1 after 302 calls.");
                }
                return new ChatResponseUpdate(ChatRole.Assistant, "Turn 2 reply after call cap.");
            });

            using var builder = new TestIdeationCoordinatorBuilder
            {
                BaseChatClient = client,
                WorkspaceRepository = repo,
                TaskRepository = taskRepo,
                TranscriptService = new SessionTranscriptService(tempTranscripts),
                PathResolver = TestPathResolver.Create()
            };
            var ws = new Workspace { Id = 105, Name = "Cap WS", RepositoryPath = Directory.GetCurrentDirectory() };
            ws.AgentProfiles.Add(new AgentProfile
            {
                Id = StandardStageIds.Ideation,
                Name = "Ideation",
                WorkspaceId = ws.Id,
                ModelName = "test-model",
                Mode = AgentMode.ReadOnly,
                MaxTurns = 500
            });
            await builder.SeedWorkspaceAsync(ws);
            var coordinator = builder.Build();
            var handle = await coordinator.StartConversationAsync(ws.Id, "Over300");

            var res1 = await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Run many tools"));
            Assert.Contains("Finished turn 1", res1.AssistantMessage);
            Assert.Equal(302, callSeq);
            Assert.Equal(ThinkingAndActivityTracingChatClient.MaxRetainedModelCalls, 300);

            var res2 = await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Follow up question"));
            Assert.Contains("Turn 2 reply", res2.AssistantMessage);
            Assert.NotNull(res2.FullTranscriptMarkdown);
            Assert.Contains("### Model calls (requests as sent to the model)", res2.FullTranscriptMarkdown);
            Assert.Contains($"Call {callSeq}", res2.FullTranscriptMarkdown);
        }
        finally
        {
            if (Directory.Exists(tempTranscripts))
            {
                try
                {
                    Directory.Delete(tempTranscripts, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort temp cleanup
                }
            }
        }
    }

    [Fact]
    public async Task SendMessageAsync_PersistsStructuredTurnsAndCompactionCheckpoint_InChronologicalOrder()
    {
        int turn = 0;
        var longPrev = new List<ConversationTurn>();
        var client = new MultiTurnStreamingChatClient(msgs =>
        {
            turn++;
            if (turn == 1)
            {
                return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("Thinking before ask...")]);
            }
            // During turn 1 the Ask tool call returns from ModelCall #1: let's emit Thinking + Ask call together, then summary/assistant
            return new ChatResponseUpdate(ChatRole.Assistant, "Done.");
        });

        // Use MultiTurnChatClient on non-streaming fallback or MultiTurnUpdateStreaming
        int step = 0;
        var stepClient = new CapturingAskStreamingClient((msgs, opts) =>
        {
            step++;
            if (step == 1)
            {
                // Compaction summarizer OR ask turn
                return
                [
                    new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("Deep reasoning before ask")]),
                    new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("ask-call-1", CanonicalToolNames.Ask, new Dictionary<string, object?>
                    {
                        ["prompt"] = "Which approach?"
                    })])
                ];
            }

            return
            [
                new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Selected approach documented.")])
            ];
        });

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = stepClient,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(106);
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Structured Persistence");

        var askedTcs = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = coordinator.Subscribe(handle.Conversation.Id, upd =>
        {
            if (upd.Kind == IdeationStreamUpdateKind.QuestionAsked && upd.QuestionGroupId.HasValue)
            {
                askedTcs.TrySetResult(upd.QuestionGroupId.Value);
            }
        });

        var sendTask = coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Analyze options"));
        var groupId = await askedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var submitted = await coordinator.SubmitQuestionAnswersAsync(
            handle.Conversation.Id,
            groupId,
            new Dictionary<int, string> { [0] = "Option B" },
            metadataJson: "{\"Answers\":[{\"Question\":\"Which approach?\",\"Answer\":\"Option B\"}]}");
        Assert.True(submitted);

        var result = await sendTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Selected approach documented.", result.AssistantMessage);

        var conv = await builder.TaskRepository.GetConversationWithTurnsAsync(handle.Conversation.Id);
        Assert.NotNull(conv);
        var orderedTurns = conv.Turns.OrderBy(t => t.TurnIndex).ToList();
        var roles = orderedTurns.Select(t => t.Role).ToList();
        Assert.Equal([StageTurnRole.User, StageTurnRole.Thinking, StageTurnRole.Tool, StageTurnRole.ToolResult, StageTurnRole.Assistant], roles);

        Assert.True(orderedTurns[1].DurationMs >= 0);
        Assert.Contains("ask-call-1", orderedTurns[2].MetadataJson);
        Assert.Contains("ask-call-1", orderedTurns[3].MetadataJson);
        Assert.Contains("Option B", orderedTurns[3].MetadataJson);
    }

    private sealed class CapturingAskStreamingClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyList<ChatResponseUpdate>> _fn;
        public CapturingAskStreamingClient(Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyList<ChatResponseUpdate>> fn) => _fn = fn;
        public ChatClientMetadata Metadata => new("AskStreaming");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summarized compact history.")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var u in _fn(chatMessages, options))
            {
                yield return u;
            }
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task ColdResume_LegacyToolResultAndHighTokenHistory_ArmsCompactionAndRestoresCleanly()
    {
        var taskRepo = new InMemoryTaskRepository();
        var ws = new Workspace { Id = 107, Name = "Cold Resume WS", RepositoryPath = Directory.GetCurrentDirectory() };
        var profile = new AgentProfile
        {
            Id = StandardStageIds.Ideation,
            Name = "Ideation",
            WorkspaceId = ws.Id,
            ModelName = "compact-model:7b",
            Mode = AgentMode.ReadOnly,
            ContextWindowTokens = 4096
        };
        ws.AgentProfiles.Add(profile);

        var conv = await taskRepo.StartIdeationConversationAsync(ws.Id, "Cold Resume Chat");
        await taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Initial seed");
        await taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.ToolResult, toolName: "read", toolArguments: "{\"path\":\"A.cs\"}", toolResult: new string('A', 6500));
        await taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.Assistant, content: new string('B', 6500));
        await taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Second prompt");
        await taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.Assistant, content: new string('C', 6500));

        var seenTurnMessages = new List<List<ChatMessage>>();
        int summarizerCalls = 0;
        var client = new CapturingColdResumeClient(
            onStream: msgs => seenTurnMessages.Add(msgs.ToList()),
            onResponse: _ => Interlocked.Increment(ref summarizerCalls));

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = client,
            TaskRepository = taskRepo,
            PathResolver = TestPathResolver.Create()
        };
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();

        var result = await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, conv.Id, "Next user prompt"));

        Assert.Contains("Resumed response.", result.AssistantMessage);
        Assert.True(summarizerCalls >= 1);
        Assert.Single(seenTurnMessages);

        // Verify persisted Optimization checkpoint turn
        var updatedConv = await taskRepo.GetConversationWithTurnsAsync(conv.Id);
        Assert.Contains(updatedConv!.Turns, t => t.Role == StageTurnRole.Optimization);
        // Ensure zero duplicate User message for "Next user prompt"
        Assert.Equal(1, updatedConv.Turns.Count(t => t.Content?.Contains("Next user prompt") == true));
    }

    private sealed class CapturingColdResumeClient : IChatClient
    {
        private readonly Action<IEnumerable<ChatMessage>> _onStream;
        private readonly Action<IEnumerable<ChatMessage>> _onResponse;

        public CapturingColdResumeClient(Action<IEnumerable<ChatMessage>> onStream, Action<IEnumerable<ChatMessage>> onResponse)
        {
            _onStream = onStream;
            _onResponse = onResponse;
        }

        public ChatClientMetadata Metadata => new("ColdResumeClient");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _onResponse(chatMessages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Compacted cold summary.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _onStream(chatMessages);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Resumed response.");
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task MidTurnSubscriberDisconnectAndReconnect_SurvivesTurnAndAllowsAskSubmission()
    {
        int step = 0;
        var client = new CapturingAskStreamingClient((_, _) =>
        {
            step++;
            if (step == 1)
            {
                return
                [
                    new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("Thinking before question")]),
                    new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("ask-reconnect", CanonicalToolNames.Ask, new Dictionary<string, object?> { ["prompt"] = "Proceed with deploy?" })])
                ];
            }
            return [new ChatResponseUpdate(ChatRole.Assistant, "Turn finished after reconnect.")];
        });

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(108);
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Reconnect");

        var askedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub1 = coordinator.Subscribe(handle.Conversation.Id, u =>
        {
            if (u.Kind == IdeationStreamUpdateKind.QuestionAsked)
            {
                askedTcs.TrySetResult(true);
            }
        });

        await coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Check deployment"));
        await askedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Simulate browser refresh: old circuit disposes its subscription without cancelling turn
        sub1.Dispose();
        Assert.True(coordinator.IsConversationGenerating(handle.Conversation.Id));

        // Reconnect: Subscribe captures atomic InitialSnapshot with partial thinking & pending question
        var completedTcs = new TaskCompletionSource<IdeationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub2 = coordinator.Subscribe(handle.Conversation.Id, u =>
        {
            if (u.Kind == IdeationStreamUpdateKind.Completed && u.FinalResult != null)
            {
                completedTcs.TrySetResult(u.FinalResult);
            }
        });

        Assert.NotNull(sub2.InitialSnapshot);
        Assert.True(sub2.InitialSnapshot.IsGenerating);
        Assert.Contains("Thinking before question", sub2.InitialSnapshot.CurrentThinkingBuffer);
        var questionPart = Assert.Single(sub2.InitialSnapshot.CurrentParts, p => p.Kind == IdeationInFlightPartKind.Question);
        Assert.NotNull(questionPart.QuestionGroupId);
        Assert.Null(questionPart.QuestionAnswer);

        var ok = await coordinator.SubmitQuestionAnswersAsync(
            handle.Conversation.Id,
            questionPart.QuestionGroupId.Value,
            new Dictionary<int, string> { [0] = "Yes, proceed" });
        Assert.True(ok);

        var final = await completedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Turn finished after reconnect.", final.AssistantMessage);
        Assert.False(coordinator.IsConversationGenerating(handle.Conversation.Id));
    }

    [Fact]
    public async Task ConcurrentTurns_SerializedByTurnLock_AndActiveTurnDefersEvictionUntilIdle()
    {
        var turn1Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowTurn1Finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int activeCalls = 0;
        int maxConcurrent = 0;

        var client = new BlockingConcurrencyClient(async ct =>
        {
            int cur = Interlocked.Increment(ref activeCalls);
            int prev;
            do { prev = maxConcurrent; } while (cur > prev && Interlocked.CompareExchange(ref maxConcurrent, cur, prev) != prev);
            turn1Entered.TrySetResult(true);
            await allowTurn1Finish.Task.WaitAsync(ct);
            Interlocked.Decrement(ref activeCalls);
            return "serialized ok";
        });

        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(131072);
        gatewayMock.Setup(g => g.ReleaseSessionAffinityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = client,
            GatewayService = gatewayMock.Object,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(109);
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Serialization");

        var t1 = coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "First"));
        await turn1Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var t2 = coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Second"));
        Assert.False(t2.IsCompleted);

        // Request eviction while active without cancel -> must refuse and mark PendingEviction
        var evictedWhileActive = await coordinator.ReleaseConversationSessionAsync(ws.Id, handle.Conversation.Id, cancelIfActive: false);
        Assert.False(evictedWhileActive);
        gatewayMock.Verify(g => g.ReleaseSessionAffinityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        allowTurn1Finish.TrySetResult(true);
        await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, maxConcurrent);
        // After idle completion, pending eviction ran and released session affinity
        gatewayMock.Verify(g => g.ReleaseSessionAffinityAsync(LlmSessionIds.ForIdeation(ws.Id, handle.Conversation.Id), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    private sealed class BlockingConcurrencyClient : IChatClient
    {
        private readonly Func<CancellationToken, Task<string>> _fn;
        public BlockingConcurrencyClient(Func<CancellationToken, Task<string>> fn) => _fn = fn;
        public ChatClientMetadata Metadata => new("Blocking");

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            new(new ChatMessage(ChatRole.Assistant, await _fn(cancellationToken)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var text = await _fn(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task UpdateSessionOptionsAsync_InvalidatesWarmSetupAndTriggersNewPreload()
    {
        var profilesUsed = new List<(string? Model, OllamaRoutingTarget Target, string Instructions)>();
        var client = new MessageCapturingChatClient(msgs =>
        {
            var sys = msgs.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? string.Empty;
            profilesUsed.Add((null, OllamaRoutingTarget.Auto, sys));
        });

        var requestedProfiles = new List<AgentProfile?>();
        var chatFactoryMock = new Mock<ILlmChatClientFactory>();
        chatFactoryMock.Setup(f => f.CreateClient(It.IsAny<AgentProfile?>(), It.IsAny<string?>()))
            .Callback<AgentProfile?, string?>((p, _) => requestedProfiles.Add(p))
            .Returns(client);

        var preloads = new List<(string Model, string? Target)>();
        var preloadMock = new Mock<ILlmModelPreloadService>();
        preloadMock.Setup(p => p.PreloadModelAsync(It.IsAny<string>(), It.IsAny<ModelPreloadOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, ModelPreloadOptions?, CancellationToken>((m, o, _) => preloads.Add((m, o?.Target)))
            .ReturnsAsync((string m, ModelPreloadOptions? _, CancellationToken _) => new ModelPreloadResult(true, m));

        var tracker = new TrackingBackgroundTaskRunner();
        using var builder = new TestIdeationCoordinatorBuilder
        {
            ChatClientFactory = chatFactoryMock.Object,
            PreloadService = preloadMock.Object,
            BackgroundTaskRunner = tracker,
            PathResolver = TestPathResolver.Create()
        };
        var ws = new Workspace { Id = 110, Name = "Config Override", RepositoryPath = Directory.GetCurrentDirectory() };
        ws.AgentProfiles.Add(CreatePreloadProfile(ws.Id, target: OllamaRoutingTarget.Local));
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Options");
        await tracker.WaitForAllAsync();

        await coordinator.UpdateSessionOptionsAsync(
            ws.Id,
            handle.Conversation.Id,
            new IdeationSessionOptions(
                RoutingTarget: OllamaRoutingTarget.Cloud,
                ModelOverride: "cloud-arch:70b",
                CustomSystemPrompt: "Custom strict rules."));
        await tracker.WaitForAllAsync();

        Assert.Equal(2, requestedProfiles.Count);
        Assert.Equal("cloud-arch:70b", requestedProfiles[1]?.ModelName);
        Assert.Equal(OllamaRoutingTarget.Cloud, requestedProfiles[1]?.RoutingTarget);
        Assert.Contains(preloads, p => p.Model == "cloud-arch:70b" && p.Target == "Cloud");

        await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Hi"));
        Assert.Contains("Custom strict rules.", profilesUsed[0].Instructions);
    }

    [Fact]
    public async Task MarkWorkspaceContextDirty_PreservesSystemPromptDuringRefresh_AndInjectsDeltaAndHotspotsOnNextTurn()
    {
        var instructionsPerCall = new List<string>();
        var turnPrompts = new List<string>();
        int backlogCall = 0;

        var workspaceContextMock = new Mock<IAgentWorkspaceContextService>();
        workspaceContextMock.Setup(c => c.ResolveLocations(It.IsAny<Workspace>(), null, null))
            .Returns((Workspace ws, int? _, string? __) => new AgentWorkspaceLocations(ws.RepositoryPath, ws.RepositoryPath, ws.RepositoryPath));
        workspaceContextMock.Setup(c => c.BuildPromptContextAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IWorkspaceSkillsService>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentPromptContext(string.Empty, string.Empty, Array.Empty<string>()));
        workspaceContextMock.Setup(c => c.BuildBacklogDigestAsync(It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref backlogCall) == 1 ? "- Task #1" : "- Task #1\n- Task #2");
        workspaceContextMock.Setup(c => c.BuildWorkspaceAttachmentsDigestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("[]");
        workspaceContextMock.Setup(c => c.BuildFrictionHotspotsDigestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => backlogCall <= 1 ? string.Empty : "- src/Leaky.cs (5 failures)");

        var client = new MessageCapturingChatClient(msgs =>
        {
            var list = msgs.ToList();
            instructionsPerCall.Add(list.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? string.Empty);
            turnPrompts.Add(list.Last(m => m.Role == ChatRole.User).Text ?? string.Empty);
        })
        { ResponseToReturn = "Noted." };

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = client,
            WorkspaceContextService = workspaceContextMock.Object,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(111);
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Dirty Context");

        await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Turn 1"));
        coordinator.MarkWorkspaceContextDirty(ws.Id);

        // Simulated page refresh: WarmupSessionAsync MUST NOT rebuild system prompt / bust KV cache once turns exist
        await coordinator.WarmupSessionAsync(ws.Id, handle.Conversation.Id);
        await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Turn 2"));

        Assert.Equal(2, instructionsPerCall.Count);
        Assert.Equal(instructionsPerCall[0], instructionsPerCall[1]);
        Assert.Contains("<workspace_context_update>", turnPrompts[1]);
        Assert.Contains("Task #2", turnPrompts[1]);
        Assert.Contains("src/Leaky.cs", turnPrompts[1]);
    }

    [Fact]
    public async Task NonBlockingInitialBacklogSync_RunsInBackgroundWithoutBlockingTurnDispatch()
    {
        var syncStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSync = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var workspaceContextMock = new Mock<IAgentWorkspaceContextService>();
        workspaceContextMock.Setup(c => c.ResolveLocations(It.IsAny<Workspace>(), null, null))
            .Returns((Workspace ws, int? _, string? __) => new AgentWorkspaceLocations(ws.RepositoryPath, ws.RepositoryPath, ws.RepositoryPath));
        workspaceContextMock.Setup(c => c.SyncBacklogToDiskAsync(It.IsAny<Workspace>(), It.IsAny<AgentWorkspaceLocations>(), It.IsAny<CancellationToken>()))
            .Returns<Workspace, AgentWorkspaceLocations, CancellationToken>(async (_, _, ct) =>
            {
                syncStarted.TrySetResult(true);
                await releaseSync.Task.WaitAsync(ct);
            });
        workspaceContextMock.Setup(c => c.BuildPromptContextAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IWorkspaceSkillsService>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentPromptContext(string.Empty, string.Empty, Array.Empty<string>()));
        workspaceContextMock.Setup(c => c.BuildBacklogDigestAsync(It.IsAny<int>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        workspaceContextMock.Setup(c => c.BuildWorkspaceAttachmentsDigestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        workspaceContextMock.Setup(c => c.BuildFrictionHotspotsDigestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var tracker = new TrackingBackgroundTaskRunner();
        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = new FakeChatClient { ResponseToReturn = "Fast reply without waiting for disk sync." },
            WorkspaceContextService = workspaceContextMock.Object,
            BackgroundTaskRunner = tracker,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(112);
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "NonBlockingSync");

        var result = await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Quick query"))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Fast reply without waiting for disk sync.", result.AssistantMessage);
        await syncStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseSync.TrySetResult(true);
        await tracker.WaitForAllAsync();
    }

    private sealed class TrackingDisposableExecutionEnvironment : IExecutionEnvironment, IDisposable
    {
        public ExecutionTier Tier => ExecutionTier.BareHost;
        public bool IsDisposed { get; private set; }
        public Task<CommandResult> ExecuteAsync(
            string command,
            string? workingDirectory = null,
            int? timeoutSeconds = null,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero));

        public void Dispose() => IsDisposed = true;
    }

    [Fact]
    public async Task GlobalPause_IdleSweep_And_DisposeAsync_ReleaseResourcesWithoutDisposingExecutionEnvironment()
    {
        var fakeTime = new AdjustableTimeProvider(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        bool isPaused = false;
        Action<bool>? pauseChangedHandler = null;
        var opsMock = new Mock<IOperationsControlService>();
        opsMock.SetupGet(o => o.IsPaused).Returns(() => isPaused);
        opsMock.SetupAdd(o => o.OnOperationsStateChanged += It.IsAny<Action<bool>>())
            .Callback<Action<bool>>(h => pauseChangedHandler += h);
        opsMock.SetupRemove(o => o.OnOperationsStateChanged -= It.IsAny<Action<bool>>())
            .Callback<Action<bool>>(h => pauseChangedHandler -= h);

        var env = new TrackingDisposableExecutionEnvironment();
        var createdClients = new List<TrackingDisposableChatClient>();
        var chatFactoryMock = new Mock<ILlmChatClientFactory>();
        chatFactoryMock.Setup(f => f.CreateClient(It.IsAny<AgentProfile?>(), It.IsAny<string?>()))
            .Returns<AgentProfile?, string?>((p, s) =>
            {
                var c = new TrackingDisposableChatClient(p?.ModelName ?? "m", s);
                createdClients.Add(c);
                return c;
            });

        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(131072);
        gatewayMock.Setup(g => g.ReleaseSessionAffinityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var tracker = new TrackingBackgroundTaskRunner();
        using var builder = new TestIdeationCoordinatorBuilder
        {
            TimeProvider = fakeTime,
            OperationsControlService = opsMock.Object,
            EnvProvider = new SingleExecutionEnvironmentProvider(env),
            ChatClientFactory = chatFactoryMock.Object,
            GatewayService = gatewayMock.Object,
            BackgroundTaskRunner = tracker,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(113);
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();

        // 1. Idle timeout eviction after 30 minutes
        var idleConv = await coordinator.StartConversationAsync(ws.Id, "Idle Conv");
        await coordinator.WarmupSessionAsync(ws.Id, idleConv.Conversation.Id);
        Assert.Single(createdClients);

        fakeTime.Advance(TimeSpan.FromMinutes(31));
        await coordinator.EvictIdleSessionsAsync();
        Assert.True(createdClients[0].IsDisposed);
        gatewayMock.Verify(g => g.ReleaseSessionAffinityAsync(LlmSessionIds.ForIdeation(ws.Id, idleConv.Conversation.Id), It.IsAny<CancellationToken>()), Times.Once);

        // 2. Global pause cancels/evicts warm sessions
        var pauseConv = await coordinator.StartConversationAsync(ws.Id, "Pause Conv");
        await coordinator.WarmupSessionAsync(ws.Id, pauseConv.Conversation.Id);
        Assert.Equal(2, createdClients.Count);
        Assert.False(createdClients[1].IsDisposed);

        isPaused = true;
        pauseChangedHandler?.Invoke(true);
        await tracker.WaitForAllAsync();
        Assert.True(createdClients[1].IsDisposed);

        // 3. Coordinator DisposeAsync disposes active clients cleanly and NEVER disposes singleton IExecutionEnvironment
        isPaused = false;
        var finalConv = await coordinator.StartConversationAsync(ws.Id, "Dispose Conv");
        await coordinator.WarmupSessionAsync(ws.Id, finalConv.Conversation.Id);
        await coordinator.DisposeAsync();
        Assert.True(createdClients[2].IsDisposed);
        Assert.False(env.IsDisposed);
    }

    [Fact]
    public async Task StartTurnAsync_WhenTurnAlreadyActive_ReturnsFalseWithoutCorruptingInFlightTurn()
    {
        var turn1Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowTurn1Finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = new BlockingConcurrencyClient(async ct =>
        {
            turn1Entered.TrySetResult(true);
            await allowTurn1Finish.Task.WaitAsync(ct);
            return "turn 1 completed";
        });

        var tracker = new TrackingBackgroundTaskRunner();
        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = client,
            BackgroundTaskRunner = tracker,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(114);
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Concurrent StartTurn");

        var completedTcs = new TaskCompletionSource<IdeationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = coordinator.Subscribe(handle.Conversation.Id, u =>
        {
            if (u.Kind == IdeationStreamUpdateKind.Completed && u.FinalResult != null)
            {
                completedTcs.TrySetResult(u.FinalResult);
            }
        });

        Assert.True(await coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "First prompt")));
        await turn1Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Second concurrent StartTurnAsync must be rejected without wiping the live in-flight turn state
        Assert.False(await coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Second prompt")));

        Assert.True(coordinator.IsConversationGenerating(handle.Conversation.Id));
        var snapshot = coordinator.GetInFlightSnapshot(handle.Conversation.Id);
        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsGenerating);

        allowTurn1Finish.TrySetResult(true);
        var result = await completedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await tracker.WaitForAllAsync();

        Assert.Equal("turn 1 completed", result.AssistantMessage);
        Assert.False(coordinator.IsConversationGenerating(handle.Conversation.Id));
    }

    private sealed class BlockingTrackingDisposableChatClient : IChatClient
    {
        private readonly Func<CancellationToken, Task<string>> _streamFn;
        public BlockingTrackingDisposableChatClient(string modelName, Func<CancellationToken, Task<string>> streamFn)
        {
            ModelName = modelName;
            _streamFn = streamFn;
        }

        public string ModelName { get; }
        public bool IsDisposed { get; private set; }
        public ChatClientMetadata Metadata => new("BlockingTracking");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var text = await _streamFn(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => IsDisposed = true;
    }

    [Fact]
    public async Task PassiveLoadAndGetOrCreate_PreservesWarmSessionOptions_AndNeverDisposesActiveClientMidStream()
    {
        var tempTranscripts = Path.Combine(Path.GetTempPath(), "ncf-passive-load-" + Guid.NewGuid().ToString("N"));
        try
        {
            var streamStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStream = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var createdClients = new List<BlockingTrackingDisposableChatClient>();

            var chatFactoryMock = new Mock<ILlmChatClientFactory>();
            chatFactoryMock.Setup(f => f.CreateClient(It.IsAny<AgentProfile?>(), It.IsAny<string?>()))
                .Returns<AgentProfile?, string?>((p, _) =>
                {
                    var client = new BlockingTrackingDisposableChatClient(
                        p?.ModelName ?? "default",
                        async ct =>
                        {
                            streamStarted.TrySetResult(true);
                            await releaseStream.Task.WaitAsync(ct);
                            return $"reply:{p?.ModelName}";
                        });
                    createdClients.Add(client);
                    return client;
                });

            var tracker = new TrackingBackgroundTaskRunner();
            using var builder = new TestIdeationCoordinatorBuilder
            {
                ChatClientFactory = chatFactoryMock.Object,
                TranscriptService = new SessionTranscriptService(tempTranscripts),
                BackgroundTaskRunner = tracker,
                PathResolver = TestPathResolver.Create()
            };
            var ws = CreateWorkspace(115);
            await builder.SeedWorkspaceAsync(ws);
            var coordinator = builder.Build();

            var customOptions = new IdeationSessionOptions(
                OllamaRoutingTarget.Cloud,
                "qwen3:32b",
                "Strict");
            var createdHandle = await coordinator.StartConversationAsync(ws.Id, "Warm Options", options: customOptions);
            await coordinator.WarmupSessionAsync(ws.Id, createdHandle.Conversation.Id, customOptions);

            var completedTcs = new TaskCompletionSource<IdeationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = coordinator.Subscribe(createdHandle.Conversation.Id, u =>
            {
                if (u.Kind == IdeationStreamUpdateKind.Completed && u.FinalResult != null)
                {
                    completedTcs.TrySetResult(u.FinalResult);
                }
            });

            Assert.True(await coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, createdHandle.Conversation.Id, "Inspect", customOptions)));
            await streamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Passive load / attach with null options + config change mid-turn must NOT overwrite options or dispose active client mid-stream
            var reloadedMidTurn = await coordinator.LoadConversationAsync(ws.Id, createdHandle.Conversation.Id, options: null);
            var activeMidTurn = await coordinator.GetOrCreateActiveConversationAsync(ws.Id, options: null);
            await coordinator.UpdateSessionOptionsAsync(
                ws.Id,
                createdHandle.Conversation.Id,
                new IdeationSessionOptions(OllamaRoutingTarget.Local, "qwen3:14b", "Updated"));
            await coordinator.WarmupSessionAsync(ws.Id, createdHandle.Conversation.Id, options: null);

            Assert.Single(createdClients);
            Assert.False(createdClients[0].IsDisposed);
            Assert.NotNull(reloadedMidTurn?.SessionOptions);
            Assert.Equal(OllamaRoutingTarget.Cloud, reloadedMidTurn.SessionOptions.RoutingTarget);
            Assert.Equal("qwen3:32b", reloadedMidTurn.SessionOptions.ModelOverride);
            Assert.Equal("Strict", reloadedMidTurn.SessionOptions.CustomSystemPrompt);
            Assert.Equal(OllamaRoutingTarget.Cloud, activeMidTurn.SessionOptions?.RoutingTarget);

            releaseStream.TrySetResult(true);
            await completedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await tracker.WaitForAllAsync();
            Assert.False(createdClients[0].IsDisposed);

            // After completion, passive LoadConversationAsync exposes persisted LatestTranscriptPath & LatestFullTranscriptMarkdown
            var loadedAfter = await coordinator.LoadConversationAsync(ws.Id, createdHandle.Conversation.Id, options: null);
            Assert.NotNull(loadedAfter);
            Assert.False(string.IsNullOrWhiteSpace(loadedAfter.LatestTranscriptPath));
            Assert.False(string.IsNullOrWhiteSpace(loadedAfter.LatestFullTranscriptMarkdown));
            Assert.Equal("qwen3:14b", loadedAfter.SessionOptions?.ModelOverride);
        }
        finally
        {
            if (Directory.Exists(tempTranscripts))
            {
                try
                {
                    Directory.Delete(tempTranscripts, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort temp cleanup
                }
            }
        }
    }

    [Fact]
    public async Task CancelActiveTurnAsync_WhileWaitingOnAsk_SanitizesUnansweredAskFromDb_AndKeepsChatHistoryAlignedForNextTurn()
    {
        var seenHistories = new List<List<ChatMessage>>();
        int step = 0;

        var client = new CapturingAskStreamingClient((msgs, _) =>
        {
            seenHistories.Add(msgs.ToList());
            step++;
            if (step == 1)
            {
                return
                [
                    new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("Thinking before ask...")]),
                    new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("ask-cancel-1", CanonicalToolNames.Ask, new Dictionary<string, object?> { ["prompt"] = "Proceed?" })])
                ];
            }

            return [new ChatResponseUpdate(ChatRole.Assistant, "Follow-up reply after cancel.")];
        });

        var tracker = new TrackingBackgroundTaskRunner();
        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = client,
            BackgroundTaskRunner = tracker,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(116);
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Cancel Ask");

        var askedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledTcs = new TaskCompletionSource<IdeationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = coordinator.Subscribe(handle.Conversation.Id, u =>
        {
            if (u.Kind == IdeationStreamUpdateKind.QuestionAsked)
            {
                askedTcs.TrySetResult(true);
            }
            else if (u.Kind == IdeationStreamUpdateKind.Completed && u.FinalResult?.IsCancelled == true)
            {
                cancelledTcs.TrySetResult(u.FinalResult);
            }
        });

        Assert.True(await coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "First question")));
        await askedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.CancelActiveTurnAsync(handle.Conversation.Id);
        var cancelResult = await cancelledTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await tracker.WaitForAllAsync();

        Assert.True(cancelResult.IsCancelled);
        Assert.Contains("Generation stopped by user", cancelResult.AssistantMessage);

        // Verify DB turns have no unanswered Tool(ask) turn and serialize cleanly without InterruptedToolResult
        var convAfterCancel = await builder.TaskRepository.GetConversationWithTurnsAsync(handle.Conversation.Id);
        Assert.NotNull(convAfterCancel);
        Assert.DoesNotContain(convAfterCancel.Turns, t => t.Role == StageTurnRole.Tool && t.ToolName == CanonicalToolNames.Ask);
        Assert.Contains(convAfterCancel.Turns, t => t.Role == StageTurnRole.Assistant && t.Content!.Contains("Generation stopped by user"));

        var coldSerialized = ConversationHistorySerializer.Serialize(convAfterCancel.Turns);
        Assert.DoesNotContain(ConversationHistorySerializer.InterruptedToolResult, coldSerialized);

        // Next turn must see strictly alternating User -> Assistant -> User in memory
        var nextResult = await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Follow up"));
        Assert.Equal("Follow-up reply after cancel.", nextResult.AssistantMessage);

        Assert.Equal(2, seenHistories.Count);
        var turn2NonSystemRoles = seenHistories[1].Where(m => m.Role != ChatRole.System).Select(m => m.Role).ToList();
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User], turn2NonSystemRoles);
        Assert.Contains("Generation stopped by user", seenHistories[1].First(m => m.Role == ChatRole.Assistant).Text);
    }

    private sealed class SequenceConditionalStreamingClient : IChatClient
    {
        private readonly Func<int, IEnumerable<ChatMessage>, string> _handler;
        private int _callNumber;

        public SequenceConditionalStreamingClient(Func<int, IEnumerable<ChatMessage>, string> handler) => _handler = handler;

        public ChatClientMetadata Metadata => new("SequenceConditional");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _handler(Interlocked.Increment(ref _callNumber), chatMessages))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callNumber);
            var text = _handler(call, chatMessages);
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task SendMessageAsync_WhenLlmThrowsError_RollsBackFailedUserTurnAndPreservesDirtyContextDeltaForRetry()
    {
        int backlogCall = 0;
        var workspaceContextMock = new Mock<IAgentWorkspaceContextService>();
        workspaceContextMock.Setup(c => c.ResolveLocations(It.IsAny<Workspace>(), null, null))
            .Returns((Workspace ws, int? _, string? __) => new AgentWorkspaceLocations(ws.RepositoryPath, ws.RepositoryPath, ws.RepositoryPath));
        workspaceContextMock.Setup(c => c.BuildPromptContextAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IWorkspaceSkillsService>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentPromptContext(string.Empty, string.Empty, Array.Empty<string>()));
        workspaceContextMock.Setup(c => c.BuildBacklogDigestAsync(It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref backlogCall) == 1 ? "- Task #1" : "- Task #1\n- Task #2");
        workspaceContextMock.Setup(c => c.BuildWorkspaceAttachmentsDigestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        workspaceContextMock.Setup(c => c.BuildFrictionHotspotsDigestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        List<ChatMessage>? retryCaptured = null;
        var client = new SequenceConditionalStreamingClient((call, msgs) =>
        {
            if (call == 1) return "Turn 1 success";
            if (call == 2) throw new HttpRequestException("Simulated upstream 500");
            retryCaptured = msgs.ToList();
            return "Turn 3 retry success";
        });

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = client,
            WorkspaceContextService = workspaceContextMock.Object,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(117);
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Error Rollback");

        await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Turn 1"));
        coordinator.MarkWorkspaceContextDirty(ws.Id);

        var errors = new List<IdeationStreamUpdate>();
        using var sub = coordinator.Subscribe(handle.Conversation.Id, u =>
        {
            if (u.Kind == IdeationStreamUpdateKind.Error) errors.Add(u);
        });

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Failed Turn 2")));

        var errEvt = Assert.Single(errors);
        Assert.Contains("Simulated upstream 500", errEvt.ErrorMessage);

        var convAfterError = await builder.TaskRepository.GetConversationWithTurnsAsync(handle.Conversation.Id);
        Assert.NotNull(convAfterError);
        Assert.DoesNotContain(convAfterError.Turns, t => t.Content?.Contains("Failed Turn 2") == true);

        // Retry Turn 2 -> must NOT have consecutive duplicate user prompts and MUST still include <workspace_context_update>
        var retryResult = await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Retry Turn 2"));
        Assert.Equal("Turn 3 retry success", retryResult.AssistantMessage);

        Assert.NotNull(retryCaptured);
        var nonSystemRoles = retryCaptured.Where(m => m.Role != ChatRole.System).Select(m => m.Role).ToList();
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User], nonSystemRoles);
        Assert.DoesNotContain(retryCaptured, m => m.Text?.Contains("Failed Turn 2") == true);
        Assert.Contains("<workspace_context_update>", retryCaptured.Last().Text);
        Assert.Contains("Task #2", retryCaptured.Last().Text);
    }

    [Fact]
    public async Task WorkspaceActivityWatcher_WhenBacklogMutationOrTaskSuspendedNoticeArrives_MarksWorkspaceContextDirty()
    {
        var innerRepo = new InMemoryTaskRepository();
        var ws = CreateWorkspace(118);
        var task = await innerRepo.CreateWithBaselineAsync(
            new ForgeTask { WorkspaceId = ws.Id, Title = "Edited Externally", Status = TaskOverallStatus.Suggested },
            null,
            AuditActors.User,
            "Init");

        var fenceSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repoMock = new Mock<ITaskRepository>();
        repoMock.Setup(r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>((id, ct) =>
            {
                if (id == 999_999)
                {
                    fenceSignal.TrySetResult(true);
                }
                return innerRepo.GetByIdAsync(id, ct);
            });
        repoMock.Setup(r => r.StartIdeationConversationAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<int, string?, string?, string?, CancellationToken>((w, t, m, p, ct) => innerRepo.StartIdeationConversationAsync(w, t, m, p, ct));
        repoMock.Setup(r => r.GetConversationWithTurnsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>((id, ct) => innerRepo.GetConversationWithTurnsAsync(id, ct));
        repoMock.Setup(r => r.AddConversationTurnAsync(
                It.IsAny<int>(), It.IsAny<StageTurnRole>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<long?>(),
                It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<int, StageTurnRole, string?, string?, string?, string?, string?, long?, bool, string?, CancellationToken>(
                (c, r, cnt, tr, tn, ta, trs, dur, err, meta, ct) =>
                    innerRepo.AddConversationTurnAsync(c, r, cnt, tr, tn, ta, trs, dur, err, meta, ct));
        repoMock.Setup(r => r.UpdateConversationUsageAsync(It.IsAny<int>(), It.IsAny<SessionUsageDelta>(), It.IsAny<CancellationToken>()))
            .Returns<int, SessionUsageDelta, CancellationToken>((id, d, ct) => innerRepo.UpdateConversationUsageAsync(id, d, ct));

        int backlogCall = 0;
        var workspaceContextMock = new Mock<IAgentWorkspaceContextService>();
        workspaceContextMock.Setup(c => c.ResolveLocations(It.IsAny<Workspace>(), null, null))
            .Returns((Workspace w, int? _, string? __) => new AgentWorkspaceLocations(w.RepositoryPath, w.RepositoryPath, w.RepositoryPath));
        workspaceContextMock.Setup(c => c.BuildPromptContextAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IWorkspaceSkillsService>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentPromptContext(string.Empty, string.Empty, Array.Empty<string>()));
        workspaceContextMock.Setup(c => c.BuildBacklogDigestAsync(It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref backlogCall) == 1 ? "- Task #1" : $"- Task #{task.Id} Edited Externally");
        workspaceContextMock.Setup(c => c.BuildWorkspaceAttachmentsDigestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        workspaceContextMock.Setup(c => c.BuildFrictionHotspotsDigestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        var turnPrompts = new List<string>();
        var client = new MessageCapturingChatClient(msgs =>
            turnPrompts.Add(msgs.Last(m => m.Role == ChatRole.User).Text ?? string.Empty))
        {
            ResponseToReturn = "Noted."
        };

        var streamer = new InMemoryAgentActivityStreamer();
        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = client,
            TaskRepository = repoMock.Object,
            ActivityStreamer = streamer,
            WorkspaceContextService = workspaceContextMock.Object,
            PathResolver = TestPathResolver.Create()
        };
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Watcher Test");

        SpinWait.SpinUntil(() => streamer.ActiveSubscriptionsCount >= 1, TimeSpan.FromSeconds(5));
        await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Turn 1"));

        // Broadcast Notice with BacklogMutation tool metadata and follow with a TaskSuspended notice fence
        await streamer.BroadcastAsync(new AgentActivityEvent(
            "external-session",
            task.Id,
            StandardStageIds.Triage,
            AgentActivityKind.Notice,
            "Edited task",
            DateTime.UtcNow,
            new Dictionary<string, object?> { [AgentActivityMetadataKeys.ToolName] = CanonicalToolNames.EditTask }));

        await streamer.BroadcastAsync(new AgentActivityEvent(
            "external-session",
            999_999,
            StandardStageIds.TaskReview,
            AgentActivityKind.Notice,
            "Suspended",
            DateTime.UtcNow,
            new Dictionary<string, object?> { [AgentActivityMetadataKeys.ActionType] = AgentActivityConstants.ActionTypeTaskSuspended }));

        await fenceSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Turn 2"));

        Assert.Equal(2, turnPrompts.Count);
        Assert.Contains("<workspace_context_update>", turnPrompts[1]);
        Assert.Contains("Edited Externally", turnPrompts[1]);
    }

    [Fact]
    public async Task DisposeAsync_WhileTurnIsUnwinding_WaitsForLocksWithoutThrowingObjectDisposedException()
    {
        var llmEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new BlockingTrackingDisposableChatClient(
            "dispose-test",
            async ct =>
            {
                llmEntered.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    await Task.Delay(50);
                    throw;
                }
                return "never";
            });

        var logger = new TestLogger<IdeationConversationCoordinator>();
        var tracker = new TrackingBackgroundTaskRunner();
        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = client,
            Logger = logger,
            BackgroundTaskRunner = tracker,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(119);
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Dispose Unwind");

        Assert.True(await coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Long work")));
        await llmEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.DisposeAsync();
        await tracker.WaitForAllAsync();

        Assert.True(client.IsDisposed);
        Assert.DoesNotContain(logger.Logs, l => l.Message.Contains("ObjectDisposedException", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeOperationsControlService : IOperationsControlService
    {
        public bool IsPaused { get; private set; }
        public OperationsReclaimStatus ReclaimStatus => OperationsReclaimStatus.Idle;
        public string? LastReclaimError => null;
        public event Action<bool>? OnOperationsStateChanged;

        public void SetPaused(bool paused)
        {
            IsPaused = paused;
            OnOperationsStateChanged?.Invoke(paused);
        }

        public Task<bool> PauseOperationsAsync(string reason = "User initiated pause", CancellationToken cancellationToken = default)
        {
            SetPaused(true);
            return Task.FromResult(true);
        }

        public Task<bool> ResumeOperationsAsync(CancellationToken cancellationToken = default)
        {
            SetPaused(false);
            return Task.FromResult(true);
        }

        public Task<int> GetDeferredTaskCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    [Fact]
    public async Task ConcurrentSubscribeAndWarmup_NeverDeadlocks_AndLiveSubscriberReceivesTurnUpdates()
    {
        var tracker = new TrackingBackgroundTaskRunner();
        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = new FakeChatClient { ResponseToReturn = "Concurrent OK" },
            BackgroundTaskRunner = tracker,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(120);
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Stress Sub");

        var updates = new ConcurrentQueue<IdeationStreamUpdate>();
        var completedTcs = new TaskCompletionSource<IdeationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var persistentSub = coordinator.Subscribe(handle.Conversation.Id, u =>
        {
            updates.Enqueue(u);
            if (u.Kind == IdeationStreamUpdateKind.Completed && u.FinalResult != null)
            {
                completedTcs.TrySetResult(u.FinalResult);
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var workers = Enumerable.Range(0, 12).Select(idx => Task.Run(async () =>
        {
            for (int iter = 0; iter < 8; iter++)
            {
                if ((idx + iter) % 3 == 0)
                {
                    await coordinator.GetOrCreateActiveConversationAsync(ws.Id, cancellationToken: cts.Token);
                }
                else if ((idx + iter) % 3 == 1)
                {
                    await coordinator.WarmupSessionAsync(
                        ws.Id,
                        handle.Conversation.Id,
                        new IdeationSessionOptions(OllamaRoutingTarget.Auto, null, $"Prompt-{idx % 2}"),
                        cts.Token);
                }
                else
                {
                    using var tempSub = coordinator.Subscribe(handle.Conversation.Id, _ => { });
                    await Task.Yield();
                }
            }
        }, cts.Token));

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
        await tracker.WaitForAllAsync();

        var result = await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Ping"));
        var streamedResult = await completedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Concurrent OK", result.AssistantMessage);
        Assert.Equal("Concurrent OK", streamedResult.AssistantMessage);
        Assert.Contains(updates, u => u.Kind == IdeationStreamUpdateKind.Completed);
    }

    [Fact]
    public async Task OperationsPause_WithOpenSubscriber_EvictsIdleAndActiveSessions_AndExistingSubscriberResumesAfterUnpause()
    {
        var ops = new FakeOperationsControlService();
        int turnNumber = 0;
        var turn3Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var createdClients = new List<BlockingTrackingDisposableChatClient>();

        var chatFactoryMock = new Mock<ILlmChatClientFactory>();
        chatFactoryMock.Setup(f => f.CreateClient(It.IsAny<AgentProfile?>(), It.IsAny<string?>()))
            .Returns<AgentProfile?, string?>((p, _) =>
            {
                var client = new BlockingTrackingDisposableChatClient(
                    p?.ModelName ?? "default",
                    async ct =>
                    {
                        int n = Interlocked.Increment(ref turnNumber);
                        if (n == 3)
                        {
                            turn3Started.TrySetResult(true);
                            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                        }
                        return $"reply-{n}";
                    });
                createdClients.Add(client);
                return client;
            });

        int affinityReleased = 0;
        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(131072);
        gatewayMock.Setup(g => g.ReleaseSessionAffinityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref affinityReleased))
            .ReturnsAsync(true);

        var tracker = new TrackingBackgroundTaskRunner();
        using var builder = new TestIdeationCoordinatorBuilder
        {
            ChatClientFactory = chatFactoryMock.Object,
            GatewayService = gatewayMock.Object,
            OperationsControlService = ops,
            BackgroundTaskRunner = tracker,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(121);
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Pause Evict");

        var completions = new ConcurrentQueue<IdeationResult>();
        var turn2Done = new TaskCompletionSource<IdeationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn3Done = new TaskCompletionSource<IdeationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn4Done = new TaskCompletionSource<IdeationResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Leave subscriber open across both pause/unpause cycles
        using var sub = coordinator.Subscribe(handle.Conversation.Id, u =>
        {
            if (u.Kind == IdeationStreamUpdateKind.Completed && u.FinalResult != null)
            {
                completions.Enqueue(u.FinalResult);
                if (u.FinalResult.AssistantMessage == "reply-2") turn2Done.TrySetResult(u.FinalResult);
                else if (u.FinalResult.IsCancelled) turn3Done.TrySetResult(u.FinalResult);
                else if (u.FinalResult.AssistantMessage == "reply-4") turn4Done.TrySetResult(u.FinalResult);
            }
        });

        // Turn 1 completes while subscriber remains open
        var r1 = await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Turn 1"));
        Assert.Equal("reply-1", r1.AssistantMessage);
        Assert.Single(createdClients);
        Assert.False(createdClients[0].IsDisposed);

        // Pause while session is IDLE and subscriber is OPEN -> must evict client & release router affinity
        ops.SetPaused(true);
        await tracker.WaitForAllAsync();

        Assert.True(createdClients[0].IsDisposed);
        Assert.True(Volatile.Read(ref affinityReleased) >= 1);

        // Unpause and run Turn 2 -> EXISTING subscriber must receive Turn 2 updates without re-subscribing
        ops.SetPaused(false);
        var r2 = await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Turn 2"));
        var streamed2 = await turn2Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("reply-2", r2.AssistantMessage);
        Assert.Equal("reply-2", streamed2.AssistantMessage);
        Assert.Equal(2, createdClients.Count);
        Assert.False(createdClients[1].IsDisposed);

        // Start Turn 3 (blocks), then pause mid-turn while subscriber is STILL OPEN
        int releasesBeforeTurn3Pause = Volatile.Read(ref affinityReleased);
        Assert.True(await coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Turn 3")));
        await turn3Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        ops.SetPaused(true);
        var streamed3 = await turn3Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await tracker.WaitForAllAsync();

        Assert.True(streamed3.IsCancelled);
        Assert.True(createdClients[1].IsDisposed);
        Assert.True(Volatile.Read(ref affinityReleased) > releasesBeforeTurn3Pause);

        // Unpause again and verify Turn 4 still delivers to the very same subscriber
        ops.SetPaused(false);
        var r4 = await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Turn 4"));
        var streamed4 = await turn4Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("reply-4", r4.AssistantMessage);
        Assert.Equal("reply-4", streamed4.AssistantMessage);
    }

    [Fact]
    public async Task CancelActiveTurnAsync_AndReleaseWithCancel_DuringStartupBeforeActiveTurnCtsAssigned_CancelsAndDrainsCleanly()
    {
        var innerRepo = new InMemoryTaskRepository();
        var persistEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePersist = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool gatePersistUserTurn = false;

        var repoMock = new Mock<ITaskRepository>();
        repoMock.Setup(r => r.StartIdeationConversationAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<int, string?, string?, string?, CancellationToken>((w, t, m, p, ct) => innerRepo.StartIdeationConversationAsync(w, t, m, p, ct));
        repoMock.Setup(r => r.GetConversationWithTurnsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>((id, ct) => innerRepo.GetConversationWithTurnsAsync(id, ct));
        repoMock.Setup(r => r.UpdateConversationUsageAsync(It.IsAny<int>(), It.IsAny<SessionUsageDelta>(), It.IsAny<CancellationToken>()))
            .Returns<int, SessionUsageDelta, CancellationToken>((id, d, ct) => innerRepo.UpdateConversationUsageAsync(id, d, ct));
        repoMock.Setup(r => r.SanitizeTrailingInterruptedTurnsAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns<int, bool, int?, CancellationToken>((id, ask, u, ct) => innerRepo.SanitizeTrailingInterruptedTurnsAsync(id, ask, u, ct));
        repoMock.Setup(r => r.AddConversationTurnAsync(
                It.IsAny<int>(), It.IsAny<StageTurnRole>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<long?>(),
                It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<int, StageTurnRole, string?, string?, string?, string?, string?, long?, bool, string?, CancellationToken>(
                async (c, r, cnt, tr, tn, ta, trs, dur, err, meta, ct) =>
                {
                    if (gatePersistUserTurn && r == StageTurnRole.User)
                    {
                        gatePersistUserTurn = false;
                        persistEntered.TrySetResult(true);
                        await releasePersist.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    return await innerRepo.AddConversationTurnAsync(c, r, cnt, tr, tn, ta, trs, dur, err, meta, ct);
                });

        int llmCallCount = 0;
        var client = new MessageCapturingChatClient(_ => Interlocked.Increment(ref llmCallCount))
        {
            ResponseToReturn = "Should not run"
        };

        var tracker = new TrackingBackgroundTaskRunner();
        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = client,
            TaskRepository = repoMock.Object,
            BackgroundTaskRunner = tracker,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(122);
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();

        // Part A: CancelActiveTurnAsync during StartTurnAsync before ActiveTurnCts exists
        var convA = (await coordinator.StartConversationAsync(ws.Id, "Cancel Startup A")).Conversation;
        var cancelledTcsA = new TaskCompletionSource<IdeationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subA = coordinator.Subscribe(convA.Id, u =>
        {
            if (u.Kind == IdeationStreamUpdateKind.Completed && u.FinalResult != null)
            {
                cancelledTcsA.TrySetResult(u.FinalResult);
            }
        });

        gatePersistUserTurn = true;
        var startTaskA = coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, convA.Id, "Startup prompt A"));
        await persistEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.CancelActiveTurnAsync(convA.Id);
        releasePersist.TrySetResult(true);

        Assert.True(await startTaskA);
        var resA = await cancelledTcsA.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await tracker.WaitForAllAsync();
        Assert.True(resA.IsCancelled);
        Assert.Equal(0, Volatile.Read(ref llmCallCount));

        // Part B: ReleaseConversationSessionAsync(cancelIfActive: true) while startup holds TurnLock (WaitingOrActiveTurns > 0, !IsGenerating)
        persistEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        releasePersist = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var convB = (await coordinator.StartConversationAsync(ws.Id, "Release Startup B")).Conversation;
        gatePersistUserTurn = true;
        var startTaskB = coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, convB.Id, "Startup prompt B"));
        await persistEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var releaseTask = coordinator.ReleaseConversationSessionAsync(ws.Id, convB.Id, cancelIfActive: true);
        Assert.False(releaseTask.IsCompleted);

        releasePersist.TrySetResult(true);
        Assert.True(await startTaskB);
        Assert.True(await releaseTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await tracker.WaitForAllAsync();
        Assert.False(coordinator.IsConversationGenerating(convB.Id));
        Assert.Equal(0, Volatile.Read(ref llmCallCount));
    }

    [Fact]
    public async Task WarmMultiTurn_WithRealActivityStreamer_DoesNotSelfDirtyDigestsOnOwnStageCompleted()
    {
        int backlogCalls = 0;
        int attachmentCalls = 0;
        int hotspotCalls = 0;

        var workspaceContextMock = new Mock<IAgentWorkspaceContextService>();
        workspaceContextMock.Setup(c => c.ResolveLocations(It.IsAny<Workspace>(), null, null))
            .Returns((Workspace w, int? _, string? __) => new AgentWorkspaceLocations(w.RepositoryPath, w.RepositoryPath, w.RepositoryPath));
        workspaceContextMock.Setup(c => c.BuildPromptContextAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IWorkspaceSkillsService>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentPromptContext(string.Empty, string.Empty, Array.Empty<string>()));
        workspaceContextMock.Setup(c => c.BuildBacklogDigestAsync(It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { Interlocked.Increment(ref backlogCalls); return "- Task #1"; });
        workspaceContextMock.Setup(c => c.BuildWorkspaceAttachmentsDigestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { Interlocked.Increment(ref attachmentCalls); return "- doc.md"; });
        workspaceContextMock.Setup(c => c.BuildFrictionHotspotsDigestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { Interlocked.Increment(ref hotspotCalls); return "- Program.cs"; });

        var streamer = new InMemoryAgentActivityStreamer();
        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = new FakeChatClient { ResponseToReturn = "Read-only observation." },
            ActivityStreamer = streamer,
            WorkspaceContextService = workspaceContextMock.Object,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(123);
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Self Dirty Check");

        SpinWait.SpinUntil(() => streamer.ActiveSubscriptionsCount >= 1, TimeSpan.FromSeconds(5));

        await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Turn 1"));

        // Publish a fence event on a non-ideation channel and allow watcher loop to drain Turn 1's StageCompleted event
        await streamer.BroadcastAsync(new AgentActivityEvent(
            "fence",
            0,
            StandardStageIds.TaskReview,
            AgentActivityKind.ThinkingDelta,
            "fence",
            DateTime.UtcNow));
        await Task.Delay(50);

        await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Turn 2"));

        Assert.Equal(1, Volatile.Read(ref backlogCalls));
        Assert.Equal(1, Volatile.Read(ref attachmentCalls));
        Assert.Equal(1, Volatile.Read(ref hotspotCalls));
    }

    [Fact]
    public async Task SubmitQuestionAnswersAsync_DuplicateSubmission_ReturnsFalseAndPreservesFirstAnswer()
    {
        var secondModelCallGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int step = 0;

        var client = new CapturingAskStreamingClient((_, _) =>
        {
            step++;
            if (step == 1)
            {
                return
                [
                    new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("ask-dup-1", CanonicalToolNames.Ask, new Dictionary<string, object?> { ["prompt"] = "Pick stack?" })])
                ];
            }

            secondModelCallGate.Task.GetAwaiter().GetResult();
            return [new ChatResponseUpdate(ChatRole.Assistant, "Recorded stack.")];
        });

        var tracker = new TrackingBackgroundTaskRunner();
        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = client,
            BackgroundTaskRunner = tracker,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(124);
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Duplicate Submit");

        var askedTcs = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedTcs = new TaskCompletionSource<IdeationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = coordinator.Subscribe(handle.Conversation.Id, u =>
        {
            if (u.Kind == IdeationStreamUpdateKind.QuestionAsked && u.QuestionGroupId.HasValue)
            {
                askedTcs.TrySetResult(u.QuestionGroupId.Value);
            }
            else if (u.Kind == IdeationStreamUpdateKind.Completed && u.FinalResult != null)
            {
                completedTcs.TrySetResult(u.FinalResult);
            }
        });

        Assert.True(await coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Configure")));
        var groupId = await askedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        const string firstMeta = "{\"Answers\":[{\"QuestionIndex\":0,\"AnswerText\":\"First Choice\"}]}";
        const string secondMeta = "{\"Answers\":[{\"QuestionIndex\":0,\"AnswerText\":\"Second Overwrite\"}]}";

        var firstAccepted = await coordinator.SubmitQuestionAnswersAsync(
            handle.Conversation.Id,
            groupId,
            new Dictionary<int, string> { [0] = "First Choice" },
            formattedAnswer: "First Choice",
            metadataJson: firstMeta);
        var secondAccepted = await coordinator.SubmitQuestionAnswersAsync(
            handle.Conversation.Id,
            groupId,
            new Dictionary<int, string> { [0] = "Second Overwrite" },
            formattedAnswer: "Second Overwrite",
            metadataJson: secondMeta);

        Assert.True(firstAccepted);
        Assert.False(secondAccepted);

        var snapshot = coordinator.GetInFlightSnapshot(handle.Conversation.Id);
        Assert.NotNull(snapshot);
        var qPart = Assert.Single(snapshot.CurrentParts, p => p.Kind == IdeationInFlightPartKind.Question);
        Assert.Equal("First Choice", qPart.QuestionAnswer);
        Assert.Equal(firstMeta, qPart.QuestionMetadataJson);

        secondModelCallGate.TrySetResult(true);
        var final = await completedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await tracker.WaitForAllAsync();
        Assert.Equal("Recorded stack.", final.AssistantMessage);

        var conv = await builder.TaskRepository.GetConversationWithTurnsAsync(handle.Conversation.Id);
        var toolResultTurn = Assert.Single(conv!.Turns, t => t.Role == StageTurnRole.ToolResult && t.ToolName == CanonicalToolNames.Ask);
        Assert.Contains("First Choice", toolResultTurn.ToolResult);
        Assert.DoesNotContain("Second Overwrite", toolResultTurn.ToolResult);
        Assert.Contains("ask-dup-1", toolResultTurn.MetadataJson);
        Assert.Contains("First Choice", toolResultTurn.MetadataJson);
        Assert.DoesNotContain("Second Overwrite", toolResultTurn.MetadataJson);
    }

    [Fact]
    public async Task ColdTurnBackgroundPreload_UsesCoordinatorDisposeToken_AndRetriesAfterOperationsPausedException()
    {
        var preloadEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPreloadFinish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken capturedPreloadCt = default;

        int preloadAttempt = 0;
        var preloadMock = new Mock<ILlmModelPreloadService>();
        preloadMock.Setup(p => p.PreloadModelAsync(It.IsAny<string>(), It.IsAny<ModelPreloadOptions?>(), It.IsAny<CancellationToken>()))
            .Returns<string, ModelPreloadOptions?, CancellationToken>(async (model, opts, ct) =>
            {
                int attempt = Interlocked.Increment(ref preloadAttempt);
                if (attempt == 1)
                {
                    capturedPreloadCt = ct;
                    preloadEntered.TrySetResult(true);
                    await allowPreloadFinish.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.False(ct.IsCancellationRequested);
                    return new ModelPreloadResult(true, model, opts?.Target, opts?.KeepAliveSeconds, opts?.ContextIntentTokens, opts?.ContextIntentTokens);
                }
                if (attempt == 2)
                {
                    throw new OperationsPausedException("Paused during preload");
                }
                return new ModelPreloadResult(true, model, opts?.Target, opts?.KeepAliveSeconds, opts?.ContextIntentTokens, opts?.ContextIntentTokens);
            });

        var tracker = new TrackingBackgroundTaskRunner();
        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = new FakeChatClient { ResponseToReturn = "Done." },
            PreloadService = preloadMock.Object,
            BackgroundTaskRunner = tracker,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(125);
        ws.AgentProfiles.First().ModelName = "qwen3:14b";
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();

        // Part A: Cold turn completes and disposes its turn CTS before preload finishes; preload token must remain uncancelled
        var convA = (await coordinator.StartConversationAsync(ws.Id, "Preload A")).Conversation;
        var res = await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, convA.Id, "Hello A"));
        Assert.Equal("Done.", res.AssistantMessage);
        await preloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(capturedPreloadCt.IsCancellationRequested);

        allowPreloadFinish.TrySetResult(true);
        await tracker.WaitForAllAsync();
        Assert.False(capturedPreloadCt.IsCancellationRequested);

        // Part B: StartConversationAsync triggers background warmup (attempt 2) which throws OperationsPausedException
        // and clears LastPreloadedSignature; a subsequent warmup with the SAME model signature must retry.
        var convB = (await coordinator.StartConversationAsync(
            ws.Id,
            "Preload B",
            options: new IdeationSessionOptions(OllamaRoutingTarget.Local, "qwen3:14b"))).Conversation;
        await tracker.WaitForAllAsync();
        Assert.Equal(2, Volatile.Read(ref preloadAttempt));

        await coordinator.UpdateSessionOptionsAsync(ws.Id, convB.Id, new IdeationSessionOptions(OllamaRoutingTarget.Local, "qwen3:14b", "Rewarm"));
        await coordinator.WarmupSessionAsync(ws.Id, convB.Id);
        await tracker.WaitForAllAsync();
        Assert.Equal(3, Volatile.Read(ref preloadAttempt));
    }

    [Fact]
    public async Task ErrorRollback_ColdPreHydratedZeroTools_RemovesFailedUserFromChatHistory_AndAfterToolsPreservesNotice()
    {
        // Part A: Cold Turn 1 error (PersistUserTurnAsync runs before WarmupCoreAsync, so warmup hydrates Failed Turn 1)
        var capturedPartAHistories = new List<List<ChatMessage>>();
        int partACall = 0;
        var clientA = new SequenceConditionalStreamingClient((call, msgs) =>
        {
            int n = Interlocked.Increment(ref partACall);
            capturedPartAHistories.Add(msgs.ToList());
            if (n == 1) throw new HttpRequestException("Simulated 503 on cold first turn");
            return "Retry succeeded";
        });

        using var builderA = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = clientA,
            PathResolver = TestPathResolver.Create()
        };
        var wsA = CreateWorkspace(126);
        await builderA.SeedWorkspaceAsync(wsA);
        var coordA = builderA.Build();
        var convA = (await coordA.StartConversationAsync(wsA.Id, "Cold Zero Tool Rollback")).Conversation;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            coordA.SendMessageAsync(new IdeationSendRequest(wsA.Id, convA.Id, "Failed Cold Turn 1")));

        var retryResult = await coordA.SendMessageAsync(new IdeationSendRequest(wsA.Id, convA.Id, "Retry Cold Turn 1"));
        Assert.Equal("Retry succeeded", retryResult.AssistantMessage);
        Assert.Equal(2, capturedPartAHistories.Count);
        var retryNonSystem = capturedPartAHistories[1].Where(m => m.Role != ChatRole.System).ToList();
        var singleUser = Assert.Single(retryNonSystem);
        Assert.Equal(ChatRole.User, singleUser.Role);
        Assert.Contains("Retry Cold Turn 1", singleUser.Text);
        Assert.DoesNotContain("Failed Cold Turn 1", singleUser.Text);

        // Part B: Error AFTER at least one tool completes preserves partial text + ErrorAfterToolsAssistantNotice and proposals
        var toolTempDir = Path.Combine(Path.GetTempPath(), "ncf-err-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(toolTempDir);
        try
        {
            int step = 0;
            var clientB = new CapturingAskStreamingClient((_, _) =>
            {
                step++;
                if (step == 1)
                {
                    var argsJson = new Dictionary<string, object?>
                    {
                        ["title"] = "Partial Proposal",
                        ["description"] = "Found before failure",
                        ["priority"] = TaskPriority.Normal
                    };
                    return
                    [
                        new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("Analysing repo...")]),
                        new ChatResponseUpdate(ChatRole.Assistant, "Partial discovery."),
                        new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("prop-1", CanonicalToolNames.ProposeTasks, argsJson)])
                    ];
                }

                throw new HttpRequestException("Simulated upstream crash after tool");
            });

            var pathResolverMock = new Mock<IWorkspacePathResolver>();
            pathResolverMock.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
            pathResolverMock.Setup(r => r.GetAgentWorkingClonePath(It.IsAny<int>())).Returns(toolTempDir);

            using var builderB = new TestIdeationCoordinatorBuilder
            {
                BaseChatClient = clientB,
                PathResolver = pathResolverMock.Object
            };
            var wsB = CreateWorkspace(127, path: toolTempDir);
            await builderB.SeedWorkspaceAsync(wsB);
            var coordB = builderB.Build();
            var convB = (await coordB.StartConversationAsync(wsB.Id, "Error After Tools")).Conversation;

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                coordB.SendMessageAsync(new IdeationSendRequest(wsB.Id, convB.Id, "Propose then crash")));

            var loadedB = await builderB.TaskRepository.GetConversationWithTurnsAsync(convB.Id);
            Assert.NotNull(loadedB);
            Assert.Contains(loadedB.Turns, t => t.Role == StageTurnRole.Tool && t.ToolName == CanonicalToolNames.ProposeTasks);
            Assert.Contains(loadedB.Turns, t => t.Role == StageTurnRole.ToolResult && t.ToolName == CanonicalToolNames.ProposeTasks);
            var errAssistantTurn = Assert.Single(loadedB.Turns, t => t.Role == StageTurnRole.Assistant);
            Assert.Contains("Partial discovery.", errAssistantTurn.Content);
            Assert.Contains(IIdeationConversationCoordinator.ErrorAfterToolsAssistantNotice, errAssistantTurn.Content);
            Assert.Contains("Analysing repo...", errAssistantTurn.ThinkingTrace);
            Assert.Contains("Partial Proposal", errAssistantTurn.MetadataJson);
        }
        finally
        {
            if (Directory.Exists(toolTempDir))
            {
                try { Directory.Delete(toolTempDir, true); }
                catch (IOException) { }
            }
        }
    }

    [Fact]
    public void ParseFallbackProposals_OmittingPriority_DefaultsToTaskPriorityNormal()
    {
        var responseText = """
            Here is a task proposal:
            ```json
            [
              {
                "title": "Harden Cache Eviction",
                "description": "Ensure bounded eviction on memory pressure."
              }
            ]
            ```
            """;

        var proposals = IdeationSystemPromptBuilder.ParseFallbackProposals(responseText, NullLogger.Instance);
        var proposal = Assert.Single(proposals);
        Assert.Equal("Harden Cache Eviction", proposal.Title);
        Assert.Equal(TaskPriority.Normal, proposal.Priority);
    }

    [Fact]
    public async Task FailedStaleBackgroundWarmup_DoesNotPoisonSubsequentTurnAfterOptionsUpdate()
    {
        int promptCtxCalls = 0;
        var staleWarmupEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleWarmup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var workspaceContextMock = new Mock<IAgentWorkspaceContextService>();
        workspaceContextMock.Setup(c => c.ResolveLocations(It.IsAny<Workspace>(), null, null))
            .Returns((Workspace w, int? _, string? __) => new AgentWorkspaceLocations(w.RepositoryPath, w.RepositoryPath, w.RepositoryPath));
        workspaceContextMock.Setup(c => c.BuildBacklogDigestAsync(It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        workspaceContextMock.Setup(c => c.BuildWorkspaceAttachmentsDigestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        workspaceContextMock.Setup(c => c.BuildFrictionHotspotsDigestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        workspaceContextMock.Setup(c => c.BuildPromptContextAsync(
                It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IWorkspaceSkillsService>(), It.IsAny<CancellationToken>()))
            .Returns<Workspace, string, IReadOnlyList<string>, IWorkspaceSkillsService, CancellationToken>(async (_, _, _, _, ct) =>
            {
                int call = Interlocked.Increment(ref promptCtxCalls);
                if (call == 1)
                {
                    staleWarmupEntered.TrySetResult(true);
                    await releaseStaleWarmup.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    throw new InvalidOperationException("Transient failure in stale background warmup");
                }
                return new AgentPromptContext(string.Empty, string.Empty, Array.Empty<string>());
            });

        using var builder = new TestIdeationCoordinatorBuilder
        {
            BaseChatClient = new FakeChatClient { ResponseToReturn = "Healthy reply after stale warmup." },
            WorkspaceContextService = workspaceContextMock.Object,
            PathResolver = TestPathResolver.Create()
        };
        var ws = CreateWorkspace(128);
        await builder.SeedWorkspaceAsync(ws);
        var coordinator = builder.Build();

        // Trigger background warmup with gen 1 (it hangs inside BuildPromptContextAsync call 1)
        var handle = await coordinator.GetOrCreateActiveConversationAsync(ws.Id);
        await staleWarmupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Update options to gen 2, let stale gen 1 warmup fault, and execute turn on gen 2
        await coordinator.UpdateSessionOptionsAsync(
            ws.Id,
            handle.Conversation.Id,
            new IdeationSessionOptions(OllamaRoutingTarget.Cloud, "qwen3:32b", "New prompt"));
        releaseStaleWarmup.TrySetResult(true);
        await Task.Delay(30);

        var res = await coordinator.SendMessageAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Run on gen 2"));
        Assert.Equal("Healthy reply after stale warmup.", res.AssistantMessage);
        Assert.True(Volatile.Read(ref promptCtxCalls) >= 2);
    }
}
