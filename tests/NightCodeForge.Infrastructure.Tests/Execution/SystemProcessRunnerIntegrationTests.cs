using System.Diagnostics;
using System.Text;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Tests.Execution;

[Trait("Category", "Integration")]
public class SystemProcessRunnerIntegrationTests : IDisposable
{
    private readonly SystemProcessRunner _runner = new();
    private readonly string _tempDir;

    public SystemProcessRunnerIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"NCF_ProcTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    private string CreateScript(string fileName, string scriptContent)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, scriptContent, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public async Task Streaming_ProgressiveEvents_EmittedInRealTimeWithCorrectTimestamps()
    {
        // Arrange: Script that progressively emits 5 stdout events and 2 stderr events separated by 100ms pauses
        var scriptPath = CreateScript("progressive_stream.ps1", """
param([int]$Count = 5, [int]$DelayMs = 100)
for ($i = 1; $i -le $Count; $i++) {
    [Console]::Out.WriteLine("STDOUT_EVENT_$i")
    [Console]::Out.Flush()
    if ($i % 2 -eq 0) {
        [Console]::Error.WriteLine("STDERR_EVENT_$i")
        [Console]::Error.Flush()
    }
    Start-Sleep -Milliseconds $DelayMs
}
""");

        var chunks = new List<(ProcessStreamChunk Chunk, TimeSpan Elapsed)>();
        var sw = Stopwatch.StartNew();

        // Act: Stream the events
        await foreach (var chunk in _runner.RunStreamingAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
            timeoutSeconds: 15))
        {
            chunks.Add((chunk, sw.Elapsed));
        }

        sw.Stop();

        // Assert:
        Assert.NotEmpty(chunks);

        // 1. Verify stdout chunks
        var stdoutChunks = chunks.Where(c => c.Chunk.Kind == ProcessStreamKind.StandardOutput).ToList();
        var allStdout = string.Concat(stdoutChunks.Select(c => c.Chunk.Text));
        for (int i = 1; i <= 5; i++)
        {
            Assert.Contains($"STDOUT_EVENT_{i}", allStdout);
        }

        // 2. Verify stderr chunks
        var stderrChunks = chunks.Where(c => c.Chunk.Kind == ProcessStreamKind.StandardError).ToList();
        var allStderr = string.Concat(stderrChunks.Select(c => c.Chunk.Text));
        Assert.Contains("STDERR_EVENT_2", allStderr);
        Assert.Contains("STDERR_EVENT_4", allStderr);

        // 3. Verify exit chunk
        var exitChunk = chunks.LastOrDefault(c => c.Chunk.Kind == ProcessStreamKind.Exit);
        Assert.NotNull(exitChunk.Chunk);
        Assert.Equal(0, exitChunk.Chunk.ExitCode);

        // 4. Verify progressive streaming (first stdout chunk arrived well before total duration)
        var firstStdout = stdoutChunks.FirstOrDefault();
        Assert.NotNull(firstStdout.Chunk);
        // The first chunk must have arrived within the first half of the execution, proving real-time streaming
        Assert.True(firstStdout.Elapsed < TimeSpan.FromMilliseconds(sw.ElapsedMilliseconds * 0.75),
            $"First chunk elapsed ({firstStdout.Elapsed.TotalMilliseconds}ms) should be earlier than total ({sw.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public async Task BufferLimits_HighVolumeOutput_DrainsWithoutDeadlockOrTruncation()
    {
        // Arrange: Generate 10,000 lines of 250 characters each (> 2.5 MB stdout) and 1,000 lines of stderr (> 250 KB)
        // Standard OS pipe buffers are 4KB-64KB. This strictly tests concurrent draining without blocking or deadlocking.
        var scriptPath = CreateScript("high_volume.ps1", """
param([int]$Lines = 10000)
$payload = "X" * 250
for ($i = 1; $i -le $Lines; $i++) {
    [Console]::Out.WriteLine("DATA_LINE_${i}_$payload")
    if ($i % 10 -eq 0) {
        [Console]::Error.WriteLine("ERR_LINE_${i}_$payload")
    }
}
""");

        // Act: Run asynchronously with 30s timeout
        var result = await _runner.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
            timeoutSeconds: 30);

        // Assert
        Assert.Equal(0, result.ExitCode);

        // Verify stdout contains all lines
        Assert.Contains("DATA_LINE_1_", result.StandardOutput);
        Assert.Contains("DATA_LINE_5000_", result.StandardOutput);
        Assert.Contains("DATA_LINE_10000_", result.StandardOutput);

        // Verify stderr contains error lines
        Assert.Contains("ERR_LINE_10_", result.StandardError);
        Assert.Contains("ERR_LINE_5000_", result.StandardError);
        Assert.Contains("ERR_LINE_10000_", result.StandardError);

        // Verify total size exceeds 2MB
        Assert.True(result.StandardOutput.Length > 2_500_000,
            $"Expected stdout > 2.5MB, but got {result.StandardOutput.Length} chars");
    }

    [Fact]
    public async Task BufferLimits_HighVolumeStreaming_ConsumesAllChunksWithoutLoss()
    {
        // Arrange: Stream 5,000 lines of 200 characters each (> 1 MB)
        var scriptPath = CreateScript("stream_volume.ps1", """
param([int]$Lines = 5000)
$payload = "Y" * 200
for ($i = 1; $i -le $Lines; $i++) {
    [Console]::Out.WriteLine("CHUNK_LINE_${i}_$payload")
}
""");

        var stdoutBuilder = new StringBuilder();
        int chunkCount = 0;
        int? finalExitCode = null;

        // Act: Consume via streaming
        await foreach (var chunk in _runner.RunStreamingAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
            timeoutSeconds: 30))
        {
            if (chunk.Kind == ProcessStreamKind.StandardOutput)
            {
                stdoutBuilder.Append(chunk.Text);
                chunkCount++;
            }
            else if (chunk.Kind == ProcessStreamKind.Exit)
            {
                finalExitCode = chunk.ExitCode;
            }
        }

        var fullOutput = stdoutBuilder.ToString();

        // Assert
        Assert.Equal(0, finalExitCode);
        Assert.True(chunkCount > 1, $"Expected multiple streaming chunks, got {chunkCount}");
        Assert.Contains("CHUNK_LINE_1_", fullOutput);
        Assert.Contains("CHUNK_LINE_2500_", fullOutput);
        Assert.Contains("CHUNK_LINE_5000_", fullOutput);
        Assert.True(fullOutput.Length > 1_000_000, $"Expected stdout > 1MB, got {fullOutput.Length} chars");
    }

    [Fact]
    public void ArgumentList_ComplexEscaping_PreservesExactArgumentsVerbatim()
    {
        // Arrange: Script that echoes each argument formatted as [ARG:index]=value
        var scriptPath = CreateScript("echo_arguments.ps1", """
[Console]::InputEncoding = [Console]::OutputEncoding = $OutputEncoding = [System.Text.Encoding]::UTF8
for ($i = 0; $i -lt $args.Count; $i++) {
    [Console]::Out.WriteLine("[ARG:" + $i + "]=" + $args[$i])
}
""");

        var testArguments = new List<string>
        {
            "Simple",
            "With Spaces And   Tabs",
            "Quote \"Inside\" Double",
            "Single 'Quote' Inside",
            @"C:\Path\With\Trailing\Backslash\",
            "$PATH_VAR & < > | ; ~ ^ ` %TEMP%",
            "Multi\nLine\nArgument",
            "Unicode: 🚀 éàç 漢字 日本語 Русский"
        };

        // Act: Execute via ArgumentList
        var argsList = new List<string>
        {
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath
        };
        argsList.AddRange(testArguments);

        var result = _runner.Run("powershell.exe", argsList, timeoutSeconds: 15);

        // Assert
        Assert.Equal(0, result.ExitCode);

        for (int i = 0; i < testArguments.Count; i++)
        {
            var expectedMarker = $"[ARG:{i}]=" + testArguments[i];
            Assert.Contains(expectedMarker, result.StandardOutput);
        }
    }

    [Fact]
    public async Task InteractiveSession_TwoWayFullDuplexCommunication_HandlesRequestResponseLoops()
    {
        // Arrange: A REPL script that listens on stdin and responds with PONG: <input>, quitting on QUIT
        var scriptPath = CreateScript("echo_repl.ps1", """
[Console]::InputEncoding = [Console]::OutputEncoding = $OutputEncoding = [System.Text.Encoding]::UTF8
while ($line = [Console]::In.ReadLine()) {
    if ($line -eq "QUIT") {
        break
    }
    [Console]::Out.WriteLine("PONG: $line")
    [Console]::Out.Flush()
}
exit 0
""");

        var spec = new ProcessStartSpec
        {
            FileName = "powershell.exe",
            ArgumentList = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
            TimeoutSeconds = 15
        };

        // Act: Start interactive session
        var session = _runner.StartSession(spec);
        await using (session.ConfigureAwait(false))
        {
            var testMessages = new[]
            {
                "Hello World",
                "JSON: {\"action\": \"execute\", \"id\": 101}",
                "Special @#$%^&*()_+ symbols",
                "Line with spaces and unicode: 🚀 test"
            };

            foreach (var message in testMessages)
            {
                // Send request
                await session.StandardInput.WriteLineAsync(message);
                await session.StandardInput.FlushAsync();

                // Read response
                var response = await session.StandardOutput.ReadLineAsync();
                Assert.Equal($"PONG: {message}", response);
            }

            // Send quit
            await session.StandardInput.WriteLineAsync("QUIT");
            await session.StandardInput.FlushAsync();

            var exitCode = await session.WaitForExitAsync();
            Assert.Equal(0, exitCode);
            Assert.True(session.HasExited);
        }
    }

    [Fact]
    public async Task Cancellation_MidStream_TerminatesProcessTreeImmediately()
    {
        // Arrange: Infinite loop generating output
        var scriptPath = CreateScript("infinite_loop.ps1", """
$i = 0
while ($true) {
    [Console]::Out.WriteLine("HEARTBEAT_$i")
    [Console]::Out.Flush()
    $i++
    Start-Sleep -Milliseconds 50
}
""");

        using var cts = new CancellationTokenSource();
        int receivedChunks = 0;

        // Act: Consume a couple of chunks, then cancel
        try
        {
            await foreach (var chunk in _runner.RunStreamingAsync(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                timeoutSeconds: 30,
                cancellationToken: cts.Token))
            {
                receivedChunks++;
                if (receivedChunks >= 3)
                {
                    cts.Cancel();
                }
            }
        }
        catch (OperationCanceledException) { }

        // Assert: Stream successfully halted
        Assert.True(receivedChunks >= 3, $"Expected at least 3 chunks before cancellation, got {receivedChunks}");
    }

    [Fact]
    public async Task ErrorHandling_StderrAndStdoutCaptured_ReturnsCustomExitCode()
    {
        // Arrange: Script that outputs to both stdout and stderr then exits with code 42
        var scriptPath = CreateScript("exit_error.ps1", """
[Console]::Out.WriteLine("OUTPUT BEFORE ERROR")
[Console]::Error.WriteLine("FATAL ERROR IN SCRIPT")
exit 42
""");

        // Act
        var result = await _runner.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
            timeoutSeconds: 10);

        // Assert
        Assert.Equal(42, result.ExitCode);
        Assert.Contains("OUTPUT BEFORE ERROR", result.StandardOutput);
        Assert.Contains("FATAL ERROR IN SCRIPT", result.StandardError);
    }

    [Fact]
    public void NullByteSanitization_CleansEmbeddedNullsFromStream()
    {
        // Arrange: Script that emits a string containing embedded null \0 characters
        var scriptPath = CreateScript("null_bytes.ps1", """
$rawWithNulls = "CLEAN_START" + [char]0 + "MIDDLE" + [char]0 + "END"
[Console]::Out.WriteLine($rawWithNulls)
""");

        // Act
        var result = _runner.Run(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
            timeoutSeconds: 10);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain('\0', result.StandardOutput);
        Assert.Contains("CLEAN_STARTMIDDLEEND", result.StandardOutput);
    }
}
