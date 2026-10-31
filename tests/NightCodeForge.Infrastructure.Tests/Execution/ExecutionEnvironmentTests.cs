using System.Runtime.InteropServices;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Tests.Fixtures;
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
        public string GetAgentWorkspaceRootPath(int workspaceId) => $"/home/wsluser/.nightcodeforge/wks/{workspaceId}";
        public string GetAgentWorkingClonePath(int workspaceId) => $"/home/wsluser/.nightcodeforge/wks/{workspaceId}/clone";
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

        Assert.Contains(mounts, m => m.ContainerPath == "/cache/nuget" && !m.IsReadOnly);
        Assert.Contains(mounts, m => m.ContainerPath == "/cache/npm" && !m.IsReadOnly);
        Assert.Contains(mounts, m => m.ContainerPath == "/cache/pnpm" && !m.IsReadOnly);
        Assert.Contains(mounts, m => m.ContainerPath == "/cache/uv" && !m.IsReadOnly);
        Assert.Contains(mounts, m => m.ContainerPath == "/cache/m2" && !m.IsReadOnly);
        Assert.Contains(mounts, m => m.ContainerPath == "/cache/ms-playwright" && !m.IsReadOnly);
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

        var fontsConfigMount = Assert.Single(mounts, m => m.ContainerPath == "/etc/fonts");
        Assert.True(fontsConfigMount.IsReadOnly);

        var fontsDirMount = Assert.Single(mounts, m => m.ContainerPath == "/usr/share/fonts");
        Assert.True(fontsDirMount.IsReadOnly);
    }

    [Fact]
    public void ContainerExecutor_MountsUserDotnetToContainerHomeDotnet_ReadWrite()
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/home/gruearbre/.nightcodeforge/wks/1",
            MountHostSdks = true,
            SdkMountPaths = new List<string>
            {
                "/usr/lib/dotnet",
                "/root/.dotnet",
                "/home/gruearbre/.dotnet/tools",
                "/home/gruearbre/.dotnet"
            }
        };

        var mounts = NightCodeForgeContainerExecutor.BuildVolumeMounts(options);

        var sysDotnetMount = Assert.Single(mounts, m => m.ContainerPath == "/usr/lib/dotnet");
        Assert.True(sysDotnetMount.IsReadOnly);

        var rootDotnetMount = Assert.Single(mounts, m => m.ContainerPath == "/root/.dotnet");
        Assert.True(rootDotnetMount.IsReadOnly);

        var containerHomeDotnetMount = Assert.Single(mounts, m => m.ContainerPath == $"{ContainerExecutorOptions.ContainerHomePath}/.dotnet");
        Assert.Equal("/home/gruearbre/.dotnet", containerHomeDotnetMount.HostPath);
        Assert.False(containerHomeDotnetMount.IsReadOnly);

        Assert.DoesNotContain(mounts, m => m.ContainerPath == "/home/gruearbre/.dotnet");
        Assert.DoesNotContain(mounts, m => m.ContainerPath == "/home/gruearbre/.dotnet/tools");
    }

    [Fact]
    public void ContainerExecutor_RestrictedReadOnly_DoesNotMountUserDotnetReadWrite()
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/home/gruearbre/.nightcodeforge/wks/1",
            MountHostSdks = true,
            IsRestrictedReadOnly = true,
            SdkMountPaths = new List<string>
            {
                "/usr/lib/dotnet",
                "/home/gruearbre/.dotnet"
            }
        };

        var mounts = NightCodeForgeContainerExecutor.BuildVolumeMounts(options);

        Assert.DoesNotContain(mounts, m => m.ContainerPath == $"{ContainerExecutorOptions.ContainerHomePath}/.dotnet");
        Assert.DoesNotContain(mounts, m => m.ContainerPath == "/home/gruearbre/.dotnet");
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
        Assert.Contains("~/.nuget/packages:/cache/nuget", args);
        Assert.Contains("~/.cache/ms-playwright:/cache/ms-playwright", args);
        Assert.Contains("PLAYWRIGHT_BROWSERS_PATH=/cache/ms-playwright", args);
        Assert.Contains("/usr/share/dotnet:/usr/share/dotnet:ro", args);
        Assert.Contains("--shm-size", args);
        Assert.Contains("2g", args);
        Assert.Contains("--ulimit", args);
        Assert.Contains("core=0", args);
        Assert.Contains("-w", args);
        Assert.Contains("/workspace", args);
    }

    [Fact]
    public void ExecutionEnvironmentProvider_ContainerMode_InjectsResolvedWorkspaceAndCacheMounts()
    {
        var pathResolver = new FakePathResolver();
        var provider = new TestExecutionEnvironmentProviderBuilder { PathResolver = pathResolver }.Build();

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
        Assert.Equal(ContainerExecutorOptions.DefaultAgentBaseImage, containerExecutor.Options.ContainerImage);
        Assert.True(containerExecutor.Options.MountPackageCaches);
        Assert.True(containerExecutor.Options.MountHostSdks);
        Assert.Equal("wsluser", containerExecutor.Options.WslUserName);
        Assert.Contains(containerExecutor.Options.AdditionalVolumeMounts,
            m => m.ContainerPath == "/tmp" && m.HostPath == "/home/wsluser/.nightcodeforge/wks/42/tmp" && !m.IsReadOnly);
    }

    [Theory]
    [InlineData("ncf-agent-base:latest", ContainerExecutorOptions.DefaultAgentBaseImage)]
    [InlineData("ncf-agent-base:v1", ContainerExecutorOptions.DefaultAgentBaseImage)]
    [InlineData("ncf-agent-base:v2", ContainerExecutorOptions.DefaultAgentBaseImage)]
    [InlineData("nightcodeforge-agent:latest", ContainerExecutorOptions.DefaultAgentBaseImage)]
    [InlineData("nightcodeforge-agent:v2", ContainerExecutorOptions.DefaultAgentBaseImage)]
    [InlineData("alpine:3.19", "alpine:3.19")]
    public void ExecutionEnvironmentProvider_ContainerMode_UpgradesLegacyManagedBaseImages(string configuredImage, string expectedImage)
    {
        var pathResolver = new FakePathResolver();
        var provider = new TestExecutionEnvironmentProviderBuilder { PathResolver = pathResolver }.Build();

        var workspace = new Workspace
        {
            Id = 42,
            ExecutionMode = ExecutionMode.ContainerSandbox,
            ContainerImage = configuredImage
        };

        var env = provider.GetEnvironment(workspace);
        var containerExecutor = Assert.IsType<NightCodeForgeContainerExecutor>(env);

        Assert.Equal(expectedImage, containerExecutor.Options.ContainerImage);
    }

    [Fact]
    public void ExecutionEnvironmentProvider_RestrictedReadOnly_DoesNotBindMountHostTmp()
    {
        var pathResolver = new FakePathResolver();
        var provider = new TestExecutionEnvironmentProviderBuilder { PathResolver = pathResolver }.Build();

        var workspace = new Workspace
        {
            Id = 42,
            ExecutionMode = ExecutionMode.ContainerSandbox
        };

        var env = provider.GetEnvironment(workspace, SandboxSecurityTier.RestrictedReadOnly);
        var containerExecutor = Assert.IsType<NightCodeForgeContainerExecutor>(env);

        Assert.DoesNotContain(containerExecutor.Options.AdditionalVolumeMounts, m => m.ContainerPath == "/tmp");
    }

    [Fact]
    public void ContainerExecutor_DefaultsToNcfAgentBaseImage()
    {
        var options = new ContainerExecutorOptions();
        Assert.Equal(ContainerExecutorOptions.DefaultAgentBaseImage, options.ContainerImage);

        var args = NightCodeForgeContainerExecutor.BuildDockerRunArgs(options);
        Assert.Contains(ContainerExecutorOptions.DefaultAgentBaseImage, args);
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
    public void ContainerExecutor_ComputesPathEnvironmentVariable_WithGoNodeAndJvmSdks()
    {
        var options = new ContainerExecutorOptions
        {
            MountHostSdks = true,
            SdkMountPaths = new List<string>
            {
                "/usr/local/go",
                "/home/user/.nvm/versions/node/v20.10.0",
                "/usr/lib/jvm/java-17-openjdk-amd64",
                "/custom/tools/bin"
            }
        };

        var path = NightCodeForgeContainerExecutor.ComputePathEnvironmentVariable(options);

        Assert.Contains("/usr/local/go/bin", path);
        Assert.Contains("/home/user/.nvm/versions/node/v20.10.0/bin", path);
        Assert.Contains("/usr/lib/jvm/java-17-openjdk-amd64/bin", path);
        Assert.DoesNotContain("/usr/lib/jvm/java-17-openjdk-amd64/default-java/bin", path);
        Assert.Contains("/custom/tools/bin", path);
    }

    [Fact]
    public void ContainerExecutor_ComputesPathEnvironmentVariable_SkipsNonBinaryMountsAndDeduplicatesAndroidPaths()
    {
        var options = new ContainerExecutorOptions
        {
            MountHostSdks = true,
            SdkMountPaths = new List<string>
            {
                "/etc/ssl/certs",
                "/etc/fonts",
                "/usr/share/fonts",
                "/usr/lib/x86_64-linux-gnu",
                "/usr/lib/snapd",
                "/home/user/android-sdk",
                "/home/user/android-sdk/cmdline-tools/latest",
                "/home/user/.dotnet"
            }
        };

        var path = NightCodeForgeContainerExecutor.ComputePathEnvironmentVariable(options);
        var entries = path.Split(':');

        Assert.DoesNotContain("/etc/ssl/certs", entries);
        Assert.DoesNotContain("/etc/fonts", entries);
        Assert.DoesNotContain("/usr/share/fonts", entries);
        Assert.DoesNotContain("/usr/lib/x86_64-linux-gnu", entries);
        Assert.DoesNotContain("/usr/lib/snapd", entries);
        Assert.DoesNotContain("/home/user/android-sdk/cmdline-tools/latest/cmdline-tools/latest/bin", entries);
        Assert.Contains("/home/user/android-sdk/platform-tools", entries);
        Assert.Contains("/home/user/android-sdk/cmdline-tools/latest/bin", entries);
        Assert.DoesNotContain("/home/user/.dotnet/tools", entries);
        Assert.DoesNotContain("/home/user/.dotnet", entries);
        Assert.Contains($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools", entries);
        Assert.Contains($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet", entries);
        Assert.Contains(ContainerExecutorOptions.HostDotnetToolsContainerPath, entries);
    }

    [Fact]
    public void ContainerExecutor_WhenUserDotnetIsOnlySdk_SetsDotnetRootToContainerHomeDotnet()
    {
        var options = new ContainerExecutorOptions
        {
            MountHostSdks = true,
            SdkMountPaths = new List<string>
            {
                "/home/user/.dotnet",
                "/home/user/.dotnet/tools"
            }
        };

        var args = NightCodeForgeContainerExecutor.BuildDockerRunArgs(options);

        Assert.Contains($"DOTNET_ROOT={ContainerExecutorOptions.ContainerHomePath}/.dotnet", args);
        var pathArg = Assert.Single(args, a => a.StartsWith("PATH="));
        var entries = pathArg["PATH=".Length..].Split(':');
        Assert.Contains($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet", entries);
        Assert.Contains($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools", entries);
        Assert.DoesNotContain("/home/user/.dotnet", entries);
        Assert.DoesNotContain("/home/user/.dotnet/tools", entries);
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
        Assert.Contains(ContainerExecutorOptions.DefaultAgentBaseImage, args);
    }

    [Fact]
    public void ContainerExecutor_DefaultAgentBaseDockerfile_IncludesGitRipgrepAndPython()
    {
        var dockerfile = ContainerExecutorOptions.DefaultAgentBaseDockerfile;
        Assert.Contains("git", dockerfile);
        Assert.Contains("ripgrep", dockerfile);
        Assert.Contains("python3", dockerfile);
        Assert.Contains("python3-venv", dockerfile);
        Assert.Contains("python3-pip", dockerfile);
        Assert.Contains("build-essential", dockerfile);
        Assert.Contains("curl", dockerfile);
        Assert.Contains("jq", dockerfile);
        Assert.Contains("fontconfig", dockerfile);
        Assert.Contains("fonts-liberation", dockerfile);
        Assert.Contains("fonts-dejavu-core", dockerfile);
    }

    [Fact]
    public void ContainerExecutor_IsImageFresh_WhenRecent_ReturnsTrue()
    {
        var recentTime = DateTime.UtcNow.AddDays(-5).ToString("O");
        var fakeRunner = new MockProcessRunner((exe, args) => new ProcessExecutionResult(0, recentTime, string.Empty));
        var options = new ContainerExecutorOptions();

        var fresh = NightCodeForgeContainerExecutor.IsImageFresh(fakeRunner, "ncf-agent-base:latest", options, TimeSpan.FromDays(30));
        Assert.True(fresh);
    }

    [Fact]
    public void ContainerExecutor_IsImageFresh_WhenOlderThanMaxAge_ReturnsFalse()
    {
        var oldTime = DateTime.UtcNow.AddDays(-45).ToString("O");
        var fakeRunner = new MockProcessRunner((exe, args) => new ProcessExecutionResult(0, oldTime, string.Empty));
        var options = new ContainerExecutorOptions();

        var fresh = NightCodeForgeContainerExecutor.IsImageFresh(fakeRunner, "ncf-agent-base:latest", options, TimeSpan.FromDays(30));
        Assert.False(fresh);
    }

    [Fact]
    public void ContainerExecutor_IsImageFresh_WhenInspectFails_ReturnsFalse()
    {
        var fakeRunner = new MockProcessRunner((exe, args) => new ProcessExecutionResult(1, string.Empty, "Error: No such image"));
        var options = new ContainerExecutorOptions();

        var fresh = NightCodeForgeContainerExecutor.IsImageFresh(fakeRunner, "ncf-agent-base:latest", options, TimeSpan.FromDays(30));
        Assert.False(fresh);
    }

    private static ContainerExecutorOptions StoppedTestOptions() => new()
    {
        AutoStartContainer = true,
        ContainerNameOrId = "ncf-sandbox-1",
        ContainerImage = "alpine",
        HostWorkspacePath = "/test/repo",
        ContainerWorkspacePath = "/workspace",
        MountPackageCaches = true,
        MountHostSdks = false,
        ProtectCloneWorkingTree = false
    };

    /// <summary>Simulates `docker inspect` mount output (destination=source=rw;) for the expected mounts, minus omitted ones.</summary>
    private static string MountInspectOutput(ContainerExecutorOptions options, string? omitDestination = null, string? readOnlyFlipDestination = null) =>
        string.Concat(NightCodeForgeContainerExecutor.BuildVolumeMounts(options)
            .Where(m => m.ContainerPath != omitDestination)
            .Select(m => $"{m.ContainerPath}={m.HostPath}={(m.ContainerPath == readOnlyFlipDestination ? m.IsReadOnly : !m.IsReadOnly).ToString().ToLowerInvariant()};"));

    private static NightCodeForgeContainerExecutor CreateExecutor(ContainerExecutorOptions options, IProcessRunner runner) =>
        new(
            options,
            runner,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NightCodeForgeContainerExecutor>.Instance,
            new Mock<IHostUserIdentityResolver>().Object,
            new Mock<IOperationsTelemetryService>().Object);

    private static MockProcessRunner DriftRunner(List<string> executed, string mountOutput, string image, bool running = true, string user = "") =>
        new((exe, args) =>
        {
            executed.Add($"{exe} {args}".Trim());
            if (args.Contains("inspect") && args.Contains(".State.Running"))
            {
                return new ProcessExecutionResult(0, running ? "true\n" : "false\n", string.Empty);
            }
            if (args.Contains("inspect") && args.Contains(".Mounts"))
            {
                return new ProcessExecutionResult(0, mountOutput, string.Empty);
            }
            if (args.Contains("inspect") && args.Contains(".Config.Image"))
            {
                return new ProcessExecutionResult(0, image + "\n", string.Empty);
            }
            if (args.Contains("inspect") && args.Contains(".Config.User"))
            {
                return new ProcessExecutionResult(0, user + "\n", string.Empty);
            }
            return new ProcessExecutionResult(0, string.Empty, string.Empty);
        });

    [Fact]
    public void EnsureContainerRunning_WhenUserDiffers_RemovesAndRecreatesContainer()
    {
        var executed = new List<string>();
        var options = StoppedTestOptions();
        options.UserUidGid = "1000:1000";
        var runner = DriftRunner(executed, MountInspectOutput(options), "alpine", user: "0:0");

        CreateExecutor(options, runner).EnsureContainerRunning();

        Assert.Contains(executed, c => c.Contains("rm -f ncf-sandbox-1"));
        Assert.Contains(executed, c => c.Contains("run "));
    }

    [Fact]
    public void EnsureContainerRunning_WhenUserMatches_ReusesContainerWithoutRecreation()
    {
        var executed = new List<string>();
        var options = StoppedTestOptions();
        options.UserUidGid = "1000:1000";
        var runner = DriftRunner(executed, MountInspectOutput(options), "alpine", user: "1000:1000");

        CreateExecutor(options, runner).EnsureContainerRunning();

        Assert.DoesNotContain(executed, c => c.Contains("rm -f"));
        Assert.DoesNotContain(executed, c => c.Contains("run "));
    }


    [Fact]
    public void EnsureContainerRunning_WhenMountsAreMissing_RemovesAndRecreatesContainer()
    {
        var executed = new List<string>();
        var options = StoppedTestOptions();
        var runner = DriftRunner(executed, MountInspectOutput(options, omitDestination: "/cache/nuget"), "alpine");

        CreateExecutor(options, runner).EnsureContainerRunning();

        var rmIndex = executed.FindIndex(c => c.Contains("rm -f ncf-sandbox-1"));
        var runIndex = executed.FindIndex(c => c.Contains("run "));
        Assert.True(rmIndex >= 0, "expected docker rm -f");
        Assert.True(runIndex > rmIndex, "expected docker run after docker rm");
        Assert.Contains(executed, c => c.Contains("/cache/nuget") && c.Contains("run "));
    }

    [Fact]
    public void EnsureContainerRunning_WhenMountModeDiffers_RemovesAndRecreatesContainer()
    {
        var executed = new List<string>();
        var options = StoppedTestOptions();
        var runner = DriftRunner(executed, MountInspectOutput(options, readOnlyFlipDestination: "/cache/npm"), "alpine");

        CreateExecutor(options, runner).EnsureContainerRunning();

        Assert.Contains(executed, c => c.Contains("rm -f ncf-sandbox-1"));
        Assert.Contains(executed, c => c.Contains("run "));
    }

    [Fact]
    public void EnsureContainerRunning_WhenMountsMatch_ReusesContainerWithoutRecreation()
    {
        var executed = new List<string>();
        var options = StoppedTestOptions();
        var runner = DriftRunner(executed, MountInspectOutput(options), "alpine");

        CreateExecutor(options, runner).EnsureContainerRunning();

        Assert.DoesNotContain(executed, c => c.Contains("rm -f"));
        Assert.DoesNotContain(executed, c => c.Contains("run "));
        Assert.DoesNotContain(executed, c => c.Contains("start "));
    }

    [Fact]
    public void EnsureContainerRunning_WhenImageMismatches_RemovesAndRecreatesContainer()
    {
        var executed = new List<string>();
        var options = StoppedTestOptions();
        var runner = DriftRunner(executed, MountInspectOutput(options), "alpine:old");

        CreateExecutor(options, runner).EnsureContainerRunning();

        Assert.Contains(executed, c => c.Contains("rm -f ncf-sandbox-1"));
        Assert.Contains(executed, c => c.Contains("run "));
    }

    [Fact]
    public void EnsureContainerRunning_WhenWindowsDriveMountsMatchDockerDesktopHostMnt_ReusesContainerWithoutRecreation()
    {
        var executed = new List<string>();
        var options = StoppedTestOptions();
        options.HostWorkspacePath = "C:\\test\\repo";
        options.UseWsl = false;

        // Simulate Docker Desktop returning /host_mnt/c/test/repo for C:\test\repo
        var mounts = NightCodeForgeContainerExecutor.BuildVolumeMounts(options)
            .Select(m => m.ContainerPath == "/workspace"
                ? $"/workspace=/host_mnt/c/test/repo={(!m.IsReadOnly).ToString().ToLowerInvariant()};"
                : $"{m.ContainerPath}={m.HostPath}={(!m.IsReadOnly).ToString().ToLowerInvariant()};");
        var mountOutput = string.Concat(mounts);
        var runner = DriftRunner(executed, mountOutput, "alpine");

        CreateExecutor(options, runner).EnsureContainerRunning();

        Assert.DoesNotContain(executed, c => c.Contains("rm -f"));
        Assert.DoesNotContain(executed, c => c.Contains("run "));
    }

    [Fact]
    public void EnsureContainerRunning_WhenInspectFails_RemovesAndRecreatesContainer()
    {
        var executed = new List<string>();
        var options = StoppedTestOptions();
        var runner = new MockProcessRunner((exe, args) =>
        {
            executed.Add($"{exe} {args}".Trim());
            if (args.Contains("inspect") && args.Contains(".State.Running"))
            {
                return new ProcessExecutionResult(0, "true\n", string.Empty);
            }
            if (args.Contains("inspect") && args.Contains(".Mounts"))
            {
                return new ProcessExecutionResult(1, string.Empty, "Error: No such container: ncf-sandbox-1");
            }
            return new ProcessExecutionResult(0, string.Empty, string.Empty);
        });

        CreateExecutor(options, runner).EnsureContainerRunning();

        Assert.Contains(executed, c => c.Contains("rm -f ncf-sandbox-1"));
        Assert.Contains(executed, c => c.Contains("run "));
    }

    [Fact]
    public void EnsureContainerRunning_WhenImageHasImplicitLatestTag_ReusesContainerWithoutRecreation()
    {
        var executed = new List<string>();
        var options = StoppedTestOptions();
        options.ContainerImage = "alpine";
        var runner = DriftRunner(executed, MountInspectOutput(options), "alpine:latest");

        CreateExecutor(options, runner).EnsureContainerRunning();

        Assert.DoesNotContain(executed, c => c.Contains("rm -f"));
        Assert.DoesNotContain(executed, c => c.Contains("run "));
    }

    [Fact]
    public void EnsureContainerRunning_WhenMountDestinationHasTrailingSlash_ReusesContainerWithoutRecreation()
    {
        var executed = new List<string>();
        var options = StoppedTestOptions();
        // Simulate Docker inspect returning destinations with trailing slashes
        var mounts = NightCodeForgeContainerExecutor.BuildVolumeMounts(options)
            .Select(m => $"{m.ContainerPath}/={m.HostPath}={(!m.IsReadOnly).ToString().ToLowerInvariant()};");
        var mountOutput = string.Concat(mounts);
        var runner = DriftRunner(executed, mountOutput, "alpine");

        CreateExecutor(options, runner).EnsureContainerRunning();

        Assert.DoesNotContain(executed, c => c.Contains("rm -f"));
        Assert.DoesNotContain(executed, c => c.Contains("run "));
    }

    [Fact]
    public void EnsureContainerRunning_WhenContainerWorkspacePathNull_DetectsDriftWithoutThrowing()
    {
        var executed = new List<string>();
        var options = StoppedTestOptions();
        options.ContainerWorkspacePath = null!;
        var runner = DriftRunner(executed, "/workspace=/other/path=true;", "alpine");

        // Should not throw ArgumentNullException, and should detect source mismatch drift
        CreateExecutor(options, runner).EnsureContainerRunning();

        Assert.Contains(executed, c => c.Contains("rm -f ncf-sandbox-1"));
        Assert.Contains(executed, c => c.Contains("run "));
    }

    [Fact]
    public void EnsureContainerRunning_WhenCanonicalClonePathSourceMismatches_RemovesAndRecreatesContainer()
    {
        var executed = new List<string>();
        var options = StoppedTestOptions();
        options.CanonicalClonePath = "/original/clone";
        var mounts = NightCodeForgeContainerExecutor.BuildVolumeMounts(options)
            .Select(m => m.ContainerPath == "/clone"
                ? "/clone=/mismatched/clone=false;"
                : $"{m.ContainerPath}={m.HostPath}={(!m.IsReadOnly).ToString().ToLowerInvariant()};");
        var mountOutput = string.Concat(mounts);
        var runner = DriftRunner(executed, mountOutput, "alpine");

        CreateExecutor(options, runner).EnsureContainerRunning();

        Assert.Contains(executed, c => c.Contains("rm -f ncf-sandbox-1"));
        Assert.Contains(executed, c => c.Contains("run "));
    }

    [Fact]
    public void ContainerExecutor_EnsureContainerRunning_WhenStopped_StartsContainer()
    {
        var executedCommands = new List<string>();
        var fakeRunner = new MockProcessRunner((exe, args) =>
        {
            executedCommands.Add($"{exe} {args}".Trim());
            if (args.Contains("inspect") && args.Contains(".State.Running"))
            {
                return new ProcessExecutionResult(0, "false\n", string.Empty);
            }
            if (args.Contains("inspect") && args.Contains(".Mounts"))
            {
                return new ProcessExecutionResult(0, MountInspectOutput(StoppedTestOptions()), string.Empty);
            }
            if (args.Contains("inspect") && args.Contains(".Config.Image"))
            {
                return new ProcessExecutionResult(0, "alpine\n", string.Empty);
            }
            if (args.StartsWith("start") || args.Contains(" start "))
            {
                return new ProcessExecutionResult(0, "ncf-sandbox-1\n", string.Empty);
            }
            return new ProcessExecutionResult(0, string.Empty, string.Empty);
        });

        var options = StoppedTestOptions();

        var executor = new NightCodeForgeContainerExecutor(
            options,
            fakeRunner,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NightCodeForgeContainerExecutor>.Instance,
            new Mock<IHostUserIdentityResolver>().Object,
            new Mock<IOperationsTelemetryService>().Object);

        executor.EnsureContainerRunning();

        Assert.Contains(executedCommands, cmd => cmd.Contains("start ncf-sandbox-1"));
        Assert.DoesNotContain(executedCommands, cmd => cmd.Contains("run "));
    }

    [Fact]
    public void ContainerExecutor_EnsureContainerRunning_WhenSpawnConflict_RemovesStaleContainerAndRetries()
    {
        var executedCommands = new List<string>();
        int spawnAttempts = 0;

        var fakeRunner = new MockProcessRunner((exe, args) =>
        {
            executedCommands.Add($"{exe} {args}".Trim());
            if (args.Contains("inspect") && args.Contains(".State.Running"))
            {
                return new ProcessExecutionResult(1, string.Empty, "Error: No such container");
            }
            if (args.StartsWith("run ") || args.Contains(" run "))
            {
                spawnAttempts++;
                if (spawnAttempts == 1)
                {
                    return new ProcessExecutionResult(125, string.Empty, "docker: Error response from daemon: Conflict. The container name \"/ncf-sandbox-1\" is already in use by container \"deadbeef\".");
                }
                return new ProcessExecutionResult(0, "deadbeef\n", string.Empty);
            }
            if (args.Contains("rm -f"))
            {
                return new ProcessExecutionResult(0, "ncf-sandbox-1\n", string.Empty);
            }
            return new ProcessExecutionResult(0, string.Empty, string.Empty);
        });

        var options = new ContainerExecutorOptions
        {
            AutoStartContainer = true,
            ContainerNameOrId = "ncf-sandbox-1",
            ContainerImage = "alpine"
        };

        var executor = new NightCodeForgeContainerExecutor(
            options,
            fakeRunner,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NightCodeForgeContainerExecutor>.Instance,
            new Mock<IHostUserIdentityResolver>().Object,
            new Mock<IOperationsTelemetryService>().Object);

        executor.EnsureContainerRunning();

        Assert.Equal(2, spawnAttempts);
        Assert.Contains(executedCommands, cmd => cmd.Contains("rm -f ncf-sandbox-1"));
    }

    [Fact]
    public async Task BareHostExecutionEnvironment_ExecutesViaProcessRunner_ReturnsCommandResult()
    {
        var fakeRunner = new MockProcessRunner((exe, args) =>
        {
            return new ProcessExecutionResult(0, "success output\n", string.Empty);
        });

        var env = new BareHostExecutionEnvironment(
            fakeRunner,
            ExecutionTier.BareHost,
            null,
            "Ubuntu",
            new FakePathResolver());

        var result = await env.ExecuteAsync("echo 'hello'");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("success output\n", result.StandardOutput);
        Assert.True(result.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task BareHostExecutionEnvironment_WhenTimedOut_ThrowsTimeoutException()
    {
        var fakeRunner = new MockProcessRunner((exe, args) =>
        {
            return new ProcessExecutionResult(124, string.Empty, "Process timed out after 5s.");
        });

        var env = new BareHostExecutionEnvironment(
            fakeRunner,
            new FakePathResolver());

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await env.ExecuteAsync("sleep 10", timeoutSeconds: 5);
        });
    }

    [Fact]
    public async Task WslExecutionEnvironment_ExecutesCommandViaProcessRunner_ReturnsExpectedResult()
    {
        string? capturedExe = null;
        string? capturedArgs = null;

        var fakeRunner = new MockProcessRunner((exe, args) =>
        {
            capturedExe = exe;
            capturedArgs = args;
            return new ProcessExecutionResult(0, "wsl output\n", string.Empty);
        });

        var env = new WslExecutionEnvironment(
            processRunner: fakeRunner,
            distribution: "Ubuntu-22.04",
            defaultWorkingDirectory: @"C:\Work\Repo");

        var result = await env.ExecuteAsync("dotnet --version");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("wsl output\n", result.StandardOutput);
        Assert.NotNull(capturedExe);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal("wsl.exe", capturedExe);
            Assert.Contains("-d Ubuntu-22.04", capturedArgs);
            Assert.Contains("bash -c cd \"/mnt/c/Work/Repo\" && dotnet --version", capturedArgs);
        }
    }

    [Fact]
    public async Task WslExecutionEnvironment_WhenTimedOut_ReturnsExitCode124WithTruncatedTrue()
    {
        var fakeRunner = new MockProcessRunner((exe, args) =>
        {
            return new ProcessExecutionResult(124, string.Empty, "Process timed out after 5s.");
        });

        var env = new WslExecutionEnvironment(
            fakeRunner,
            distribution: "Ubuntu",
            defaultWorkingDirectory: null);

        var result = await env.ExecuteAsync("long_running_job", timeoutSeconds: 5);

        Assert.Equal(124, result.ExitCode);
        Assert.True(result.Truncated);
        Assert.Contains("WSL command execution timed out", result.StandardError);
    }

    [Fact]
    public void WslExecutionEnvironment_Properties_ReflectConstructorParameters()
    {
        var env = new WslExecutionEnvironment(
            new MockProcessRunner((_, _) => new ProcessExecutionResult(0, string.Empty, string.Empty)),
            distribution: "Debian",
            defaultWorkingDirectory: "/mnt/c/project");

        Assert.Equal(ExecutionTier.WslDirect, env.Tier);
        Assert.Equal("Debian", env.Distribution);
        Assert.Equal("/mnt/c/project", env.DefaultWorkingDirectory);
    }

    private static (ExecutionEnvironmentProvider Provider, List<string> Executed) BuildCleanupProvider(Func<string, ProcessExecutionResult>? handler = null)
    {
        var executed = new List<string>();
        var runner = new MockProcessRunner((exe, args) =>
        {
            var cmd = $"{exe} {args}".Trim();
            executed.Add(cmd);
            return handler?.Invoke(cmd) ?? new ProcessExecutionResult(0, string.Empty, string.Empty);
        });
        var provider = new TestExecutionEnvironmentProviderBuilder
        {
            ProcessRunner = runner,
            PathResolver = new FakePathResolver(),
            Options = Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions { AutoDetectWsl = false })
        }.Build();
        return (provider, executed);
    }

    [Fact]
    public async Task CleanupTaskContainerAsync_RemovesMatchingTaskContainer()
    {
        var (provider, executed) = BuildCleanupProvider();
        var workspace = new Workspace { Id = 3, ExecutionMode = ExecutionMode.ContainerSandbox, ContainerName = "ncf-sandbox-3" };

        await provider.CleanupTaskContainerAsync(workspace, 5);

        Assert.Equal(["docker rm -f ncf-sandbox-3-task-5"], executed);
    }

    [Fact]
    public async Task CleanupTaskContainerAsync_WithWsl_RoutesThroughWslExecutable()
    {
        var (provider, executed) = BuildCleanupProvider();
        var workspace = new Workspace
        {
            Id = 3,
            ExecutionMode = ExecutionMode.ContainerSandbox,
            ContainerName = "ncf-sandbox-3",
            UseWslForContainers = true,
            WslDistribution = "Ubuntu"
        };

        await provider.CleanupTaskContainerAsync(workspace, 5);

        Assert.Equal(["wsl.exe -d Ubuntu -- docker rm -f ncf-sandbox-3-task-5"], executed);
    }

    [Fact]
    public async Task CleanupTaskContainerAsync_WhenContainerMissingOrDockerFails_DoesNotThrow()
    {
        var (missing, _) = BuildCleanupProvider(_ => new ProcessExecutionResult(1, string.Empty, "Error: No such container: x"));
        var (failing, _) = BuildCleanupProvider(_ => new ProcessExecutionResult(1, string.Empty, "Cannot connect to the Docker daemon"));
        var workspace = new Workspace { Id = 3, ExecutionMode = ExecutionMode.ContainerSandbox, ContainerName = "ncf-sandbox-3" };

        await missing.CleanupTaskContainerAsync(workspace, 5);
        await failing.CleanupTaskContainerAsync(workspace, 5);
    }

    [Fact]
    public async Task CleanupTaskContainerAsync_WhenNotContainerSandbox_DoesNothing()
    {
        var (provider, executed) = BuildCleanupProvider();

        await provider.CleanupTaskContainerAsync(new Workspace { Id = 3, ExecutionMode = ExecutionMode.BareHost }, 5);

        Assert.Empty(executed);
    }

    [Fact]
    public async Task ListTaskContainerIdsAsync_ParsesOnlyExactTaskContainerNames()
    {
        var (provider, executed) = BuildCleanupProvider(cmd => cmd.Contains(" ps ")
            ? new ProcessExecutionResult(0, "ncf-sandbox-3-task-5\nncf-sandbox-3-task-12\nncf-sandbox-3-task-x\nother-ncf-sandbox-3-task-9\n", string.Empty)
            : new ProcessExecutionResult(0, string.Empty, string.Empty));
        var workspace = new Workspace { Id = 3, ExecutionMode = ExecutionMode.ContainerSandbox, ContainerName = "ncf-sandbox-3" };

        var ids = await provider.ListTaskContainerIdsAsync(workspace);

        Assert.Equal([5, 12], ids);
        Assert.Contains(executed, c => c.Contains("name=ncf-sandbox-3-task-"));
    }

    [Fact]
    public async Task ListTaskContainerIdsAsync_WithLeadingSlashAndDuplicates_ParsesAndDeduplicatesCorrectly()
    {
        var (provider, executed) = BuildCleanupProvider(cmd => cmd.Contains(" ps ")
            ? new ProcessExecutionResult(0, "/ncf-sandbox-3-task-5\nncf-sandbox-3-task-5\n/ncf-sandbox-3-task-19\n", string.Empty)
            : new ProcessExecutionResult(0, string.Empty, string.Empty));
        var workspace = new Workspace { Id = 3, ExecutionMode = ExecutionMode.ContainerSandbox, ContainerName = "ncf-sandbox-3" };

        var ids = await provider.ListTaskContainerIdsAsync(workspace);

        Assert.Equal([5, 19], ids);
    }

    [Fact]
    public async Task CleanupWorkspaceContainersAsync_RemovesAllListedTaskContainers()
    {
        var (provider, executed) = BuildCleanupProvider(cmd => cmd.Contains(" ps ")
            ? new ProcessExecutionResult(0, "ncf-sandbox-3-task-5\nncf-sandbox-3-task-6\n", string.Empty)
            : new ProcessExecutionResult(0, string.Empty, string.Empty));
        var workspace = new Workspace { Id = 3, ExecutionMode = ExecutionMode.ContainerSandbox, ContainerName = "ncf-sandbox-3" };

        await provider.CleanupWorkspaceContainersAsync(workspace);

        Assert.Contains("docker rm -f ncf-sandbox-3-task-5", executed);
        Assert.Contains("docker rm -f ncf-sandbox-3-task-6", executed);
        Assert.Contains("docker rm -f ncf-sandbox-3", executed);
    }

    private class MockProcessRunner : IProcessRunner
    {
        private readonly Func<string, string, ProcessExecutionResult> _handler;

        public MockProcessRunner(Func<string, string, ProcessExecutionResult> handler)
        {
            _handler = handler;
        }

        public ProcessExecutionResult Run(ProcessStartSpec spec)
            => _handler(spec.FileName, spec.ArgumentList != null ? string.Join(" ", spec.ArgumentList) : (spec.Arguments ?? string.Empty));

        public Task<ProcessExecutionResult> RunAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(Run(spec));

        public IAsyncEnumerable<ProcessStreamChunk> RunStreamingAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IProcessStreamSession StartSession(ProcessStartSpec spec)
            => throw new NotImplementedException();
    }
}

