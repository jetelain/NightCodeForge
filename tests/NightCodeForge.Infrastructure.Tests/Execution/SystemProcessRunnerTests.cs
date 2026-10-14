using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public class SystemProcessRunnerTests
{
    private readonly SystemProcessRunner _runner = new();

    [Fact]
    public void Run_WithArgumentList_ExecutesSuccessfully()
    {
        // Act: Run dotnet --version via ArgumentList
        var result = _runner.Run("dotnet", ["--version"], timeoutSeconds: 10);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
        Assert.True(string.IsNullOrEmpty(result.StandardError));
    }

    [Fact]
    public async Task RunAsync_WithArgumentList_ExecutesSuccessfully()
    {
        var result = await _runner.RunAsync("dotnet", ["--version"], timeoutSeconds: 10);

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
    }

    [Fact]
    public async Task RunWithCallbacksAsync_StreamsStandardOutputLines()
    {
        var capturedLines = new List<string>();

        var result = await _runner.RunWithCallbacksAsync(
            "dotnet",
            ["--version"],
            onStandardOutput: line => capturedLines.Add(line),
            timeoutSeconds: 10);

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(capturedLines);
        Assert.Contains(capturedLines[0], result.StandardOutput);
    }

    [Fact]
    public async Task RunStreamingAsync_StreamsOutputChunksAndExitNotification()
    {
        var chunks = new List<ProcessStreamChunk>();

        await foreach (var chunk in _runner.RunStreamingAsync("dotnet", ["--version"], timeoutSeconds: 10))
        {
            chunks.Add(chunk);
        }

        Assert.NotEmpty(chunks);

        // Verify stdout chunks
        var stdoutChunks = chunks.Where(c => c.Kind == ProcessStreamKind.StandardOutput).ToList();
        Assert.NotEmpty(stdoutChunks);

        // Verify exit notification
        var exitChunk = chunks.LastOrDefault(c => c.Kind == ProcessStreamKind.Exit);
        Assert.NotNull(exitChunk);
        Assert.Equal(0, exitChunk.ExitCode);
    }

    [Fact]
    public async Task StartSession_AllowsLiveSessionInteraction()
    {
        var spec = new ProcessStartSpec
        {
            FileName = "dotnet",
            ArgumentList = ["--version"],
            TimeoutSeconds = 10
        };

        var session = _runner.StartSession(spec);
        await using (session.ConfigureAwait(false))
        {
            var exitCode = await session.WaitForExitAsync();
            Assert.Equal(0, exitCode);
            Assert.True(session.HasExited);
            Assert.Equal(0, session.ExitCode);
        }
    }

    [Fact]
    public async Task RunAsync_WhenTimedOut_KillsProcessAndReturnsTimeoutCode()
    {
        // Start a sleep command with 1s timeout
        var isWindows = OperatingSystem.IsWindows();
        var fileName = isWindows ? "powershell.exe" : "sleep";
        var args = isWindows ? new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 10" } : new[] { "10" };

        var result = await _runner.RunAsync(fileName, args, timeoutSeconds: 1);

        Assert.Equal(124, result.ExitCode);
        Assert.Contains("timed out", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunStreamingAsync_WhenProcessNotFound_YieldsErrorAndExit()
    {
        var chunks = new List<ProcessStreamChunk>();

        await foreach (var chunk in _runner.RunStreamingAsync("non_existent_binary_xyz_12345", ["--arg"]))
        {
            chunks.Add(chunk);
        }

        Assert.Contains(chunks, c => c.Kind == ProcessStreamKind.StandardError && c.ExitCode == -1);
        Assert.Contains(chunks, c => c.Kind == ProcessStreamKind.Exit && c.ExitCode == -1);
    }
}
