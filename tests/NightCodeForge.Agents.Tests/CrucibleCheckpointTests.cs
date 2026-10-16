using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Crucible.ChatClients;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Tests;

public class CrucibleCheckpointTests
{
    [Fact]
    public void SerializeAndDeserializeSession_PreservesMessageRolesAndContents()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt instructions"),
            new(ChatRole.User, "Hello assistant"),
            new(ChatRole.Assistant, [
                new TextContent("Let me run a tool"),
                new FunctionCallContent("call-1", "read", new Dictionary<string, object?> { ["path"] = "src/Test.cs" })
            ]),
            new(ChatRole.Tool, [
                new FunctionResultContent("call-1", "line 1\nline 2")
            ])
        };

        var json = CrucibleAgentHarness.SerializeSession(messages);
        Assert.NotNull(json);

        var restored = CrucibleAgentHarness.DeserializeSession(json);
        Assert.Equal(4, restored.Count);
        Assert.Equal(ChatRole.System, restored[0].Role);
        Assert.Equal("System prompt instructions", restored[0].Text);

        Assert.Equal(ChatRole.Assistant, restored[2].Role);
        var fnCall = restored[2].Contents.OfType<FunctionCallContent>().FirstOrDefault();
        Assert.NotNull(fnCall);
        Assert.Equal("read", fnCall.Name);

        Assert.Equal(ChatRole.Tool, restored[3].Role);
        var fnResult = restored[3].Contents.OfType<FunctionResultContent>().FirstOrDefault();
        Assert.NotNull(fnResult);
        Assert.Equal("line 1\nline 2", fnResult.Result);
    }

    [Fact]
    public void SerializeAndDeserializeSession_PreservesReasoningOrderAndAuthor()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Do it"),
            new(ChatRole.Assistant, [
                new TextReasoningContent("step one") { ProtectedData = "opaque-blob" },
                new TextContent("I will read"),
                new FunctionCallContent("call-1", "read", new Dictionary<string, object?> { ["path"] = "a.cs" }),
                new TextReasoningContent("step two")
            ]) { AuthorName = "crucible" },
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "content")])
        };

        var restored = CrucibleAgentHarness.DeserializeSession(CrucibleAgentHarness.SerializeSession(messages));

        Assert.Equal(3, restored.Count);
        var assistant = restored[1];
        Assert.Equal("crucible", assistant.AuthorName);
        Assert.Collection(assistant.Contents,
            c => { var r = Assert.IsType<TextReasoningContent>(c); Assert.Equal("step one", r.Text); Assert.Equal("opaque-blob", r.ProtectedData); },
            c => Assert.Equal("I will read", Assert.IsType<TextContent>(c).Text),
            c => Assert.Equal("read", Assert.IsType<FunctionCallContent>(c).Name),
            c => Assert.Equal("step two", Assert.IsType<TextReasoningContent>(c).Text));
        Assert.Equal("a.cs", assistant.Contents.OfType<FunctionCallContent>().Single().Arguments!["path"]?.ToString());
        Assert.Equal("content", restored[2].Contents.OfType<FunctionResultContent>().Single().Result);

        // Re-serializing the restored session is lossless.
        Assert.Equal(CrucibleAgentHarness.SerializeSession(messages), CrucibleAgentHarness.SerializeSession(restored));
    }

    [Fact]
    public void DeserializeSession_ReadsLegacyCheckpointFormat()
    {
        var legacy = """[{"Role":"user","Text":"hi","FunctionCalls":[],"FunctionResults":[]},{"Role":"assistant","Text":"calling","FunctionCalls":[{"CallId":"c1","Name":"read","Arguments":{"path":"x"}}],"FunctionResults":[]},{"Role":"tool","Text":"","FunctionCalls":[],"FunctionResults":[{"CallId":"c1","Result":"ok"}]}]""";

        var restored = CrucibleAgentHarness.DeserializeSession(legacy);

        Assert.Equal(3, restored.Count);
        Assert.Equal("hi", restored[0].Text);
        Assert.Equal("read", restored[1].Contents.OfType<FunctionCallContent>().Single().Name);
        Assert.Equal("ok", restored[2].Contents.OfType<FunctionResultContent>().Single().Result);
    }

    [Fact]
    public void BuildTranscriptEntries_ToolResultWithUnknownCallId_FallsBackToCallId()
    {
        var entries = CrucibleAgentHarness.BuildTranscriptEntries([
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("orphan-1", "out")])
        ]);

        Assert.Equal("orphan-1", Assert.Single(entries).ToolName);
    }

    [Fact]
    public async Task MessageInjectingChatClient_InjectsAnswerOnResumption()
    {
        var inner = new CapturingChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Resumed response")));
        var injector = new MessageInjectingChatClient(inner);

        injector.InjectUserAnswer("The database connection string is Server=127.0.0.1;Database=dev");

        var initialMessages = new List<ChatMessage>
        {
            new(ChatRole.System, "Rules"),
            new(ChatRole.User, "Original request")
        };

        var response = await injector.GetResponseAsync(initialMessages);

        Assert.NotNull(response);
        Assert.NotNull(inner.CapturedMessages);
        Assert.Equal(3, inner.CapturedMessages.Count);
        Assert.Equal("The database connection string is Server=127.0.0.1;Database=dev", inner.CapturedMessages[2].Text);
    }

    private class CapturingChatClient : IChatClient
    {
        private readonly ChatResponse _cannedResponse;
        public IReadOnlyList<ChatMessage>? CapturedMessages { get; private set; }

        public CapturingChatClient(ChatResponse cannedResponse)
        {
            _cannedResponse = cannedResponse;
        }

        public ChatClientMetadata Metadata => new("Capturing");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CapturedMessages = chatMessages.ToList();
            return Task.FromResult(_cannedResponse);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CapturedMessages = chatMessages.ToList();
            yield return new ChatResponseUpdate { Contents = [new TextContent(_cannedResponse.Text ?? "")] };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
