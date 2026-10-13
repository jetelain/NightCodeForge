using System.Diagnostics;
using System.Text;
using NightCodeForge.Agents.Acp;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents.Tests;

public class AcpAgentHarnessTests
{
    private class TestActivityStreamer : IAgentActivityStreamer
    {
        public List<AgentActivityEvent> Events { get; } = new();

        public Task BroadcastAsync(AgentActivityEvent activityEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(activityEvent);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<AgentActivityEvent> SubscribeAsync(int? issueId = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var ev in Events)
            {
                if (!issueId.HasValue || ev.IssueId == issueId.Value) yield return ev;
            }
        }
    }

    private class TestExecutionEnvironment : IExecutionEnvironment
    {
        public ExecutionTier Tier => ExecutionTier.BareHost;

        public Task<CommandResult> ExecuteAsync(string command, string? workingDirectory = null, int? timeoutSeconds = null, IReadOnlyDictionary<string, string>? environmentVariables = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CommandResult(0, $"Executed: {command}", "", TimeSpan.FromMilliseconds(50), false));
        }
    }

    [Fact]
    public async Task RunSessionStreamAsync_StreamsThoughtMessageAndTools_AndCompletes()
    {
        var streamer = new TestActivityStreamer();
        var harness = new AcpAgentHarness(activityStreamer: streamer);

        var context = new AgentSessionContext
        {
            Issue = new Issue { Id = 20, Title = "ACP Test", Description = "Implement feature" },
            StageId = "stage-2",
            Profile = new AgentProfile { Name = "AcpAgent" }
        };

        // Simulated ACP agent responses
        var acpOutput = new StringBuilder();
        // 1. Initialize response (negotiates v2)
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}");
        // 2. session/update thought chunk
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\",\"update\":{\"sessionUpdate\":\"agent_thought_chunk\",\"content\":{\"text\":\"Thinking about design...\"}}}}");
        // 3. session/update message chunk
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"text\":\"Solution proposed.\"}}}}");
        // 4. session/update tool call
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\",\"update\":{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"tc-1\",\"title\":\"read\",\"rawInput\":\"{\\\"file\\\":\\\"main.rs\\\"}\"}}}");
        // 5. session/update tool call update
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\",\"update\":{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"tc-1\",\"status\":\"completed\",\"rawOutput\":\"fn main() {}\"}}}");
        // 6. prompt-1 response
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-1", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("Solution proposed.", result.Summary);

        // Verify sent commands
        var sentCommands = Encoding.UTF8.GetString(outStream.ToArray());
        Assert.Contains("\"method\":\"initialize\"", sentCommands);
        Assert.Contains("\"method\":\"session/new\"", sentCommands);
        Assert.Contains("\"method\":\"session/prompt\"", sentCommands);

        // Verify streamed activity events
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.ThinkingDelta && e.Content == "Thinking about design...");
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.MessageDelta && e.Content == "Solution proposed.");
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.ToolStarted && e.Content.Contains("read"));
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.ToolCompleted && e.Content.Contains("fn main()"));
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.StageCompleted);
    }

    [Fact]
    public async Task RunSessionStreamAsync_HandlesTerminalExecution()
    {
        var execEnv = new TestExecutionEnvironment();
        var harness = new AcpAgentHarness(executionEnv: execEnv);

        var context = new AgentSessionContext
        {
            Issue = new Issue { Id = 21, Title = "Terminal Test", Description = "Run command" },
            StageId = "stage-2",
            Profile = new AgentProfile { Name = "AcpAgent" }
        };

        var acpOutput = new StringBuilder();
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":1}}");
        // Agent calls terminal/create
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"term-req-1\",\"method\":\"terminal/create\",\"params\":{\"command\":\"dotnet test\"}}");
        // Complete prompt
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-2", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        var sentCommands = Encoding.UTF8.GetString(outStream.ToArray());
        Assert.Contains("\"id\":\"term-req-1\"", sentCommands);
        Assert.Contains("Executed: dotnet test", sentCommands);
    }

    [Fact]
    public async Task RunSessionStreamAsync_WhenRequestPermission_BroadcastsInterruptedAndReturnsWaitingForHuman()
    {
        var streamer = new TestActivityStreamer();
        var harness = new AcpAgentHarness(activityStreamer: streamer);

        var context = new AgentSessionContext
        {
            Issue = new Issue { Id = 22, Title = "Perm Test", Description = "Need permission" },
            StageId = "stage-1",
            Profile = new AgentProfile { Name = "AcpAgent" }
        };

        var acpOutput = new StringBuilder();
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}");
        // Agent requests permission
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"perm-1\",\"method\":\"session/request_permission\",\"params\":{\"prompt\":\"Allow deleting old migrations?\",\"options\":[{\"name\":\"Yes\"},{\"name\":\"No\"}]}}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-3", sw);

        Assert.Equal(AgentRunStatus.WaitingForHuman, result.Status);
        Assert.NotNull(result.PendingQuestion);
        Assert.Contains("Allow deleting old migrations?", result.PendingQuestion);
        Assert.Contains("Yes", result.PendingQuestion);
        Assert.Contains("No", result.PendingQuestion);

        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.Interrupted && e.Content.Contains("Allow deleting old migrations?"));
    }

    [Fact]
    public async Task RunSessionStreamAsync_ResumesWithSessionResume_WhenV2Negotiated()
    {
        var harness = new AcpAgentHarness();

        var context = new AgentSessionContext
        {
            Issue = new Issue { Id = 23, Title = "Resume Test", Description = "Resume" },
            StageId = "stage-2",
            Profile = new AgentProfile { Name = "AcpAgent" },
            SerializedStateJson = "saved-session-99",
            PendingHumanAnswer = "User clicked Yes"
        };

        var acpOutput = new StringBuilder();
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-4", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        var sent = Encoding.UTF8.GetString(outStream.ToArray());
        Assert.Contains("\"method\":\"session/resume\"", sent);
        Assert.Contains("\"sessionId\":\"saved-session-99\"", sent);
        Assert.Contains("[Human Answer]: User clicked Yes", sent);
    }
}
