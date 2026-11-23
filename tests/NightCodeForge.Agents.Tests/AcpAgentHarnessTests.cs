using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Acp;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Infrastructure.Execution;

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

        public async IAsyncEnumerable<AgentActivityEvent> SubscribeAsync(int? taskId = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var ev in Events)
            {
                if (!taskId.HasValue || ev.TaskId == taskId.Value) yield return ev;
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
        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder { ActivityStreamer = streamer }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 20, Title = "ACP Test", Description = "Implement feature" },
            StageId = "stage-2",
            Profile = new AgentProfile { Name = "AcpAgent" },
            Workspace = new Workspace { Id = 1, RepositoryPath = "C:\\test" }
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
        using var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true };

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
        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder
        {
            EnvProvider = new NightCodeForge.Core.Execution.SingleExecutionEnvironmentProvider(execEnv)
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 21, Title = "Terminal Test", Description = "Run command" },
            StageId = "stage-2",
            Profile = new AgentProfile { Name = "AcpAgent" },
            Workspace = new Workspace { Id = 1, RepositoryPath = "C:\\test" }
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
        using var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true };

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
        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder { ActivityStreamer = streamer }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 22, Title = "Perm Test", Description = "Need permission" },
            StageId = "stage-1",
            Profile = new AgentProfile { Name = "AcpAgent" },
            Workspace = new Workspace { Id = 1, RepositoryPath = "C:\\test" }
        };

        var acpOutput = new StringBuilder();
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}");
        // Agent requests permission
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"perm-1\",\"method\":\"session/request_permission\",\"params\":{\"prompt\":\"Allow deleting old migrations?\",\"options\":[{\"name\":\"Yes\"},{\"name\":\"No\"}]}}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-3", sw);

        Assert.Equal(AgentRunStatus.WaitingForHuman, result.Status);
        Assert.NotNull(result.PendingQuestions);
        Assert.Single(result.PendingQuestions);
        var q = result.PendingQuestions[0];
        Assert.Contains("Allow deleting old migrations?", q.Prompt);
        Assert.Contains("Yes", q.OptionsJson ?? "");
        Assert.Contains("No", q.OptionsJson ?? "");

        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.Interrupted && e.Content.Contains("Allow deleting old migrations?"));
    }

    [Fact]
    public async Task RunSessionStreamAsync_ResumesWithSessionResume_WhenV2Negotiated()
    {
        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder().Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 23, Title = "Resume Test", Description = "Resume" },
            StageId = "stage-2",
            Profile = new AgentProfile { Name = "AcpAgent" },
            Workspace = new Workspace { Id = 1, RepositoryPath = "C:\\test" },
            SerializedStateJson = "saved-session-99",
            PendingHumanAnswer = "User clicked Yes"
        };

        var acpOutput = new StringBuilder();
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-4", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        var sent = Encoding.UTF8.GetString(outStream.ToArray());
        Assert.Contains("\"method\":\"session/resume\"", sent);
        Assert.Contains("\"sessionId\":\"saved-session-99\"", sent);
        Assert.Contains("[Human Answer]: User clicked Yes", sent);
    }

    [Fact]
    public async Task RunSessionStreamAsync_HandlesFsReadAndWrite_WithWorkspacePathResolver()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "acp_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var testFilePath = Path.Combine(tempDir, "sample.txt");
            await File.WriteAllTextAsync(testFilePath, "Hello from ACP File");

            var pathResolver = new TestWorkspacePathResolver(tempDir);
            var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder { PathResolver = pathResolver }.Build();

            var context = new AgentSessionContext
            {
                ForgeTask = new ForgeTask { Id = 24, Title = "FS Test", Description = "Test file tools" },
                StageId = "stage-2",
                Profile = new AgentProfile { Name = "AcpAgent" },
                Workspace = new Workspace { Id = 42, RepositoryPath = tempDir }
            };

            var acpOutput = new StringBuilder();
            acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}");
            // 1. Agent reads file
            acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"read-1\",\"method\":\"fs/read_text_file\",\"params\":{\"path\":\"/workspace/sample.txt\"}}");
            // 2. Agent writes file
            acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"write-1\",\"method\":\"fs/write_text_file\",\"params\":{\"path\":\"/workspace/written.txt\",\"content\":\"Generated by ACP\"}}");
            // 3. Stop
            acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}");

            using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput.ToString()));
            using var reader = new StreamReader(inStream);
            using var outStream = new MemoryStream();
            using var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true };

            var sw = Stopwatch.StartNew();
            var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-5", sw);

            Assert.Equal(AgentRunStatus.Completed, result.Status);
            var sent = Encoding.UTF8.GetString(outStream.ToArray());

            Assert.Contains("\"id\":\"read-1\"", sent);
            Assert.Contains("Hello from ACP File", sent);
            Assert.Contains("\"id\":\"write-1\"", sent);

            var writtenPath = Path.Combine(tempDir, "written.txt");
            Assert.True(File.Exists(writtenPath));
            Assert.Contains("Generated by ACP", await File.ReadAllTextAsync(writtenPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Theory]
    [InlineData("{\"executablePath\":\"custom-acp\",\"arguments\":\"--flag1 --flag2\"}", "custom-acp", "--flag1 --flag2")]
    [InlineData("{\"executable\":\"acp-bin\",\"args\":\"--v2\"}", "acp-bin", "--v2")]
    [InlineData("{\"command\":\"my-cmd\",\"args\":\"--custom\"}", "my-cmd", "--custom")]
    [InlineData("", "agy", "acp --dangerously-skip-permissions")]
    [InlineData(null, "agy", "acp --dangerously-skip-permissions")]
    [InlineData("{ malformed json", "agy", "acp --dangerously-skip-permissions")]
    [InlineData("{\"arguments\":[\"--array-not-string\"]}", "agy", "acp --dangerously-skip-permissions")]
    [InlineData("{\"args\":123}", "agy", "acp --dangerously-skip-permissions")]
    public async Task RunStageAsync_ParsesHarnessConfigurationJson_OrFallsBackToDefaults(
        string? harnessConfigJson,
        string expectedExe,
        string expectedArgs)
    {
        var streamer = new TestActivityStreamer();
        var acpOutput = new StringBuilder();
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":1}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"session-1\",\"result\":{\"sessionId\":\"s-cfg\"}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}");

        using var session = new FakeProcessStreamSession(acpOutput.ToString());
        ProcessStartSpec? capturedSpec = null;
        bool stageStartedBeforeProcess = false;

        var mockRunner = new Moq.Mock<IProcessRunner>();
        mockRunner
            .Setup(r => r.StartSession(Moq.It.IsAny<ProcessStartSpec>()))
            .Callback<ProcessStartSpec>(spec =>
            {
                capturedSpec = spec;
                stageStartedBeforeProcess = streamer.Events.Any(e => e.Kind == AgentActivityKind.StageStarted);
            })
            .Returns(session);

        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder
        {
            ActivityStreamer = streamer,
            ProcessRunner = mockRunner.Object
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 101, Title = "ACP Config Test", Description = "Test config parsing" },
            StageId = "stage-2",
            Profile = new AgentProfile { Name = "AcpAgent", HarnessConfigurationJson = harnessConfigJson },
            Workspace = new Workspace { Id = 1, RepositoryPath = Directory.GetCurrentDirectory(), ExecutionMode = ExecutionMode.BareHost },
            WorkspacePath = Directory.GetCurrentDirectory()
        };

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.True(stageStartedBeforeProcess);
        Assert.NotNull(capturedSpec);
        Assert.Equal(expectedExe, capturedSpec.FileName);
        Assert.Equal(expectedArgs, capturedSpec.Arguments);
        Assert.True(session.IsDisposed);
    }

    [Theory]
    [InlineData("/home/wsluser/.nightcodeforge/wks/15/task-202")]
    [InlineData("~/.nightcodeforge/wks/15/task-202")]
    public async Task RunStageAsync_ResolvesUnixWorkingDirectory_ViaWorkspacePathResolver_AndDisposesSessionOnFailure(string unixWorkspacePath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "acp_unix_wd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var pathResolver = new TestWorkspacePathResolver(tempDir);
            using var session = new FakeProcessStreamSession(
                stdoutContent: "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}\n",
                throwOnSecondLine: new IOException("Process stream pipe broken"));

            ProcessStartSpec? capturedSpec = null;
            var mockRunner = new Moq.Mock<IProcessRunner>();
            mockRunner
                .Setup(r => r.StartSession(Moq.It.IsAny<ProcessStartSpec>()))
                .Callback<ProcessStartSpec>(spec => capturedSpec = spec)
                .Returns(session);

            var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder
            {
                PathResolver = pathResolver,
                ProcessRunner = mockRunner.Object
            }.Build();

            var context = new AgentSessionContext
            {
                ForgeTask = new ForgeTask { Id = 202, Title = "Unix Path Test", Description = "Verify path resolution" },
                StageId = "stage-2",
                Profile = new AgentProfile { Name = "AcpAgent" },
                Workspace = new Workspace { Id = 15, RepositoryPath = tempDir, ExecutionMode = ExecutionMode.BareHost },
                WorkspacePath = unixWorkspacePath
            };

            await Assert.ThrowsAsync<IOException>(() => harness.RunStageAsync(context));

            Assert.NotNull(capturedSpec);
            Assert.Equal(tempDir, capturedSpec.WorkingDirectory);
            Assert.True(session.IsDisposed);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RunSessionStreamAsync_WhenCancelledMidStream_WritesSessionCancelAndRethrowsOperationCanceledException()
    {
        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder().Build();
        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 203, Title = "Cancel Test", Description = "Cancel mid-stream" },
            StageId = "stage-2",
            Profile = new AgentProfile { Name = "AcpAgent" },
            Workspace = new Workspace { Id = 1, RepositoryPath = Directory.GetCurrentDirectory() }
        };

        using var cts = new CancellationTokenSource();
        using var inStream = new CancellingAfterInitStream(
            "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}\n",
            cts);
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.RunSessionStreamAsync(writer, reader, context, "sess-cancel", sw, cts.Token));

        var sentCommands = Encoding.UTF8.GetString(outStream.ToArray());
        Assert.Contains("{\"jsonrpc\":\"2.0\",\"method\":\"session/cancel\"}", sentCommands);
    }

    [Fact]
    public async Task RunSessionStreamAsync_WhenUnexpectedExceptionOccurs_BroadcastsError_SavesFailedTranscript_AndRethrows()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "acp_err_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var streamer = new TestActivityStreamer();
            var transcriptService = new NightCodeForge.Agents.Transcripts.SessionTranscriptService(Path.Combine(tempDir, "docs", "transcripts"));
            var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder
            {
                ActivityStreamer = streamer,
                TranscriptService = transcriptService
            }.Build();

            var context = new AgentSessionContext
            {
                ForgeTask = new ForgeTask { Id = 204, Title = "Crash Test", Description = "Simulate stream crash" },
                StageId = "stage-2",
                Profile = new AgentProfile { Name = "AcpAgent", ModelName = "gemini-2.5-pro" },
                Workspace = new Workspace { Id = 1, RepositoryPath = tempDir },
                WorkspacePath = tempDir
            };

            using var session = new FakeProcessStreamSession(
                stdoutContent: "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}\n",
                throwOnSecondLine: new InvalidOperationException("Unexpected ACP transport failure"));

            var sw = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.RunSessionStreamAsync(session.StandardInput, session.StandardOutput, context, "sess-err", sw));

            Assert.Equal("Unexpected ACP transport failure", ex.Message);
            Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.Error && e.Content.Contains("Unexpected ACP transport failure"));

            var transcriptDir = Path.Combine(tempDir, "docs", "transcripts");
            Assert.True(Directory.Exists(transcriptDir));
            var transcriptFiles = Directory.GetFiles(transcriptDir, "*-task-204-stage-2-failed.md");
            var transcriptFile = Assert.Single(transcriptFiles);
            var transcriptText = await File.ReadAllTextAsync(transcriptFile);
            Assert.Contains("status: Failed", transcriptText);
            Assert.Contains("Unexpected ACP transport failure", transcriptText);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RunSessionStreamAsync_HandlesTerminalOutputWaitForExitReleaseAndKill()
    {
        var mockEnv = new Moq.Mock<IExecutionEnvironment>();
        mockEnv
            .Setup(e => e.ExecuteAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<string?>(), Moq.It.IsAny<int?>(), Moq.It.IsAny<IReadOnlyDictionary<string, string>?>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync((string cmd, string? _, int? _, IReadOnlyDictionary<string, string>? _, CancellationToken _) =>
                cmd == "cmd-create"
                    ? new CommandResult(7, "create-stdout", "create-stderr", TimeSpan.FromMilliseconds(10), false)
                    : new CommandResult(9, "run-stdout", "", TimeSpan.FromMilliseconds(10), false));

        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder
        {
            EnvProvider = new NightCodeForge.Core.Execution.SingleExecutionEnvironmentProvider(mockEnv.Object)
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 205, Title = "Terminal RPC Lifecycle", Description = "Test terminal RPCs" },
            StageId = "stage-2",
            Profile = new AgentProfile { Name = "AcpAgent" },
            Workspace = new Workspace { Id = 1, RepositoryPath = Directory.GetCurrentDirectory() }
        };

        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true };

        string ExtractTerminalId(string requestId)
        {
            var written = Encoding.UTF8.GetString(outStream.ToArray());
            foreach (var line in written.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(line);
                if (node?["id"]?.ToString() == requestId)
                {
                    return node["result"]?["terminalId"]?.ToString() ?? string.Empty;
                }
            }
            return string.Empty;
        }

        var steps = new Func<string>[]
        {
            () => "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}",
            () => "{\"jsonrpc\":\"2.0\",\"id\":\"t-create\",\"method\":\"terminal/create\",\"params\":{\"command\":\"cmd-create\"}}",
            () => $"{{\"jsonrpc\":\"2.0\",\"id\":\"t-out-1\",\"method\":\"terminal/output\",\"params\":{{\"terminalId\":\"{ExtractTerminalId("t-create")}\"}}}}",
            () => $"{{\"jsonrpc\":\"2.0\",\"id\":\"t-wait-1\",\"method\":\"terminal/wait_for_exit\",\"params\":{{\"terminalId\":\"{ExtractTerminalId("t-create")}\"}}}}",
            () => $"{{\"jsonrpc\":\"2.0\",\"id\":\"t-rel-1\",\"method\":\"terminal/release\",\"params\":{{\"terminalId\":\"{ExtractTerminalId("t-create")}\"}}}}",
            () => $"{{\"jsonrpc\":\"2.0\",\"id\":\"t-out-after-rel\",\"method\":\"terminal/output\",\"params\":{{\"terminalId\":\"{ExtractTerminalId("t-create")}\"}}}}",
            () => $"{{\"jsonrpc\":\"2.0\",\"id\":\"t-wait-after-rel\",\"method\":\"terminal/wait_for_exit\",\"params\":{{\"terminalId\":\"{ExtractTerminalId("t-create")}\"}}}}",
            () => "{\"jsonrpc\":\"2.0\",\"id\":\"t-run\",\"method\":\"terminal/run\",\"params\":{\"command\":\"cmd-run\"}}",
            () => $"{{\"jsonrpc\":\"2.0\",\"id\":\"t-kill-1\",\"method\":\"terminal/kill\",\"params\":{{\"terminalId\":\"{ExtractTerminalId("t-run")}\"}}}}",
            () => $"{{\"jsonrpc\":\"2.0\",\"id\":\"t-out-after-kill\",\"method\":\"terminal/output\",\"params\":{{\"terminalId\":\"{ExtractTerminalId("t-run")}\"}}}}",
            () => "{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}"
        };

        using var inStream = new DynamicLineProducerStream(steps);
        using var reader = new StreamReader(inStream);

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-term-lifecycle", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var responses = Encoding.UTF8.GetString(outStream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => System.Text.Json.Nodes.JsonNode.Parse(l)!)
            .Where(n => n["id"] != null && n["result"] != null)
            .ToDictionary(n => n["id"]!.ToString(), n => n["result"]!);

        var expectedCombinedOutput = $"create-stdout{Environment.NewLine}create-stderr";
        Assert.Equal(expectedCombinedOutput, responses["t-out-1"]["output"]?.ToString());
        Assert.Equal(7, responses["t-wait-1"]["exitCode"]?.GetValue<int>());
        Assert.Equal(string.Empty, responses["t-out-after-rel"]["output"]?.ToString());
        Assert.Equal(0, responses["t-wait-after-rel"]["exitCode"]?.GetValue<int>());
        Assert.Equal(string.Empty, responses["t-out-after-kill"]["output"]?.ToString());
    }

    [Fact]
    public async Task RunSessionStreamAsync_FsReadTextFile_SlicesBy1BasedOffsetAndLimit_AndReturnsRpcErrorForMissingFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "acp_fs_slice_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "lines.txt");
            await File.WriteAllLinesAsync(filePath, new[] { "Line 1", "Line 2", "Line 3", "Line 4", "Line 5" });

            var pathResolver = new TestWorkspacePathResolver(tempDir);
            var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder { PathResolver = pathResolver }.Build();

            var context = new AgentSessionContext
            {
                ForgeTask = new ForgeTask { Id = 206, Title = "FS Slice Test", Description = "Test offset and limit" },
                StageId = "stage-2",
                Profile = new AgentProfile { Name = "AcpAgent" },
                Workspace = new Workspace { Id = 42, RepositoryPath = tempDir }
            };

            var acpOutput = new StringBuilder();
            acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}");
            acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"read-slice\",\"method\":\"fs/read_text_file\",\"params\":{\"path\":\"/workspace/lines.txt\",\"offset\":2,\"limit\":2}}");
            acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"read-missing\",\"method\":\"fs/read_text_file\",\"params\":{\"path\":\"/workspace/does-not-exist.txt\"}}");
            acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}");

            using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput.ToString()));
            using var reader = new StreamReader(inStream);
            using var outStream = new MemoryStream();
            using var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true };

            var sw = Stopwatch.StartNew();
            var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-fs-slice", sw);

            Assert.Equal(AgentRunStatus.Completed, result.Status);

            var messages = Encoding.UTF8.GetString(outStream.ToArray())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => System.Text.Json.Nodes.JsonNode.Parse(l)!)
                .Where(n => n["id"] != null && n["method"] == null)
                .ToDictionary(n => n["id"]!.ToString(), n => n);

            var expectedSlice = $"Line 2{Environment.NewLine}Line 3";
            Assert.Equal(expectedSlice, messages["read-slice"]["result"]?["content"]?.ToString());

            var missingErr = messages["read-missing"]["error"];
            Assert.NotNull(missingErr);
            Assert.Equal(-32602, missingErr["code"]?.GetValue<int>());
            Assert.Contains("does-not-exist.txt", missingErr["message"]?.ToString() ?? string.Empty);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RunSessionStreamAsync_FsWriteTextFile_InvokesEditorConfigServicesAndWritesFormattedBytes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "acp_fs_write_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var pathResolver = new TestWorkspacePathResolver(tempDir);
            var expectedSettings = new NightCodeForge.Agents.Tools.EditorConfig.EditorConfigSettings();
            var formattedBytes = Encoding.UTF8.GetBytes("FORMATTED_BY_EDITORCONFIG\n");

            var mockConfigService = new Moq.Mock<NightCodeForge.Agents.Tools.EditorConfig.IEditorConfigService>();
            mockConfigService
                .Setup(s => s.GetSettingsForPathAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedSettings);

            var mockApplier = new Moq.Mock<NightCodeForge.Agents.Tools.EditorConfig.IEditorConfigFileApplier>();
            mockApplier
                .Setup(a => a.ApplyFormattingAndEncode("raw content", expectedSettings, null, Moq.It.IsAny<string>()))
                .Returns(formattedBytes);

            var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder
            {
                PathResolver = pathResolver,
                EditorConfigService = mockConfigService.Object,
                EditorConfigFileApplier = mockApplier.Object
            }.Build();

            var context = new AgentSessionContext
            {
                ForgeTask = new ForgeTask { Id = 207, Title = "FS Write EditorConfig Test", Description = "Test EditorConfig application" },
                StageId = "stage-2",
                Profile = new AgentProfile { Name = "AcpAgent" },
                Workspace = new Workspace { Id = 42, RepositoryPath = tempDir }
            };

            var acpOutput = new StringBuilder();
            acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}");
            acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"write-fmt\",\"method\":\"fs/write_text_file\",\"params\":{\"path\":\"/workspace/sub/formatted.cs\",\"content\":\"raw content\"}}");
            acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}");

            using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput.ToString()));
            using var reader = new StreamReader(inStream);
            using var outStream = new MemoryStream();
            using var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true };

            var sw = Stopwatch.StartNew();
            var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-fs-write", sw);

            Assert.Equal(AgentRunStatus.Completed, result.Status);

            var expectedPath = pathResolver.ResolveCruciblePath(42, "/workspace/sub/formatted.cs");
            mockConfigService.Verify(s => s.GetSettingsForPathAsync(expectedPath, Moq.It.IsAny<CancellationToken>()), Moq.Times.Once);
            mockApplier.Verify(a => a.ApplyFormattingAndEncode("raw content", expectedSettings, null, expectedPath), Moq.Times.Once);
            Assert.True(File.Exists(expectedPath));
            Assert.Equal(formattedBytes, await File.ReadAllBytesAsync(expectedPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RunSessionStreamAsync_AntigravityWireSequence_SetsSessionIdConfigOptionsCorrelatesToolsExtractsUsageAndPersistsDb()
    {
        var streamer = new TestActivityStreamer();
        var taskRepo = new InMemoryTaskRepository();
        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Title = "ACP Wire Sequence",
            Description = "Implement feature X",
            WorkspaceId = 1
        });

        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder
        {
            ActivityStreamer = streamer,
            TaskRepository = taskRepo
        }.Build();

        var scope = SandboxScopeDescriptor.ForWorkspace(
            new Workspace { Id = 1, RepositoryPath = Directory.GetCurrentDirectory(), ExecutionMode = ExecutionMode.ContainerSandbox },
            SandboxSecurityTier.Standard,
            taskId: task.Id,
            mountTable: SandboxMountTable.ForWorkspace(Directory.GetCurrentDirectory(), "/wks/1/task-1", false, task.Id),
            defaultWorkingDirectory: $"/workspace/task-{task.Id}");

        var context = new AgentSessionContext
        {
            ForgeTask = task,
            StageId = StandardStageIds.Implementation,
            Profile = new AgentProfile
            {
                Id = StandardStageIds.Implementation,
                Name = "AgyImpl",
                HarnessId = StandardHarnessIds.Acp,
                ModelName = "gemini-4-argon",
                ThinkingEffort = ThinkingEffort.High,
                Mode = AgentMode.Full,
                SystemPrompt = "Be concise and thorough."
            },
            Workspace = new Workspace { Id = 1, RepositoryPath = Directory.GetCurrentDirectory(), ExecutionMode = ExecutionMode.ContainerSandbox },
            SandboxScope = scope
        };

        var acpOutput = new StringBuilder();
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":1,\"agentInfo\":{\"name\":\"Antigravity ACP Server\",\"version\":\"1.5.3\"}}}");
        // Interleaved session/update before session-1 completes
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"srv-agy-42\",\"update\":{\"sessionUpdate\":\"available_commands_update\",\"commands\":[]}}}");
        // session/new response with server-assigned sessionId and configOptions
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"session-1\",\"result\":{\"sessionId\":\"srv-agy-42\",\"configOptions\":[{\"id\":\"model\",\"options\":[{\"value\":\"gemini-4-argon\"}]},{\"id\":\"thought_level\",\"options\":[{\"value\":\"low\"},{\"value\":\"medium\"},{\"value\":\"high\"}]},{\"id\":\"mode\",\"options\":[{\"value\":\"default\"},{\"value\":\"accept-edits\"},{\"value\":\"plan\"}]}]}}");
        // cfg-* responses ignored cleanly
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"cfg-opt-1\",\"result\":{}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"cfg-opt-2\",\"result\":{}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"cfg-opt-mode\",\"result\":{}}");
        // Agent thinking, tool call (with name + args), tool_call_update, final message, and usage
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"srv-agy-42\",\"update\":{\"sessionUpdate\":\"agent_thought_chunk\",\"content\":{\"text\":\"Planning build check...\"}}}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"srv-agy-42\",\"update\":{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"call-99\",\"name\":\"run_command\",\"args\":{\"CommandLine\":\"dotnet test\"}}}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"srv-agy-42\",\"update\":{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"call-99\",\"status\":\"completed\",\"rawOutput\":\"Passed: 12\"}}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"srv-agy-42\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"text\":\"Implemented feature X and all tests pass.\"}}}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\",\"usage\":{\"inputTokens\":1200,\"cachedInputTokens\":400,\"outputTokens\":300,\"thoughtTokens\":50,\"totalTokens\":1550}}}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "local-sess-1", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("srv-agy-42", result.UpdatedState);
        Assert.Equal("Implemented feature X and all tests pass.", result.Summary);
        Assert.Equal(1200, result.Metrics.PromptTokens);
        Assert.Equal(350, result.Metrics.CompletionTokens);
        Assert.Equal(400, result.Metrics.CachedInputTokens);
        Assert.Equal(1550, result.Metrics.TotalTokens);

        var sentLines = Encoding.UTF8.GetString(outStream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonNode.Parse(l.TrimStart('\uFEFF'))!)
            .ToList();

        var initReq = sentLines.First(n => n["id"]?.ToString() == "init-1");
        Assert.Equal(1, initReq["params"]?["protocolVersion"]?.GetValue<int>());
        Assert.True(initReq["params"]?["clientCapabilities"]?["terminal"]?.GetValue<bool>());

        var sessionReq = sentLines.First(n => n["id"]?.ToString() == "session-1");
        Assert.Equal($"/workspace/task-{task.Id}", sessionReq["params"]?["cwd"]?.ToString());
        Assert.Equal("Be concise and thorough.", sessionReq["params"]?["_meta"]?["antigravity"]?["systemPrompt"]?["append"]?.ToString());

        var modelCfg = sentLines.First(n => n["id"]?.ToString() == "cfg-opt-1");
        Assert.Equal("session/set_config_option", modelCfg["method"]?.ToString());
        Assert.Equal("srv-agy-42", modelCfg["params"]?["sessionId"]?.ToString());
        Assert.Equal("model", modelCfg["params"]?["configId"]?.ToString());
        Assert.Equal("gemini-4-argon", modelCfg["params"]?["value"]?.ToString());

        var thoughtCfg = sentLines.First(n => n["id"]?.ToString() == "cfg-opt-2");
        Assert.Equal("thought_level", thoughtCfg["params"]?["configId"]?.ToString());
        Assert.Equal("high", thoughtCfg["params"]?["value"]?.ToString());

        var modeCfg = sentLines.First(n => n["id"]?.ToString() == "cfg-opt-mode");
        Assert.Equal("mode", modeCfg["params"]?["configId"]?.ToString());
        Assert.Equal("accept-edits", modeCfg["params"]?["value"]?.ToString());

        var promptReq = sentLines.First(n => n["id"]?.ToString() == "prompt-1");
        Assert.Equal("srv-agy-42", promptReq["params"]?["sessionId"]?.ToString());
        Assert.Equal("text", promptReq["params"]?["prompt"]?[0]?["type"]?.ToString());
        Assert.Contains($"Current Working Directory (PWD): /workspace/task-{task.Id}", promptReq["params"]?["prompt"]?[0]?["text"]?.ToString());

        // Tool correlation metadata
        var toolStart = Assert.Single(streamer.Events, e => e.Kind == AgentActivityKind.ToolStarted);
        Assert.Equal("run_command", toolStart.Metadata?[AgentActivityMetadataKeys.ToolName]?.ToString());

        var toolEnd = Assert.Single(streamer.Events, e => e.Kind == AgentActivityKind.ToolCompleted);
        Assert.Equal("run_command", toolEnd.Metadata?[AgentActivityMetadataKeys.ToolName]?.ToString());
        Assert.StartsWith("run_command: completed:", toolEnd.Content);

        // SQLite Conversation & Turns persisted via HarnessActivityRecorder
        var convs = await taskRepo.GetConversationsAsync(task.Id);
        var conv = Assert.Single(convs);
        Assert.Equal(StandardHarnessIds.Acp, conv.HarnessId);
        Assert.Equal(StageExecutionStatus.Completed, conv.Status);
        Assert.Equal(1200, conv.InputTokens);
        Assert.Equal(350, conv.OutputTokens);
        Assert.Equal(400, conv.CachedInputTokens);
        Assert.Null(conv.Cost);
        Assert.False(string.IsNullOrWhiteSpace(conv.TranscriptFileName));

        var loadedConv = await taskRepo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(loadedConv);
        Assert.Contains(loadedConv!.Turns, t => t.Role == StageTurnRole.User);
        Assert.Contains(loadedConv.Turns, t => t.Role == StageTurnRole.Tool && t.ToolName == "run_command");
        Assert.Contains(loadedConv.Turns, t => t.Role == StageTurnRole.ToolResult && t.ToolName == "run_command" && t.ToolResult == "Passed: 12");
        Assert.Contains(loadedConv.Turns, t => t.Role == StageTurnRole.Assistant && t.Content == "Implemented feature X and all tests pass.");
        Assert.Contains(loadedConv.Turns, t => t.Role == StageTurnRole.Assistant && t.ThinkingTrace == "Planning build check...");
    }

    [Fact]
    public async Task RunSessionStreamAsync_WhenSessionNewReturnsError_ThrowsAcpAuthenticationExceptionAndMarksConversationFailed()
    {
        var taskRepo = new InMemoryTaskRepository();
        var task = await taskRepo.CreateAsync(new ForgeTask { Title = "Auth Fail", Description = "Desc" });
        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder
        {
            TaskRepository = taskRepo
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = task,
            StageId = StandardStageIds.Implementation,
            Profile = new AgentProfile { Name = "Agy", HarnessId = StandardHarnessIds.Acp },
            Workspace = new Workspace { Id = 1, RepositoryPath = Directory.GetCurrentDirectory(), ExecutionMode = ExecutionMode.BareHost }
        };

        var acpOutput = new StringBuilder();
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":1}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"session-1\",\"error\":{\"code\":-32001,\"message\":\"Not authenticated\"}}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<AcpAuthenticationException>(() =>
            harness.RunSessionStreamAsync(writer, reader, context, "sess-auth-fail", sw));

        var convs = await taskRepo.GetConversationsAsync(task.Id);
        var conv = Assert.Single(convs);
        Assert.Equal(StageExecutionStatus.Failed, conv.Status);
    }

    [Fact]
    public async Task RunStageAsync_ReadOnlyMode_OmitsSkipPermissionsFlag_SetsPlanMode_AndRejectsPermissionRequest()
    {
        var acpOutput = new StringBuilder();
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":1}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"session-1\",\"result\":{\"sessionId\":\"srv-ro\",\"configOptions\":[{\"id\":\"mode\",\"options\":[{\"value\":\"plan\"},{\"value\":\"accept-edits\"}]}]}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"perm-ro\",\"method\":\"session/request_permission\",\"params\":{\"prompt\":\"Write file?\"}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"srv-ro\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"text\":\"Read-only review completed.\"}}}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}");

        using var session = new FakeProcessStreamSession(acpOutput.ToString());
        ProcessStartSpec? capturedSpec = null;
        var mockRunner = new Mock<IProcessRunner>();
        mockRunner
            .Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>()))
            .Callback<ProcessStartSpec>(spec => capturedSpec = spec)
            .Returns(session);

        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder
        {
            ProcessRunner = mockRunner.Object
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 88, Title = "RO ACP", Description = "Review only" },
            StageId = StandardStageIds.TaskReview,
            Profile = new AgentProfile { Name = "AgyRO", HarnessId = StandardHarnessIds.Acp, Mode = AgentMode.ReadOnly },
            RequiredMode = AgentMode.ReadOnly,
            Workspace = new Workspace { Id = 1, RepositoryPath = Directory.GetCurrentDirectory(), ExecutionMode = ExecutionMode.BareHost },
            WorkspacePath = Directory.GetCurrentDirectory()
        };

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.NotNull(capturedSpec);
        Assert.DoesNotContain("--dangerously-skip-permissions", capturedSpec!.Arguments ?? string.Empty);

        var sentLines = session.GetCapturedStdinText();
        Assert.Contains("\"configId\":\"mode\",\"value\":\"plan\"", sentLines);
        Assert.Contains("\"id\":\"perm-ro\"", sentLines);
        Assert.Contains("Permission denied", sentLines);
    }

    [Fact]
    public async Task AcpProcessLauncher_ContainerSandbox_And_WslDirect_ResolveExpectedCommandAndWorkingDirectory()
    {
        var runnerMock = new Mock<IProcessRunner>();
        runnerMock.Setup(r => r.Run(It.IsAny<ProcessStartSpec>()))
            .Returns(new ProcessExecutionResult(0, "true\n", string.Empty));
        var identityMock = new Mock<IHostUserIdentityResolver>();
        identityMock.Setup(i => i.ResolveUserIdentity(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string>())).Returns("1000:1000");
        var telemetryMock = new Mock<IOperationsTelemetryService>();

        var containerOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-sandbox-5-task-42",
            DefaultWorkingDirectory = "/workspace/task-42",
            UserUidGid = "1000:1000",
            UseWsl = true,
            WslDistribution = "Ubuntu",
            AutoStartContainer = false,
            ExtraPathDirectories = [ContainerExecutorOptions.HostAgyContainerDirectory]
        };
        using var executor = new NightCodeForgeContainerExecutor(
            containerOptions,
            runnerMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            identityMock.Object,
            telemetryMock.Object);

        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(p => p.GetWslDistributionName()).Returns("Ubuntu");
        pathResolverMock.Setup(p => p.GetAgentWorkingClonePath(5)).Returns("/home/dev/.nightcodeforge/wks/5/clone");

        var launcher = new AcpProcessLauncher(
            new SingleExecutionEnvironmentProvider(executor),
            pathResolverMock.Object,
            NullLogger<AcpProcessLauncher>.Instance);

        var containerWs = new Workspace { Id = 5, ExecutionMode = ExecutionMode.ContainerSandbox, UseWslForContainers = true, WslDistribution = "Ubuntu" };
        var containerScope = SandboxScopeDescriptor.ForWorkspace(
            containerWs,
            SandboxSecurityTier.Standard,
            taskId: 42,
            mountTable: SandboxMountTable.ForWorkspace("/host/wks/5", "/home/dev/.nightcodeforge/wks/5", false, 42),
            defaultWorkingDirectory: "/workspace/task-42");

        var containerContext = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 42, Title = "Container Task", Description = "Desc" },
            StageId = StandardStageIds.Implementation,
            Profile = new AgentProfile { Name = "Agy", HarnessId = StandardHarnessIds.Acp, Mode = AgentMode.Full },
            Workspace = containerWs,
            SandboxScope = containerScope
        };

        var containerPlan = await launcher.PrepareLaunchAsync(
            containerContext,
            "agy",
            "acp --dangerously-skip-permissions",
            "/host/wks/5/task-42",
            "/workspace",
            "/host/wks/5/task-42",
            600);

        Assert.Equal("wsl.exe", containerPlan.ProcessSpec.FileName);
        var argList = containerPlan.ProcessSpec.ArgumentList!.ToList();
        Assert.Contains("docker", argList);
        Assert.Contains("exec", argList);
        Assert.Contains("-w", argList);
        Assert.Contains("/workspace/task-42", argList);
        Assert.Contains("/opt/ncf-acp/agy", argList);
        Assert.Contains($"HOME={ContainerExecutorOptions.ContainerHomePath}", argList);
        var expectedVsTestEnv = $"{ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar}={ContainerExecutorOptions.ContainerVsTestArtifactsBaseDirectory}";
        var vsTestEnvIdx = argList.IndexOf(expectedVsTestEnv);
        Assert.True(vsTestEnvIdx > 0 && argList[vsTestEnvIdx - 1] == "-e", "Expected -e immediately preceding VSTEST_ARTIFACTS_BASE_DIRECTORY");
        Assert.Contains(argList, a => a.StartsWith("PATH=/opt/ncf-acp:", StringComparison.Ordinal));
        Assert.Equal("/workspace/task-42", containerPlan.SessionWorkingDirectory);

        var wslContext = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 42, Title = "WSL Task", Description = "Desc" },
            StageId = StandardStageIds.Implementation,
            Profile = new AgentProfile { Name = "Agy", HarnessId = StandardHarnessIds.Acp, Mode = AgentMode.Full },
            Workspace = new Workspace
            {
                Id = 5,
                ExecutionMode = ExecutionMode.WslDirect,
                WslDistribution = "Ubuntu",
                Ext4MirrorPath = "~/.nightcodeforge/wks/5/clone"
            },
            WorkspacePath = "~/.nightcodeforge/wks/5/task-42"
        };

        // 1. Unconfigured WSL user falls back to sh -c exec $HOME/... for binary invocation
        var wslPlanFallback = await launcher.PrepareLaunchAsync(
            wslContext,
            "agy",
            "acp --dangerously-skip-permissions",
            "C:\\dummy",
            "~/.nightcodeforge/wks/5/task-42",
            "C:\\dummy",
            600);

        Assert.Equal("wsl.exe", wslPlanFallback.ProcessSpec.FileName);
        Assert.Equal(["-d", "Ubuntu", "--", "sh", "-c", "exec \"$HOME/.local/bin/agy\" acp --dangerously-skip-permissions"], wslPlanFallback.ProcessSpec.ArgumentList);
        Assert.Equal("~/.nightcodeforge/wks/5/task-42", wslPlanFallback.SessionWorkingDirectory);

        // 2. Resolved WSL user expands both executable (~/.local/bin/agy -> /home/dev/.local/bin/agy)
        //    and session working directory (~/.nightcodeforge/... -> /home/dev/.nightcodeforge/...)
        pathResolverMock.Setup(p => p.GetWslUserName()).Returns("dev");

        var wslPlan = await launcher.PrepareLaunchAsync(
            wslContext,
            "agy",
            "acp --dangerously-skip-permissions",
            "C:\\dummy",
            "~/.nightcodeforge/wks/5/task-42",
            "C:\\dummy",
            600);

        Assert.Equal("wsl.exe", wslPlan.ProcessSpec.FileName);
        Assert.Equal("-d Ubuntu -- /home/dev/.local/bin/agy acp --dangerously-skip-permissions", wslPlan.ProcessSpec.Arguments);
        Assert.Equal("/home/dev/.nightcodeforge/wks/5/task-42", wslPlan.SessionWorkingDirectory);

        var wslExeFallbackPlan = await launcher.PrepareLaunchAsync(
            wslContext,
            "C:\\Users\\dev\\.gemini\\bin\\agy.exe",
            "acp --dangerously-skip-permissions",
            "C:\\dummy",
            "~/.nightcodeforge/wks/5/task-42",
            "C:\\dummy",
            600);
        Assert.Equal("-d Ubuntu -- /home/dev/.local/bin/agy acp --dangerously-skip-permissions", wslExeFallbackPlan.ProcessSpec.Arguments);
    }

    [Fact]
    public async Task AcpProcessLauncher_ContainerSandbox_FallbackAndRestrictedReadOnly_PassVsTestArtifactsEnvVar()
    {
        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        var containerWs = new Workspace
        {
            Id = 9,
            ExecutionMode = ExecutionMode.ContainerSandbox,
            UseWslForContainers = true,
            WslDistribution = "Ubuntu"
        };
        var expectedVsTestEnv = $"{ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar}={ContainerExecutorOptions.ContainerVsTestArtifactsBaseDirectory}";

        // 1. Fallback container branch (when IExecutionEnvironment is not NightCodeForgeContainerExecutor)
        var fallbackEnvMock = new Mock<IExecutionEnvironment>();
        fallbackEnvMock.Setup(e => e.Tier).Returns(ExecutionTier.ContainerSandbox);
        var fallbackLauncher = new AcpProcessLauncher(
            new SingleExecutionEnvironmentProvider(fallbackEnvMock.Object),
            pathResolverMock.Object,
            NullLogger<AcpProcessLauncher>.Instance);

        var fallbackContext = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 17, Title = "Fallback Container", Description = "Desc" },
            StageId = StandardStageIds.Implementation,
            Profile = new AgentProfile { Name = "Agy", HarnessId = StandardHarnessIds.Acp, Mode = AgentMode.Full },
            Workspace = containerWs
        };

        var fallbackPlan = await fallbackLauncher.PrepareLaunchAsync(
            fallbackContext,
            "agy",
            "acp",
            "/host/wks/9/task-17",
            "/workspace",
            "/host/wks/9/task-17",
            600);

        var fallbackArgs = fallbackPlan.ProcessSpec.ArgumentList!.ToList();
        var fallbackIdx = fallbackArgs.IndexOf(expectedVsTestEnv);
        Assert.True(fallbackIdx > 0 && fallbackArgs[fallbackIdx - 1] == "-e", "Expected -e immediately preceding VSTEST_ARTIFACTS_BASE_DIRECTORY in fallback launch");

        // 2. RestrictedReadOnly container launch
        var runnerMock = new Mock<IProcessRunner>();
        runnerMock.Setup(r => r.Run(It.IsAny<ProcessStartSpec>()))
            .Returns(new ProcessExecutionResult(0, "true\n", string.Empty));
        var identityMock = new Mock<IHostUserIdentityResolver>();
        identityMock.Setup(i => i.ResolveUserIdentity(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string>())).Returns("2000:2000");

        using var roExecutor = new NightCodeForgeContainerExecutor(
            new ContainerExecutorOptions
            {
                ContainerNameOrId = "ncf-sandbox-9-ro",
                DefaultWorkingDirectory = "/workspace/clone",
                UserUidGid = "2000:2000",
                IsRestrictedReadOnly = true,
                UseWsl = true,
                WslDistribution = "Ubuntu",
                AutoStartContainer = false
            },
            runnerMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            identityMock.Object,
            new Mock<IOperationsTelemetryService>().Object);

        var roLauncher = new AcpProcessLauncher(
            new SingleExecutionEnvironmentProvider(roExecutor),
            pathResolverMock.Object,
            NullLogger<AcpProcessLauncher>.Instance);

        var roContext = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 0, Title = "RO Container", Description = "Desc" },
            StageId = StandardStageIds.TaskReview,
            Profile = new AgentProfile { Name = "AgyRO", HarnessId = StandardHarnessIds.Acp, Mode = AgentMode.ReadOnly, SecurityTier = SandboxSecurityTier.RestrictedReadOnly },
            RequiredMode = AgentMode.ReadOnly,
            Workspace = containerWs
        };

        var roPlan = await roLauncher.PrepareLaunchAsync(
            roContext,
            "agy",
            "acp --dangerously-skip-permissions",
            "/host/wks/9/clone",
            "/workspace",
            "/host/wks/9/clone",
            600);

        var roArgs = roPlan.ProcessSpec.ArgumentList!.ToList();
        var userIdx = roArgs.IndexOf("-u");
        Assert.True(userIdx >= 0 && roArgs[userIdx + 1] == ContainerExecutorOptions.RestrictedReadOnlyUserUidGid);
        var roVsTestIdx = roArgs.IndexOf(expectedVsTestEnv);
        Assert.True(roVsTestIdx > 0 && roArgs[roVsTestIdx - 1] == "-e", "Expected -e immediately preceding VSTEST_ARTIFACTS_BASE_DIRECTORY in RestrictedReadOnly launch");
    }

    [Fact]
    public async Task RunSessionStreamAsync_EndsHostRpcSessionWithServerSessionId_AndBroadcastsErrorWithActiveSessionId()
    {
        var mockHostRpc = new Mock<IAcpHostRpcHandler>();
        mockHostRpc.Setup(h => h.CanHandle(It.IsAny<string>())).Returns(false);
        var streamer = new TestActivityStreamer();

        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder
        {
            HostRpcHandler = mockHostRpc.Object,
            ActivityStreamer = streamer
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 208, Title = "EndSession Test", Description = "Test activeSessionId cleanup" },
            StageId = StandardStageIds.Implementation,
            Profile = new AgentProfile { Name = "Agy", HarnessId = StandardHarnessIds.Acp },
            Workspace = new Workspace { Id = 1, RepositoryPath = Directory.GetCurrentDirectory() }
        };

        var acpOutput = string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":1}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"session-1\",\"result\":{\"sessionId\":\"srv-assigned-99\"}}") + "\n";

        using var session = new FakeProcessStreamSession(acpOutput, throwOnSecondLine: new IOException("transport dropped"));
        var sw = Stopwatch.StartNew();

        await Assert.ThrowsAsync<IOException>(() =>
            harness.RunSessionStreamAsync(session.StandardInput, session.StandardOutput, context, "local-sess-99", sw));

        mockHostRpc.Verify(h => h.EndSession("local-sess-99"), Times.Once);
        mockHostRpc.Verify(h => h.EndSession("srv-assigned-99"), Times.Once);
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.Error && e.SessionId == "srv-assigned-99");
    }

    [Fact]
    public async Task RunSessionStreamAsync_WslDirect_UsesWslWorktreeInSessionNewAndPromptEnvironment()
    {
        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder().Build();
        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 42, Title = "WSL Direct PWD", Description = "Check WSL PWD" },
            StageId = StandardStageIds.Implementation,
            Profile = new AgentProfile { Name = "Agy", HarnessId = StandardHarnessIds.Acp },
            Workspace = new Workspace
            {
                Id = 5,
                RepositoryPath = "C:\\repos\\proj",
                ExecutionMode = ExecutionMode.WslDirect,
                Ext4MirrorPath = "~/.nightcodeforge/wks/5/clone"
            },
            WorkspacePath = "C:\\repos\\proj"
        };

        var acpOutput = string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":1}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"session-1\",\"result\":{\"sessionId\":\"srv-wsl-1\"}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}") + "\n";

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-wsl", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        var sentLines = Encoding.UTF8.GetString(outStream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonNode.Parse(l.TrimStart('\uFEFF'))!)
            .ToList();

        var sessionReq = sentLines.First(n => n["id"]?.ToString() == "session-1");
        Assert.Equal("~/.nightcodeforge/wks/5/task-42", sessionReq["params"]?["cwd"]?.ToString());

        var promptReq = sentLines.First(n => n["id"]?.ToString() == "prompt-1");
        Assert.Contains("Current Working Directory (PWD): ~/.nightcodeforge/wks/5/task-42", promptReq["params"]?["prompt"]?[0]?["text"]?.ToString());
    }

    [Fact]
    public async Task RunSessionStreamAsync_WslDirect_WithResolvedWslUserName_ExpandsTildeWorkingDirectory()
    {
        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(p => p.GetWslUserName()).Returns("dev");
        pathResolverMock.Setup(p => p.GetWslDistributionName()).Returns("Ubuntu");
        pathResolverMock.Setup(p => p.GetAgentWorkingClonePath(5)).Returns("/home/dev/.nightcodeforge/wks/5/clone");

        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder
        {
            PathResolver = pathResolverMock.Object
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 42, Title = "WSL Direct Expanded PWD", Description = "Check expanded WSL PWD" },
            StageId = StandardStageIds.Implementation,
            Profile = new AgentProfile { Name = "Agy", HarnessId = StandardHarnessIds.Acp },
            Workspace = new Workspace
            {
                Id = 5,
                RepositoryPath = "C:\\repos\\proj",
                ExecutionMode = ExecutionMode.WslDirect,
                Ext4MirrorPath = "~/.nightcodeforge/wks/5/clone"
            },
            WorkspacePath = "C:\\repos\\proj"
        };

        var acpOutput = string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":1}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"session-1\",\"result\":{\"sessionId\":\"srv-wsl-2\"}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}") + "\n";

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true };

        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-wsl-expanded", Stopwatch.StartNew());

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        var sentLines = Encoding.UTF8.GetString(outStream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonNode.Parse(l.TrimStart('\uFEFF'))!)
            .ToList();

        var sessionReq = sentLines.First(n => n["id"]?.ToString() == "session-1");
        Assert.Equal("/home/dev/.nightcodeforge/wks/5/task-42", sessionReq["params"]?["cwd"]?.ToString());

        var promptReq = sentLines.First(n => n["id"]?.ToString() == "prompt-1");
        Assert.Contains("Current Working Directory (PWD): /home/dev/.nightcodeforge/wks/5/task-42", promptReq["params"]?["prompt"]?[0]?["text"]?.ToString());
    }

    [Fact]
    public async Task RunSessionStreamAsync_WhenInitializeOrPromptReturnsJsonRpcError_FailsFastAndMarksConversationFailed()
    {
        var streamer = new TestActivityStreamer();
        var taskRepo = new InMemoryTaskRepository();
        var task1 = await taskRepo.CreateAsync(new ForgeTask { Title = "Prompt Error", Description = "Desc" });
        var task2 = await taskRepo.CreateAsync(new ForgeTask { Title = "Prompt Auth Error", Description = "Desc" });
        var task3 = await taskRepo.CreateAsync(new ForgeTask { Title = "Init Error", Description = "Desc" });

        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder
        {
            ActivityStreamer = streamer,
            TaskRepository = taskRepo
        }.Build();

        AgentSessionContext BuildContext(ForgeTask task) => new()
        {
            ForgeTask = task,
            StageId = StandardStageIds.Implementation,
            Profile = new AgentProfile { Name = "Agy", HarnessId = StandardHarnessIds.Acp },
            Workspace = new Workspace { Id = 1, RepositoryPath = Directory.GetCurrentDirectory(), ExecutionMode = ExecutionMode.BareHost }
        };

        // 1. prompt-1 non-auth error must throw InvalidOperationException, flush partial assistant output, and fail conversation
        var promptErrStream = string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":1}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"session-1\",\"result\":{\"sessionId\":\"srv-err-1\"}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"srv-err-1\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"text\":\"Partial thought\"}}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"error\":{\"code\":-32000,\"message\":\"Quota exceeded\"}}") + "\n";

        using (var inStream = new MemoryStream(Encoding.UTF8.GetBytes(promptErrStream)))
        using (var reader = new StreamReader(inStream))
        using (var outStream = new MemoryStream())
        using (var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true })
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.RunSessionStreamAsync(writer, reader, BuildContext(task1), "sess-prompt-err", Stopwatch.StartNew()));
            Assert.Contains("Quota exceeded", ex.Message);
        }

        Assert.Contains(streamer.Events, e => e.TaskId == task1.Id && e.Kind == AgentActivityKind.Error && e.Content.Contains("Quota exceeded"));
        var conv1 = Assert.Single(await taskRepo.GetConversationsAsync(task1.Id));
        Assert.Equal(StageExecutionStatus.Failed, conv1.Status);
        var loadedConv1 = await taskRepo.GetConversationWithTurnsAsync(conv1.Id);
        Assert.Contains(loadedConv1!.Turns, t => t.Role == StageTurnRole.Assistant && t.Content == "Partial thought");

        // 2. prompt-1 auth error throws AcpAuthenticationException
        var promptAuthErrStream = string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":1}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"session-1\",\"result\":{\"sessionId\":\"srv-auth-2\"}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"error\":{\"code\":-32001,\"message\":\"Token expired mid-session\"}}") + "\n";

        using (var inStream = new MemoryStream(Encoding.UTF8.GetBytes(promptAuthErrStream)))
        using (var reader = new StreamReader(inStream))
        using (var outStream = new MemoryStream())
        using (var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true })
        {
            await Assert.ThrowsAsync<AcpAuthenticationException>(() =>
                harness.RunSessionStreamAsync(writer, reader, BuildContext(task2), "sess-prompt-auth-err", Stopwatch.StartNew()));
        }

        var conv2 = Assert.Single(await taskRepo.GetConversationsAsync(task2.Id));
        Assert.Equal(StageExecutionStatus.Failed, conv2.Status);

        // 3. init-1 JSON-RPC error throws instead of silently returning protocol version 1
        var initErrStream = "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"error\":{\"code\":-32600,\"message\":\"Unsupported protocol version\"}}\n";
        using (var inStream = new MemoryStream(Encoding.UTF8.GetBytes(initErrStream)))
        using (var reader = new StreamReader(inStream))
        using (var outStream = new MemoryStream())
        using (var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true })
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.RunSessionStreamAsync(writer, reader, BuildContext(task3), "sess-init-err", Stopwatch.StartNew()));
            Assert.Contains("Unsupported protocol version", ex.Message);
        }

        var conv3 = Assert.Single(await taskRepo.GetConversationsAsync(task3.Id));
        Assert.Equal(StageExecutionStatus.Failed, conv3.Status);
    }

    [Fact]
    public async Task RunSessionStreamAsync_SessionLoadSendsCwdAndMcpServers_AndFallsBackToSessionNewOnNonAuthError()
    {
        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder().Build();
        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 209, Title = "Resume Fallback", Description = "Fallback to session/new" },
            StageId = StandardStageIds.Implementation,
            Profile = new AgentProfile { Name = "Agy", HarnessId = StandardHarnessIds.Acp },
            Workspace = new Workspace { Id = 1, RepositoryPath = Directory.GetCurrentDirectory() },
            WorkspacePath = Directory.GetCurrentDirectory(),
            SerializedStateJson = "stale-session-id"
        };

        var acpOutput = string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":1}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"session-1\",\"error\":{\"code\":-32000,\"message\":\"Session not found\"}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"session-fallback-1\",\"result\":{\"sessionId\":\"fresh-session-1\"}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"fresh-session-1\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"text\":\"Recovered via new session.\"}}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}") + "\n";

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-fallback", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("fresh-session-1", result.UpdatedState);
        Assert.Equal("Recovered via new session.", result.Summary);

        var sentLines = Encoding.UTF8.GetString(outStream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonNode.Parse(l.TrimStart('\uFEFF'))!)
            .ToList();

        var loadReq = sentLines.First(n => n["id"]?.ToString() == "session-1");
        Assert.Equal("session/load", loadReq["method"]?.ToString());
        Assert.Equal("stale-session-id", loadReq["params"]?["sessionId"]?.ToString());
        Assert.NotNull(loadReq["params"]?["cwd"]);
        Assert.NotNull(loadReq["params"]?["mcpServers"]);

        var fallbackReq = sentLines.First(n => n["id"]?.ToString() == "session-fallback-1");
        Assert.Equal("session/new", fallbackReq["method"]?.ToString());

        var promptReq = sentLines.First(n => n["id"]?.ToString() == "prompt-1");
        Assert.Equal("fresh-session-1", promptReq["params"]?["sessionId"]?.ToString());
    }

    [Fact]
    public async Task RunSessionStreamAsync_IgnoresInProgressToolCallUpdates_AndSerializesCompactJsonArgs()
    {
        var streamer = new TestActivityStreamer();
        var taskRepo = new InMemoryTaskRepository();
        var task = await taskRepo.CreateAsync(new ForgeTask { Title = "Tool Update Filter", Description = "Desc" });
        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder
        {
            ActivityStreamer = streamer,
            TaskRepository = taskRepo
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = task,
            StageId = StandardStageIds.Implementation,
            Profile = new AgentProfile { Id = StandardStageIds.Implementation, Name = "Agy", HarnessId = StandardHarnessIds.Acp },
            Workspace = new Workspace { Id = 1, RepositoryPath = Directory.GetCurrentDirectory() }
        };

        var acpOutput = string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":1}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"session-1\",\"result\":{\"sessionId\":\"srv-tool-1\"}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"srv-tool-1\",\"update\":{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"tc-1\",\"name\":\"run_command\",\"args\":{\"CommandLine\":\"dotnet build\"}}}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"srv-tool-1\",\"update\":{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"tc-1\",\"status\":\"in_progress\",\"content\":[{\"type\":\"text\",\"text\":\"Building...\"}]}}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"srv-tool-1\",\"update\":{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"tc-1\",\"status\":\"pending\"}}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"srv-tool-1\",\"update\":{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"tc-1\",\"status\":\"completed\",\"content\":[{\"type\":\"content\",\"content\":{\"type\":\"text\",\"text\":\"Build succeeded.\"}}]}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}") + "\n";

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-tool-filter", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var started = Assert.Single(streamer.Events, e => e.Kind == AgentActivityKind.ToolStarted);
        Assert.Equal("run_command: {\"CommandLine\":\"dotnet build\"}", started.Content);

        var completed = Assert.Single(streamer.Events, e => e.Kind == AgentActivityKind.ToolCompleted);
        Assert.Equal("run_command: completed: Build succeeded.", completed.Content);

        var conv = Assert.Single(await taskRepo.GetConversationsAsync(task.Id));
        var loaded = await taskRepo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(loaded);
        var toolResultTurn = Assert.Single(loaded!.Turns, t => t.Role == StageTurnRole.ToolResult);
        Assert.Equal("Build succeeded.", toolResultTurn.ToolResult);
    }

    [Fact]
    public async Task RunSessionStreamAsync_ConfigOptionsWithoutThoughtLevel_AndCombinedAvailableModelsResolution()
    {
        // Case 1: configOptions has only "model" (no "thought_level") -> does not emit thought_level request
        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestAcpHarnessBuilder().Build();
        var context1 = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 210, Title = "Config Guard", Description = "Desc" },
            StageId = StandardStageIds.Implementation,
            Profile = new AgentProfile { Name = "Agy", HarnessId = StandardHarnessIds.Acp, ModelName = "gemini-4-argon", ThinkingEffort = ThinkingEffort.High },
            Workspace = new Workspace { Id = 1, RepositoryPath = Directory.GetCurrentDirectory() }
        };

        var acpOutput1 = string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":1}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"session-1\",\"result\":{\"sessionId\":\"srv-cfg-1\",\"configOptions\":[{\"id\":\"model\",\"options\":[{\"value\":\"gemini-4-argon\"}]}]}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}") + "\n";

        using (var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput1)))
        using (var reader = new StreamReader(inStream))
        using (var outStream = new MemoryStream())
        using (var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true })
        {
            var res1 = await harness.RunSessionStreamAsync(writer, reader, context1, "sess-cfg-1", Stopwatch.StartNew());
            Assert.Equal(AgentRunStatus.Completed, res1.Status);
            var sent1 = Encoding.UTF8.GetString(outStream.ToArray());
            Assert.Contains("\"configId\":\"model\",\"value\":\"gemini-4-argon\"", sent1);
            Assert.DoesNotContain("thought_level", sent1);
        }

        // Case 2: no configOptions, only combined availableModels using "id" field -> resolves gemini-4-argon + High -> gemini-4-argon-high
        var acpOutput2 = string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":1}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"session-1\",\"result\":{\"sessionId\":\"srv-cfg-2\",\"models\":{\"availableModels\":[{\"id\":\"gemini-4-argon-low\"},{\"id\":\"gemini-4-argon-high\"}]}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}") + "\n";

        using (var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput2)))
        using (var reader = new StreamReader(inStream))
        using (var outStream = new MemoryStream())
        using (var writer = new StreamWriter(outStream, new UTF8Encoding(false)) { AutoFlush = true })
        {
            var res2 = await harness.RunSessionStreamAsync(writer, reader, context1, "sess-cfg-2", Stopwatch.StartNew());
            Assert.Equal(AgentRunStatus.Completed, res2.Status);
            var sent2 = Encoding.UTF8.GetString(outStream.ToArray());
            Assert.Contains("\"method\":\"session/set_model\"", sent2);
            Assert.Contains("\"modelId\":\"gemini-4-argon-high\"", sent2);
        }
    }

    private sealed class FakeProcessStreamSession : IProcessStreamSession
    {
        private readonly MemoryStream _stdinStream = new();
        private readonly Stream _stdoutStream;
        private readonly MemoryStream _stderrStream = new();

        public FakeProcessStreamSession(string stdoutContent, Exception? throwOnSecondLine = null)
        {
            _stdoutStream = throwOnSecondLine == null
                ? new MemoryStream(Encoding.UTF8.GetBytes(stdoutContent))
                : new ThrowingAfterInitialChunkStream(stdoutContent, throwOnSecondLine);
            StandardInput = new StreamWriter(_stdinStream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            StandardOutput = new StreamReader(_stdoutStream, Encoding.UTF8, leaveOpen: true);
            StandardError = new StreamReader(_stderrStream, Encoding.UTF8, leaveOpen: true);
        }

        public int? ExitCode => 0;
        public bool HasExited => true;
        public bool IsDisposed { get; private set; }
        public StreamWriter StandardInput { get; }
        public StreamReader StandardOutput { get; }
        public StreamReader StandardError { get; }

        public string GetCapturedStdinText()
        {
            StandardInput.Flush();
            return Encoding.UTF8.GetString(_stdinStream.ToArray());
        }

        public IAsyncEnumerable<ProcessStreamChunk> ReadAllAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public void Kill() { }

        public void Dispose()
        {
            IsDisposed = true;
            StandardInput.Dispose();
            StandardOutput.Dispose();
            StandardError.Dispose();
            _stdinStream.Dispose();
            _stdoutStream.Dispose();
            _stderrStream.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingAfterInitialChunkStream : Stream
    {
        private readonly byte[] _initialBytes;
        private readonly Exception _exception;
        private int _position;

        public ThrowingAfterInitialChunkStream(string initialText, Exception exception)
        {
            _initialBytes = Encoding.UTF8.GetBytes(initialText);
            _exception = exception;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _initialBytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position < _initialBytes.Length)
            {
                int toCopy = Math.Min(count, _initialBytes.Length - _position);
                Array.Copy(_initialBytes, _position, buffer, offset, toCopy);
                _position += toCopy;
                return toCopy;
            }
            throw _exception;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _initialBytes.Length)
            {
                int toCopy = Math.Min(buffer.Length, _initialBytes.Length - _position);
                _initialBytes.AsMemory(_position, toCopy).CopyTo(buffer);
                _position += toCopy;
                return ValueTask.FromResult(toCopy);
            }
            return ValueTask.FromException<int>(_exception);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancellingAfterInitStream : Stream
    {
        private readonly byte[] _initBytes;
        private readonly CancellationTokenSource _cts;
        private int _position;

        public CancellingAfterInitStream(string initLine, CancellationTokenSource cts)
        {
            _initBytes = Encoding.UTF8.GetBytes(initLine);
            _cts = cts;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _initBytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _initBytes.Length)
            {
                int toCopy = Math.Min(buffer.Length, _initBytes.Length - _position);
                _initBytes.AsMemory(_position, toCopy).CopyTo(buffer);
                _position += toCopy;
                return toCopy;
            }

            await _cts.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(_cts.Token);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DynamicLineProducerStream : Stream
    {
        private readonly IReadOnlyList<Func<string>> _lineProducers;
        private int _lineIndex;
        private byte[] _currentBuffer = Array.Empty<byte>();
        private int _bufferOffset;

        public DynamicLineProducerStream(IReadOnlyList<Func<string>> lineProducers)
        {
            _lineProducers = lineProducers;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_bufferOffset >= _currentBuffer.Length)
            {
                if (_lineIndex >= _lineProducers.Count)
                {
                    return ValueTask.FromResult(0);
                }

                var nextLine = _lineProducers[_lineIndex++]();
                _currentBuffer = Encoding.UTF8.GetBytes(nextLine + "\n");
                _bufferOffset = 0;
            }

            int toCopy = Math.Min(buffer.Length, _currentBuffer.Length - _bufferOffset);
            _currentBuffer.AsMemory(_bufferOffset, toCopy).CopyTo(buffer);
            _bufferOffset += toCopy;
            return ValueTask.FromResult(toCopy);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
