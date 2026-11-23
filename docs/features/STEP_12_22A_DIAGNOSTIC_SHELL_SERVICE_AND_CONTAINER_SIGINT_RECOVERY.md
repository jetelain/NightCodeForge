# 🖥️ Step 12.22A: Container Diagnostic Shell Service, Environment Doctor & `SIGINT` Cancellation Fix

> **Feature Implementation Step (Part 1 of 2 — Core/Infrastructure Diagnostic Service & Container Executor Hooks)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Steps:* [Step 12.36A: Declarative Sandbox Mount Table & Container Lifecycle Redesign](../archive/phase-12/STEP_12_36A_DECLARATIVE_SANDBOX_MOUNT_TABLE_AND_CONTAINER_LIFECYCLE_REDESIGN.md) | [Step 12.36B: `CanonicalTools` Modular Decomposition & Interruption Pipeline](../archive/phase-12/STEP_12_36B_CANONICAL_TOOLS_MODULAR_DECOMPOSITION_AND_INTERRUPTION_PIPELINE.md)  
> *Companion Step:* [Step 12.22B: Interactive Container Diagnostic Bash Console Web UI (`/diagnostics/shell`)](STEP_12_22B_DIAGNOSTIC_BASH_CONSOLE_WEB_UI.md)  
> *Related Steps:* [Step 11.21: Container ICU & Dynamic Timeouts](../archive/phase-11/STEP_11_21_CONTAINER_ICU_DYNAMIC_TIMEOUTS_AND_REVIEW_TEST_MANDATE.md) | [Step 11.22: Container Git Worktree Relative Paths](../archive/phase-11/STEP_11_22_CONTAINER_GIT_WORKTREE_RELATIVE_PATHS_AND_PORTABILITY.md) | [Step 11.26: Container Reuse Validation & Cleanup](../archive/phase-11/STEP_11_26_CONTAINER_REUSE_VALIDATION_AND_TASK_LIFECYCLE_CLEANUP.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Agents`, `tests/NightCodeForge.Infrastructure.Tests`, `tests/NightCodeForge.Agents.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

To eliminate "blind box" container debugging and prepare the backend for `/diagnostics/shell` ([Step 12.22B](STEP_12_22B_DIAGNOSTIC_BASH_CONSOLE_WEB_UI.md)), this step (Part 1 of Step 12.22) builds atop the declarative `SandboxMountTable`, `-ro` container isolation, and `PersistentContainerShellSession` `\x03` `SIGINT` recovery introduced in [Step 12.36A](../archive/phase-12/STEP_12_36A_DECLARATIVE_SANDBOX_MOUNT_TABLE_AND_CONTAINER_LIFECYCLE_REDESIGN.md) and `SandboxBashToolModule` from [Step 12.36B](../archive/phase-12/STEP_12_36B_CANONICAL_TOOLS_MODULAR_DECOMPOSITION_AND_INTERRUPTION_PIPELINE.md) to implement `IEnvironmentDiagnosticShellService` (in `NightCodeForge.Agents`):
1. **Dual-Pipeline Diagnostic Execution (`IEnvironmentDiagnosticShellService`):**
   - **Agent `bash` Tool Mode (`DiagnosticExecutionPipeline.AgentBashTool`):** Executes commands through `SandboxBashToolModule` ([Step 12.36B](../archive/phase-12/STEP_12_36B_CANONICAL_TOOLS_MODULAR_DECOMPOSITION_AND_INTERRUPTION_PIPELINE.md), via a structured companion helper that preserves `ExitCode`, `IsTruncated`, and `EffectiveWorkingDirectory`), exercising `ISandboxPathResolver` virtual path remapping (`/workspace` or `/nightcodeforge`), `ReadOnlyCommandValidator`, dynamic build/test timeout escalation, `StructuredDiagnosticExtractor`, and `HeadTailBuffer` truncation.
   - **Raw Execution Environment Mode (`DiagnosticExecutionPipeline.RawExecutionEnvironment`):** Executes directly on `IExecutionEnvironment.ExecuteAsync` with working directory and timeout overrides so OS/container-level enforcement (`:ro` mounts, `--network none`, `uid:gid`) can be probed directly.
2. **Container Inspection & Reset Hooks (Across Workspace, Task, and Nightwatch Scopes):**
   - Build upon the container management hooks already implemented in [Step 12.36A](../archive/phase-12/STEP_12_36A_DECLARATIVE_SANDBOX_MOUNT_TABLE_AND_CONTAINER_LIFECYCLE_REDESIGN.md) (`IExecutionEnvironmentProvider.RecreateEnvironmentContainerAsync`, `IContainerEnvironmentFactory.RemoveContainerAsync`, `ContainerMountAndDriftBuilder.BuildVolumeMounts` / `ComputePathEnvironmentVariable`, and [`NightCodeForgeContainerExecutor.DetectContainerDriftAsync`](../../src/NightCodeForge.Infrastructure/Execution/NightCodeForgeContainerExecutor.cs)).
   - Add a lightweight container liveness/running probe (`IsContainerRunningAsync` or inspection metadata helper) on `NightCodeForgeContainerExecutor` / `ContainerMountAndDriftBuilder`, and resolve `SandboxScopeDescriptor.ForNightwatch(...)` inside `EnvironmentDiagnosticShellService` when `ScopeKindOverride == SandboxScopeKind.SystemNightwatch` (`ncf-nightwatch-ro`) so `InspectEnvironmentAsync` accurately reports mounts, drift, `PATH`, and running status across `WorkspaceClone`, `TaskWorktree`, and `SystemNightwatch`.
3. **Cancelled Command Result Surfacing & In-Process Shell Telemetry Events:**
   - `NightCodeForgeContainerExecutor.ExecuteAsync` already drains `\x03` (`SIGINT`), records a cancelled `ShellCommandRecord` (`ExitCode = 130` when `SIGINT`-drained or `-1`, plus captured `OutputPreview`), and rethrows `OperationCanceledException`. `EnvironmentDiagnosticShellService.ExecuteCommandAsync` catches `OperationCanceledException` when caller-canceled, recovers the captured partial output (via a per-command output buffer subscribed to `OnShellCommandOutput` or the recorded cancelled `ShellCommandRecord`), and returns a cancelled `DiagnosticCommandResult` (exit code `130` when `SIGINT`-drained or `-1`) rather than losing partial transcript output.
   - Add in-process C# shell telemetry events on [`IOperationsTelemetryService`](../../src/NightCodeForge.Core/Interfaces/IOperationsTelemetryService.cs) and raise them alongside SignalR broadcasts in [`OperationsTelemetryService`](../../src/NightCodeForge.Infrastructure/Telemetry/OperationsTelemetryService.cs):
     - `event Action<ShellCommandRecord>? OnShellCommandChanged;` (raised on command start and completion)
     - `event Action<string, string, bool>? OnShellCommandOutput;` (`commandId`, `outputChunk`, `isError`, raised from `UpdateShellCommandOutput`)
   - Correlate shell telemetry events (e.g. by `CommandId` or scoped correlation) in `EnvironmentDiagnosticShellService` and diagnostic UI consumers so concurrent background agent shell commands do not cross-talk into the diagnostic console stream.
4. **Automated "Environment Doctor" (`RunEnvironmentDoctorAsync`):**
   - Execute a single-round-trip diagnostic script verifying user identity & writable `$HOME`, writable `/tmp` & package caches, workspace/clone or `/nightcodeforge` virtual mount integrity (`:ro` / `:rw` expectations from `SandboxMountTable`), Git worktree status, SDK/companion tool availability (`git`, `rg`, `dotnet`, `csnav`, `mdfetch`, `codecondenser`), and `libicu` globalization readiness.

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
    int? WorkspaceId,
    int? TaskId = null,
    SandboxSecurityTier SecurityTier = SandboxSecurityTier.Standard,
    DiagnosticExecutionPipeline Pipeline = DiagnosticExecutionPipeline.AgentBashTool,
    string? WorkingDirectoryOverride = null,
    int? TimeoutSecondsOverride = null,
    SandboxScopeKind? ScopeKindOverride = null);

public record EnvironmentMountInfo(
    string HostPath,
    string ContainerPath,
    bool IsReadOnly);

public record EnvironmentInspectionSnapshot(
    SandboxScopeKind ScopeKind,
    int? WorkspaceId,
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

- [ ] Define `IEnvironmentDiagnosticShellService` and its data records (including `EnvironmentInspectionSnapshot` with `SandboxScopeKind ScopeKind` and `int? WorkspaceId` so `SystemNightwatch` snapshots have `WorkspaceId = null` and `WorkspaceName = "System Nightwatch"`) in `NightCodeForge.Core`, and add in-process shell command events (`event Action<ShellCommandRecord>? OnShellCommandChanged`, `event Action<string, string, bool>? OnShellCommandOutput`) to `IOperationsTelemetryService` / `OperationsTelemetryService`.
- [ ] Building on [Step 12.36A](../archive/phase-12/STEP_12_36A_DECLARATIVE_SANDBOX_MOUNT_TABLE_AND_CONTAINER_LIFECYCLE_REDESIGN.md)'s existing `RecreateEnvironmentContainerAsync`, `RemoveContainerAsync`, `BuildVolumeMounts`, `ComputePathEnvironmentVariable`, `DetectContainerDriftAsync`, and `PersistentContainerShellSession` `SIGINT` recovery, add a lightweight container liveness/running probe (`IsContainerRunningAsync` or inspection metadata helper) on `NightCodeForgeContainerExecutor` / `ContainerMountAndDriftBuilder` and support `SystemNightwatch` `SandboxScopeDescriptor.ForNightwatch(...)` scope resolution in `EnvironmentDiagnosticShellService`.
- [ ] Implement `EnvironmentDiagnosticShellService` in `NightCodeForge.Agents` (keeping constructor dependencies within the [AGENTS.md](../../AGENTS.md) 10-parameter limit) supporting `InspectEnvironmentAsync`, `ExecuteCommandAsync` (`AgentBashTool` via `SandboxBashToolModule` and `RawExecutionEnvironment`, catching caller-canceled `OperationCanceledException` to recover captured partial output via correlated shell telemetry and return exit code `130` or `-1`), `RunEnvironmentDoctorAsync`, and `RecreateEnvironmentContainerAsync`.
- [ ] Add unit tests in `NightCodeForge.Infrastructure.Tests` (covering shell telemetry events `OnShellCommandChanged` / `OnShellCommandOutput` and container liveness/inspection metadata hooks) and `NightCodeForge.Agents.Tests` (covering `WorkspaceClone`, `TaskWorktree`, and `SystemNightwatch` scopes, both execution pipelines, cancellation partial output recovery, telemetry event correlation without cross-talk, doctor check parsing, and container recreation).

