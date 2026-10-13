using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Tests;

public class ThinkingCompactionTests
{
    [Fact]
    public void CompactThinkBlock_PrunesMonologue_PreservesFirstLineStub()
    {
        var input = @"<think>
Examining the namespace dependencies in OrderService.
Need to add Microsoft.AspNetCore.Authentication.
Checking if IOrderRepository has required methods.
</think>
I have identified the missing namespace. Let me edit the file.";

        var compacted = ThinkingTraceCompactionChatClient.CompactThinkBlock(input);

        Assert.DoesNotContain("Checking if IOrderRepository has required methods.", compacted);
        Assert.Contains("[Reasoned: Examining the namespace dependencies in OrderService.]", compacted);
        Assert.Contains("I have identified the missing namespace.", compacted);
    }

    [Fact]
    public void CompactHistoricalAssistantMessages_CompactsOnlyAssistantTurns()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt rules"),
            new(ChatRole.User, "Fix bug in OrderService"),
            new(ChatRole.Assistant, "<think>Thinking deeply about the bug...</think>Here is the plan."),
            new(ChatRole.User, "Proceed with edits")
        };

        var compacted = ThinkingTraceCompactionChatClient.CompactHistoricalAssistantMessages(messages);

        Assert.Equal(4, compacted.Count);
        Assert.Equal("System prompt rules", compacted[0].Text);
        Assert.Equal("Fix bug in OrderService", compacted[1].Text);
        Assert.Contains("[Reasoned: Thinking deeply about the bug...]", compacted[2].Text);
        Assert.DoesNotContain("<think>", compacted[2].Text);
        Assert.Equal("Proceed with edits", compacted[3].Text);
    }

    [Fact]
    public async Task ChatClient_WhenOptedOut_PreservesVerbatimThinkBlock()
    {
        var flags = new LocalOptimizationFlags { ThinkingTraceCompaction = false };
        var innerClient = new MockChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Response")));

        var client = new ThinkingTraceCompactionChatClient(innerClient, flags);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "<think>Unpruned reasoning monologue</think>Result")
        };

        var response = await client.GetResponseAsync(messages);

        Assert.NotNull(response);
        Assert.Equal("<think>Unpruned reasoning monologue</think>Result", innerClient.LastReceivedMessages?.FirstOrDefault()?.Text);
    }

    private class MockChatClient : IChatClient
    {
        private readonly ChatResponse _cannedResponse;
        public IReadOnlyList<ChatMessage>? LastReceivedMessages { get; private set; }

        public MockChatClient(ChatResponse cannedResponse)
        {
            _cannedResponse = cannedResponse;
        }

        public ChatClientMetadata Metadata => new("Mock");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastReceivedMessages = chatMessages.ToList();
            return Task.FromResult(_cannedResponse);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastReceivedMessages = chatMessages.ToList();
            yield return new ChatResponseUpdate { Contents = [new TextContent(_cannedResponse.Text ?? "")] };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
