using Microsoft.Extensions.AI;
using Moq;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using Xunit;

namespace NightCodeForge.Agents.Tests.Inference;

public class OperationsPauseGatingChatClientTests
{
    private readonly Mock<IChatClient> _innerClient;
    private readonly Mock<IOperationsControlService> _operationsControlService;

    public OperationsPauseGatingChatClientTests()
    {
        _innerClient = new Mock<IChatClient>();
        _operationsControlService = new Mock<IOperationsControlService>();
    }

    [Fact]
    public async Task GetResponseAsync_WhenPaused_ThrowsOperationsPausedException()
    {
        _operationsControlService.Setup(o => o.IsPaused).Returns(true);
        var client = new OperationsPauseGatingChatClient(_innerClient.Object, _operationsControlService.Object);

        var messages = new[] { new ChatMessage(ChatRole.User, "Hello") };

        await Assert.ThrowsAsync<OperationsPausedException>(() =>
            client.GetResponseAsync(messages));

        _innerClient.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WhenPaused_ThrowsOperationsPausedException()
    {
        _operationsControlService.Setup(o => o.IsPaused).Returns(true);
        var client = new OperationsPauseGatingChatClient(_innerClient.Object, _operationsControlService.Object);

        var messages = new[] { new ChatMessage(ChatRole.User, "Hello") };

        var stream = client.GetStreamingResponseAsync(messages);

        await Assert.ThrowsAsync<OperationsPausedException>(async () =>
        {
            await foreach (var _ in stream)
            {
            }
        });

        _innerClient.Verify(c => c.GetStreamingResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetResponseAsync_WhenNotPaused_CallsInnerClient()
    {
        _operationsControlService.Setup(o => o.IsPaused).Returns(false);
        var expectedResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hi"));
        _innerClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var client = new OperationsPauseGatingChatClient(_innerClient.Object, _operationsControlService.Object);
        var messages = new[] { new ChatMessage(ChatRole.User, "Hello") };

        var actualResponse = await client.GetResponseAsync(messages);

        Assert.Same(expectedResponse, actualResponse);
        _innerClient.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WhenNotPaused_StreamsFromInnerClient()
    {
        _operationsControlService.Setup(o => o.IsPaused).Returns(false);

        async IAsyncEnumerable<ChatResponseUpdate> GetUpdates()
        {
            await Task.Yield();
            yield return new ChatResponseUpdate { Contents = [new TextContent("Chunk 1")] };
            yield return new ChatResponseUpdate { Contents = [new TextContent("Chunk 2")] };
        }

        _innerClient.Setup(c => c.GetStreamingResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()))
            .Returns(GetUpdates());

        var client = new OperationsPauseGatingChatClient(_innerClient.Object, _operationsControlService.Object);
        var messages = new[] { new ChatMessage(ChatRole.User, "Hello") };

        var collected = new List<string?>();
        await foreach (var update in client.GetStreamingResponseAsync(messages))
        {
            collected.Add(update.Text);
        }

        Assert.Equal(new[] { "Chunk 1", "Chunk 2" }, collected);
        _innerClient.Verify(c => c.GetStreamingResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
