using Microsoft.Extensions.AI;
using Moq;
using NightCodeForge.Agents.Compaction;
using Xunit;

namespace NightCodeForge.Agents.Tests.Compaction;

public class HistoricalReasoningCompactionChatClientTests
{
    [Fact]
    public async Task GetResponseAsync_PrunesReasoningFromOlderAssistantTurns_PreservesLatestTurn()
    {
        var innerClient = new Mock<IChatClient>();
        IEnumerable<ChatMessage>? capturedMessages = null;

        innerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) => capturedMessages = msgs.ToList())
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Final answer")));

        var client = new HistoricalReasoningCompactionChatClient(innerClient.Object);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Turn 1 question"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new TextReasoningContent("Turn 1 thinking trace that should be pruned"),
                new TextContent("Turn 1 response text")
            }),
            new(ChatRole.User, "Turn 2 question"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new TextReasoningContent("Turn 2 active thinking trace that should be kept"),
                new TextContent("Turn 2 response text")
            })
        };

        await client.GetResponseAsync(messages);

        Assert.NotNull(capturedMessages);
        var capturedList = capturedMessages.ToList();
        Assert.Equal(4, capturedList.Count);

        // Turn 1 assistant message should have NO TextReasoningContent
        var turn1Assistant = capturedList[1];
        Assert.DoesNotContain(turn1Assistant.Contents, c => c is TextReasoningContent);
        Assert.Contains(turn1Assistant.Contents, c => c is TextContent tc && tc.Text == "Turn 1 response text");

        // Turn 2 assistant message should KEEP its TextReasoningContent
        var turn2Assistant = capturedList[3];
        Assert.Contains(turn2Assistant.Contents, c => c is TextReasoningContent);
    }
}
