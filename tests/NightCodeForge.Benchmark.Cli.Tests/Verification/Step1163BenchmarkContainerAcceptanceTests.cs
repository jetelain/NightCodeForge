using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Benchmark.Cli.Verification;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Security;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Benchmark.Cli.Tests.Verification;

[Trait("Category", "Acceptance")]
public sealed class Step1163BenchmarkContainerAcceptanceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly bool _useWsl;
    private readonly List<string> _containersToCleanup = new();

    public Step1163BenchmarkContainerAcceptanceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"ncf_1163_bench_accept_{Guid.NewGuid():N}");
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
        catch (IOException)
        {
            // Best-effort cleanup of temp acceptance test directory.
        }
    }

    [Fact]
    public async Task WrapWithContainerDependencySetup_PythonPipConstraintsAndCaBundle_PinsWerkzeugInsidePython311Container()
    {
        if (!RunQuickCheck(_useWsl, "docker info >/dev/null 2>&1"))
        {
            return;
        }

        var workDir = Path.Combine(_testRoot, "py-pinned");
        Directory.CreateDirectory(workDir);
        await File.WriteAllTextAsync(Path.Combine(workDir, "pyproject.toml"),
            """
            [build-system]
            requires = ["setuptools"]
            build-backend = "setuptools.build_meta"

            [project]
            name = "ncf_pin_probe"
            version = "0.1.0"
            dependencies = ["Werkzeug>=2.2"]
            """);
        await File.WriteAllTextAsync(Path.Combine(workDir, "test_pin.py"),
            """
            import os
            import importlib.metadata

            def test_werkzeug_is_pinned_and_ca_bundle_exported():
                version = importlib.metadata.version("Werkzeug")
                major = int(version.split(".")[0])
                assert major < 3, f"Expected Werkzeug < 3.0, got {version}"
                assert os.environ.get("REQUESTS_CA_BUNDLE") == "/etc/ssl/certs/ca-certificates.crt"
            """);

        var containerName = $"ncf-1163-py-{Guid.NewGuid():N}"[..24];
        _containersToCleanup.Add(containerName);

        var factory = CreateRuntimeServices();
        var scope = BenchmarkVerifier.CreateVerificationScope(
            workDir,
            customContainerImage: "python:3.11",
            ExecutionMode.ContainerSandbox,
            containerName);

        await using var executor = Assert.IsType<NightCodeForgeContainerExecutor>(
            await factory.CreateContainerEnvironmentAsync(scope));
        executor.EnsureContainerRunning();

        var wrapped = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(
            "pytest test_pin.py --junitxml=/workspace/.ncf-bench-results/junit.xml",
            BenchmarkLanguages.Python,
            ExecutionMode.ContainerSandbox,
            ["Werkzeug<3.0"]);

        var result = await executor.ExecuteAsync(wrapped, timeoutSeconds: 180);
        Assert.True(result.ExitCode == 0, $"Python pip_constraints acceptance test failed (exit {result.ExitCode}):\n{result.StandardOutput}\n{result.StandardError}");

        var junitPath = Path.Combine(workDir, BenchmarkPaths.BenchmarkResultDirRelative, BenchmarkPaths.JUnitFileName);
        Assert.True(File.Exists(junitPath), $"Expected JUnit XML at '{junitPath}'");
        var outcomes = JUnitXmlTestResultParser.ParseFile(junitPath);
        Assert.Contains(outcomes, o => o.Passed && o.Name.Contains("test_werkzeug_is_pinned_and_ca_bundle_exported", StringComparison.Ordinal));
    }

    private ContainerEnvironmentFactory CreateRuntimeServices()
    {
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
        return new ContainerEnvironmentFactory(
            processRunner,
            providerOptions,
            Options.Create(new SecurityOptions()),
            pathResolver,
            sdkDiscovery,
            identityResolver,
            new Mock<IOperationsTelemetryService>().Object,
            new Mock<ICompanionToolInstaller>().Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance);
    }

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
            // Justification: prerequisite tool is unavailable on this host.
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
