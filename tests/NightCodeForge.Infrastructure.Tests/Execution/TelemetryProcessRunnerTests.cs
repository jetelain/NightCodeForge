using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Execution;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public class TelemetryProcessRunnerTests
{
    private readonly Mock<IProcessRunner> _innerRunner;
    private readonly Mock<IOperationsTelemetryService> _telemetryService;
    private readonly TelemetryProcessRunner _runner;

    public TelemetryProcessRunnerTests()
    {
        _innerRunner = new Mock<IProcessRunner>();
        _telemetryService = new Mock<IOperationsTelemetryService>();
        _runner = new TelemetryProcessRunner(
            _innerRunner.Object,
            _telemetryService.Object,
            NullLogger<TelemetryProcessRunner>.Instance);
    }

    [Fact]
    public void Run_OnSuccess_CapturesStartAndCompletedTelemetry()
    {
        var spec = new ProcessStartSpec
        {
            FileName = "dotnet",
            Arguments = "test",
            WorkingDirectory = "/workspace/tests"
        };

        var expectedResult = new ProcessExecutionResult(0, "Passed 10 tests", "");
        _innerRunner.Setup(r => r.Run(spec)).Returns(expectedResult);

        ShellCommandRecord? started = null;
        ShellCommandRecord? completed = null;

        _telemetryService.Setup(t => t.RecordShellCommandStarted(It.IsAny<ShellCommandRecord>()))
            .Callback<ShellCommandRecord>(r => started = r);
        _telemetryService.Setup(t => t.RecordShellCommandCompleted(It.IsAny<ShellCommandRecord>()))
            .Callback<ShellCommandRecord>(r => completed = r);

        var result = _runner.Run(spec);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(started);
        Assert.Contains("dotnet test", started.CommandLine);
        Assert.Equal("/workspace/tests", started.WorkingDirectory);
        Assert.Equal(ShellCommandStatus.Running, started.Status);

        Assert.NotNull(completed);
        Assert.Equal(started.CommandId, completed.CommandId);
        Assert.Equal(0, completed.ExitCode);
        Assert.Equal(ShellCommandStatus.Completed, completed.Status);
        Assert.Contains("Passed 10 tests", completed.OutputPreview);
    }

    [Fact]
    public void Run_OnNonZeroExitCode_RecordsFailedStatus()
    {
        var spec = new ProcessStartSpec { FileName = "git", Arguments = "status" };
        var failedResult = new ProcessExecutionResult(1, "", "fatal: not a git repo");

        _innerRunner.Setup(r => r.Run(spec)).Returns(failedResult);

        ShellCommandRecord? completed = null;
        _telemetryService.Setup(t => t.RecordShellCommandCompleted(It.IsAny<ShellCommandRecord>()))
            .Callback<ShellCommandRecord>(r => completed = r);

        var result = _runner.Run(spec);

        Assert.Equal(1, result.ExitCode);
        Assert.NotNull(completed);
        Assert.Equal(1, completed.ExitCode);
        Assert.Equal(ShellCommandStatus.Failed, completed.Status);
        Assert.Contains("fatal: not a git repo", completed.ErrorPreview);
    }

    [Fact]
    public async Task RunAsync_CapturesTelemetryAndReturnsResult()
    {
        var spec = new ProcessStartSpec { FileName = "echo", Arguments = "hello" };
        var expectedResult = new ProcessExecutionResult(0, "hello", "");

        _innerRunner.Setup(r => r.RunAsync(spec, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var result = await _runner.RunAsync(spec);

        Assert.Equal(0, result.ExitCode);
        _telemetryService.Verify(t => t.RecordShellCommandStarted(It.IsAny<ShellCommandRecord>()), Times.Once);
        _telemetryService.Verify(t => t.RecordShellCommandCompleted(It.Is<ShellCommandRecord>(r => r.ExitCode == 0)), Times.Once);
    }

    [Fact]
    public async Task RunStreamingAsync_StreamsChunksAndRecordsCompletion()
    {
        var spec = new ProcessStartSpec { FileName = "bash", Arguments = "script.sh" };

        async IAsyncEnumerable<ProcessStreamChunk> FakeChunks()
        {
            yield return new ProcessStreamChunk(ProcessStreamKind.StandardOutput, "step 1\n");
            await Task.Yield();
            yield return new ProcessStreamChunk(ProcessStreamKind.StandardError, "warn 1\n");
            yield return new ProcessStreamChunk(ProcessStreamKind.Exit, "", 0);
        }

        _innerRunner.Setup(r => r.RunStreamingAsync(spec, It.IsAny<CancellationToken>()))
            .Returns(FakeChunks());

        var received = new List<ProcessStreamChunk>();
        await foreach (var chunk in _runner.RunStreamingAsync(spec))
        {
            received.Add(chunk);
        }

        Assert.Equal(3, received.Count);
        _telemetryService.Verify(t => t.RecordShellCommandStarted(It.IsAny<ShellCommandRecord>()), Times.Once);
        _telemetryService.Verify(t => t.UpdateShellCommandOutput(It.IsAny<string>(), "step 1\n", false), Times.Once);
        _telemetryService.Verify(t => t.UpdateShellCommandOutput(It.IsAny<string>(), "warn 1\n", true), Times.Once);
        _telemetryService.Verify(t => t.RecordShellCommandCompleted(It.Is<ShellCommandRecord>(r => r.ExitCode == 0 && r.Status == ShellCommandStatus.Completed)), Times.Once);
    }

    [Fact]
    public async Task RunStreamingAsync_WhenConsumerBreaksEarly_StillRecordsCompletion()
    {
        var spec = new ProcessStartSpec { FileName = "tail", Arguments = "-f file.log" };

        async IAsyncEnumerable<ProcessStreamChunk> MultiChunks()
        {
            yield return new ProcessStreamChunk(ProcessStreamKind.StandardOutput, "line 1\n");
            await Task.Yield();
            yield return new ProcessStreamChunk(ProcessStreamKind.StandardOutput, "line 2\n");
            await Task.Yield();
            yield return new ProcessStreamChunk(ProcessStreamKind.StandardOutput, "line 3\n");
        }

        _innerRunner.Setup(r => r.RunStreamingAsync(spec, It.IsAny<CancellationToken>()))
            .Returns(MultiChunks());

        ShellCommandRecord? completed = null;
        _telemetryService.Setup(t => t.RecordShellCommandCompleted(It.IsAny<ShellCommandRecord>()))
            .Callback<ShellCommandRecord>(r => completed = r);

        await foreach (var _ in _runner.RunStreamingAsync(spec))
        {
            break;
        }

        Assert.NotNull(completed);
        Assert.Equal(ShellCommandStatus.Failed, completed.Status);
        Assert.Equal(-1, completed.ExitCode);
        Assert.Contains("line 1", completed.OutputPreview);
    }

    [Fact]
    public void StartSession_WhenKilled_RecordsFailedStatus()
    {
        var spec = new ProcessStartSpec { FileName = "sleep", Arguments = "60" };
        var innerSession = new Mock<IProcessStreamSession>();
        innerSession.Setup(s => s.ExitCode).Returns((int?)null);

        _innerRunner.Setup(r => r.StartSession(spec)).Returns(innerSession.Object);

        ShellCommandRecord? completed = null;
        _telemetryService.Setup(t => t.RecordShellCommandCompleted(It.IsAny<ShellCommandRecord>()))
            .Callback<ShellCommandRecord>(r => completed = r);

        var session = _runner.StartSession(spec);
        session.Kill();

        Assert.NotNull(completed);
        Assert.Equal(ShellCommandStatus.Failed, completed.Status);
        Assert.Equal(-1, completed.ExitCode);
    }

    [Fact]
    public async Task StartSession_ReadAllAsync_WhenConsumerBreaksEarly_CompletesTelemetry()
    {
        var spec = new ProcessStartSpec { FileName = "bash", Arguments = "-c echo" };
        var innerSession = new Mock<IProcessStreamSession>();
        innerSession.Setup(s => s.ExitCode).Returns(0);

        async IAsyncEnumerable<ProcessStreamChunk> Chunks()
        {
            yield return new ProcessStreamChunk(ProcessStreamKind.StandardOutput, "output line\n");
            await Task.Yield();
            yield return new ProcessStreamChunk(ProcessStreamKind.Exit, "", 0);
        }

        innerSession.Setup(s => s.ReadAllAsync(It.IsAny<CancellationToken>()))
            .Returns(Chunks());

        _innerRunner.Setup(r => r.StartSession(spec)).Returns(innerSession.Object);

        ShellCommandRecord? completed = null;
        _telemetryService.Setup(t => t.RecordShellCommandCompleted(It.IsAny<ShellCommandRecord>()))
            .Callback<ShellCommandRecord>(r => completed = r);

        var session = _runner.StartSession(spec);
        await foreach (var _ in session.ReadAllAsync())
        {
            break;
        }

        Assert.NotNull(completed);
        Assert.Equal(ShellCommandStatus.Completed, completed.Status);
        Assert.Equal(0, completed.ExitCode);
    }

    [Fact]
    public void StartSession_WhenPersistentShellSession_BypassesTelemetry()
    {
        var spec = new ProcessStartSpec
        {
            FileName = "wsl.exe",
            ArgumentList = ["-d", "Ubuntu", "--", "docker", "exec", "-i", "ncf-sandbox", "bash", "--noprofile", "--norc"]
        };
        var innerSession = new Mock<IProcessStreamSession>();
        _innerRunner.Setup(r => r.StartSession(spec)).Returns(innerSession.Object);

        var session = _runner.StartSession(spec);

        Assert.Same(innerSession.Object, session);
        _telemetryService.Verify(t => t.RecordShellCommandStarted(It.IsAny<ShellCommandRecord>()), Times.Never);
        _telemetryService.Verify(t => t.RecordShellCommandCompleted(It.IsAny<ShellCommandRecord>()), Times.Never);
    }

    [Fact]
    public void StartSession_WhenOrdinaryBashCommand_DoesNotBypassTelemetry()
    {
        var spec = new ProcessStartSpec
        {
            FileName = "bash",
            Arguments = "-c \"echo hello\""
        };
        var innerSession = new Mock<IProcessStreamSession>();
        _innerRunner.Setup(r => r.StartSession(spec)).Returns(innerSession.Object);

        var session = _runner.StartSession(spec);

        Assert.NotSame(innerSession.Object, session);
        _telemetryService.Verify(t => t.RecordShellCommandStarted(It.IsAny<ShellCommandRecord>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_RedactsCredentialsAcrossCommandLineStreamingCallbacksAndPreviews()
    {
        var spec = new ProcessStartSpec
        {
            FileName = "git",
            Arguments = "-c \"http.extraHeader=Authorization: Bearer secret_arg_token\" fetch https://user:secret_url_pass@gitlab.example.com/org/repo.git",
            WorkingDirectory = "/repo"
        };

        var startedRecords = new List<ShellCommandRecord>();
        var completedRecords = new List<ShellCommandRecord>();
        var streamedOutputs = new List<(string Chunk, bool IsError)>();

        _telemetryService.Setup(t => t.RecordShellCommandStarted(It.IsAny<ShellCommandRecord>()))
            .Callback<ShellCommandRecord>(r => startedRecords.Add(r));
        _telemetryService.Setup(t => t.RecordShellCommandCompleted(It.IsAny<ShellCommandRecord>()))
            .Callback<ShellCommandRecord>(r => completedRecords.Add(r));
        _telemetryService.Setup(t => t.UpdateShellCommandOutput(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Callback<string, string, bool>((_, chunk, isErr) => streamedOutputs.Add((chunk, isErr)));

        _innerRunner
            .Setup(r => r.RunAsync(spec, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                spec.OnStandardOutput?.Invoke("remote: token ghp_liveStreamSecret01");
                spec.OnStandardError?.Invoke("fatal: unable to access https://alice:liveErrSecret02@gitlab.example.com/org/repo.git");
                return new ProcessExecutionResult(1, "stdout ghp_finalOutSecret03", "stderr https://bob:finalErrSecret04@bitbucket.org/org/repo.git");
            });

        var result = await _runner.RunAsync(spec);

        Assert.Equal(1, result.ExitCode);
        var started = Assert.Single(startedRecords);
        Assert.DoesNotContain("secret_arg_token", started.CommandLine);
        Assert.DoesNotContain("secret_url_pass", started.CommandLine);
        Assert.Contains(NightCodeForge.Core.Security.GitCredentialRedactor.RedactedPlaceholder, started.CommandLine);

        Assert.Contains(streamedOutputs, s => !s.IsError && !s.Chunk.Contains("ghp_liveStreamSecret01") && s.Chunk.Contains(NightCodeForge.Core.Security.GitCredentialRedactor.RedactedPlaceholder));
        Assert.Contains(streamedOutputs, s => s.IsError && !s.Chunk.Contains("liveErrSecret02") && s.Chunk.Contains(NightCodeForge.Core.Security.GitCredentialRedactor.RedactedPlaceholder));

        var completed = Assert.Single(completedRecords);
        Assert.DoesNotContain("secret_arg_token", completed.CommandLine);
        Assert.DoesNotContain("ghp_finalOutSecret03", completed.OutputPreview);
        Assert.DoesNotContain("finalErrSecret04", completed.ErrorPreview);
        Assert.Contains(NightCodeForge.Core.Security.GitCredentialRedactor.RedactedPlaceholder, completed.OutputPreview);
        Assert.Contains(NightCodeForge.Core.Security.GitCredentialRedactor.RedactedPlaceholder, completed.ErrorPreview);
    }

    [Fact]
    public async Task RunRawAsync_BinaryStdout_ForwardsRawBytesAndRecordsSafeTelemetryPlaceholder()
    {
        var spec = new ProcessStartSpec { FileName = "git", Arguments = "show HEAD:bin.dat", WorkingDirectory = "/repo" };
        var rawBytes = new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 0x00, 0x01 };

        ShellCommandRecord? completed = null;
        _telemetryService.Setup(t => t.RecordShellCommandCompleted(It.IsAny<ShellCommandRecord>()))
            .Callback<ShellCommandRecord>(r => completed = r);
        _innerRunner.Setup(r => r.RunRawAsync(spec, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessRawExecutionResult(0, rawBytes, string.Empty));

        var result = await _runner.RunRawAsync(spec);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(rawBytes, result.StandardOutputBytes);
        Assert.NotNull(completed);
        Assert.Contains("[binary output: 6 bytes]", completed!.OutputPreview);
    }

    [Fact]
    public async Task RunRawAsync_Utf16BomTextStdout_DecodesReadablePreviewWithoutGarbling()
    {
        var spec = new ProcessStartSpec { FileName = "git", Arguments = "show HEAD:utf16.txt", WorkingDirectory = "/repo" };
        var bodyBytes = System.Text.Encoding.Unicode.GetBytes("Hello UTF-16!");
        var rawBytes = new byte[2 + bodyBytes.Length];
        rawBytes[0] = 0xFF;
        rawBytes[1] = 0xFE;
        bodyBytes.CopyTo(rawBytes, 2);

        ShellCommandRecord? completed = null;
        _telemetryService.Setup(t => t.RecordShellCommandCompleted(It.IsAny<ShellCommandRecord>()))
            .Callback<ShellCommandRecord>(r => completed = r);
        _innerRunner.Setup(r => r.RunRawAsync(spec, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessRawExecutionResult(0, rawBytes, string.Empty));

        var result = await _runner.RunRawAsync(spec);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(completed);
        Assert.Equal("Hello UTF-16!", completed!.OutputPreview);
    }
}
