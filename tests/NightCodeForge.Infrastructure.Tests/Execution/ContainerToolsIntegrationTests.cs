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

        executor.EnsureContainerRunning();

        // 0. Verify container-level ENV and pre-created directory exist before PersistentContainerShellSession preamble
        AssertDirectDockerExecVsTestArtifactsEnv(processRunner, options);

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

        // 4. Verify node and npm are present and functional
        var nodeResult = await executor.ExecuteAsync("node --version");
        Assert.Equal(0, nodeResult.ExitCode);
        Assert.StartsWith("v", nodeResult.StandardOutput.Trim());

        var npmResult = await executor.ExecuteAsync("npm --version");
        Assert.Equal(0, npmResult.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(npmResult.StandardOutput));
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
                    containerPath: ContainerExecutorOptions.HostDotnetToolsContainerPath,
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
        await executor.ExecuteAsync($"chmod +x {ContainerExecutorOptions.HostDotnetToolsContainerPath}/custom-echo-tool");

        // Verify PATH includes HostDotnetToolsContainerPath
        var pathResult = await executor.ExecuteAsync("echo $PATH");
        Assert.Equal(0, pathResult.ExitCode);
        Assert.Contains(ContainerExecutorOptions.HostDotnetToolsContainerPath, pathResult.StandardOutput);

        // Verify which resolves custom-echo-tool through PATH
        var whichResult = await executor.ExecuteAsync("which custom-echo-tool");
        Assert.Equal(0, whichResult.ExitCode);
        Assert.Contains($"{ContainerExecutorOptions.HostDotnetToolsContainerPath}/custom-echo-tool", whichResult.StandardOutput.Trim());

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
        var containerName = $"ncf-sandbox-{workspaceId}";
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

        var env = await provider.GetEnvironmentAsync(workspace, SandboxSecurityTier.Standard, null);

        // 1. Verify container is responsive and standard utilities work
        var unameResult = await env.ExecuteAsync("uname -s");
        Assert.Equal(0, unameResult.ExitCode);
        Assert.Contains("Linux", unameResult.StandardOutput);

        // 2. Verify PATH contains mounted tools directory and does not grow across executions
        var pathResult = await env.ExecuteAsync("echo $PATH");
        Assert.Equal(0, pathResult.ExitCode);
        Assert.Contains(ContainerExecutorOptions.HostDotnetToolsContainerPath, pathResult.StandardOutput);
        Assert.Contains($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools", pathResult.StandardOutput);

        var pathResultSecond = await env.ExecuteAsync("echo $PATH");
        Assert.Equal(0, pathResultSecond.ExitCode);
        Assert.Equal(pathResult.StandardOutput.Trim(), pathResultSecond.StandardOutput.Trim());

        // 2b. Verify $HOME and $HOME/.dotnet are writable by the container user (so dotnet build sentinel creation succeeds)
        var homeWritableResult = await env.ExecuteAsync("test -w \"$HOME\" && test -w \"$HOME/.dotnet\" && touch \"$HOME/.dotnet/sentinel.test\" && rm -f \"$HOME/.dotnet/sentinel.test\" && echo home-dotnet-writable");
        Assert.Equal(0, homeWritableResult.ExitCode);
        Assert.Contains("home-dotnet-writable", homeWritableResult.StandardOutput);

        // 2b-vstest. Verify container-level ENV and shell VSTEST_ARTIFACTS_BASE_DIRECTORY support 0700 subdirectories
        var containerExecutor = Assert.IsType<NightCodeForgeContainerExecutor>(env);
        AssertDirectDockerExecVsTestArtifactsEnv(processRunner, containerExecutor.Options);

        var vsTestArtifactsCheck = await env.ExecuteAsync(
            $"test \"${ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar}\" = \"{ContainerExecutorOptions.ContainerVsTestArtifactsBaseDirectory}\" && " +
            $"test -d \"${ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar}\" && " +
            $"test -w \"${ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar}\" && " +
            $"d=\"${ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar}/probe-$$\" && " +
            "mkdir -m 700 \"$d\" && " +
            "{ mode=\"$(stat -c %a \"$d\" 2>/dev/null || true)\"; rmdir \"$d\" 2>/dev/null || true; test \"$mode\" = \"700\"; } && " +
            "echo vstest-artifacts-ok");
        Assert.Equal(0, vsTestArtifactsCheck.ExitCode);
        Assert.Contains("vstest-artifacts-ok", vsTestArtifactsCheck.StandardOutput);

        // 2b-playwright-shm. Verify PLAYWRIGHT_BROWSERS_PATH is mounted at /cache/ms-playwright and /dev/shm is >= 1G
        var playwrightShmCheck = await env.ExecuteAsync(
            "test \"$PLAYWRIGHT_BROWSERS_PATH\" = \"/cache/ms-playwright\" && test -d /cache/ms-playwright && test $(df -k /dev/shm | awk 'NR==2 {print $2}') -ge 1048576 && echo playwright-and-shm-ok");
        Assert.Equal(0, playwrightShmCheck.ExitCode);
        Assert.Contains("playwright-and-shm-ok", playwrightShmCheck.StandardOutput);

        // 2b-workloads. Verify host non-root user .dotnet directory (when discovered on host) is mounted into $HOME/.dotnet
        var discoveredSdkMounts = sdkDiscovery.DiscoverSdkMountPaths(_useWsl ? wslMetadataService.GetDefaultDistributionName() : null);
        var hasHostUserDotnet = discoveredSdkMounts.Any(p =>
            p.TrimEnd('/').StartsWith("/home/", StringComparison.OrdinalIgnoreCase) &&
            p.TrimEnd('/').EndsWith("/.dotnet", StringComparison.OrdinalIgnoreCase));
        if (hasHostUserDotnet)
        {
            var workloadMountCheck = await env.ExecuteAsync(
                $"grep -q \" {ContainerExecutorOptions.ContainerHomePath}/.dotnet \" /proc/mounts && echo dotnet-workloads-mounted");
            Assert.Equal(0, workloadMountCheck.ExitCode);
            Assert.Contains("dotnet-workloads-mounted", workloadMountCheck.StandardOutput);
        }

        // 2c. Verify /tmp is bind-mounted from {hostWorkspacePath}/tmp and shared between host and container
        var hostTmpFile = pathResolver.ResolveCruciblePath(_testRoot, "/tmp/shared_tmp_check.py");
        Directory.CreateDirectory(Path.GetDirectoryName(hostTmpFile)!);
        await File.WriteAllTextAsync(hostTmpFile, "print('shared-tmp-ok')\n");
        var tmpExecResult = await env.ExecuteAsync("python3 /tmp/shared_tmp_check.py");
        Assert.Equal(0, tmpExecResult.ExitCode);
        Assert.Contains("shared-tmp-ok", tmpExecResult.StandardOutput);

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

    [Fact]
    public async Task ContainerExecutor_VsTestArtifactsDirectory_DecouplesFromDotnetWritabilityAndFallsBackWhenReadOnly()
    {
        if (!IsDockerAccessible(_useWsl))
        {
            _output.WriteLine("Docker is not accessible in this environment. Skipping test.");
            return;
        }

        var roDotnetDir = Path.Combine(_testRoot, "ro-dotnet");
        var roArtifactsDir = Path.Combine(_testRoot, "ro-vstest-artifacts");
        Directory.CreateDirectory(roDotnetDir);
        Directory.CreateDirectory(roArtifactsDir);

        var processRunner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        var identityResolver = new HostUserIdentityResolver(processRunner, NullLogger<HostUserIdentityResolver>.Instance);
        var telemetryMock = new Mock<IOperationsTelemetryService>();

        // Subcase A: read-only /ncf-home/.dotnet triggers HOME fallback, while VSTEST_ARTIFACTS_BASE_DIRECTORY stays in /ncf-home/.vstest-artifacts
        var containerA = $"ncf-vstest-ro-dotnet-{Guid.NewGuid():N}";
        _containersToCleanup.Add(containerA);
        var optionsA = new ContainerExecutorOptions
        {
            ContainerNameOrId = containerA,
            ContainerImage = ContainerExecutorOptions.DefaultAgentBaseImage,
            HostWorkspacePath = _testRoot,
            ContainerWorkspacePath = "/workspace",
            DefaultWorkingDirectory = "/workspace",
            AutoStartContainer = true,
            MountPackageCaches = false,
            MountHostSdks = false,
            UseWsl = _useWsl,
            DefaultTimeoutSeconds = 60,
            AdditionalVolumeMounts =
            [
                new(roDotnetDir, $"{ContainerExecutorOptions.ContainerHomePath}/.dotnet", isReadOnly: true)
            ]
        };

        await using (var executorA = new NightCodeForgeContainerExecutor(
            Options.Create(optionsA),
            processRunner,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            identityResolver,
            telemetryMock.Object))
        {
            var resA = await executorA.ExecuteAsync(
                "case \"$HOME\" in /tmp/ncf-home-*) ;; *) echo \"unexpected-home:$HOME\"; exit 1;; esac && " +
                $"test \"${ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar}\" = \"{ContainerExecutorOptions.ContainerVsTestArtifactsBaseDirectory}\" && " +
                $"d=\"${ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar}/probe-$$\" && " +
                "mkdir -m 700 \"$d\" && " +
                "{ mode=\"$(stat -c %a \"$d\" 2>/dev/null || true)\"; rmdir \"$d\" 2>/dev/null || true; test \"$mode\" = \"700\"; } && " +
                "echo decoupled-ok");
            Assert.Equal(0, resA.ExitCode);
            Assert.Contains("decoupled-ok", resA.StandardOutput);
        }

        // Subcase B: read-only /ncf-home/.vstest-artifacts falls back to /tmp/ncf-home-$(id -u)/.vstest-artifacts
        var containerB = $"ncf-vstest-ro-dir-{Guid.NewGuid():N}";
        _containersToCleanup.Add(containerB);
        var optionsB = new ContainerExecutorOptions
        {
            ContainerNameOrId = containerB,
            ContainerImage = ContainerExecutorOptions.DefaultAgentBaseImage,
            HostWorkspacePath = _testRoot,
            ContainerWorkspacePath = "/workspace",
            DefaultWorkingDirectory = "/workspace",
            AutoStartContainer = true,
            MountPackageCaches = false,
            MountHostSdks = false,
            UseWsl = _useWsl,
            DefaultTimeoutSeconds = 60,
            AdditionalVolumeMounts =
            [
                new(roArtifactsDir, ContainerExecutorOptions.ContainerVsTestArtifactsBaseDirectory, isReadOnly: true)
            ]
        };

        await using (var executorB = new NightCodeForgeContainerExecutor(
            Options.Create(optionsB),
            processRunner,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            identityResolver,
            telemetryMock.Object))
        {
            var resB = await executorB.ExecuteAsync(
                $"expected=\"/tmp/ncf-home-$(id -u)/{ContainerExecutorOptions.VsTestArtifactsSubDirectoryName}\" && " +
                $"test \"${ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar}\" = \"$expected\" && " +
                $"test -d \"${ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar}\" && " +
                $"test -w \"${ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar}\" && " +
                "echo fallback-ok");
            Assert.Equal(0, resB.ExitCode);
            Assert.Contains("fallback-ok", resB.StandardOutput);
        }
    }

    [Fact]
    public async Task ContainerExecutor_RestrictedReadOnly_SupportsWritableVsTestArtifactsDirectoryWhenTmpIsReadOnly()
    {
        if (!IsDockerAccessible(_useWsl))
        {
            _output.WriteLine("Docker is not accessible in this environment. Skipping test.");
            return;
        }

        var roTmpDir = Path.Combine(_testRoot, "ro-tmp");
        Directory.CreateDirectory(roTmpDir);

        var containerName = $"ncf-vstest-ro-tier-{Guid.NewGuid():N}";
        _containersToCleanup.Add(containerName);

        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = containerName,
            ContainerImage = ContainerExecutorOptions.DefaultAgentBaseImage,
            HostWorkspacePath = _testRoot,
            ContainerWorkspacePath = "/workspace",
            DefaultWorkingDirectory = "/workspace",
            AutoStartContainer = true,
            IsRestrictedReadOnly = true,
            MountPackageCaches = false,
            MountHostSdks = false,
            UseWsl = _useWsl,
            DefaultTimeoutSeconds = 60,
            AdditionalVolumeMounts =
            [
                new(roTmpDir, "/tmp", isReadOnly: true)
            ]
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

        var res = await executor.ExecuteAsync(
            "! test -w /tmp && " +
            $"test \"${ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar}\" = \"{ContainerExecutorOptions.ContainerVsTestArtifactsBaseDirectory}\" && " +
            $"test -d \"${ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar}\" && " +
            $"test -w \"${ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar}\" && " +
            $"d=\"${ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar}/probe-$$\" && " +
            "mkdir -m 700 \"$d\" && " +
            "{ mode=\"$(stat -c %a \"$d\" 2>/dev/null || true)\"; rmdir \"$d\" 2>/dev/null || true; test \"$mode\" = \"700\"; } && " +
            "echo ro-tier-vstest-ok");
        Assert.Equal(0, res.ExitCode);
        Assert.Contains("ro-tier-vstest-ok", res.StandardOutput);
    }

    private static void AssertDirectDockerExecVsTestArtifactsEnv(IProcessRunner processRunner, ContainerExecutorOptions options)
    {
        var effectiveUser = options.IsRestrictedReadOnly
            ? ContainerExecutorOptions.RestrictedReadOnlyUserUidGid
            : options.UserUidGid;

        List<string> BuildExecArgs(params string[] commandArgs)
        {
            var args = new List<string> { "exec" };
            if (!string.IsNullOrWhiteSpace(effectiveUser))
            {
                args.AddRange(["--user", effectiveUser]);
            }
            args.Add(options.ContainerNameOrId);
            args.AddRange(commandArgs);
            return args;
        }

        var printEnvResult = processRunner.Run(NightCodeForgeContainerExecutor.BuildDockerProcessSpec(
            options,
            BuildExecArgs("printenv", ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar),
            timeoutSeconds: 15));
        Assert.Equal(0, printEnvResult.ExitCode);
        Assert.Equal(ContainerExecutorOptions.ContainerVsTestArtifactsBaseDirectory, printEnvResult.StandardOutput.Trim());

        var dirCheckResult = processRunner.Run(NightCodeForgeContainerExecutor.BuildDockerProcessSpec(
            options,
            BuildExecArgs(
                "sh",
                "-c",
                $"test -d {ContainerExecutorOptions.ContainerVsTestArtifactsBaseDirectory} && " +
                $"test -w {ContainerExecutorOptions.ContainerVsTestArtifactsBaseDirectory} && echo container-env-vstest-ok"),
            timeoutSeconds: 15));
        Assert.Equal(0, dirCheckResult.ExitCode);
        Assert.Contains("container-env-vstest-ok", dirCheckResult.StandardOutput);
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
