using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Tests;

public class ThinkingCompactionTests
{
    private static ChatMessage Reasoned(string reasoning, string text)
    {
        var msg = new ChatMessage(ChatRole.Assistant, text);
        msg.Contents.Add(new TextReasoningContent(reasoning));
        return msg;
    }

    [Fact]
    public void CompactHistoricalAssistantMessages_CompactsOnlyAssistantTurns()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt rules"),
            new(ChatRole.User, "Fix bug in OrderService"),
            Reasoned("Thinking deeply about the bug...", "Here is the plan."),
            new(ChatRole.User, "Proceed with edits")
        };

        var compacted = ThinkingTraceCompactionChatClient.CompactHistoricalAssistantMessages(messages, keepVerbatimCount: 0);

        Assert.Equal(4, compacted.Count);
        Assert.Equal("System prompt rules", compacted[0].Text);
        Assert.Equal("Fix bug in OrderService", compacted[1].Text);
        Assert.Equal("Here is the plan.", compacted[2].Text);
        Assert.Equal("[Reasoned: Thinking deeply about the bug...]", compacted[2].Contents.OfType<TextReasoningContent>().Single().Text);
        Assert.Equal("Proceed with edits", compacted[3].Text);
    }

    [Fact]
    public void CompactHistoricalAssistantMessages_PreservesRecentTurnsWithinKeepVerbatimCount()
    {
        var messages = new List<ChatMessage>();
        for (int i = 0; i < 5; i++)
        {
            messages.Add(Reasoned($"Reasoning step {i}\nMore detail", $"Response {i}"));
        }

        // Keep 2 verbatim
        var compacted = ThinkingTraceCompactionChatClient.CompactHistoricalAssistantMessages(messages, keepVerbatimCount: 2);

        Assert.Equal(5, compacted.Count);

        // First 3 should be compacted
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal($"[Reasoned: Reasoning step {i}]", compacted[i].Contents.OfType<TextReasoningContent>().Single().Text);
            Assert.Equal($"Response {i}", compacted[i].Text);
        }

        // Last 2 should be verbatim
        for (int i = 3; i < 5; i++)
        {
            Assert.Contains("More detail", compacted[i].Contents.OfType<TextReasoningContent>().Single().Text);
        }
    }

    [Fact]
    public async Task ChatClient_WhenOptedOut_PreservesVerbatimReasoning()
    {
        var flags = new LocalOptimizationFlags { ThinkingTraceCompaction = false };
        var innerClient = new MockChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Response")));

        var client = new ThinkingTraceCompactionChatClient(innerClient, flags);
        var messages = new List<ChatMessage>
        {
            Reasoned("Unpruned reasoning monologue", "Result")
        };

        var response = await client.GetResponseAsync(messages);

        Assert.NotNull(response);
        var forwarded = innerClient.LastReceivedMessages!.First();
        Assert.Equal("Unpruned reasoning monologue", forwarded.Contents.OfType<TextReasoningContent>().Single().Text);
    }

    [Fact]
    public async Task ChatClient_UsesThinkingKeepVerbatimCountFromFlags_WhenNoOverrideProvided()
    {
        var flags = new LocalOptimizationFlags
        {
            ThinkingTraceCompaction = true,
            ThinkingKeepVerbatimCount = 1
        };
        var innerClient = new MockChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Response")));
        var client = new ThinkingTraceCompactionChatClient(innerClient, flags);

        var messages = new List<ChatMessage>
        {
            Reasoned("First thought", "Turn 1"),
            Reasoned("Second thought", "Turn 2")
        };

        await client.GetResponseAsync(messages);

        var forwarded = innerClient.LastReceivedMessages!.ToList();
        Assert.Equal("[Reasoned: First thought]", forwarded[0].Contents.OfType<TextReasoningContent>().Single().Text);
        Assert.Equal("Second thought", forwarded[1].Contents.OfType<TextReasoningContent>().Single().Text);
    }

    [Fact]
    public void CompactHistoricalAssistantMessages_CompactsTextReasoningContent_PreservingVerbatimWindow()
    {
        var messages = new List<ChatMessage>();
        for (int i = 0; i < 4; i++)
        {
            var msg = new ChatMessage(ChatRole.Assistant, $"Assistant response {i}");
            msg.Contents.Add(new TextReasoningContent($"Detailed reasoning monologue for turn {i}\nSecond line of reasoning"));
            messages.Add(msg);
        }

        // Keep 1 verbatim
        var compacted = ThinkingTraceCompactionChatClient.CompactHistoricalAssistantMessages(messages, keepVerbatimCount: 1);

        Assert.Equal(4, compacted.Count);

        // First 3 should have compacted TextReasoningContent
        for (int i = 0; i < 3; i++)
        {
            var reasoning = compacted[i].Contents.OfType<TextReasoningContent>().FirstOrDefault();
            Assert.NotNull(reasoning);
            Assert.Equal($"[Reasoned: Detailed reasoning monologue for turn {i}]", reasoning.Text);
            Assert.Equal($"Assistant response {i}", compacted[i].Text);
        }

        // 4th should be verbatim
        var lastReasoning = compacted[3].Contents.OfType<TextReasoningContent>().FirstOrDefault();
        Assert.NotNull(lastReasoning);
        Assert.Contains("Second line of reasoning", lastReasoning.Text);
    }

    [Fact]
    public void CrucibleAgentHarness_CompactOlderToolResults_TruncatesOlderToolOutputs()
    {
        var messages = new List<ChatMessage>();
        for (int i = 0; i < 6; i++)
        {
            var longOutput = new string('x', 2500);
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call-{i}", longOutput)]));
        }

        Crucible.CrucibleAgentHarness.CompactOlderToolResults(messages, keepVerbatimCount: 2);

        // First 4 messages should be truncated
        for (int i = 0; i < 4; i++)
        {
            var frc = messages[i].Contents.OfType<FunctionResultContent>().First();
            Assert.Contains("[Output truncated;", frc.Result?.ToString());
        }

        // Last 2 messages should be verbatim
        for (int i = 4; i < 6; i++)
        {
            var frc = messages[i].Contents.OfType<FunctionResultContent>().First();
            Assert.Equal(2500, frc.Result?.ToString()?.Length);
        }
    }

    [Fact]
    public void CrucibleAgentHarness_CompactOlderToolResults_WhenOptedOut_DoesNotTruncate()
    {
        var messages = new List<ChatMessage>();
        for (int i = 0; i < 6; i++)
        {
            var longOutput = new string('z', 2500);
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call-{i}", longOutput)]));
        }

        var flags = new LocalOptimizationFlags { CompactOlderToolResults = false };
        Crucible.CrucibleAgentHarness.CompactOlderToolResults(messages, keepVerbatimCount: 2, flags: flags);

        // None should be truncated
        for (int i = 0; i < 6; i++)
        {
            var frc = messages[i].Contents.OfType<FunctionResultContent>().First();
            Assert.Equal(2500, frc.Result?.ToString()?.Length);
            Assert.DoesNotContain("[Output truncated;", frc.Result?.ToString());
        }
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
