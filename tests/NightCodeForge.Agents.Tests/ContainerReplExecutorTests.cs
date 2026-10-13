using System.Diagnostics;
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

        using var executor = new NightCodeForgeContainerExecutor(options);
        var result = await executor.ExecuteAsync("echo hello world");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello world", result.StandardOutput);
    }
}
