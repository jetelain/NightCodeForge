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
}
