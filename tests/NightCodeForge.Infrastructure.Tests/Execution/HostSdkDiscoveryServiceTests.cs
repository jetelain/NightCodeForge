using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using Xunit;
using Xunit.Abstractions;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public class HostSdkDiscoveryServiceTests
{
    private readonly ITestOutputHelper? _output;

    public HostSdkDiscoveryServiceTests(ITestOutputHelper? output = null)
    {
        _output = output;
    }
    private static HostSdkDiscoveryService CreateService(
        IProcessRunner runner,
        IWslMetadataService? wslMetadataService = null,
        IOptions<ExecutionEnvironmentProviderOptions>? options = null,
        bool? isWindowsOverride = null,
        ILogger<HostSdkDiscoveryService>? logger = null)
    {
        return new HostSdkDiscoveryService(
            runner,
            wslMetadataService ?? new Mock<IWslMetadataService>().Object,
            options ?? Options.Create(new ExecutionEnvironmentProviderOptions()),
            isWindowsOverride,
            logger ?? NullLogger<HostSdkDiscoveryService>.Instance);
    }
    private class MockProcessRunner : IProcessRunner
    {
        public int CallCount { get; private set; }
        public List<(string FileName, string? Arguments, List<string>? ArgumentList)> Invocations { get; } = new();
        public Func<string, string, ProcessExecutionResult>? Handler { get; set; }

        public ProcessExecutionResult Run(ProcessStartSpec spec)
        {
            CallCount++;
            var list = spec.ArgumentList?.ToList();
            Invocations.Add((spec.FileName, spec.Arguments, list));
            if (Handler != null)
            {
                var args = spec.ArgumentList != null ? string.Join(" ", spec.ArgumentList) : (spec.Arguments ?? string.Empty);
                return Handler(spec.FileName, args);
            }
            return new ProcessExecutionResult(0, string.Empty, string.Empty);
        }

        public Task<ProcessExecutionResult> RunAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Run(spec));
        }

        public async IAsyncEnumerable<ProcessStreamChunk> RunStreamingAsync(ProcessStartSpec spec, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var res = Run(spec);
            yield return new ProcessStreamChunk(ProcessStreamKind.StandardOutput, res.StandardOutput);
            yield return new ProcessStreamChunk(ProcessStreamKind.Exit, string.Empty, res.ExitCode);
        }

        public IProcessStreamSession StartSession(ProcessStartSpec spec) => throw new NotImplementedException();
    }

    [Fact]
    public void ParseDiscoveryOutput_ParsesEnvVarsAndWhichCommandsAndDirs()
    {
        var sampleOutput = @"
---ENV---
ANDROID_HOME=/home/user/Android/Sdk
JAVA_HOME=/usr/lib/jvm/java-17-openjdk-amd64
DOTNET_ROOT=/usr/share/dotnet
GOROOT=/usr/local/go
CARGO_HOME=/home/user/.cargo
---CMD---
dotnet=/usr/bin/dotnet=/usr/lib/dotnet/dotnet
node=/usr/bin/node=/usr/bin/node
java=/usr/bin/java=/usr/lib/jvm/java-17-openjdk-amd64/bin/java
go=/usr/bin/go=/usr/local/go/bin/go
cargo=/usr/bin/cargo=/home/user/.cargo/bin/cargo
adb=/usr/bin/adb=/home/user/Android/Sdk/platform-tools/adb
---DIR---
/usr/lib/dotnet
/usr/share/dotnet
/etc/ssl/certs
/usr/lib/x86_64-linux-gnu
";

        var details = HostSdkDiscoveryService.ParseDiscoveryOutput(sampleOutput);

        // Env vars
        Assert.Equal("/home/user/Android/Sdk", details.EnvironmentVariables["ANDROID_HOME"]);
        Assert.Equal("/usr/lib/jvm/java-17-openjdk-amd64", details.EnvironmentVariables["JAVA_HOME"]);
        Assert.Equal("/usr/local/go", details.EnvironmentVariables["GOROOT"]);

        // Commands
        Assert.Equal("/usr/bin/dotnet", details.CommandPaths["dotnet"]);
        Assert.Equal("/usr/bin/java", details.CommandPaths["java"]);
        Assert.Equal("/usr/bin/go", details.CommandPaths["go"]);

        // Mount paths: must contain derived and discovered paths
        Assert.Contains("/home/user/Android/Sdk", details.MountPaths);
        Assert.Contains("/usr/lib/jvm/java-17-openjdk-amd64", details.MountPaths);
        Assert.Contains("/usr/lib/dotnet", details.MountPaths);
        Assert.Contains("/usr/share/dotnet", details.MountPaths);
        Assert.Contains("/usr/local/go", details.MountPaths);
        Assert.Contains("/home/user/.cargo", details.MountPaths);
        Assert.Contains("/etc/ssl/certs", details.MountPaths);
        Assert.Contains("/usr/lib/x86_64-linux-gnu", details.MountPaths);

        // Crucial safety check: system roots must NEVER be included
        Assert.DoesNotContain("/usr", details.MountPaths);
        Assert.DoesNotContain("/usr/bin", details.MountPaths);
        Assert.DoesNotContain("/", details.MountPaths);
    }

    [Fact]
    public void ParseDiscoveryOutput_ExcludesSystemRoots()
    {
        var sampleOutput = @"
---CMD---
node=/usr/bin/node=/usr/bin/node
python3=/usr/bin/python3=/usr/bin/python3.12
---DIR---
/usr
/usr/bin
/usr/lib
/etc
/bin
";

        var details = HostSdkDiscoveryService.ParseDiscoveryOutput(sampleOutput);

        Assert.DoesNotContain("/usr", details.MountPaths);
        Assert.DoesNotContain("/usr/bin", details.MountPaths);
        Assert.DoesNotContain("/usr/lib", details.MountPaths);
        Assert.DoesNotContain("/etc", details.MountPaths);
        Assert.DoesNotContain("/bin", details.MountPaths);
    }

    [Fact]
    public void ParseDiscoveryOutput_EmptyOutput_ReturnsDefaultSdkMountPaths()
    {
        var details = HostSdkDiscoveryService.ParseDiscoveryOutput(string.Empty);

        Assert.NotNull(details);
        Assert.Equal(ContainerExecutorOptions.DefaultSdkMountPaths, details.MountPaths);
    }

    [Fact]
    public void DiscoverSdkMountPaths_WindowsWsl_InvokesWslAndCachesResult()
    {
        var mockRunner = new MockProcessRunner
        {
            Handler = (file, args) =>
            {
                var stdout = @"
---ENV---
JAVA_HOME=/usr/lib/jvm/java-17-openjdk-amd64
---CMD---
dotnet=/usr/bin/dotnet=/usr/lib/dotnet/dotnet
---DIR---
/usr/lib/dotnet
/etc/ssl/certs
";
                return new ProcessExecutionResult(0, stdout, string.Empty);
            }
        };

        var service = CreateService(mockRunner, isWindowsOverride: true);

        // First call: executes process runner
        var paths1 = service.DiscoverSdkMountPaths("Ubuntu");
        Assert.Equal(1, mockRunner.CallCount);
        Assert.Contains("/usr/lib/dotnet", paths1);
        Assert.Contains("/etc/ssl/certs", paths1);
        Assert.Equal("wsl.exe", mockRunner.Invocations[0].FileName);
        Assert.Equal("-d", mockRunner.Invocations[0].ArgumentList![0]);
        Assert.Equal("Ubuntu", mockRunner.Invocations[0].ArgumentList![1]);

        // Second call: returns cached result without running process again
        var paths2 = service.DiscoverSdkMountPaths("Ubuntu");
        Assert.Equal(1, mockRunner.CallCount);
        Assert.Equal(paths1, paths2);
    }

    [Fact]
    public void DiscoverSdkMountPaths_BareHost_InvokesBashAndCachesResult()
    {
        var mockRunner = new MockProcessRunner
        {
            Handler = (file, args) =>
            {
                var stdout = @"
---ENV---
JAVA_HOME=/opt/jdk-21
---DIR---
/etc/ssl/certs
";
                return new ProcessExecutionResult(0, stdout, string.Empty);
            }
        };

        var service = CreateService(mockRunner, isWindowsOverride: false);

        var paths1 = service.DiscoverSdkMountPaths();
        Assert.Equal(1, mockRunner.CallCount);
        Assert.Equal("bash", mockRunner.Invocations[0].FileName);
        Assert.Contains("/opt/jdk-21", paths1);

        // Cached call
        var paths2 = service.DiscoverSdkMountPaths();
        Assert.Equal(1, mockRunner.CallCount);
        Assert.Same(paths1, paths2);
    }

    [Fact]
    public void ClearCache_ForcesReEvaluationOnNextCall()
    {
        var mockRunner = new MockProcessRunner
        {
            Handler = (file, args) =>
            {
                var stdout = @"
---DIR---
/usr/lib/dotnet
";
                return new ProcessExecutionResult(0, stdout, string.Empty);
            }
        };

        var service = CreateService(mockRunner, isWindowsOverride: true);

        service.DiscoverSdkMountPaths("Ubuntu");
        Assert.Equal(1, mockRunner.CallCount);

        service.ClearCache();

        service.DiscoverSdkMountPaths("Ubuntu");
        Assert.Equal(2, mockRunner.CallCount);
    }

    [Fact]
    public async Task DiscoverSdkMountPathsAsync_ReturnsSameResultsAsSync()
    {
        var mockRunner = new MockProcessRunner
        {
            Handler = (file, args) =>
            {
                var stdout = @"
---ENV---
ANDROID_HOME=/opt/android-sdk
---DIR---
/etc/ssl/certs
";
                return new ProcessExecutionResult(0, stdout, string.Empty);
            }
        };

        var service = CreateService(mockRunner, isWindowsOverride: false);

        var paths = await service.DiscoverSdkMountPathsAsync();
        Assert.Equal(1, mockRunner.CallCount);
        Assert.Contains("/opt/android-sdk", paths);
        Assert.Contains("/etc/ssl/certs", paths);
    }

    [Fact]
    public void DiscoverSdkMountPaths_ProcessFails_FallsBackToDefaultsWithoutCrashing()
    {
        var mockRunner = new MockProcessRunner
        {
            Handler = (file, args) => new ProcessExecutionResult(1, string.Empty, "Command failed")
        };

        var service = CreateService(mockRunner, isWindowsOverride: true);

        var paths = service.DiscoverSdkMountPaths("Ubuntu");

        Assert.NotNull(paths);
        Assert.Equal(ContainerExecutorOptions.DefaultSdkMountPaths, paths);
    }

    [Fact]
    public void AdditionalSdkMountPaths_ConfiguredInOptions_AppendedToResults()
    {
        var mockRunner = new MockProcessRunner
        {
            Handler = (file, args) =>
            {
                var stdout = @"
---DIR---
/usr/lib/dotnet
";
                return new ProcessExecutionResult(0, stdout, string.Empty);
            }
        };

        var options = Options.Create(new ExecutionEnvironmentProviderOptions
        {
            AdditionalSdkMountPaths = new List<string> { "/opt/custom-sdk" }
        });

        var service = CreateService(mockRunner, options: options, isWindowsOverride: false);

        var paths = service.DiscoverSdkMountPaths();

        Assert.Contains("/usr/lib/dotnet", paths);
        Assert.Contains("/opt/custom-sdk", paths);
    }

    [Fact]
    public void EnvironmentVariableOverride_PrependsPathsToResults()
    {
        var mockRunner = new MockProcessRunner
        {
            Handler = (file, args) =>
            {
                var stdout = @"
---DIR---
/usr/lib/dotnet
";
                return new ProcessExecutionResult(0, stdout, string.Empty);
            }
        };

        var prev = Environment.GetEnvironmentVariable("NCF_SDK_MOUNT_PATHS");
        try
        {
            Environment.SetEnvironmentVariable("NCF_SDK_MOUNT_PATHS", "/custom/env/sdk1:/custom/env/sdk2");
            var service = CreateService(mockRunner, isWindowsOverride: false);

            var paths = service.DiscoverSdkMountPaths();

            Assert.Contains("/custom/env/sdk1", paths);
            Assert.Contains("/custom/env/sdk2", paths);
            Assert.Contains("/usr/lib/dotnet", paths);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NCF_SDK_MOUNT_PATHS", prev);
        }
    }

    [Fact]
    public void ProbeScript_SourcesBashrcAndPrependsUserToolDirectories()
    {
        var script = HostSdkDiscoveryService.ProbeScript;

        Assert.Contains(".bashrc", script);
        Assert.Contains(".profile", script);
        Assert.Contains(".dotnet/tools", script);
        Assert.Contains(".cargo/bin", script);
        Assert.Contains("csnav", script);
        Assert.Contains("codecondenser", script);
        Assert.Contains("mdfetch", script);
    }

    [Fact]
    public void ParseDiscoveryOutput_ParsesCompanionToolsAndCustomToolPaths()
    {
        var output = @"
---CMD---
dotnet=/usr/bin/dotnet=/usr/lib/dotnet/dotnet
csnav=/home/user/.dotnet/tools/csnav=/home/user/.dotnet/tools/csnav
codecondenser=/home/user/.dotnet/tools/codecondenser=/home/user/.dotnet/tools/codecondenser
mdfetch=/home/user/.dotnet/tools/mdfetch=/home/user/.dotnet/tools/mdfetch
---DIR---
/usr/lib/dotnet
/home/user/.dotnet
";
        var details = HostSdkDiscoveryService.ParseDiscoveryOutput(output);

        Assert.Equal("/home/user/.dotnet/tools/csnav", details.CommandPaths["csnav"]);
        Assert.Equal("/home/user/.dotnet/tools/codecondenser", details.CommandPaths["codecondenser"]);
        Assert.Equal("/home/user/.dotnet/tools/mdfetch", details.CommandPaths["mdfetch"]);
        Assert.Contains("/home/user/.dotnet", details.MountPaths);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void DiscoverSdkDetails_LiveRealExecution_OutputsRealProbeResult()
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            return;

        var runner = new NightCodeForge.Core.Execution.SystemProcessRunner();
        var wslMeta = new WslMetadataService(runner, Options.Create(new ExecutionEnvironmentProviderOptions()), NullLogger<WslMetadataService>.Instance);
        var service = new HostSdkDiscoveryService(runner, wslMeta, Options.Create(new ExecutionEnvironmentProviderOptions()), NullLogger<HostSdkDiscoveryService>.Instance);

        var details = service.DiscoverSdkDetails();
        Assert.NotNull(details);
        if (details.MountPaths.Count == 0 && details.CommandPaths.Count == 0)
        {
            // WSL not installed or not running in this test environment
            return;
        }

        Assert.NotEmpty(details.MountPaths);

        _output?.WriteLine($"MountPaths count: {details.MountPaths.Count}");
        foreach (var m in details.MountPaths)
        {
            _output?.WriteLine($"  Mount: {m}");
        }

        _output?.WriteLine($"CommandPaths count: {details.CommandPaths.Count}");
        foreach (var (k, v) in details.CommandPaths)
        {
            _output?.WriteLine($"  Cmd: {k} => {v}");
        }
    }
}
