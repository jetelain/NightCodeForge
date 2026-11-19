# 🛡️ Step 12.38: Triage Task Lifecycle, Verbatim Intake Persistence, Scoped Backlog Mutations & Inspector Visibility

> **Feature Implementation Step (Sandboxed Triage Observability, Non-Backlog Triage Lifecycle & Safe Task Editing)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Aligns With:* [Step 12.13A: Ideation Backlog Mutation Tools](../archive/phase-12/STEP_12_13A_IDEATION_BACKLOG_MUTATION_TOOLS_AND_PROMPT_POLICY.md), [Step 12.37: Companion API Key Store & NightCode Assay Handoff](../archive/phase-12/STEP_12_37_COMPANION_API_KEY_STORE_AND_ASSAY_HANDOFF_ENDPOINTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Agents`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Agents.Tests`, `tests/NightCodeForge.Web.Tests` | *Status:* Completed | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Problem Statement & Root-Cause Diagnosis

Today, untrusted intake triage ([`TriageService.cs`](../../src/NightCodeForge.Agents/Triage/TriageService.cs)) suffers from four interlocking usability, data-integrity, and observability issues:

1. **Accidental Creation of a Useless Dispatchable Backlog Task & Unlinked Telemetry ("Black Box"):**
   - [`TriageService.cs`](../../src/NightCodeForge.Agents/Triage/TriageService.cs) instantiates an in-memory `ForgeTask` (`Id = 0`, `Status = TaskOverallStatus.Suggested`, `CurrentStageId = StandardStageIds.Triage`, `Description = NeutralTriageTaskDescription`) without persisting it first.
   - Because `triageTask.Id == 0` when [`CrucibleAgentHarness.RunStageAsync`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) begins, `Conversation.TaskId` is recorded as `null`, and initial activity broadcasts emit with `taskId = 0`.
   - On the first tool call inside the harness, the state persistence path calls `UpdateAsync(triageTask)` while `Id == 0`, causing Entity Framework Core to insert a new `ForgeTask` row with `Status = TaskOverallStatus.Suggested`, `HasExecutionRun = false`, and the generic placeholder `NeutralTriageTaskDescription`—while leaving `Conversation.TaskId` unlinked (`null`).
   - That orphaned row leaks onto the Kanban board (`Suggested` column), Visual DAG, `docs/tasks/*.md` sync, and backlog counters as a useless task that should never be dispatched, while [`Triage.razor`](../../src/NightCodeForge.Web/Components/Pages/Triage.razor) renders only an opaque loading spinner for the entire run.
2. **Loss of Verbatim Initial Intake Report:**
   - Because the synthetic task stores `NeutralTriageTaskDescription`, the original intake title, full description, stack trace, and external reference URL are discarded once the triage call completes instead of being preserved verbatim on the triage session record for audit and inspection.
3. **Inability to Edit or Remove Tasks Created in the Current or Previous Triage Sessions:**
   - Triage currently runs with `AttachBacklogMutationService: false`, only grants `propose_tasks` (without `get_task`, `edit_task`, or `remove_task`), and persists `PendingProposals` in batch after the agent finishes.
   - As a result, `propose_tasks` does not persist tasks incrementally or return assigned `#ID`s during the session. When the triage agent calls `propose_tasks` multiple times to refine a proposal after further codebase inspection, it produces **duplicate backlog tasks**. Furthermore, when a subsequent intake report brings new reproduction details or logs for an issue already triaged earlier, Triage cannot inspect or update the existing unstarted triage task and instead creates yet another duplicate.
4. **No Clean Way to Watch Triage in Task Execution Inspector:**
   - Even if a triage task is inspected in [`TaskExecutionInspector.razor`](../../src/NightCodeForge.Web/Components/Monitoring/TaskExecutionInspector.razor), the inspector assumes a multi-stage implementation workflow: it renders `<PipelineStepper>` across the workspace's multi-stage workflow alongside workflow-only actions (`Restart Pipeline`, `Git Diff & Changes`, `Feature Branch`, `Merge Mode`) that do not apply to single-stage read-only intake triage.

---

## 2. Executive Objectives

1. **Introduce `TaskOverallStatus.Triage` as a Dedicated Non-Backlog Session Status:** Persist a dedicated triage session task before stage execution so telemetry and `Conversation` records link cleanly to `triageTask.Id`, while strictly excluding `TaskOverallStatus.Triage` from Markdown sync, Kanban, Visual DAG, backlog counters, prompt backlog digests, and orchestrator dispatch.
2. **Persist Verbatim Intake Safely on the Triage Task:** Store the complete verbatim intake report on the `TaskOverallStatus.Triage` task while guaranteeing that prompt-injection isolation in [`CrucibleAgentHarness`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) never elevates untrusted triage task content into trusted system-prompt `<task>` instructions.
3. **Grant Triage-Scoped Backlog Mutation Tools (`propose_tasks`, `get_task`, `edit_task`, `remove_task`):** Enable incremental persistence of proposed `Suggested` tasks with returned `#ID`s, read inspection of existing backlog items (`<existing_workspace_tasks>` + `get_task`), and audited editing/removal of unstarted **triage-created** tasks—both from the current session and from prior triage sessions—while strictly blocking mutations on manual/ideation/import or already-started tasks.
4. **Provide Live & Post-Run Triage Observability Without `PipelineStepper`:** Surface triage tasks in [`TaskInspector.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskInspector.razor) and [`TaskExecutionInspector.razor`](../../src/NightCodeForge.Web/Components/Monitoring/TaskExecutionInspector.razor) in a dedicated single-stage view without `<PipelineStepper>`, and expose an immediate **"Watch Live in Task Inspector (#ID)"** link in [`Triage.razor`](../../src/NightCodeForge.Web/Components/Pages/Triage.razor).

---

## 3. Technical Specifications

### 3.1 Dedicated Triage Status (`TaskOverallStatus.Triage`) & Non-Backlog Exclusions

1. **Enum Addition ([`Enums.cs`](../../src/NightCodeForge.Core/Domain/Enums.cs)):**
   - Add `TaskOverallStatus.Triage = -2` (alongside `Draft = -1`) to represent an untrusted intake triage session record rather than an actionable implementation backlog item.
   - Confirm no database migration breaks existing rows (`Draft = -1` and `0..8` remain unchanged). Existing orphaned bogus `Suggested` triage tasks previously inserted with `NeutralTriageTaskDescription` need not be migrated automatically, or may be ignored/cleaned by maintainers, but new triage sessions must exclusively create `TaskOverallStatus.Triage`.

2. **Upfront Triage Task Persistence & Lifecycle ([`TriageService.cs`](../../src/NightCodeForge.Agents/Triage/TriageService.cs)):**
   - Before invoking `harness.RunStageAsync`, [`TriageService`](../../src/NightCodeForge.Agents/Triage/TriageService.cs) must persist the `ForgeTask` (via `ITaskRepository.CreateAsync` or `CreateWithBaselineAsync`) with:
     - `WorkspaceId = workspace.Id`
     - `Title` prefixed for clarity (e.g. `Triage: <truncated report title>`)
     - `Description` holding the verbatim initial intake (see Section 3.2)
     - `Status = TaskOverallStatus.Triage`
     - `Origin = request.Origin`
     - `ExternalSource` / `ExternalReferenceId` propagated from `TriageReportRequest`
     - `BaseBranch = resolvedBaseBranch`
     - `FeatureBranch = resolvedBaseBranch`
     - `CurrentStageId = StandardStageIds.Triage`
     - `HasExecutionRun = true` (so the triage session is immediately discoverable in executed-task inspector queries)
     - `Priority = TaskPriority.Normal` (or report-appropriate default; `Triage` status is never queued or prioritized in backlog digests)
   - Separate `persistedTaskId` from `worktreeTaskId` in [`CrucibleAgentHarness.RunStageAsync`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) when `triageTask.Id > 0`:
     - `persistedTaskId` (`triageTask.Id`) binds conversations (`CrucibleConversationCoordinator.BeginAsync` sets `Conversation.TaskId = triageTask.Id`), broadcasts/tracing, `LlmSessionIds.ForStage`, and `<existing_workspace_tasks>` exclusion in `BuildBacklogDigestAsync`.
     - `worktreeTaskId` remains `null` for Triage (`SandboxScope.TaskId == null`), so `ResolveLocations` and `CanonicalToolScope` resolve paths against the workspace canonical clone / base branch rather than `/workspace/task-{id}`.
     - [`AutonomousTaskClassifier.IsAutonomousTask`](../../src/NightCodeForge.Core/Workflow/AutonomousTaskClassifier.cs) recognizes `taskId > 0` with `StageId == StandardStageIds.Triage` (it only excludes `StandardStageIds.Ideation`), so SignalR live streaming (`ThinkingDelta`, `MessageDelta`, `ToolStarted`, `ToolCompleted`) and active task telemetry work out of the box without modifying non-ideation autonomous classification rules—though UI consumers such as Kanban/Dashboard must filter by task status as specified below.
   - Upon triage completion (success or failure/cancellation), the `ForgeTask` remains in `TaskOverallStatus.Triage` (never promoted to `Suggested`, `Open`, or `Closed`), preserving its `Conversation` history, `ExecutionLogs`, and `CurrentStageId = StandardStageIds.Triage`. Failure of the triage run must still record the failure outcome on the `Conversation` and log/throw the triage exception cleanly to the caller.
   - Ensure `Triage` sessions never accidentally advance pipeline stages, never write task markdown files, and never enter [`ITaskJobQueue`](../../src/NightCodeForge.Core/Workflow/ITaskJobQueue.cs) / [`HangfireTaskJobQueue`](../../src/NightCodeForge.Infrastructure/BackgroundJobs/HangfireTaskJobQueue.cs), [`StartupRecoveryService`](../../src/NightCodeForge.Core/Services/StartupRecoveryService.cs), or [`DevQueueService`](../../src/NightCodeForge.Web/Services/DevQueueService.cs).

3. **Strict Exclusion from Backlog, Markdown Sync, DAG, Counters & Dispatch:**
   All non-inspector views and synchronization services must exclude `TaskOverallStatus.Triage`:
   - **Markdown Sync ([`TaskMarkdownSyncService.cs`](../../src/NightCodeForge.Core/Services/TaskMarkdownSyncService.cs)):**
     - Introduce a clear exclusion predicate (e.g., `IsNonSyncedStatus(TaskOverallStatus status)` or status check) so `TaskOverallStatus.Triage` is **never** written to `docs/tasks/` or `docs/tasks/archive/` by `SyncTaskToDiskAsync` or `SyncAllTasksToDiskAsync`, and never re-imported into active backlog statuses by `SyncAllTasksFromDiskAsync`.
     - Note: Only `TaskOverallStatus.Triage` session records are excluded from markdown sync; the actual `Suggested` tasks created or edited by the triage agent **must** be synced to `docs/tasks/` as normal backlog items.
   - **Kanban & Visual DAG ([`Backlog.razor`](../../src/NightCodeForge.Web/Components/Pages/Backlog.razor), [`BacklogStatusClassifier.cs`](../../src/NightCodeForge.Web/Services/BacklogStatusClassifier.cs)):**
     - `Backlog.razor` must filter out `t.Status == TaskOverallStatus.Triage` (alongside `TaskOverallStatus.Draft`) when loading `_allTasks` for both Kanban columns and [`DagVisualizer.razor`](../../src/NightCodeForge.Web/Components/Backlog/DagVisualizer.razor).
     - Explicitly handle `TaskOverallStatus.Triage` in `BacklogStatusClassifier.GetColumn`, `IsNonBacklog`, and `IsEligibleToRun` so `IsOpenBacklog` returns `false` and `Triage` tasks are never treated as `BacklogKanbanColumn.Open` or eligible to run.
   - **All Tasks Archive & Default Filters ([`Tasks.razor`](../../src/NightCodeForge.Web/Components/Pages/Tasks.razor), [`TaskTableFilterService.cs`](../../src/NightCodeForge.Web/Services/TaskTableFilterService.cs)):**
     - Exclude `TaskOverallStatus.Triage` in `Tasks.razor` (`LoadDataAsync`) and `TaskTableFilterService.FilterAndSort` for default backlog listings (`TaskStatusFilter.All` and backlog status filters) so the task ledger stays focused on deliverable backlog tasks (or expose an explicit optional filter / badge if needed, without appearing as an open/suggested work item). If someone navigates directly to a triage task by ID (or links from Inspector), its status badge renders cleanly (`🛡️ Triage`).
   - **Prompt Backlog Digest ([`WorkspaceBacklogSummaryService.cs`](../../src/NightCodeForge.Core/Services/WorkspaceBacklogSummaryService.cs)):**
     - `WorkspaceBacklogSummaryService.ActiveStatuses` already excludes negative statuses not explicitly listed; keep `TaskOverallStatus.Triage` excluded so `<existing_workspace_tasks>` only lists real backlog items.
   - **Dashboard Counters, Badges & Containers ([`HomeDashboardBuilder.cs`](../../src/NightCodeForge.Web/Services/HomeDashboardBuilder.cs), [`TaskContainerReaper.cs`](../../src/NightCodeForge.Core/Services/TaskContainerReaper.cs), [`TaskBadgeCounter.cs`](../../src/NightCodeForge.Core/Workflow/TaskBadgeCounter.cs)):**
     - `HomeDashboardBuilder.BuildSnapshotAsync` (`TotalTaskCount`, `lastTaskActivity`, `OpenTaskCount`, `SuggestedTaskCount`, `RunningTaskCount` unless specifically tracking triage) and `TaskBadgeCounter` must ignore `TaskOverallStatus.Triage` backlog counts so triage sessions do not inflate backlog/suggested badge counts or distort recent backlog activity.
     - `TaskContainerReaper.ShouldReapTaskContainer` must include `TaskOverallStatus.Triage` alongside `Draft` and `Suggested` so no ephemeral container ever lingers for a triage session.
   - **Execution Dispatch & Promotion ([`ForgeTask.cs`](../../src/NightCodeForge.Core/Domain/ForgeTask.cs), [`TaskLifecycleMutationCoordinator.cs`](../../src/NightCodeForge.Core/Workflow/TaskLifecycleMutationCoordinator.cs)):**
     - `ForgeTask.IsRunnable()` continues to return `false` for `TaskOverallStatus.Triage`.
     - `PromoteSuggestedTaskAsync` (and equivalent UI promotion actions) must reject attempts to promote a `TaskOverallStatus.Triage` task (`TaskCannotBeModifiedException` or `InvalidOperationException`), ensuring triage session tasks can never be promoted or dispatched into the coding pipeline.

---

### 3.2 Verbatim Initial Intake Persistence & Prompt-Injection Isolation

1. **Storing the Verbatim Initial Intake on the Triage Task:**
   - Instead of setting `triageTask.Description = NeutralTriageTaskDescription`, persist the verbatim initial intake submission on the `TaskOverallStatus.Triage` task's `Description` (combining the full intake content from `TriageReportRequest`: full report title, verbatim description/reproduction steps, stack trace if provided in the form/`BuildFullDescription()`, external source, external reference ID, and base branch).
   - Ensure no destructive truncation or lossy rewriting occurs on `Description` so reviewers inspecting the triage task later see the exact input that triggered the triage agent.

2. **Preserving Prompt-Injection Isolation ([`CrucibleAgentHarness.cs`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs)):**
   - Currently, `CrucibleAgentHarness.BuildTaskScope(context.ForgeTask, ...)` serializes `<task id="..." title="...">\n{task.Description}\n</task>` into Layer 3 (`taskScope`) of the stabilized system prompt prefix. Previously, `NeutralTriageTaskDescription` prevented untrusted report content from appearing inside Layer 3.
   - Because `triageTask.Description` now stores the **verbatim untrusted intake**, `CrucibleAgentHarness` MUST treat triage tasks specially:
     - When `context.ForgeTask.Status == TaskOverallStatus.Triage` or `context.StageId == StandardStageIds.Triage`, `BuildTaskScope` (or a triage-specific scope builder) MUST emit a **neutral** `<task>` metadata block containing only safe identifiers (`id`, `base_branch`, `origin`, and a neutral notice pointing to `<untrusted_external_report>` in the user prompt) and MUST **never** echo `ForgeTask.Title` or `ForgeTask.Description` into the system prompt prefix.
     - The verbatim intake report remains strictly enclosed inside the `<untrusted_external_report>` tags in `BootstrapPrompt` (with embedded `</untrusted_external_report>` tags escaped via `EscapeUntrustedReportTags`), accompanied by the explicit anti-injection directive.
   - For deterministic symbol map extraction (`DeterministicSymbolMap`), extracting identifier/path tokens from the verbatim intake text to pre-rank repository symbols remains acceptable because symbol-map generation only matches existing repository symbol names and does not execute instructions.

---

### 3.3 Triage-Scoped Backlog Mutation Tools (`propose_tasks`, `get_task`, `edit_task`, `remove_task`)

1. **Tool Whitelist, Default Profile & Security Boundaries ([`TriageService.cs`](../../src/NightCodeForge.Agents/Triage/TriageService.cs), [`CanonicalToolNames.cs`](../../src/NightCodeForge.Core/Domain/CanonicalToolNames.cs), [`DefaultAgentTools.cs`](../../src/NightCodeForge.Core/Domain/DefaultAgentTools.cs), [`AgentProfile.cs`](../../src/NightCodeForge.Core/Domain/AgentProfile.cs), [`AppDbContext.cs`](../../src/NightCodeForge.Infrastructure/Data/AppDbContext.cs)):**
   - Add `DefaultAgentTools.Triage` granting:
     - Codebase inspection & skills: `read`, `bash` (RestrictedReadOnly), `load_skill`
     - Scoped backlog management: `propose_tasks`, `get_task`, `edit_task`, `remove_task`
   - Keep `write`, `edit` (filesystem edits), `web_search`, `ask`, `request_changes`, `set_task_priority`, and `rework_priorities` **denied** in untrusted triage so the triage agent cannot alter source files, access external networks, block on human Q&A dialogs, or reorder unrelated team priorities.
   - Use `DefaultAgentTools.Triage` in `AgentProfile.CreateStandardGlobalProfiles` for `StandardStageIds.Triage`, and reconcile existing stored `triage` profiles (global and workspace-scoped) in `AppDbContext.EnsureDefaultGlobalAgentProfilesAsync` so `get_task`, `edit_task`, and `remove_task` are granted alongside `propose_tasks` while disallowed tools (`web_search`, `ask`) are omitted.
   - Align `UntrustedTriagePermittedTools` and `SanitizeReadOnlyTools` in `TriageService.cs` with `DefaultAgentTools.Triage`: preserve configured triage tools only within this triage allowlist and automatically guarantee `propose_tasks`, `get_task`, `edit_task`, and `remove_task` while stripping any disallowed tool or `AllowedTools` override in `HarnessConfigurationJson`.

2. **Live `IBacklogMutationToolService` Attachment & Constructor Ceiling Compliance:**
   - Replace the current hardcoded `AttachBacklogMutationService: false` in [`CrucibleAgentHarness.RunStageAsync`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) with a context-driven setting (via [`AgentSessionContext`](../../src/NightCodeForge.Core/Domain/AgentSessionContext.cs) / backlog mutation policy passed through `CanonicalToolsetRequest` and `CanonicalToolScope` / `BacklogToolScope`) that attaches `IBacklogMutationToolService` for Triage sessions (`StandardStageIds.Triage`) just as Ideation does, while keeping normal pipeline implementation/review stages unattached (`false`).
   - **Constructor Ceiling Constraint ([`AGENTS.md`](../../AGENTS.md)):** Both [`CrucibleAgentHarness`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) and [`BacklogMutationToolService`](../../src/NightCodeForge.Agents/Tools/BacklogMutationToolService.cs) (after adding `ITaskAttachmentRepository` in Step 12.2B) are already at the **10-parameter constructor ceiling** enforced by `AGENTS.md` and `ConstructorParameterCeilingTests`. Therefore, all triage scope metadata (`Actor`: `AuditActors.TriageAgent`, `SessionOrigin`: `request.Origin`, `DefaultBaseBranch`: resolved `baseBranch`, `ExternalSource`, `ExternalReferenceId`, `CloneDirectory`, and session-created/modified/removed task ID tracking) MUST flow through `BacklogToolScope` (and `CanonicalToolScope` / `CanonicalToolsetRequest` / `BacklogManagementToolModule`) rather than adding constructor parameters to `BacklogMutationToolService` or `CrucibleAgentHarness`, and without hardcoding `AuditActors.IdeationAgent` or `TaskOverallStatus.Draft`.

3. **Triage Mutation Semantics & Strict Ownership Guardrails ([`BacklogMutationToolService.cs`](../../src/NightCodeForge.Agents/Tools/BacklogMutationToolService.cs)):**
   - **`propose_tasks` (in Triage scope):**
     - Persists each proposed task **immediately** into SQLite during the tool call in status **`TaskOverallStatus.Suggested`** (never `Draft`, and never `Open`—maintainer promotion remains mandatory for untrusted intake).
     - Populates `Origin` from the triage request (`TaskOrigin.UntrustedIntake` or `TaskOrigin.GitHubIssue`), `BaseBranch` from the triage session's target branch, `ExternalSource`, `ExternalReferenceId`, `Priority`, `MergeMode = MergeMode.ManualReviewRequired` (or validated requested merge mode within security constraints), `CreatedBy = AuditActors.TriageAgent`, and explicit `DependsOnIds` / `DependsOnTitles`.
     - Syncs created/edited/removed `Suggested`/`Cancelled` tasks in Triage scope immediately via `_markdownSyncService` to host `workspace.RepositoryPath` (and mirrors to `cloneDir` passed via `BacklogToolScope.CloneDirectory` when `Ext4MirrorPath` or a working clone exists, matching `TriageService`), and broadcasts activity under the active triage context.
     - Returns structured JSON containing the assigned numeric `id` (alongside title, status, priority, prerequisites) to the LLM so the agent knows every task `#ID` it created and can refine or remove it later in the same session.
   - **`get_task` (in Triage scope):**
     - Allowed for any task belonging to `scope.WorkspaceId` so the triage agent can read full descriptions, external references, origins, statuses, and dependency links of existing tasks before deciding whether an intake report duplicates or enriches an existing task.
     - Include `origin` (`Manual`, `Ideation`, `UntrustedIntake`, `GitHubIssue`, `Import`) in the JSON payload returned by `get_task` so Triage can see whether a task is triage-owned.
   - **`edit_task` (in Triage scope):**
     - Permitted **only** if ALL of the following invariants hold on the target task:
       1. Task belongs to `scope.WorkspaceId` and is not locked by a running pipeline stage.
       2. `task.Status` is `TaskOverallStatus.Suggested or TaskOverallStatus.Open`.
       3. `task.HasExecutionRun == false` (not yet started in the pipeline).
       4. **`task.Origin is TaskOrigin.UntrustedIntake or TaskOrigin.GitHubIssue`** (created by Triage, either earlier in the **current triage session** OR in a **previous triage session**).
     - Attempting to call `edit_task` from Triage on a `TaskOverallStatus.Triage` session task itself, on a `Manual` / `Ideation` / `Import` task, or on any task where `HasExecutionRun == true` or `Status` is outside `{Suggested, Open}` MUST fail fast with [`TaskCannotBeModifiedException`](../../src/NightCodeForge.Core/Exceptions/TaskCannotBeModifiedException.cs) (surfaced cleanly as an actionable `Error: ...` tool message by [`BacklogManagementToolModule`](../../src/NightCodeForge.Agents/Tools/BacklogManagementToolModule.cs)).
     - Permitted edits update `Title`, `Description`, `Priority`, `DependsOnIds` (validated against circular dependencies and workspace membership), `MergeMode`, and `BaseBranch`, record `ForgeTaskHistory` under `AuditActors.TriageAgent`, and sync updated markdown to `docs/tasks/`.
   - **`remove_task` (in Triage scope):**
     - Subject to the exact same ownership & unstarted guardrails as `edit_task` (`Status is Suggested or Open`, `!HasExecutionRun`, `Origin is UntrustedIntake or GitHubIssue`), plus existing active-dependent checks.
     - Transitions the task to `TaskOverallStatus.Cancelled`, records `ForgeTaskHistory` under `AuditActors.TriageAgent` with the provided removal reason, and archives/syncs `docs/tasks/` as already performed by `BacklogMutationToolService.RemoveTaskAsync`.
     - Note: To prevent indirect prompt injection from maliciously tampering with unrelated backlog items, maintain clear validation error messages when a non-triage or started task is targeted, and ensure Ideation (`AuditActors.IdeationAgent`) keeps its full existing permissions (`Draft`, `Suggested`, `Open`).

4. **Backlog Digest Visibility & Triage Prompt Directive ([`CrucibleAgentHarness.cs`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs), [`WorkspaceBacklogSummaryService.cs`](../../src/NightCodeForge.Core/Services/WorkspaceBacklogSummaryService.cs), [`TriageService.cs`](../../src/NightCodeForge.Agents/Triage/TriageService.cs)):**
   - Inject `<existing_workspace_tasks>` from `IWorkspaceBacklogSummaryService.BuildDigestAsync(workspace.Id, excludeTaskId: triageTask.Id, ...)` during Triage runs (just as Stage 1 `TaskReview` does) so the triage agent sees existing workspace tasks up front without wasting turns listing markdown files.
   - Include each task's `Origin` in the `<existing_workspace_tasks>` summary format (e.g., `- #42 [P2/High - Suggested - Origin:UntrustedIntake]: ...`) or clearly indicate triage-created items so the agent immediately knows which existing tasks it is permitted to refine via `get_task` / `edit_task` / `remove_task`.
   - Update the Triage `StageDirective` and `BootstrapPrompt` (in [`TriageService.cs`](../../src/NightCodeForge.Agents/Triage/TriageService.cs) and [`TriageStage.cs`](../../src/NightCodeForge.Core/Workflow/Stages/TriageStage.cs)):
     - Direct the agent to check `<existing_workspace_tasks>` and `get_task` first for related or previously triaged issues.
     - If an unstarted triage task (`Origin: UntrustedIntake` or `GitHubIssue`) from a prior session already covers the same issue and the new intake adds new reproduction steps, logs, or affected files, direct it to **update that task via `edit_task`** instead of creating a duplicate.
     - If the agent refines, splits, merges, or re-scopes tasks it created earlier **in the current session**, direct it to use **`edit_task` or `remove_task`** using the `#ID` returned by `propose_tasks` rather than calling `propose_tasks` again to emit duplicates.
     - If an identical issue already exists as an active `Manual`/`Ideation` or already-started task, direct it not to create a redundant task (and note any new observations cleanly if appropriate without violating mutation boundaries).

5. **Session Outcome Tracking & Consolidated Return Value:**
   - When `IBacklogMutationToolService` handles `propose_tasks` / `edit_task` / `remove_task` live during the triage session, track the set of task IDs created or modified during that triage session via `BacklogToolScope` / `BacklogManagementToolModule` session state (excluding any task created and subsequently cancelled/removed via `remove_task` during the same session).
   - `ITriageService.TriageExternalReportAsync` reloads and returns the surviving created and updated `ForgeTask` entities (in deterministic order) without duplicating `PendingProposals` entries that already carry `TaskId > 0`, while still persisting fallback `PendingProposals` entries where `TaskId == null` (for example, fake harnesses in unit tests), so callers and [`Triage.razor`](../../src/NightCodeForge.Web/Components/Pages/Triage.razor) display the accurate final result of the session—without duplicates from intermediate self-corrections—and distinguish updated tasks from newly created ones if desired in [`TriageTaskProposalCard.razor`](../../src/NightCodeForge.Web/Components/Triage/TriageTaskProposalCard.razor).
   - Synchronize [`TaskBadgeCounter`](../../src/NightCodeForge.Core/Workflow/TaskBadgeCounter.cs) after the triage session finishes whenever backlog tasks were created, edited, or removed.

---

### 3.4 Watching Triage in Task Execution Inspector (Without Stepper) & Breaking the `Triage.razor` Black Box

> **UI & Presentation Separation ([`AGENTS.md`](../../AGENTS.md)):** Keep `.razor` components (`TaskInspector.razor`, `TaskExecutionInspector.razor`, `Triage.razor`) focused on layout, rendering, and user interaction; place non-trivial triage inspector/status presentation, grouping, badge formatting, and label mapping logic in dedicated `NightCodeForge.Web.Services` helpers (`TaskInspectorGroupingHelper`, `StageConversationParser`, etc.) so they can be unit tested without UI rendering context.

1. **Inspector Discovery & Grouping ([`TaskInspector.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskInspector.razor), [`TaskInspectorGroupingHelper.cs`](../../src/NightCodeForge.Web/Services/TaskInspectorGroupingHelper.cs)):**
   - Because the persisted `TaskOverallStatus.Triage` task has `HasExecutionRun = true`, `EfCoreTaskRepository.GetExecutedTasksByWorkspaceIdAsync(workspaceId)` automatically includes it when a workspace is active.
   - Update the global fallback query in `TaskInspector.razor` (when no active workspace is selected) to also query `TaskOverallStatus.Triage` alongside `Open`, `Reviewed`, `Implemented`, `Ready`, `Closed`, and `Failed`.
   - Update `TaskInspectorGroupingHelper`:
     - Add a dedicated group constant (e.g., `GroupTriage = "🛡️ Triage"`).
     - When a `TaskOverallStatus.Triage` task is currently executing (`activeTaskId` / `runningTaskIds`), place it in `GroupRunning` (`🚀 Running`) so operators immediately see an in-progress triage session at the top.
     - When a `TaskOverallStatus.Triage` task has finished executing, place it in `GroupTriage` (`🛡️ Triage`) rather than `GroupOpen` or `GroupClosed`, so multi-stage pipeline tasks remain visually distinct from triage sessions.

2. **Single-Stage Triage Rendering in [`TaskExecutionInspector.razor`](../../src/NightCodeForge.Web/Components/Monitoring/TaskExecutionInspector.razor):**
   - Detect triage session tasks via a shared presentation helper or `IsTriageTask` (`_task.Status == TaskOverallStatus.Triage || string.Equals(_task.CurrentStageId, StandardStageIds.Triage, StringComparison.OrdinalIgnoreCase)`).
   - When `IsTriageTask` is `true`:
     - **Skip Default Workflow Loading:** In `LoadTaskDataAsync`, skip calling `WorkflowRepository.GetDefaultWorkflowWithStagesAsync()` when `_task.Workflow == null`, and **omit `<PipelineStepper>` completely** (triage is a single-stage inspection session, not a multi-stage coding workflow).
     - **Omit workflow-only header controls and tabs:**
       - Hide both `Retry Stage` and `Restart Pipeline` (triage re-runs originate from Triage / Companion intake rather than the coding orchestrator).
       - Hide the `Workflow:` badge if irrelevant or label it clearly as `🛡️ Untrusted Intake Triage (RestrictedReadOnly)`.
       - Hide `🔀 Git Diff & Changes` tab (read-only triage on base branch never creates commits or diffs) and reset `_activeTab` to `"conversations"` if `"diff"` was active when switching to a triage task.
     - **Adapt the Header Metadata & Overview Tab:**
       - In the header subtitle and `Task Overview & Requirements` tab, hide `Feature Branch` and `Merge Mode` (triage never checks out a feature branch or merges code), while displaying `Base Branch`, `Origin`, `External Source` / `External Reference ID` (when present), and **Full Initial Intake Report** (rendering the verbatim `Description`).
     - **Preserve Live Observability Tabs:**
       - Keep **💬 Stage Conversations** (with live SignalR streaming of thinking deltas, assistant messages, `read`/`bash`/`propose_tasks`/`get_task`/`edit_task`/`remove_task` tool calls & outputs), **🖥 Terminal & Logs**, and **🕰️ Revisions & History**.
     - **Stage Label Formatting ([`StageRunSelector.razor`](../../src/NightCodeForge.Web/Components/Monitoring/StageRunSelector.razor), [`StageConversationParser.cs`](../../src/NightCodeForge.Web/Services/StageConversationParser.cs)):**
       - Map `StandardStageIds.Triage` to a human-readable stage title (`"Untrusted Intake Triage"`) in `StageConversationParser.FormatStageName` (delegated to by `StageRunSelector.razor`) instead of raw `"triage"`.

3. **Immediate Inspector Link in [`Triage.razor`](../../src/NightCodeForge.Web/Components/Pages/Triage.razor) & Companion Drafts ([`ITriageService.cs`](../../src/NightCodeForge.Core/Workflow/ITriageService.cs), [`CompanionEndpoints.cs`](../../src/NightCodeForge.Web/Endpoints/CompanionEndpoints.cs), [`ICompanionTriageDraftStore`](../../src/NightCodeForge.Core/Services/CompanionTriageDraftStore.cs)):**
   - Surface the persisted `TriageTask.Id` to callers **as soon as the triage `ForgeTask` is created** (before the potentially multi-minute `RunStageAsync` call finishes)—for example via a callback/progress parameter on `TriageExternalReportAsync` or a `TriageSessionHandle` / `CompanionTriageDraft.TriageTaskId` update.
   - While `_isTriaging` is `true` in `Triage.razor`:
     - Keep the isolation badges (`Network: Isolated`, `Mount: Read-Only`, `Base: ...`) and add a prominent primary link/button in `#triage-running-card`:
       - **"🔎 Watch Live in Task Inspector (#{TriageTaskId}) ➔"** pointing to `WorkspaceState.GetPath($"inspector/{TriageTaskId}")` (opening live streaming conversation & tool calls immediately, without losing background triage execution if launched via background/companion task or opened in another tab; consider preserving/running manual triage via a background-safe scope or opening inspector in a new tab with `target="_blank"` when `StartTriageAsync` is awaiting in-circuit so Blazor circuit navigation does not cancel an in-flight `CancellationToken`).
       - Important concurrency detail: When `Triage.razor` triggers `StartTriageAsync` from the browser circuit, navigating away from `/triage` in the same Blazor circuit disposes the component! If navigation happens in-tab while triage is bound only to the component, it could interrupt feedback unless either the triage execution uses a non-circuit-aborted token / background coordinator or the **"Watch Live in Task Inspector"** action opens cleanly in a new tab / detaches safely while the server completes triage. Either mechanism is permitted so long as clicking "Watch Live" never aborts an in-flight triage run unintentionally.
   - After triage completes in `Triage.razor`:
     - Display a secondary button/link in the results header (`"🔎 Inspect Triage Session (#{TriageTaskId})"`) alongside `"Go to Backlog ➔"`, and display clear visual distinction when resulting tasks were **Updated** vs **Created** during the session.
   - **NightCodeAssay Companion Handoff ([`CompanionEndpoints.cs`](../../src/NightCodeForge.Web/Endpoints/CompanionEndpoints.cs)):**
     - `StartBackgroundTriageAsync` runs inside `IServiceScopeFactory` under `appLifetime.ApplicationStopping`. Passing the `onTriageTaskCreated` callback updates the `CompanionTriageDraft` with the `TriageTaskId` immediately, so opening `/w/{workspaceId}/triage?draftId={id}` shows the live Task Inspector link right away—and because companion triage already runs independently of the Blazor circuit, opening Task Inspector in the same or new tab will never interrupt it.

---

## 4. Verification & Definition of Done (DoD)

1. **Automated Unit & Integration Tests (`dotnet test --filter "Category!=Acceptance&Category!=E2E"`):**
   - **Triage Task Lifecycle & Exclusions:**
     - [x] Verify `TriageService.TriageExternalReportAsync` persists a `ForgeTask` with `Status == TaskOverallStatus.Triage`, `HasExecutionRun == true`, `CurrentStageId == StandardStageIds.Triage`, and verbatim intake stored in `Description` **before** calling `harness.RunStageAsync`.
     - [x] Verify no extra bogus `Suggested` task is left behind (`Suggested` count only reflects real proposals).
     - [x] Update existing [`TriageServiceTests.cs`](../../tests/NightCodeForge.Agents.Tests/TriageServiceTests.cs) tests (`MisconfiguredTriageToolProfiles`, `TriageExternalReportAsync_WhenNoProposalsMade_ReturnsEmptyList`, `TriageExternalReportAsync_WhenHarnessFails_ThrowsInvalidOperationExceptionAndCreatesNoTasks`) to account for the persisted `TaskOverallStatus.Triage` session task (zero `Suggested` tasks left behind on no-proposal/failure) and live `IBacklogMutationToolService` triage tool attachment, and verify both live tool-persisted tasks (`TaskId > 0`) without duplication and fallback `PendingProposals` (`TaskId == null`).
     - [x] Verify `TaskMarkdownSyncService.SyncAllTasksToDiskAsync` and `SyncTaskToDiskAsync` skip `TaskOverallStatus.Triage` (no `.md` file emitted under `docs/tasks/`).
     - [x] Verify `BacklogStatusClassifier`, `TaskInspectorGroupingHelper`, `TaskTableFilterService`, `HomeDashboardBuilder`, `ForgeTask.IsRunnable()`, and `TaskContainerReaper` handle `TaskOverallStatus.Triage` as specified (excluded from Kanban/DAG/backlog counts/runnable dispatch; included in Inspector under `Running` or `Triage` group).
   - **Prompt-Injection Isolation:**
     - [x] Verify `CrucibleAgentHarness.BuildTaskScope` does **not** include raw `ForgeTask.Title` or `ForgeTask.Description` in Layer 3 `<task>` system instructions when `task.Status == TaskOverallStatus.Triage`, while keeping verbatim intake in `<untrusted_external_report>` with escaped closing tags.
   - **Scoped Backlog Mutation Tools (`propose_tasks`, `get_task`, `edit_task`, `remove_task`):**
     - [x] Verify `DefaultAgentTools.Triage`, `AgentProfile.CreateStandardGlobalProfiles`, and `AppDbContext.EnsureDefaultGlobalAgentProfilesAsync` grant `get_task`, `edit_task`, `remove_task`, and `propose_tasks` to triage profiles without exposing `write`, `edit`, `web_search`, `ask`, `request_changes`, `set_task_priority`, or `rework_priorities`.
     - [x] Verify Triage `propose_tasks` immediately persists `TaskOverallStatus.Suggested` tasks with `Origin` matching the triage request (`UntrustedIntake` / `GitHubIssue`), `CreatedBy = AuditActors.TriageAgent`, syncs to `docs/tasks/`, and returns assigned `#ID`s.
     - [x] Verify same-session self-correction: calling `propose_tasks`, then `edit_task` or `remove_task` on the returned `#ID`, updates or cancels that task instead of creating duplicates, and `TriageExternalReportAsync` returns only surviving tasks with updated fields.
     - [x] Verify cross-session refinement: an unstarted task (`Suggested` or `Open`, `HasExecutionRun == false`) created in an earlier triage session (`Origin: UntrustedIntake` or `GitHubIssue`) can be read via `get_task` and updated/removed via `edit_task`/`remove_task` by a later triage session.
     - [x] Verify ownership & state protection: calling `edit_task` or `remove_task` in Triage scope on a `TaskOrigin.Manual`, `TaskOrigin.Ideation`, `TaskOrigin.Import` task, on a `TaskOverallStatus.Triage` session task, or on any task with `HasExecutionRun == true` fails fast with `TaskCannotBeModifiedException`.
     - [x] Verify `WorkspaceBacklogSummaryService` digest includes task `Origin` and is injected into Triage prompts (excluding the triage task itself and `TaskOverallStatus.Triage` records).
   - **UI & Inspector Components (`NightCodeForge.Web.Tests`):**
     - [x] Verify `TaskExecutionInspector` skips `WorkflowRepository.GetDefaultWorkflowWithStagesAsync()` and does **not** render `<PipelineStepper>`, `Retry Stage`, `Restart Pipeline`, or `Git Diff & Changes` when inspecting a `TaskOverallStatus.Triage` task, while rendering `Stage Conversations`, verbatim intake description in `Task Overview & Requirements`, `Terminal & Logs`, and `Revisions & History`.
     - [x] Verify `Triage.razor` surfaces the live **"Watch Live in Task Inspector (#ID)"** link as soon as the triage task ID is created while `_isTriaging` is active and after completion.
2. **Full Suite Check:**
   - [x] Upon finishing implementation of Step 12.38, run the full test suite (without skipping E2E per project policy) and confirm all tests pass (`dotnet test`: 5,011 passed, 0 failed, 0 skipped across Cli, Core, Infrastructure, Agents, and Web test assemblies).

---

## 5. Implementation Checklist & Deliverables

- [x] **Dedicated `TaskOverallStatus.Triage` Lifecycle & Non-Backlog Exclusions (`NightCodeForge.Core`, `NightCodeForge.Infrastructure`, `NightCodeForge.Web`):**
  - Added `TaskOverallStatus.Triage = -2` in [`Enums.cs`](../../src/NightCodeForge.Core/Domain/Enums.cs) and preserved empty `FeatureBranch` on [`EfCoreTaskRepository.CreateWithBaselineAsync`](../../src/NightCodeForge.Infrastructure/Data/Repositories/EfCoreTaskRepository.cs) for `Triage` session tasks.
  - Guarded [`TaskLifecycleMutationCoordinator`](../../src/NightCodeForge.Core/Workflow/TaskLifecycleMutationCoordinator.cs), [`TaskMutationService`](../../src/NightCodeForge.Web/Services/TaskMutationService.cs), and [`TaskCreationUiService`](../../src/NightCodeForge.Web/Services/TaskCreationUiService.cs) against modifying, promoting, rejecting, or linking `Triage` session tasks as dependencies.
  - Excluded `TaskOverallStatus.Triage` from Markdown disk export/import and dependency resolution ([`TaskMarkdownSyncService.cs`](../../src/NightCodeForge.Core/Services/TaskMarkdownSyncService.cs), [`TaskMarkdownValidator.cs`](../../src/NightCodeForge.Core/Services/TaskMarkdownValidator.cs)), Kanban/DAG/task filters/dashboard counts/reaper/worktree pruning ([`BacklogStatusClassifier.cs`](../../src/NightCodeForge.Web/Services/BacklogStatusClassifier.cs), [`HomeDashboardBuilder.cs`](../../src/NightCodeForge.Web/Services/HomeDashboardBuilder.cs), [`TaskBadgeCounter.cs`](../../src/NightCodeForge.Core/Workflow/TaskBadgeCounter.cs), [`TaskContainerReaper.cs`](../../src/NightCodeForge.Core/Services/TaskContainerReaper.cs), [`GitSyncService.cs`](../../src/NightCodeForge.Web/Services/GitSyncService.cs), [`IdeationProposalCard.razor`](../../src/NightCodeForge.Web/Components/Ideation/IdeationProposalCard.razor)), and GitHub mirror sibling checks ([`GitHubTaskMirrorService.cs`](../../src/NightCodeForge.Infrastructure/GitHub/GitHubTaskMirrorService.cs)).
- [x] **Upfront Verbatim Intake Persistence & Prompt-Injection Isolation (`NightCodeForge.Agents`):**
  - Updated [`TriageService.cs`](../../src/NightCodeForge.Agents/Triage/TriageService.cs) to validate harness availability up front, persist the `TaskOverallStatus.Triage` record (with verbatim initial intake including `Base Branch`, `ExternalSource`, and `ExternalReferenceId`) before `RunStageAsync`, emit `onTriageTaskCreated`, and synchronize badge counts cleanly in `finally` on abnormal exit after live or fallback mutations.
  - Hardened [`CrucibleAgentHarness.cs`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) so `isTriageSession` always enforces `BacklogMutationSessionPolicy.ForTriage(...)`, strict `DefaultAgentTools.Triage` allowlist filtering, read-only workspace mounting (`worktreeTaskId = null` separated from `persistedTaskId`), and neutral Layer 3 `<task>` scope framing pointing to `<untrusted_external_report>` without echoing raw `Title`/`Description`.
- [x] **Triage-Scoped Backlog Mutation Tools & Prompt Digest (`NightCodeForge.Core`, `NightCodeForge.Infrastructure`, `NightCodeForge.Agents`):**
  - Added [`BacklogMutationSessionPolicy.cs`](../../src/NightCodeForge.Core/Domain/BacklogMutationSessionPolicy.cs) and `DefaultAgentTools.Triage` ([`DefaultAgentTools.cs`](../../src/NightCodeForge.Core/Domain/DefaultAgentTools.cs), [`AgentProfile.cs`](../../src/NightCodeForge.Core/Domain/AgentProfile.cs), [`AppDbContext.cs`](../../src/NightCodeForge.Infrastructure/Data/AppDbContext.cs)) granting `propose_tasks`, `get_task`, `edit_task`, and `remove_task` without exposing `write`, `edit`, `web_search`, `ask`, `request_changes`, `set_task_priority`, or `rework_priorities`.
  - Wired [`BacklogMutationToolService.cs`](../../src/NightCodeForge.Agents/Tools/BacklogMutationToolService.cs) and [`BacklogManagementToolModule.cs`](../../src/NightCodeForge.Agents/Tools/BacklogManagementToolModule.cs) (preserving the 10-parameter constructor ceiling via `BacklogToolScope` / `CanonicalToolScope`) to persist immediate `Suggested` proposals (`Origin: UntrustedIntake` / `GitHubIssue`), enforce cross-session ownership & unstarted guardrails on `edit_task`/`remove_task`, protect `SessionTaskId`, block `MergeMode.Automatic`, isolate workspace attachments (skipping `@attachment:{id}` extraction from untrusted single and JSON-batch descriptions while rejecting explicit attachment links), and track surviving session task IDs accurately.
  - Updated [`WorkspaceBacklogSummaryService.cs`](../../src/NightCodeForge.Core/Services/WorkspaceBacklogSummaryService.cs) to include task `Origin` in `<existing_workspace_tasks>` digests injected during Triage while excluding `TaskOverallStatus.Triage` session records.
- [x] **Inspector Visibility, Live Progress Link & Companion Handoff (`NightCodeForge.Web`):**
  - Added [`TaskInspectorPresentationHelper.cs`](../../src/NightCodeForge.Web/Services/TaskInspectorPresentationHelper.cs) and updated [`TaskInspector.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskInspector.razor), [`TaskInspectorGroupingHelper.cs`](../../src/NightCodeForge.Web/Services/TaskInspectorGroupingHelper.cs), [`TaskExecutionInspector.razor`](../../src/NightCodeForge.Web/Components/Monitoring/TaskExecutionInspector.razor), [`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor), and [`StageConversationParser.cs`](../../src/NightCodeForge.Web/Services/StageConversationParser.cs) for single-stage triage inspection (`🛡️ Triage` / `🚀 Running`, no `<PipelineStepper>`, hidden coding/diff/mutable controls, verbatim `Full Initial Intake Report`, and live conversations/logs/history).
  - Updated [`TriageViewModels.cs`](../../src/NightCodeForge.Web/Services/TriageViewModels.cs) (`TriagePresentationHelper`), [`Triage.razor`](../../src/NightCodeForge.Web/Components/Pages/Triage.razor), [`TriageTaskProposalCard.razor`](../../src/NightCodeForge.Web/Components/Triage/TriageTaskProposalCard.razor), and [`CompanionEndpoints.cs`](../../src/NightCodeForge.Web/Endpoints/CompanionEndpoints.cs) for immediate **"Watch Live in Task Inspector (#ID)"** links, disposal-safe UI dispatch, accurate proposal status badges, and background task exception observation.
- [x] **Regression & Full-Suite Test Coverage:**
  - Added and verified [`Step1238TriageLifecycleIsolationTests.cs`](../../tests/NightCodeForge.Core.Tests/Step1238TriageLifecycleIsolationTests.cs), [`Step1238TriageInfrastructureTests.cs`](../../tests/NightCodeForge.Infrastructure.Tests/Step1238TriageInfrastructureTests.cs), [`Step1238TriageBacklogMutationAndHarnessTests.cs`](../../tests/NightCodeForge.Agents.Tests/Step1238TriageBacklogMutationAndHarnessTests.cs), [`TriageServiceTests.cs`](../../tests/NightCodeForge.Agents.Tests/TriageServiceTests.cs), [`CompanionEndpointsTests.cs`](../../tests/NightCodeForge.Web.Tests/CompanionEndpointsTests.cs), [`Step1238TriageWebAndInspectorTests.cs`](../../tests/NightCodeForge.Web.Tests/Step1238TriageWebAndInspectorTests.cs), and [`TriagePageTests.cs`](../../tests/NightCodeForge.Web.Tests/TriagePageTests.cs) with all tests passing (`dotnet test`: 5,011 passed, 0 failed, 0 skipped).
