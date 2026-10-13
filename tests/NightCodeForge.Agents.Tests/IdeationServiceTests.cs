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
    ""description"": ""Wire cache into IssueOrchestrator"",
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
        Assert.Equal(2, result.ProposedIssues.Count);

        var p1 = result.ProposedIssues[0];
        Assert.Equal("Implement Token Cache", p1.Title);
        Assert.Equal("Add LRU cache for prompt tokens", p1.Description);
        Assert.Equal(1, p1.Priority);

        var p2 = result.ProposedIssues[1];
        Assert.Equal("Integrate Token Cache into Pipeline", p2.Title);
        Assert.Equal(2, p2.Priority);
        Assert.NotNull(p2.DependsOnTitles);
        Assert.Contains("Implement Token Cache", p2.DependsOnTitles);
    }
}
