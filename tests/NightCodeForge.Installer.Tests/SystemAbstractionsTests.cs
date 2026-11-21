namespace NightCodeForge.Installer.Tests;

using NightCodeForge.Installer.Core.Abstractions;

public sealed class SystemAbstractionsTests
{
    [Fact]
    public void BuildEffectiveWindowsProcessPath_PreservesMachinePathAndDeduplicatesUserPath()
    {
        var machinePath = @"C:\Windows\System32; C:\Program Files\dotnet\ ;""C:\Tools\Git\cmd""";
        var userPath = @"C:\Program Files\dotnet;C:\Users\Dev\.dotnet\tools;C:\tools\git\cmd\";

        var combined = SystemPlatformEnvironment.BuildEffectiveWindowsProcessPath(machinePath, userPath);
        var segments = combined.Split(';');

        Assert.Equal(4, segments.Length);
        Assert.Equal(@"C:\Windows\System32", segments[0]);
        Assert.Equal(@"C:\Program Files\dotnet\", segments[1]);
        Assert.Equal(@"""C:\Tools\Git\cmd""", segments[2]);
        Assert.Equal(@"C:\Users\Dev\.dotnet\tools", segments[3]);

        // Empty user PATH must preserve machine PATH intact.
        var machineOnly = SystemPlatformEnvironment.BuildEffectiveWindowsProcessPath(machinePath, string.Empty);
        Assert.Contains(@"C:\Windows\System32", machineOnly, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemProcessRunner_RawArgumentsAndArgumentsTogether_ThrowsArgumentException()
    {
        var runner = new SystemProcessRunner();
        var request = new ProcessRunRequest
        {
            FileName = "cmd.exe",
            Arguments = ["/c", "echo hi"],
            RawArguments = "/c echo hi"
        };

        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(request));
    }

    [Fact]
    public async Task SystemProcessRunner_MissingExecutable_ReturnsFileNotFoundExitCode()
    {
        var runner = new SystemProcessRunner();
        var missingBinary = Path.Combine(Path.GetTempPath(), "ncf-nonexistent-" + Guid.NewGuid().ToString("N"), "missing.exe");

        var capturedResult = await runner.RunAsync(new ProcessRunRequest
        {
            FileName = missingBinary,
            Arguments = ["--version"]
        });
        Assert.NotEqual(0, capturedResult.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(capturedResult.StandardError));

        var detachedResult = await runner.RunAsync(new ProcessRunRequest
        {
            FileName = missingBinary,
            Detached = true
        });
        Assert.NotEqual(0, detachedResult.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(detachedResult.StandardError));
    }

    [Fact]
    public async Task SystemProcessRunner_WindowsCmdRawArguments_HandlesQuotedPathWithSpacesAndAmpersand()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new SystemProcessRunner();
        const string quotedPayload = @"""C:\App Folder & More\NightCodeForge.Installer.exe""";
        var result = await runner.RunAsync(new ProcessRunRequest
        {
            FileName = "cmd.exe",
            RawArguments = $"/c \"ping 127.0.0.1 -n 1 > nul & echo {quotedPayload}\""
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(quotedPayload, result.StandardOutput.Trim());
    }
}
