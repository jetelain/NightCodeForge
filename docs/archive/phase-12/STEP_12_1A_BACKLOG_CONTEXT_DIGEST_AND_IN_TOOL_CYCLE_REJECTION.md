# 🔗 Step 12.1A: Backlog Context Digest, Agent Dependency Directives & In-Tool Cycle Rejection

> **Feature Implementation Step (Part 1 of 2 — Backlog Context & Agent Validation Layer)**  
> *Parent Plan:* [Phase 12 Master Plan](../12_EXTEND_FEATURES.md)  
> *Companion Step:* [Step 12.1B: Orchestrator Prerequisite Suspension & Blocker UI Badges](../../features/STEP_12_1B_ORCHESTRATOR_PREREQUISITE_SUSPENSION_AND_BLOCKER_UI.md)  
> *Target Components:* `src/NightCodeForge.Agents/Ideation`, `src/NightCodeForge.Agents/Crucible`, `src/NightCodeForge.Agents/Tools`, `src/NightCodeForge.Core/Services`, `src/NightCodeForge.Web/Services` | *Status:* ✅ Completed | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../../AGENTS.md), [docs/PLAN.md](../../PLAN.md)

---

## 1. Executive Objective

In real-world repositories with active backlogs (such as **OllamaRouter** and **ComicGenCLI**), tasks are rarely completely independent. While NightCodeForge's database and queue orchestrator already support DAG-based execution via [`TaskDependency`](../../src/NightCodeForge.Core/Domain/TaskDependency.cs), agents currently operate mostly in isolation without proactive awareness of other tasks in the workspace.

This step (Part 1 of Step 12.1) instructs and equips both the **Ideation Agent** and the **Stage 1 (Task Review) Agent** to:
1. **Understand Workspace Backlog Context:**
   - Ingest a lightweight, token-optimized summary of active workspace tasks (capped at top 15–20 tasks ordered by priority, strictly excluding archived/closed tasks).
   - Position the context within **Layer 3 (Task Scope)** of [`PromptPrefixStabilizer`](../../src/NightCodeForge.Agents/Optimizations/PromptPrefixStabilizer.cs) to prevent invalidating the 90%+ KV cache hit rate across local LLM inference engines.
2. **Detect Semantic & Architectural Dependencies in Ideation:**
   - Automatically analyze newly proposed tasks against existing backlog items.
   - Populate `dependsOnTitles` or numeric task IDs in [`TaskProposal`](../../src/NightCodeForge.Core/Domain/ForgeTask.cs) / `propose_tasks` calls, resolving both titles and integer task IDs (`#101`, `101`) in [`IdeationSessionState`](../../src/NightCodeForge.Web/Services/IdeationSessionState.cs).
3. **Verify & Document Dependencies in Stage 1 (Task Review):**
   - The Task Review agent explicitly inspects whether the task relies on APIs, models, or schema changes from another task that is not yet merged, updating frontmatter `depends_on: [...]` and documenting architectural rationale under `## Dependencies`.
4. **Detect & Reject Circular Dependencies Fail-Fast (In-Tool Interception):**
   - Prohibit circular dependency links (`A -> B -> A`). Intercept modifications to `docs/tasks/*.md` in [`CanonicalTools`](../../src/NightCodeForge.Agents/Tools/CanonicalTools.cs) before disk writes to immediately return an in-turn tool error to the agent, enabling self-correction before stage completion.

*(Dynamic task suspension in `TaskOrchestrator`, stale branch cleanup, and Kanban/Drawer blocker badges are specified in [Step 12.1B](STEP_12_1B_ORCHESTRATOR_PREREQUISITE_SUSPENSION_AND_BLOCKER_UI.md).)*

---

## 2. Backlog Context Injection

### 2.1 Backlog Scalability, Archival Filtering & Active Task Capping
To keep the backlog context lean (~250–350 tokens total) and sharply focused on actionable work, `IWorkspaceBacklogSummaryService` enforces the following ingestion policy:

1. **Strict Exclusion of Archived & Terminal Tasks:**
   - Tasks in terminal states (`TaskOverallStatus.Closed`, `TaskOverallStatus.Rejected`, `TaskOverallStatus.Cancelled`) are **excluded entirely**.
   - **Rationale:** Code from `Closed` (merged) tasks already exists directly in the repository filesystem on disk. Agents can and should inspect the codebase using standard tools (`read`, `grep`, `bash`).

2. **Active Task Selection & Priority Ordering:**
   - Only active tasks are considered (`TaskOverallStatus.Suggested`, `Open`, `Reviewed`, `Implemented`, `Ready`).
   - Active candidates are ordered deterministically by:
     1. `Priority ASC` (`TaskPriority.Critical` [1] $\rightarrow$ `TaskPriority.High` [2] $\rightarrow$ `TaskPriority.Normal` [3] $\rightarrow$ `TaskPriority.Low` [4] $\rightarrow$ `TaskPriority.Trivial` [5]).
     2. `UpdatedAt DESC` (most recently modified tasks within the same priority tier).

3. **Configurable Cap (15–20 Tasks):**
   - The digest caps active tasks to a maximum limit (default: **20 tasks**, configurable via options).
   - If the workspace has more active tasks than the cap, an explicit overflow notice is appended to the digest indicating how many tasks were omitted:
     ```xml
     <!-- Showing top 20 of 64 active tasks by priority order (P1 Critical -> P5 Trivial). Lower-priority tasks omitted. -->
     ```

### 2.2 Compact Backlog Digest Format
To minimize token consumption (~12–15 tokens per task), a lean XML block is injected into the agent's instructions:

```xml
<existing_workspace_tasks>
<!-- Showing top 3 of 14 active tasks by priority order (excluding archived/closed tasks) -->
- #101 [P1/Critical - Ready]: Core LLM HTTP client abstractions
- #102 [P2/High - Open]: Implement Ollama streaming response parser
- #103 [P3/Normal - Suggested]: Add CLI command for model benchmarking
</existing_workspace_tasks>
```

### 2.3 KV Cache Protection via Prompt Layering
NightCodeForge uses a strict 3-layer prompt structure in [`PromptPrefixStabilizer`](../../src/NightCodeForge.Agents/Optimizations/PromptPrefixStabilizer.cs) to maximize KV cache hit rates (>90%) on local LLMs:
- **Layer 1 (Global Rules):** Shared across all stages and tasks.
- **Layer 2 (Stage Persona):** Shared across all tasks for a given stage.
- **Layer 3 (Task Scope):** Task-specific execution context and environment.

> [!IMPORTANT]
> Because `<existing_workspace_tasks>` changes whenever any backlog task is created, reprioritized, or transitioned, placing it in Layer 1 or Layer 2 would invalidate the prefix cache across the entire workspace on every task change.
> 
> Therefore, for Stage 1 (Task Review), `<existing_workspace_tasks>` **MUST be injected strictly within Layer 3 (Task Scope)** (appended to `taskScopeAndPlan` in `BuildLayer3TaskScope`). For Ideation, it is placed in the task guidance / workspace context section.

### 2.4 Service & Constructor Hygiene (`AGENTS.md` Compliance)
- `IWorkspaceBacklogSummaryService` (`WorkspaceBacklogSummaryService`) must have strictly $\le 2$ required constructor parameters (`ITaskRepository`, `IDependencyResolutionEngine`).
- `PromptPrefixStabilizer` has 0 constructor parameters and exposes static methods; pass the precomputed `<existing_workspace_tasks>` digest string into `BuildLayer3TaskScope(...)` / `AssembleStabilizedPrompt(...)` rather than injecting repositories into `PromptPrefixStabilizer`.
- Do **not** add new constructor parameters to `CanonicalTools`: attach an `ITaskMarkdownValidator? TaskMarkdownValidator { get; set; }` property on `CanonicalTools` (matching the existing `AskHandler` / `RequestChangesHandler` property pattern), where `TaskMarkdownValidator` has $\le 3$ required constructor parameters (`ITaskRepository`, `IDependencyResolutionEngine`, `ILogger<TaskMarkdownValidator>`).
- If [Step 11.38C](../fixes/STEP_11_38C_CRUCIBLE_AND_IDEATION_SHARED_SESSION_AND_TOOLSET_FACTORIZATION.md) has already been executed, inject `IWorkspaceBacklogSummaryService` into `ICruciblePromptComposer` and `ITaskMarkdownValidator` into `ICanonicalToolSetFactory`. If `Step 12.1A` is implemented before `Step 11.38C`, fold `IWorkspaceBacklogSummaryService` into a lightweight prompt collaborator so `CrucibleAgentHarness` and `IdeationService` constructor parameter counts do not increase.

---

## 3. Agent Instructions & Stage Directives

### 3.1 Ideation Stage Guidance (`IdeationService.cs`)
Updated task planning guidelines in [`IdeationService`](../../src/NightCodeForge.Agents/Ideation/IdeationService.cs):
```markdown
<dependency_identification_rules>
When planning or proposing tasks:
1. Cross-reference your ideas against the `<existing_workspace_tasks>` list above (which contains top active backlog tasks ordered by priority).
2. DO NOT propose duplicate tasks for capabilities already captured in existing tasks or already merged in the codebase.
3. If your proposed task builds on, calls, or extends interfaces or data models introduced in an existing open/suggested task, you MUST set 'dependsOnTitles' in your 'propose_tasks' call with the title or numeric ID (e.g., '#101' or '101') of the prerequisite task.
4. Explain to the developer why this dependency exists and the recommended order of execution.
5. AVOID CIRCULAR DEPENDENCIES: Ensure your dependencies form a strict acyclic graph. Circular dependencies will be rejected with an error.
</dependency_identification_rules>
```

In addition, [`IdeationProposalViewModel.ResolveDependencies`](../../src/NightCodeForge.Web/Services/IdeationSessionState.cs#L192-L211) is updated to support parsing integer task IDs (e.g. `101`, `#101`) in addition to string title matching.

### 3.2 Stage 1: Task Review Directive (`CrucibleAgentHarness.cs`)
Updated `StandardStageIds.TaskReview` directive in [`CrucibleAgentHarness`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs):
```csharp
StandardStageIds.TaskReview or "stage-1" =>
    "You are a Staff Software Architect & Requirements Investigator. " +
    "1. Thoroughly investigate the task requirements and inspect the codebase using 'read' and 'bash'. " +
    "2. BACKLOG DEPENDENCY AUDIT: Inspect the <existing_workspace_tasks> list (top active tasks by priority). Determine whether this task depends on any unmerged tasks (e.g. shared database migrations, missing models, interface definitions). If a prerequisite is found, record it in the YAML frontmatter under 'depends_on: [ ... ]' (using prerequisite task ID or title) and document the architectural rationale under '## Dependencies' in the markdown body. " +
    "3. CIRCULAR DEPENDENCY PREVENTION: Never introduce a circular dependency (e.g. A depends on B when B already depends on A). Cycles are rejected immediately by the edit tool. If an edit fails with a circular dependency error, re-check your assumptions and resolve the link. " +
    "4. WORKFLOW SUSPENSION AWARENESS: Note that if you declare a dependency on a task that is not yet merged/closed, this task will be suspended and reset to 'Open' until the prerequisite is completed, after which the full workflow will rerun from Stage 1 with the updated code. " +
    "5. MANDATORY FILE EDIT: Edit the task specification document on disk in docs/tasks/ using 'edit' or 'write' with updated frontmatter (including 'depends_on'), approved architecture, affected files, technical plan, dependencies, and acceptance criteria. " +
    "6. Gather all questions and ambiguities into a single 'ask' call using the question list. Do not guess. " +
    "7. Do not write production code.",
```

---

## 4. Specification Markdown Synchronization & In-Tool Cycle Detection

### 4.1 Machine-Readable Frontmatter (`depends_on`) & `## Dependencies` Section
When the Stage 1 review agent updates `docs/tasks/{id}-{slug}.md`, it records dependencies in YAML frontmatter (`depends_on: [ 101 ]`) and documents human-readable rationale under `## Dependencies`.

Upon file save on disk, [`TaskMarkdownSyncService`](../../src/NightCodeForge.Core/Services/TaskMarkdownSyncService.cs) deserializes `depends_on`, resolves titles/slugs to canonical task IDs, validates DAG acyclicity via `IDependencyResolutionEngine.HasCycle`, updates `TaskDependency` records, and normalizes `depends_on` to integer IDs.

### 4.2 In-Tool Cycle Detection & Agent Self-Correction Loopback
To ensure the agent can self-correct within its active turn instead of failing post-stage after the turn has already ended:
1. When [`CanonicalTools.EditAsync`](../../src/NightCodeForge.Agents/Tools/CanonicalTools.cs#L262) or `WriteAsync` targets a task specification file under `docs/tasks/*.md`, it parses the prospective YAML frontmatter `depends_on` field.
2. If new dependencies are declared, it tests the prospective edges against the workspace graph using [`IDependencyResolutionEngine.HasCycle`](../../src/NightCodeForge.Core/Dependencies/IDependencyResolutionEngine.cs#L11).
3. If a cycle is detected:
   - The tool rejects the edit **in-place without writing to disk**.
   - It returns an immediate, model-visible error message:
     ```text
     [ERROR] Circular dependency detected: Task #{id} -> #{prereqId} creates a directed graph cycle.
     The dependency update was rejected and the file was not modified. Re-check your dependency graph and resolve the circular link.
     ```
   - Because the agent remains in its active LLM turn, it reads this tool result and issues a corrected edit to resolve the cycle.

---

## 5. Implementation Checklist & Deliverables

- [x] **Backlog Context Builder:**
  - [x] Implement `IWorkspaceBacklogSummaryService` generating compact `<existing_workspace_tasks>` digests.
  - [x] Filter out archived and terminal tasks (`Closed`, `Rejected`, `Cancelled`).
  - [x] Sort active tasks by `Priority ASC` then `UpdatedAt DESC`, capped to configurable limit (default: 20) with overflow notice.
  - [x] Inject summary into [`IdeationService`](../../src/NightCodeForge.Agents/Ideation/IdeationService.cs) system prompt and Stage 1 Task Review prompt in [`PromptPrefixStabilizer`](../../src/NightCodeForge.Agents/Optimizations/PromptPrefixStabilizer.cs) strictly within **Layer 3 (Task Scope)**.
- [x] **Stage Directives & Ideation Numeric ID Updates:**
  - [x] Update Stage 1 directive in [`CrucibleAgentHarness`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) and Ideation planning guidelines.
  - [x] Update `IdeationProposalViewModel.ResolveDependencies` in [`IdeationSessionState`](../../src/NightCodeForge.Web/Services/IdeationSessionState.cs) to support numeric IDs (`101`, `#101`).
- [x] **In-Tool Cycle Detection & Agent Error Loopback:**
  - [x] Add frontmatter dependency validation in [`CanonicalTools`](../../src/NightCodeForge.Agents/Tools/CanonicalTools.cs) for `edit` and `write` targeting `docs/tasks/*.md` using `IDependencyResolutionEngine.HasCycle`.
- [x] **Tests:**
  - [x] Unit tests verifying backlog summary filtering, priority ordering, task capping, and overflow notice.
  - [x] Unit test verifying `<existing_workspace_tasks>` is stabilized in Layer 3 and does not alter Layer 1/2 hash.
  - [x] Unit test verifying `CanonicalTools` file edit on `docs/tasks/*.md` rejects circular dependencies in-tool with an actionable error.

