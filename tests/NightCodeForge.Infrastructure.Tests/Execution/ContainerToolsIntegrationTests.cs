using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace NightCodeForge.Infrastructure.Tests.Execution;

[Trait("Category", "Integration")]
public class ContainerToolsIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testRoot;
    private readonly bool _isWindows;
    private readonly bool _useWsl;
    private readonly List<string> _containersToCleanup = new();

    public ContainerToolsIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _testRoot = Path.Combine(Path.GetTempPath(), "ncf_tools_integ_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        _useWsl = _isWindows;
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

    [Fact]
    public async Task ContainerExecutor_StandardTools_ExecuteSuccessfullyInContainer()
    {
        if (!IsDockerAccessible(_useWsl))
        {
            _output.WriteLine("Docker is not accessible in this environment. Skipping test.");
            return;
        }

        var containerName = $"ncf-tools-test-{Guid.NewGuid():N}";
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
        var telemetryMock = new Mock<IOperationsTelemetryService>();

        await using var executor = new NightCodeForgeContainerExecutor(
            Options.Create(options),
            processRunner,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            identityResolver,
            telemetryMock.Object);

        // 1. Verify git is present and functional
        var gitResult = await executor.ExecuteAsync("git --version");
        Assert.Equal(0, gitResult.ExitCode);
        Assert.Contains("git version", gitResult.StandardOutput, StringComparison.OrdinalIgnoreCase);

        var whichGit = await executor.ExecuteAsync("which git");
        Assert.Equal(0, whichGit.ExitCode);
        Assert.Contains("/usr/bin/git", whichGit.StandardOutput.Trim());

        // 2. Verify python3 is present and functional
        var pythonResult = await executor.ExecuteAsync("python3 --version");
        Assert.Equal(0, pythonResult.ExitCode);
        Assert.Contains("Python 3.", pythonResult.StandardOutput);

        var whichPython = await executor.ExecuteAsync("which python3");
        Assert.Equal(0, whichPython.ExitCode);
        Assert.Contains("/usr/bin/python3", whichPython.StandardOutput.Trim());

        // 3. Verify ripgrep (rg) is present and functional
        var rgResult = await executor.ExecuteAsync("rg --version");
        Assert.Equal(0, rgResult.ExitCode);
        Assert.Contains("ripgrep", rgResult.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContainerExecutor_MountedTools_AreInPathAndExecutable()
    {
        if (!IsDockerAccessible(_useWsl))
        {
            _output.WriteLine("Docker is not accessible in this environment. Skipping test.");
            return;
        }

        var containerName = $"ncf-mounttools-test-{Guid.NewGuid():N}";
        _containersToCleanup.Add(containerName);

        // Prepare a host tools directory with an executable tool script
        var hostToolsDir = Path.Combine(_testRoot, "custom-tools");
        Directory.CreateDirectory(hostToolsDir);

        var toolFilePath = Path.Combine(hostToolsDir, "custom-echo-tool");
        await File.WriteAllTextAsync(toolFilePath, "#!/bin/sh\necho \"custom-tool-executed: $1\"\n");

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
            DefaultTimeoutSeconds = 60,
            AdditionalVolumeMounts = new List<ContainerVolumeMount>
            {
                new(
                    hostPath: hostToolsDir,
                    containerPath: "/tmp/ncf-home/.dotnet/tools",
                    isReadOnly: false)
            }
        };

        var processRunner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        var identityResolver = new HostUserIdentityResolver(processRunner, NullLogger<HostUserIdentityResolver>.Instance);
        var telemetryMock = new Mock<IOperationsTelemetryService>();

        await using var executor = new NightCodeForgeContainerExecutor(
            Options.Create(options),
            processRunner,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            identityResolver,
            telemetryMock.Object);

        // Ensure executable permissions inside the container
        await executor.ExecuteAsync("chmod +x /tmp/ncf-home/.dotnet/tools/custom-echo-tool");

        // Verify PATH includes /tmp/ncf-home/.dotnet/tools
        var pathResult = await executor.ExecuteAsync("echo $PATH");
        Assert.Equal(0, pathResult.ExitCode);
        Assert.Contains("/tmp/ncf-home/.dotnet/tools", pathResult.StandardOutput);

        // Verify which resolves custom-echo-tool through PATH
        var whichResult = await executor.ExecuteAsync("which custom-echo-tool");
        Assert.Equal(0, whichResult.ExitCode);
        Assert.Contains("/tmp/ncf-home/.dotnet/tools/custom-echo-tool", whichResult.StandardOutput.Trim());

        // Execute tool directly by name without full path
        var execResult = await executor.ExecuteAsync("custom-echo-tool integration-payload");
        Assert.Equal(0, execResult.ExitCode);
        Assert.Contains("custom-tool-executed: integration-payload", execResult.StandardOutput);
    }

    [Fact]
    public async Task ExecutionEnvironmentProvider_ContainerMode_MountsAndRunsCompanionToolsSuccessfully()
    {
        if (!IsDockerAccessible(_useWsl))
        {
            _output.WriteLine("Docker is not accessible in this environment. Skipping test.");
            return;
        }

        var workspaceId = new Random().Next(10000, 99999);
        var containerName = $"ncf-workspace-{workspaceId}";
        _containersToCleanup.Add(containerName);

        var workspace = new Workspace
        {
            Id = workspaceId,
            Name = $"ToolsWorkspace-{workspaceId}",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            UseWslForContainers = _useWsl,
            RepositoryPath = _testRoot,
            ContainerImage = ContainerExecutorOptions.DefaultAgentBaseImage
        };

        var processRunner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        var envOptions = Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions());
        IWslMetadataService wslMetadataService = _useWsl
            ? new WslMetadataService(processRunner, envOptions, NullLogger<WslMetadataService>.Instance)
            : new Mock<IWslMetadataService>().Object;
        var pathResolver = new WorkspacePathResolver(wslMetadataService, envOptions);
        var sdkDiscovery = new HostSdkDiscoveryService(processRunner, wslMetadataService, envOptions, NullLogger<HostSdkDiscoveryService>.Instance);
        var companionInstaller = new CompanionToolInstaller(processRunner, NullLogger<CompanionToolInstaller>.Instance);
        var identityResolver = new HostUserIdentityResolver(processRunner, NullLogger<HostUserIdentityResolver>.Instance);

        var provider = new TestExecutionEnvironmentProviderBuilder
        {
            ProcessRunner = processRunner,
            PathResolver = pathResolver,
            SdkDiscoveryService = sdkDiscovery,
            CompanionToolInstaller = companionInstaller,
            UserIdentityResolver = identityResolver
        }.Build();

        var env = provider.GetEnvironment(workspace, SandboxSecurityTier.Standard, null);

        // 1. Verify container is responsive and standard utilities work
        var unameResult = await env.ExecuteAsync("uname -s");
        Assert.Equal(0, unameResult.ExitCode);
        Assert.Contains("Linux", unameResult.StandardOutput);

        // 2. Verify PATH contains mounted tools directory
        var pathResult = await env.ExecuteAsync("echo $PATH");
        Assert.Equal(0, pathResult.ExitCode);
        Assert.Contains("/tmp/ncf-home/.dotnet/tools", pathResult.StandardOutput);

        // 3. Verify /workspace/skills mount exists and is accessible
        var skillsResult = await env.ExecuteAsync("test -d /workspace/skills && echo skills-dir-exists");
        Assert.Equal(0, skillsResult.ExitCode);
        Assert.Contains("skills-dir-exists", skillsResult.StandardOutput);

        // 4. If companion tools are installed on the host/WSL (e.g. csnav, codecondenser, mdfetch), verify execution in container
        var checkCsnav = await env.ExecuteAsync("which csnav");
        if (checkCsnav.ExitCode == 0)
        {
            _output.WriteLine($"Detected csnav in container PATH: {checkCsnav.StandardOutput.Trim()}. Executing help check...");
            var csnavHelp = await env.ExecuteAsync("csnav --help");
            _output.WriteLine($"csnav ExitCode={csnavHelp.ExitCode}, Stdout='{csnavHelp.StandardOutput}', Stderr='{csnavHelp.StandardError}'");
            Assert.Equal(0, csnavHelp.ExitCode);
            Assert.Contains("csnav", csnavHelp.StandardOutput, StringComparison.OrdinalIgnoreCase);
        }

        var checkCodeCondenser = await env.ExecuteAsync("which codecondenser");
        if (checkCodeCondenser.ExitCode == 0)
        {
            _output.WriteLine("Detected codecondenser in container PATH. Executing help check...");
            var ccHelp = await env.ExecuteAsync("codecondenser --help");
            Assert.Equal(0, ccHelp.ExitCode);
            Assert.Contains("codecondenser", ccHelp.StandardOutput, StringComparison.OrdinalIgnoreCase);
        }

        var checkMdfetch = await env.ExecuteAsync("which mdfetch");
        if (checkMdfetch.ExitCode == 0)
        {
            _output.WriteLine("Detected mdfetch in container PATH. Executing help check...");
            var mdfetchHelp = await env.ExecuteAsync("mdfetch --help");
            Assert.Equal(0, mdfetchHelp.ExitCode);
        }
    }

    private static bool IsDockerAccessible(bool useWsl)
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
                try { process.Kill(); } catch { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveContainerSafe(string containerName, bool useWsl)
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
        catch
        {
            // Best-effort test container cleanup: ignore if container does not exist or daemon stopped
        }
    }
}
