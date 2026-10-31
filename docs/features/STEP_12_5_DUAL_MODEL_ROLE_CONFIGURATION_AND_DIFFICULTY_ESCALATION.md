# 🧠 Step 12.5: Task Difficulty Classification & Difficulty-Aware Model Escalation

> **Phase 12 Implementation Step (Extend Features - Intelligence & Quality Gates)** *(formerly Step 11.1)*  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Dependencies:* [Step 11.33: Unified LLM Provider & Dynamic Fleet Integration](../archive/phase-11/STEP_11_33_UNIFIED_LLM_PROVIDER_DYNAMIC_CONTEXT_AND_FLEET_INTEGRATION.md), [Step 11.35: OpenAI-Compatible LLM Provider](../archive/phase-11/STEP_11_35_OPENAI_COMPATIBLE_LLM_PROVIDER.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Agents`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Web.Tests`  
> *Status:* Partially Implemented (Foundations completed in 11.33 & 11.35; Difficulty Escalation Planned) | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Summary & Architectural Alignment with Steps 11.33 & 11.35

### 1.1 Foundations Already Delivered in Steps 11.33 & 11.35
As a result of the unified provider and dynamic fleet architecture implemented in [Step 11.33](../archive/phase-11/STEP_11_33_UNIFIED_LLM_PROVIDER_DYNAMIC_CONTEXT_AND_FLEET_INTEGRATION.md) and [Step 11.35](../archive/phase-11/STEP_11_35_OPENAI_COMPATIBLE_LLM_PROVIDER.md), the foundational dual-model infrastructure is already in place:

1. **Role-Specialized Baseline Profile Generation (`Completed`):**
   - [`LlmProviderOptions`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Inference/LlmProviderOptions.cs) and [`ILlmProviderOptions`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Interfaces/ILlmProviderOptions.cs) support differentiated `ArchitectModelId` / `ArchitectThinkingEffort` and `CoderModelId` / `CoderThinkingEffort`.
   - [`AgentProfile.CreateStandardGlobalProfiles(ILlmProviderOptions)`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/AgentProfile.cs#L99-L182) configures `triage`, `ideation`, `task-review`, and `code-review-and-fix` with the Architect/Reviewer model, and `implementation` with the Coder model.
2. **Direct Per-Stage Profile Configuration (`Completed`):**
   - Per Step 11.33 (§1.1 #2), NightCodeForge avoids error-prone top-level role override fields on [`Workspace`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/Workspace.cs). Instead, each stage profile card in [`AgentProfilesTab.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Settings/AgentProfilesTab.razor) directly configures its model, thinking effort, routing target (`OllamaRoutingTarget`), and limits (either globally or via dedicated workspace profiles).
3. **Automatic Per-Model Context Window Discovery & Compaction Sizing (`Completed`):**
   - [`AgentProfile.ContextWindowTokens`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/AgentProfile.cs#L62) (`int?`) is persisted per profile and automatically discovered via [`ILlmGatewayService.ResolveContextWindowAsync`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Interfaces/ILlmGatewayService.cs).
   - [`CrucibleAgentHarness`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs#L772-L800) and [`ModelCapabilityResolver.BuildCompactionStrategy`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Inference/ModelCapabilityResolver.cs#L21-L39) dynamically size Pi-style context compaction per active profile and model.
4. **Deterministic Stage Asynchronous JIT Warmup Infrastructure (`Completed`):**
   - [`DeterministicBuildStage`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicBuildStage.cs#L88-L139) looks ahead in `context.Pipeline` for the next `Agentic` stage and triggers non-blocking model preloading via [`ILlmModelPreloadService`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Interfaces/ILlmModelPreloadService.cs) and [`IBackgroundTaskRunner`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Interfaces/IBackgroundTaskRunner.cs).

### 1.2 Remaining Scope of Step 12.5
With those foundations in place, Step 12.5 focuses cleanly on **Task Difficulty Classification & Dynamic Cognitive Escalation**:

1. **`TaskDifficulty` Classification:** Introduce first-class `TaskDifficulty` (`Standard`, `Difficult`, `Expert`) across [`ForgeTask`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/ForgeTask.cs), [`TaskProposal`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/ForgeTask.cs#L48), markdown frontmatter synchronization (`docs/tasks/*.md`), and agent tools (`propose_tasks`).
2. **Difficulty-Aware Implementation Escalation:** When executing `StandardStageIds.Implementation` for a task classified as `Difficult` or `Expert`, [`TaskOrchestrator`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) automatically escalates the implementation stage's model, context window, routing target, and thinking effort (`>= High`) to the workspace's effective **Architect / Reviewer** profile (or an explicit `EscalatedModelName` configured on the implementation profile), without requiring duplicate model fields on `Workspace`.
3. **Effective Profile & Difficulty-Aware JIT Preload Lookahead:** Upgrade [`DeterministicBuildStage`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicBuildStage.cs) lookahead to resolve the effective profile (including global fallback and difficulty escalation) so single-GPU rigs warm the exact model that the upcoming `Implementation` stage will run.
4. **First-Class UI & Role Grouping:** Add difficulty badges/selectors in `CreateTask.razor`, `TaskDetailDrawer.razor`, and `TaskExecutionInspector.razor` (with an `⚡ Escalated` indicator), and group profile cards by role (`Architecture & Review` vs `Implementation`) in [`AgentProfilesTab.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Settings/AgentProfilesTab.razor).

---

## 2. Problem Statement & Motivation

### 2.1 Dynamic Task Difficulty in Real-World Backlogs
- Real-world backlogs consist of mixed complexities:
  - **Standard tasks:** Routine feature work, CRUD endpoints, UI components, unit test additions, or localized bug fixes where a fast, high-throughput Coder model (e.g. `Qwen3.6-35B-A3B-Code:latest`) excels.
  - **Difficult / Expert tasks:** Lock hierarchy refactoring, multi-stage concurrency state machines, cross-layer architectural migrations, or subtle race-condition debugging.
- Pure coder models can stall or loop on `Difficult` / `Expert` tasks that demand deep multi-step deductive reasoning.
- Conversely, running a heavy Architect/Reasoning model with `High` thinking effort on every `Standard` implementation task wastes time and GPU tokens.
- A **Difficulty-Aware Escalation Policy** provides the optimal balance: fast coder execution by default for `Standard` tasks, and automatic escalation to the Architect/Reviewer reasoning model and high thinking effort when a task is marked `Difficult` or `Expert`.

### 2.2 Accurate JIT Warmup Lookahead on Escalated Tasks
- [`DeterministicBuildStage`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicBuildStage.cs#L88-L139) overlaps CPU-bound build verification (`PreImplementationBaseline`) with asynchronous GPU model preloading (`ILlmModelPreloadService`).
- Currently, `DeterministicBuildStage` only checks `context.Workspace?.AgentProfiles` directly (missing inherited global profiles when a workspace has no dedicated overrides) and does not account for task difficulty escalation.
- If a task is `Difficult` or `Expert`, preloading the standard `implementation` coder model during `PreImplementationBaseline` would load the wrong weights into VRAM, causing a double PCIe swap penalty when `TaskOrchestrator` immediately escalates `Implementation` to the Architect model.

---

## 3. Behavioral Specifications

### 3.1 Task Difficulty Classification & Persistence
1. **`TaskDifficulty` Enum:**
   Add to `NightCodeForge.Core.Domain`:
   ```csharp
   namespace NightCodeForge.Core.Domain;

   public enum TaskDifficulty
   {
       Standard = 0,
       Difficult = 1,
       Expert = 2
   }
   ```
2. **Domain & Markdown Integration:**
   - Add `public TaskDifficulty Difficulty { get; set; } = TaskDifficulty.Standard;` to [`ForgeTask`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/ForgeTask.cs) with an EF Core migration.
   - Include `Difficulty` in [`ForgeTaskHistory`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/ForgeTaskHistory.cs) snapshots if task metadata is snapshotted.
   - Extend [`TaskProposal`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/ForgeTask.cs#L48) and `CanonicalTools.propose_tasks` with an optional `difficulty` parameter (`"standard"`, `"difficult"`, `"expert"`, defaulting to `TaskDifficulty.Standard`).
   - Extend `TaskFrontmatter` and `TaskMarkdownSyncService` to serialize and parse `difficulty` in `docs/tasks/*.md` YAML frontmatter so the **Task Review Agent** (Stage 1) can upgrade a task's difficulty after inspecting the codebase and requirements.
   - Allow maintainers to set or change `Difficulty` in `CreateTask.razor` (`/tasks/new`), `TaskDetail.razor`, and `TaskDetailDrawer.razor`.

### 3.2 Profile-Based Escalation Resolution (No Duplicate Workspace Fields)
Following the direct profile architecture from Step 11.33, escalation derives directly from the agent profiles rather than duplicating model/context fields onto `Workspace`:

1. **Optional Override on `AgentProfile`:**
   - Add `public string? EscalatedModelName { get; set; }` to [`AgentProfile`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/AgentProfile.cs) (optional explicit override for the `implementation` profile when escalated; if null/empty, escalation uses the workspace's effective Architect/Reviewer profile).
2. **Orchestrator Escalation Logic (`TaskOrchestrator`):**
   - Extract a shared helper or service method (e.g. on `IWorkspaceRepository` or a dedicated resolver) to resolve the effective [`AgentProfile`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/AgentProfile.cs) for `(workspaceId, stageId, taskDifficulty)`:
   - When `stageId == StandardStageIds.Implementation` and `task.Difficulty >= TaskDifficulty.Difficult`:
     1. Resolve the base `implementation` profile via `GetEffectiveAgentProfileAsync(workspace.Id, StandardStageIds.Implementation, ct)`.
     2. Resolve the reference Architect/Reviewer profile via `GetEffectiveAgentProfileAsync(workspace.Id, StandardStageIds.TaskReview, ct)` (or `CodeReviewAndFix`).
     3. Determine escalated parameters:
        - `ModelName`: ` !string.IsNullOrWhiteSpace(implProfile.EscalatedModelName) ? implProfile.EscalatedModelName : reviewerProfile?.ModelName ?? implProfile.ModelName`
        - `ContextWindowTokens`: If `ModelName` changed to `reviewerProfile?.ModelName`, use `reviewerProfile.ContextWindowTokens` (or `null` so `ILlmGatewayService.ResolveContextWindowAsync` resolves the escalated model's true window dynamically).
        - `RoutingTarget`: If `ModelName` changed to `reviewerProfile?.ModelName`, use `reviewerProfile.RoutingTarget`.
        - `ThinkingEffort`: Elevate to at least `ThinkingEffort.High` (unless `ThinkingEffort.CustomBudget` is configured).
     4. Return a cloned `AgentProfile` instance for the stage execution with the escalated `ModelName`, `ContextWindowTokens`, `RoutingTarget`, and `ThinkingEffort` (preserving `implementation` tools, mode, security tier, and system prompt).
     5. Record an informational execution log and broadcast telemetry:
        `"Task #{TaskId} is marked as {Difficulty}: escalating Implementation stage to model '{ModelName}' with thinking effort {ThinkingEffort}."`

### 3.3 Effective & Difficulty-Aware Preload Lookahead
In [`DeterministicBuildStage`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicBuildStage.cs#L88-L139):
1. When looking ahead in `context.Pipeline` for the next `WorkflowStageKind.Agentic` stage:
   - Resolve the upcoming stage's effective profile using the same difficulty-aware resolution as `TaskOrchestrator` (including fallback to global profiles when `workspace.AgentProfiles` has no dedicated overrides).
   - If the upcoming stage is `StandardStageIds.Implementation` and `context.ForgeTask?.Difficulty >= TaskDifficulty.Difficult`, the preloaded model is the **escalated model** rather than the standard coder model.
2. Preloading continues to run asynchronously via `_backgroundTaskRunner.RunTracked` and `ILlmModelPreloadService.PreloadModelAsync` bound to the deterministic stage's `CancellationToken`.

### 3.4 Web UI Enhancements & Observability
1. **Settings -> Agent Profiles Tab ([`AgentProfilesTab.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Settings/AgentProfilesTab.razor)):**
   - Group the 5 standard profile cards into two visual sections:
     - 🏛️ **Architecture & Review Agents** (`triage`, `ideation`, `task-review`, `code-review-and-fix`)
     - ⚡ **Implementation Agent** (`implementation`)
   - Provide a quick action on the **Architecture & Review Agents** section header to apply a selected model and thinking effort across all 4 review/architecture profiles at once.
   - On the `implementation` profile card, allow optional configuration of `EscalatedModelName` (defaulting to *"Inherit from Task Review (Architect) Profile"*).
2. **Task Authoring & Backlog UI (`CreateTask.razor`, `TaskDetailDrawer.razor`, `TaskDetail.razor`, `KanbanCard.razor`):**
   - Add a `Difficulty` selector / badge:
     - `Standard` (subtle/neutral badge)
     - `Difficult` (amber badge)
     - `Expert` (purple/indigo badge)
3. **Task Execution Inspector (`TaskExecutionInspector.razor`):**
   - Display the task's `Difficulty` badge in the inspector header.
   - When an `Implementation` conversation used an escalated model, display a badge on the stage/conversation card: `⚡ Escalated ({ModelName})`.

---

## 4. Implementation Checklist & Deliverables

### Already Completed (Steps 11.33 & 11.35)
- [x] Role-specialized model and thinking effort options (`CoderModelId`, `ArchitectModelId`) in `LlmProviderOptions` and `AgentProfile.CreateStandardGlobalProfiles`.
- [x] `ContextWindowTokens` property on `AgentProfile` with automatic discovery via `ILlmGatewayService.ResolveContextWindowAsync`.
- [x] Per-profile context compaction sizing in `CrucibleAgentHarness` and `ModelCapabilityResolver.BuildCompactionStrategy`.
- [x] Asynchronous JIT warmup infrastructure (`ILlmModelPreloadService`, `DeterministicBuildStage` background lookahead).

### Remaining Deliverables for Step 12.5
- [ ] **Core Domain & Persistence:**
  - [ ] Add `TaskDifficulty` enum (`Standard`, `Difficult`, `Expert`) to `NightCodeForge.Core.Domain`.
  - [ ] Add `Difficulty` property to `ForgeTask` and `EscalatedModelName` (`string?`) to `AgentProfile` with EF Core migration.
  - [ ] Add optional `Difficulty` parameter to `TaskProposal` and `CanonicalTools.propose_tasks`.
  - [ ] Synchronize `difficulty` in `TaskFrontmatter` and `TaskMarkdownSyncService` (`docs/tasks/*.md`).
- [ ] **Orchestrator Escalation & Preload Lookahead:**
  - [ ] Implement difficulty-aware effective `AgentProfile` resolution for `StandardStageIds.Implementation` when `task.Difficulty >= TaskDifficulty.Difficult` (escalating model, context window, routing target, and thinking effort `>= High`, and logging the escalation).
  - [ ] Update `DeterministicBuildStage` JIT preload lookahead to resolve the effective profile (including global profile fallback and difficulty escalation).
- [ ] **Web UI Enhancements:**
  - [ ] Group profiles into **Architecture & Review Agents** vs **Implementation Agent** in `AgentProfilesTab.razor`, with a batch model apply helper for Architecture & Review profiles and an optional `EscalatedModelName` selector on the Implementation profile.
  - [ ] Add `Difficulty` selector and badges to `CreateTask.razor`, `TaskDetailDrawer.razor`, `TaskDetail.razor`, and `KanbanCard.razor`.
  - [ ] Display escalation indicator (`⚡ Escalated`) in `TaskExecutionInspector.razor`.
- [ ] **Automated Testing:**
  - [ ] Unit tests in `TaskOrchestratorTests` verifying `Standard` tasks run with the Coder profile while `Difficult` and `Expert` tasks escalate `Implementation` to the Architect/Reviewer model (or `EscalatedModelName`) with `ThinkingEffort.High`.
  - [ ] Unit tests in `DeterministicBuildStageTests` verifying lookahead preloads the escalated model when the task is `Difficult` or `Expert` and falls back to global profiles when workspace-scoped profiles are absent.
  - [ ] Unit tests for `TaskMarkdownSyncService` round-tripping `difficulty` frontmatter.
  - [ ] Run test suite per AGENTS.md: `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
