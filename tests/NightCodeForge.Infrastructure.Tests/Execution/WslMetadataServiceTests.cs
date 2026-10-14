using Microsoft.Extensions.Options;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public class WslMetadataServiceTests
{
    private class MockProcessRunner : IProcessRunner
    {
        public int CallCount { get; private set; }
        public List<(string FileName, string? Arguments, List<string>? ArgumentList)> Invocations { get; } = new();

        public Func<string, string, ProcessExecutionResult>? Handler { get; set; }

        public ProcessExecutionResult Run(string fileName, string arguments, string? workingDirectory = null, int timeoutSeconds = 10)
        {
            CallCount++;
            Invocations.Add((fileName, arguments, null));

            if (Handler != null)
            {
                return Handler(fileName, arguments);
            }

            return new ProcessExecutionResult(0, string.Empty, string.Empty);
        }

        public ProcessExecutionResult Run(string fileName, IEnumerable<string> arguments, string? workingDirectory = null, int timeoutSeconds = 10)
        {
            CallCount++;
            var list = arguments.ToList();
            Invocations.Add((fileName, null, list));

            if (Handler != null)
            {
                return Handler(fileName, string.Join(" ", list));
            }

            return new ProcessExecutionResult(0, string.Empty, string.Empty);
        }

        public ProcessExecutionResult Run(ProcessStartSpec spec)
        {
            if (spec.ArgumentList != null)
                return Run(spec.FileName, spec.ArgumentList, spec.WorkingDirectory, spec.TimeoutSeconds);
            return Run(spec.FileName, spec.Arguments ?? string.Empty, spec.WorkingDirectory, spec.TimeoutSeconds);
        }

        public Task<ProcessExecutionResult> RunAsync(string fileName, string arguments, string? workingDirectory = null, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Run(fileName, arguments, workingDirectory, timeoutSeconds));
        }

        public Task<ProcessExecutionResult> RunAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory = null, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Run(fileName, arguments, workingDirectory, timeoutSeconds));
        }

        public Task<ProcessExecutionResult> RunAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Run(spec));
        }

        public Task<ProcessExecutionResult> RunWithCallbacksAsync(string fileName, IEnumerable<string> arguments, Action<string>? onStandardOutput, Action<string>? onStandardError = null, string? workingDirectory = null, int timeoutSeconds = 10, string? standardInput = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Run(fileName, arguments, workingDirectory, timeoutSeconds));
        }

        public Task<ProcessExecutionResult> RunWithCallbacksAsync(string fileName, string arguments, Action<string>? onStandardOutput, Action<string>? onStandardError = null, string? workingDirectory = null, int timeoutSeconds = 10, string? standardInput = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Run(fileName, arguments, workingDirectory, timeoutSeconds));
        }

        public async IAsyncEnumerable<ProcessStreamChunk> RunStreamingAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory = null, int timeoutSeconds = 300, string? standardInput = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var res = Run(fileName, arguments, workingDirectory, timeoutSeconds);
            yield return new ProcessStreamChunk(ProcessStreamKind.StandardOutput, res.StandardOutput);
            yield return new ProcessStreamChunk(ProcessStreamKind.Exit, string.Empty, res.ExitCode);
        }

        public async IAsyncEnumerable<ProcessStreamChunk> RunStreamingAsync(string fileName, string arguments, string? workingDirectory = null, int timeoutSeconds = 300, string? standardInput = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var res = Run(fileName, arguments, workingDirectory, timeoutSeconds);
            yield return new ProcessStreamChunk(ProcessStreamKind.StandardOutput, res.StandardOutput);
            yield return new ProcessStreamChunk(ProcessStreamKind.Exit, string.Empty, res.ExitCode);
        }

        public IAsyncEnumerable<ProcessStreamChunk> RunStreamingAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default)
        {
            if (spec.ArgumentList != null)
                return RunStreamingAsync(spec.FileName, spec.ArgumentList, spec.WorkingDirectory, spec.TimeoutSeconds, spec.StandardInput, cancellationToken);
            return RunStreamingAsync(spec.FileName, spec.Arguments ?? string.Empty, spec.WorkingDirectory, spec.TimeoutSeconds, spec.StandardInput, cancellationToken);
        }

        public IProcessStreamSession StartSession(ProcessStartSpec spec) => throw new NotImplementedException();
    }

    [Fact]
    public void GetDefaultDistributionName_ParsesDefaultDistroWithAsterisk()
    {
        // Arrange
        var mockRunner = new MockProcessRunner
        {
            Handler = (file, args) =>
            {
                var stdout = "  NAME            STATE           VERSION\n* Ubuntu-24.04    Running         2\n  docker-desktop  Running         2\n";
                return new ProcessExecutionResult(0, stdout, string.Empty);
            }
        };

        var service = new WslMetadataService(mockRunner, isWindowsOverride: true);

        // Act
        var distro = service.GetDefaultDistributionName();

        // Assert
        Assert.Equal("Ubuntu-24.04", distro);
        Assert.Equal(1, mockRunner.CallCount);
        Assert.Equal("wsl.exe", mockRunner.Invocations[0].FileName);
        Assert.Equal(new[] { "--list", "--verbose" }, mockRunner.Invocations[0].ArgumentList);
    }

    [Fact]
    public void GetDefaultDistributionName_HandlesUtf16NullBytesInOutput()
    {
        // Arrange
        var rawOutputWithNulls = "  N\0A\0M\0E\0             S\0T\0A\0T\0E\0           V\0E\0R\0S\0I\0O\0N\0\n" +
                                 " *   D\0e\0b\0i\0a\0n\0         R\0u\0n\0n\0i\0n\0g\0                   2\0  \n";

        // Remove null bytes as SystemProcessRunner.Sanitize does
        var sanitized = new string(rawOutputWithNulls.Where(c => c != '\0').ToArray());
        var parsed = WslMetadataService.ParseDefaultDistribution(sanitized);

        Assert.Equal("Debian", parsed);
    }

    [Fact]
    public void GetDefaultDistributionName_CachesResult_DoesNotCallProcessRunnerAgain()
    {
        // Arrange
        var mockRunner = new MockProcessRunner
        {
            Handler = (file, args) => new ProcessExecutionResult(0, "  NAME    STATE\n* Ubuntu  Running\n", string.Empty)
        };

        var service = new WslMetadataService(mockRunner, isWindowsOverride: true);

        // Act
        var first = service.GetDefaultDistributionName();
        var second = service.GetDefaultDistributionName();

        // Assert
        Assert.Equal("Ubuntu", first);
        Assert.Equal("Ubuntu", second);
        Assert.Equal(1, mockRunner.CallCount); // Cached! Only 1 execution
    }

    [Fact]
    public void GetDefaultDistributionName_WhenClearCacheCalled_RefetchesFromProcess()
    {
        // Arrange
        int call = 0;
        var mockRunner = new MockProcessRunner
        {
            Handler = (file, args) =>
            {
                call++;
                var distro = call == 1 ? "Ubuntu" : "Fedora";
                return new ProcessExecutionResult(0, $"* {distro} Running\n", string.Empty);
            }
        };

        var service = new WslMetadataService(mockRunner, isWindowsOverride: true);

        // Act & Assert
        Assert.Equal("Ubuntu", service.GetDefaultDistributionName());
        Assert.Equal(1, mockRunner.CallCount);

        service.ClearCache();

        Assert.Equal("Fedora", service.GetDefaultDistributionName());
        Assert.Equal(2, mockRunner.CallCount);
    }

    [Fact]
    public void GetDefaultUserName_ParsesOutputAndCaches()
    {
        // Arrange
        var mockRunner = new MockProcessRunner
        {
            Handler = (file, args) => new ProcessExecutionResult(0, "gruearbre\n", string.Empty)
        };

        var service = new WslMetadataService(mockRunner, isWindowsOverride: true);

        // Act
        var user1 = service.GetDefaultUserName();
        var user2 = service.GetDefaultUserName();

        // Assert
        Assert.Equal("gruearbre", user1);
        Assert.Equal("gruearbre", user2);
        Assert.Equal(1, mockRunner.CallCount);
        Assert.Equal("wsl.exe", mockRunner.Invocations[0].FileName);
        Assert.Equal(new[] { "bash", "-c", "echo $USER" }, mockRunner.Invocations[0].ArgumentList);
    }

    [Fact]
    public void GetDefaultDistributionName_WhenProcessFails_FallsBackToConfiguredOrUbuntu()
    {
        // Arrange
        var mockRunner = new MockProcessRunner
        {
            Handler = (file, args) => new ProcessExecutionResult(1, string.Empty, "WSL not found")
        };

        var options = Options.Create(new ExecutionEnvironmentProviderOptions { DefaultWslDistribution = "ArchLinux" });
        var service = new WslMetadataService(mockRunner, options, isWindowsOverride: true);

        // Act
        var distro = service.GetDefaultDistributionName();

        // Assert
        Assert.Equal("ArchLinux", distro);
    }

    [Fact]
    public void GetDefaultUserName_WhenProcessFails_FallsBackToHostUserOrUser()
    {
        // Arrange
        var mockRunner = new MockProcessRunner
        {
            Handler = (file, args) => new ProcessExecutionResult(1, string.Empty, "Error")
        };

        var service = new WslMetadataService(mockRunner, isWindowsOverride: true);

        // Act
        var user = service.GetDefaultUserName();

        // Assert
        var expected = !string.IsNullOrWhiteSpace(Environment.UserName)
            ? Environment.UserName.ToLowerInvariant()
            : "user";
        Assert.Equal(expected, user);
    }

    [Fact]
    public void GetDefaultDistributionName_EnvironmentVariableOverride_TakesPrecedenceWithoutProcessCall()
    {
        var envVarName = "NCF_WSL_DISTRIBUTION";
        var previous = Environment.GetEnvironmentVariable(envVarName);

        try
        {
            Environment.SetEnvironmentVariable(envVarName, "CustomDistroOverride");

            var mockRunner = new MockProcessRunner();
            var service = new WslMetadataService(mockRunner, isWindowsOverride: true);

            var distro = service.GetDefaultDistributionName();

            Assert.Equal("CustomDistroOverride", distro);
            Assert.Equal(0, mockRunner.CallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, previous);
        }
    }
}
