using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Diagnostics;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Filtering;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Hubs;
using NightCodeForge.Infrastructure.Telemetry;
using Xunit;

namespace NightCodeForge.Agents.Tests.Diagnostics;

public class EnvironmentDiagnosticShellServiceTests
{
    private static OperationsTelemetryService CreateTelemetryService()
    {
        var mockHubContext = new Mock<IHubContext<OrchestratorHub, IOrchestratorHubClient>>();
        var mockClients = new Mock<IHubClients<IOrchestratorHubClient>>();
        var mockClient = new Mock<IOrchestratorHubClient>();
        mockClients.Setup(c => c.All).Returns(mockClient.Object);
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);
        return new OperationsTelemetryService(mockHubContext.Object, NullLogger<OperationsTelemetryService>.Instance);
    }

    private static IContainerEnvironmentFactory CreateContainerFactory(
        IProcessRunner processRunner,
        IOperationsTelemetryService telemetryService,
        IWorkspacePathResolver? pathResolver = null,
        ExecutionEnvironmentProviderOptions? providerOptions = null)
    {
        var resolverMock = new Mock<IWorkspacePathResolver>();
        resolverMock.Setup(r => r.GetAgentWorkspaceRootPath(It.IsAny<int>())).Returns<int>(id => $"/tmp/ncf-diag-test/wks/{id}");
        resolverMock.Setup(r => r.GetAgentWorkingClonePath(It.IsAny<int>())).Returns<int>(id => $"/tmp/ncf-diag-test/wks/{id}/clone");
        resolverMock.Setup(r => r.GetWorkspaceMemoryPath(It.IsAny<int>())).Returns<int>(id => $"/tmp/ncf-diag-test/wks/{id}/memory");
        resolverMock.Setup(r => r.GetAttachmentsPath(It.IsAny<int>())).Returns<int>(id => $"/tmp/ncf-diag-test/wks/{id}/attachments");
        resolverMock.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        resolverMock.Setup(r => r.GetWslUserName()).Returns("testuser");

        var sdkMock = new Mock<IHostSdkDiscoveryService>();
        sdkMock.Setup(s => s.DiscoverSdkMountPaths(It.IsAny<string?>())).Returns(["/usr/share/dotnet"]);
        sdkMock.Setup(s => s.DiscoverSdkDetails(It.IsAny<string?>()))
            .Returns(new HostSdkDiscoveryDetails(["/usr/share/dotnet"], new Dictionary<string, string>(), new Dictionary<string, string>()));

        var identityMock = new Mock<IHostUserIdentityResolver>();
        identityMock.Setup(i => i.ResolveUserIdentity(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string>())).Returns("1000:1000");

        var companionMock = new Mock<ICompanionToolInstaller>();

        return new ContainerEnvironmentFactory(
            processRunner,
            Options.Create(providerOptions ?? new ExecutionEnvironmentProviderOptions()),
            Options.Create(new SecurityOptions()),
            pathResolver ?? resolverMock.Object,
            sdkMock.Object,
            identityMock.Object,
            telemetryService,
            companionMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance);
    }

    private static EnvironmentDiagnosticShellService CreateService(
        IWorkspaceRepository workspaceRepository,
        IExecutionEnvironmentProvider environmentProvider,
        IContainerEnvironmentFactory containerFactory,
        IOperationsTelemetryService telemetryService,
        IWorkspacePathResolver? pathResolver = null,
        ExecutionEnvironmentProviderOptions? providerOptions = null)
    {
        var resolver = pathResolver ?? Mock.Of<IWorkspacePathResolver>(r =>
            r.ToHostPath(It.IsAny<string>()) == "~/.nightcodeforge/wks");

        return new EnvironmentDiagnosticShellService(
            workspaceRepository,
            environmentProvider,
            containerFactory,
            resolver,
            CliOutputFilterRegistry.Default,
            telemetryService,
            Options.Create(providerOptions ?? new ExecutionEnvironmentProviderOptions()),
            NullLogger<EnvironmentDiagnosticShellService>.Instance);
    }

    [Fact]
    public async Task InspectEnvironmentAsync_CoversWorkspaceClone_TaskWorktree_AndSystemNightwatchScopes()
    {
        var repo = new InMemoryWorkspaceRepository();
        var workspace = await repo.CreateAsync(new Workspace
        {
            Name = "SampleRepo",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            RepositoryPath = "/repos/sample",
            Ext4MirrorPath = "/home/testuser/.nightcodeforge/wks/1/clone",
            ContainerName = "ncf-sample",
            ContainerImage = "ncf-agent-base:v5"
        });

        var telemetry = CreateTelemetryService();
        var runnerMock = new Mock<IProcessRunner>();
        runnerMock
            .Setup(r => r.RunAsync(
                It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("inspect") && s.ArgumentList.Contains("{{.State.Running}}")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "true\n", string.Empty));
        runnerMock
            .Setup(r => r.RunAsync(
                It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("inspect") && !s.ArgumentList.Contains("{{.State.Running}}")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(1, string.Empty, "Error: No such container"));

        var factory = CreateContainerFactory(runnerMock.Object, telemetry);

        var envProviderMock = new Mock<IExecutionEnvironmentProvider>();
        envProviderMock
            .Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .Returns<SandboxScopeDescriptor, CancellationToken>((scope, ct) => factory.CreateContainerEnvironmentAsync(scope, ct));

        var service = CreateService(repo, envProviderMock.Object, factory, telemetry);

        // 1. WorkspaceClone
        var cloneSnapshot = await service.InspectEnvironmentAsync(new EnvironmentDiagnosticTarget(
            WorkspaceId: workspace.Id,
            TaskId: null,
            SecurityTier: SandboxSecurityTier.Standard));

        Assert.Equal(SandboxScopeKind.WorkspaceClone, cloneSnapshot.ScopeKind);
        Assert.Equal(workspace.Id, cloneSnapshot.WorkspaceId);
        Assert.Equal("SampleRepo", cloneSnapshot.WorkspaceName);
        Assert.Equal("ncf-sample", cloneSnapshot.ContainerName);
        Assert.Equal("/workspace/clone", cloneSnapshot.DefaultWorkingDirectory);
        Assert.True(cloneSnapshot.IsContainerRunning);
        Assert.Null(cloneSnapshot.DriftDescription);
        Assert.Contains("/usr/share/dotnet", cloneSnapshot.SynthesizedPath);
        Assert.NotEmpty(cloneSnapshot.ConfiguredMounts);

        // 2. TaskWorktree (RestrictedReadOnly -> -ro suffix)
        var taskSnapshot = await service.InspectEnvironmentAsync(new EnvironmentDiagnosticTarget(
            WorkspaceId: workspace.Id,
            TaskId: 17,
            SecurityTier: SandboxSecurityTier.RestrictedReadOnly,
            ScopeKindOverride: SandboxScopeKind.TaskWorktree));

        Assert.Equal(SandboxScopeKind.TaskWorktree, taskSnapshot.ScopeKind);
        Assert.Equal(17, taskSnapshot.TaskId);
        Assert.Equal("ncf-sample-task-17-ro", taskSnapshot.ContainerName);
        Assert.Equal("/workspace/task-17", taskSnapshot.DefaultWorkingDirectory);
        Assert.Equal(ContainerExecutorOptions.RestrictedReadOnlyUserUidGid, taskSnapshot.UserUidGid);

        // 3. SystemNightwatch (WorkspaceId = null, WorkspaceName = "System Nightwatch", ncf-nightwatch-ro)
        var nightwatchSnapshot = await service.InspectEnvironmentAsync(new EnvironmentDiagnosticTarget(
            WorkspaceId: null,
            ScopeKindOverride: SandboxScopeKind.SystemNightwatch));

        Assert.Equal(SandboxScopeKind.SystemNightwatch, nightwatchSnapshot.ScopeKind);
        Assert.Null(nightwatchSnapshot.WorkspaceId);
        Assert.Equal(EnvironmentDiagnosticShellService.SystemNightwatchWorkspaceName, nightwatchSnapshot.WorkspaceName);
        Assert.Equal("ncf-nightwatch-ro", nightwatchSnapshot.ContainerName);
        Assert.Equal(SandboxSecurityTier.RestrictedReadOnly, nightwatchSnapshot.SecurityTier);
        Assert.Equal(SandboxMountTable.NightwatchVirtualRoot, nightwatchSnapshot.DefaultWorkingDirectory);
        Assert.Contains(nightwatchSnapshot.ConfiguredMounts, m => m.ContainerPath == "/nightcodeforge/ncf-logs" && m.IsReadOnly);
    }

    [Fact]
    public async Task InspectEnvironmentAsync_ValidatesWorkspaceLookupAndRequiredIds()
    {
        var repo = new InMemoryWorkspaceRepository();
        var telemetry = CreateTelemetryService();
        var factory = CreateContainerFactory(new Mock<IProcessRunner>().Object, telemetry);
        var service = CreateService(repo, new Mock<IExecutionEnvironmentProvider>().Object, factory, telemetry);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.InspectEnvironmentAsync(new EnvironmentDiagnosticTarget(WorkspaceId: null)));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.InspectEnvironmentAsync(new EnvironmentDiagnosticTarget(WorkspaceId: 999)));
    }

    [Fact]
    public async Task ExecuteCommandAsync_SupportsAgentBashToolAndRawExecutionEnvironmentPipelines()
    {
        var repo = new InMemoryWorkspaceRepository();
        var workspace = await repo.CreateAsync(new Workspace
        {
            Name = "DualPipeline",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            RepositoryPath = "/repos/dual"
        });

        var telemetry = CreateTelemetryService();
        var factory = CreateContainerFactory(new Mock<IProcessRunner>().Object, telemetry);

        var envMock = new Mock<IExecutionEnvironment>();
        envMock.SetupGet(e => e.Tier).Returns(ExecutionTier.ContainerSandbox);
        envMock
            .Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>(
                (cmd, wd, timeout, _, _) => Task.FromResult(new CommandResult(
                    0,
                    $"ran:{cmd}|wd:{wd}|to:{timeout}",
                    string.Empty,
                    TimeSpan.FromMilliseconds(25))));

        var providerMock = new Mock<IExecutionEnvironmentProvider>();
        providerMock
            .Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(envMock.Object);

        var service = CreateService(repo, providerMock.Object, factory, telemetry);

        // AgentBashTool in RestrictedReadOnly rejects mutating command via ReadOnlyCommandValidator without invoking underlying env
        var blocked = await service.ExecuteCommandAsync(
            new EnvironmentDiagnosticTarget(
                WorkspaceId: workspace.Id,
                SecurityTier: SandboxSecurityTier.RestrictedReadOnly,
                Pipeline: DiagnosticExecutionPipeline.AgentBashTool),
            "rm -rf /workspace/tmp");

        Assert.NotEqual(0, blocked.ExitCode);
        Assert.StartsWith("[READ-ONLY VIOLATION]:", blocked.Output);
        envMock.Verify(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()), Times.Never);

        // AgentBashTool allowed command routes through SandboxBashToolModule (dynamic timeout escalation + formatted output)
        var bashResult = await service.ExecuteCommandAsync(
            new EnvironmentDiagnosticTarget(
                WorkspaceId: workspace.Id,
                SecurityTier: SandboxSecurityTier.Standard,
                Pipeline: DiagnosticExecutionPipeline.AgentBashTool,
                WorkingDirectoryOverride: "/workspace/src"),
            "echo hello");

        Assert.Equal(0, bashResult.ExitCode);
        Assert.Equal("/workspace/src", bashResult.EffectiveWorkingDirectory);
        Assert.Contains("ran:echo hello|wd:/workspace/src", bashResult.Output);

        // RawExecutionEnvironment passes command, working directory, and timeout override directly
        var rawResult = await service.ExecuteCommandAsync(
            new EnvironmentDiagnosticTarget(
                WorkspaceId: workspace.Id,
                SecurityTier: SandboxSecurityTier.RestrictedReadOnly,
                Pipeline: DiagnosticExecutionPipeline.RawExecutionEnvironment,
                WorkingDirectoryOverride: "/nightcodeforge",
                TimeoutSecondsOverride: 45),
            "id && pwd");

        Assert.Equal(0, rawResult.ExitCode);
        Assert.Equal("/nightcodeforge", rawResult.EffectiveWorkingDirectory);
        Assert.Equal("ran:id && pwd|wd:/nightcodeforge|to:45", rawResult.Output);
    }

    [Fact]
    public async Task ExecuteCommandAsync_OnCancellation_RecoversCorrelatedPartialOutputWithoutCrossTalk()
    {
        var repo = new InMemoryWorkspaceRepository();
        var workspace = await repo.CreateAsync(new Workspace
        {
            Name = "CancelRecovery",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            RepositoryPath = "/repos/cancel"
        });

        var telemetry = CreateTelemetryService();
        var factory = CreateContainerFactory(new Mock<IProcessRunner>().Object, telemetry);

        using var cts = new CancellationTokenSource();
        var envMock = new Mock<IExecutionEnvironment>();
        envMock.SetupGet(e => e.Tier).Returns(ExecutionTier.ContainerSandbox);
        envMock
            .Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>(
                (cmd, wd, _, _, ct) =>
                {
                    // Unrelated concurrent background agent shell command (must not cross-talk)
                    var bgId = "bg-agent-cmd";
                    var bgRecord = new ShellCommandRecord(bgId, "dotnet test other", "/other", DateTime.UtcNow, 0, null, ShellCommandStatus.Running, null, null);
                    telemetry.RecordShellCommandStarted(bgRecord);
                    telemetry.UpdateShellCommandOutput(bgId, "NOISE_FROM_OTHER_AGENT\n", false);

                    // Diagnostic command telemetry
                    var diagId = "diag-cmd-1";
                    var diagRecord = new ShellCommandRecord(diagId, cmd, wd ?? "/workspace", DateTime.UtcNow, 0, null, ShellCommandStatus.Running, null, null);
                    telemetry.RecordShellCommandStarted(diagRecord);
                    telemetry.UpdateShellCommandOutput(diagId, "partial line 1\n", false);
                    telemetry.UpdateShellCommandOutput(bgId, "MORE_NOISE\n", false);
                    telemetry.UpdateShellCommandOutput(diagId, "partial line 2\n", false);

                    cts.Cancel();

                    telemetry.RecordShellCommandCompleted(diagRecord with
                    {
                        DurationMs = 120,
                        ExitCode = 130,
                        Status = ShellCommandStatus.Failed,
                        OutputPreview = "partial line 1\npartial line 2\n",
                        ErrorPreview = "Command execution was canceled by caller."
                    });

                    throw new OperationCanceledException(ct);
                });

        var providerMock = new Mock<IExecutionEnvironmentProvider>();
        providerMock
            .Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(envMock.Object);

        var service = CreateService(repo, providerMock.Object, factory, telemetry);

        var result = await service.ExecuteCommandAsync(
            new EnvironmentDiagnosticTarget(
                WorkspaceId: workspace.Id,
                Pipeline: DiagnosticExecutionPipeline.RawExecutionEnvironment),
            "sleep 60",
            cts.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Equal("partial line 1\npartial line 2\n", result.Output);
        Assert.DoesNotContain("NOISE_FROM_OTHER_AGENT", result.Output);
        Assert.DoesNotContain("MORE_NOISE", result.Output);
    }

    [Fact]
    public async Task RunEnvironmentDoctorAsync_ParsesAllCategories_IncludingWarningsFailuresAndNightwatchGitSkip()
    {
        var repo = new InMemoryWorkspaceRepository();
        var workspace = await repo.CreateAsync(new Workspace
        {
            Name = "DoctorWs",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            RepositoryPath = "/repos/doc"
        });

        var telemetry = CreateTelemetryService();
        var factory = CreateContainerFactory(new Mock<IProcessRunner>().Object, telemetry);

        var simulatedDoctorStdout = """
            ===NCF_DOC:IDENTITY===
            UID=1000
            GID=1000
            USER=ubuntu
            HOME=/ncf-home
            HOME_WRITABLE=1
            ===NCF_DOC:TMP_CACHES===
            DIR|/tmp|RW
            DIR|/cache/nuget|RW
            DIR|/cache/npm|MISSING
            ===NCF_DOC:MOUNTS===
            MOUNT|/workspace|RW|RW
            MOUNT|/workspace/attachments|RO|RW
            ===NCF_DOC:GIT===
            GIT_WORKTREE=true
            GIT_DIR=.git
            ## main...origin/main
            ===NCF_DOC:TOOLS===
            TOOL|git|/usr/bin/git
            TOOL|rg|/usr/bin/rg
            TOOL|dotnet|/usr/share/dotnet/dotnet
            TOOL|csnav|/opt/ncf-dotnet-tools/csnav
            TOOL|mdfetch|
            TOOL|codecondenser|/opt/ncf-dotnet-tools/codecondenser
            ===NCF_DOC:ICU===
            INVARIANT=0
            LANG=en_US.UTF-8
            LC_ALL=en_US.UTF-8
            ICU_LIB=libicuuc.so.74 (libc6,x86-64)
            .NET SDK: 10.0.100
            """;

        var envMock = new Mock<IExecutionEnvironment>();
        envMock.SetupGet(e => e.Tier).Returns(ExecutionTier.ContainerSandbox);
        envMock
            .Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(0, simulatedDoctorStdout, string.Empty, TimeSpan.FromMilliseconds(50)));

        var providerMock = new Mock<IExecutionEnvironmentProvider>();
        providerMock
            .Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(envMock.Object);

        var service = CreateService(repo, providerMock.Object, factory, telemetry);

        var checks = await service.RunEnvironmentDoctorAsync(new EnvironmentDiagnosticTarget(WorkspaceId: workspace.Id));

        Assert.Equal(6, checks.Count);
        Assert.Equal(DoctorCheckStatus.Passed, Assert.Single(checks, c => c.Category == "Identity").Status);
        Assert.Equal(DoctorCheckStatus.Warning, Assert.Single(checks, c => c.Category == "Storage").Status);

        var mountCheck = Assert.Single(checks, c => c.Category == "Mounts");
        Assert.Equal(DoctorCheckStatus.Failed, mountCheck.Status);
        Assert.Contains("/workspace/attachments", mountCheck.Summary);

        Assert.Equal(DoctorCheckStatus.Passed, Assert.Single(checks, c => c.Category == "Git").Status);

        var toolsCheck = Assert.Single(checks, c => c.Category == "Toolchains");
        Assert.Equal(DoctorCheckStatus.Warning, toolsCheck.Status);
        Assert.Contains("mdfetch", toolsCheck.Summary);

        Assert.Equal(DoctorCheckStatus.Passed, Assert.Single(checks, c => c.Category == "Globalization").Status);

        // Nightwatch skips Git check cleanly
        var nightwatchStdout = """
            ===NCF_DOC:IDENTITY===
            UID=1000
            GID=1000
            USER=ubuntu
            HOME=/ncf-home
            HOME_WRITABLE=1
            ===NCF_DOC:GIT===
            GIT_SKIPPED=nightwatch
            """;
        envMock
            .Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(0, nightwatchStdout, string.Empty, TimeSpan.FromMilliseconds(20)));

        var nwChecks = await service.RunEnvironmentDoctorAsync(new EnvironmentDiagnosticTarget(
            WorkspaceId: null,
            ScopeKindOverride: SandboxScopeKind.SystemNightwatch));
        var nwGit = Assert.Single(nwChecks, c => c.Category == "Git");
        Assert.Equal(DoctorCheckStatus.Passed, nwGit.Status);
        Assert.Contains("Nightwatch", nwGit.Summary);
    }

    [Fact]
    public async Task RecreateEnvironmentContainerAsync_DelegatesToProviderWithResolvedScope()
    {
        var repo = new InMemoryWorkspaceRepository();
        var workspace = await repo.CreateAsync(new Workspace
        {
            Name = "RecreateWs",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            RepositoryPath = "/repos/recreate"
        });

        var telemetry = CreateTelemetryService();
        var factory = CreateContainerFactory(new Mock<IProcessRunner>().Object, telemetry);

        var capturedScopes = new List<SandboxScopeDescriptor>();
        var providerMock = new Mock<IExecutionEnvironmentProvider>();
        providerMock
            .Setup(p => p.RecreateEnvironmentContainerAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .Callback<SandboxScopeDescriptor, CancellationToken>((s, _) => capturedScopes.Add(s))
            .ReturnsAsync(Mock.Of<IExecutionEnvironment>());

        var service = CreateService(repo, providerMock.Object, factory, telemetry);

        await service.RecreateEnvironmentContainerAsync(new EnvironmentDiagnosticTarget(
            WorkspaceId: workspace.Id,
            TaskId: 9,
            SecurityTier: SandboxSecurityTier.Standard));

        await service.RecreateEnvironmentContainerAsync(new EnvironmentDiagnosticTarget(
            WorkspaceId: null,
            ScopeKindOverride: SandboxScopeKind.SystemNightwatch));

        Assert.Equal(2, capturedScopes.Count);
        Assert.Equal(SandboxScopeKind.TaskWorktree, capturedScopes[0].ScopeKind);
        Assert.Equal(9, capturedScopes[0].TaskId);
        Assert.Equal(SandboxScopeKind.SystemNightwatch, capturedScopes[1].ScopeKind);
        Assert.Null(capturedScopes[1].WorkspaceId);
        Assert.Equal(SandboxSecurityTier.RestrictedReadOnly, capturedScopes[1].SecurityTier);
    }

    [Fact]
    public async Task ExecuteCommandAsync_AgentBashTool_ReturnsStructuredError_WhenCommandReferencesUnmappedAttachments()
    {
        var repo = new InMemoryWorkspaceRepository();
        var telemetry = CreateTelemetryService();
        var factory = CreateContainerFactory(new Mock<IProcessRunner>().Object, telemetry);

        var envMock = new Mock<IExecutionEnvironment>();
        envMock.SetupGet(e => e.Tier).Returns(ExecutionTier.ContainerSandbox);

        var providerMock = new Mock<IExecutionEnvironmentProvider>();
        providerMock
            .Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(envMock.Object);

        var service = CreateService(repo, providerMock.Object, factory, telemetry);

        var result = await service.ExecuteCommandAsync(
            new EnvironmentDiagnosticTarget(
                WorkspaceId: null,
                ScopeKindOverride: SandboxScopeKind.SystemNightwatch,
                Pipeline: DiagnosticExecutionPipeline.AgentBashTool),
            "cat /workspace/attachments/file.txt");

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("Error:", result.Output);
        Assert.Contains("/workspace/attachments", result.Error);
        envMock.Verify(
            e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteCommandAsync_OnCancellation_IgnoresUnrelatedCompletedCommandWithSameText()
    {
        var repo = new InMemoryWorkspaceRepository();
        var workspace = await repo.CreateAsync(new Workspace
        {
            Name = "SameCommandCollision",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            RepositoryPath = "/repos/collision"
        });

        var telemetry = CreateTelemetryService();
        var factory = CreateContainerFactory(new Mock<IProcessRunner>().Object, telemetry);

        using var cts = new CancellationTokenSource();
        var envMock = new Mock<IExecutionEnvironment>();
        envMock.SetupGet(e => e.Tier).Returns(ExecutionTier.ContainerSandbox);
        envMock
            .Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>(
                (cmd, wd, _, _, ct) =>
                {
                    var effectiveWd = wd ?? "/workspace";

                    // An unrelated already-completed command with identical text completes after subscription
                    telemetry.RecordShellCommandCompleted(new ShellCommandRecord(
                        CommandId: "other-finished-cmd",
                        CommandLine: cmd,
                        WorkingDirectory: effectiveWd,
                        StartTime: DateTime.UtcNow.AddSeconds(-5),
                        DurationMs: 5000,
                        ExitCode: 0,
                        Status: ShellCommandStatus.Completed,
                        OutputPreview: "STALE_OTHER_OUTPUT",
                        ErrorPreview: null));

                    var diagId = "diag-real-cmd";
                    var diagRecord = new ShellCommandRecord(
                        CommandId: diagId,
                        CommandLine: cmd,
                        WorkingDirectory: effectiveWd,
                        StartTime: DateTime.UtcNow,
                        DurationMs: 0,
                        ExitCode: null,
                        Status: ShellCommandStatus.Running,
                        OutputPreview: null,
                        ErrorPreview: null);
                    telemetry.RecordShellCommandStarted(diagRecord);
                    telemetry.UpdateShellCommandOutput(diagId, "diag partial\n", false);

                    cts.Cancel();

                    telemetry.RecordShellCommandCompleted(diagRecord with
                    {
                        DurationMs = 80,
                        ExitCode = 130,
                        Status = ShellCommandStatus.Failed,
                        OutputPreview = "diag partial\n",
                        ErrorPreview = "Command execution was canceled by caller."
                    });

                    throw new OperationCanceledException(ct);
                });

        var providerMock = new Mock<IExecutionEnvironmentProvider>();
        providerMock
            .Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(envMock.Object);

        var service = CreateService(repo, providerMock.Object, factory, telemetry);

        var result = await service.ExecuteCommandAsync(
            new EnvironmentDiagnosticTarget(
                WorkspaceId: workspace.Id,
                Pipeline: DiagnosticExecutionPipeline.RawExecutionEnvironment),
            "sleep 60",
            cts.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Equal("diag partial\n", result.Output);
        Assert.DoesNotContain("STALE_OTHER_OUTPUT", result.Output);
    }

    [Fact]
    public async Task ExecuteCommandAsync_OnCancellationBeforeCommandStarts_DoesNotLeakStaleResult()
    {
        var repo = new InMemoryWorkspaceRepository();
        var workspace = await repo.CreateAsync(new Workspace
        {
            Name = "PreStartCancel",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            RepositoryPath = "/repos/prestart"
        });

        var telemetry = CreateTelemetryService();
        var factory = CreateContainerFactory(new Mock<IProcessRunner>().Object, telemetry);

        using var cts = new CancellationTokenSource();
        var envMock = new Mock<IExecutionEnvironment>();
        envMock.SetupGet(e => e.Tier).Returns(ExecutionTier.ContainerSandbox);
        envMock
            .Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>(
                (_, _, _, _, ct) =>
                {
                    cts.Cancel();
                    throw new OperationCanceledException(ct);
                });

        var providerMock = new Mock<IExecutionEnvironmentProvider>();
        providerMock
            .Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(envMock.Object);

        var service = CreateService(repo, providerMock.Object, factory, telemetry);

        var result = await service.ExecuteCommandAsync(
            new EnvironmentDiagnosticTarget(
                WorkspaceId: workspace.Id,
                Pipeline: DiagnosticExecutionPipeline.RawExecutionEnvironment),
            "sleep 60",
            cts.Token);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Contains("canceled", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunEnvironmentDoctorAsync_ScriptUsesNonEmptyFallbackForIcu_AndQuotesMountEcho()
    {
        var repo = new InMemoryWorkspaceRepository();
        var workspace = await repo.CreateAsync(new Workspace
        {
            Name = "DoctorScript",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            RepositoryPath = "/repos/docscript"
        });

        var telemetry = CreateTelemetryService();
        var factory = CreateContainerFactory(new Mock<IProcessRunner>().Object, telemetry);

        string? capturedScript = null;
        var envMock = new Mock<IExecutionEnvironment>();
        envMock.SetupGet(e => e.Tier).Returns(ExecutionTier.ContainerSandbox);
        envMock
            .Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>(
                (cmd, _, _, _, _) => capturedScript = cmd)
            .ReturnsAsync(new CommandResult(0, "===NCF_DOC:IDENTITY===\nHOME_WRITABLE=1\n", string.Empty, TimeSpan.FromMilliseconds(10)));

        var providerMock = new Mock<IExecutionEnvironmentProvider>();
        providerMock
            .Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(envMock.Object);

        var service = CreateService(repo, providerMock.Object, factory, telemetry);

        await service.RunEnvironmentDoctorAsync(new EnvironmentDiagnosticTarget(WorkspaceId: workspace.Id));

        Assert.NotNull(capturedScript);
        Assert.Contains("if [ -z \"$icu_lib\" ]; then icu_lib=$(find /usr/lib /lib", capturedScript);
        Assert.Contains("'MOUNT|/workspace", capturedScript);
        Assert.DoesNotContain("\"MOUNT|", capturedScript);
    }

    [Fact]
    public async Task RunEnvironmentDoctorAsync_DetectsCaseInsensitiveInvariantMode_AndReportsNonZeroExitOnPartialOutput()
    {
        var repo = new InMemoryWorkspaceRepository();
        var workspace = await repo.CreateAsync(new Workspace
        {
            Name = "DoctorPartialFailure",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            RepositoryPath = "/repos/docpartial"
        });

        var telemetry = CreateTelemetryService();
        var factory = CreateContainerFactory(new Mock<IProcessRunner>().Object, telemetry);

        var partialDoctorStdout = """
            ===NCF_DOC:IDENTITY===
            UID=1000
            GID=1000
            USER=ubuntu
            HOME=/ncf-home
            HOME_WRITABLE=1
            ===NCF_DOC:ICU===
            INVARIANT=TRUE
            LANG=en_US.UTF-8
            LC_ALL=en_US.UTF-8
            ICU_LIB=libicuuc.so.74
            .NET SDK: 10.0.100
            """;

        var envMock = new Mock<IExecutionEnvironment>();
        envMock.SetupGet(e => e.Tier).Returns(ExecutionTier.ContainerSandbox);
        envMock
            .Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(
                130,
                partialDoctorStdout,
                "Command execution was canceled by caller.",
                TimeSpan.FromMilliseconds(35)));

        var providerMock = new Mock<IExecutionEnvironmentProvider>();
        providerMock
            .Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(envMock.Object);

        var service = CreateService(repo, providerMock.Object, factory, telemetry);

        var checks = await service.RunEnvironmentDoctorAsync(new EnvironmentDiagnosticTarget(WorkspaceId: workspace.Id));

        var icuCheck = Assert.Single(checks, c => c.Category == "Globalization");
        Assert.Equal(DoctorCheckStatus.Failed, icuCheck.Status);

        var executionCheck = Assert.Single(checks, c => c.Category == "Execution" && c.Name == "Doctor Probe");
        Assert.Equal(DoctorCheckStatus.Failed, executionCheck.Status);
        Assert.Contains("130", executionCheck.Summary);
    }
}
