using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public class Step1236ASandboxMountAndLifecycleTests
{
    [Fact]
    public void ServiceCollection_RegistersExecutionEnvironmentProviderAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNightCodeForgeCoreAndInfrastructure();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IExecutionEnvironmentProvider));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope1 = sp.CreateScope();
        using var scope2 = sp.CreateScope();

        var p1 = scope1.ServiceProvider.GetRequiredService<IExecutionEnvironmentProvider>();
        var p2 = scope2.ServiceProvider.GetRequiredService<IExecutionEnvironmentProvider>();
        Assert.Same(p1, p2);
    }

    [Fact]
    public void ContainerMountAndDriftBuilder_ResolvesRoContainerNames_AndBuildsNightwatchDirectBindMounts()
    {
        // 1. -ro suffix isolation
        Assert.Equal("ncf-ws-5-ro", ContainerMountAndDriftBuilder.ApplySecurityTierSuffix("ncf-ws-5", SandboxSecurityTier.RestrictedReadOnly));
        Assert.Equal("ncf-ws-5-task-12-ro", ContainerMountAndDriftBuilder.ApplySecurityTierSuffix("ncf-ws-5-task-12", SandboxSecurityTier.RestrictedReadOnly));
        Assert.Equal("ncf-ws-5", ContainerMountAndDriftBuilder.ApplySecurityTierSuffix("ncf-ws-5", SandboxSecurityTier.Standard));

        var nwScope = SandboxScopeDescriptor.ForNightwatch(
            hostNcfLogsPath: @"C:\ncf\logs",
            wslNcfLogsPath: "/mnt/c/ncf/logs",
            hostSkillsPath: @"C:\ncf\skills",
            wslSkillsPath: "/mnt/c/ncf/skills",
            hostWorkspacesRootPath: null,
            wslWorkspacesRootPath: null,
            additionalMounts:
            [
                new VirtualMountEntry(
                    virtualSubPath: "wks/1/clone",
                    virtualMountPath: "/nightcodeforge/wks/1/clone",
                    hostDirectoryPath: @"\\wsl.localhost\Ubuntu\home\user\.nightcodeforge\wks\1\clone",
                    wslTargetDirectoryPath: "/home/user/.nightcodeforge/wks/1/clone",
                    isReadOnly: true)
            ]);

        Assert.Equal("ncf-nightwatch-ro", ContainerMountAndDriftBuilder.ResolveContainerName(nwScope));

        // 2. Direct -v bind mounts for /nightcodeforge/* without symlinks
        var options = new ContainerExecutorOptions
        {
            Scope = nwScope,
            MountTable = nwScope.MountTable,
            ContainerNameOrId = ContainerMountAndDriftBuilder.ResolveContainerName(nwScope),
            ContainerWorkspacePath = nwScope.VirtualRootPath,
            DefaultWorkingDirectory = nwScope.DefaultWorkingDirectory,
            IsRestrictedReadOnly = true,
            UseWsl = true,
            MountPackageCaches = false,
            MountHostSdks = false
        };

        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);

        Assert.Contains(mounts, m => m.ContainerPath == "/nightcodeforge/ncf-logs" && m.HostPath == "/mnt/c/ncf/logs" && m.IsReadOnly);
        Assert.Contains(mounts, m => m.ContainerPath == "/nightcodeforge/skills" && m.HostPath == "/mnt/c/ncf/skills" && m.IsReadOnly);
        Assert.Contains(mounts, m => m.ContainerPath == "/nightcodeforge/wks/1/clone" && m.HostPath == "/home/user/.nightcodeforge/wks/1/clone" && m.IsReadOnly);

        var runArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(options);
        Assert.Contains("ncf-nightwatch-ro", runArgs);
        Assert.Contains("/mnt/c/ncf/logs:/nightcodeforge/ncf-logs:ro", runArgs);
        Assert.Contains("/home/user/.nightcodeforge/wks/1/clone:/nightcodeforge/wks/1/clone:ro", runArgs);
        Assert.Contains("-w", runArgs);
        var wIdx = runArgs.IndexOf("-w");
        Assert.Equal("/nightcodeforge", runArgs[wIdx + 1]);
    }

    [Fact]
    public async Task ContainerEnvironmentFactory_CachesCompanionToolChecksOncePerStack_AndSupportsSystemNightwatchScope()
    {
        var runnerMock = new Mock<IProcessRunner>();
        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(p => p.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        pathResolverMock.Setup(p => p.GetWslUserName()).Returns("testuser");

        var sdkMock = new Mock<IHostSdkDiscoveryService>();
        sdkMock.Setup(s => s.DiscoverSdkMountPaths(It.IsAny<string?>())).Returns(["/usr/share/dotnet"]);

        var identityMock = new Mock<IHostUserIdentityResolver>();
        identityMock.Setup(i => i.ResolveUserIdentity(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string>())).Returns("1000:1000");

        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var companionMock = new Mock<ICompanionToolInstaller>();

        var factory = new ContainerEnvironmentFactory(
            runnerMock.Object,
            Options.Create(new ExecutionEnvironmentProviderOptions { DefaultWslDistribution = "Ubuntu" }),
            pathResolverMock.Object,
            sdkMock.Object,
            identityMock.Object,
            telemetryMock.Object,
            companionMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance);

        var ws1 = new Workspace { Id = 1, ExecutionMode = ExecutionMode.ContainerSandbox, RepositoryPath = "/repo/1" };
        var ws2 = new Workspace { Id = 2, ExecutionMode = ExecutionMode.ContainerSandbox, RepositoryPath = "/repo/2" };

        await factory.CreateContainerEnvironmentAsync(ws1, SandboxSecurityTier.Standard, effectiveTaskId: null);
        await factory.CreateContainerEnvironmentAsync(ws2, SandboxSecurityTier.RestrictedReadOnly, effectiveTaskId: 7);

        // Both workspaces resolve to the same (DotNet, UseWsl, Ubuntu) tuple -> EnsureToolsInstalledAsync is awaited only ONCE
        companionMock.Verify(
            c => c.EnsureToolsInstalledAsync(It.IsAny<string>(), It.IsAny<bool>(), "Ubuntu", It.IsAny<CancellationToken>()),
            Times.Once);

        // SystemNightwatch scope can be provisioned directly without any Workspace entity
        var nwScope = SandboxScopeDescriptor.ForNightwatch(
            hostNcfLogsPath: "/logs",
            wslNcfLogsPath: "/logs",
            hostSkillsPath: "/skills",
            wslSkillsPath: "/skills",
            hostWorkspacesRootPath: "/wks",
            wslWorkspacesRootPath: "/wks");

        var nwEnv = await factory.CreateContainerEnvironmentAsync(nwScope);
        var nwExec = Assert.IsType<NightCodeForgeContainerExecutor>(nwEnv);
        Assert.Equal("ncf-nightwatch-ro", nwExec.Options.ContainerNameOrId);
        Assert.Equal("/nightcodeforge", nwExec.Options.DefaultWorkingDirectory);
        Assert.True(nwExec.Options.IsRestrictedReadOnly);
    }

    [Fact]
    public async Task ExecutionEnvironmentProvider_DeduplicatesConcurrentAsyncCreation_AndDoesNotCacheFailedInitializations()
    {
        var runnerMock = new Mock<IProcessRunner>();
        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        var gitMock = new Mock<IGitWorkspaceManager>();
        var factoryMock = new Mock<IContainerEnvironmentFactory>();

        var callCount = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var expectedEnv = new Mock<IExecutionEnvironment>().Object;

        factoryMock.Setup(f => f.CreateContainerEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .Returns(async (SandboxScopeDescriptor _, CancellationToken _) =>
            {
                var attempt = Interlocked.Increment(ref callCount);
                if (attempt == 1)
                {
                    throw new InvalidOperationException("Transient Docker failure");
                }

                await gate.Task;
                return expectedEnv;
            });

        var provider = new ExecutionEnvironmentProvider(
            new BareHostExecutionEnvironment(runnerMock.Object, pathResolverMock.Object),
            runnerMock.Object,
            gitMock.Object,
            factoryMock.Object,
            NullLogger<ExecutionEnvironmentProvider>.Instance);

        var nwScope = SandboxScopeDescriptor.ForNightwatch(
            hostNcfLogsPath: "/logs",
            wslNcfLogsPath: "/logs",
            hostSkillsPath: "/skills",
            wslSkillsPath: "/skills",
            hostWorkspacesRootPath: "/wks",
            wslWorkspacesRootPath: "/wks");

        // 1. First call fails -> must NOT be cached
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetEnvironmentAsync(nwScope));
        Assert.Equal(1, callCount);

        // 2. Concurrent calls for the same ScopeCacheKey wait on the per-key SemaphoreSlim and share the single creation
        var t1 = provider.GetEnvironmentAsync(nwScope);
        var t2 = provider.GetEnvironmentAsync(nwScope);
        gate.SetResult();

        var results = await Task.WhenAll(t1, t2);
        Assert.Same(expectedEnv, results[0]);
        Assert.Same(expectedEnv, results[1]);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task PersistentContainerShellSession_WhenCancelled_DrainsViaSigIntAndKeepsSessionAliveForSubsequentCommands()
    {
        var runnerMock = new Mock<IProcessRunner>();
        var identityMock = new Mock<IHostUserIdentityResolver>();
        identityMock.Setup(u => u.ResolveUserIdentity(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns("1000:1000");
        var telemetryMock = new Mock<IOperationsTelemetryService>();

        using var cts = new CancellationTokenSource();
        using var interactiveSession = new SigIntResponsiveProcessStreamSession(() => cts.Cancel());
        var sessionStartCount = 0;

        runnerMock.Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref sessionStartCount);
                return interactiveSession;
            });

        runnerMock.Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-sigint-test",
            AutoStartContainer = false,
            SigIntDrainTimeoutMs = 2000
        };

        using var executor = new NightCodeForgeContainerExecutor(
            options,
            runnerMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            identityMock.Object,
            telemetryMock.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await executor.ExecuteAsync("long-running-cmd", timeoutSeconds: 10, cancellationToken: cts.Token);
        });

        // Verify SIGINT (\x03) drained cleanly to the sentinel without killing the persistent shell session
        // and without running the in-container process reaper (which would kill the persistent bash process)
        Assert.True(executor.ShellSession.WasDrainedOnLastCancel);
        Assert.NotNull(executor.ShellSession.LastCancelledCommandResult);
        Assert.Equal(PersistentContainerShellSession.SigIntExitCode, executor.ShellSession.LastCancelledCommandResult!.ExitCode);
        Assert.Contains("partial output before sigint", executor.ShellSession.LastCancelledCommandResult.StandardOutput);
        Assert.False(interactiveSession.WasKilled);
        Assert.True(executor.ShellSession.IsRunning);
        runnerMock.Verify(
            r => r.RunAsync(
                It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Any(a => a.Contains("get_targets"))),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // Subsequent command reuses the exact same shell session without starting a new docker exec process,
        // and ignores any stale sentinel line from the previous cancelled command
        var nextResult = await executor.ExecuteAsync("echo after-sigint");
        Assert.Equal(0, nextResult.ExitCode);
        Assert.Contains("after-sigint-ok", nextResult.StandardOutput);
        Assert.DoesNotContain("__NCF_SENTINEL_", nextResult.StandardOutput);
        Assert.Equal(1, sessionStartCount);
    }

    [Fact]
    public async Task ExecutionEnvironmentProvider_RecreateEnvironmentContainerAsync_ViaInterface_RecreatesSpecificTierContainer()
    {
        var runnerMock = new Mock<IProcessRunner>();
        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        var gitMock = new Mock<IGitWorkspaceManager>();
        var factoryMock = new Mock<IContainerEnvironmentFactory>();

        var firstEnv = new Mock<IExecutionEnvironment>();
        var secondEnv = new Mock<IExecutionEnvironment>();
        var createCount = 0;

        var ws = new Workspace
        {
            Id = 15,
            ContainerName = "ncf-ws-15",
            ExecutionMode = ExecutionMode.ContainerSandbox
        };

        var roScope = SandboxScopeDescriptor.ForWorkspace(ws, SandboxSecurityTier.RestrictedReadOnly);

        factoryMock.Setup(f => f.BuildWorkspaceScope(ws, SandboxSecurityTier.RestrictedReadOnly, null))
            .Returns(roScope);

        factoryMock.Setup(f => f.CreateContainerEnvironmentAsync(ws, SandboxSecurityTier.RestrictedReadOnly, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++createCount == 1 ? firstEnv.Object : secondEnv.Object);

        IExecutionEnvironmentProvider provider = new ExecutionEnvironmentProvider(
            new BareHostExecutionEnvironment(runnerMock.Object, pathResolverMock.Object),
            runnerMock.Object,
            gitMock.Object,
            factoryMock.Object,
            NullLogger<ExecutionEnvironmentProvider>.Instance);

        var initial = await provider.GetEnvironmentAsync(ws, SandboxSecurityTier.RestrictedReadOnly);
        Assert.Same(firstEnv.Object, initial);

        var recreated = await provider.RecreateEnvironmentContainerAsync(ws, SandboxSecurityTier.RestrictedReadOnly);
        Assert.Same(secondEnv.Object, recreated);
        factoryMock.Verify(f => f.RemoveContainerAsync(roScope, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WslExecutionEnvironment_WithTildeFallbackWorkingDirectory_ExpandsHomeAndEnsuresDirectoryExists()
    {
        ProcessStartSpec? capturedSpec = null;
        var runnerMock = new Mock<IProcessRunner>();
        runnerMock.Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessStartSpec, CancellationToken>((spec, _) => capturedSpec = spec)
            .ReturnsAsync(new ProcessExecutionResult(0, "ok\n", string.Empty));

        var wslEnv = new WslExecutionEnvironment(
            runnerMock.Object,
            distribution: "Ubuntu",
            defaultWorkingDirectory: SandboxMountTable.DefaultNightwatchFallbackWorkingDirectory);

        var res = await wslEnv.ExecuteAsync("ls -la");
        Assert.Equal(0, res.ExitCode);
        Assert.NotNull(capturedSpec);

        var joinedArgs = string.Join(" ", capturedSpec!.ArgumentList ?? []);
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            Assert.Contains("mkdir -p \"$HOME/.nightcodeforge/nightwatch-root\" && cd \"$HOME/.nightcodeforge/nightwatch-root\"", joinedArgs);
        }
        else
        {
            Assert.DoesNotContain("~", capturedSpec.WorkingDirectory ?? string.Empty);
        }
    }

    [Fact]
    public void ContainerMountAndDriftBuilder_ResolveHostPath_ExtractsLinuxPathFromWslUncWhenUseWslEnabled()
    {
        var options = new ContainerExecutorOptions
        {
            UseWsl = true,
            WslDistribution = "Ubuntu"
        };

        var resolved = ContainerMountAndDriftBuilder.ResolveHostPath(
            @"\\wsl.localhost\Ubuntu\home\dev\.nightcodeforge\wks\1\clone",
            options);
        Assert.Equal("/home/dev/.nightcodeforge/wks/1/clone", resolved);

        var resolvedDollar = NightCodeForge.Infrastructure.Git.WslPathConverter.ToWslPath(
            @"\\wsl$\Ubuntu\home\dev\.nightcodeforge\wks\2");
        Assert.Equal("/home/dev/.nightcodeforge/wks/2", resolvedDollar);
    }

    [Fact]
    public async Task ContainerMountAndDriftBuilder_WorkspaceWithExternalMountTableEntries_MountsAndDetectsSourceDrift()
    {
        var mountTable = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: "/wks/1/task-4",
            wslWorkingDirectory: "/wks/1/task-4",
            isReadOnly: false,
            taskId: 4,
            hostCloneDirectory: "/wks/1/clone",
            wslCloneDirectory: "/wks/1/clone",
            additionalEntries:
            [
                new VirtualMountEntry(
                    virtualSubPath: "attachments",
                    virtualMountPath: "/workspace/attachments",
                    hostDirectoryPath: "/external/attachments-v2",
                    wslTargetDirectoryPath: "/external/attachments-v2",
                    isReadOnly: true)
            ]);

        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-ws-1-task-4",
            ContainerImage = "alpine",
            HostWorkspacePath = "/wks/1",
            CanonicalClonePath = "/wks/1/clone",
            ContainerWorkspacePath = "/workspace",
            DefaultWorkingDirectory = "/workspace/task-4",
            MountTable = mountTable,
            MountPackageCaches = false,
            MountHostSdks = false,
            ProtectCloneWorkingTree = true
        };

        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);

        // /workspace is mounted once, /workspace/task-4 inside /wks/1 is not redundantly mounted, and external /workspace/attachments IS mounted ro
        Assert.Single(mounts, m => m.ContainerPath == "/workspace" && m.HostPath == "/wks/1" && !m.IsReadOnly);
        Assert.DoesNotContain(mounts, m => m.ContainerPath == "/workspace/task-4");
        Assert.Contains(mounts, m => m.ContainerPath == "/workspace/attachments" && m.HostPath == "/external/attachments-v2" && m.IsReadOnly);

        var runnerMock = new Mock<IProcessRunner>();
        var staleInspectOutput = string.Concat(mounts.Select(m =>
            m.ContainerPath == "/workspace/attachments"
                ? "/workspace/attachments=/external/attachments-v1=false;"
                : $"{m.ContainerPath}={m.HostPath}={(!m.IsReadOnly).ToString().ToLowerInvariant()};"));

        runnerMock.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .Mounts}}{{.Destination}}={{.Source}}={{.RW}};{{end}}")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, staleInspectOutput, string.Empty));
        runnerMock.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.Config.Image}}")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "alpine\n", string.Empty));

        var drift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(runnerMock.Object, options);
        Assert.NotNull(drift);
        Assert.Contains("/workspace/attachments", drift!);
    }

    [Fact]
    public async Task ContainerEnvironmentFactory_BuildWorkspaceScope_And_GetEnvironmentAsync_MatchWorkspaceProvisioningAndNormalizeWorktree()
    {
        var runnerMock = new Mock<IProcessRunner>();
        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(p => p.ToHostPath(It.IsAny<string>()))
            .Returns<string>(p => p.StartsWith("/home/", StringComparison.Ordinal)
                ? $@"\\wsl.localhost\Ubuntu\{p.TrimStart('/').Replace('/', '\\')}"
                : p);
        pathResolverMock.Setup(p => p.GetWslUserName()).Returns("testuser");

        var sdkMock = new Mock<IHostSdkDiscoveryService>();
        sdkMock.Setup(s => s.DiscoverSdkMountPaths(It.IsAny<string?>())).Returns(["/usr/share/dotnet"]);

        var identityMock = new Mock<IHostUserIdentityResolver>();
        identityMock.Setup(i => i.ResolveUserIdentity(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string>())).Returns("1000:1000");

        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var companionMock = new Mock<ICompanionToolInstaller>();
        var gitMock = new Mock<IGitWorkspaceManager>();

        var factory = new ContainerEnvironmentFactory(
            runnerMock.Object,
            Options.Create(new ExecutionEnvironmentProviderOptions { DefaultWslDistribution = "Ubuntu", UseWslForContainers = true }),
            pathResolverMock.Object,
            sdkMock.Object,
            identityMock.Object,
            telemetryMock.Object,
            companionMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance);

        var ws = new Workspace
        {
            Id = 3,
            Name = "WSL Scope Consistency",
            Ext4MirrorPath = "/home/testuser/.nightcodeforge/wks/3/clone",
            ContainerName = "ncf-ws-3",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            UseWslForContainers = true
        };

        var builtScope = factory.BuildWorkspaceScope(ws, SandboxSecurityTier.Standard, effectiveTaskId: 5);
        Assert.Same(ws, builtScope.Workspace);
        Assert.Equal("/home/testuser/.nightcodeforge/wks/3", builtScope.HostWorkspacePath);
        Assert.Equal("/home/testuser/.nightcodeforge/wks/3/clone", builtScope.CanonicalClonePath);

        // Host C# file resolution uses Windows UNC path via ToHostPath while WSL bash uses Linux path
        var resolver = new SandboxPathResolver(builtScope);
        var hostResolved = resolver.ResolveHostPath("src/Program.cs");
        Assert.StartsWith(@"\\wsl.localhost\Ubuntu\home\testuser\.nightcodeforge\wks\3\task-5\", hostResolved);
        Assert.Equal("/home/testuser/.nightcodeforge/wks/3/task-5", resolver.ResolveBashWorkingDirectory(null, ExecutionTier.WslDirect));

        // Provisioning via GetEnvironmentAsync(scope) normalizes worktree relative paths AND matches CreateContainerEnvironmentAsync(workspace)
        var provider = new ExecutionEnvironmentProvider(
            new BareHostExecutionEnvironment(runnerMock.Object, pathResolverMock.Object),
            runnerMock.Object,
            gitMock.Object,
            factory,
            NullLogger<ExecutionEnvironmentProvider>.Instance);

        var envFromScope = await provider.GetEnvironmentAsync(builtScope);
        gitMock.Verify(g => g.EnsureWorktreeRelativePathsAsync(ws, 5, It.IsAny<CancellationToken>()), Times.Once);

        var execFromScope = Assert.IsType<NightCodeForgeContainerExecutor>(envFromScope);
        var execFromWorkspace = Assert.IsType<NightCodeForgeContainerExecutor>(
            await factory.CreateContainerEnvironmentAsync(ws, SandboxSecurityTier.Standard, effectiveTaskId: 5));

        Assert.Equal(execFromWorkspace.Options.ContainerNameOrId, execFromScope.Options.ContainerNameOrId);
        Assert.Equal(execFromWorkspace.Options.HostWorkspacePath, execFromScope.Options.HostWorkspacePath);
        Assert.Equal(execFromWorkspace.Options.CanonicalClonePath, execFromScope.Options.CanonicalClonePath);
        Assert.Equal(execFromWorkspace.Options.DefaultWorkingDirectory, execFromScope.Options.DefaultWorkingDirectory);
    }

    [Fact]
    public async Task ExecutionEnvironmentProvider_ConcurrentRecreateAndGetEnvironment_SerializesUnderPerKeyLock()
    {
        var runnerMock = new Mock<IProcessRunner>();
        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        var gitMock = new Mock<IGitWorkspaceManager>();
        var factoryMock = new Mock<IContainerEnvironmentFactory>();

        var env1 = new Mock<IExecutionEnvironment>().Object;
        var env2 = new Mock<IExecutionEnvironment>().Object;
        var createCount = 0;

        var removeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRemoveToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var nwScope = SandboxScopeDescriptor.ForNightwatch(
            hostNcfLogsPath: "/logs",
            wslNcfLogsPath: "/logs",
            hostSkillsPath: "/skills",
            wslSkillsPath: "/skills");

        factoryMock.Setup(f => f.CreateContainerEnvironmentAsync(nwScope, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref createCount) == 1 ? env1 : env2);

        factoryMock.Setup(f => f.RemoveContainerAsync(nwScope, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                removeStarted.TrySetResult();
                await allowRemoveToFinish.Task;
            });

        var provider = new ExecutionEnvironmentProvider(
            new BareHostExecutionEnvironment(runnerMock.Object, pathResolverMock.Object),
            runnerMock.Object,
            gitMock.Object,
            factoryMock.Object,
            NullLogger<ExecutionEnvironmentProvider>.Instance);

        Assert.Same(env1, await provider.GetEnvironmentAsync(nwScope));

        // Start recreating while another caller concurrently calls GetEnvironmentAsync for the same scope
        var recreateTask = provider.RecreateEnvironmentContainerAsync(nwScope);
        await removeStarted.Task;

        var concurrentGetTask = provider.GetEnvironmentAsync(nwScope);
        Assert.False(concurrentGetTask.IsCompleted);

        allowRemoveToFinish.SetResult();
        var results = await Task.WhenAll(recreateTask, concurrentGetTask);

        Assert.Same(env2, results[0]);
        Assert.Same(env2, results[1]);
        Assert.Equal(2, createCount);
    }

    [Fact]
    public async Task NightCodeForgeContainerExecutor_FiltersStaleSentinelDuringSigIntDrain_AndRecoversAfterUnexpectedStdoutEof()
    {
        var runnerMock = new Mock<IProcessRunner>();
        var identityMock = new Mock<IHostUserIdentityResolver>();
        identityMock.Setup(u => u.ResolveUserIdentity(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns("1000:1000");
        var telemetryMock = new Mock<IOperationsTelemetryService>();

        using var cts = new CancellationTokenSource();
        using var sigIntSessionWithStaleSentinel = new SigIntResponsiveProcessStreamSession(
            onLongRunningCommandFramed: () => cts.Cancel(),
            emitStaleSentinelDuringSigIntDrain: true);
        using var eofSession = new UnexpectedEofProcessStreamSession();
        using var healthyRecoverySession = new SigIntResponsiveProcessStreamSession();

        var startIdx = 0;
        var sessions = new IProcessStreamSession[] { sigIntSessionWithStaleSentinel, eofSession, healthyRecoverySession };
        runnerMock.Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>()))
            .Returns(() => sessions[Math.Min(Interlocked.Increment(ref startIdx) - 1, sessions.Length - 1)]);
        runnerMock.Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        using var executor = new NightCodeForgeContainerExecutor(
            new ContainerExecutorOptions
            {
                ContainerNameOrId = "ncf-eof-test",
                AutoStartContainer = false,
                SigIntDrainTimeoutMs = 2000
            },
            runnerMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            identityMock.Object,
            telemetryMock.Object);

        // 1. Stale sentinel emitted during SIGINT drain must NOT leak into LastCancelledCommandResult
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync("long-running-cmd", timeoutSeconds: 10, cancellationToken: cts.Token));

        Assert.True(executor.ShellSession.WasDrainedOnLastCancel);
        Assert.NotNull(executor.ShellSession.LastCancelledCommandResult);
        Assert.DoesNotContain("__NCF_SENTINEL_", executor.ShellSession.LastCancelledCommandResult!.StandardOutput);
        executor.ShellSession.Terminate();

        // 2. Unexpected EOF mid-command terminates the broken shell session so the next command starts a fresh session
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync("cmd-causing-eof"));
        Assert.False(executor.ShellSession.IsRunning);

        var recovered = await executor.ExecuteAsync("echo after-sigint");
        Assert.Equal(0, recovered.ExitCode);
        Assert.Contains("after-sigint-ok", recovered.StandardOutput);
        Assert.Equal(3, startIdx);
    }

    private sealed class SigIntResponsiveProcessStreamSession : IProcessStreamSession
    {
        private readonly Channel<byte[]> _stdoutPipe = Channel.CreateUnbounded<byte[]>();
        private readonly StreamWriter _stdin;
        private readonly StreamReader _stdout;
        private readonly StreamReader _stderr = new(new MemoryStream());

        public bool WasKilled { get; private set; }
        public int? ExitCode => WasKilled ? 137 : null;
        public bool HasExited => WasKilled;
        public StreamWriter StandardInput => _stdin;
        public StreamReader StandardOutput => _stdout;
        public StreamReader StandardError => _stderr;

        private readonly Action? _onLongRunningCommandFramed;
        private readonly bool _emitStaleSentinelDuringSigIntDrain;

        public SigIntResponsiveProcessStreamSession(
            Action? onLongRunningCommandFramed = null,
            bool emitStaleSentinelDuringSigIntDrain = false)
        {
            _onLongRunningCommandFramed = onLongRunningCommandFramed;
            _emitStaleSentinelDuringSigIntDrain = emitStaleSentinelDuringSigIntDrain;
            var stdinStream = new CallbackWriteStream(OnStdinWritten);
            _stdin = new StreamWriter(stdinStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _stdout = new StreamReader(new ChannelReadStream(_stdoutPipe.Reader), new UTF8Encoding(false));
        }

        private readonly StringBuilder _inputBuffer = new();
        private bool _inLongRunningCommand;

        private void OnStdinWritten(string chunk)
        {
            lock (_inputBuffer)
            {
                _inputBuffer.Append(chunk);
                var current = _inputBuffer.ToString();
                int newlineIdx;
                while ((newlineIdx = current.IndexOf('\n')) >= 0)
                {
                    var line = current[..newlineIdx].TrimEnd('\r');
                    current = current[(newlineIdx + 1)..];
                    ProcessInputLine(line);
                }
                _inputBuffer.Clear();
                _inputBuffer.Append(current);
            }
        }

        private void ProcessInputLine(string line)
        {
            if (line.Contains("long-running-cmd", StringComparison.Ordinal))
            {
                _inLongRunningCommand = true;
                EmitLine("partial output before sigint");
                return;
            }

            if (line.Contains('\x03'))
            {
                _inLongRunningCommand = false;
                if (_emitStaleSentinelDuringSigIntDrain)
                {
                    EmitLine($"^C __NCF_SENTINEL_{new string('b', 32)}__ 130");
                }
                return;
            }

            if (line.Contains("echo after-sigint", StringComparison.Ordinal))
            {
                _inLongRunningCommand = false;
                // Emit a stale sentinel line before the command output to verify stale sentinel filtering
                EmitLine($"__NCF_SENTINEL_{new string('a', 32)}__ 130");
                EmitLine("after-sigint-ok");
                return;
            }

            if (line.StartsWith("echo \"__NCF_SENTINEL_", StringComparison.Ordinal))
            {
                if (_inLongRunningCommand)
                {
                    // Simulate command still running until \x03 arrives and triggers the recovery sentinel echo
                    _onLongRunningCommandFramed?.Invoke();
                    return;
                }

                // Extract sentinel and exit code token
                var quoted = line["echo \"".Length..].TrimEnd('"');
                var parts = quoted.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var sentinel = parts[0];
                var code = parts.Length > 1 && parts[1] != "$?" ? parts[1] : "0";
                EmitLine($"{sentinel} {code}");
            }
        }

        private void EmitLine(string line)
        {
            _stdoutPipe.Writer.TryWrite(Encoding.UTF8.GetBytes(line + "\n"));
        }

        public void Kill()
        {
            WasKilled = true;
            _stdoutPipe.Writer.TryComplete();
        }

        public async IAsyncEnumerable<ProcessStreamChunk> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => Task.FromResult(ExitCode ?? 0);

        public void Dispose()
        {
            _stdoutPipe.Writer.TryComplete();
            _stdin.Dispose();
            _stdout.Dispose();
            _stderr.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnexpectedEofProcessStreamSession : IProcessStreamSession
    {
        private readonly Channel<byte[]> _stdoutPipe = Channel.CreateUnbounded<byte[]>();
        private readonly StreamWriter _stdin;
        private readonly StreamReader _stdout;
        private readonly StreamReader _stderr = new(new MemoryStream());

        public bool WasKilled { get; private set; }
        public int? ExitCode => WasKilled ? 137 : null;
        public bool HasExited => WasKilled;
        public StreamWriter StandardInput => _stdin;
        public StreamReader StandardOutput => _stdout;
        public StreamReader StandardError => _stderr;

        public UnexpectedEofProcessStreamSession()
        {
            var stdinStream = new CallbackWriteStream(chunk =>
            {
                if (chunk.Contains("cmd-causing-eof", StringComparison.Ordinal))
                {
                    // Close stdout abruptly without emitting sentinel while HasExited is still false
                    _stdoutPipe.Writer.TryComplete();
                }
            });
            _stdin = new StreamWriter(stdinStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _stdout = new StreamReader(new ChannelReadStream(_stdoutPipe.Reader), new UTF8Encoding(false));
        }

        public void Kill()
        {
            WasKilled = true;
            _stdoutPipe.Writer.TryComplete();
        }

        public async IAsyncEnumerable<ProcessStreamChunk> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => Task.FromResult(ExitCode ?? 0);

        public void Dispose()
        {
            _stdoutPipe.Writer.TryComplete();
            _stdin.Dispose();
            _stdout.Dispose();
            _stderr.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CallbackWriteStream(Action<string> onWrite) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > 0)
            {
                onWrite(Encoding.UTF8.GetString(buffer, offset, count));
            }
        }
    }

    private sealed class ChannelReadStream(ChannelReader<byte[]> reader) : Stream
    {
        private byte[]? _currentChunk;
        private int _chunkPos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_currentChunk == null || _chunkPos >= _currentChunk.Length)
            {
                if (!await reader.WaitToReadAsync(cancellationToken))
                    return 0;

                if (!reader.TryRead(out _currentChunk))
                    return 0;

                _chunkPos = 0;
            }

            int toCopy = Math.Min(count, _currentChunk.Length - _chunkPos);
            Buffer.BlockCopy(_currentChunk, _chunkPos, buffer, offset, toCopy);
            _chunkPos += toCopy;
            return toCopy;
        }
    }
}
