using System.ComponentModel;
using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Ideation;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Tests;

/// <summary>
/// Verifies that the transcript exposes what the agent framework actually sends to the model
/// (effective instructions, injected/rewritten messages, tool schemas, options).
/// </summary>
public class ModelCallTracingTests
{
    private sealed class EchoClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static AIFunction SampleTool() =>
        AIFunctionFactory.Create(
            ([Description("A path")] string path) => path,
            "sample_tool",
            "Returns the path.");

    private static ChatOptions Options(string? instructions = "base instructions") => new()
    {
        Instructions = instructions,
        ModelId = "test-model",
        Temperature = 0.2f,
        Tools = new List<AITool> { SampleTool() },
        AdditionalProperties = new AdditionalPropertiesDictionary { ["think"] = true, ["reasoning_effort"] = "high" }
    };

    [Fact]
    public async Task Tracer_RecordsEachCall_WithFullFirstRequestAndIncrementalDiffs()
    {
        var tracer = new ThinkingAndActivityTracingChatClient(new EchoClient());
        var user = new ChatMessage(ChatRole.User, "hello");
        var assistantCall = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("c1", "sample_tool", new Dictionary<string, object?> { ["path"] = "a.txt" })
        });
        var toolResult = new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("c1", "file body") });

        await tracer.GetResponseAsync(new[] { user }, Options());
        await tracer.GetResponseAsync(new[] { user, assistantCall, toolResult }, Options());

        var calls = tracer.ModelCalls;
        Assert.Equal(2, calls.Count);

        var first = calls[0];
        Assert.Equal(1, first.TotalMessages);
        Assert.Equal(0, first.UnchangedPrefixCount);
        Assert.True(first.InstructionsChanged);
        Assert.Equal("base instructions", first.Instructions);
        Assert.True(first.ToolsChanged);
        var tool = Assert.Single(first.Tools!);
        Assert.Equal("sample_tool", tool.Name);
        Assert.Equal("Returns the path.", tool.Description);
        Assert.Contains("path", tool.JsonSchema);
        Assert.Contains("model: test-model", first.OptionsSummary);
        Assert.Contains("reasoning_effort", first.OptionsSummary);

        var second = calls[1];
        Assert.Equal(3, second.TotalMessages);
        Assert.Equal(1, second.UnchangedPrefixCount);
        Assert.False(second.HistoryRewritten);
        Assert.False(second.InstructionsChanged);
        Assert.Null(second.Instructions);
        Assert.False(second.ToolsChanged);
        Assert.Null(second.Tools);
        Assert.Equal(2, second.Messages.Count);
        Assert.Contains("[tool call] sample_tool", second.Messages[0].Text);
        Assert.Contains("[tool result c1] file body", second.Messages[1].Text);
    }

    [Fact]
    public async Task Tracer_FlagsRewrittenHistory_WhenEarlierMessagesChange()
    {
        var tracer = new ThinkingAndActivityTracingChatClient(new EchoClient());
        var m1 = new ChatMessage(ChatRole.User, "first");
        var m2 = new ChatMessage(ChatRole.Assistant, "long reasoning answer");
        var m3 = new ChatMessage(ChatRole.User, "second");

        await tracer.GetResponseAsync(new[] { m1, m2, m3 }, Options());
        await tracer.GetResponseAsync(
            new[] { m1, new ChatMessage(ChatRole.Assistant, "[compacted]"), m3 },
            Options("changed instructions"));

        var second = tracer.ModelCalls[1];
        Assert.True(second.HistoryRewritten);
        Assert.Equal(1, second.UnchangedPrefixCount);
        Assert.Equal(2, second.Messages.Count);
        Assert.True(second.InstructionsChanged);
        Assert.Equal("changed instructions", second.Instructions);
    }

    [Fact]
    public async Task Tracer_CapturesInstructionsAndSystemMessagesTogether()
    {
        var tracer = new ThinkingAndActivityTracingChatClient(new EchoClient());

        await tracer.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.System, "injected system"), new ChatMessage(ChatRole.User, "hi") },
            Options("base instructions"));

        Assert.NotNull(tracer.LastCapturedSystemPrompt);
        Assert.Contains("base instructions", tracer.LastCapturedSystemPrompt);
        Assert.Contains("injected system", tracer.LastCapturedSystemPrompt);
        Assert.Contains("[System message in chat history]", tracer.LastCapturedSystemPrompt);

        // The injected System message is also visible as an actual message of the request.
        Assert.Contains(tracer.ModelCalls[0].Messages, m => m.Role == "system" && m.Text == "injected system");
    }

    [Fact]
    public async Task Tracer_RecordsStreamingRequests()
    {
        var tracer = new ThinkingAndActivityTracingChatClient(new EchoClient());

        await foreach (var _ in tracer.GetStreamingResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, Options()))
        {
        }

        Assert.Single(tracer.ModelCalls);
    }

    [Fact]
    public async Task Tracer_BoundsRetainedCalls_KeepingFirstAndMostRecent()
    {
        var tracer = new ThinkingAndActivityTracingChatClient(new EchoClient());
        var messages = new List<ChatMessage>();

        for (int i = 0; i < ThinkingAndActivityTracingChatClient.MaxRetainedModelCalls + 25; i++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"m{i}"));
            await tracer.GetResponseAsync(messages.ToList(), Options());
        }

        var calls = tracer.ModelCalls;
        Assert.Equal(ThinkingAndActivityTracingChatClient.MaxRetainedModelCalls, calls.Count);
        Assert.Equal(1, calls[0].Index);
        Assert.Equal(ThinkingAndActivityTracingChatClient.MaxRetainedModelCalls + 25, calls[^1].Index);
    }

    [Fact]
    public async Task SessionTranscriptService_RendersModelCallsSection()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ncf-modelcalls-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tracer = new ThinkingAndActivityTracingChatClient(new EchoClient());
            var m1 = new ChatMessage(ChatRole.User, "first");
            var m2 = new ChatMessage(ChatRole.Assistant, "answer");
            await tracer.GetResponseAsync(new[] { m1, m2 }, Options());
            await tracer.GetResponseAsync(new[] { m1, new ChatMessage(ChatRole.Assistant, "[compacted]") }, Options());

            var metadata = new TranscriptMetadata(
                TaskId: 9,
                StageId: "execution",
                AgentProfile: "p",
                ModelName: "m",
                DurationMs: 1,
                Metrics: new AgentRunMetrics(1, 0, 0, 0),
                Optimizations: new LocalOptimizationFlags(),
                Status: AgentRunStatus.Completed,
                ModelCalls: tracer.ModelCalls);

            var path = await new SessionTranscriptService(dir).SaveTranscriptAsync(
                metadata, new List<TranscriptEntry> { new(DateTime.UtcNow, "USER", "first") });
            var markdown = await File.ReadAllTextAsync(path);

            Assert.Contains("### Model calls (requests as sent to the model)", markdown);
            Assert.Contains("Call 1", markdown);
            Assert.Contains("Call 2", markdown);
            Assert.Contains("**Instructions**", markdown);
            Assert.Contains("base instructions", markdown);
            Assert.Contains("**Instructions:** unchanged", markdown);
            Assert.Contains("**Tools (1)**", markdown);
            Assert.Contains("- `sample_tool` — Returns the path.", markdown);
            Assert.Contains("**Tools:** unchanged (1)", markdown);
            Assert.Contains("History rewritten", markdown);
            Assert.Contains("[compacted]", markdown);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task IdeationTranscript_ContainsModelCallsWithEffectiveInstructionsAndToolSchemas()
    {
        var repo = Path.Combine(Path.GetTempPath(), "ncf-ideation-modelcalls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var service = new NightCodeForge.Agents.Tests.Fixtures.TestIdeationServiceBuilder
            {
                PathResolver = NightCodeForge.Agents.Tests.Fixtures.TestPathResolver.Create(),
                BaseChatClient = new EchoClient(),
                TranscriptService = new SessionTranscriptService(Path.Combine(repo, "t"))
            }.Build();
            var workspace = new Workspace { Id = 5, RepositoryPath = repo };
            workspace.AgentProfiles.Add(AgentProfile.CreateDefaultIdeationProfile(5));

            IdeationResult? result = null;
            await foreach (var update in service.ConverseStreamingAsync(workspace, "Hello"))
            {
                if (update.Kind == IdeationStreamUpdateKind.Completed)
                {
                    result = update.FinalResult;
                }
            }

            Assert.NotNull(result?.FullTranscriptMarkdown);
            var markdown = result.FullTranscriptMarkdown;
            Assert.Contains("### Model calls (requests as sent to the model)", markdown);
            Assert.Contains("Call 1", markdown);
            Assert.Contains("<environment>", markdown);
            Assert.Contains("````json", markdown);
            Assert.Contains("- `read`", markdown);
        }
        finally
        {
            if (Directory.Exists(repo))
            {
                try { Directory.Delete(repo, true); } catch { }
            }
        }
    }
}
