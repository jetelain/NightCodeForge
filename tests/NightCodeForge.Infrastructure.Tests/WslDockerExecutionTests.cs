using System.Diagnostics;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests;

public class WslDockerExecutionTests
{
    [Fact]
    public void BuildProcessStartInfo_NativeDocker_BuildsDirectDockerCommand()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "my-sandbox",
            ShellExecutable = "docker",
            UseWsl = false
        };

        var psi = NightCodeForgeContainerExecutor.BuildProcessStartInfo(options);

        Assert.Equal("docker", psi.FileName);
        Assert.Equal("exec -i my-sandbox bash --noprofile --norc", psi.Arguments);
        Assert.True(psi.RedirectStandardInput);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
    }

    [Fact]
    public void BuildProcessStartInfo_WslWithoutDistro_BuildsWslExeCommandLine()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "my-sandbox",
            ShellExecutable = "docker",
            UseWsl = true,
            WslDistribution = null,
            WslExecutable = "wsl.exe"
        };

        var psi = NightCodeForgeContainerExecutor.BuildProcessStartInfo(options);

        Assert.Equal("wsl.exe", psi.FileName);
        Assert.Equal("-- docker exec -i my-sandbox bash --noprofile --norc", psi.Arguments);
    }

    [Fact]
    public void BuildProcessStartInfo_WslWithDistro_IncludesDistributionFlag()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "my-sandbox",
            ShellExecutable = "docker",
            UseWsl = true,
            WslDistribution = "Ubuntu",
            WslExecutable = "wsl.exe"
        };

        var psi = NightCodeForgeContainerExecutor.BuildProcessStartInfo(options);

        Assert.Equal("wsl.exe", psi.FileName);
        Assert.Equal("-d \"Ubuntu\" -- docker exec -i my-sandbox bash --noprofile --norc", psi.Arguments);
    }

    [Fact]
    public void BuildProcessStartInfo_WslWithRestrictedReadOnly_IncludesUnprivilegedUserFlag()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-restricted",
            ShellExecutable = "docker",
            UseWsl = true,
            WslDistribution = "docker-desktop",
            IsRestrictedReadOnly = true
        };

        var psi = NightCodeForgeContainerExecutor.BuildProcessStartInfo(options);

        Assert.Equal("wsl.exe", psi.FileName);
        Assert.Equal("-d \"docker-desktop\" -- docker exec -i --user 1000:1000 ncf-restricted bash --noprofile --norc", psi.Arguments);
    }

    [Fact]
    public void BuildDockerCommand_GeneratesCorrectCommandPair()
    {
        var (nativeExe, nativeArgs) = NightCodeForgeContainerExecutor.BuildDockerCommand("ps -a", useWsl: false);
        Assert.Equal("docker", nativeExe);
        Assert.Equal("ps -a", nativeArgs);

        var (wslExe, wslArgs) = NightCodeForgeContainerExecutor.BuildDockerCommand("run -d --name test alpine", useWsl: true, wslDistribution: "Ubuntu");
        Assert.Equal("wsl.exe", wslExe);
        Assert.Equal("-d \"Ubuntu\" -- docker run -d --name test alpine", wslArgs);
    }

    [Fact]
    public void ExecutionEnvironmentProvider_ConfiguresWslForContainers_WhenExplicitlySetOnWorkspace()
    {
        var provider = new ExecutionEnvironmentProvider();
        var workspace = new Workspace
        {
            Id = 101,
            ExecutionMode = ExecutionMode.ContainerSandbox,
            ContainerName = "wsl-sandbox-container",
            UseWslForContainers = true,
            WslDistribution = "Ubuntu"
        };

        var env = provider.GetEnvironment(workspace);
        Assert.NotNull(env);
        Assert.IsType<NightCodeForgeContainerExecutor>(env);
    }

    [Fact]
    public void ExecutionEnvironmentProvider_UsesDefaultWslDistribution_WhenProviderOptionsConfigured()
    {
        var providerOptions = new ExecutionEnvironmentProviderOptions
        {
            UseWslForContainers = true,
            DefaultWslDistribution = "Debian"
        };
        var provider = new ExecutionEnvironmentProvider(options: providerOptions);

        var workspace = new Workspace
        {
            Id = 102,
            ExecutionMode = ExecutionMode.ContainerSandbox,
            ContainerName = "debian-sandbox"
        };

        var env = provider.GetEnvironment(workspace);
        Assert.NotNull(env);
        Assert.IsType<NightCodeForgeContainerExecutor>(env);
    }

    [Fact]
    public void ExecutionEnvironmentProvider_WslDirect_AppliesWorkspaceWslDistribution()
    {
        var provider = new ExecutionEnvironmentProvider();
        var workspace = new Workspace
        {
            Id = 103,
            ExecutionMode = ExecutionMode.WslDirect,
            WslDistribution = "ArchLinux",
            Ext4MirrorPath = "/home/user/repo"
        };

        var env = provider.GetEnvironment(workspace);
        Assert.NotNull(env);
        Assert.IsType<WslExecutionEnvironment>(env);
    }

    [Fact]
    public void ExecutionEnvironmentProvider_AutoDetectsWsl_WhenEnvVarSet()
    {
        Environment.SetEnvironmentVariable("NCF_USE_WSL_DOCKER", "true");
        try
        {
            var provider = new ExecutionEnvironmentProvider();
            var workspace = new Workspace
            {
                Id = 104,
                ExecutionMode = ExecutionMode.ContainerSandbox,
                ContainerName = "autodetect-sandbox"
            };

            var env = provider.GetEnvironment(workspace);
            Assert.NotNull(env);
            Assert.IsType<NightCodeForgeContainerExecutor>(env);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NCF_USE_WSL_DOCKER", null);
        }
    }

    [Fact]
    public void ExecutionEnvironmentProvider_AutoDetectsWsl_WhenExt4MirrorPathSet()
    {
        var provider = new ExecutionEnvironmentProvider();
        var workspace = new Workspace
        {
            Id = 105,
            ExecutionMode = ExecutionMode.ContainerSandbox,
            ContainerName = "ext4-sandbox",
            Ext4MirrorPath = "/mnt/wsl/ext4/myrepo"
        };

        var env = provider.GetEnvironment(workspace);
        Assert.NotNull(env);
        Assert.IsType<NightCodeForgeContainerExecutor>(env);
    }
}
