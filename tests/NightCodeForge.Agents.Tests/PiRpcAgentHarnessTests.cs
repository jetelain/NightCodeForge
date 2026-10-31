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
        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestPiRpcHarnessBuilder { ActivityStreamer = streamer }.Build();

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
        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestPiRpcHarnessBuilder { ActivityStreamer = streamer }.Build();

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
        Assert.NotNull(result.PendingQuestions);
        Assert.Single(result.PendingQuestions);
        var q = result.PendingQuestions[0];
        Assert.Contains("Which database driver?", q.Prompt);
        Assert.Contains("Npgsql", q.OptionsJson ?? "");
        Assert.Contains("Microsoft.Data.Sqlite", q.OptionsJson ?? "");

        // Verify streamed interruption
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.Interrupted && e.Content.Contains("Which database driver?"));
    }

    [Fact]
    public async Task RunSessionStreamAsync_IncludesPendingHumanAnswer_WhenResuming()
    {
        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestPiRpcHarnessBuilder().Build();

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
            var harness = new NightCodeForge.Agents.Tests.Fixtures.TestPiRpcHarnessBuilder { PathResolver = pathResolver }.Build();

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

    [Theory]
    [InlineData("{\"executablePath\":\"custom-node\",\"arguments\":\"--custom-rpc\"}", "custom-node", "--custom-rpc")]
    [InlineData("{\"executable\":\"pi-bin\",\"args\":\"--rpc-v2\"}", "pi-bin", "--rpc-v2")]
    [InlineData("{\"command\":\"pi-daemon\",\"args\":\"--daemon\"}", "pi-daemon", "--daemon")]
    [InlineData("", "node", "./packages/coding-agent/dist/rpc-entry.js --mode rpc")]
    [InlineData(null, "node", "./packages/coding-agent/dist/rpc-entry.js --mode rpc")]
    [InlineData("{ malformed json", "node", "./packages/coding-agent/dist/rpc-entry.js --mode rpc")]
    [InlineData("{\"arguments\":[\"--array-not-string\"]}", "node", "./packages/coding-agent/dist/rpc-entry.js --mode rpc")]
    [InlineData("{\"args\":456}", "node", "./packages/coding-agent/dist/rpc-entry.js --mode rpc")]
    public async Task RunStageAsync_ParsesHarnessConfigurationJson_OrFallsBackToDefaults(
        string? harnessConfigJson,
        string expectedExe,
        string expectedArgs)
    {
        var streamer = new TestActivityStreamer();
        using var session = new FakeProcessStreamSession("{\"type\":\"agent_settled\"}\n");
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

        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestPiRpcHarnessBuilder
        {
            ActivityStreamer = streamer,
            ProcessRunner = mockRunner.Object
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 301, Title = "Pi Config Test", Description = "Test config parsing" },
            StageId = "stage-2",
            Profile = new AgentProfile { Name = "PiAgent", HarnessConfigurationJson = harnessConfigJson },
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
    [InlineData("/home/wsluser/.nightcodeforge/wks/19/task-302")]
    [InlineData("~/.nightcodeforge/wks/19/task-302")]
    public async Task RunStageAsync_ResolvesUnixWorkingDirectory_ViaWorkspacePathResolver_AndDisposesSessionOnFailure(string unixWorkspacePath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pirpc_unix_wd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var pathResolver = new TestWorkspacePathResolver(tempDir);
            using var session = new FakeProcessStreamSession(
                stdoutContent: string.Empty,
                throwOnRead: new IOException("Pi daemon pipe closed unexpectedly"));

            ProcessStartSpec? capturedSpec = null;
            var mockRunner = new Moq.Mock<IProcessRunner>();
            mockRunner
                .Setup(r => r.StartSession(Moq.It.IsAny<ProcessStartSpec>()))
                .Callback<ProcessStartSpec>(spec => capturedSpec = spec)
                .Returns(session);

            var harness = new NightCodeForge.Agents.Tests.Fixtures.TestPiRpcHarnessBuilder
            {
                PathResolver = pathResolver,
                ProcessRunner = mockRunner.Object
            }.Build();

            var context = new AgentSessionContext
            {
                ForgeTask = new ForgeTask { Id = 302, Title = "Pi Unix Path Test", Description = "Verify path resolution" },
                StageId = "stage-2",
                Profile = new AgentProfile { Name = "PiAgent" },
                Workspace = new Workspace { Id = 19, RepositoryPath = tempDir },
                WorkspacePath = unixWorkspacePath
            };

            await Assert.ThrowsAsync<IOException>(() => harness.RunStageAsync(context));

            Assert.NotNull(capturedSpec);
            Assert.Equal(OperatingSystem.IsWindows() ? tempDir : unixWorkspacePath, capturedSpec.WorkingDirectory);
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
    public async Task RunSessionStreamAsync_WhenCancelledMidStream_WritesAbortAndRethrowsOperationCanceledException()
    {
        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestPiRpcHarnessBuilder().Build();
        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 303, Title = "Pi Cancel Test", Description = "Cancel mid-stream" },
            StageId = "stage-2",
            Profile = new AgentProfile { Name = "PiAgent" }
        };

        using var cts = new CancellationTokenSource();
        using var inStream = new CancellingOnReadStream(cts);
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.RunSessionStreamAsync(writer, reader, context, "sess-pi-cancel", sw, cts.Token));

        var sentCommands = Encoding.UTF8.GetString(outStream.ToArray());
        Assert.Contains("\"id\":\"abort-", sentCommands);
        Assert.Contains("\"type\":\"abort\"", sentCommands);
    }

    [Fact]
    public async Task RunSessionStreamAsync_WhenPromptResponseFails_BroadcastsError_SavesFailedTranscript_AndRethrows()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pirpc_err_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var streamer = new TestActivityStreamer();
            var transcriptService = new NightCodeForge.Agents.Transcripts.SessionTranscriptService(Path.Combine(tempDir, "docs", "transcripts"));
            var harness = new NightCodeForge.Agents.Tests.Fixtures.TestPiRpcHarnessBuilder
            {
                ActivityStreamer = streamer,
                TranscriptService = transcriptService
            }.Build();

            var context = new AgentSessionContext
            {
                ForgeTask = new ForgeTask { Id = 304, Title = "Pi Error Response Test", Description = "Test prompt failure" },
                StageId = "stage-2",
                Profile = new AgentProfile { Name = "PiAgent", ModelName = "qwen2.5-coder:14b" },
                Workspace = new Workspace { Id = 1, RepositoryPath = tempDir },
                WorkspacePath = tempDir
            };

            var piOutput = "{\"type\":\"response\",\"command\":\"prompt\",\"success\":false,\"error\":\"Model context limit exceeded\"}\n";
            using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(piOutput));
            using var reader = new StreamReader(inStream);
            using var outStream = new MemoryStream();
            using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

            var sw = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.RunSessionStreamAsync(writer, reader, context, "sess-pi-err", sw));

            Assert.Equal("Model context limit exceeded", ex.Message);
            Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.Error && e.Content.Contains("Model context limit exceeded"));

            var transcriptDir = Path.Combine(tempDir, "docs", "transcripts");
            Assert.True(Directory.Exists(transcriptDir));
            var transcriptFiles = Directory.GetFiles(transcriptDir, "*-task-304-stage-2-failed.md");
            var transcriptFile = Assert.Single(transcriptFiles);
            var transcriptText = await File.ReadAllTextAsync(transcriptFile);
            Assert.Contains("status: Failed", transcriptText);
            Assert.Contains("Model context limit exceeded", transcriptText);
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

        public FakeProcessStreamSession(string stdoutContent, Exception? throwOnRead = null)
        {
            _stdoutStream = throwOnRead == null
                ? new MemoryStream(Encoding.UTF8.GetBytes(stdoutContent))
                : new ThrowingOnReadStream(throwOnRead);
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

    private sealed class ThrowingOnReadStream : Stream
    {
        private readonly Exception _exception;

        public ThrowingOnReadStream(Exception exception) => _exception = exception;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw _exception;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(_exception);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancellingOnReadStream : Stream
    {
        private readonly CancellationTokenSource _cts;

        public CancellingOnReadStream(CancellationTokenSource cts) => _cts = cts;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _cts.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(_cts.Token);
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
