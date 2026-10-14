using NightCodeForge.Core.Interfaces;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class ProcessRunnerExtensionsTests
{
    private class RecordingProcessRunner : IProcessRunner
    {
        public ProcessStartSpec? LastSpec { get; private set; }
        public int RunCount { get; private set; }
        public int RunAsyncCount { get; private set; }
        public int RunStreamingCount { get; private set; }

        public ProcessExecutionResult Run(ProcessStartSpec spec)
        {
            RunCount++;
            LastSpec = spec;
            return new ProcessExecutionResult(0, "sync out", "sync err");
        }

        public Task<ProcessExecutionResult> RunAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default)
        {
            RunAsyncCount++;
            LastSpec = spec;
            spec.OnStandardOutput?.Invoke("stream line out");
            spec.OnStandardError?.Invoke("stream line err");
            return Task.FromResult(new ProcessExecutionResult(0, "async out", "async err"));
        }

        public async IAsyncEnumerable<ProcessStreamChunk> RunStreamingAsync(ProcessStartSpec spec, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RunStreamingCount++;
            LastSpec = spec;
            yield return new ProcessStreamChunk(ProcessStreamKind.StandardOutput, "stream chunk");
            yield return new ProcessStreamChunk(ProcessStreamKind.Exit, string.Empty, 0);
        }

        public IProcessStreamSession StartSession(ProcessStartSpec spec)
        {
            throw new NotImplementedException();
        }
    }

    [Fact]
    public void Run_WithArgumentsString_PassesCorrectSpec()
    {
        var runner = new RecordingProcessRunner();
        var result = runner.Run("git", "status --short", "/repo", 25);

        Assert.Equal(1, runner.RunCount);
        Assert.NotNull(runner.LastSpec);
        Assert.Equal("git", runner.LastSpec.FileName);
        Assert.Equal("status --short", runner.LastSpec.Arguments);
        Assert.Null(runner.LastSpec.ArgumentList);
        Assert.Equal("/repo", runner.LastSpec.WorkingDirectory);
        Assert.Equal(25, runner.LastSpec.TimeoutSeconds);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("sync out", result.StandardOutput);
    }

    [Fact]
    public void Run_WithArgumentsList_PassesCorrectSpec()
    {
        var runner = new RecordingProcessRunner();
        var result = runner.Run("git", ["commit", "-m", "hello world"], "/repo", 15);

        Assert.Equal(1, runner.RunCount);
        Assert.NotNull(runner.LastSpec);
        Assert.Equal("git", runner.LastSpec.FileName);
        Assert.Null(runner.LastSpec.Arguments);
        Assert.Equal(new[] { "commit", "-m", "hello world" }, runner.LastSpec.ArgumentList);
        Assert.Equal("/repo", runner.LastSpec.WorkingDirectory);
        Assert.Equal(15, runner.LastSpec.TimeoutSeconds);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_WithArgumentsString_PassesCorrectSpec()
    {
        var runner = new RecordingProcessRunner();
        var result = await runner.RunAsync("dotnet", "build -c Release", "/work", 45);

        Assert.Equal(1, runner.RunAsyncCount);
        Assert.NotNull(runner.LastSpec);
        Assert.Equal("dotnet", runner.LastSpec.FileName);
        Assert.Equal("build -c Release", runner.LastSpec.Arguments);
        Assert.Equal("/work", runner.LastSpec.WorkingDirectory);
        Assert.Equal(45, runner.LastSpec.TimeoutSeconds);
        Assert.Equal("async out", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_WithArgumentsList_PassesCorrectSpec()
    {
        var runner = new RecordingProcessRunner();
        var result = await runner.RunAsync("dotnet", ["test", "--no-build"], "/work", 30);

        Assert.Equal(1, runner.RunAsyncCount);
        Assert.NotNull(runner.LastSpec);
        Assert.Equal("dotnet", runner.LastSpec.FileName);
        Assert.Equal(new[] { "test", "--no-build" }, runner.LastSpec.ArgumentList);
        Assert.Equal("/work", runner.LastSpec.WorkingDirectory);
        Assert.Equal(30, runner.LastSpec.TimeoutSeconds);
        Assert.Equal("async out", result.StandardOutput);
    }

    [Fact]
    public async Task RunWithCallbacksAsync_WithArgumentsList_InvokesCallbacksAndConfiguresSpec()
    {
        var runner = new RecordingProcessRunner();
        var outLines = new List<string>();
        var errLines = new List<string>();

        var result = await runner.RunWithCallbacksAsync(
            "node",
            ["index.js"],
            onStandardOutput: line => outLines.Add(line),
            onStandardError: line => errLines.Add(line),
            workingDirectory: "/app",
            timeoutSeconds: 20,
            standardInput: "input data");

        Assert.Equal(1, runner.RunAsyncCount);
        Assert.NotNull(runner.LastSpec);
        Assert.Equal("node", runner.LastSpec.FileName);
        Assert.Equal(new[] { "index.js" }, runner.LastSpec.ArgumentList);
        Assert.Equal("/app", runner.LastSpec.WorkingDirectory);
        Assert.Equal(20, runner.LastSpec.TimeoutSeconds);
        Assert.Equal("input data", runner.LastSpec.StandardInput);
        Assert.Single(outLines, "stream line out");
        Assert.Single(errLines, "stream line err");
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunWithCallbacksAsync_WithArgumentsString_InvokesCallbacksAndConfiguresSpec()
    {
        var runner = new RecordingProcessRunner();
        var outLines = new List<string>();

        var result = await runner.RunWithCallbacksAsync(
            "node",
            "server.js",
            onStandardOutput: line => outLines.Add(line),
            workingDirectory: "/app");

        Assert.Equal(1, runner.RunAsyncCount);
        Assert.NotNull(runner.LastSpec);
        Assert.Equal("node", runner.LastSpec.FileName);
        Assert.Equal("server.js", runner.LastSpec.Arguments);
        Assert.Single(outLines, "stream line out");
    }

    [Fact]
    public async Task RunStreamingAsync_WithArgumentsList_YieldsStreamChunks()
    {
        var runner = new RecordingProcessRunner();
        var chunks = new List<ProcessStreamChunk>();

        await foreach (var chunk in runner.RunStreamingAsync("python", ["main.py"], "/scripts", 120, "input"))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(1, runner.RunStreamingCount);
        Assert.NotNull(runner.LastSpec);
        Assert.Equal("python", runner.LastSpec.FileName);
        Assert.Equal(new[] { "main.py" }, runner.LastSpec.ArgumentList);
        Assert.Equal("/scripts", runner.LastSpec.WorkingDirectory);
        Assert.Equal(120, runner.LastSpec.TimeoutSeconds);
        Assert.Equal("input", runner.LastSpec.StandardInput);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(ProcessStreamKind.StandardOutput, chunks[0].Kind);
        Assert.Equal(ProcessStreamKind.Exit, chunks[1].Kind);
    }

    [Fact]
    public async Task RunStreamingAsync_WithArgumentsString_YieldsStreamChunks()
    {
        var runner = new RecordingProcessRunner();
        var chunks = new List<ProcessStreamChunk>();

        await foreach (var chunk in runner.RunStreamingAsync("python", "-u main.py"))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(1, runner.RunStreamingCount);
        Assert.NotNull(runner.LastSpec);
        Assert.Equal("python", runner.LastSpec.FileName);
        Assert.Equal("-u main.py", runner.LastSpec.Arguments);
        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public async Task Extensions_ThrowOnNullRunner()
    {
        IProcessRunner? nullRunner = null;

        Assert.Throws<ArgumentNullException>(() => nullRunner!.Run("ls", "-la"));
        Assert.Throws<ArgumentNullException>(() => nullRunner!.Run("ls", ["-la"]));
        await Assert.ThrowsAsync<ArgumentNullException>(() => nullRunner!.RunAsync("ls", "-la"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => nullRunner!.RunAsync("ls", ["-la"]));
        await Assert.ThrowsAsync<ArgumentNullException>(() => nullRunner!.RunWithCallbacksAsync("ls", "-la", null));
        await Assert.ThrowsAsync<ArgumentNullException>(() => nullRunner!.RunWithCallbacksAsync("ls", ["-la"], null));
        Assert.Throws<ArgumentNullException>(() => nullRunner!.RunStreamingAsync("ls", "-la"));
        Assert.Throws<ArgumentNullException>(() => nullRunner!.RunStreamingAsync("ls", ["-la"]));
    }
}
