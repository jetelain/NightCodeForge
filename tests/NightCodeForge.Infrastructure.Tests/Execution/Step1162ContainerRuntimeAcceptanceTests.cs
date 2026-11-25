using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Security;
using NightCodeForge.Infrastructure.Execution;
using Xunit.Abstractions;

namespace NightCodeForge.Infrastructure.Tests.Execution;

[Trait("Category", "Acceptance")]
public sealed class Step1162ContainerRuntimeAcceptanceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testRoot;
    private readonly bool _useWsl;
    private readonly List<string> _containersToCleanup = new();

    public Step1162ContainerRuntimeAcceptanceTests(ITestOutputHelper output)
    {
        _output = output;
        _testRoot = Path.Combine(Path.GetTempPath(), $"ncf_1162_accept_{Guid.NewGuid():N}");
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
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Failed to clean up test directory '{_testRoot}': {ex.Message}");
        }
    }

    [Fact]
    public async Task Python39And311CustomImages_ExcludeHostMultiarchLibsAndKeepSslAndPipWorking_WhileManagedImageKeepsLibs()
    {
        if (!IsDockerAccessible(_useWsl))
        {
            _output.WriteLine("Docker is not accessible in this environment. Skipping live acceptance test.");
            return;
        }

        var requiredImages = new[] { "python:3.9", "python:3.11", ContainerExecutorOptions.DefaultAgentBaseImage };
        foreach (var img in requiredImages)
        {
            if (!DoesDockerImageExist(img, _useWsl))
            {
                _output.WriteLine($"Required Docker image '{img}' is not present locally. Skipping live acceptance test.");
                return;
            }
        }

        var processRunner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        var wslMetadata = new WslMetadataService(processRunner, Options.Create(new ExecutionEnvironmentProviderOptions()), NullLogger<WslMetadataService>.Instance);
        var providerOptions = Options.Create(new ExecutionEnvironmentProviderOptions
        {
            UseWslForContainers = _useWsl,
            DefaultWslDistribution = _useWsl ? wslMetadata.GetDefaultDistributionName() : null
        });
        var sdkDiscovery = new HostSdkDiscoveryService(processRunner, wslMetadata, providerOptions, NullLogger<HostSdkDiscoveryService>.Instance);
        var identityResolver = new HostUserIdentityResolver(processRunner, NullLogger<HostUserIdentityResolver>.Instance);
        var pathResolver = new WorkspacePathResolver(wslMetadata, providerOptions);
        var factory = new ContainerEnvironmentFactory(
            processRunner,
            providerOptions,
            Options.Create(new SecurityOptions()),
            pathResolver,
            sdkDiscovery,
            identityResolver,
            new Mock<IOperationsTelemetryService>().Object,
            new Mock<ICompanionToolInstaller>().Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance);

        foreach (var pythonImage in new[] { "python:3.9", "python:3.11" })
        {
            var wksDir = Path.Combine(_testRoot, pythonImage.Replace(':', '_'));
            Directory.CreateDirectory(wksDir);
            var containerName = $"ncf-1162-py-{Guid.NewGuid():N}"[..24];
            _containersToCleanup.Add(containerName);

            var scope = new SandboxScopeDescriptor(
                SandboxScopeKind.WorkspaceClone,
                ExecutionMode.ContainerSandbox,
                SandboxSecurityTier.Standard,
                SandboxMountTable.ForWorkspace(wksDir, isReadOnly: false),
                containerImage: pythonImage,
                containerName: containerName,
                repositoryPath: wksDir,
                hostWorkspacePath: wksDir,
                mountHostDotnetTools: false,
                useWslForContainers: _useWsl,
                wslDistribution: providerOptions.Value.DefaultWslDistribution,
                mountAcpHostPaths: false);

            await using var executor = Assert.IsType<NightCodeForgeContainerExecutor>(
                await factory.CreateContainerEnvironmentAsync(scope));

            executor.EnsureContainerRunning();

            var sslResult = await executor.ExecuteAsync("python3 -c \"import ssl, _ssl; print(ssl.OPENSSL_VERSION)\"");
            Assert.True(sslResult.ExitCode == 0, $"{pythonImage} _ssl failed: {sslResult.StandardOutput}\n{sslResult.StandardError}");
            Assert.DoesNotContain("OPENSSL_3.3.0", sslResult.StandardOutput + sslResult.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("libcrypto.so.3", sslResult.StandardOutput + sslResult.StandardError, StringComparison.OrdinalIgnoreCase);

            var pipResult = await executor.ExecuteAsync("python3 -m pip --version");
            Assert.True(pipResult.ExitCode == 0, $"{pythonImage} pip failed: {pipResult.StandardOutput}\n{pipResult.StandardError}");
            Assert.DoesNotContain("OPENSSL_3.3.0", pipResult.StandardOutput + pipResult.StandardError, StringComparison.OrdinalIgnoreCase);

            var inspectResult = await processRunner.RunAsync(ContainerMountAndDriftBuilder.BuildDockerProcessSpec(
                executor.Options,
                ["inspect", "-f", "{{range .Mounts}}{{.Destination}}={{.Source}};{{end}}", containerName],
                timeoutSeconds: 10));
            Assert.Equal(0, inspectResult.ExitCode);
            Assert.DoesNotContain("linux-gnu", inspectResult.StandardOutput, StringComparison.OrdinalIgnoreCase);
        }

        // Verify managed base image preserves host multiarch shared-library mounts when discovered on host
        var hostDetails = sdkDiscovery.DiscoverSdkDetails(providerOptions.Value.DefaultWslDistribution);
        var discoveredMultiarch = hostDetails.MountPaths
            .Where(ContainerMountAndDriftBuilder.IsHostMultiarchSharedLibraryPath)
            .ToList();
        if (discoveredMultiarch.Count > 0)
        {
            var managedWks = Path.Combine(_testRoot, "managed");
            Directory.CreateDirectory(managedWks);
            var managedContainer = $"ncf-1162-v6-{Guid.NewGuid():N}"[..24];
            _containersToCleanup.Add(managedContainer);

            var managedScope = new SandboxScopeDescriptor(
                SandboxScopeKind.WorkspaceClone,
                ExecutionMode.ContainerSandbox,
                SandboxSecurityTier.Standard,
                SandboxMountTable.ForWorkspace(managedWks, isReadOnly: false),
                containerImage: ContainerExecutorOptions.DefaultAgentBaseImage,
                containerName: managedContainer,
                repositoryPath: managedWks,
                hostWorkspacePath: managedWks,
                mountHostDotnetTools: false,
                useWslForContainers: _useWsl,
                wslDistribution: providerOptions.Value.DefaultWslDistribution,
                mountAcpHostPaths: false);

            await using var managedExecutor = Assert.IsType<NightCodeForgeContainerExecutor>(
                await factory.CreateContainerEnvironmentAsync(managedScope));
            managedExecutor.EnsureContainerRunning();

            var managedInspect = await processRunner.RunAsync(ContainerMountAndDriftBuilder.BuildDockerProcessSpec(
                managedExecutor.Options,
                ["inspect", "-f", "{{range .Mounts}}{{.Destination}}={{.Source}};{{end}}", managedContainer],
                timeoutSeconds: 10));
            Assert.Equal(0, managedInspect.ExitCode);
            foreach (var multiarchPath in discoveredMultiarch)
            {
                Assert.Contains($"{multiarchPath}=", managedInspect.StandardOutput, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task DetectContainerDriftAsync_LiveStalePythonContainerWithHostMultiarchMount_FlagsDrift()
    {
        if (!IsDockerAccessible(_useWsl) || !DoesDockerImageExist("python:3.11", _useWsl))
        {
            _output.WriteLine("Docker or python:3.11 not available. Skipping live drift acceptance test.");
            return;
        }

        var processRunner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        var wslMetadata = new WslMetadataService(processRunner, Options.Create(new ExecutionEnvironmentProviderOptions()), NullLogger<WslMetadataService>.Instance);
        var distro = _useWsl ? wslMetadata.GetDefaultDistributionName() : null;
        var containerName = $"ncf-1162-drift-{Guid.NewGuid():N}"[..26];
        _containersToCleanup.Add(containerName);

        var wksDir = Path.Combine(_testRoot, "drift-wks");
        Directory.CreateDirectory(wksDir);
        var hostWks = _useWsl ? NightCodeForge.Infrastructure.Git.WslPathConverter.ToWslPath(wksDir) : wksDir;

        // Spawn a stale container explicitly carrying /usr/lib/x86_64-linux-gnu
        var targetOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = containerName,
            ContainerImage = "python:3.11",
            HostWorkspacePath = hostWks,
            MountPackageCaches = false,
            MountHostSdks = true,
            SdkMountPaths = ["/usr/lib/x86_64-linux-gnu"],
            UseWsl = _useWsl,
            WslDistribution = distro
        };

        var spawnResult = await processRunner.RunAsync(ContainerMountAndDriftBuilder.BuildDockerProcessSpec(
            targetOptions,
            ["run", "-d", "--name", containerName, "-v", $"{hostWks}:/workspace", "-v", "/usr/lib/x86_64-linux-gnu:/usr/lib/x86_64-linux-gnu:ro", "python:3.11", "tail", "-f", "/dev/null"],
            timeoutSeconds: 20));
        if (spawnResult.ExitCode != 0)
        {
            _output.WriteLine($"Skipping drift test because host /usr/lib/x86_64-linux-gnu could not be mounted: {spawnResult.StandardError}");
            return;
        }

        var drift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(processRunner, targetOptions);
        Assert.NotNull(drift);
        Assert.Contains("/usr/lib/x86_64-linux-gnu (expected absent)", drift);
    }

    [Fact]
    public async Task HostRustupToolchain_LiveDiscoveryAndContainerExecution_CompilesAndRunsRustProgram()
    {
        if (!IsDockerAccessible(_useWsl) || !DoesDockerImageExist(ContainerExecutorOptions.DefaultAgentBaseImage, _useWsl))
        {
            _output.WriteLine("Docker or ncf-agent-base:v6 not available. Skipping live Rust acceptance test.");
            return;
        }

        var processRunner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        var wslMetadata = new WslMetadataService(processRunner, Options.Create(new ExecutionEnvironmentProviderOptions()), NullLogger<WslMetadataService>.Instance);
        var providerOptions = Options.Create(new ExecutionEnvironmentProviderOptions
        {
            UseWslForContainers = _useWsl,
            DefaultWslDistribution = _useWsl ? wslMetadata.GetDefaultDistributionName() : null
        });
        var sdkDiscovery = new HostSdkDiscoveryService(processRunner, wslMetadata, providerOptions, NullLogger<HostSdkDiscoveryService>.Instance);
        var details = await sdkDiscovery.DiscoverSdkDetailsAsync(providerOptions.Value.DefaultWslDistribution);

        if (!details.CommandPaths.TryGetValue("cargo", out var cargoPath) ||
            !details.CommandPaths.TryGetValue("rustc", out var rustcPath) ||
            !details.MountPaths.Any(p => !HostSdkDiscoveryService.IsExcludedDirectory(p) &&
                                         (cargoPath.StartsWith(HostSdkDiscoveryService.NormalizePath(p) + "/", StringComparison.Ordinal) ||
                                          cargoPath.Contains("/.cargo/", StringComparison.Ordinal))))
        {
            _output.WriteLine("No mountable host rustup toolchain installed on this host. Skipping live Rust acceptance test.");
            return;
        }

        var identityResolver = new HostUserIdentityResolver(processRunner, NullLogger<HostUserIdentityResolver>.Instance);
        var pathResolver = new WorkspacePathResolver(wslMetadata, providerOptions);
        var factory = new ContainerEnvironmentFactory(
            processRunner,
            providerOptions,
            Options.Create(new SecurityOptions()),
            pathResolver,
            sdkDiscovery,
            identityResolver,
            new Mock<IOperationsTelemetryService>().Object,
            new Mock<ICompanionToolInstaller>().Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance);

        var wksDir = Path.Combine(_testRoot, "rust-wks");
        Directory.CreateDirectory(Path.Combine(wksDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(wksDir, "Cargo.toml"),
            """
            [package]
            name = "ncf_rust_accept"
            version = "0.1.0"
            edition = "2021"
            """);
        await File.WriteAllTextAsync(Path.Combine(wksDir, "src", "main.rs"),
            """
            fn main() {
                println!("ncf-rust-ok:{}", 2 + 2);
            }
            """);

        var containerName = $"ncf-1162-rust-{Guid.NewGuid():N}"[..25];
        _containersToCleanup.Add(containerName);

        var scope = new SandboxScopeDescriptor(
            SandboxScopeKind.WorkspaceClone,
            ExecutionMode.ContainerSandbox,
            SandboxSecurityTier.Standard,
            SandboxMountTable.ForWorkspace(wksDir, isReadOnly: false),
            containerImage: ContainerExecutorOptions.DefaultAgentBaseImage,
            containerName: containerName,
            repositoryPath: wksDir,
            hostWorkspacePath: wksDir,
            mountHostDotnetTools: false,
            useWslForContainers: _useWsl,
            wslDistribution: providerOptions.Value.DefaultWslDistribution,
            mountAcpHostPaths: false);

        await using var executor = Assert.IsType<NightCodeForgeContainerExecutor>(
            await factory.CreateContainerEnvironmentAsync(scope));
        executor.EnsureContainerRunning();

        var inspectResult = await processRunner.RunAsync(ContainerMountAndDriftBuilder.BuildDockerProcessSpec(
            executor.Options,
            ["inspect", "-f", "{{range .Mounts}}{{.Destination}}={{.Source}};{{end}}", containerName],
            timeoutSeconds: 10));
        Assert.Equal(0, inspectResult.ExitCode);
        Assert.DoesNotContain("/usr/bin=", inspectResult.StandardOutput, StringComparison.OrdinalIgnoreCase);

        var execResult = await executor.ExecuteAsync("echo \"CARGO_HOME=$CARGO_HOME\" && cargo --version && rustc --version && cargo run --offline");
        Assert.True(execResult.ExitCode == 0, $"Rust container execution failed:\n{execResult.StandardOutput}\n{execResult.StandardError}");
        Assert.Contains("CARGO_HOME=/cache/cargo", execResult.StandardOutput);
        Assert.Contains("cargo ", execResult.StandardOutput);
        Assert.Contains("rustc ", execResult.StandardOutput);
        Assert.Contains("ncf-rust-ok:4", execResult.StandardOutput);
    }

    private static bool IsDockerAccessible(bool useWsl) => RunQuickCheck(useWsl, "docker info");

    private static bool DoesDockerImageExist(string image, bool useWsl) => RunQuickCheck(useWsl, $"docker image inspect {image}");

    private static bool RunQuickCheck(bool useWsl, string command)
    {
        try
        {
            var psi = useWsl
                ? new ProcessStartInfo("wsl.exe", $"-- {command}")
                : new ProcessStartInfo("sh", $"-c \"{command}\"");
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
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Justification: process may have exited concurrently before Kill() ran.
                }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Justification: prerequisite tool (wsl.exe/docker) is unavailable on this host.
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
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Best-effort cleanup of acceptance test containers.
        }
    }
}
