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

    [Fact]
    public void BuildEffectiveWindowsProcessPath_ThreeArgOverloadPreservesProcessPathAndNormalizesSlashes()
    {
        var combined = SystemPlatformEnvironment.BuildEffectiveWindowsProcessPath(
            machinePath: @"C:\Tools\Git\;",
            userPath: @"C:/Tools/Git/;C:\UserTools\",
            currentProcessPath: @"C:\ProcessOnly;""C:\UserTools""");

        var segments = combined.Split(';');
        Assert.Equal(3, segments.Length);
        Assert.Equal(@"C:\Tools\Git\", segments[0]);
        Assert.Equal(@"C:\UserTools\", segments[1]);
        Assert.Equal(@"C:\ProcessOnly", segments[2]);
    }

    [Fact]
    public void IsCommandOnPath_RejectsTraversalHandlesWindowsExtensionsAndDottedCommands()
    {
        using var workspace = new TempTestWorkspace();
        var binDir = workspace.CreateSubdirectory("bin");
        File.WriteAllText(Path.Combine(binDir, "ollamarouter"), "#!/usr/bin/env bash");
        File.WriteAllText(Path.Combine(binDir, "NightCodeForge.Web.exe"), "pe");

        // Traversal/path-separator commands are never allowed.
        Assert.False(SystemPlatformEnvironment.IsCommandOnPath(@"..\ollamarouter", binDir, isWindows: true, [".exe"]));
        Assert.False(SystemPlatformEnvironment.IsCommandOnPath("sub/tool", binDir, isWindows: false, []));

        // Windows ignores extensionless bash shims when looking up "ollamarouter" or "ollamarouter.exe",
        // while still resolving dotted executable names like "NightCodeForge.Web.exe".
        Assert.False(SystemPlatformEnvironment.IsCommandOnPath("ollamarouter", binDir, isWindows: true, [".exe", ".cmd"]));
        Assert.False(SystemPlatformEnvironment.IsCommandOnPath("ollamarouter.exe", binDir, isWindows: true, [".exe", ".cmd"]));
        Assert.True(SystemPlatformEnvironment.IsCommandOnPath("NightCodeForge.Web", binDir, isWindows: true, [".exe", ".cmd"]));
        Assert.True(SystemPlatformEnvironment.IsCommandOnPath("NightCodeForge.Web.exe", binDir, isWindows: true, [".exe", ".cmd"]));
    }

    [Fact]
    public async Task SystemProcessRunner_InvalidTimeoutOrShellExecuteWithEnvVars_ThrowsArgumentException()
    {
        var runner = new SystemProcessRunner();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunAsync(new ProcessRunRequest
        {
            FileName = "dotnet",
            Timeout = TimeSpan.Zero
        }));

        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(new ProcessRunRequest
        {
            FileName = "cmd.exe",
            UseShellExecute = true,
            EnvironmentVariables = new Dictionary<string, string> { ["FOO"] = "bar" }
        }));
    }
}
