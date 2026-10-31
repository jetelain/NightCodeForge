# 🖥️ Step 12.22A: Container Diagnostic Shell Service, Environment Doctor & `SIGINT` Cancellation Fix

> **Feature Implementation Step (Part 1 of 2 — Core/Infrastructure Diagnostic Service & Container Executor Hooks)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Step:* [Step 12.22B: Interactive Container Diagnostic Bash Console Web UI (`/diagnostics/shell`)](STEP_12_22B_DIAGNOSTIC_BASH_CONSOLE_WEB_UI.md)  
> *Related Steps:* [Step 11.21: Container ICU & Dynamic Timeouts](../archive/phase-11/STEP_11_21_CONTAINER_ICU_DYNAMIC_TIMEOUTS_AND_REVIEW_TESTS.md) | [Step 11.22: Container Git Worktree Relative Paths](../archive/phase-11/STEP_11_22_CONTAINER_GIT_WORKTREE_RELATIVE_PATHS.md) | [Step 11.26: Container Reuse Validation & Cleanup](../archive/phase-11/STEP_11_26_CONTAINER_REUSE_VALIDATION_AND_TASK_CLEANUP.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Agents`, `tests/NightCodeForge.Infrastructure.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

To eliminate "blind box" container debugging and prepare the backend for `/diagnostics/shell` ([Step 12.22B](STEP_12_22B_DIAGNOSTIC_BASH_CONSOLE_WEB_UI.md)), this step (Part 1 of Step 12.22) implements `IEnvironmentDiagnosticShellService` and hardens `NightCodeForgeContainerExecutor`:
1. **Dual-Pipeline Diagnostic Execution (`IEnvironmentDiagnosticShellService`):**
   - **Agent `bash` Tool Mode (`DiagnosticExecutionPipeline.AgentBashTool`):** Executes commands through [`CanonicalTools.BashAsync`](../../src/NightCodeForge.Agents/Tools/CanonicalTools.cs), exercising `/workspace` path remapping, `ReadOnlyCommandValidator`, dynamic build/test timeout escalation, and `HeadTailBuffer` truncation.
   - **Raw Execution Environment Mode (`DiagnosticExecutionPipeline.RawExecutionEnvironment`):** Executes directly on `IExecutionEnvironment.ExecuteAsync` with working directory and timeout overrides so OS/container-level enforcement (`:ro` mounts, `--network none`, `uid:gid`) can be probed directly.
2. **Container Inspection, `-ro` Container Isolation & Reset Hooks:**
   - Expose resolved container metadata, `ComputePathEnvironmentVariable`, `BuildVolumeMounts`, and `DetectContainerDrift()` from [`NightCodeForgeContainerExecutor`](../../src/NightCodeForge.Infrastructure/Execution/NightCodeForgeContainerExecutor.cs).
   - Append a `-ro` suffix to `containerName` in `ExecutionEnvironmentProvider.GetEnvironment` when `securityTier == SandboxSecurityTier.RestrictedReadOnly` so probing `RestrictedReadOnly` never triggers drift destruction of the active `Standard` container.
3. **Explicit `CancellationToken` `SIGINT` Recovery in `NightCodeForgeContainerExecutor.ExecuteAsync`:**
   - Update `ExecuteAsync` so that when `OperationCanceledException` is thrown while `ReadUntilSentinelAsync` is awaiting command completion, the executor sends `\x03` (`SIGINT`) and drains the sentinel (with a 2-second grace window, falling back to `ForceRestartShellProcess()`) before releasing `_execLock`.
4. **Automated "Environment Doctor" (`RunEnvironmentDoctorAsync`):**
   - Execute a single-round-trip diagnostic script verifying user identity & writable `$HOME`, writable `/tmp` & package caches, workspace/clone mount integrity (`/workspace/clone:ro` and `.git:rw`), Git worktree status, SDK/companion tool availability (`git`, `rg`, `dotnet`, `csnav`, `mdfetch`, `codecondenser`), and `libicu` globalization readiness.

---

## 2. Service Contracts & Technical Specification

```csharp
namespace NightCodeForge.Core.Interfaces;

public enum DiagnosticExecutionPipeline
{
    AgentBashTool = 0,
    RawExecutionEnvironment = 1
}

public record EnvironmentDiagnosticTarget(
    int WorkspaceId,
    int? TaskId = null,
    SandboxSecurityTier SecurityTier = SandboxSecurityTier.Standard,
    DiagnosticExecutionPipeline Pipeline = DiagnosticExecutionPipeline.AgentBashTool,
    string? WorkingDirectoryOverride = null,
    int? TimeoutSecondsOverride = null);

public record EnvironmentMountInfo(
    string HostPath,
    string ContainerPath,
    bool IsReadOnly);

public record EnvironmentInspectionSnapshot(
    int WorkspaceId,
    string WorkspaceName,
    ExecutionMode ConfiguredMode,
    ExecutionTier ResolvedTier,
    int? TaskId,
    SandboxSecurityTier SecurityTier,
    string DefaultWorkingDirectory,
    string? ContainerName,
    string? ContainerImage,
    bool? IsContainerRunning,
    string? DriftDescription,
    string? UserUidGid,
    bool UseWsl,
    string? WslDistribution,
    string SynthesizedPath,
    IReadOnlyList<EnvironmentMountInfo> ConfiguredMounts);

public record DiagnosticCommandResult(
    string CommandLine,
    string EffectiveWorkingDirectory,
    int ExitCode,
    string Output,
    string Error,
    TimeSpan Duration,
    bool IsTruncated,
    DateTimeOffset ExecutedAt);

public enum DoctorCheckStatus { Passed, Warning, Failed }

public record EnvironmentDoctorCheckItem(
    string Category,
    string Name,
    DoctorCheckStatus Status,
    string Summary,
    string? DiagnosticOutput);

public interface IEnvironmentDiagnosticShellService
{
    Task<EnvironmentInspectionSnapshot> InspectEnvironmentAsync(
        EnvironmentDiagnosticTarget target,
        CancellationToken cancellationToken = default);

    Task<DiagnosticCommandResult> ExecuteCommandAsync(
        EnvironmentDiagnosticTarget target,
        string command,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EnvironmentDoctorCheckItem>> RunEnvironmentDoctorAsync(
        EnvironmentDiagnosticTarget target,
        CancellationToken cancellationToken = default);

    Task RecreateEnvironmentContainerAsync(
        EnvironmentDiagnosticTarget target,
        CancellationToken cancellationToken = default);
}
```

---

## 3. Definition of Done (DoD) Checklist

- [ ] Define `IEnvironmentDiagnosticShellService` and its data records in `NightCodeForge.Core`.
- [ ] Expose container inspection metadata and container reset/recreation helpers on `NightCodeForgeContainerExecutor` and `IExecutionEnvironmentProvider`.
- [ ] Append `-ro` suffix to `containerName` when `securityTier == SandboxSecurityTier.RestrictedReadOnly` in `ExecutionEnvironmentProvider.GetEnvironment`.
- [ ] Update `NightCodeForgeContainerExecutor.ExecuteAsync` so `OperationCanceledException` during `ReadUntilSentinelAsync` sends `\x03` (`SIGINT`) and drains the sentinel (or restarts the shell process) before releasing `_execLock`.
- [ ] Implement `EnvironmentDiagnosticShellService` supporting `InspectEnvironmentAsync`, `ExecuteCommandAsync` (`AgentBashTool` and `RawExecutionEnvironment`), `RunEnvironmentDoctorAsync`, and `RecreateEnvironmentContainerAsync`.
- [ ] Add unit tests in `NightCodeForge.Infrastructure.Tests` covering both execution pipelines, `-ro` container naming, `CancellationToken` `SIGINT` recovery, doctor check parsing, and container recreation.

