using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public sealed class ContainerDotnetToolsMountToggleTests
{
    [Fact]
    public void BuildVolumeMounts_And_ComputePath_HonorMountHostDotnetToolsFlag()
    {
        var baseMounts = new List<ContainerVolumeMount>
        {
            new("/home/user/.dotnet/tools", ContainerExecutorOptions.HostDotnetToolsContainerPath, isReadOnly: true)
        };

        var enabledOptions = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/tmp/bench-wks",
            MountHostDotnetTools = true,
            MountPackageCaches = false,
            MountHostSdks = false,
            AdditionalVolumeMounts = baseMounts
        };

        var disabledOptions = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/tmp/bench-wks",
            MountHostDotnetTools = false,
            MountPackageCaches = false,
            MountHostSdks = false,
            AdditionalVolumeMounts = baseMounts
        };

        var enabledMounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(enabledOptions);
        var disabledMounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(disabledOptions);

        Assert.Contains(enabledMounts, m => m.ContainerPath == ContainerExecutorOptions.HostDotnetToolsContainerPath);
        Assert.DoesNotContain(disabledMounts, m => m.ContainerPath == ContainerExecutorOptions.HostDotnetToolsContainerPath);

        var enabledPath = ContainerMountAndDriftBuilder.ComputePathEnvironmentVariable(enabledOptions);
        var disabledPath = ContainerMountAndDriftBuilder.ComputePathEnvironmentVariable(disabledOptions);

        Assert.Contains(ContainerExecutorOptions.HostDotnetToolsContainerPath, enabledPath.Split(':'));
        Assert.DoesNotContain(ContainerExecutorOptions.HostDotnetToolsContainerPath, disabledPath.Split(':'));
    }

    [Fact]
    public void BuildVolumeMounts_ComputePath_And_DockerArgs_StripSdkDiscoveredToolsAndMaskUserDotnetToolsWhenDisabled()
    {
        var disabledOptions = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/tmp/bench-wks",
            MountHostDotnetTools = false,
            MountPackageCaches = false,
            MountHostSdks = true,
            SdkMountPaths = ["/home/user/.dotnet", "/home/user/.dotnet/tools", "/usr/share/dotnet"],
            AdditionalVolumeMounts =
            [
                new("/home/user/.dotnet/tools", ContainerExecutorOptions.HostDotnetToolsContainerPath, isReadOnly: true)
            ]
        };

        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(disabledOptions);
        Assert.Contains(mounts, m => m.ContainerPath == $"{ContainerExecutorOptions.ContainerHomePath}/.dotnet");
        Assert.DoesNotContain(mounts, m => m.ContainerPath.Contains(".dotnet/tools", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mounts, m => m.ContainerPath == ContainerExecutorOptions.HostDotnetToolsContainerPath);

        var pathEntries = ContainerMountAndDriftBuilder.ComputePathEnvironmentVariable(disabledOptions).Split(':');
        Assert.Contains($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet", pathEntries);
        Assert.DoesNotContain(ContainerExecutorOptions.HostDotnetToolsContainerPath, pathEntries);
        Assert.DoesNotContain($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools", pathEntries);
        Assert.DoesNotContain("/root/.dotnet/tools", pathEntries);

        var dockerArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(disabledOptions);
        var tmpfsIdx = dockerArgs.IndexOf("--tmpfs");
        Assert.True(tmpfsIdx >= 0);
        Assert.Contains($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools:ro", dockerArgs[tmpfsIdx + 1]);
    }

    [Fact]
    public void BuildDockerRunArgs_SupportsDisableNetworkAndDeduplicatedBlockedHosts()
    {
        var blockedOptions = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/tmp/bench-wks",
            MountPackageCaches = false,
            MountHostSdks = false,
            DisableNetwork = false,
            BlockedHosts = ["github.com", "api.github.com", "GITHUB.COM", "   "]
        };

        var blockedArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(blockedOptions);
        Assert.DoesNotContain("--network", blockedArgs);
        Assert.Equal(2, blockedArgs.Count(a => a == "--add-host"));
        Assert.Contains("github.com:127.0.0.1", blockedArgs);
        Assert.Contains("api.github.com:127.0.0.1", blockedArgs);

        var offlineOptions = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/tmp/bench-wks",
            MountPackageCaches = false,
            MountHostSdks = false,
            DisableNetwork = true,
            IsRestrictedReadOnly = false,
            BlockedHosts = ["github.com"]
        };

        var offlineArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(offlineOptions);
        var netIdx = offlineArgs.IndexOf("--network");
        Assert.True(netIdx >= 0);
        Assert.Equal("none", offlineArgs[netIdx + 1]);
        Assert.DoesNotContain("--read-only", offlineArgs);
        Assert.DoesNotContain("--add-host", offlineArgs);
    }

    [Fact]
    public void ComputePathAndBuildDockerRunArgs_IncludeContainerCargoBinAndRustupHomeWithoutLeakingRustupToPath()
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/tmp/bench-wks",
            MountPackageCaches = false,
            MountHostSdks = true,
            SdkMountPaths = ["/usr/share/dotnet", "/home/user/.cargo", "/home/user/.rustup"]
        };

        var pathEntries = ContainerMountAndDriftBuilder.ComputePathEnvironmentVariable(options).Split(':');
        Assert.Contains($"{ContainerExecutorOptions.ContainerHomePath}/.cargo/bin", pathEntries);
        Assert.Contains("/home/user/.cargo/bin", pathEntries);
        Assert.DoesNotContain("/home/user/.rustup", pathEntries);

        var dockerArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(options);
        Assert.Contains("RUSTUP_HOME=/home/user/.rustup", dockerArgs);

        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);
        Assert.Contains(mounts, m => m.ContainerPath == "/home/user/.cargo" && m.IsReadOnly);
        Assert.Contains(mounts, m => m.ContainerPath == "/home/user/.rustup" && m.IsReadOnly);
    }

    [Fact]
    public void ComputePathAndBuildDockerRunArgs_HandleCustomRustupDirectoryWithoutLeakingToPath()
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/tmp/bench-wks",
            MountPackageCaches = false,
            MountHostSdks = true,
            SdkMountPaths = ["/usr/share/dotnet", "/usr/local/cargo", "/usr/local/rustup"]
        };

        var pathEntries = ContainerMountAndDriftBuilder.ComputePathEnvironmentVariable(options).Split(':');
        Assert.Contains("/usr/local/cargo/bin", pathEntries);
        Assert.DoesNotContain("/usr/local/rustup", pathEntries);

        var dockerArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(options);
        Assert.Contains("RUSTUP_HOME=/usr/local/rustup", dockerArgs);
    }

    [Fact]
    public async Task PersistentContainerShellSession_WriteEnvironmentPreambleAsync_ConfiguresCargoPathAndQuotedRustupHome()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-preamble-test",
            MountPackageCaches = false,
            MountHostSdks = true,
            SdkMountPaths = ["/usr/share/dotnet", "/opt/my's/.rustup"]
        };

        var runnerMock = new Mock<IProcessRunner>();
        var telemetryMock = new Mock<IOperationsTelemetryService>();

        async Task<string> CapturePreambleAsync(IReadOnlyDictionary<string, string>? envVars)
        {
            using var stdinBuffer = new MemoryStream();
            using var stdinWriter = new StreamWriter(stdinBuffer, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var stdoutStream = new MemoryStream();
            using var stdoutReader = new StreamReader(stdoutStream);

            var streamSession = new Mock<IProcessStreamSession>();
            streamSession.SetupGet(s => s.StandardInput).Returns(stdinWriter);
            streamSession.SetupGet(s => s.StandardOutput).Returns(stdoutReader);
            streamSession.SetupGet(s => s.HasExited).Returns(false);

            runnerMock.Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>())).Returns(streamSession.Object);

            using var session = new PersistentContainerShellSession(options, runnerMock.Object, telemetryMock.Object, NullLogger.Instance);
            session.Start();
            await session.WriteEnvironmentPreambleAsync(envVars);
            await stdinWriter.FlushAsync();

            stdinBuffer.Position = 0;
            using var reader = new StreamReader(stdinBuffer, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        var defaultPreamble = await CapturePreambleAsync(null);
        Assert.Contains("if [ -d \"$HOME/.cargo/bin\" ]", defaultPreamble);
        Assert.Contains("export RUSTUP_HOME='/opt/my'\\''s/.rustup'", defaultPreamble);

        var customEnvPreamble = await CapturePreambleAsync(new Dictionary<string, string>
        {
            ["PATH"] = "/custom/bin",
            ["RUSTUP_HOME"] = "/custom/rustup"
        });
        Assert.DoesNotContain("if [ -d \"$HOME/.cargo/bin\" ]", customEnvPreamble);
        Assert.DoesNotContain("export RUSTUP_HOME='/opt/my'\\''s/.rustup'", customEnvPreamble);
        Assert.Contains("export RUSTUP_HOME='/custom/rustup'", customEnvPreamble);
    }
}
