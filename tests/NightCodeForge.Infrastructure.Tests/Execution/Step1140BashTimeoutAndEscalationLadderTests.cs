using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Execution;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public class Step1140BashTimeoutAndEscalationLadderTests
{
    private readonly Mock<IOperationsTelemetryService> _telemetryMock = new();
    private readonly Mock<IHostUserIdentityResolver> _userIdentityMock = new();

    public Step1140BashTimeoutAndEscalationLadderTests()
    {
        _userIdentityMock.Setup(u => u.ResolveUserIdentity(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns("1000:1000");
    }

    [Fact]
    public void BuildDockerRunArgs_IncludesInitAndPidsLimitByDefault()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-test-init"
        };

        var args = NightCodeForgeContainerExecutor.BuildDockerRunArgs(options);

        Assert.Contains("--init", args);
        Assert.Contains("--pids-limit", args);
        var pidsIdx = args.IndexOf("--pids-limit");
        Assert.Equal("4096", args[pidsIdx + 1]);
    }

    [Fact]
    public void BuildDockerRunArgs_RespectsCustomPidsLimit()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-test-custom-pids",
            PidsLimit = 2048
        };

        var args = NightCodeForgeContainerExecutor.BuildDockerRunArgs(options);

        Assert.Contains("--init", args);
        Assert.Contains("--pids-limit", args);
        var pidsIdx = args.IndexOf("--pids-limit");
        Assert.Equal("2048", args[pidsIdx + 1]);
    }

    [Fact]
    public void BuildDockerRunArgs_DisabledPidsLimit_OmitsFlag()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-test-zero-pids",
            PidsLimit = 0
        };

        var args = NightCodeForgeContainerExecutor.BuildDockerRunArgs(options);

        Assert.Contains("--init", args);
        Assert.DoesNotContain("--pids-limit", args);
    }

    [Fact]
    public void BuildInContainerReaperScript_DefaultWhitelist_ProtectsCsnavKeepAliveBashPidAndSkipsZombies()
    {
        var script = NightCodeForgeContainerExecutor.BuildInContainerReaperScript();

        Assert.Contains("csnav", script);
        Assert.Contains("[[ \"$comm\" == \"tail\" ]] && [[ \"$cmdline\" == *\"tail\"*\"/dev/null\"* ]]", script);
        Assert.Contains("[ \"$pid\" = \"$BASHPID\" ] && continue", script);
        Assert.Contains("[ \"$state\" = \"Z\" ]", script);
        Assert.Contains("kill -15", script);
        Assert.Contains("kill -9", script);
        Assert.DoesNotContain("index.lock", script);
        Assert.Contains("comm", script);
        Assert.Contains("cmdline", script);
    }

    [Fact]
    public void BuildInContainerReaperScript_CustomWhitelist_IncludesAllTokens()
    {
        var script = NightCodeForgeContainerExecutor.BuildInContainerReaperScript(new[] { "csnav", "my-daemon", "custom_worker" });

        Assert.Contains("csnav", script);
        Assert.Contains("my-daemon", script);
        Assert.Contains("custom_worker", script);
    }

    [Fact]
    public async Task ExecuteAsync_Step1HealthyProbe_Returns124AndSweepSucceeded()
    {
        // Scenario 1: Command times out -> Step 1 reaper runs -> Step 2A probe (5s) succeeds
        // Shell restarts cleanly, exit code 124, container is NOT removed.
        var executedSpecs = new List<ProcessStartSpec>();
        var runnerMock = new Mock<IProcessRunner>();

        runnerMock.Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>()))
            .Returns(() => new HangingProcessStreamSession());

        runnerMock.Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessStartSpec spec, CancellationToken ct) =>
            {
                executedSpecs.Add(spec);
                // When probe is executed, return probe OK
                if (spec.ArgumentList != null && spec.ArgumentList.Contains("__NCF_PROBE_OK__"))
                {
                    return new ProcessExecutionResult(0, "__NCF_PROBE_OK__\n", string.Empty);
                }
                // Reaper script execution
                return new ProcessExecutionResult(0, string.Empty, string.Empty);
            });

        runnerMock.Setup(r => r.Run(It.IsAny<ProcessStartSpec>()))
            .Returns((ProcessStartSpec spec) =>
            {
                executedSpecs.Add(spec);
                return new ProcessExecutionResult(0, string.Empty, string.Empty);
            });

        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-sandbox-sc1",
            DefaultTimeoutSeconds = 1,
            AutoStartContainer = false
        };

        var executor = new NightCodeForgeContainerExecutor(
            options,
            runnerMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            _userIdentityMock.Object,
            _telemetryMock.Object);

        var result = await executor.ExecuteAsync("sleep 100", timeoutSeconds: 1);

        Assert.Equal(124, result.ExitCode);
        Assert.Contains("in-container processes reaped", result.StandardError);

        // Verify Step 1 reaper ran
        Assert.Contains(executedSpecs, s => s.ArgumentList != null && s.ArgumentList.Any(a => a.Contains("get_targets")));

        // Verify Step 2A probe ran with 5s timeout
        var probeSpec = executedSpecs.FirstOrDefault(s => s.ArgumentList != null && s.ArgumentList.Contains("__NCF_PROBE_OK__"));
        Assert.NotNull(probeSpec);
        Assert.Equal(5, probeSpec.TimeoutSeconds);

        // Verify container was NOT removed (docker rm -f was NOT called)
        Assert.DoesNotContain(executedSpecs, s => s.ArgumentList != null && s.ArgumentList.Contains("rm"));

        // Verify telemetry recorded ProcessSweepSucceeded
        _telemetryMock.Verify(t => t.RecordShellCommandCompleted(
            It.Is<ShellCommandRecord>(r => r.ExitCode == 124 && r.RecoveryStage == ContainerRecoveryStage.ProcessSweepSucceeded)),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Step2ProbeFails_RecyclesContainerAndSucceedsOnRetryProbe()
    {
        // Scenario 2: Command times out -> Step 1 runs -> Step 2A probe fails
        // -> Step 2B docker rm -f + EnsureContainerRunning -> Step 2C retry probe succeeds
        var executedSpecs = new List<ProcessStartSpec>();
        var runnerMock = new Mock<IProcessRunner>();
        var probeAttempts = 0;

        runnerMock.Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>()))
            .Returns(() => new HangingProcessStreamSession());

        runnerMock.Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessStartSpec spec, CancellationToken ct) =>
            {
                executedSpecs.Add(spec);
                if (spec.ArgumentList != null && spec.ArgumentList.Contains("__NCF_PROBE_OK__"))
                {
                    probeAttempts++;
                    if (probeAttempts == 1)
                    {
                        // 2A probe fails
                        return new ProcessExecutionResult(1, "unresponsive", string.Empty);
                    }
                    // 2C retry probe succeeds
                    return new ProcessExecutionResult(0, "__NCF_PROBE_OK__\n", string.Empty);
                }
                return new ProcessExecutionResult(0, string.Empty, string.Empty);
            });

        runnerMock.Setup(r => r.Run(It.IsAny<ProcessStartSpec>()))
            .Returns((ProcessStartSpec spec) =>
            {
                executedSpecs.Add(spec);
                return new ProcessExecutionResult(0, string.Empty, string.Empty);
            });

        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-sandbox-sc2",
            DefaultTimeoutSeconds = 1,
            AutoStartContainer = false
        };

        var executor = new NightCodeForgeContainerExecutor(
            options,
            runnerMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            _userIdentityMock.Object,
            _telemetryMock.Object);

        var result = await executor.ExecuteAsync("sleep 100", timeoutSeconds: 1);

        Assert.Equal(124, result.ExitCode);
        Assert.Contains("unresponsive container was recycled", result.StandardError);

        // Verify rm -f was called to recycle the wedged container
        Assert.Contains(executedSpecs, s => s.ArgumentList != null && s.ArgumentList.Contains("rm") && s.ArgumentList.Contains("-f"));

        // Verify wsl --shutdown was NOT called
        Assert.DoesNotContain(executedSpecs, s => s.ArgumentList != null && s.ArgumentList.Contains("--shutdown"));

        // Verify telemetry recorded ContainerRecreated
        _telemetryMock.Verify(t => t.RecordShellCommandCompleted(
            It.Is<ShellCommandRecord>(r => r.ExitCode == 124 && r.RecoveryStage == ContainerRecoveryStage.ContainerRecreated)),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Step3WindowsWslWedged_TriggersWslShutdownAndSucceedsOn30sProbe()
    {
        // Scenario 3: Windows + UseWsl=true. 2A probe fails, 2C probe fails -> Step 3 triggers wsl --shutdown
        // and succeeds on 30s probe.
        var executedSpecs = new List<ProcessStartSpec>();
        var runnerMock = new Mock<IProcessRunner>();
        var probeAttempts = 0;

        runnerMock.Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>()))
            .Returns(() => new HangingProcessStreamSession());

        runnerMock.Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessStartSpec spec, CancellationToken ct) =>
            {
                executedSpecs.Add(spec);
                if (spec.ArgumentList != null && spec.ArgumentList.Contains("__NCF_PROBE_OK__"))
                {
                    probeAttempts++;
                    if (probeAttempts < 3)
                    {
                        // 2A and 2C probes fail
                        return new ProcessExecutionResult(1, "unresponsive", string.Empty);
                    }
                    // 3C probe (30s) succeeds
                    return new ProcessExecutionResult(0, "__NCF_PROBE_OK__\n", string.Empty);
                }
                return new ProcessExecutionResult(0, string.Empty, string.Empty);
            });

        runnerMock.Setup(r => r.Run(It.IsAny<ProcessStartSpec>()))
            .Returns((ProcessStartSpec spec) =>
            {
                executedSpecs.Add(spec);
                return new ProcessExecutionResult(0, string.Empty, string.Empty);
            });

        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-sandbox-sc3",
            DefaultTimeoutSeconds = 1,
            AutoStartContainer = false,
            UseWsl = true
        };

        var executor = new NightCodeForgeContainerExecutor(
            options,
            runnerMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            _userIdentityMock.Object,
            _telemetryMock.Object);

        var result = await executor.ExecuteAsync("sleep 100", timeoutSeconds: 1);

        Assert.Equal(124, result.ExitCode);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Contains("WSL2 environment was restarted", result.StandardError);
            Assert.Contains(executedSpecs, s => s.ArgumentList != null && s.ArgumentList.Contains("--shutdown") && s.TimeoutSeconds == 120);

            var probe30 = executedSpecs.FirstOrDefault(s => s.ArgumentList != null && s.ArgumentList.Contains("__NCF_PROBE_OK__") && s.TimeoutSeconds == 30);
            Assert.NotNull(probe30);
        }
        else
        {
            Assert.Contains("recovery failed", result.StandardError);
            Assert.DoesNotContain(executedSpecs, s => s.ArgumentList != null && s.ArgumentList.Contains("--shutdown"));
        }
    }

    [Fact]
    public async Task ExecuteAsync_NonWslOrNonWindows_NeverTriggersWslShutdown()
    {
        // Scenario 4: UseWsl = false -> wsl --shutdown is never called even if probes fail.
        var executedSpecs = new List<ProcessStartSpec>();
        var runnerMock = new Mock<IProcessRunner>();

        runnerMock.Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>()))
            .Returns(() => new HangingProcessStreamSession());

        runnerMock.Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessStartSpec spec, CancellationToken ct) =>
            {
                executedSpecs.Add(spec);
                return new ProcessExecutionResult(1, "fail", string.Empty);
            });

        runnerMock.Setup(r => r.Run(It.IsAny<ProcessStartSpec>()))
            .Returns((ProcessStartSpec spec) =>
            {
                executedSpecs.Add(spec);
                return new ProcessExecutionResult(1, "fail", string.Empty);
            });

        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-sandbox-sc4",
            DefaultTimeoutSeconds = 1,
            AutoStartContainer = false,
            UseWsl = false
        };

        var executor = new NightCodeForgeContainerExecutor(
            options,
            runnerMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            _userIdentityMock.Object,
            _telemetryMock.Object);

        var result = await executor.ExecuteAsync("sleep 100", timeoutSeconds: 1);

        Assert.Equal(124, result.ExitCode);
        Assert.Contains("recovery failed", result.StandardError);
        Assert.DoesNotContain(executedSpecs, s => s.ArgumentList != null && s.ArgumentList.Contains("--shutdown"));
    }

    [Fact]
    public async Task ExecuteAsync_CallerCancellation_ClosesShellRunsReaperAndRethrows()
    {
        var executedSpecs = new List<ProcessStartSpec>();
        var runnerMock = new Mock<IProcessRunner>();
        var session = new HangingProcessStreamSession();

        runnerMock.Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>()))
            .Returns(session);

        runnerMock.Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessStartSpec spec, CancellationToken ct) =>
            {
                executedSpecs.Add(spec);
                return new ProcessExecutionResult(0, string.Empty, string.Empty);
            });

        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-sandbox-cancel",
            DefaultTimeoutSeconds = 10,
            AutoStartContainer = false
        };

        var executor = new NightCodeForgeContainerExecutor(
            options,
            runnerMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            _userIdentityMock.Object,
            _telemetryMock.Object);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await executor.ExecuteAsync("sleep 100", timeoutSeconds: 10, cancellationToken: cts.Token);
        });

        // Verify shell session was killed
        Assert.True(session.WasKilled);

        // Verify in-container reaper process sweep was executed
        Assert.Contains(executedSpecs, s => s.ArgumentList != null && s.ArgumentList.Any(a => a.Contains("get_targets")));
    }

    [Fact]
    public async Task EvictIdleWorkspaceContainersAsync_EvictsOnlyIdleContainersBeyondTtl()
    {
        var workspace = new Workspace
        {
            Id = 42,
            ExecutionMode = ExecutionMode.ContainerSandbox
        };

        var factoryMock = new Mock<IContainerEnvironmentFactory>();
        var runnerMock = new Mock<IProcessRunner>();
        var gitMock = new Mock<IGitWorkspaceManager>();
        var pathResolverMock = new Mock<IWorkspacePathResolver>();

        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-sandbox-42",
            AutoStartContainer = false
        };

        var executor = new NightCodeForgeContainerExecutor(
            options,
            runnerMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            _userIdentityMock.Object,
            _telemetryMock.Object);

        factoryMock.Setup(f => f.CreateContainerEnvironment(workspace, SandboxSecurityTier.Standard, null))
            .Returns(executor);

        var provider = new ExecutionEnvironmentProvider(
            new BareHostExecutionEnvironment(runnerMock.Object, pathResolverMock.Object),
            runnerMock.Object,
            gitMock.Object,
            factoryMock.Object,
            NullLogger<ExecutionEnvironmentProvider>.Instance);

        // 1. Get environment (caches it)
        var env = provider.GetEnvironment(workspace, SandboxSecurityTier.Standard, taskId: null);
        Assert.Same(executor, env);

        // 2. Not idle long enough (TimeSpan.FromHours(1)) -> 0 evicted
        var evicted = await provider.EvictIdleWorkspaceContainersAsync(workspace, TimeSpan.FromHours(1));
        Assert.Equal(0, evicted);
        factoryMock.Verify(f => f.RemoveBaseContainerAsync(workspace, It.IsAny<CancellationToken>()), Times.Never);

        // 3. Idle timeout zero -> evicted!
        var evictedNow = await provider.EvictIdleWorkspaceContainersAsync(workspace, TimeSpan.Zero);
        Assert.Equal(1, evictedNow);
        factoryMock.Verify(f => f.RemoveBaseContainerAsync(workspace, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EvictIdleWorkspaceContainersAsync_MultipleSecurityTiers_DoesNotEvictWhileAnyTierIsActiveAndEvictsOnceWhenAllIdle()
    {
        var workspace = new Workspace
        {
            Id = 77,
            ExecutionMode = ExecutionMode.ContainerSandbox
        };

        var factoryMock = new Mock<IContainerEnvironmentFactory>();
        var runnerMock = new Mock<IProcessRunner>();
        var gitMock = new Mock<IGitWorkspaceManager>();
        var pathResolverMock = new Mock<IWorkspacePathResolver>();

        var standardExecutor = new NightCodeForgeContainerExecutor(
            new ContainerExecutorOptions { ContainerNameOrId = "ncf-sandbox-77", AutoStartContainer = false },
            runnerMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            _userIdentityMock.Object,
            _telemetryMock.Object);

        factoryMock.Setup(f => f.CreateContainerEnvironment(workspace, SandboxSecurityTier.Standard, null))
            .Returns(standardExecutor);

        var provider = new ExecutionEnvironmentProvider(
            new BareHostExecutionEnvironment(runnerMock.Object, pathResolverMock.Object),
            runnerMock.Object,
            gitMock.Object,
            factoryMock.Object,
            NullLogger<ExecutionEnvironmentProvider>.Instance);

        // Cache Standard tier first
        _ = provider.GetEnvironment(workspace, SandboxSecurityTier.Standard, taskId: null);
        await Task.Delay(60);

        // Create and cache RestrictedReadOnly tier much later so it has a fresh timestamp
        var restrictedExecutor = new NightCodeForgeContainerExecutor(
            new ContainerExecutorOptions { ContainerNameOrId = "ncf-sandbox-77", IsRestrictedReadOnly = true, AutoStartContainer = false },
            runnerMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            _userIdentityMock.Object,
            _telemetryMock.Object);
        factoryMock.Setup(f => f.CreateContainerEnvironment(workspace, SandboxSecurityTier.RestrictedReadOnly, null))
            .Returns(restrictedExecutor);
        _ = provider.GetEnvironment(workspace, SandboxSecurityTier.RestrictedReadOnly, taskId: null);

        // Standard tier is older than 40ms, but RestrictedReadOnly tier was just created (< 40ms) -> must NOT evict shared base container
        var evictedPartial = await provider.EvictIdleWorkspaceContainersAsync(workspace, TimeSpan.FromMilliseconds(40));
        Assert.Equal(0, evictedPartial);
        factoryMock.Verify(f => f.RemoveBaseContainerAsync(workspace, It.IsAny<CancellationToken>()), Times.Never);

        // When both tiers are idle beyond threshold -> evicts both cached entries and removes base container ONCE
        var evictedBoth = await provider.EvictIdleWorkspaceContainersAsync(workspace, TimeSpan.Zero);
        Assert.Equal(1, evictedBoth);
        factoryMock.Verify(f => f.RemoveBaseContainerAsync(workspace, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutWithPartialOutput_PreservesAccumulatedOutputAndMatchesRestrictedUser()
    {
        var executedSpecs = new List<ProcessStartSpec>();
        var runnerMock = new Mock<IProcessRunner>();
        var session = new HangingProcessStreamSession();
        session.EmitStdoutLine("Step 1 of build succeeded...");
        session.EmitStdoutLine("Running hung integration test...");

        runnerMock.Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>()))
            .Returns(session);

        runnerMock.Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessStartSpec spec, CancellationToken ct) =>
            {
                executedSpecs.Add(spec);
                if (spec.ArgumentList != null && spec.ArgumentList.Contains("__NCF_PROBE_OK__"))
                {
                    return new ProcessExecutionResult(0, "__NCF_PROBE_OK__\n", string.Empty);
                }
                return new ProcessExecutionResult(0, string.Empty, string.Empty);
            });

        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-sandbox-ro",
            DefaultTimeoutSeconds = 1,
            AutoStartContainer = false,
            IsRestrictedReadOnly = true,
            UserUidGid = "2000:2000"
        };

        var executor = new NightCodeForgeContainerExecutor(
            options,
            runnerMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            _userIdentityMock.Object,
            _telemetryMock.Object);

        var result = await executor.ExecuteAsync("dotnet test", timeoutSeconds: 1);

        Assert.Equal(124, result.ExitCode);
        Assert.Contains("Step 1 of build succeeded...", result.StandardOutput);
        Assert.Contains("Running hung integration test...", result.StandardOutput);

        // In RestrictedReadOnly mode, both reaper and probe must pass --user 1000:1000
        var reaperSpec = executedSpecs.First(s => s.ArgumentList != null && s.ArgumentList.Any(a => a.Contains("get_targets")));
        var reaperArgs = reaperSpec.ArgumentList!.ToList();
        var reaperUserIdx = reaperArgs.IndexOf("--user");
        Assert.True(reaperUserIdx >= 0);
        Assert.Equal("1000:1000", reaperArgs[reaperUserIdx + 1]);

        var probeSpec = executedSpecs.First(s => s.ArgumentList != null && s.ArgumentList.Contains("__NCF_PROBE_OK__"));
        var probeArgs = probeSpec.ArgumentList!.ToList();
        var probeUserIdx = probeArgs.IndexOf("--user");
        Assert.True(probeUserIdx >= 0);
        Assert.Equal("1000:1000", probeArgs[probeUserIdx + 1]);
    }

    [Fact]
    public async Task ExecuteAsync_Step3DockerInfoFailsAfterWslShutdown_StartsDockerDaemonInWsl()
    {
        var executedSpecs = new List<ProcessStartSpec>();
        var runnerMock = new Mock<IProcessRunner>();
        var probeAttempts = 0;

        runnerMock.Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>()))
            .Returns(() => new HangingProcessStreamSession());

        runnerMock.Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessStartSpec spec, CancellationToken ct) =>
            {
                executedSpecs.Add(spec);
                if (spec.ArgumentList != null && spec.ArgumentList.Contains("__NCF_PROBE_OK__"))
                {
                    probeAttempts++;
                    return probeAttempts >= 3
                        ? new ProcessExecutionResult(0, "__NCF_PROBE_OK__\n", string.Empty)
                        : new ProcessExecutionResult(1, "unresponsive", string.Empty);
                }
                if (spec.ArgumentList != null && spec.ArgumentList.Contains("info"))
                {
                    return new ProcessExecutionResult(1, string.Empty, "Cannot connect to the Docker daemon");
                }
                return new ProcessExecutionResult(0, string.Empty, string.Empty);
            });

        runnerMock.Setup(r => r.Run(It.IsAny<ProcessStartSpec>()))
            .Returns((ProcessStartSpec spec) =>
            {
                executedSpecs.Add(spec);
                return new ProcessExecutionResult(0, string.Empty, string.Empty);
            });

        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-sandbox-sc3-daemon",
            DefaultTimeoutSeconds = 1,
            AutoStartContainer = false,
            UseWsl = true,
            WslDistribution = "Ubuntu-24.04"
        };

        var executor = new NightCodeForgeContainerExecutor(
            options,
            runnerMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            _userIdentityMock.Object,
            _telemetryMock.Object);

        var result = await executor.ExecuteAsync("sleep 100", timeoutSeconds: 1);

        Assert.Equal(124, result.ExitCode);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Contains(executedSpecs, s =>
                s.ArgumentList != null &&
                s.ArgumentList.Contains("-d") &&
                s.ArgumentList.Contains("Ubuntu-24.04") &&
                s.ArgumentList.Any(a => a.Contains("service docker start")));
        }
    }

    [Fact]
    public async Task WslExecutionEnvironment_Timeout_ExecutesWslRecoveryParity()
    {
        var runnerMock = new Mock<IProcessRunner>();
        var executedSpecs = new List<ProcessStartSpec>();
        var probeCount = 0;

        runnerMock.Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessStartSpec spec, CancellationToken ct) =>
            {
                executedSpecs.Add(spec);
                if (spec.ArgumentList != null && spec.ArgumentList.Contains("__NCF_PROBE_OK__"))
                {
                    probeCount++;
                    // First probe (5s) fails -> triggers wsl.exe --shutdown -> second probe (30s) succeeds
                    return probeCount == 1
                        ? new ProcessExecutionResult(1, string.Empty, "probe failed")
                        : new ProcessExecutionResult(0, "__NCF_PROBE_OK__\n", string.Empty);
                }
                if (spec.ArgumentList != null && spec.ArgumentList.Contains("--shutdown"))
                {
                    return new ProcessExecutionResult(0, string.Empty, string.Empty);
                }
                // Simulate timeout on initial command
                return new ProcessExecutionResult(124, string.Empty, "Process timed out after 1s");
            });

        var wslEnv = new WslExecutionEnvironment(runnerMock.Object, "Ubuntu", "/repo");
        var res = await wslEnv.ExecuteAsync("sleep 100", timeoutSeconds: 1);

        Assert.Equal(124, res.ExitCode);
        Assert.True(res.Truncated);
        Assert.Contains("WSL command execution timed out", res.StandardError);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Verify 5s probe, wsl --shutdown (120s timeout), and 30s probe were all executed
            Assert.Contains(executedSpecs, s => s.ArgumentList != null && s.ArgumentList.Contains("__NCF_PROBE_OK__") && s.TimeoutSeconds == 5);
            Assert.Contains(executedSpecs, s => s.ArgumentList != null && s.ArgumentList.Contains("--shutdown") && s.TimeoutSeconds == 120);
            Assert.Contains(executedSpecs, s => s.ArgumentList != null && s.ArgumentList.Contains("__NCF_PROBE_OK__") && s.TimeoutSeconds == 30);
        }
    }

    [Fact]
    public async Task WslExecutionEnvironment_TimeoutViaCancellationTokenSource_ExecutesWslRecoveryParity()
    {
        var runnerMock = new Mock<IProcessRunner>();
        var executedSpecs = new List<ProcessStartSpec>();
        var probeCount = 0;

        runnerMock.Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProcessStartSpec spec, CancellationToken ct) =>
            {
                executedSpecs.Add(spec);
                if (spec.ArgumentList != null && spec.ArgumentList.Contains("__NCF_PROBE_OK__"))
                {
                    probeCount++;
                    return probeCount == 1
                        ? new ProcessExecutionResult(1, string.Empty, "probe failed")
                        : new ProcessExecutionResult(0, "__NCF_PROBE_OK__\n", string.Empty);
                }
                if (spec.ArgumentList != null && spec.ArgumentList.Contains("--shutdown"))
                {
                    return new ProcessExecutionResult(0, string.Empty, string.Empty);
                }
                // Wait until internal timeout CTS cancels
                await Task.Delay(Timeout.Infinite, ct);
                return new ProcessExecutionResult(0, string.Empty, string.Empty);
            });

        var wslEnv = new WslExecutionEnvironment(runnerMock.Object, "Ubuntu", "/repo");
        var res = await wslEnv.ExecuteAsync("sleep 100", timeoutSeconds: 1);

        Assert.Equal(124, res.ExitCode);
        Assert.True(res.Truncated);
        Assert.Contains("WSL command execution timed out", res.StandardError);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Contains(executedSpecs, s => s.ArgumentList != null && s.ArgumentList.Contains("__NCF_PROBE_OK__") && s.TimeoutSeconds == 5);
            Assert.Contains(executedSpecs, s => s.ArgumentList != null && s.ArgumentList.Contains("--shutdown") && s.TimeoutSeconds == 120);
            Assert.Contains(executedSpecs, s => s.ArgumentList != null && s.ArgumentList.Contains("__NCF_PROBE_OK__") && s.TimeoutSeconds == 30);
        }
    }

    private class HangingProcessStreamSession : IProcessStreamSession
    {
        private readonly StreamWriter _stdin;
        private readonly StreamReader _stdout;
        private readonly StreamReader _stderr;
        private readonly MemoryStream _inMs = new();
        private readonly MemoryStream _errMs = new();
        private readonly System.Threading.Channels.Channel<byte[]> _pipe = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

        public bool WasKilled { get; private set; }

        public HangingProcessStreamSession()
        {
            _stdin = new StreamWriter(_inMs);
            _stdout = new StreamReader(new ChannelReadStream(_pipe.Reader));
            _stderr = new StreamReader(_errMs);
        }

        public void EmitStdoutLine(string line)
        {
            _pipe.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(line + "\n"));
        }

        public int? ExitCode => WasKilled ? 137 : null;
        public bool HasExited => WasKilled;
        public StreamWriter StandardInput => _stdin;
        public StreamReader StandardOutput => _stdout;
        public StreamReader StandardError => _stderr;

        public void Kill()
        {
            WasKilled = true;
            _pipe.Writer.TryComplete();
        }

        public async IAsyncEnumerable<ProcessStreamChunk> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ExitCode ?? 0);
        }

        public void Dispose()
        {
            _pipe.Writer.TryComplete();
            _stdin.Dispose();
            _stdout.Dispose();
            _stderr.Dispose();
            _inMs.Dispose();
            _errMs.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private class ChannelReadStream : Stream
    {
        private readonly System.Threading.Channels.ChannelReader<byte[]> _reader;
        private byte[]? _currentChunk;
        private int _chunkPos;

        public ChannelReadStream(System.Threading.Channels.ChannelReader<byte[]> reader) => _reader = reader;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_currentChunk == null || _chunkPos >= _currentChunk.Length)
            {
                if (!await _reader.WaitToReadAsync(cancellationToken))
                    return 0;

                if (!_reader.TryRead(out _currentChunk))
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
