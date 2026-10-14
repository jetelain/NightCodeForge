using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.PiRpc;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents.Tests;

public class PiRpcAgentHarnessTests
{
    private class TestActivityStreamer : IAgentActivityStreamer
    {
        public List<AgentActivityEvent> Events { get; } = new();

        public Task BroadcastAsync(AgentActivityEvent activityEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(activityEvent);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<AgentActivityEvent> SubscribeAsync(int? taskId = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var ev in Events)
            {
                if (!taskId.HasValue || ev.TaskId == taskId.Value) yield return ev;
            }
        }
    }

    [Fact]
    public async Task RunSessionStreamAsync_StreamsReasoningAndTools_AndCompletes()
    {
        var streamer = new TestActivityStreamer();
        var harness = new PiRpcAgentHarness(activityStreamer: streamer);

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 10, Title = "Pi Test", Description = "Implement feature" },
            StageId = "stage-2",
            Profile = new AgentProfile { Name = "PiAgent", ModelName = "qwen2.5-coder:7b" }
        };

        // Simulated stdout from Pi daemon
        var piOutput = new StringBuilder();
        piOutput.AppendLine("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"thinking_delta\",\"delta\":\"Analyzing code structure...\"}}");
        piOutput.AppendLine("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"Here is the plan.\"}}");
        piOutput.AppendLine("{\"type\":\"tool_execution_start\",\"toolName\":\"read\",\"args\":\"{\\\"path\\\":\\\"Program.cs\\\"}\"}");
        piOutput.AppendLine("{\"type\":\"tool_execution_end\",\"toolName\":\"read\",\"result\":\"class Program {}\"}");
        piOutput.AppendLine("{\"type\":\"usage\",\"prompt_tokens\":100,\"completion_tokens\":50}");
        piOutput.AppendLine("{\"type\":\"agent_settled\"}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(piOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-1", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("Here is the plan.", result.Summary);
        Assert.Equal(100, result.Metrics.PromptTokens);
        Assert.Equal(50, result.Metrics.CompletionTokens);

        // Verify sent commands
        var sentCommands = Encoding.UTF8.GetString(outStream.ToArray());
        Assert.Contains("\"type\":\"set_model\"", sentCommands);
        Assert.Contains("\"modelId\":\"qwen2.5-coder:7b\"", sentCommands);
        Assert.Contains("\"type\":\"prompt\"", sentCommands);

        // Verify streamed activity events
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.ThinkingDelta && e.Content == "Analyzing code structure...");
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.MessageDelta && e.Content == "Here is the plan.");
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.ToolStarted && e.Content.Contains("read"));
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.ToolCompleted && e.Content.Contains("class Program"));
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.StageCompleted);
    }

    [Fact]
    public async Task RunSessionStreamAsync_WhenExtensionUiRequest_BroadcastsInterruptedAndReturnsWaitingForHuman()
    {
        var streamer = new TestActivityStreamer();
        var harness = new PiRpcAgentHarness(activityStreamer: streamer);

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 11, Title = "Pi Question", Description = "Ask question" },
            StageId = "stage-1",
            Profile = new AgentProfile { Name = "PiAgent" }
        };

        var piOutput = new StringBuilder();
        piOutput.AppendLine("{\"type\":\"extension_ui_request\",\"id\":\"req-1\",\"method\":\"select\",\"title\":\"Which database driver?\",\"options\":[\"Npgsql\",\"Microsoft.Data.Sqlite\"]}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(piOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-2", sw);

        Assert.Equal(AgentRunStatus.WaitingForHuman, result.Status);
        Assert.NotNull(result.PendingQuestion);
        Assert.Contains("Which database driver?", result.PendingQuestion);
        Assert.Contains("Npgsql", result.PendingQuestion);
        Assert.Contains("Microsoft.Data.Sqlite", result.PendingQuestion);

        // Verify streamed interruption
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.Interrupted && e.Content.Contains("Which database driver?"));
    }

    [Fact]
    public async Task RunSessionStreamAsync_IncludesPendingHumanAnswer_WhenResuming()
    {
        var harness = new PiRpcAgentHarness();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 12, Title = "Resume Test", Description = "Resume execution" },
            StageId = "stage-2",
            Profile = new AgentProfile { Name = "PiAgent" },
            PendingHumanAnswer = "Use SQLite for tests"
        };

        var piOutput = new StringBuilder();
        piOutput.AppendLine("{\"type\":\"agent_settled\"}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(piOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-3", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        var sentCommands = Encoding.UTF8.GetString(outStream.ToArray());
        Assert.Contains("[Human Answer]: Use SQLite for tests", sentCommands);
    }

    [Fact]
    public async Task RunSessionStreamAsync_ResolvesUnixWorkspacePath_WithWorkspacePathResolver()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pirpc_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var pathResolver = new TestWorkspacePathResolver(tempDir);
            var harness = new PiRpcAgentHarness(pathResolver: pathResolver);

            var context = new AgentSessionContext
            {
                ForgeTask = new ForgeTask { Id = 33, Title = "Pi Path Test", Description = "Test path resolution" },
                StageId = "stage-1",
                Profile = new AgentProfile { Name = "PiAgent" },
                Workspace = new Workspace { Id = 7, RepositoryPath = tempDir },
                WorkspacePath = "/home/wsluser/.nightcodeforge/wks/7"
            };

            var piOutput = new StringBuilder();
            piOutput.AppendLine("{\"type\":\"agent_settled\"}");

            using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(piOutput.ToString()));
            using var reader = new StreamReader(inStream);
            using var outStream = new MemoryStream();
            using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

            var sw = Stopwatch.StartNew();
            var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-6", sw);

            Assert.Equal(AgentRunStatus.Completed, result.Status);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    private class TestWorkspacePathResolver : IWorkspacePathResolver
    {
        private readonly string _rootDir;

        public TestWorkspacePathResolver(string rootDir)
        {
            _rootDir = rootDir;
        }

        public string GetAgentWorkingClonePath(int workspaceId) => _rootDir;
        public string GetHostRemoteUrl(string repositoryPath) => repositoryPath;
        public string ResolveCruciblePath(int workspaceId, string relativeOrContainerPath)
        {
            var rel = (relativeOrContainerPath ?? string.Empty).Replace('\\', '/').TrimStart('/');
            if (rel.StartsWith("workspace/", StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring("workspace/".Length);
            return string.IsNullOrEmpty(rel) ? _rootDir : Path.Combine(_rootDir, rel);
        }
        public string GetWslDistributionName() => "Ubuntu";
        public string GetWslUserName() => "user";
    }
}
