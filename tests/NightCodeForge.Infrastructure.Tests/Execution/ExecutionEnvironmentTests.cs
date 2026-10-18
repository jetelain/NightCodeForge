using System.Runtime.InteropServices;
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
        Assert.Contains("/custom/tools/bin", path);
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

