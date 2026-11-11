# 🛡️ Step 11.40: Bash Timeout Escalation Ladder, In-Container Process Reaper, Stale Container Recycling & WSL2 Self-Healing

> **Bug Fix & Resiliency Step (Execution Sandbox, Container Process Hygiene & WSL2 Recovery)**  
> *Parent Plan:* [Phase 11 Master Plan](../11_EXTEND_FEATURES.md)  
> *Target Framework:* .NET 10 (C# 13) | *Status:* Completed  
> *Target Projects:* [`src/NightCodeForge.Core`](../../../src/NightCodeForge.Core), [`src/NightCodeForge.Infrastructure`](../../../src/NightCodeForge.Infrastructure), [`src/NightCodeForge.Agents`](../../../src/NightCodeForge.Agents), [`tests/NightCodeForge.Core.Tests`](../../../tests/NightCodeForge.Core.Tests), [`tests/NightCodeForge.Infrastructure.Tests`](../../../tests/NightCodeForge.Infrastructure.Tests)  
> *Dependencies:* [Step 11.21: Container ICU & Dynamic Timeouts](STEP_11_21_CONTAINER_ICU_DYNAMIC_TIMEOUTS_AND_REVIEW_TEST_MANDATE.md), [Step 11.26: Container Reuse Validation & Task Lifecycle Cleanup](STEP_11_26_CONTAINER_REUSE_VALIDATION_AND_TASK_LIFECYCLE_CLEANUP.md)  
> *Aligns With:* [AGENTS.md](../../../AGENTS.md), [docs/PLAN.md](../../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Summary & Root Cause Analysis

### 1.1 Problem Statement
During autonomous stage execution and interactive sessions, shell commands executed via the `bash` tool or deterministic quality gates occasionally time out due to hung test runners (`dotnet test` / `testhost`), deadlocked child processes, compiler server stalls, or underlying Docker / WSL2 instability.

Investigation of how [`NightCodeForgeContainerExecutor`](../../../src/NightCodeForge.Infrastructure/Execution/NightCodeForgeContainerExecutor.cs) and [`WslExecutionEnvironment`](../../../src/NightCodeForge.Infrastructure/Execution/WslExecutionEnvironment.cs) currently handle command timeouts reveals four critical failure modes:

| # | Vulnerability | Root Cause in Current Codebase | Impact |
| :--- | :--- | :--- | :--- |
| **1** | **Orphaned In-Container Processes Survive Timeouts** | On timeout, [`NightCodeForgeContainerExecutor.ExecuteAsync`](../../../src/NightCodeForge.Infrastructure/Execution/NightCodeForgeContainerExecutor.cs#L391-L431) writes `\x03` to `_stdin` (which is a non-PTY pipe where `\x03` does not generate kernel `SIGINT`, and the subshell redirects `< /dev/null` anyway), then calls `ForceRestartShellProcess()` which only kills the host-side `docker exec -i` client process. | Hung child processes (`dotnet`, `testhost`, `msbuild`, `node`, `python3`, `git`) remain alive inside the container reparented to `PID 1`, holding file locks (`obj/`, `bin/`, `.git/index.lock`), ports, and RAM/CPU, causing all subsequent turns to fail or time out. |
| **2** | **Zombie Process Accumulation (`PID 1 = tail`)** | Containers are launched with `tail -f /dev/null` as `PID 1` without `--init`. `tail` does not call `waitpid(-1)` on reparented child processes. | Every terminated or orphaned subprocess inside the container becomes a permanent `<defunct>` zombie in the container's PID table. |
| **3** | **Wedged Container State After Timeout** | `ForceRestartShellProcess()` immediately spawns a new `docker exec -i` without verifying whether the container runtime can still execute commands or whether the container's namespace/mounts are wedged. | If the container is wedged, the next command hangs until another full build/test timeout (5–10 minutes) expires. |
| **4** | **WSL2 / Docker Daemon Deadlocks on Windows** | On Windows hosts using WSL2 (`UseWsl = true` or `ExecutionMode.WslDirect`), the WSL2 utility VM, 9P/Plan9 mount bridge, or in-WSL Docker daemon occasionally freezes under heavy I/O or memory pressure. | Killing the container fails or hangs indefinitely until a human manually runs `wsl --shutdown`. |
| **5** | **Idle Containers Remain Resident on `Ready` / `WaitingForHuman` / `Failed` Tasks** | [`TaskContainerReaper`](../../../src/NightCodeForge.Core/Services/TaskContainerReaper.cs) only reaps containers when a task reaches `Closed`, `Rejected`, or `Cancelled`. | Tasks waiting hours or days for human review (`Ready`), human answers (`WaitingForHuman`), or sitting in `Failed` state keep their containers (and inside-container daemons like `csnav` holding 500 MB–2 GB RAM each) alive indefinitely. |

---

## 2. Escalation Architecture on `bash` Timeout

When a command executed in `NightCodeForgeContainerExecutor` (or `WslExecutionEnvironment`) times out, the executor must execute a deterministic **3-stage self-healing escalation ladder** before releasing the execution lock and returning the timeout result to the caller:

```mermaid
flowchart TD
    Timeout["Command Timeout Detected in ExecuteAsync"] --> CloseShell["Close / Kill Host-Side Shell Session"]
    CloseShell --> Step1["Step 1: In-Container Process Reaper<br/>1. Identify all non-zombie processes except PID 1 (init/tail) & Whitelist (e.g. csnav)<br/>2. Send SIGTERM (-15)<br/>3. Poll up to 5s for exit<br/>4. Send SIGKILL (-9) to any survivors"]
    Step1 --> Probe1{"Step 2A: Run Liveness Probe<br/>(5s timeout)"}
    Probe1 -- "Pass (< 5s)" --> RestartShell["Ensure Interactive Shell Session Ready & Return Timeout Result (124)"]
    Probe1 -- "Fail / Timeout (5s)" --> KillContainer["Step 2B: Force-Kill & Recreate Container<br/>(docker rm -f & EnsureContainerRunning)"]
    KillContainer --> Probe2{"Step 2C: Retry Liveness Probe<br/>(5s timeout)"}
    Probe2 -- "Pass (< 5s)" --> RestartShell
    Probe2 -- "Fail / Timeout (5s)" --> IsWindowsWsl{"Step 3: Windows + WSL Active?"}
    IsWindowsWsl -- "No (Linux/macOS or Non-WSL)" --> LogHardFail["Log Critical Recovery Failure & Return Timeout Result"]
    IsWindowsWsl -- "Yes (Windows WSL)" --> WslShutdown["Step 3A: Shutdown WSL<br/>(wsl --shutdown)"]
    WslShutdown --> EnsureDocker["Step 3B: Boot WSL, Ensure Docker Daemon & Recreate Container<br/>(within 30s recovery budget)"]
    EnsureDocker --> Probe3{"Step 3C: Retry Liveness Probe<br/>(30s timeout)"}
    Probe3 -- "Pass (< 30s)" --> RestartShell
    Probe3 -- "Fail / Timeout (30s)" --> LogHardFail
```

---

## 3. Detailed Behavioral Specifications

### 3.1 Step 1: In-Container Process Sweep (`SIGTERM` $\to$ 5s Wait $\to$ `SIGKILL`)

1. **Trigger Condition & Removal of Broken `\x03` Stdin Grace Period:**
   - Whenever a command in `NightCodeForgeContainerExecutor.ExecuteAsync` times out (`TimeoutException`), or is aborted via caller `OperationCanceledException` while a command is already in-flight on `_stdin`, **immediately** terminate and dispose the host-side `docker exec -i` shell session (`_shellSession` / `_shellProcess`) so the stuck or desynchronized subshell client is closed.
   - Remove the legacy `\x03` (`Ctrl+C`) write to `_stdin` and its 2-second grace `ReadUntilSentinelAsync` retry, which cannot signal non-PTY processes reading from `/dev/null` and can corrupt `_stdin` if the command finishes near the deadline.
   - Preserve any partial stdout lines already accumulated in `HeadTailBuffer` prior to the timeout so the returned timeout `CommandResult.StandardOutput` and `ShellCommandRecord.OutputPreview` retain the output leading up to the hang (or `string.Empty` if no output was emitted).
   - Note: On caller `OperationCanceledException`, after closing the desynchronized shell session and running Step 1 process cleanup (using `CancellationToken.None` with a bounded cleanup timeout), rethrow the `OperationCanceledException` to preserve standard cancellation semantics.

2. **Protected Process Whitelist:**
   - Maintain a configurable whitelist of process names (`ContainerExecutorOptions.ProcessKillWhitelist`) that must **not** be killed during timeout cleanup:
     - **Always protected:** Container root process (`PID 1`, e.g., `docker-init`, `tini`, or `tail`), the `tail -f /dev/null` keep-alive process (matched by `comm == "tail"` and `cmdline` containing `tail` and `/dev/null`, which runs as a child of `PID 1` when `--init` is enabled and whose termination would cause the container to exit), and the reaper script's own process tree (`$$`, `$BASHPID`, and `$PPID`).
     - **Default whitelist (`DefaultProcessKillWhitelist`):** `["csnav"]` (preserves the warm Roslyn navigation daemon across command timeouts so subsequent agent turns do not pay cold solution indexing latency).
     - **Explicitly NOT whitelisted (must be reaped on timeout):** `dotnet` (non-`csnav`), `testhost`, `vstest.console`, `MSBuild`, `VBCSCompiler`, `node`, `npm`, `python3`, `pytest`, `cargo`, `git`, `bash`, `sh`, and any browser binaries (`chrome`, `chromium`, `msedge`, `msedgewebview2`, `firefox`) spawned by E2E tests.
     - Note: Because `csnav` runs as a .NET global tool (whose `/proc/<pid>/comm` may be `csnav` or `dotnet` with `csnav` in `/proc/<pid>/cmdline`), whitelist matching must inspect both the executable name (`comm`) and command-line arguments (`cmdline`) so `csnav server` is accurately preserved while `dotnet test` / `dotnet build` / `VBCSCompiler` are terminated.

3. **Two-Phase Signal Escalation (`SIGTERM` $\to$ 5s $\to$ `SIGKILL`), Zombie Filtering & Container User Matching:**
   - Execute an out-of-band cleanup command inside the container (`docker exec --user <effectiveUser> <containerName> bash -c "..."`):
     - **User Identity Constraint:** Always pass the same `--user <effectiveUser>` as `BuildProcessStartSpec` (`1000:1000` when `IsRestrictedReadOnly == true`, or `options.UserUidGid`). In `RestrictedReadOnly` containers launched with `--cap-drop=ALL`, `root` lacks `CAP_KILL` and cannot signal processes owned by UID `1000`, whereas matching `--user` allows signalling all processes of that UID without elevated capabilities.
     1. Enumerate all running PIDs inside the container excluding `PID 1`, the keep-alive `tail -f /dev/null`, the reaper script's own PIDs (`$$`, `$BASHPID`, `$PPID`), `<defunct>` zombie/dead processes (`State: Z` or `X` in `/proc/<pid>/stat`), and any process matching the whitelist.
     2. If target PIDs exist, send `SIGTERM` (`kill -15 <pids>`).
     3. Wait up to **5 seconds** (polling every `200ms` via `kill -0 <pid>` while skipping PIDs that have transitioned to `Z`/`X` in `/proc/<pid>/stat` so `<defunct>` zombies do not stall the grace loop) for all targeted PIDs to terminate gracefully. As soon as all targeted PIDs have exited, proceed immediately without waiting the full 5 seconds.
     4. After 5 seconds, if any targeted PIDs (or newly spawned non-zombie descendants of those PIDs) remain alive, send `SIGKILL` (`kill -9 <survivor_pids>`).
   - Bound the Step 1 `docker exec` invocation itself with a hard host-side timeout (e.g., `10 seconds`) so a completely frozen container or Docker daemon immediately falls through to Step 2.
   - Note: Do **not** blindly delete `.git/index.lock` files from inside the container reaper script, because multiple per-task containers mount the shared workspace directory (`/workspace`) and canonical clone (`/workspace/clone/.git/worktrees/*`) while each container only has visibility into its own PID namespace (`/proc`). Stale `index.lock` cleanup remains handled by the existing Git workspace/recovery layer.

---

### 3.2 Step 2: Container Liveness Probe (5s) & Container Recycling (5s Retry)

1. **Recovery Telemetry Contract (`ContainerRecoveryStage`):**
   - Define `ContainerRecoveryStage` (`None`, `ProcessSweepSucceeded`, `ContainerRecreated`, `WslShutdownRecovered`, `RecoveryFailed`) in [`OperationsTelemetryModels.cs`](../../../src/NightCodeForge.Core/Workflow/OperationsTelemetryModels.cs) and include it as an optional property on `ShellCommandRecord` (`ContainerRecoveryStage RecoveryStage = ContainerRecoveryStage.None`) so recovery outcomes are recorded on `RecordShellCommandCompleted` without adding new dependencies to `NightCodeForgeContainerExecutor`.

2. **Initial Liveness Probe (5-Second Timeout):**
   - Immediately after Step 1 completes (or if Step 1 timed out / failed), run a lightweight probe command inside the container (e.g., `docker exec <containerName> echo __NCF_PROBE_OK__` or equivalent sentinel check) via `_processRunner.RunAsync` with a strict **5-second timeout**.
   - If the probe exits with code `0` and emits the expected probe token within 5 seconds:
     - Start a fresh interactive shell session (`StartShellProcess()`).
     - Record recovery telemetry (`ContainerRecoveryStage.ProcessSweepSucceeded`) and return the timeout `CommandResult` (exit code `124`).

3. **Container Kill, Recreation & Probe Retry (5-Second Timeout):**
   - If the initial probe times out after 5 seconds or returns a non-zero exit code:
     - Log a warning indicating that container `{ContainerName}` is unresponsive after timeout and is being recycled.
     - Force-remove the wedged container (`docker rm -f <containerName>` with a bounded timeout, e.g., 10s).
     - Recreate and start a clean container instance (`EnsureContainerRunning()`, ensuring container creation proceeds during recovery when `CustomProcessFactory == null`).
     - **Retry the liveness probe with a 5-second timeout.**
     - If the retry probe succeeds within 5 seconds:
       - Start a fresh interactive shell session (`StartShellProcess()`).
       - Record recovery telemetry (`ContainerRecoveryStage.ContainerRecreated`).

---

### 3.3 Step 3: Windows-Specific WSL2 Reset (`wsl --shutdown`) & 30-Second Probe Retry

1. **Trigger Condition:**
   - Executed **only on Windows** (`RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`) when WSL2 is in use (`options.UseWsl == true` for `ContainerSandbox`, or in `WslDirect` mode) and **Step 2's retry probe failed or timed out**.

2. **WSL2 Shutdown & Environment Re-Initialization:**
   - Log a critical warning that both the container probe and container recreation failed, triggering a full WSL2 utility VM reset (`wsl --shutdown`).
   - Execute `wsl.exe --shutdown` via `IProcessRunner` (with a bounded timeout of `120 seconds`, as tearing down a hung WSL2 utility VM can take a long time).
   - Because `wsl --shutdown` terminates the WSL2 VM and causes all host-side `wsl.exe` / `docker exec` client processes to exit (`HasExited == true`), any other cached `NightCodeForgeContainerExecutor` instances will automatically detect the exited session in `EnsureShellProcessRunning()` on their next command and invoke `EnsureContainerRunning()` + `StartShellProcess()` without requiring a circular reference between `NightCodeForgeContainerExecutor` and `ExecutionEnvironmentProvider` (an optional callback on `ContainerExecutorOptions` may also be used if proactive cache invalidation is desired).
   - Within the **30-second recovery window**:
     - Ensure the target WSL2 distribution boots and the Docker daemon inside WSL2 is reachable (if `docker info` fails because native `dockerd` inside WSL2 stopped on shutdown, attempt non-interactive daemon startup `wsl -u root -- sh -c "service docker start || systemctl start docker || true"`).
     - Recreate/start the container via `EnsureContainerRunning()`.
     - **Retry the container liveness probe with a 30-second timeout.**
   - If the 30-second probe succeeds, start a fresh interactive shell session and record telemetry (`ContainerRecoveryStage.WslShutdownRecovered`).
   - If the 30-second probe still fails, log an error with full diagnostic context, record `ContainerRecoveryStage.RecoveryFailed`, and surface a clear failure message in the timeout `CommandResult` (exit code `124`).

3. **`WslExecutionEnvironment` (`WslDirect` Mode) Parity:**
   - In [`WslExecutionEnvironment.ExecuteAsync`](../../../src/NightCodeForge.Infrastructure/Execution/WslExecutionEnvironment.cs), command timeouts surface either as `res.ExitCode == 124` (from `IProcessRunner`) or `OperationCanceledException when (!cancellationToken.IsCancellationRequested)` (from the internal timeout `CancellationTokenSource`).
   - On Windows (`RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`), both timeout paths must:
     - Run a 5-second WSL probe (`wsl.exe [-d <distro>] -- echo __NCF_PROBE_OK__`).
     - If the 5-second probe fails or times out, execute `wsl.exe --shutdown` and retry the probe with a **30-second timeout**.
     - Always return the standard timeout `CommandResult` (`ExitCode: 124`, `Truncated: true`, `StandardError` containing `"WSL command execution timed out"`) whether the recovery probe succeeded or failed.

---

### 3.4 Automatic Stale Container, PID Limit & Zombie Lifecycle Hardening

1. **Enable `--init` and `--pids-limit` on Container Spawn (`BuildDockerRunArgs`):**
   - Include `--init` in `NightCodeForgeContainerExecutor.BuildDockerRunArgs` so Docker's lightweight init process (`docker-init` / `tini`) runs as `PID 1` and automatically reaps reparented zombie processes throughout the container's lifetime.
   - Add a configurable `PidsLimit` to `ContainerExecutorOptions` (default: `DefaultPidsLimit = 4096`) and pass `--pids-limit <PidsLimit>` in `BuildDockerRunArgs` when `PidsLimit > 0` so a runaway test runner or fork loop inside a container cannot exhaust the WSL2 kernel PID table before the command timeout triggers.
   - Existing containers created prior to this change do not require forced drift-detection recreation solely for `--init`/`--pids-limit`; they will adopt the new flags naturally as containers are reaped or recycled.

2. **Expand `TaskContainerReaper` & Lifecycle Teardown to Non-Active States:**
   - When a task finishes a worker turn in [`TaskOrchestrator.ProcessTaskAsync`](../../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) (and [`TaskMergeAndCascadeCoordinator`](../../../src/NightCodeForge.Core/Workflow/TaskMergeAndCascadeCoordinator.cs)) and its resulting state is non-executing:
     - Specifically, when `task.Status` is `Ready` (awaiting human review in Review Room), `Failed`, `Cancelled`, `Closed`, or `Rejected`, **or** when `task.IsWaitingForHuman == true` (awaiting human answer or manual conflict rebase in Inbox) or `task.IsDeferredByPause == true`:
     - Tear down its per-task sandbox container via [`ITaskWorkspacePreparer.CleanupTaskContainerSafeAsync`](../../../src/NightCodeForge.Core/Workflow/TaskWorkspacePreparer.cs) so idle containers and in-container daemons (`csnav`) do not consume gigabytes of WSL2 memory while waiting for human action.
     - **Cross-Cohort Loopback Guard:** Do **not** key container teardown off `cohortExitReason == CohortExitReason.Failed` alone, because cross-cohort loopbacks (e.g., `HandleCrossCohortLoopBackAsync` or `ConflictVerificationOutcome.LoopedBackToImplementation`) set `cohortExitReason = CohortExitReason.Failed` while leaving `task.Status == TaskOverallStatus.Open` and immediately enqueuing the target stage. Always inspect the actual post-run task state (`Status`, `IsWaitingForHuman`, `IsDeferredByPause`).
     - When the task later resumes (e.g., human answers a question, requests changes in Review Room, or restarts a failed stage), `EnsureContainerRunning` transparently recreates the container on demand with all volume mounts and caches intact.
   - Update [`TaskContainerReaper.ReapAsync`](../../../src/NightCodeForge.Core/Services/TaskContainerReaper.cs) (and share `TaskContainerReaper.ShouldReapTaskContainer`) so periodic and startup sweeps also reap containers belonging to `Failed` tasks, `Ready` tasks, and `Open` tasks where `IsWaitingForHuman == true` or `IsDeferredByPause == true`.

3. **Idle Workspace Container TTL Eviction:**
   - Track `LastExecutionTimestampUtc` (initialized to `DateTime.UtcNow` and updated when `ExecuteAsync` completes) and `IsExecuting` (true while `_execLock` is held) on `NightCodeForgeContainerExecutor`.
   - Expose `EvictIdleWorkspaceContainersAsync` on [`IExecutionEnvironmentProvider`](../../../src/NightCodeForge.Core/Interfaces/IExecutionEnvironment.cs) (implemented on [`ExecutionEnvironmentProvider`](../../../src/NightCodeForge.Infrastructure/Execution/ExecutionEnvironmentProvider.cs)) and invoke it during periodic maintenance sweeps ([`GitSyncService.PruneWorktreesAsync`](../../../src/NightCodeForge.Web/Services/GitSyncService.cs) / `TaskContainerReaper.ReapAsync`).
   - Because all cached workspace-level executors (`TaskId == null` across `SandboxSecurityTier` values) share the same underlying base container (`ncf-sandbox-{workspaceId}`), `EvictIdleWorkspaceContainersAsync` evaluates all cached workspace-level executors for a workspace together: only when **all** cached workspace-level tier executors are not currently executing (`!IsExecuting`) and have been idle for at least `idleTimeout` (default: `30 minutes`) are all cached workspace-level entries removed, disposed, and the underlying base container removed once via `IContainerEnvironmentFactory.RemoveBaseContainerAsync`.

---

## 4. Verification & Test Strategy

1. **In-Container Process Reaper Unit Tests (`NightCodeForge.Infrastructure.Tests`):**
   - Verify that on command timeout (and on caller `OperationCanceledException` while a command is in-flight) in `NightCodeForgeContainerExecutor.ExecuteAsync`, Step 1 closes the stuck shell session and executes the process sweep as `--user <effectiveUser>` targeting non-whitelisted processes with `SIGTERM`, waits up to 5 seconds, and escalates to `SIGKILL` while preserving `PID 1`, `tail`, and whitelisted `csnav` (without touching shared `.git/index.lock` files across concurrent worktrees).
   - Verify custom `ProcessKillWhitelist` entries in `ContainerExecutorOptions` are respected.

2. **3-Stage Escalation Ladder Unit Tests (`NightCodeForge.Infrastructure.Tests`):**
   - **Scenario 1 (Healthy Probe after Step 1):** Command times out $\to$ Step 1 sweeps processes $\to$ Step 2A probe (5s) succeeds $\to$ container is NOT removed, shell restarts cleanly, returns exit code `124`.
   - **Scenario 2 (Unresponsive Container Recovered at Step 2):** Command times out $\to$ Step 1 runs $\to$ Step 2A probe (5s) times out/fails $\to$ Step 2B runs `docker rm -f` + `EnsureContainerRunning` $\to$ Step 2C retry probe (5s) succeeds $\to$ `wsl --shutdown` is NOT called.
   - **Scenario 3 (Windows WSL2 Wedged Recovered at Step 3):** Command times out $\to$ Step 2A probe (5s) fails $\to$ Step 2B recreates container $\to$ Step 2C retry probe (5s) fails $\to$ on Windows with `UseWsl = true`, Step 3 executes `wsl --shutdown`, restarts container, and succeeds on the 30s probe.
   - **Scenario 4 (Non-Windows or Non-WSL Guard):** Verify Step 3 (`wsl --shutdown`) is never invoked when `UseWsl == false` or on non-Windows platforms.

3. **Stale Container Reaper, `--init` & `--pids-limit` Tests (`NightCodeForge.Core.Tests` & `NightCodeForge.Infrastructure.Tests`):**
   - Verify `BuildDockerRunArgs` includes `--init` and `--pids-limit 4096` (or the configured `PidsLimit`).
   - Verify `TaskContainerReaper.ReapAsync` reaps containers for `Failed`, `Ready`, and `WaitingForHuman` / paused tasks in addition to `Closed`, `Rejected`, and `Cancelled`.

---

## 5. Definition of Done (DoD) Checklist

- [x] Add `ProcessKillWhitelist` (defaulting to `["csnav"]`) and `PidsLimit` (defaulting to `4096`) to `ContainerExecutorOptions`, and include `--init` and `--pids-limit` in `BuildDockerRunArgs`.
- [x] Implement **Step 1 (In-Container Process Reaper)** on `bash` timeout (and in-flight caller cancellation) in `NightCodeForgeContainerExecutor`: close the stuck shell session, run the reaper as `--user <effectiveUser>`, send `SIGTERM` to all non-zombie container processes except `PID 1`/`tail` and `ProcessKillWhitelist`, wait up to 5s, and send `SIGKILL` to survivors.
- [x] Implement **Step 2 (Container Liveness Probe & Recycling)**: run a 5s probe command; if it fails or times out, force-remove and recreate the container (`docker rm -f` + `EnsureContainerRunning`) and retry the probe with a 5s timeout.
- [x] Implement **Step 3 (Windows WSL2 Shutdown Recovery)**: on Windows when `UseWsl` is enabled and Step 2's retry probe fails, execute `wsl --shutdown`, ensure Docker daemon & container recovery, and retry the probe with a 30s timeout (and apply matching 5s probe $\to$ `wsl --shutdown` $\to$ 30s probe recovery to `WslExecutionEnvironment`).
- [x] Expand `TaskContainerReaper` and task lifecycle transitions (`Failed`, `Ready`, `WaitingForHuman`) to tear down idle task containers automatically, plus idle workspace container TTL eviction.
- [x] Author unit tests in `NightCodeForge.Infrastructure.Tests` and `NightCodeForge.Core.Tests` covering all 3 escalation stages, process whitelist preservation, `--init` and `--pids-limit` flag generation, and expanded container reaping; verify all tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

