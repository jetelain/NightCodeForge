using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Tests.Fixtures;
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
        Assert.Equal("-d Ubuntu -- docker exec -i my-sandbox bash --noprofile --norc", psi.Arguments);
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
        Assert.Equal("-d docker-desktop -- docker exec -i --user 1000:1000 ncf-restricted bash --noprofile --norc", psi.Arguments);
    }

    [Fact]
    public void BuildProcessStartSpec_NativeDocker_BuildsArgumentList()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "my-sandbox",
            ShellExecutable = "docker",
            UseWsl = false
        };

        var spec = NightCodeForgeContainerExecutor.BuildProcessStartSpec(options);

        Assert.Equal("docker", spec.FileName);
        Assert.Equal(new[] { "exec", "-i", "my-sandbox", "bash", "--noprofile", "--norc" }, spec.ArgumentList);
    }

    [Fact]
    public void BuildProcessStartSpec_WslWithDistroAndRestricted_BuildsArgumentList()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-restricted",
            ShellExecutable = "docker",
            UseWsl = true,
            WslDistribution = "Ubuntu-24.04",
            IsRestrictedReadOnly = true
        };

        var spec = NightCodeForgeContainerExecutor.BuildProcessStartSpec(options);

        Assert.Equal("wsl.exe", spec.FileName);
        Assert.Equal(new[] { "-d", "Ubuntu-24.04", "--", "docker", "exec", "-i", "--user", "1000:1000", "ncf-restricted", "bash", "--noprofile", "--norc" }, spec.ArgumentList);
    }

    [Fact]
    public void BuildDockerProcessSpec_NativeDocker_BuildsArgumentList()
    {
        var options = new ContainerExecutorOptions
        {
            ShellExecutable = "docker",
            UseWsl = false
        };

        var spec = NightCodeForgeContainerExecutor.BuildDockerProcessSpec(options, ["inspect", "-f", "{{.State.Running}}", "ncf-sandbox"]);

        Assert.Equal("docker", spec.FileName);
        Assert.Equal(new[] { "inspect", "-f", "{{.State.Running}}", "ncf-sandbox" }, spec.ArgumentList);
    }

    [Fact]
    public void BuildDockerProcessSpec_WslWithDistro_BuildsCorrectWslArgumentListWithoutQuotes()
    {
        var options = new ContainerExecutorOptions
        {
            ShellExecutable = "docker",
            UseWsl = true,
            WslDistribution = "Ubuntu",
            WslExecutable = "wsl.exe"
        };

        var spec = NightCodeForgeContainerExecutor.BuildDockerProcessSpec(options, ["start", "ncf-sandbox-1"]);

        Assert.Equal("wsl.exe", spec.FileName);
        Assert.Equal(new[] { "-d", "Ubuntu", "--", "docker", "start", "ncf-sandbox-1" }, spec.ArgumentList);
    }

    [Fact]
    public void BuildDockerCommand_GeneratesCorrectCommandPair()
    {
        var (nativeExe, nativeArgs) = NightCodeForgeContainerExecutor.BuildDockerCommand("ps -a", useWsl: false);
        Assert.Equal("docker", nativeExe);
        Assert.Equal("ps -a", nativeArgs);

        var (wslExe, wslArgs) = NightCodeForgeContainerExecutor.BuildDockerCommand("run -d --name test alpine", useWsl: true, wslDistribution: "Ubuntu");
        Assert.Equal("wsl.exe", wslExe);
        Assert.Equal("-d Ubuntu -- docker run -d --name test alpine", wslArgs);
    }

    [Fact]
    public void ExecutionEnvironmentProvider_ConfiguresWslForContainers_WhenExplicitlySetOnWorkspace()
    {
        var provider = new TestExecutionEnvironmentProviderBuilder().Build();
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
        var provider = new TestExecutionEnvironmentProviderBuilder { Options = Microsoft.Extensions.Options.Options.Create(providerOptions) }.Build();

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
        var provider = new TestExecutionEnvironmentProviderBuilder().Build();
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
            var provider = new TestExecutionEnvironmentProviderBuilder().Build();
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
        var provider = new TestExecutionEnvironmentProviderBuilder().Build();
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

    [Fact]
    public void BuildVolumeMounts_IncludesWorkspaceSdkAndPackageCacheMounts()
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/home/developer/.nightcodeforge/wks/42",
            ContainerWorkspacePath = "/workspace",
            MountPackageCaches = true,
            MountHostSdks = true
        };

        var mounts = NightCodeForgeContainerExecutor.BuildVolumeMounts(options);

        // Workspace
        Assert.Contains(mounts, m => m.HostPath == "/home/developer/.nightcodeforge/wks/42" && m.ContainerPath == "/workspace" && !m.IsReadOnly);

        // Layered clone protection
        Assert.Contains(mounts, m => m.HostPath == "/home/developer/.nightcodeforge/wks/42/clone" && m.ContainerPath == "/workspace/clone" && m.IsReadOnly);
        Assert.Contains(mounts, m => m.HostPath == "/home/developer/.nightcodeforge/wks/42/clone/.git" && m.ContainerPath == "/workspace/clone/.git" && !m.IsReadOnly);

        // Package caches (NuGet, npm, pnpm, uv, Maven)
        Assert.Contains(mounts, m => m.HostPath == "~/.nuget/packages" && m.ContainerPath == "/root/.nuget/packages" && !m.IsReadOnly);
        Assert.Contains(mounts, m => m.HostPath == "~/.npm" && m.ContainerPath == "/root/.npm" && !m.IsReadOnly);
        Assert.Contains(mounts, m => m.HostPath == "~/.local/share/pnpm/store" && m.ContainerPath == "/root/.local/share/pnpm/store" && !m.IsReadOnly);
        Assert.Contains(mounts, m => m.HostPath == "~/.cache/uv" && m.ContainerPath == "/root/.cache/uv" && !m.IsReadOnly);
        Assert.Contains(mounts, m => m.HostPath == "~/.m2/repository" && m.ContainerPath == "/root/.m2/repository" && !m.IsReadOnly);

        // SDK toolchains (read-only)
        Assert.Contains(mounts, m => m.HostPath == "/usr/share/dotnet" && m.ContainerPath == "/usr/share/dotnet" && m.IsReadOnly);
        Assert.Contains(mounts, m => m.HostPath == "/usr/lib/jvm" && m.ContainerPath == "/usr/lib/jvm" && m.IsReadOnly);
        Assert.Contains(mounts, m => m.HostPath == "/opt/android-sdk" && m.ContainerPath == "/opt/android-sdk" && m.IsReadOnly);
    }

    [Fact]
    public void BuildVolumeMounts_WhenCanonicalClonePathIsInsideHostWorkspacePath_DoesNotMountDuplicateClone()
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/home/developer/.nightcodeforge/wks/42",
            CanonicalClonePath = "/home/developer/.nightcodeforge/wks/42/clone",
            ContainerWorkspacePath = "/workspace"
        };

        var mounts = NightCodeForgeContainerExecutor.BuildVolumeMounts(options);

        // Workspace is mounted
        Assert.Contains(mounts, m => m.ContainerPath == "/workspace");

        // Duplicate /clone mount is NOT added
        Assert.DoesNotContain(mounts, m => m.ContainerPath == "/clone");
    }

    [Fact]
    public void BuildVolumeMounts_RestrictedReadOnly_MarksWorkspaceMountReadOnly()
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/home/developer/.nightcodeforge/wks/99",
            ContainerWorkspacePath = "/workspace",
            IsRestrictedReadOnly = true
        };

        var mounts = NightCodeForgeContainerExecutor.BuildVolumeMounts(options);
        var workspaceMount = Assert.Single(mounts, m => m.ContainerPath == "/workspace");
        Assert.True(workspaceMount.IsReadOnly);
        Assert.Equal("/home/developer/.nightcodeforge/wks/99:/workspace:ro", workspaceMount.ToString());
    }

    [Fact]
    public void BuildVolumeMounts_ExpandTildeInMounts_ExpandsWslUserHome()
    {
        var options = new ContainerExecutorOptions
        {
            UseWsl = true,
            WslUserName = "testuser",
            ExpandTildeInMounts = true,
            HostWorkspacePath = "~/.nightcodeforge/wks/5"
        };

        var mounts = NightCodeForgeContainerExecutor.BuildVolumeMounts(options);

        var wksMount = Assert.Single(mounts, m => m.ContainerPath == "/workspace");
        Assert.Equal("/home/testuser/.nightcodeforge/wks/5", wksMount.HostPath);

        var nugetMount = Assert.Single(mounts, m => m.ContainerPath == "/root/.nuget/packages");
        Assert.Equal("/home/testuser/.nuget/packages", nugetMount.HostPath);
    }

    [Fact]
    public void BuildVolumeMounts_WithIsolatedTaskWorktree_MountsWorktreeToWorkspaceAndCloneSeparately()
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/home/developer/.nightcodeforge/wks/42/task-7",
            CanonicalClonePath = "/home/developer/.nightcodeforge/wks/42/clone",
            ContainerWorkspacePath = "/workspace",
            MountPackageCaches = false,
            MountHostSdks = false
        };

        var mounts = NightCodeForgeContainerExecutor.BuildVolumeMounts(options);

        // Active task worktree mounted at /workspace
        var wksMount = Assert.Single(mounts, m => m.ContainerPath == "/workspace");
        Assert.Equal("/home/developer/.nightcodeforge/wks/42/task-7", wksMount.HostPath);
        Assert.False(wksMount.IsReadOnly);

        // Canonical clone mounted at /clone as read-only
        var cloneMount = Assert.Single(mounts, m => m.ContainerPath == "/clone");
        Assert.Equal("/home/developer/.nightcodeforge/wks/42/clone", cloneMount.HostPath);
        Assert.True(cloneMount.IsReadOnly);

        // Canonical clone .git mounted at /clone/.git as read-write
        var gitMount = Assert.Single(mounts, m => m.ContainerPath == "/clone/.git");
        Assert.Equal("/home/developer/.nightcodeforge/wks/42/clone/.git", gitMount.HostPath);
        Assert.False(gitMount.IsReadOnly);
    }

    [Fact]
    public void BuildVolumeMounts_WithIsolatedTaskWorktree_RestrictedReadOnly_MountsGitReadOnly()
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/home/developer/.nightcodeforge/wks/42/task-7",
            CanonicalClonePath = "/home/developer/.nightcodeforge/wks/42/clone",
            ContainerWorkspacePath = "/workspace",
            IsRestrictedReadOnly = true,
            MountPackageCaches = false,
            MountHostSdks = false
        };

        var mounts = NightCodeForgeContainerExecutor.BuildVolumeMounts(options);

        var wksMount = Assert.Single(mounts, m => m.ContainerPath == "/workspace");
        Assert.True(wksMount.IsReadOnly);

        var cloneMount = Assert.Single(mounts, m => m.ContainerPath == "/clone");
        Assert.True(cloneMount.IsReadOnly);

        var gitMount = Assert.Single(mounts, m => m.ContainerPath == "/clone/.git");
        Assert.True(gitMount.IsReadOnly);
    }

    [Fact]
    public void BuildDockerRunArgs_BuildsCompleteCommandWithMounts()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-sandbox-123",
            ContainerImage = "azurelinux/base/core:3.0",
            HostWorkspacePath = "/home/dev/.nightcodeforge/wks/123",
            ContainerWorkspacePath = "/workspace",
            DefaultWorkingDirectory = "/workspace",
            MountPackageCaches = true,
            MountHostSdks = true
        };

        var args = NightCodeForgeContainerExecutor.BuildDockerRunArgs(options);

        Assert.Equal("run", args[0]);
        Assert.Equal("-d", args[1]);
        Assert.Equal("--name", args[2]);
        Assert.Equal("ncf-sandbox-123", args[3]);

        Assert.Contains("-v", args);
        Assert.Contains("/home/dev/.nightcodeforge/wks/123:/workspace", args);
        Assert.Contains("~/.nuget/packages:/root/.nuget/packages", args);
        Assert.Contains("~/.npm:/root/.npm", args);
        Assert.Contains("/usr/share/dotnet:/usr/share/dotnet:ro", args);

        Assert.Contains("-w", args);
        Assert.Contains("/workspace", args);

        Assert.Contains("azurelinux/base/core:3.0", args);
        Assert.Contains("tail", args);
        Assert.Contains("/dev/null", args);
    }

    [Fact]
    public void BuildDockerRunArgs_RestrictedReadOnly_IncludesAirGapAndSecurityHardeningFlags()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-triage-sandbox",
            IsRestrictedReadOnly = true
        };

        var args = NightCodeForgeContainerExecutor.BuildDockerRunArgs(options);

        Assert.Contains("--network", args);
        var netIdx = args.IndexOf("--network");
        Assert.Equal("none", args[netIdx + 1]);

        Assert.Contains("--read-only", args);
        Assert.Contains("--tmpfs", args);
        Assert.Contains("/tmp:rw,noexec,nosuid,size=32m", args);
        Assert.Contains("--cap-drop=ALL", args);

        Assert.Contains("--security-opt", args);
        var secOptIdx = args.IndexOf("--security-opt");
        Assert.Equal("no-new-privileges:true", args[secOptIdx + 1]);

        Assert.Contains("--user", args);
        var userIdx = args.IndexOf("--user");
        Assert.Equal("1000:1000", args[userIdx + 1]);
    }

    [Fact]
    public void BuildDockerRunProcessSpec_WslEnabled_WrapsWithWslAndDistro()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "wsl-container",
            ShellExecutable = "docker",
            UseWsl = true,
            WslDistribution = "Ubuntu-24.04",
            WslExecutable = "wsl.exe",
            HostWorkspacePath = "/home/wsluser/.nightcodeforge/wks/1"
        };

        var spec = NightCodeForgeContainerExecutor.BuildDockerRunProcessSpec(options);

        Assert.Equal("wsl.exe", spec.FileName);
        var argList = spec.ArgumentList!.ToList();
        Assert.Equal("-d", argList[0]);
        Assert.Equal("Ubuntu-24.04", argList[1]);
        Assert.Equal("--", argList[2]);
        Assert.Equal("docker", argList[3]);
        Assert.Equal("run", argList[4]);
        Assert.Equal("-d", argList[5]);
        Assert.Equal("--name", argList[6]);
        Assert.Equal("wsl-container", argList[7]);
        Assert.Contains("/home/wsluser/.nightcodeforge/wks/1:/workspace", argList);
    }

    [Fact]
    public void ResolveHostPath_WithBackslashesOnWsl_ConvertsToForwardSlashes()
    {
        var options = new ContainerExecutorOptions
        {
            UseWsl = true,
            HostWorkspacePath = @"\home\developer\.nightcodeforge\wks\1"
        };

        var resolved = NightCodeForgeContainerExecutor.ResolveHostPath(options.HostWorkspacePath, options);

        Assert.Equal("/home/developer/.nightcodeforge/wks/1", resolved);
    }

    [Fact]
    public void ResolveContainerWorkingDirectory_HostRepoPath_ResolvesToDefaultWorkingDirectory()
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/home/developer/.nightcodeforge/wks/1",
            CanonicalClonePath = "/home/developer/.nightcodeforge/wks/1/clone",
            DefaultWorkingDirectory = "/workspace/clone",
            ContainerWorkspacePath = "/workspace"
        };

        // 1. Host repo path passed as working directory
        var resolved = NightCodeForgeContainerExecutor.ResolveContainerWorkingDirectory(@"C:\repos\myproject", options);
        Assert.Equal("/workspace/clone", resolved);

        // 2. Relative subpath under CanonicalClonePath
        var subpath = NightCodeForgeContainerExecutor.ResolveContainerWorkingDirectory("/home/developer/.nightcodeforge/wks/1/clone/src/Engine", options);
        Assert.Equal("/workspace/clone/src/Engine", subpath);

        // 3. /workspace passed directly
        var root = NightCodeForgeContainerExecutor.ResolveContainerWorkingDirectory("/workspace", options);
        Assert.Equal("/workspace/clone", root);

        // 4. Dot or dot-slash resolves to defaultWorkingDirectory
        var dot = NightCodeForgeContainerExecutor.ResolveContainerWorkingDirectory(".", options);
        Assert.Equal("/workspace/clone", dot);

        // 5. /clone resolves to defaultWorkingDirectory
        var clone = NightCodeForgeContainerExecutor.ResolveContainerWorkingDirectory("/clone", options);
        Assert.Equal("/workspace/clone", clone);

        var cloneSub = NightCodeForgeContainerExecutor.ResolveContainerWorkingDirectory("/clone/src/Engine", options);
        Assert.Equal("/workspace/clone/src/Engine", cloneSub);

        // 6. Relative path resolves under defaultWorkingDirectory
        var rel = NightCodeForgeContainerExecutor.ResolveContainerWorkingDirectory("src/Engine", options);
        Assert.Equal("/workspace/clone/src/Engine", rel);
    }
}



