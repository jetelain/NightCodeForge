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
using Xunit.Abstractions;

namespace NightCodeForge.Benchmark.Cli.Tests.Verification;

[Trait("Category", "Acceptance")]
public sealed class Step1162BenchmarkContainerAcceptanceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testRoot;
    private readonly bool _useWsl;
    private readonly List<string> _containersToCleanup = new();

    public Step1162BenchmarkContainerAcceptanceTests(ITestOutputHelper output)
    {
        _output = output;
        _testRoot = Path.Combine(Path.GetTempPath(), $"ncf_1162_bench_accept_{Guid.NewGuid():N}");
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
            _output.WriteLine($"Failed to clean up test root '{_testRoot}': {ex.Message}");
        }
    }

    [Fact]
    public async Task HostRustupPreflight_PassesOnLocalEnvironmentWhenRustupToolchainConfigured()
    {
        if (!RunQuickCheck(_useWsl, "rustup which cargo"))
        {
            _output.WriteLine("Host rustup with an active toolchain is not available. Skipping live pre-flight acceptance test.");
            return;
        }

        var (factory, sdkDiscovery, _) = CreateRuntimeServices();
        var validator = new BenchmarkToolchainPreflightValidator(sdkDiscovery, factory, Mock.Of<IBenchmarkCustomImageBuilder>());

        foreach (var category in new[] { BenchmarkProblemCategory.Implementation, BenchmarkProblemCategory.CodeReview })
        {
            var instance = new BenchmarkInstance
            {
                InstanceId = $"burntsushi__ripgrep-2576-{category}",
                Category = category,
                Language = BenchmarkLanguages.Rust,
                Repo = "burntsushi/ripgrep",
                BaseCommit = "abc123",
                ProblemStatement = "Fix bug"
            };

            var error = await validator.ValidateAsync(instance, category, ExecutionMode.ContainerSandbox);
            Assert.Null(error);
        }
    }

    [Fact]
    public async Task IsolatedNodeReporters_OnNode18WithPoisonedPrefixes_EmitAndParseJUnitXml()
    {
        if (!RunQuickCheck(_useWsl, "docker info") ||
            !RunQuickCheck(_useWsl, $"docker image inspect {ContainerExecutorOptions.DefaultAgentBaseImage}"))
        {
            _output.WriteLine("Docker or ncf-agent-base:v6 not available. Skipping live Node 18 reporter acceptance test.");
            return;
        }

        var (factory, _, providerOptions) = CreateRuntimeServices();

        // 1. Mocha fixture (pinned Node-18-compatible mocha@10.8.2 + pre-seeded poisoned /tmp/ncf-mocha-junit/node_modules/mocha)
        var mochaDir = Path.Combine(_testRoot, "mocha-wks");
        Directory.CreateDirectory(Path.Combine(mochaDir, "test"));
        Directory.CreateDirectory(Path.Combine(mochaDir, BenchmarkPaths.BenchmarkResultDirRelative));
        await File.WriteAllTextAsync(Path.Combine(mochaDir, "package.json"),
            """
            {
              "name": "ncf-mocha-accept",
              "version": "1.0.0",
              "scripts": { "test:mocha": "mocha" },
              "devDependencies": { "mocha": "10.8.2" }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(mochaDir, "test", "basic.test.js"),
            """
            const assert = require('assert');
            describe('MochaSuite', function () {
              it('passes arithmetic check', function () {
                assert.strictEqual(1 + 1, 2);
              });
            });
            """);

        var mochaInstance = new BenchmarkInstance
        {
            InstanceId = "axios__axios-5892-accept",
            Category = BenchmarkProblemCategory.Implementation,
            Language = BenchmarkLanguages.JavaScript,
            Repo = "axios/axios",
            BaseCommit = "abc",
            ProblemStatement = "Test mocha",
            CustomTestCommand = "npm run test:mocha",
            TestResultFormat = TestResultFormat.JUnitXml
        };

        await VerifyLiveNodeFixtureAsync(
            factory,
            mochaDir,
            mochaInstance,
            prefixRoot: "/tmp/ncf-mocha-junit",
            reporterPackage: "mocha-junit-reporter",
            poisonedRunnerPackage: "mocha",
            expectedTestNameFragment: "passes arithmetic check");

        // 2. Jest fixture (jest@29.7.0 + pre-seeded poisoned /tmp/ncf-jest-junit/node_modules/jest)
        var jestDir = Path.Combine(_testRoot, "jest-wks");
        Directory.CreateDirectory(Path.Combine(jestDir, "test"));
        Directory.CreateDirectory(Path.Combine(jestDir, BenchmarkPaths.BenchmarkResultDirRelative));
        await File.WriteAllTextAsync(Path.Combine(jestDir, "package.json"),
            """
            {
              "name": "ncf-jest-accept",
              "version": "1.0.0",
              "devDependencies": { "jest": "29.7.0" }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(jestDir, "test", "basic.test.js"),
            """
            test('jest passes arithmetic check', () => {
              expect(2 + 3).toBe(5);
            });
            """);

        var jestInstance = new BenchmarkInstance
        {
            InstanceId = "jest-accept",
            Category = BenchmarkProblemCategory.Implementation,
            Language = BenchmarkLanguages.JavaScript,
            Repo = "owner/jest-repo",
            BaseCommit = "abc",
            ProblemStatement = "Test jest",
            TestResultFormat = TestResultFormat.JUnitXml
        };

        await VerifyLiveNodeFixtureAsync(
            factory,
            jestDir,
            jestInstance,
            prefixRoot: "/tmp/ncf-jest-junit",
            reporterPackage: "jest-junit",
            poisonedRunnerPackage: "jest",
            expectedTestNameFragment: "jest passes arithmetic check");
    }

    private async Task VerifyLiveNodeFixtureAsync(
        ContainerEnvironmentFactory factory,
        string workspaceDir,
        BenchmarkInstance instance,
        string prefixRoot,
        string reporterPackage,
        string poisonedRunnerPackage,
        string expectedTestNameFragment)
    {
        var containerName = $"ncf-1162-node-{Guid.NewGuid():N}"[..25];
        _containersToCleanup.Add(containerName);

        var scope = BenchmarkVerifier.CreateVerificationScope(
            workspaceDir,
            customContainerImage: null,
            ExecutionMode.ContainerSandbox,
            containerName);

        await using var executor = Assert.IsType<NightCodeForgeContainerExecutor>(
            await factory.CreateContainerEnvironmentAsync(scope));
        executor.EnsureContainerRunning();

        // Seed both a stale broken reporter directory AND a poisoned runner directory (e.g. ESM-only mocha@12)
        // inside /tmp prefix so the pre-install eviction guard (`if [ -d prefix/node_modules/runner ]; then rm -rf prefix`)
        // is genuinely exercised rather than skipped/bypassed by a missing reporter folder.
        var poisonedPrefix = $"{prefixRoot}/node_modules/{poisonedRunnerPackage}";
        var brokenReporterDir = $"{prefixRoot}/node_modules/{reporterPackage}";
        var seedResult = await executor.ExecuteAsync(
            $"mkdir -p {poisonedPrefix} {brokenReporterDir} && " +
            $"echo '{{\"type\":\"module\"}}' > {poisonedPrefix}/package.json && " +
            $"echo 'throw new Error(\"stale-reporter-not-evicted\");' > {brokenReporterDir}/index.js");
        Assert.Equal(0, seedResult.ExitCode);

        var testCmd = BenchmarkTestCommandResolver.ResolveTestCommand(instance, workspaceDir, ExecutionMode.ContainerSandbox);
        var wrappedCmd = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(testCmd, instance.Language, ExecutionMode.ContainerSandbox);

        var runResult = await executor.ExecuteAsync(wrappedCmd, timeoutSeconds: 180);
        var combinedOutput = $"{runResult.StandardOutput}\n{runResult.StandardError}";
        Assert.True(runResult.ExitCode == 0, $"{instance.InstanceId} failed (exit {runResult.ExitCode}):\n{combinedOutput}");
        Assert.DoesNotContain("ERR_REQUIRE_ESM", combinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cannot find module", combinedOutput, StringComparison.OrdinalIgnoreCase);

        var runnerCheck = await executor.ExecuteAsync($"test ! -d {poisonedPrefix}");
        Assert.Equal(0, runnerCheck.ExitCode);

        var junitPath = Path.Combine(workspaceDir, BenchmarkPaths.BenchmarkResultDirRelative, BenchmarkPaths.JUnitFileName);
        Assert.True(File.Exists(junitPath), $"Expected JUnit XML at '{junitPath}'");

        var parsed = JUnitXmlTestResultParser.ParseFile(junitPath);
        Assert.NotEmpty(parsed);
        Assert.Contains(parsed, o => o.Passed && o.Name.Contains(expectedTestNameFragment, StringComparison.OrdinalIgnoreCase));
    }

    private (ContainerEnvironmentFactory Factory, HostSdkDiscoveryService SdkDiscovery, IOptions<ExecutionEnvironmentProviderOptions> ProviderOptions) CreateRuntimeServices()
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

        return (factory, sdkDiscovery, providerOptions);
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
            // Justification: prerequisite tool (wsl.exe/docker/rustup) is unavailable on this host.
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
