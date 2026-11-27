using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class SystemProcessRunnerRawTests
{
    [Fact]
    public async Task RunRawAsync_PreservesNullBytesInStandardOutput()
    {
        var runner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);

        ProcessStartSpec spec;
        if (OperatingSystem.IsWindows())
        {
            spec = new ProcessStartSpec
            {
                FileName = "powershell",
                ArgumentList =
                [
                    "-NoProfile",
                    "-Command",
                    "[Console]::OpenStandardOutput().Write([byte[]]@(65, 0, 66, 0, 255, 10), 0, 6)"
                ],
                TimeoutSeconds = 15
            };
        }
        else
        {
            spec = new ProcessStartSpec
            {
                FileName = "printf",
                ArgumentList = [@"A\0B\0\377\n"],
                TimeoutSeconds = 15
            };
        }

        var result = await runner.RunRawAsync(spec);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new byte[] { 65, 0, 66, 0, 255, 10 }, result.StandardOutputBytes);
    }

    [Fact]
    public async Task RunRawAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var runner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunRawAsync(new ProcessStartSpec { FileName = "git", ArgumentList = ["--version"] }, cts.Token));
    }

    [Fact]
    public async Task RunRawAsync_WhenCancelledMidFlight_ThrowsOperationCanceledExceptionAndKillsProcess()
    {
        var runner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        var spec = OperatingSystem.IsWindows()
            ? new ProcessStartSpec
            {
                FileName = "powershell",
                ArgumentList = ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"],
                TimeoutSeconds = 30
            }
            : new ProcessStartSpec
            {
                FileName = "sleep",
                ArgumentList = ["30"],
                TimeoutSeconds = 30
            };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunRawAsync(spec, cts.Token));

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Expected quick cancellation cleanup, took {sw.Elapsed}.");
    }
}
