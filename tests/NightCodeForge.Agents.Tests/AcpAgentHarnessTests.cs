using System.Diagnostics;
using System.Text;
using Moq;
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
    [InlineData("", "gemini", "--experimental-acp")]
    [InlineData(null, "gemini", "--experimental-acp")]
    [InlineData("{ malformed json", "gemini", "--experimental-acp")]
    [InlineData("{\"arguments\":[\"--array-not-string\"]}", "gemini", "--experimental-acp")]
    [InlineData("{\"args\":123}", "gemini", "--experimental-acp")]
    public async Task RunStageAsync_ParsesHarnessConfigurationJson_OrFallsBackToDefaults(
        string? harnessConfigJson,
        string expectedExe,
        string expectedArgs)
    {
        var streamer = new TestActivityStreamer();
        var acpOutput = new StringBuilder();
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}");
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
            Workspace = new Workspace { Id = 1, RepositoryPath = Directory.GetCurrentDirectory() },
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
                Workspace = new Workspace { Id = 15, RepositoryPath = tempDir },
                WorkspacePath = unixWorkspacePath
            };

            await Assert.ThrowsAsync<IOException>(() => harness.RunStageAsync(context));

            Assert.NotNull(capturedSpec);
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(tempDir, capturedSpec.WorkingDirectory);
            }
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
