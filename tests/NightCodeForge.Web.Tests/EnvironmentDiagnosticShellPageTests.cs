using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Hubs;
using NightCodeForge.Infrastructure.Telemetry;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class EnvironmentDiagnosticShellPageTests : BunitContext
{
    [Fact]
    public async Task RouteWithWorkspaceId_ActivatesWorkspaceAndRendersInspectionDetails()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws1 = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });
        var ws2 = await workspaceRepo.CreateAsync(new Workspace { Name = "Beta Repo", RepositoryPath = "/repos/beta" });

        var workspaceState = new WorkspaceStateService();
        workspaceState.SetKnownWorkspaces([ws1, ws2]);
        workspaceState.SetActiveWorkspace(ws1);

        var diagMock = new Mock<IEnvironmentDiagnosticShellService>();
        diagMock
            .Setup(s => s.InspectEnvironmentAsync(
                It.Is<EnvironmentDiagnosticTarget>(t => t.WorkspaceId == ws2.Id && t.ScopeKindOverride == SandboxScopeKind.WorkspaceClone),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleSnapshot(
                SandboxScopeKind.WorkspaceClone,
                ws2.Id,
                ws2.Name,
                driftDescription: "Image tag mismatch: expected v2, running v1"));

        RegisterPageServices(diagMock.Object, workspaceRepo, new InMemoryTaskRepository(), workspaceState);

        var cut = Render<EnvironmentDiagnosticShell>(p => p.Add(x => x.WorkspaceId, ws2.Id));

        Assert.Equal(ws2.Id, workspaceState.ActiveWorkspaceId);
        Assert.Equal("ContainerSandbox", cut.Find("#badge-execution-tier").TextContent.Trim());
        Assert.Equal("ncf-ws-2", cut.Find("#badge-container-name").TextContent.Trim());
        Assert.Equal("nightcodeforge-sandbox:latest", cut.Find("#badge-container-image").TextContent.Trim());
        Assert.Equal("Running", cut.Find("#badge-container-status").TextContent.Trim());
        Assert.Equal("1000:1000", cut.Find("#badge-uid-gid").TextContent.Trim());
        Assert.Equal("WSL2: Ubuntu", cut.Find("#badge-wsl-distro").TextContent.Trim());

        var drift = cut.Find("#container-drift-warning");
        Assert.Contains("Image tag mismatch", drift.TextContent);

        var mountsTable = cut.Find("#configured-mounts-table");
        Assert.Contains("/home/user/clone", mountsTable.TextContent);
        Assert.Contains("/workspace/clone", mountsTable.TextContent);
        Assert.Contains("RW", mountsTable.TextContent);
        Assert.Contains("/cache/nuget", mountsTable.TextContent);
        Assert.Contains("RO", mountsTable.TextContent);

        Assert.Contains("/opt/ncf-dotnet-tools", cut.Find("#synthesized-path-value").TextContent);
    }

    [Fact]
    public async Task SelectingSystemNightwatchScope_InspectsWithNullWorkspaceAndRestrictedReadOnly()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });
        var workspaceState = new WorkspaceStateService();
        workspaceState.SetKnownWorkspaces([ws]);

        var diagMock = new Mock<IEnvironmentDiagnosticShellService>();
        diagMock
            .Setup(s => s.InspectEnvironmentAsync(
                It.Is<EnvironmentDiagnosticTarget>(t => t.ScopeKindOverride == SandboxScopeKind.WorkspaceClone),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleSnapshot(SandboxScopeKind.WorkspaceClone, ws.Id, ws.Name));

        diagMock
            .Setup(s => s.InspectEnvironmentAsync(
                It.Is<EnvironmentDiagnosticTarget>(t =>
                    t.ScopeKindOverride == SandboxScopeKind.SystemNightwatch &&
                    t.WorkspaceId == null &&
                    t.SecurityTier == SandboxSecurityTier.RestrictedReadOnly),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleSnapshot(
                SandboxScopeKind.SystemNightwatch,
                workspaceId: null,
                workspaceName: "System Nightwatch",
                containerName: "ncf-nightwatch-ro",
                securityTier: SandboxSecurityTier.RestrictedReadOnly));

        RegisterPageServices(diagMock.Object, workspaceRepo, new InMemoryTaskRepository(), workspaceState);

        var cut = Render<EnvironmentDiagnosticShell>(p => p.Add(x => x.WorkspaceId, ws.Id));
        await cut.Find("#scope-selector").ChangeAsync(new ChangeEventArgs { Value = DiagnosticScopeSelection.SystemNightwatch.ToString() });

        Assert.Equal("ncf-nightwatch-ro", cut.Find("#badge-container-name").TextContent.Trim());
        Assert.True(cut.Find("#workspace-selector").HasAttribute("disabled"));
        Assert.True(cut.Find("#security-tier-selector").HasAttribute("disabled"));

        diagMock.Verify(s => s.InspectEnvironmentAsync(
            It.Is<EnvironmentDiagnosticTarget>(t =>
                t.ScopeKindOverride == SandboxScopeKind.SystemNightwatch &&
                t.WorkspaceId == null &&
                t.SecurityTier == SandboxSecurityTier.RestrictedReadOnly),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SelectingTaskWorktreeScope_LoadsWorkspaceTasksAndInspectsWithTaskId()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });
        var taskRepo = new InMemoryTaskRepository();
        var task = await taskRepo.CreateAsync(new ForgeTask { WorkspaceId = ws.Id, Title = "Fix build pipeline" });

        var workspaceState = new WorkspaceStateService();
        workspaceState.SetKnownWorkspaces([ws]);

        var diagMock = new Mock<IEnvironmentDiagnosticShellService>();
        diagMock
            .Setup(s => s.InspectEnvironmentAsync(It.IsAny<EnvironmentDiagnosticTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EnvironmentDiagnosticTarget t, CancellationToken _) => CreateSampleSnapshot(
                t.ScopeKindOverride ?? SandboxScopeKind.WorkspaceClone,
                t.WorkspaceId,
                ws.Name,
                taskId: t.TaskId,
                containerName: t.TaskId.HasValue ? $"ncf-ws-{ws.Id}-task-{t.TaskId}" : $"ncf-ws-{ws.Id}"));

        RegisterPageServices(diagMock.Object, workspaceRepo, taskRepo, workspaceState);

        var cut = Render<EnvironmentDiagnosticShell>(p => p.Add(x => x.WorkspaceId, ws.Id));
        await cut.Find("#scope-selector").ChangeAsync(new ChangeEventArgs { Value = DiagnosticScopeSelection.TaskWorktree.ToString() });

        Assert.Contains($"#{task.Id}", cut.Find("#task-selector").TextContent);
        Assert.Equal($"ncf-ws-{ws.Id}-task-{task.Id}", cut.Find("#badge-container-name").TextContent.Trim());
    }

    [Fact]
    public async Task ClickingPresetButton_ExecutesPresetCommandAndRendersTranscript()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });
        var workspaceState = new WorkspaceStateService();
        workspaceState.SetKnownWorkspaces([ws]);

        var expectedPreset = DiagnosticShellUiHelper.Presets.Single(p => p.Id == "identity-directories");

        var diagMock = new Mock<IEnvironmentDiagnosticShellService>();
        diagMock
            .Setup(s => s.InspectEnvironmentAsync(It.IsAny<EnvironmentDiagnosticTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleSnapshot(SandboxScopeKind.WorkspaceClone, ws.Id, ws.Name));
        diagMock
            .Setup(s => s.ExecuteCommandAsync(It.IsAny<EnvironmentDiagnosticTarget>(), expectedPreset.Command, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiagnosticCommandResult(
                CommandLine: expectedPreset.Command,
                EffectiveWorkingDirectory: "/workspace/clone",
                ExitCode: 0,
                Output: "uid=1000(ncf) gid=1000(ncf)\n/workspace/clone",
                Error: string.Empty,
                Duration: TimeSpan.FromMilliseconds(45),
                IsTruncated: false,
                ExecutedAt: DateTimeOffset.UtcNow));

        RegisterPageServices(diagMock.Object, workspaceRepo, new InMemoryTaskRepository(), workspaceState);

        var cut = Render<EnvironmentDiagnosticShell>(p => p.Add(x => x.WorkspaceId, ws.Id));
        await cut.Find("[data-preset-id='identity-directories']").ClickAsync(new MouseEventArgs());

        var transcript = cut.Find("#diagnostic-transcript-body");
        Assert.Contains("uid=1000(ncf)", transcript.TextContent);
        Assert.Equal("Exit 0", cut.Find("[data-testid='entry-exit-badge']").TextContent.Trim());
        Assert.Equal("45 ms", cut.Find("[data-testid='entry-duration-badge']").TextContent.Trim());
    }

    [Fact]
    public async Task RunEnvironmentDoctor_And_RecreateContainer_RenderResultsAndRefreshInspection()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });
        var workspaceState = new WorkspaceStateService();
        workspaceState.SetKnownWorkspaces([ws]);

        var diagMock = new Mock<IEnvironmentDiagnosticShellService>();
        diagMock
            .Setup(s => s.InspectEnvironmentAsync(It.IsAny<EnvironmentDiagnosticTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleSnapshot(SandboxScopeKind.WorkspaceClone, ws.Id, ws.Name));
        diagMock
            .Setup(s => s.RunEnvironmentDoctorAsync(It.IsAny<EnvironmentDiagnosticTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new EnvironmentDoctorCheckItem("Identity", "UID/GID Mapping", DoctorCheckStatus.Passed, "uid=1000(ncf)", null),
                new EnvironmentDoctorCheckItem("Tools", "Companion CLIs", DoctorCheckStatus.Warning, "codecondenser missing", "which codecondenser -> 1"),
                new EnvironmentDoctorCheckItem("Mounts", "/cache/nuget", DoctorCheckStatus.Failed, "Permission denied", "touch failed")
            ]);
        diagMock
            .Setup(s => s.RecreateEnvironmentContainerAsync(It.IsAny<EnvironmentDiagnosticTarget>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        RegisterPageServices(diagMock.Object, workspaceRepo, new InMemoryTaskRepository(), workspaceState);

        var cut = Render<EnvironmentDiagnosticShell>(p => p.Add(x => x.WorkspaceId, ws.Id));
        await cut.Find("#btn-run-doctor").ClickAsync(new MouseEventArgs());

        var doctorCard = cut.Find("#environment-doctor-card");
        Assert.Contains("1 Passed", doctorCard.TextContent);
        Assert.Contains("1 Warnings", doctorCard.TextContent);
        Assert.Contains("1 Failed", doctorCard.TextContent);
        Assert.Contains("UID/GID Mapping", doctorCard.TextContent);
        Assert.Contains("which codecondenser -> 1", doctorCard.TextContent);

        await cut.Find("#btn-recreate-container").ClickAsync(new MouseEventArgs());
        diagMock.Verify(s => s.RecreateEnvironmentContainerAsync(It.IsAny<EnvironmentDiagnosticTarget>(), It.IsAny<CancellationToken>()), Times.Once);
        diagMock.Verify(s => s.InspectEnvironmentAsync(It.IsAny<EnvironmentDiagnosticTarget>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task LiveStreamingOutput_FiltersOutUnrelatedBackgroundCommands_AndShowsCancelState()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });
        var workspaceState = new WorkspaceStateService();
        workspaceState.SetKnownWorkspaces([ws]);

        var telemetry = CreateTelemetryService();
        var executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReturn = new TaskCompletionSource<DiagnosticCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var diagMock = new Mock<IEnvironmentDiagnosticShellService>();
        diagMock
            .Setup(s => s.InspectEnvironmentAsync(It.IsAny<EnvironmentDiagnosticTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleSnapshot(SandboxScopeKind.WorkspaceClone, ws.Id, ws.Name));
        diagMock
            .Setup(s => s.ExecuteCommandAsync(It.IsAny<EnvironmentDiagnosticTarget>(), "sleep 60", It.IsAny<CancellationToken>()))
            .Returns(async (EnvironmentDiagnosticTarget _, string cmd, CancellationToken ct) =>
            {
                ct.Register(() =>
                {
                    allowReturn.TrySetResult(new DiagnosticCommandResult(
                        CommandLine: cmd,
                        EffectiveWorkingDirectory: "/workspace/clone",
                        ExitCode: 130,
                        Output: "diag partial stdout\n",
                        Error: "Interrupted",
                        Duration: TimeSpan.FromMilliseconds(210),
                        IsTruncated: false,
                        ExecutedAt: DateTimeOffset.UtcNow));
                });

                executionStarted.TrySetResult();
                return await allowReturn.Task;
            });

        RegisterPageServices(diagMock.Object, workspaceRepo, new InMemoryTaskRepository(), workspaceState, telemetry);

        var cut = Render<EnvironmentDiagnosticShell>(p => p.Add(x => x.WorkspaceId, ws.Id));
        var input = cut.Find("#diagnostic-command-input");
        input.Input("sleep 60");

        var executeTask = cut.Find("#btn-execute-command").ClickAsync(new MouseEventArgs());
        await executionStarted.Task;

        // Running state: Execute disabled, Stop enabled.
        Assert.True(cut.Find("#btn-execute-command").HasAttribute("disabled"));
        Assert.False(cut.Find("#btn-stop-command").HasAttribute("disabled"));

        // Emit unrelated background agent command telemetry — must NOT leak into console.
        telemetry.RecordShellCommandStarted(new ShellCommandRecord(
            "agent-bg-cmd",
            "git status",
            "/workspace/clone",
            DateTime.UtcNow,
            0,
            null,
            ShellCommandStatus.Running,
            null,
            null));
        telemetry.UpdateShellCommandOutput("agent-bg-cmd", "SECRET_BACKGROUND_OUTPUT\n", false);

        // Emit matching active diagnostic command telemetry — MUST stream live.
        telemetry.RecordShellCommandStarted(new ShellCommandRecord(
            "diag-live-cmd",
            "sleep 60",
            "/workspace/clone",
            DateTime.UtcNow,
            0,
            null,
            ShellCommandStatus.Running,
            null,
            null));
        telemetry.UpdateShellCommandOutput("diag-live-cmd", "diag live chunk\n", false);

        cut.WaitForAssertion(() =>
        {
            var liveEntry = cut.Find("#live-streaming-entry");
            Assert.Contains("diag live chunk", liveEntry.TextContent);
            Assert.DoesNotContain("SECRET_BACKGROUND_OUTPUT", liveEntry.TextContent);
        });

        // Click Stop / SIGINT.
        await cut.Find("#btn-stop-command").ClickAsync(new MouseEventArgs());
        await executeTask;

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("SIGINT (130)", cut.Find("[data-testid='entry-exit-badge']").TextContent.Trim());
            Assert.Contains("diag partial stdout", cut.Find("#diagnostic-transcript-body").TextContent);
            Assert.True(cut.Find("#btn-stop-command").HasAttribute("disabled"));
        });

        // Verify Up-arrow restores the executed command from history.
        await cut.Find("#diagnostic-command-input").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" });
        Assert.Equal("sleep 60", cut.Find("#diagnostic-command-input").GetAttribute("value"));
    }

    [Fact]
    public async Task SwitchingTarget_WhenInspectionFails_ClearsStaleInspectionAndDisablesRecreate()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws1 = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });
        var ws2 = await workspaceRepo.CreateAsync(new Workspace { Name = "Beta Repo", RepositoryPath = "/repos/beta" });
        var workspaceState = new WorkspaceStateService();
        workspaceState.SetKnownWorkspaces([ws1, ws2]);

        var diagMock = new Mock<IEnvironmentDiagnosticShellService>();
        diagMock
            .Setup(s => s.InspectEnvironmentAsync(
                It.Is<EnvironmentDiagnosticTarget>(t => t.WorkspaceId == ws1.Id),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleSnapshot(SandboxScopeKind.WorkspaceClone, ws1.Id, ws1.Name, containerName: "ncf-ws-1"));
        diagMock
            .Setup(s => s.InspectEnvironmentAsync(
                It.Is<EnvironmentDiagnosticTarget>(t => t.WorkspaceId == ws2.Id),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Docker daemon unreachable"));

        RegisterPageServices(diagMock.Object, workspaceRepo, new InMemoryTaskRepository(), workspaceState);

        var cut = Render<EnvironmentDiagnosticShell>(p => p.Add(x => x.WorkspaceId, ws1.Id));
        Assert.Equal("ncf-ws-1", cut.Find("#badge-container-name").TextContent.Trim());
        Assert.False(cut.Find("#btn-recreate-container").HasAttribute("disabled"));

        await cut.Find("#workspace-selector").ChangeAsync(new ChangeEventArgs { Value = ws2.Id.ToString() });

        Assert.Contains("Docker daemon unreachable", cut.Find("#diagnostic-error-banner").TextContent);
        Assert.Empty(cut.FindAll("#badge-container-name"));
        Assert.True(cut.Find("#btn-recreate-container").HasAttribute("disabled"));
    }

    [Fact]
    public async Task RapidTargetSwitch_DiscardsStaleInspectionResponseFromEarlierTarget()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws1 = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });
        var ws2 = await workspaceRepo.CreateAsync(new Workspace { Name = "Beta Repo", RepositoryPath = "/repos/beta" });
        var workspaceState = new WorkspaceStateService();
        workspaceState.SetKnownWorkspaces([ws1, ws2]);

        var ws1Gate = new TaskCompletionSource<EnvironmentInspectionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ws1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var diagMock = new Mock<IEnvironmentDiagnosticShellService>();
        diagMock
            .Setup(s => s.InspectEnvironmentAsync(
                It.Is<EnvironmentDiagnosticTarget>(t => t.WorkspaceId == ws1.Id),
                It.IsAny<CancellationToken>()))
            .Returns((EnvironmentDiagnosticTarget _, CancellationToken _) =>
            {
                ws1Started.TrySetResult();
                return ws1Gate.Task;
            });
        diagMock
            .Setup(s => s.InspectEnvironmentAsync(
                It.Is<EnvironmentDiagnosticTarget>(t => t.WorkspaceId == ws2.Id),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleSnapshot(SandboxScopeKind.WorkspaceClone, ws2.Id, ws2.Name, containerName: "ncf-ws-2"));

        RegisterPageServices(diagMock.Object, workspaceRepo, new InMemoryTaskRepository(), workspaceState);

        var cut = Render<EnvironmentDiagnosticShell>(p => p.Add(x => x.WorkspaceId, ws1.Id));
        await ws1Started.Task;

        // Switch immediately to ws2 before ws1's inspection completes.
        await cut.Find("#workspace-selector").ChangeAsync(new ChangeEventArgs { Value = ws2.Id.ToString() });
        Assert.Equal("ncf-ws-2", cut.Find("#badge-container-name").TextContent.Trim());

        // Complete the stale ws1 request out of order — must NOT overwrite ws2's snapshot.
        ws1Gate.TrySetResult(CreateSampleSnapshot(SandboxScopeKind.WorkspaceClone, ws1.Id, ws1.Name, containerName: "ncf-ws-1-stale"));
        await Task.Yield();

        Assert.Equal("ncf-ws-2", cut.Find("#badge-container-name").TextContent.Trim());
    }

    [Fact]
    public async Task TypingAfterHistoryNavigation_ResetsCursorSoNextArrowUpRecallsLatest()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });
        var workspaceState = new WorkspaceStateService();
        workspaceState.SetKnownWorkspaces([ws]);

        var diagMock = new Mock<IEnvironmentDiagnosticShellService>();
        diagMock
            .Setup(s => s.InspectEnvironmentAsync(It.IsAny<EnvironmentDiagnosticTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleSnapshot(SandboxScopeKind.WorkspaceClone, ws.Id, ws.Name));
        diagMock
            .Setup(s => s.ExecuteCommandAsync(It.IsAny<EnvironmentDiagnosticTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EnvironmentDiagnosticTarget _, string cmd, CancellationToken _) => new DiagnosticCommandResult(
                CommandLine: cmd,
                EffectiveWorkingDirectory: "/workspace/clone",
                ExitCode: 0,
                Output: "ok",
                Error: string.Empty,
                Duration: TimeSpan.FromMilliseconds(10),
                IsTruncated: false,
                ExecutedAt: DateTimeOffset.UtcNow));

        RegisterPageServices(diagMock.Object, workspaceRepo, new InMemoryTaskRepository(), workspaceState);
        var cut = Render<EnvironmentDiagnosticShell>(p => p.Add(x => x.WorkspaceId, ws.Id));

        cut.Find("#diagnostic-command-input").Input("first-cmd");
        await cut.Find("#btn-execute-command").ClickAsync(new MouseEventArgs());
        cut.Find("#diagnostic-command-input").Input("second-cmd");
        await cut.Find("#btn-execute-command").ClickAsync(new MouseEventArgs());

        // Navigate up twice to reach "first-cmd".
        await cut.Find("#diagnostic-command-input").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" });
        await cut.Find("#diagnostic-command-input").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" });
        Assert.Equal("first-cmd", cut.Find("#diagnostic-command-input").GetAttribute("value"));

        // Manual edit must reset history navigation cursor.
        cut.Find("#diagnostic-command-input").Input("my custom draft");
        await cut.Find("#diagnostic-command-input").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" });
        Assert.Equal("second-cmd", cut.Find("#diagnostic-command-input").GetAttribute("value"));

        // ArrowDown restores the newly typed draft.
        await cut.Find("#diagnostic-command-input").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        Assert.Equal("my custom draft", cut.Find("#diagnostic-command-input").GetAttribute("value"));
    }

    [Fact]
    public async Task ClickingPresetButton_PreservesUnsentCommandInputDraft()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });
        var workspaceState = new WorkspaceStateService();
        workspaceState.SetKnownWorkspaces([ws]);

        var expectedPreset = DiagnosticShellUiHelper.Presets.Single(p => p.Id == "dotnet-icu-probe");
        var diagMock = new Mock<IEnvironmentDiagnosticShellService>();
        diagMock
            .Setup(s => s.InspectEnvironmentAsync(It.IsAny<EnvironmentDiagnosticTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleSnapshot(SandboxScopeKind.WorkspaceClone, ws.Id, ws.Name));
        diagMock
            .Setup(s => s.ExecuteCommandAsync(It.IsAny<EnvironmentDiagnosticTarget>(), expectedPreset.Command, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiagnosticCommandResult(
                CommandLine: expectedPreset.Command,
                EffectiveWorkingDirectory: "/workspace/clone",
                ExitCode: 0,
                Output: ".NET 10",
                Error: string.Empty,
                Duration: TimeSpan.FromMilliseconds(20),
                IsTruncated: false,
                ExecutedAt: DateTimeOffset.UtcNow));

        RegisterPageServices(diagMock.Object, workspaceRepo, new InMemoryTaskRepository(), workspaceState);
        var cut = Render<EnvironmentDiagnosticShell>(p => p.Add(x => x.WorkspaceId, ws.Id));

        cut.Find("#diagnostic-command-input").Input("unfinished command draft");
        await cut.Find("[data-preset-id='dotnet-icu-probe']").ClickAsync(new MouseEventArgs());

        Assert.Equal("unfinished command draft", cut.Find("#diagnostic-command-input").GetAttribute("value"));
        Assert.Contains(".NET 10", cut.Find("#diagnostic-transcript-body").TextContent);
    }

    [Fact]
    public async Task FallbackCancellation_WhenServiceThrowsOperationCanceledException_RecordsPartialOutputWithElapsedDuration()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });
        var workspaceState = new WorkspaceStateService();
        workspaceState.SetKnownWorkspaces([ws]);
        var telemetry = CreateTelemetryService();

        var executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagMock = new Mock<IEnvironmentDiagnosticShellService>();
        diagMock
            .Setup(s => s.InspectEnvironmentAsync(It.IsAny<EnvironmentDiagnosticTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleSnapshot(SandboxScopeKind.WorkspaceClone, ws.Id, ws.Name));
        diagMock
            .Setup(s => s.ExecuteCommandAsync(It.IsAny<EnvironmentDiagnosticTarget>(), "tail -f /tmp/log", It.IsAny<CancellationToken>()))
            .Returns(async (EnvironmentDiagnosticTarget _, string _, CancellationToken ct) =>
            {
                executionStarted.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                throw new InvalidOperationException("Should have been cancelled.");
            });

        RegisterPageServices(diagMock.Object, workspaceRepo, new InMemoryTaskRepository(), workspaceState, telemetry);
        var cut = Render<EnvironmentDiagnosticShell>(p => p.Add(x => x.WorkspaceId, ws.Id));

        cut.Find("#diagnostic-command-input").Input("tail -f /tmp/log");
        var executeTask = cut.Find("#btn-execute-command").ClickAsync(new MouseEventArgs());
        await executionStarted.Task;

        telemetry.RecordShellCommandStarted(new ShellCommandRecord(
            "tail-1",
            "tail -f /tmp/log",
            "/workspace/clone",
            DateTime.UtcNow,
            0,
            null,
            ShellCommandStatus.Running,
            null,
            null));
        telemetry.UpdateShellCommandOutput("tail-1", "streamed before cancel\n", false);

        await Task.Delay(25);
        await cut.Find("#btn-stop-command").ClickAsync(new MouseEventArgs());
        await executeTask;

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Cancelled (-1)", cut.Find("[data-testid='entry-exit-badge']").TextContent.Trim());
            Assert.Contains("streamed before cancel", cut.Find("#diagnostic-transcript-body").TextContent);
            Assert.NotEqual("0 ms", cut.Find("[data-testid='entry-duration-badge']").TextContent.Trim());
        });
    }

    [Fact]
    public async Task UnexpectedOperationCanceledException_WhenNotCallerCancelled_DisplaysErrorBanner()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });
        var workspaceState = new WorkspaceStateService();
        workspaceState.SetKnownWorkspaces([ws]);

        var diagMock = new Mock<IEnvironmentDiagnosticShellService>();
        diagMock
            .Setup(s => s.InspectEnvironmentAsync(It.IsAny<EnvironmentDiagnosticTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleSnapshot(SandboxScopeKind.WorkspaceClone, ws.Id, ws.Name));
        diagMock
            .Setup(s => s.ExecuteCommandAsync(It.IsAny<EnvironmentDiagnosticTarget>(), "echo test", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Internal backend token expired"));

        RegisterPageServices(diagMock.Object, workspaceRepo, new InMemoryTaskRepository(), workspaceState);
        var cut = Render<EnvironmentDiagnosticShell>(p => p.Add(x => x.WorkspaceId, ws.Id));

        cut.Find("#diagnostic-command-input").Input("echo test");
        await cut.Find("#btn-execute-command").ClickAsync(new MouseEventArgs());

        Assert.Contains("Internal backend token expired", cut.Find("#diagnostic-error-banner").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='entry-exit-badge']"));
    }

    [Fact]
    public async Task RelativeWorkingDirectoryOverride_CorrelatesLiveStreamAgainstResolvedWorkingDirectory()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });
        var workspaceState = new WorkspaceStateService();
        workspaceState.SetKnownWorkspaces([ws]);
        var telemetry = CreateTelemetryService();

        var executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFinish = new TaskCompletionSource<DiagnosticCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var diagMock = new Mock<IEnvironmentDiagnosticShellService>();
        diagMock
            .Setup(s => s.InspectEnvironmentAsync(It.IsAny<EnvironmentDiagnosticTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleSnapshot(SandboxScopeKind.WorkspaceClone, ws.Id, ws.Name));
        diagMock
            .Setup(s => s.ExecuteCommandAsync(
                It.Is<EnvironmentDiagnosticTarget>(t => t.WorkingDirectoryOverride == "src/App"),
                "ls",
                It.IsAny<CancellationToken>()))
            .Returns(async (EnvironmentDiagnosticTarget _, string _, CancellationToken _) =>
            {
                executionStarted.TrySetResult();
                return await allowFinish.Task;
            });

        RegisterPageServices(diagMock.Object, workspaceRepo, new InMemoryTaskRepository(), workspaceState, telemetry);
        var cut = Render<EnvironmentDiagnosticShell>(p => p.Add(x => x.WorkspaceId, ws.Id));

        cut.Find("#workdir-override-input").Change("src/App");
        cut.Find("#diagnostic-command-input").Input("ls");
        var executeTask = cut.Find("#btn-execute-command").ClickAsync(new MouseEventArgs());
        await executionStarted.Task;

        telemetry.RecordShellCommandStarted(new ShellCommandRecord(
            "rel-ls",
            "ls",
            "/workspace/clone/src/App",
            DateTime.UtcNow,
            0,
            null,
            ShellCommandStatus.Running,
            null,
            null));
        telemetry.UpdateShellCommandOutput("rel-ls", "Program.cs\n", false);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Program.cs", cut.Find("#live-streaming-entry").TextContent);
        });

        allowFinish.SetResult(new DiagnosticCommandResult(
            "ls",
            "/workspace/clone/src/App",
            0,
            "Program.cs\n",
            string.Empty,
            TimeSpan.FromMilliseconds(15),
            false,
            DateTimeOffset.UtcNow));
        await executeTask;
    }

    [Fact]
    public async Task WorkspaceMetadataUpdate_WithoutActiveWorkspaceChange_PreservesManualWorkspaceSelection()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws1 = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });
        var ws2 = await workspaceRepo.CreateAsync(new Workspace { Name = "Beta Repo", RepositoryPath = "/repos/beta" });
        var workspaceState = new WorkspaceStateService();
        workspaceState.SetKnownWorkspaces([ws1, ws2]);
        workspaceState.SetActiveWorkspace(ws1);

        var diagMock = new Mock<IEnvironmentDiagnosticShellService>();
        diagMock
            .Setup(s => s.InspectEnvironmentAsync(It.IsAny<EnvironmentDiagnosticTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EnvironmentDiagnosticTarget t, CancellationToken _) => CreateSampleSnapshot(
                SandboxScopeKind.WorkspaceClone,
                t.WorkspaceId,
                t.WorkspaceId == ws2.Id ? ws2.Name : ws1.Name,
                containerName: $"ncf-ws-{t.WorkspaceId}"));

        RegisterPageServices(diagMock.Object, workspaceRepo, new InMemoryTaskRepository(), workspaceState);
        var cut = Render<EnvironmentDiagnosticShell>(p => p.Add(x => x.WorkspaceId, ws1.Id));

        await cut.Find("#workspace-selector").ChangeAsync(new ChangeEventArgs { Value = ws2.Id.ToString() });
        Assert.Equal("ncf-ws-2", cut.Find("#badge-container-name").TextContent.Trim());

        // Metadata update for ws1 (ActiveWorkspaceId stays ws1.Id) must not snap selector back from ws2.
        ws1.Name = "Alpha Renamed";
        workspaceState.NotifyWorkspaceUpdated(ws1);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("ncf-ws-2", cut.Find("#badge-container-name").TextContent.Trim());
        });
    }

    [Fact]
    public void DiagnosticShellStyles_AreDefinedInAppCss_AndDoNotUseUnlinkedScopedCss()
    {
        var webRoot = WebProjectTestHelper.FindWebProjectRoot();
        var appCssPath = Path.Combine(webRoot, "wwwroot", "css", "app.css");
        var scopedCssPath = Path.Combine(webRoot, "Components", "Pages", "EnvironmentDiagnosticShell.razor.css");

        Assert.True(File.Exists(appCssPath), $"Missing app.css at {appCssPath}");
        Assert.False(File.Exists(scopedCssPath), $"Unlinked scoped CSS file must not exist because App.razor does not load *.styles.css: {scopedCssPath}");

        var appCss = File.ReadAllText(appCssPath);
        string[] requiredSelectors =
        [
            ".diag-shell-page",
            ".diag-target-grid",
            ".diag-preset-btn",
            ".diag-command-bar",
            ".forge-btn-danger"
        ];

        foreach (var selector in requiredSelectors)
        {
            Assert.True(
                WebProjectTestHelper.ContainsCssSelector(appCss, selector),
                $"Expected CSS selector '{selector}' to be defined in app.css");
        }
    }

    private void RegisterPageServices(
        IEnvironmentDiagnosticShellService diagService,
        IWorkspaceRepository workspaceRepo,
        ITaskRepository taskRepo,
        WorkspaceStateService workspaceState,
        IOperationsTelemetryService? telemetry = null)
    {
        Services.AddSingleton(diagService);
        Services.AddSingleton<IOperationsTelemetryService>(telemetry ?? CreateTelemetryService());
        Services.AddSingleton(workspaceRepo);
        Services.AddSingleton(taskRepo);
        Services.AddSingleton(workspaceState);
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(NullLogger<EnvironmentDiagnosticShell>.Instance);
    }

    private static OperationsTelemetryService CreateTelemetryService()
    {
        var mockHubContext = new Mock<IHubContext<OrchestratorHub, IOrchestratorHubClient>>();
        var mockClients = new Mock<IHubClients<IOrchestratorHubClient>>();
        mockClients.Setup(c => c.All).Returns(new Mock<IOrchestratorHubClient>().Object);
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);
        return new OperationsTelemetryService(mockHubContext.Object, NullLogger<OperationsTelemetryService>.Instance);
    }

    private static EnvironmentInspectionSnapshot CreateSampleSnapshot(
        SandboxScopeKind scopeKind,
        int? workspaceId,
        string workspaceName,
        int? taskId = null,
        string containerName = "ncf-ws-2",
        SandboxSecurityTier securityTier = SandboxSecurityTier.Standard,
        string? driftDescription = null) =>
        new(
            ScopeKind: scopeKind,
            WorkspaceId: workspaceId,
            WorkspaceName: workspaceName,
            ConfiguredMode: ExecutionMode.ContainerSandbox,
            ResolvedTier: ExecutionTier.ContainerSandbox,
            TaskId: taskId,
            SecurityTier: securityTier,
            DefaultWorkingDirectory: "/workspace/clone",
            ContainerName: containerName,
            ContainerImage: "nightcodeforge-sandbox:latest",
            IsContainerRunning: true,
            DriftDescription: driftDescription,
            UserUidGid: "1000:1000",
            UseWsl: true,
            WslDistribution: "Ubuntu",
            SynthesizedPath: "/opt/ncf-dotnet-tools:/usr/local/bin:/usr/bin",
            ConfiguredMounts:
            [
                new EnvironmentMountInfo("/home/user/clone", "/workspace/clone", IsReadOnly: false),
                new EnvironmentMountInfo("/home/user/nuget", "/cache/nuget", IsReadOnly: true)
            ]);
}
