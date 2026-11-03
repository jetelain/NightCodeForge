using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public class CrucibleContinuationLoopTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "crucible-cont-" + Guid.NewGuid().ToString("N"));

    public CrucibleContinuationLoopTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, int, IAsyncEnumerable<ChatResponseUpdate>> _handler;
        private int _calls;

        public ScriptedChatClient(Func<IEnumerable<ChatMessage>, int, IAsyncEnumerable<ChatResponseUpdate>> handler) => _handler = handler;

        public int Calls => _calls;
        public List<List<ChatMessage>> Requests { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = chatMessages.ToList();
            lock (Requests) Requests.Add(list);
            return _handler(list, Interlocked.Increment(ref _calls));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private AgentSessionContext NewContext() => new()
    {
        ForgeTask = new ForgeTask { Id = 301, Title = "Continuation task", Description = "Implement" },
        Workspace = new Workspace { Id = 1, RepositoryPath = _tempDir },
        Profile = new AgentProfile { Id = "dev", Mode = AgentMode.Full },
        StageId = "implementation",
        RequiredMode = AgentMode.Full,
        WorkspacePath = _tempDir
    };

    private static async IAsyncEnumerable<ChatResponseUpdate> Text(string text, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate { Contents = [new TextContent(text)] };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ReasoningOnly(string text)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate { Contents = [new TextReasoningContent(text)] };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ToolCall(string callId, string name, IDictionary<string, object?> args)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate { Contents = [new FunctionCallContent(callId, name, args)] };
    }

    [Fact]
    public async Task TruncatedTurn_IsNudged_AndStageContinuesToCompletion()
    {
        var client = new ScriptedChatClient((messages, call) => call == 1
            ? Text("Next steps:\n   - \\")
            : Text("All work is implemented and verified."));

        var harness = new TestCrucibleHarnessBuilder { BaseChatClient = client, TaskRepository = new InMemoryTaskRepository() }.Build();

        var result = await harness.RunStageAsync(NewContext(), CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(2, client.Calls);
        Assert.Contains(client.Requests[1], m => m.Role == ChatRole.User && m.Text.Contains("interrupted mid-thought"));
        Assert.Contains("All work is implemented", result.Summary);
    }

    [Fact]
    public async Task ReasoningOnlyTurn_IsTreatedAsTruncated()
    {
        var client = new ScriptedChatClient((messages, call) => call == 1
            ? ReasoningOnly("I should now call the edit tool to")
            : Text("Done."));

        var harness = new TestCrucibleHarnessBuilder { BaseChatClient = client, TaskRepository = new InMemoryTaskRepository() }.Build();

        var result = await harness.RunStageAsync(NewContext(), CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task RepeatedTruncation_TripsCircuitBreaker()
    {
        var client = new ScriptedChatClient((messages, call) => Text("```csharp\nvar x = 1;"));

        var harness = new TestCrucibleHarnessBuilder { BaseChatClient = client, TaskRepository = new InMemoryTaskRepository() }.Build();

        var result = await harness.RunStageAsync(NewContext(), CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal(CrucibleAgentHarness.MaxConsecutiveTruncationNudges + 1, client.Calls);
        Assert.Contains("truncated", result.Error ?? result.Summary ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CleanCompletion_DoesNotNudge()
    {
        var client = new ScriptedChatClient((messages, call) => Text("Implementation complete. All tests pass."));

        var harness = new TestCrucibleHarnessBuilder { BaseChatClient = client, TaskRepository = new InMemoryTaskRepository() }.Build();

        var result = await harness.RunStageAsync(NewContext(), CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task ToolCallInPass_ResetsConsecutiveTruncationCounter()
    {
        var sampleFile = Path.Combine(_tempDir, "Sample.cs");
        await File.WriteAllTextAsync(sampleFile, "public class Sample {}");

        // Calls 1..3: 3 consecutive truncated turns (reaches MaxConsecutiveTruncationNudges = 3).
        // Call 4: executes a tool call ('read'), then Call 5 (in the same MAF pass) ends truncated again.
        // Because Call 4 executed a tool call, consecutiveTruncationNudges was reset to 0 before evaluating Call 5!
        // Call 6: completes cleanly.
        var client = new ScriptedChatClient((messages, call) => call switch
        {
            1 => Text("Step 1:\n   - \\"),
            2 => Text("Step 2:\n   - \\"),
            3 => Text("Step 3:\n   - \\"),
            4 => ToolCall("call-read-1", "read", new Dictionary<string, object?> { ["filePath"] = "Sample.cs" }),
            5 => Text("After reading, next I will"),
            _ => Text("Implementation complete and verified.")
        });

        var harness = new TestCrucibleHarnessBuilder { BaseChatClient = client, TaskRepository = new InMemoryTaskRepository() }.Build();

        var result = await harness.RunStageAsync(NewContext(), CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(6, client.Calls);
        Assert.Contains("Implementation complete", result.Summary);
    }

    [Fact]
    public async Task TruncatedTurn_WhenTurnBudgetExhausted_FailsImmediately()
    {
        var client = new ScriptedChatClient((messages, call) => Text("Next, I will"));

        var harness = new TestCrucibleHarnessBuilder { BaseChatClient = client, TaskRepository = new InMemoryTaskRepository() }.Build();
        var ctx = NewContext();
        ctx.Profile.MaxTurns = 2;

        var result = await harness.RunStageAsync(ctx, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal(2, client.Calls);
        Assert.Contains("turn budget", result.Error ?? result.Summary ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("All done.", false)]
    [InlineData("Updated `Foo`", false)]
    [InlineData("Result is **good**", false)]
    [InlineData("```cs\ncode\n```\nDone.", false)]
    [InlineData("Here is the code:\n```csharp\nprivate const char Backtick = '`';\n```\nAll done.", false)]
    [InlineData("```cs\ncode", true)]
    [InlineData("Probe `abc", true)]
    [InlineData("   - \\", true)]
    [InlineData("3.", true)]
    [InlineData("Next, I will", true)]
    [InlineData("<tool_call>\n<function=bash>", true)]
    [InlineData("<tool_call>x</tool_call> done.", false)]
    public void IsTruncated_Text(string? text, bool expected)
        => Assert.Equal(expected, InterruptedGenerationDetector.IsTruncated(text));

    [Fact]
    public void IsTruncated_Message_ReasoningOnly_IsTruncated()
    {
        var msg = new ChatMessage(ChatRole.Assistant, new List<AIContent> { new TextReasoningContent("thinking") });
        Assert.True(InterruptedGenerationDetector.IsTruncated(msg));
    }

    [Fact]
    public void IsTruncated_Message_WithToolCall_IsNotTruncated()
    {
        var msg = new ChatMessage(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("1", "bash"), new TextReasoningContent("x") });
        Assert.False(InterruptedGenerationDetector.IsTruncated(msg));
    }
}
