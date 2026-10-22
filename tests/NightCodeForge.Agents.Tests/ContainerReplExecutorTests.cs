using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Agents.Tests;

public class ContainerReplExecutorTests
{
    [Fact]
    public void HeadTailBuffer_Truncation_PreservesHeadAndTailWithMarker()
    {
        var buffer = new HeadTailBuffer(maxBytes: 1024);
        var chunk = new string('A', 500) + new string('B', 1000) + new string('C', 500);

        buffer.Append(chunk);

        Assert.True(buffer.IsTruncated);
        var formatted = buffer.GetFormattedString();
        Assert.Contains("[... truncated", formatted);
        Assert.StartsWith("AAAAA", formatted);
        Assert.EndsWith("CCCCC", formatted);
    }

    [Fact]
    public void HeadTailBuffer_UnderLimit_DoesNotTruncate()
    {
        var buffer = new HeadTailBuffer(maxBytes: 1024);
        buffer.Append("Short output string\n");

        Assert.False(buffer.IsTruncated);
        var formatted = buffer.GetFormattedString();
        Assert.DoesNotContain("[... truncated", formatted);
        Assert.Equal("Short output string\n", formatted);
    }

    [Fact]
    public void HeadTailBuffer_ParameterlessConstructor_UsesDefaultMaxBytes()
    {
        var buffer = new HeadTailBuffer();
        Assert.Equal(HeadTailBuffer.DefaultMaxBytes, buffer.MaxBytes);
        Assert.False(buffer.IsTruncated);
    }

    [Fact]
    public async Task ContainerExecutor_SentinelExecution_EvaluatesSuccess()
    {
        // Spawns interactive cmd.exe or powershell that responds with sentinels
        var options = new ContainerExecutorOptions
        {
            CustomProcessFactory = () =>
            {
                return new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoProfile -NonInteractive -Command \"while ($line = [Console]::ReadLine()) { if ($line -match '(__NCF_SENTINEL_[a-zA-Z0-9_]+__)') { [Console]::WriteLine('hello world'); [Console]::WriteLine(\\\"$($Matches[1]) 0\\\") } }\"",
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
            },
            DefaultWorkingDirectory = Path.GetTempPath(),
            DefaultTimeoutSeconds = 10
        };

        using var executor = new NightCodeForgeContainerExecutor(
            options,
            new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance),
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            new Mock<IHostUserIdentityResolver>().Object,
            new Mock<IOperationsTelemetryService>().Object);
        var result = await executor.ExecuteAsync("echo hello world");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello world", result.StandardOutput);
    }

    [Fact]
    public async Task ContainerExecutor_WithMockProcessRunner_ExecutesCommandsWithoutSpawningProcess()
    {
        var mockRunner = new MockProcessRunner();
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "mock-sandbox",
            DefaultWorkingDirectory = "/workspace",
            DefaultTimeoutSeconds = 5
        };

        using var executor = new NightCodeForgeContainerExecutor(
            options,
            mockRunner,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            new Mock<IHostUserIdentityResolver>().Object,
            new Mock<IOperationsTelemetryService>().Object);
        var result = await executor.ExecuteAsync("echo mock-result");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("mock-result", result.StandardOutput);
        Assert.NotNull(mockRunner.LastSpec);
        Assert.Equal("docker", mockRunner.LastSpec.FileName);
        Assert.Contains("mock-sandbox", mockRunner.LastSpec.ArgumentList!);
    }

    [Fact]
    public async Task ContainerExecutor_ExecuteAsync_RecordsShellCommandTelemetry()
    {
        var mockRunner = new MockProcessRunner();
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "mock-sandbox",
            DefaultWorkingDirectory = "/workspace",
            DefaultTimeoutSeconds = 5
        };

        ShellCommandRecord? startedRecord = null;
        ShellCommandRecord? completedRecord = null;

        telemetryMock
            .Setup(t => t.RecordShellCommandStarted(It.IsAny<ShellCommandRecord>()))
            .Callback<ShellCommandRecord>(r => startedRecord = r);

        telemetryMock
            .Setup(t => t.RecordShellCommandCompleted(It.IsAny<ShellCommandRecord>()))
            .Callback<ShellCommandRecord>(r => completedRecord = r);

        using var executor = new NightCodeForgeContainerExecutor(
            options,
            mockRunner,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            new Mock<IHostUserIdentityResolver>().Object,
            telemetryMock.Object);

        var result = await executor.ExecuteAsync("dotnet test --filter Category!=Acceptance", workingDirectory: "/workspace/tests");

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(startedRecord);
        Assert.Equal("dotnet test --filter Category!=Acceptance", startedRecord.CommandLine);
        Assert.Equal("/workspace/tests", startedRecord.WorkingDirectory);
        Assert.Equal(NightCodeForge.Core.Workflow.ShellCommandStatus.Running, startedRecord.Status);

        Assert.NotNull(completedRecord);
        Assert.Equal(startedRecord.CommandId, completedRecord.CommandId);
        Assert.Equal(0, completedRecord.ExitCode);
        Assert.Equal(NightCodeForge.Core.Workflow.ShellCommandStatus.Completed, completedRecord.Status);
        Assert.Contains("mock-result", completedRecord.OutputPreview);
    }

    [Fact]
    public async Task ContainerExecutor_ExecuteAsync_WhenStdinWriteFails_MarksShellCommandFailedAndRethrows()
    {
        var mockRunner = new MockThrowingStdinProcessRunner();
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "mock-sandbox",
            DefaultWorkingDirectory = "/workspace",
            DefaultTimeoutSeconds = 5
        };

        ShellCommandRecord? startedRecord = null;
        ShellCommandRecord? completedRecord = null;

        telemetryMock
            .Setup(t => t.RecordShellCommandStarted(It.IsAny<ShellCommandRecord>()))
            .Callback<ShellCommandRecord>(r => startedRecord = r);

        telemetryMock
            .Setup(t => t.RecordShellCommandCompleted(It.IsAny<ShellCommandRecord>()))
            .Callback<ShellCommandRecord>(r => completedRecord = r);

        using var executor = new NightCodeForgeContainerExecutor(
            options,
            mockRunner,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            new Mock<IHostUserIdentityResolver>().Object,
            telemetryMock.Object);

        var ex = await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync("echo hello", workingDirectory: "/workspace"));
        Assert.Equal("Pipe broken", ex.Message);

        Assert.NotNull(startedRecord);
        Assert.NotNull(completedRecord);
        Assert.Equal(startedRecord.CommandId, completedRecord.CommandId);
        Assert.Equal(NightCodeForge.Core.Workflow.ShellCommandStatus.Failed, completedRecord.Status);
        Assert.Equal(-1, completedRecord.ExitCode);
        Assert.Equal("Pipe broken", completedRecord.ErrorPreview);
    }

    private class MockThrowingStdinProcessRunner : NightCodeForge.Core.Interfaces.IProcessRunner
    {
        public NightCodeForge.Core.Interfaces.IProcessStreamSession StartSession(NightCodeForge.Core.Interfaces.ProcessStartSpec spec)
        {
            return new MockThrowingStdinSession();
        }

        public NightCodeForge.Core.Interfaces.ProcessExecutionResult Run(NightCodeForge.Core.Interfaces.ProcessStartSpec spec) => throw new NotImplementedException();
        public Task<NightCodeForge.Core.Interfaces.ProcessExecutionResult> RunAsync(NightCodeForge.Core.Interfaces.ProcessStartSpec spec, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<NightCodeForge.Core.Interfaces.ProcessStreamChunk> RunStreamingAsync(NightCodeForge.Core.Interfaces.ProcessStartSpec spec, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private class MockThrowingStdinSession : NightCodeForge.Core.Interfaces.IProcessStreamSession
    {
        private readonly StreamWriter _stdin;
        private readonly StreamReader _stdout;
        private readonly StreamReader _stderr;

        public int? ExitCode => null;
        public bool HasExited => false;
        public StreamWriter StandardInput => _stdin;
        public StreamReader StandardOutput => _stdout;
        public StreamReader StandardError => _stderr;

        public MockThrowingStdinSession()
        {
            var throwStream = new DelegatingWriteStream(bytes =>
            {
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                if (text.Contains("echo hello"))
                {
                    throw new IOException("Pipe broken");
                }
            });
            _stdin = new StreamWriter(throwStream) { AutoFlush = true, NewLine = "\n" };
            _stdout = new StreamReader(new MemoryStream());
            _stderr = new StreamReader(new MemoryStream());
        }

        public IAsyncEnumerable<NightCodeForge.Core.Interfaces.ProcessStreamChunk> ReadAllAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => Task.FromResult(-1);
        public void Kill() { }
        public void Dispose()
        {
            _stdin.Dispose();
            _stdout.Dispose();
            _stderr.Dispose();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class MockProcessRunner : NightCodeForge.Core.Interfaces.IProcessRunner
    {
        public NightCodeForge.Core.Interfaces.ProcessStartSpec? LastSpec { get; private set; }

        public NightCodeForge.Core.Interfaces.IProcessStreamSession StartSession(NightCodeForge.Core.Interfaces.ProcessStartSpec spec)
        {
            LastSpec = spec;
            return new MockChannelStreamSession();
        }

        public NightCodeForge.Core.Interfaces.ProcessExecutionResult Run(NightCodeForge.Core.Interfaces.ProcessStartSpec spec) => throw new NotImplementedException();
        public Task<NightCodeForge.Core.Interfaces.ProcessExecutionResult> RunAsync(NightCodeForge.Core.Interfaces.ProcessStartSpec spec, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<NightCodeForge.Core.Interfaces.ProcessStreamChunk> RunStreamingAsync(NightCodeForge.Core.Interfaces.ProcessStartSpec spec, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private class MockChannelStreamSession : NightCodeForge.Core.Interfaces.IProcessStreamSession
    {
        private readonly System.Threading.Channels.Channel<byte[]> _pipe = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private readonly StreamWriter _stdin;
        private readonly StreamReader _stdout;
        private readonly StreamReader _stderr;
        private readonly MemoryStream _stderrMs = new();

        public int? ExitCode => 0;
        public bool HasExited => false;
        public StreamWriter StandardInput => _stdin;
        public StreamReader StandardOutput => _stdout;
        public StreamReader StandardError => _stderr;

        public MockChannelStreamSession()
        {
            var inStream = new DelegatingWriteStream(bytes =>
            {
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                var match = System.Text.RegularExpressions.Regex.Match(text, @"(__NCF_SENTINEL_[a-zA-Z0-9_]+__)");
                if (match.Success)
                {
                    var response = System.Text.Encoding.UTF8.GetBytes($"mock-result\n{match.Groups[1].Value} 0\n");
                    _pipe.Writer.TryWrite(response);
                }
            });

            var outStream = new DelegatingReadStream(_pipe.Reader);

            _stdin = new StreamWriter(inStream, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            _stdout = new StreamReader(outStream, System.Text.Encoding.UTF8);
            _stderr = new StreamReader(_stderrMs, System.Text.Encoding.UTF8);
        }

        public IAsyncEnumerable<NightCodeForge.Core.Interfaces.ProcessStreamChunk> ReadAllAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public void Kill() { }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class DelegatingWriteStream : Stream
    {
        private readonly Action<byte[]> _onWrite;
        public DelegatingWriteStream(Action<byte[]> onWrite) => _onWrite = onWrite;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            _onWrite(copy);
        }
    }

    private class DelegatingReadStream : Stream
    {
        private readonly System.Threading.Channels.ChannelReader<byte[]> _reader;
        private byte[]? _currentChunk;
        private int _chunkPos;

        public DelegatingReadStream(System.Threading.Channels.ChannelReader<byte[]> reader) => _reader = reader;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_currentChunk == null || _chunkPos >= _currentChunk.Length)
            {
                if (!await _reader.WaitToReadAsync(cancellationToken))
                    return 0;

                if (!_reader.TryRead(out _currentChunk))
                    return 0;

                _chunkPos = 0;
            }

            int toCopy = Math.Min(count, _currentChunk.Length - _chunkPos);
            Buffer.BlockCopy(_currentChunk, _chunkPos, buffer, offset, toCopy);
            _chunkPos += toCopy;
            return toCopy;
        }
    }
}
