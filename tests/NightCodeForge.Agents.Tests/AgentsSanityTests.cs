using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace NightCodeForge.Agents.Tests;

public class AgentsSanityTests
{
    [Fact]
    public async Task AgentsProject_CompilesAndInitializes()
    {
        var options = new HarnessAgentOptions
        {
            Id = "test-agent",
            Name = "TestAgent",
            HarnessInstructions = "Test instructions",
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableWebSearch = true
        };

        var chatClient = new SimpleChatClient();
        var agent = new HarnessAgent(chatClient, options);
        Assert.NotNull(agent);

        var session = await agent.CreateSessionAsync();
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync("Hello", session))
        {
            updates.Add(update);
            var text = update.Text;
            var contents = update.Contents;
            Assert.NotNull(contents);
        }
        Assert.NotEmpty(updates);
    }

    [Fact]
    public async Task HarnessAgent_InvokesTool_AndProjectsStreamingUpdates()
    {
        bool toolCalled = false;
        var tool = AIFunctionFactory.Create(([System.ComponentModel.Description("query")] string query) =>
        {
            toolCalled = true;
            return "search result for " + query;
        }, "web_search", "Search the web");

        var options = new HarnessAgentOptions
        {
            Id = "test-agent",
            Name = "TestAgent",
            HarnessInstructions = "You are a test agent.",
            ChatOptions = new ChatOptions
            {
                Tools = [tool]
            },
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableWebSearch = true,
            DisableToolAutoApproval = false
        };

        var chatClient = new MultiTurnToolChatClient();
        var agent = new HarnessAgent(chatClient, options);
        var session = await agent.CreateSessionAsync();

        var projector = new NightCodeForge.Agents.Streaming.AgentStreamProjector();
        var domainUpdates = new List<NightCodeForge.Core.Interfaces.IdeationStreamUpdate>();

        var msgs = new List<ChatMessage>
        {
            new(ChatRole.System, "system instructions"),
            new(ChatRole.User, "Search for MAF")
        };

        await foreach (var update in agent.RunStreamingAsync(msgs, session))
        {
            foreach (var domainEvt in projector.ProjectUpdate(update))
            {
                domainUpdates.Add(domainEvt);
            }
        }
        foreach (var domainEvt in projector.Flush())
        {
            domainUpdates.Add(domainEvt);
        }

        Assert.True(toolCalled);
        Assert.Contains(domainUpdates, u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.ToolCallStarted);
        Assert.Contains(domainUpdates, u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.ToolCallCompleted);
        Assert.Contains(domainUpdates, u => u.Kind == NightCodeForge.Core.Interfaces.IdeationStreamUpdateKind.MessageDelta);
    }

    [Fact]
    public async Task HarnessAgent_WithNonStreamingClient_ExecutesRunAsync()
    {
        var options = new HarnessAgentOptions
        {
            Id = "test-agent",
            Name = "TestAgent",
            HarnessInstructions = "You are a test agent.",
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableWebSearch = true,
            DisableToolAutoApproval = false
        };

        var chatClient = new NonStreamingChatClient();
        var agent = new HarnessAgent(chatClient, options);
        var session = await agent.CreateSessionAsync();

        var response = await agent.RunAsync("Hello", session);
        Assert.NotNull(response);
        Assert.Equal("non-streaming ok", response.Text);
    }

    [Fact]
    public async Task HarnessAgent_ToolThrowsException_PropagatesException()
    {
        var tool = AIFunctionFactory.Create(() =>
        {
            throw new InvalidOperationException("Tool boom!");
#pragma warning disable CS0162
            return "ok";
#pragma warning restore CS0162
        }, "fail_tool", "A failing tool");

        var options = new HarnessAgentOptions
        {
            Id = "test-agent",
            Name = "TestAgent",
            HarnessInstructions = "You are a test agent.",
            ChatOptions = new ChatOptions { Tools = [tool] },
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableWebSearch = true,
            DisableToolAutoApproval = false
        };

        var chatClient = new FailingToolChatClient();
        var agent = new HarnessAgent(chatClient, options);
        var session = await agent.CreateSessionAsync();

        Exception? caught = null;
        try
        {
            await foreach (var _ in agent.RunStreamingAsync("Trigger tool", session)) { }
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
    }

    private class FailingToolChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("Mock");
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate
            {
                Contents = { new FunctionCallContent("call_err", "fail_tool", new Dictionary<string, object?>()) }
            };
            await Task.Yield();
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private class NonStreamingChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("Mock");
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "non-streaming ok")));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private class MultiTurnToolChatClient : IChatClient
    {
        private int _turn = 0;
        public ChatClientMetadata Metadata => new("Mock");
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _turn++;
            if (_turn == 1)
            {
                yield return new ChatResponseUpdate
                {
                    Contents =
                    {
                        new TextReasoningContent("Thinking about searching..."),
                        new FunctionCallContent("call_1", "web_search", new Dictionary<string, object?> { ["query"] = "MAF" })
                    }
                };
            }
            else
            {
                yield return new ChatResponseUpdate
                {
                    Contents =
                    {
                        new TextContent("Here is the search result.")
                    }
                };
            }
            await Task.Yield();
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private class SimpleChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("Mock");
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate { Contents = [new TextContent("ok")] };
            await Task.Yield();
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
