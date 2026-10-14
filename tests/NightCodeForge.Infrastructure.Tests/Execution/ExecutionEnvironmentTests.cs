using System.Runtime.InteropServices;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Execution;

/// <summary>
/// Verifies Bare & Container execution environment behaviors according to docs\plan\04B_EXECUTION_ENVIRONMENT_AND_WORKFLOW_ALIGNMENT.md:
/// - Windows executes bash via WSL2 (never PowerShell for agent commands)
/// - Container sandbox mounts workspace, host SDKs, and package cache directories.
/// </summary>
public class ExecutionEnvironmentTests
{
    private class FakePathResolver : IWorkspacePathResolver
    {
        public string GetAgentWorkingClonePath(int workspaceId) => $"/home/wsluser/.nightcodeforge/wks/{workspaceId}";
        public string GetHostRemoteUrl(string repositoryPath) => $"/mnt/c/repos/{repositoryPath}";
        public string ResolveCruciblePath(int workspaceId, string relativeOrContainerPath) => $@"\\wsl.localhost\Ubuntu\home\wsluser\.nightcodeforge\wks\{workspaceId}\{relativeOrContainerPath.TrimStart('/', '\\')}";
        public string GetWslDistributionName() => "Ubuntu";
        public string GetWslUserName() => "wsluser";
    }

    [Fact]
    public void ContainerExecutor_MountsWorkspace_MappedToVirtualWorkspacePath()
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/home/wsluser/.nightcodeforge/wks/10",
            ContainerWorkspacePath = "/workspace"
        };

        var mounts = NightCodeForgeContainerExecutor.BuildVolumeMounts(options);

        var workspaceMount = Assert.Single(mounts, m => m.ContainerPath == "/workspace");
        Assert.Equal("/home/wsluser/.nightcodeforge/wks/10", workspaceMount.HostPath);
        Assert.False(workspaceMount.IsReadOnly);
    }

    [Fact]
    public void ContainerExecutor_MountsWorkspace_ReadOnlyWhenRestricted()
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/home/wsluser/.nightcodeforge/wks/10",
            ContainerWorkspacePath = "/workspace",
            IsRestrictedReadOnly = true
        };

        var mounts = NightCodeForgeContainerExecutor.BuildVolumeMounts(options);

        var workspaceMount = Assert.Single(mounts, m => m.ContainerPath == "/workspace");
        Assert.True(workspaceMount.IsReadOnly);
        Assert.EndsWith(":ro", workspaceMount.ToString());
    }

    [Fact]
    public void ContainerExecutor_MountsPackageCaches_NuGetNpmUvMaven()
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/home/wsluser/.nightcodeforge/wks/1",
            MountPackageCaches = true
        };

        var mounts = NightCodeForgeContainerExecutor.BuildVolumeMounts(options);

        Assert.Contains(mounts, m => m.ContainerPath == "/root/.nuget/packages" && !m.IsReadOnly);
        Assert.Contains(mounts, m => m.ContainerPath == "/root/.npm" && !m.IsReadOnly);
        Assert.Contains(mounts, m => m.ContainerPath == "/root/.local/share/pnpm/store" && !m.IsReadOnly);
        Assert.Contains(mounts, m => m.ContainerPath == "/root/.cache/uv" && !m.IsReadOnly);
        Assert.Contains(mounts, m => m.ContainerPath == "/root/.m2/repository" && !m.IsReadOnly);
    }

    [Fact]
    public void ContainerExecutor_MountsToolchainSdks_ReadOnly()
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/home/wsluser/.nightcodeforge/wks/1",
            MountHostSdks = true
        };

        var mounts = NightCodeForgeContainerExecutor.BuildVolumeMounts(options);

        var dotnetMount = Assert.Single(mounts, m => m.ContainerPath == "/usr/share/dotnet");
        Assert.True(dotnetMount.IsReadOnly);

        var jvmMount = Assert.Single(mounts, m => m.ContainerPath == "/usr/lib/jvm");
        Assert.True(jvmMount.IsReadOnly);

        var androidMount = Assert.Single(mounts, m => m.ContainerPath == "/opt/android-sdk");
        Assert.True(androidMount.IsReadOnly);
    }

    [Fact]
    public void ContainerExecutor_BuildDockerRunArgs_IncludesAllMountsAndWorkdir()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-sandbox-test",
            HostWorkspacePath = "/home/wsluser/.nightcodeforge/wks/7",
            MountPackageCaches = true,
            MountHostSdks = true,
            DefaultWorkingDirectory = "/workspace"
        };

        var args = NightCodeForgeContainerExecutor.BuildDockerRunArgs(options);

        Assert.Equal("run", args[0]);
        Assert.Equal("-d", args[1]);
        Assert.Contains("/home/wsluser/.nightcodeforge/wks/7:/workspace", args);
        Assert.Contains("~/.nuget/packages:/root/.nuget/packages", args);
        Assert.Contains("/usr/share/dotnet:/usr/share/dotnet:ro", args);
        Assert.Contains("-w", args);
        Assert.Contains("/workspace", args);
    }

    [Fact]
    public void ExecutionEnvironmentProvider_ContainerMode_InjectsResolvedWorkspaceAndCacheMounts()
    {
        var pathResolver = new FakePathResolver();
        var provider = new ExecutionEnvironmentProvider(pathResolver: pathResolver);

        var workspace = new Workspace
        {
            Id = 42,
            ExecutionMode = ExecutionMode.ContainerSandbox,
            ContainerName = "my-test-container"
        };

        var env = provider.GetEnvironment(workspace);
        var containerExecutor = Assert.IsType<NightCodeForgeContainerExecutor>(env);

        Assert.Equal("/home/wsluser/.nightcodeforge/wks/42", containerExecutor.Options.HostWorkspacePath);
        Assert.Equal("/workspace", containerExecutor.Options.ContainerWorkspacePath);
        Assert.Equal("ubuntu:24.04", containerExecutor.Options.ContainerImage);
        Assert.True(containerExecutor.Options.MountPackageCaches);
        Assert.True(containerExecutor.Options.MountHostSdks);
        Assert.Equal("wsluser", containerExecutor.Options.WslUserName);
    }

    [Fact]
    public void ContainerExecutor_DefaultsToUbuntuImage()
    {
        var options = new ContainerExecutorOptions();
        Assert.Equal("ubuntu:24.04", options.ContainerImage);

        var args = NightCodeForgeContainerExecutor.BuildDockerRunArgs(options);
        Assert.Contains("ubuntu:24.04", args);
    }

    [Fact]
    public void ContainerExecutor_ComputesPathEnvironmentVariable_WithSdksAndStandardPaths()
    {
        var options = new ContainerExecutorOptions
        {
            MountHostSdks = true,
            ExtraPathDirectories = new List<string> { "/custom/tool/bin" }
        };

        var path = NightCodeForgeContainerExecutor.ComputePathEnvironmentVariable(options);

        // Prepend custom tools
        Assert.StartsWith("/custom/tool/bin:", path);

        // Contains .NET, cargo, local/bin, standard paths
        Assert.Contains("/usr/share/dotnet", path);
        Assert.Contains("/root/.dotnet/tools", path);
        Assert.Contains("/root/.local/bin", path);
        Assert.Contains("/root/.cargo/bin", path);
        Assert.Contains("/usr/local/bin", path);
        Assert.Contains("/usr/bin", path);
        Assert.Contains("/bin", path);
    }

    [Fact]
    public void ContainerExecutor_BuildDockerRunArgs_PassesComputedPathAndUbuntuImage()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ubuntu-sandbox"
        };

        var args = NightCodeForgeContainerExecutor.BuildDockerRunArgs(options);

        Assert.Contains("-e", args);
        var pathArg = Assert.Single(args, a => a.StartsWith("PATH="));
        Assert.Contains("/usr/share/dotnet", pathArg);
        Assert.Contains("/usr/bin", pathArg);
        Assert.Contains("ubuntu:24.04", args);
    }
}

