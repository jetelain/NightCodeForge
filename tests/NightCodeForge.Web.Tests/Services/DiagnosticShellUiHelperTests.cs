using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests.Services;

public class DiagnosticShellUiHelperTests
{
    [Fact]
    public void Presets_ContainAllSixExpectedDiagnosticCommands()
    {
        Assert.Equal(6, DiagnosticShellUiHelper.Presets.Count);

        var names = DiagnosticShellUiHelper.Presets.Select(p => p.Name).ToList();
        Assert.Equal(
            [
                "Identity & Directories",
                "Environment & PATH",
                "SDKs & Companion Tools",
                "Mount & Write Probe",
                "Git & Worktree Status",
                ".NET & ICU Probe"
            ],
            names);

        var envPath = DiagnosticShellUiHelper.Presets.Single(p => p.Id == "environment-path");
        Assert.Equal("echo \"PATH=$PATH\" && env | sort", envPath.Command);

        var dotnetProbe = DiagnosticShellUiHelper.Presets.Single(p => p.Id == "dotnet-icu-probe");
        Assert.Equal("dotnet --info", dotnetProbe.Command);
    }

    [Fact]
    public void BuildTarget_WorkspaceCanonical_ClearsTaskIdAndSetsWorkspaceCloneScope()
    {
        var target = DiagnosticShellUiHelper.BuildTarget(
            DiagnosticScopeSelection.WorkspaceCanonical,
            workspaceId: 7,
            taskId: 42,
            securityTier: SandboxSecurityTier.Standard,
            pipeline: DiagnosticExecutionPipeline.AgentBashTool,
            workingDirectoryOverride: "  /workspace/clone/src  ",
            timeoutSecondsOverride: 30);

        Assert.Equal(7, target.WorkspaceId);
        Assert.Null(target.TaskId);
        Assert.Equal(SandboxScopeKind.WorkspaceClone, target.ScopeKindOverride);
        Assert.Equal(SandboxSecurityTier.Standard, target.SecurityTier);
        Assert.Equal(DiagnosticExecutionPipeline.AgentBashTool, target.Pipeline);
        Assert.Equal("/workspace/clone/src", target.WorkingDirectoryOverride);
        Assert.Equal(30, target.TimeoutSecondsOverride);
    }

    [Fact]
    public void BuildTarget_TaskWorktree_PreservesWorkspaceAndTaskIds()
    {
        var target = DiagnosticShellUiHelper.BuildTarget(
            DiagnosticScopeSelection.TaskWorktree,
            workspaceId: 3,
            taskId: 19,
            securityTier: SandboxSecurityTier.RestrictedReadOnly,
            pipeline: DiagnosticExecutionPipeline.RawExecutionEnvironment,
            workingDirectoryOverride: "   ",
            timeoutSecondsOverride: 0);

        Assert.Equal(3, target.WorkspaceId);
        Assert.Equal(19, target.TaskId);
        Assert.Equal(SandboxScopeKind.TaskWorktree, target.ScopeKindOverride);
        Assert.Equal(SandboxSecurityTier.RestrictedReadOnly, target.SecurityTier);
        Assert.Equal(DiagnosticExecutionPipeline.RawExecutionEnvironment, target.Pipeline);
        Assert.Null(target.WorkingDirectoryOverride);
        Assert.Null(target.TimeoutSecondsOverride);
    }

    [Fact]
    public void BuildTarget_SystemNightwatch_EnforcesNullWorkspaceAndRestrictedReadOnly()
    {
        var target = DiagnosticShellUiHelper.BuildTarget(
            DiagnosticScopeSelection.SystemNightwatch,
            workspaceId: 99,
            taskId: 15,
            securityTier: SandboxSecurityTier.Standard,
            pipeline: DiagnosticExecutionPipeline.RawExecutionEnvironment);

        Assert.Null(target.WorkspaceId);
        Assert.Null(target.TaskId);
        Assert.Equal(SandboxScopeKind.SystemNightwatch, target.ScopeKindOverride);
        Assert.Equal(SandboxSecurityTier.RestrictedReadOnly, target.SecurityTier);
        Assert.Equal(DiagnosticExecutionPipeline.RawExecutionEnvironment, target.Pipeline);
    }

    [Fact]
    public void ValidateTarget_ValidatesRequiredIds()
    {
        Assert.True(DiagnosticShellUiHelper.ValidateTarget(DiagnosticScopeSelection.SystemNightwatch, null, null).IsValid);
        Assert.True(DiagnosticShellUiHelper.ValidateTarget(DiagnosticScopeSelection.WorkspaceCanonical, 1, null).IsValid);
        Assert.False(DiagnosticShellUiHelper.ValidateTarget(DiagnosticScopeSelection.WorkspaceCanonical, null, null).IsValid);
        Assert.False(DiagnosticShellUiHelper.ValidateTarget(DiagnosticScopeSelection.TaskWorktree, 1, null).IsValid);
        Assert.True(DiagnosticShellUiHelper.ValidateTarget(DiagnosticScopeSelection.TaskWorktree, 1, 5).IsValid);
    }

    [Fact]
    public void FormattingHelpers_ReturnExpectedBadgesAndSummaries()
    {
        Assert.Equal(("Running", "badge-teal"), (DiagnosticShellUiHelper.FormatContainerLiveness(true).Label, DiagnosticShellUiHelper.FormatContainerLiveness(true).CssClass));
        Assert.Equal(("Stopped", "badge-crimson"), (DiagnosticShellUiHelper.FormatContainerLiveness(false).Label, DiagnosticShellUiHelper.FormatContainerLiveness(false).CssClass));
        Assert.Equal(("N/A", "badge-gray"), (DiagnosticShellUiHelper.FormatContainerLiveness(null).Label, DiagnosticShellUiHelper.FormatContainerLiveness(null).CssClass));

        Assert.Equal("ContainerSandbox", DiagnosticShellUiHelper.FormatExecutionTier(ExecutionTier.ContainerSandbox).Label);
        Assert.Equal("WSL2: Ubuntu", DiagnosticShellUiHelper.FormatWslBadge(true, "Ubuntu"));
        Assert.Equal("Native Host", DiagnosticShellUiHelper.FormatWslBadge(false, null));
        Assert.Equal("RO", DiagnosticShellUiHelper.FormatMountMode(true).Label);
        Assert.Equal("RW", DiagnosticShellUiHelper.FormatMountMode(false).Label);

        Assert.Equal("42 ms", DiagnosticShellUiHelper.FormatDuration(TimeSpan.FromMilliseconds(42)));
        Assert.Equal("1.25 s", DiagnosticShellUiHelper.FormatDuration(TimeSpan.FromMilliseconds(1250)));
        Assert.Equal("1m 12s", DiagnosticShellUiHelper.FormatDuration(TimeSpan.FromSeconds(72)));

        Assert.Equal(("Exit 0", "badge-teal"), (DiagnosticShellUiHelper.FormatExitCodeBadge(0).Label, DiagnosticShellUiHelper.FormatExitCodeBadge(0).CssClass));
        Assert.Equal(("SIGINT (130)", "badge-amber"), (DiagnosticShellUiHelper.FormatExitCodeBadge(130).Label, DiagnosticShellUiHelper.FormatExitCodeBadge(130).CssClass));
        Assert.Equal(("Cancelled (-1)", "badge-amber"), (DiagnosticShellUiHelper.FormatExitCodeBadge(-1).Label, DiagnosticShellUiHelper.FormatExitCodeBadge(-1).CssClass));
        Assert.Equal(("Exit 2", "badge-crimson"), (DiagnosticShellUiHelper.FormatExitCodeBadge(2).Label, DiagnosticShellUiHelper.FormatExitCodeBadge(2).CssClass));

        var pathParts = DiagnosticShellUiHelper.SplitSynthesizedPath("/usr/local/bin:/usr/bin:");
        Assert.Equal(["/usr/local/bin", "/usr/bin"], pathParts);

        var checks = new List<EnvironmentDoctorCheckItem>
        {
            new("Identity", "UID/GID", DoctorCheckStatus.Passed, "OK", null),
            new("Tools", "csnav", DoctorCheckStatus.Warning, "Missing", "which csnav failed"),
            new("Mounts", "/workspace", DoctorCheckStatus.Failed, "Not writable", "chmod denied")
        };
        var summary = DiagnosticShellUiHelper.SummarizeDoctorChecks(checks);
        Assert.Equal(new DiagnosticDoctorSummary(1, 1, 1, 3), summary);
        Assert.Equal("Passed", DiagnosticShellUiHelper.FormatDoctorStatus(DoctorCheckStatus.Passed).Label);
        Assert.Equal("Warning", DiagnosticShellUiHelper.FormatDoctorStatus(DoctorCheckStatus.Warning).Label);
        Assert.Equal("Failed", DiagnosticShellUiHelper.FormatDoctorStatus(DoctorCheckStatus.Failed).Label);
    }

    [Fact]
    public void CommandHistory_RecordsDeduplicatesAndNavigatesWithDraftPreservation()
    {
        var history = new DiagnosticCommandHistory(maxCapacity: 3);

        history.Record("   ");
        Assert.Empty(history.Entries);

        history.Record("id");
        history.Record("id");
        history.Record("pwd");
        history.Record("ls -la");
        history.Record("dotnet --info");

        Assert.Equal(["pwd", "ls -la", "dotnet --info"], history.Entries);

        Assert.Equal("dotnet --info", history.NavigateUp("echo draft"));
        Assert.Equal("ls -la", history.NavigateUp("dotnet --info"));
        Assert.Equal("pwd", history.NavigateUp("ls -la"));
        Assert.Equal("pwd", history.NavigateUp("pwd"));

        Assert.Equal("ls -la", history.NavigateDown("pwd"));
        Assert.Equal("dotnet --info", history.NavigateDown("ls -la"));
        Assert.Equal("echo draft", history.NavigateDown("dotnet --info"));
    }

    [Fact]
    public void LiveStreamCorrelator_FiltersBackgroundCommandsAndIsolatesActiveExecution()
    {
        var correlator = new DiagnosticLiveStreamCorrelator();

        var unrelatedRecord = new ShellCommandRecord(
            "bg-1",
            "git fetch --all",
            "/workspace/clone",
            DateTime.UtcNow,
            0,
            null,
            ShellCommandStatus.Running,
            null,
            null);

        Assert.False(correlator.HandleShellCommandChanged(unrelatedRecord));
        Assert.False(correlator.HandleShellCommandOutput("bg-1", "background output\n", false));

        correlator.BeginExecution("echo hello", "/workspace/clone");

        // Unrelated background agent command must be ignored while diagnostic command is pending.
        Assert.False(correlator.HandleShellCommandChanged(unrelatedRecord));
        Assert.False(correlator.HandleShellCommandOutput("bg-1", "should not leak\n", false));

        // Same command in a different working directory must also be ignored.
        var otherWorkdirRecord = new ShellCommandRecord(
            "bg-2",
            "echo hello",
            "/other/repo",
            DateTime.UtcNow,
            0,
            null,
            ShellCommandStatus.Running,
            null,
            null);
        Assert.False(correlator.HandleShellCommandChanged(otherWorkdirRecord));

        // Matching running diagnostic command binds TrackedCommandId.
        var diagRecord = new ShellCommandRecord(
            "diag-1",
            "echo hello",
            "/workspace/clone/",
            DateTime.UtcNow,
            0,
            null,
            ShellCommandStatus.Running,
            null,
            null);
        Assert.True(correlator.HandleShellCommandChanged(diagRecord));

        // Background output still filtered out even after binding.
        Assert.False(correlator.HandleShellCommandOutput("bg-1", "still ignored\n", false));

        // Active command stdout & stderr are captured and coalesced by stream type.
        Assert.True(correlator.HandleShellCommandOutput("diag-1", "hello ", false));
        Assert.True(correlator.HandleShellCommandOutput("diag-1", "world\n", false));
        Assert.True(correlator.HandleShellCommandOutput("diag-1", "warn line\n", true));

        var snapshot = correlator.GetSnapshot();
        Assert.True(snapshot.IsActive);
        Assert.Equal("diag-1", snapshot.TrackedCommandId);
        Assert.Equal(2, snapshot.Chunks.Count);
        Assert.Equal(new DiagnosticStreamChunk("hello world\n", false), snapshot.Chunks[0]);
        Assert.Equal(new DiagnosticStreamChunk("warn line\n", true), snapshot.Chunks[1]);

        correlator.EndExecution();
        var cleared = correlator.GetSnapshot();
        Assert.False(cleared.IsActive);
        Assert.Null(cleared.TrackedCommandId);
        Assert.Empty(cleared.Chunks);
    }

    [Fact]
    public void BuildTarget_ClampsTimeoutAboveMaxAndIgnoresNonPositiveValues()
    {
        var clamped = DiagnosticShellUiHelper.BuildTarget(
            DiagnosticScopeSelection.WorkspaceCanonical,
            workspaceId: 1,
            taskId: null,
            SandboxSecurityTier.Standard,
            DiagnosticExecutionPipeline.RawExecutionEnvironment,
            timeoutSecondsOverride: DiagnosticShellUiHelper.MaxTimeoutSeconds + 500);

        Assert.Equal(DiagnosticShellUiHelper.MaxTimeoutSeconds, clamped.TimeoutSecondsOverride);

        var negative = DiagnosticShellUiHelper.BuildTarget(
            DiagnosticScopeSelection.WorkspaceCanonical,
            workspaceId: 1,
            taskId: null,
            SandboxSecurityTier.Standard,
            DiagnosticExecutionPipeline.RawExecutionEnvironment,
            timeoutSecondsOverride: -5);

        Assert.Null(negative.TimeoutSecondsOverride);
    }

    [Fact]
    public void ResolveExpectedWorkingDirectory_ResolvesRelativeOverridesAndNormalizesPaths()
    {
        Assert.Equal(
            "/workspace/clone",
            DiagnosticShellUiHelper.ResolveExpectedWorkingDirectory(null, "/workspace/clone"));
        Assert.Equal(
            "/workspace/clone/src/App",
            DiagnosticShellUiHelper.ResolveExpectedWorkingDirectory("src/App", "/workspace/clone"));
        Assert.Equal(
            "/workspace/clone/lib",
            DiagnosticShellUiHelper.ResolveExpectedWorkingDirectory("./src/../lib/", "/workspace/clone/"));
        Assert.Equal(
            "/etc/nginx",
            DiagnosticShellUiHelper.ResolveExpectedWorkingDirectory("/etc/nginx/", "/workspace/clone"));
        Assert.Equal(
            "~/scripts",
            DiagnosticShellUiHelper.ResolveExpectedWorkingDirectory("~/scripts/", "/workspace/clone"));
        Assert.Equal(
            "C:/repos/app/sub",
            DiagnosticShellUiHelper.ResolveExpectedWorkingDirectory(@"src\..\sub", @"C:\repos\app"));
    }

    [Fact]
    public void BuildFallbackCancelledResult_AggregatesStreamChunksAndUsesElapsedDuration()
    {
        var snapshot = new DiagnosticLiveStreamSnapshot(
            IsActive: true,
            TrackedCommandId: "cmd-1",
            Command: "sleep 30",
            Chunks:
            [
                new DiagnosticStreamChunk("line 1\n", false),
                new DiagnosticStreamChunk("line 2\n", false),
                new DiagnosticStreamChunk("err 1\n", true)
            ]);

        var elapsed = TimeSpan.FromMilliseconds(345);
        var timestamp = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

        var result = DiagnosticShellUiHelper.BuildFallbackCancelledResult(
            "sleep 30",
            "/workspace/clone",
            snapshot,
            elapsed,
            timestamp);

        Assert.Equal(DiagnosticShellUiHelper.CancelledExitCode, result.ExitCode);
        Assert.Equal("line 1\nline 2\n", result.Output);
        Assert.Equal("err 1\n", result.Error);
        Assert.Equal(elapsed, result.Duration);
        Assert.Equal("/workspace/clone", result.EffectiveWorkingDirectory);
        Assert.Equal(timestamp, result.ExecutedAt);

        var emptyFallback = DiagnosticShellUiHelper.BuildFallbackCancelledResult(
            "sleep 30",
            null,
            new DiagnosticLiveStreamSnapshot(true, null, "sleep 30", Array.Empty<DiagnosticStreamChunk>()),
            TimeSpan.FromMilliseconds(-10),
            timestamp);

        Assert.Equal(DiagnosticShellUiHelper.DefaultFallbackWorkingDirectory, emptyFallback.EffectiveWorkingDirectory);
        Assert.Equal(DiagnosticShellUiHelper.DefaultCancelledErrorMessage, emptyFallback.Error);
        Assert.Equal(TimeSpan.Zero, emptyFallback.Duration);
    }

    [Fact]
    public void BuildInspectionKey_RespectsScopeInvariants()
    {
        var nightwatchA = DiagnosticShellUiHelper.BuildInspectionKey(
            DiagnosticScopeSelection.SystemNightwatch, 1, 9, SandboxSecurityTier.RestrictedReadOnly);
        var nightwatchB = DiagnosticShellUiHelper.BuildInspectionKey(
            DiagnosticScopeSelection.SystemNightwatch, 2, 10, SandboxSecurityTier.RestrictedReadOnly);
        Assert.Equal(nightwatchA, nightwatchB);

        var canonicalA = DiagnosticShellUiHelper.BuildInspectionKey(
            DiagnosticScopeSelection.WorkspaceCanonical, 3, 5, SandboxSecurityTier.Standard);
        var canonicalB = DiagnosticShellUiHelper.BuildInspectionKey(
            DiagnosticScopeSelection.WorkspaceCanonical, 3, 99, SandboxSecurityTier.Standard);
        Assert.Equal(canonicalA, canonicalB);

        var taskA = DiagnosticShellUiHelper.BuildInspectionKey(
            DiagnosticScopeSelection.TaskWorktree, 3, 5, SandboxSecurityTier.Standard);
        var taskB = DiagnosticShellUiHelper.BuildInspectionKey(
            DiagnosticScopeSelection.TaskWorktree, 3, 6, SandboxSecurityTier.Standard);
        Assert.NotEqual(taskA, taskB);
    }

    [Fact]
    public void LiveStreamCorrelator_RejectsSubstringFalsePositivesFromBackgroundCommands()
    {
        var correlator = new DiagnosticLiveStreamCorrelator();
        correlator.BeginExecution("id", "/workspace/clone");

        // Substring collisions that previously cross-talked into the console.
        var taskIdEcho = new ShellCommandRecord(
            "bg-substring-1",
            "echo \"task_id=42\"",
            "/workspace/clone",
            DateTime.UtcNow,
            0,
            null,
            ShellCommandStatus.Running,
            null,
            null);
        Assert.False(correlator.HandleShellCommandChanged(taskIdEcho));
        Assert.False(correlator.HandleShellCommandOutput("bg-substring-1", "task_id=42\n", false));

        var wrappedMismatch = new ShellCommandRecord(
            "bg-substring-2",
            "wsl.exe -d Ubuntu -- bash -c cd \"/workspace/clone\" && echo \"valid\"",
            "C:\\host",
            DateTime.UtcNow,
            0,
            null,
            ShellCommandStatus.Running,
            null,
            null);
        Assert.False(correlator.HandleShellCommandChanged(wrappedMismatch));

        // Exact command match must bind.
        var exact = new ShellCommandRecord(
            "diag-id",
            "id",
            "/workspace/clone",
            DateTime.UtcNow,
            0,
            null,
            ShellCommandStatus.Running,
            null,
            null);
        Assert.True(correlator.HandleShellCommandChanged(exact));
    }

    [Fact]
    public void LiveStreamCorrelator_CorrelatesWslAndBashWrappedCommandsAndFiltersWrongWorkDir()
    {
        var correlator = new DiagnosticLiveStreamCorrelator();
        correlator.BeginExecution("dotnet --info", @"C:\repos\alpha");

        // Wrapped WSL command targeting a different repo must be rejected.
        var wrongDirWsl = new ShellCommandRecord(
            "wsl-other",
            "wsl.exe -d Ubuntu -- bash -c cd \"/mnt/c/repos/beta\" && dotnet --info",
            @"C:\app",
            DateTime.UtcNow,
            0,
            null,
            ShellCommandStatus.Running,
            null,
            null);
        Assert.False(correlator.HandleShellCommandChanged(wrongDirWsl));

        // Wrapped WSL command targeting the expected repo (/mnt/c/repos/alpha == C:\repos\alpha) must correlate.
        var matchingWsl = new ShellCommandRecord(
            "wsl-match",
            "wsl.exe -d Ubuntu -- bash -c mkdir -p \"/mnt/c/repos/alpha\" && cd \"/mnt/c/repos/alpha\" && dotnet --info",
            @"C:\app",
            DateTime.UtcNow,
            0,
            null,
            ShellCommandStatus.Running,
            null,
            null);
        Assert.True(correlator.HandleShellCommandChanged(matchingWsl));
        Assert.True(correlator.HandleShellCommandOutput("wsl-match", ".NET SDK 10\n", false));

        correlator.EndExecution();

        // Also verify /bin/bash -c cd "..." && ... wrapper.
        correlator.BeginExecution("pwd", "/workspace/clone");
        var bashWrapped = new ShellCommandRecord(
            "bash-match",
            "/bin/bash -c cd \"/workspace/clone\" && pwd",
            "/host/fallback",
            DateTime.UtcNow,
            0,
            null,
            ShellCommandStatus.Running,
            null,
            null);
        Assert.True(correlator.HandleShellCommandChanged(bashWrapped));
    }

    [Fact]
    public void LiveStreamCorrelator_ReturnsFalseOnceMaxBufferCapacityIsReached()
    {
        var correlator = new DiagnosticLiveStreamCorrelator();
        correlator.BeginExecution("yes", "/workspace/clone");

        Assert.True(correlator.HandleShellCommandChanged(new ShellCommandRecord(
            "cmd-buf",
            "yes",
            "/workspace/clone",
            DateTime.UtcNow,
            0,
            null,
            ShellCommandStatus.Running,
            null,
            null)));

        var fillChunk = new string('x', DiagnosticLiveStreamCorrelator.MaxBufferedChars);
        Assert.True(correlator.HandleShellCommandOutput("cmd-buf", fillChunk, false));
        Assert.False(correlator.HandleShellCommandOutput("cmd-buf", "overflow", false));
    }
}
