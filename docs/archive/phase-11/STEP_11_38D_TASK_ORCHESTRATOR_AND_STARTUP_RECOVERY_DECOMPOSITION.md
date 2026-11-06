# Step 11.38D — `TaskOrchestrator` & `StartupRecoveryService` Decomposition and Zero-Exception `<= 10` Constructor Parameter Enforcement

**Status:** Completed (Fix / Code Factorization & Constructor Diet)
**Prerequisite:** Step 11.38C

---

## 1. Problem Statement

After Steps 11.38B and 11.38C, only two classes in the entire solution exceed the 10-constructor-parameter limit:

1. [`TaskOrchestrator`](../../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) (**3,807 lines**, **19 constructor parameters**):
   - Currently acts as a God class handling:
     - Pipeline cohort loop and stage execution (`ProcessTaskAsync`, ~2,200 lines),
     - Ext4 mirror initialization/sync, feature branch & git worktree checkout, relative worktree path normalization, markdown task file syncing, and review diff computation,
     - Merge gate execution, conflict resolution lifecycle initiation, and downstream DAG cascade unblocking (`CompleteTaskAndCascadeAsync`, `CompleteTaskAndCascadeInternalAsync`, `InitiateConflictResolutionAsync`),
     - Human interaction state transitions (`SubmitHumanAnswerAsync`, `SubmitAnswersAsync`, `RequestChangesAsync`, `PromoteSuggestedTaskAsync`, `RejectTaskAsync`, `RestartTaskPipelineAsync`).
2. [`StartupRecoveryService`](../../../src/NightCodeForge.Core/Services/StartupRecoveryService.cs) (**637 lines**, **11 constructor parameters**):
   - Combines low-level Git filesystem recovery (WSL availability check, stale `.git/*.lock` purging, ext4 mirror alignment with `host-origin`, dirty worktree auto-commit rescue, worktree relative path normalization, orphaned container reaping) with high-level workflow task status normalization and Hangfire queue re-enqueuing.

Additionally, [`ExecutionEnvironmentProvider`](../../../src/NightCodeForge.Infrastructure/Execution/ExecutionEnvironmentProvider.cs) and [`DevQueueService`](../../../src/NightCodeForge.Web/Services/DevQueueService.cs) sit right at the **10-parameter boundary** (`10` constructor parameters each), leaving zero headroom for future dependencies.

---

## 2. Architectural Goals

1. **Decompose `TaskOrchestrator` (`19 -> <= 9` Constructor Parameters):**
   - Extract cohesive domain collaborators for workspace/git/markdown preparation, merge & conflict-resolution cascade, and human interaction mutations so `TaskOrchestrator` focuses on stage/cohort pipeline execution.
2. **Decompose `StartupRecoveryService` (`11 -> <= 8` Constructor Parameters):**
   - Extract Git worktree/mirror/lock recovery into a dedicated service (`IWorkspaceGitRecoveryService`) shared or aligned with workspace preparation.
3. **Give Headroom to 10-Parameter Boundary Services (`ExecutionEnvironmentProvider` & `DevQueueService`):**
   - Group container-specific dependencies in `ExecutionEnvironmentProvider` into an `IContainerEnvironmentFactory` (reducing `ExecutionEnvironmentProvider` from 10 to `<= 5` parameters).
4. **Zero-Exception `<= 10` Constructor Parameter Enforcement:**
   - Delete `LegacyConstructorDebtAllowlist` from `ConstructorParameterCeilingTests` so that **every** class in the solution strictly obeys `constructor.GetParameters().Length <= 10` with zero exceptions.

---

## 3. Required Specification & Behavioral Contracts

### 3.1 `TaskOrchestrator` Collaborator Extraction

Extract cohesive services in `NightCodeForge.Core.Workflow` (all with `<= 8` required constructor parameters):

1. **`ITaskWorkspacePreparer` (`TaskWorkspacePreparer`):**
   - Encapsulates ext4 mirror initialization and synchronization (`IGitMirrorSyncService`), feature branch & task worktree preparation (`IGitWorkspaceManager`), markdown task file syncing (`ITaskMarkdownSyncService` — `SyncMarkdownTaskSafeAsync`, `SyncTaskMarkdownAndMirrorAsync`), and review diff population (`PopulateReviewDiffsAsync`).
   - Removes `IGitMirrorSyncService`, `ITaskMarkdownSyncService`, and direct low-level mirror/diff plumbing from `TaskOrchestrator`.
2. **`ITaskMergeAndCascadeCoordinator` (`TaskMergeAndCascadeCoordinator`):**
   - Encapsulates `CompleteTaskAndCascadeAsync`, `CompleteTaskAndCascadeInternalAsync`, `InitiateConflictResolutionAsync`, `IConflictResolutionVerifier` invocation, and downstream DAG readiness cascade unblocking (`IDependencyResolutionEngine`, `ITaskDependencyStore`, `ITaskJobQueue`).
   - `TaskOrchestrator.CompleteTaskAndCascadeAsync` delegates directly to `ITaskMergeAndCascadeCoordinator` (preserving the `ITaskOrchestrator` public interface contract).
3. **`ITaskLifecycleMutationCoordinator` (`TaskLifecycleMutationCoordinator`):**
   - Encapsulates `SubmitHumanAnswerAsync`, `SubmitAnswersAsync`, `RequestChangesAsync`, `PromoteSuggestedTaskAsync`, `RejectTaskAsync`, and `RestartTaskPipelineAsync`.
   - `TaskOrchestrator` delegates these `ITaskOrchestrator` methods to `ITaskLifecycleMutationCoordinator`.
4. **Refactored `TaskOrchestrator`:**
   - Constructor parameter count is reduced from **19 to `<= 9`**.
   - All existing `TaskOrchestrator*Tests`, `ConflictResolutionLifecycleTests`, `PipelineCohortChainingTests`, and `CascadeUnblockingTests` continue to pass through `ITaskOrchestrator`.

### 3.2 `StartupRecoveryService` Collaborator Extraction (`11 -> <= 8` Parameters)

1. **`IWorkspaceGitRecoveryService` (`WorkspaceGitRecoveryService`):**
   - Encapsulates WSL availability verification (`VerifyWslAvailability`), stale `.git/*.lock` purging (`PurgeStaleGitLocks`), ext4 mirror alignment (`AlignMirrorWithHostOriginAsync`), dirty worktree discovery and uncommitted change rescue (`GetWorktreesToRescueAsync`, `RescueUncommittedChangesAsync`), and relative worktree path normalization (`EnsureWorktreeRelativePathsAsync`).
   - Constructor has `<= 6` required parameters (`IGitCliRunner`, `IGitWorkspaceManager`, `IWorkspacePathResolver`, `IProcessRunner`, `ITaskRepository`, `ILogger<WorkspaceGitRecoveryService>`).
2. **Refactored `StartupRecoveryService`:**
   - Injects `IWorkspaceGitRecoveryService` instead of low-level `IGitCliRunner`, `IGitWorkspaceManager`, `IWorkspacePathResolver`, and `IProcessRunner`, reducing its constructor from **11 parameters to `<= 8`**.

### 3.3 `ExecutionEnvironmentProvider` Headroom (`10 -> <= 5` Parameters)

- Extract container-sandbox creation dependencies (`IHostSdkDiscoveryService`, `IHostUserIdentityResolver`, `ICompanionToolInstaller`, `ILogger<NightCodeForgeContainerExecutor>`, etc.) into `IContainerEnvironmentFactory` (`ContainerEnvironmentFactory`, `<= 8` parameters) so `ExecutionEnvironmentProvider` drops from 10 parameters to `<= 5`.

### 3.4 Zero-Exception `ConstructorParameterCeilingTests`

- Remove `LegacyConstructorDebtAllowlist` from `ConstructorParameterCeilingTests`.
- The test must assert without any exclusions or allowlists that every service, harness, and domain collaborator constructor across `NightCodeForge.Core`, `NightCodeForge.Infrastructure`, `NightCodeForge.Agents`, and `NightCodeForge.Web` has **`<= 10` parameters**.

---

## 4. Verification Criteria

- `ConstructorParameterCeilingTests` passes with **zero allowlisted classes** (`MaxConstructorParameters = 10`).
- All unit tests for `TaskWorkspacePreparer`, `TaskMergeAndCascadeCoordinator`, `TaskLifecycleMutationCoordinator`, `WorkspaceGitRecoveryService`, and `ContainerEnvironmentFactory` pass.
- Full test suite (including all `TaskOrchestrator*Tests` and `StartupRecovery*Tests`) passes.
