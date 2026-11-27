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
public sealed class Step1164ShellFramingAndEnvParityAcceptanceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testRoot;
    private readonly bool _useWsl;
    private readonly List<string> _containersToCleanup = new();

    public Step1164ShellFramingAndEnvParityAcceptanceTests(ITestOutputHelper output)
    {
        _output = output;
        _testRoot = Path.Combine(Path.GetTempPath(), $"ncf_1164_accept_{Guid.NewGuid():N}");
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
        catch (IOException ex)
        {
            _output.WriteLine($"Failed to clean up test directory '{_testRoot}': {ex.Message}");
        }
    }

    [Fact]
    public async Task ManagedBaseImage_IsolatesUnbalancedBackticksAndQuotes_RunsCsnavDecompile_AndExportsParityEnv()
    {
        if (!IsDockerAccessible(_useWsl) || !DoesDockerImageExist(ContainerExecutorOptions.DefaultAgentBaseImage, _useWsl))
        {
            _output.WriteLine("Docker or ncf-agent-base:v6 not available. Skipping live Step 11.64 acceptance test.");
            return;
        }

        var wksDir = Path.Combine(_testRoot, "wks-framing");
        var roPnpmDir = Path.Combine(_testRoot, "ro-pnpm");
        var roNpmDir = Path.Combine(_testRoot, "ro-npm");
        Directory.CreateDirectory(wksDir);
        Directory.CreateDirectory(roPnpmDir);
        Directory.CreateDirectory(roNpmDir);
        await File.WriteAllTextAsync(
            Path.Combine(wksDir, "Sample.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(wksDir, "Sample.cs"), "public class Sample {}");

        var containerName = $"ncf-1164-{Guid.NewGuid():N}"[..24];
        _containersToCleanup.Add(containerName);

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

        var scope = new SandboxScopeDescriptor(
            SandboxScopeKind.WorkspaceClone,
            ExecutionMode.ContainerSandbox,
            SandboxSecurityTier.Standard,
            SandboxMountTable.ForWorkspace(wksDir, isReadOnly: false),
            containerImage: ContainerExecutorOptions.DefaultAgentBaseImage,
            containerName: containerName,
            repositoryPath: wksDir,
            hostWorkspacePath: wksDir,
            mountHostDotnetTools: true,
            useWslForContainers: _useWsl,
            wslDistribution: providerOptions.Value.DefaultWslDistribution,
            mountAcpHostPaths: false);

        await using var executor = Assert.IsType<NightCodeForgeContainerExecutor>(
            await factory.CreateContainerEnvironmentAsync(scope));

        // Force /cache/pnpm and /cache/npm to be read-only inside this test container so fallback probing triggers.
        executor.Options.PnpmCachePath = null;
        executor.Options.NpmCachePath = null;
        var roPnpmHost = _useWsl ? NightCodeForge.Infrastructure.Git.WslPathConverter.ToWslPath(roPnpmDir) : roPnpmDir;
        var roNpmHost = _useWsl ? NightCodeForge.Infrastructure.Git.WslPathConverter.ToWslPath(roNpmDir) : roNpmDir;
        executor.Options.AdditionalVolumeMounts.Add(new ContainerVolumeMount(roPnpmHost, ContainerExecutorOptions.ContainerPnpmStorePath, isReadOnly: true));
        executor.Options.AdditionalVolumeMounts.Add(new ContainerVolumeMount(roNpmHost, ContainerExecutorOptions.ContainerNpmCachePath, isReadOnly: true));
        executor.EnsureContainerRunning();

        // 1. Unbalanced backtick inside double quotes (exact Qwen / ncf-cr-csharp-003 command) must fail fast without hanging.
        var badBacktickCmd = "csnav decompile --type \"System.Collections.ObjectModel.ReadOnlyDictionary`2\" --format text 2>&1 | head -15";
        var badBacktickResult = await executor.ExecuteAsync(badBacktickCmd, timeoutSeconds: 15);
        Assert.NotEqual(0, badBacktickResult.ExitCode);
        Assert.NotEqual(NightCodeForgeContainerExecutor.TimeoutExitCode, badBacktickResult.ExitCode);
        Assert.True(badBacktickResult.Duration < TimeSpan.FromSeconds(10), $"Expected fast syntax failure, took {badBacktickResult.Duration}");

        // 2. Persistent shell must remain completely healthy after the syntax error.
        var unclosedQuoteResult = await executor.ExecuteAsync("echo 'unclosed", timeoutSeconds: 15);
        Assert.NotEqual(0, unclosedQuoteResult.ExitCode);
        Assert.NotEqual(NightCodeForgeContainerExecutor.TimeoutExitCode, unclosedQuoteResult.ExitCode);

        var aliveResult = await executor.ExecuteAsync("echo still-alive", timeoutSeconds: 15);
        Assert.Equal(0, aliveResult.ExitCode);
        Assert.Contains("still-alive", aliveResult.StandardOutput);

        // 3. Stdin-reading command must return immediately without blocking, and cd must stay subshell-isolated.
        var catResult = await executor.ExecuteAsync("cat", timeoutSeconds: 15);
        Assert.Equal(0, catResult.ExitCode);
        Assert.Equal(string.Empty, catResult.StandardOutput.Trim());

        var cdResult = await executor.ExecuteAsync("cd /tmp && pwd", timeoutSeconds: 15);
        Assert.Equal(0, cdResult.ExitCode);
        Assert.Contains("/tmp", cdResult.StandardOutput);

        var pwdResult = await executor.ExecuteAsync("pwd", timeoutSeconds: 15);
        Assert.Equal(0, pwdResult.ExitCode);
        Assert.Equal("/workspace", pwdResult.StandardOutput.Trim());

        // 4. DOTNET_ROLL_FORWARD and writable fallback cache directories must be active.
        var envCheck = await executor.ExecuteAsync(
            "echo \"RF=$DOTNET_ROLL_FORWARD\" && echo \"PNPM=$npm_config_store_dir\" && echo \"NPM=$npm_config_cache\" && test -w \"$npm_config_store_dir\" && test -w \"$npm_config_cache\"",
            timeoutSeconds: 15);
        Assert.True(envCheck.ExitCode == 0, $"Environment parity check failed:\n{envCheck.StandardOutput}\n{envCheck.StandardError}");
        Assert.Contains("RF=LatestMajor", envCheck.StandardOutput);
        Assert.Contains("PNPM=/tmp/ncf-pnpm-store", envCheck.StandardOutput);
        Assert.Contains("NPM=/tmp/ncf-npm-cache", envCheck.StandardOutput);

        // 4b. Large >64 KB command executes cleanly via /dev/fd/3 heredoc path without hitting argv limits.
        var largePayload = new string('A', 70_000);
        var largeCmdResult = await executor.ExecuteAsync($"printf '%s' '{largePayload}' | wc -c", timeoutSeconds: 15);
        Assert.True(largeCmdResult.ExitCode == 0, $"Large heredoc command failed ({largeCmdResult.ExitCode}): {largeCmdResult.StandardOutput}");
        Assert.Equal("70000", largeCmdResult.StandardOutput.Trim());

        // 5. Properly single-quoted csnav decompile returns cleanly when csnav is installed on host dotnet tools.
        var whichCsnav = await executor.ExecuteAsync("command -v csnav", timeoutSeconds: 15);
        if (whichCsnav.ExitCode == 0)
        {
            var csnavCmd = "set -o pipefail; csnav decompile --type 'System.Collections.ObjectModel.ReadOnlyDictionary`2' --format text 2>&1 | head -15";
            var csnavRes = await executor.ExecuteAsync(csnavCmd, timeoutSeconds: 120);
            Assert.True(csnavRes.ExitCode == 0, $"csnav decompile failed ({csnavRes.ExitCode}):\n{csnavRes.StandardOutput}\n{csnavRes.StandardError}");
            Assert.Contains("ReadOnlyDictionary", csnavRes.StandardOutput);
        }
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
