# 🔄 Step 9.16: Bidirectional Task Markdown Synchronization, Workspace Hygiene & Audit Trail Anticipation

> **Phase 9 Implementation Step**  
> *Parent Plan:* [Phase 9 Master Plan](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> *Anticipates:* [Step 10.7: Task State History & Audit Trail](../../plan/phase-10/STEP_10_7_TASK_STATE_HISTORY_AND_AUDIT_TRAIL.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Infrastructure.Tests`  
> *Status:* Completed | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints below.

---

## 1. Executive Objective

Ensure agents running in isolated worktrees/sandboxes see all workspace tasks in `docs/tasks/`, synchronize their disk edits back to the database at the end of a run, keep repositories clean of test artifacts, and lay the groundwork for the Phase 10 audit trail.

1. **Single authority for sync:** `TaskMarkdownSyncService` is the only component that converts between `docs/tasks/*.md` and `ForgeTask` entities. `TaskOrchestrator` must contain no file reading, YAML parsing or description extraction of its own.
2. **Pre-agent full sync:** before any agent stage, write **all** workspace tasks (active and closed) to `docs/tasks/` in the agent working path and mirror to the host repository, with `depends_on` populated for every task.
3. **Post-agent comprehensive read-back:** at the end of an agent stage, scan the whole `docs/tasks/` directory, apply valid changes, create new tasks, and mirror results to the host repository.
4. **Fail fast:** corrupt frontmatter, unknown task IDs, circular dependencies, forbidden status changes and concurrency conflicts all fail the stage with dedicated domain exceptions. No silent catch/log/continue.
5. **Audit trail foundation:** introduce task history snapshots recorded atomically with task mutations performed by sync.
6. **Workspace hygiene:** `*.trx` and `TestResults/` are git-ignored everywhere `.gitignore` entries are managed.

---

## 2. Problem Statement & Findings Inventory

| # | Subsystem | Issue | Impact |
| :-- | :--- | :--- | :--- |
| S1 | `TaskOrchestrator` | Pre-stage sync writes only the current task, without dependencies, while `docs/tasks/` is git-ignored in worktrees. | Agents cannot see dependencies, parents or backlog; dependency IDs risk being wiped on read-back. |
| S2 | `TaskOrchestrator` | Post-stage read-back inspects only the current task's file and swallows exceptions with a warning log. | Multi-task edits and new sub-tasks are ignored; corrupt files/cycles are silently dropped. |
| S3 | `TaskOrchestrator` | Duplicates file reading, YAML parsing and description extraction; contains a stale null-check on the sync service. | Violates SRP and the fail-fast DI rule. |
| S4 | `ForgeTask` / data | Task edits overwrite DB rows with no versioning, baseline or concurrency validation. | UI edits made during an agent run are silently lost. |
| S5 | Provisioning / Git | `.gitignore` management only covers `docs/tasks/` and `docs/transcripts/`. | `dotnet test` artifacts pollute git status. |

---

## 3. Behavioral Specifications

### 3.1 Domain constants and exceptions
- Define named constants for audit actors (system, user, task-review agent, orchestrator) and a helper deriving an actor name from a stage ID. Change-reason texts, default priority, `docs/tasks` path segments and file-name patterns are also named constants (no magic strings/numbers).
- Create `TaskMarkdownSyncException` carrying the file path and task ID when known, and `TaskConcurrencyException` carrying task ID, expected version and actual version.

### 3.2 Sync service contract
The sync service constructor requires all dependencies (no optional parameters, no fallback constructors). Two operations are added on top of the existing single-task operations:

- **Sync all tasks to disk:** inputs are workspace, target directory and optional host repository path. Writes every workspace task (active and closed) to the target directory and, if different and existing, to the host repository. Dependency IDs are loaded explicitly per task. Returns (a) the map of task ID to written path and (b) the **baseline snapshot** (task ID to version/`UpdatedAt`) used later for concurrency checks. Dependency loading should be batched where practical (avoid per-task queries on large backlogs).
- **Sync all tasks from disk:** inputs are workspace, target directory, host repository path, the baseline snapshot, the change actor and the change reason. Returns all refreshed tasks, **including unchanged ones** whose file was read, so callers can distinguish "agent did not touch the file" from "task absent".

### 3.3 Read-back rules (per file)
1. **Read failure / unparsable frontmatter** → `TaskMarkdownSyncException` (with file path).
2. **Hash suppression** (self-written content) is honored, but a suppressed/unchanged file must still be reported as an unchanged refreshed task, and still mirrored when needed.
3. **Existing task (frontmatter has an ID):**
   - Unknown ID → `TaskMarkdownSyncException`.
   - **Concurrency:** if the task's current DB version differs from the baseline snapshot **and** the disk content differs from the baseline, throw `TaskConcurrencyException`. Introduce a version token on `ForgeTask` (e.g. an incrementing version or row version) for this purpose.
   - **Protected fields:** any status change in markdown is rejected with `TaskMarkdownSyncException` (not only Closed/Cancelled). Status transitions go through workflow gates only.
   - **Diffed fields:** title, description, priority, dependencies. An empty description/title on disk is treated as unchanged, not as a clear.
   - **Dependencies:** a **missing** `depends_on` field means "unchanged" and never removes dependencies; an explicit empty list removes them. Every referenced dependency must exist in the **same workspace**. Acyclicity is validated against the **resulting combined dependency set**, not edge by edge against stale DB state. Violations → `TaskMarkdownSyncException`.
   - If nothing changed, no write and no history version is created.
4. **New task (no ID in frontmatter):**
   - A missing/blank title is an error (`TaskMarkdownSyncException`); no synthetic "Untitled" value.
   - New tasks start as `Suggested`; dependencies from `depends_on` are persisted, validated like above.
   - The file is renamed to the standard convention and its frontmatter is **rewritten with the allocated ID**, so a later read-back cannot create a duplicate. The mirror to the host repository uses the **new** path, and any stale old-named copy in the host repository is removed.
5. **Mirroring:** every file mirrored to the host repository registers its content hash for suppression so the host file watcher does not re-ingest it and produce duplicate history.

### 3.4 Atomicity and layering
- Task update + dependency update + history snapshot must be one atomic unit. This is provided by a **repository-level operation** (e.g. update-with-history / create-with-baseline), **not** by the Core sync service opening transactions on `AppDbContext`. Core must not depend on Infrastructure types.
- The in-memory repository must support the same operations with equivalent semantics so tests do not depend on EF transactions.
- Version allocation is monotonic per task; the unique `(TaskId, Version)` index is the final guard.
- **Exactly one** component creates the Version 1 baseline: the repository creation operation. The sync service must not add a second one.

### 3.5 History entity
`ForgeTaskHistory` is an immutable snapshot: task ID, version, title, description, status, priority, merge mode, base/feature branches, current stage, dependency list (serialized), change timestamp, change actor, optional change reason, plus navigation to the task. Table `TaskHistories`, unique index on `(TaskId, Version)`, index on `ChangedAt`, cascade delete from the task. A migration is added. History is recorded for sync-originated changes in this step; recording for UI/orchestrator mutations (including status changes) is explicitly deferred to Step 10.6, and the doc scope must not claim otherwise.

### 3.6 Orchestrator behavior
- **Pre-stage:** choose the worktree path if it exists, otherwise the repository path; run the full sync; store the current task's relative markdown path in the pipeline data bag; keep the baseline snapshot for the post-stage call. Failures are wrapped in `TaskMarkdownSyncException` **except cancellation**, which propagates unchanged.
- **Interrupted stages:** when a stage ends in `WaitingForHuman`, post-stage read-back must still run (or edits must otherwise be preserved) **before** the resume pre-sync overwrites `docs/tasks/`; agent edits made before an `ask` must not be lost. Pre-sync on resume must not overwrite files differing from the baseline without a read-back first.
- **Post-stage:** delegate entirely to sync-from-disk with the derived actor and a stage-specific reason. Copy refreshed fields onto the in-memory task. For `task-review`:
  - the stage must have produced a modified task specification; otherwise `WorkflowExecutionException`;
  - the approved plan data-bag entry is set from the refreshed task description;
  - "unchanged on resumed run" is **not** a failure if the task was already modified by an earlier run of the same stage (i.e. the baseline differs from the initial task creation, or an earlier read-back is recorded).
- Remove the null-check on the sync service and every warning-only error handler.
- `StageSummary` fidelity is owned by Step 9.18; `ApprovedPlan` holds the technical architecture.

### 3.7 Repository hygiene
`*.trx` and `TestResults/` are added to the required `.gitignore` entries in: workspace provisioning, git mirror sync, the root `.gitignore`, and the synthetic test fixtures. Entry lists are defined once as a shared constant where possible.

---

## 4. Implementation Checklist & Deliverables

- [x] **Domain & migration**
  - [x] Actor/reason/default constants.
  - [x] `ForgeTaskHistory`, `History` navigation on `ForgeTask`, version token on `ForgeTask`, EF mapping, migration `AddTaskHistories`.
  - [x] `TaskMarkdownSyncException`, `TaskConcurrencyException`.
- [x] **Repository**
  - [x] History read/max-version operations.
  - [x] Atomic update-with-history and create-with-baseline operations (EF + in-memory).
  - [x] Confirm existing workspace-task, dependency read and dependency write operations exist on the repository interface; add if missing.
- [x] **Sync service**
  - [x] Constructor clean break (required `IServiceScopeFactory` only).
  - [x] Sync-all-to-disk (full backlog, dependencies, baseline snapshot).
  - [x] Sync-all-from-disk implementing section 3.3 (concurrency, status guard, dependency semantics, new-task ID write-back/rename/mirror, hash suppression for mirrors).
  - [x] No `AppDbContext` usage in Core.
- [x] **Orchestrator**
  - [x] Replace pre-stage and post-stage logic per 3.6, including interrupted-stage preservation and cancellation passthrough.
  - [x] Remove duplicated parsing and the null-check.
- [x] **Hygiene**
  - [x] Provisioning, git mirror sync, root `.gitignore`, `SyntheticRepoFixture`, `SyntheticGitRepo`.
- [x] **Tests**
  - [x] Sync-to-disk writes every task with dependencies.
  - [x] Round-trip: dependencies preserved; missing `depends_on` does not wipe; explicit empty removes.
  - [x] History: baseline created exactly once; versions increment only on real changes.
  - [x] Cycle detection (including cycles formed across two edited files) and unknown/cross-workspace dependency IDs.
  - [x] Concurrency conflict raises `TaskConcurrencyException`.
  - [x] Any status change on disk is rejected.
  - [x] New file: task created, ID written back, file renamed, host mirror correct, second read-back creates no duplicate; blank title fails.
  - [x] Watcher suppression of mirrored files.
  - [x] Unchanged files returned as unchanged; `task-review` with no modification fails; resumed `task-review` after an earlier edit does not fail.
  - [x] Agent edits before an `ask` survive resume.
  - [x] Cancellation is not wrapped.
  - [x] `.gitignore` entries present in all managed locations.
- [x] Run the full test suite including E2E, per AGENTS.md.
