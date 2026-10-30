using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Execution;
using Xunit;
using Xunit.Abstractions;

namespace NightCodeForge.Infrastructure.Tests.Execution;

[Trait("Category", "Integration")]
public class ContainerOutputRedirectionIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testRoot;
    private readonly bool _useWsl;
    private readonly List<string> _containersToCleanup = new();

    public ContainerOutputRedirectionIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _testRoot = Path.Combine(Path.GetTempPath(), "ncf_redirect_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _useWsl = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    public void Dispose()
    {
        foreach (var container in _containersToCleanup)
        {
            RemoveContainerSafe(container, _useWsl);
        }

        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, true);
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Failed to clean up test root directory: {ex.Message}");
        }
    }

    private NightCodeForgeContainerExecutor CreateExecutor(
        string containerName,
        Mock<IOperationsTelemetryService> telemetryMock)
    {
        _containersToCleanup.Add(containerName);

        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = containerName,
            ContainerImage = ContainerExecutorOptions.DefaultAgentBaseImage,
            HostWorkspacePath = _testRoot,
            ContainerWorkspacePath = "/workspace",
            DefaultWorkingDirectory = "/workspace",
            AutoStartContainer = true,
            MountPackageCaches = false,
            MountHostSdks = false,
            UseWsl = _useWsl,
            DefaultTimeoutSeconds = 60
        };

        var processRunner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        var identityResolver = new HostUserIdentityResolver(processRunner, NullLogger<HostUserIdentityResolver>.Instance);

        return new NightCodeForgeContainerExecutor(
            Options.Create(options),
            processRunner,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            identityResolver,
            telemetryMock.Object);
    }

    [Fact]
    public async Task ContainerExecutor_StderrOnlyCommand_CapturesStderrInOutputAndPreservesExitCode()
    {
        if (!IsDockerAccessible(_useWsl))
        {
            _output.WriteLine("Docker is not accessible in this environment. Skipping test.");
            return;
        }

        var telemetryMock = new Mock<IOperationsTelemetryService>();
        ShellCommandRecord? completedRecord = null;
        telemetryMock
            .Setup(t => t.RecordShellCommandCompleted(It.IsAny<ShellCommandRecord>()))
            .Callback<ShellCommandRecord>(r => completedRecord = r);

        var containerName = $"ncf-stderr-test-{Guid.NewGuid():N}";
        await using var executor = CreateExecutor(containerName, telemetryMock);

        // Command writing solely to stderr and exiting with non-zero exit code
        var result = await executor.ExecuteAsync("bash -c \"echo 'fatal: reference not found: main' >&2; exit 128\"");

        Assert.Equal(128, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput), "StandardOutput must capture stderr output via 2>&1 redirection");
        Assert.Contains("fatal: reference not found: main", result.StandardOutput);

        Assert.NotNull(completedRecord);
        Assert.Equal(128, completedRecord.ExitCode);
        Assert.Equal(ShellCommandStatus.Failed, completedRecord.Status);
        Assert.Contains("fatal: reference not found: main", completedRecord.OutputPreview);
    }

    [Fact]
    public async Task ContainerExecutor_StdoutOnlyCommand_CapturesStdoutAndExitCodeZero()
    {
        if (!IsDockerAccessible(_useWsl))
        {
            _output.WriteLine("Docker is not accessible in this environment. Skipping test.");
            return;
        }

        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var containerName = $"ncf-stdout-test-{Guid.NewGuid():N}";
        await using var executor = CreateExecutor(containerName, telemetryMock);

        var result = await executor.ExecuteAsync("echo 'Build succeeded: 0 Warning(s), 0 Error(s)'");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Build succeeded: 0 Warning(s), 0 Error(s)", result.StandardOutput);
    }

    [Fact]
    public async Task ContainerExecutor_IntermixedStdoutAndStderr_CapturesBothInOrder()
    {
        if (!IsDockerAccessible(_useWsl))
        {
            _output.WriteLine("Docker is not accessible in this environment. Skipping test.");
            return;
        }

        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var containerName = $"ncf-intermix-test-{Guid.NewGuid():N}";
        await using var executor = CreateExecutor(containerName, telemetryMock);

        var result = await executor.ExecuteAsync("bash -c \"echo 'step 1: restore'; echo 'step 1 error: package missing' >&2; echo 'step 2: done'\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("step 1: restore", result.StandardOutput);
        Assert.Contains("step 1 error: package missing", result.StandardOutput);
        Assert.Contains("step 2: done", result.StandardOutput);

        var idx1 = result.StandardOutput.IndexOf("step 1: restore", StringComparison.Ordinal);
        var idxErr = result.StandardOutput.IndexOf("step 1 error: package missing", StringComparison.Ordinal);
        var idx2 = result.StandardOutput.IndexOf("step 2: done", StringComparison.Ordinal);

        Assert.True(idx1 < idxErr, "step 1 should appear before error");
        Assert.True(idxErr < idx2, "error should appear before step 2");
    }

    [Fact]
    public async Task ContainerExecutor_FailedCommandWritingToStderr_CapturesStderrDiagnosticsInOutput()
    {
        if (!IsDockerAccessible(_useWsl))
        {
            _output.WriteLine("Docker is not accessible in this environment. Skipping test.");
            return;
        }

        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var containerName = $"ncf-errdiag-test-{Guid.NewGuid():N}";
        await using var executor = CreateExecutor(containerName, telemetryMock);

        // 1. Test git command writing error diagnostics to stderr
        var gitResult = await executor.ExecuteAsync("git checkout --nonexistent-branch-flag");
        Assert.NotEqual(0, gitResult.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(gitResult.StandardOutput), "Git stderr output must be captured in output");
        Assert.True(
            gitResult.StandardOutput.Contains("fatal:", StringComparison.OrdinalIgnoreCase) ||
            gitResult.StandardOutput.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
            gitResult.StandardOutput.Contains("unknown", StringComparison.OrdinalIgnoreCase),
            $"Expected git error in output, got: {gitResult.StandardOutput}");

        // 2. Test python command raising uncaught exception to stderr
        var pyResult = await executor.ExecuteAsync("python3 -c \"raise RuntimeError('simulated runtime failure')\"");
        Assert.NotEqual(0, pyResult.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(pyResult.StandardOutput), "Python traceback stderr output must be captured in output");
        Assert.Contains("RuntimeError: simulated runtime failure", pyResult.StandardOutput);

        // 3. Test dotnet build if dotnet is available in PATH
        var whichDotnet = await executor.ExecuteAsync("which dotnet");
        if (whichDotnet.ExitCode == 0)
        {
            var dotnetResult = await executor.ExecuteAsync("dotnet build /nonexistent/MissingProject.csproj");
            Assert.NotEqual(0, dotnetResult.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(dotnetResult.StandardOutput), "dotnet build error output must be captured");
            Assert.True(
                dotnetResult.StandardOutput.Contains("MSB1009", StringComparison.OrdinalIgnoreCase) ||
                dotnetResult.StandardOutput.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                dotnetResult.StandardOutput.Contains("error", StringComparison.OrdinalIgnoreCase),
                $"Expected dotnet error in output, got: {dotnetResult.StandardOutput}");
        }
    }

    [Fact]
    public async Task ContainerExecutor_ComplexPipedAndSubshellCommand_PreservesOutputAndExitCode()
    {
        if (!IsDockerAccessible(_useWsl))
        {
            _output.WriteLine("Docker is not accessible in this environment. Skipping test.");
            return;
        }

        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var containerName = $"ncf-subshell-test-{Guid.NewGuid():N}";
        await using var executor = CreateExecutor(containerName, telemetryMock);

        var result = await executor.ExecuteAsync("bash -c \"( echo 'subshell starting' && false ) || echo 'fallback error message' >&2; exit 42\"");

        Assert.Equal(42, result.ExitCode);
        Assert.Contains("subshell starting", result.StandardOutput);
        Assert.Contains("fallback error message", result.StandardOutput);
    }

    private bool IsDockerAccessible(bool useWsl)
    {
        try
        {
            var psi = useWsl
                ? new ProcessStartInfo("wsl.exe", "-- docker info")
                : new ProcessStartInfo("docker", "info");

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            using var process = Process.Start(psi);
            if (process == null) return false;

            if (!process.WaitForExit(8000))
            {
                try
                {
                    process.Kill();
                }
                catch (Exception killEx)
                {
                    // Best-effort process termination on timeout
                    _output.WriteLine($"Failed to kill timed-out docker info check: {killEx.Message}");
                }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Docker accessibility check failed: {ex.Message}");
            return false;
        }
    }

    private void RemoveContainerSafe(string containerName, bool useWsl)
    {
        try
        {
            var psi = useWsl
                ? new ProcessStartInfo("wsl.exe", $"-- docker rm -f {containerName}")
                : new ProcessStartInfo("docker", $"rm -f {containerName}");

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
        }
        catch (Exception ex)
        {
            // Best-effort test container cleanup
            _output.WriteLine($"Failed to remove test container '{containerName}': {ex.Message}");
        }
    }
}
