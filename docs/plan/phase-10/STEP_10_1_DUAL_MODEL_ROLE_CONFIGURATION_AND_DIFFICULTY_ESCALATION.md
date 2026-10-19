# 🧠 Step 10.1: Dual Model Role Configuration & Difficulty-Aware Escalation

> **Phase 10 Implementation Step**  
> *Parent Plan:* [Phase 10 Master Plan](../10_EXTEND_FEATURES.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Web.Tests`  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **Role-Specialized Dual Model Architecture:** Introduce first-class configuration supporting heterogeneous LLM models assigned by functional stage role:
   - **Architect / Reviewer Role:** High-reasoning, deep critique models (e.g. Qwen 3.8 27B, Qwen 2.5 32B, Claude 3.7 Sonnet) powering `ideation`, `task-review`, `code-review-and-fix`, and untrusted `triage`.
   - **Coder / Implementation Role:** High-throughput, instruction-tuned coding models (e.g. Qwen 3.6 35B A3B, Qwen 2.5 Coder 32B) powering the fast iterative `implementation` stage.
2. **Difficulty-Aware Escalation Policy:** Enable dynamic cognitive scaling. Tasks classified as **Difficult** or **Expert** automatically escalate the implementation stage to the Architect / heavy reasoning model (or a designated high-capacity model) with boosted thinking budgets, ensuring complex algorithmic or architectural changes receive top-tier reasoning while standard tasks execute rapidly.
3. **Hardware Affinity & VRAM Optimization:** Permit independent hardware routing policies ([OllamaRoutingTarget](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/AgentProfile.cs#L13-L26)) per role (e.g. pinning the fast Coder model to developer Host GPU `Local` and the heavy Reviewer model to a dedicated high-VRAM LAN Server `Remote`), maximizing VRAM efficiency and context cache persistence.
4. **First-Class UI & Presets:** Streamline workspace provisioning and profile management in the Blazor dashboard with pre-configured dual-model presets and task difficulty badges.

---

## 2. Problem Statement & Motivation

### 2.1 The Monolithic Model Compromise
- Currently, workspace creation and global settings default to a single model name ([AgentProfile.CreateStandardGlobalProfiles](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/AgentProfile.cs#L91)) applied uniformly across all 5 standard agent profiles.
- Autonomous software engineering requires fundamentally different cognitive competencies across stages:
  - **Ideation & Task Review:** Requires broad system vision, high-level abstraction, skeptical requirement decomposition, edge-case anticipation, and specification drafting.
  - **Implementation:** Requires rapid file reading, precise patch application, fast compiler test-debug iterations, and syntactic adherence without excessive meta-cognitive commentary.
  - **Code Review:** Requires adversarial bug hunting, security scrutiny, adherence to XML documentation standards, and quality gate verification.
- Selecting a single model forces an undesirable trade-off:
  - A smaller/faster coder model compromises review fidelity and architectural planning.
  - A heavy reasoning model slows down routine code-editing iterations and consumes excessive GPU resources for straightforward tasks.

### 2.2 Dynamic Task Difficulty
- Real-world backlogs consist of mixed complexities: routine tasks (e.g. adding an XML doc, exposing an existing method, simple CRUD endpoints) alongside difficult tasks (e.g. lock hierarchy refactoring, distributed consensus, race condition debugging).
- Hard tasks stall when assigned to pure coder models that lack multi-step deductive reasoning.
- A dynamic escalation policy provides the ideal balance: fast execution by default, automatic escalation when task complexity demands it.

---

## 3. Behavioral Specifications

### 3.1 Role Taxonomy & Workspace Configuration
1. **Model Roles:**
   - `ArchitectReviewer`: Assigned to `StandardStageIds.Triage`, `StandardStageIds.Ideation`, `StandardStageIds.TaskReview`, and `StandardStageIds.CodeReviewAndFix`.
   - `CoderImplementation`: Assigned to `StandardStageIds.Implementation`.
   - `EscalatedCoder`: Model used for implementation when a task is flagged as `Difficult` or `Expert` (defaults to `ArchitectReviewer` model if not explicitly specified).
2. **Workspace Entity & Creation Model:**
   Extend [WorkspaceCreationModel](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Models/WorkspaceCreationModel.cs) and [Workspace](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/Workspace.cs) with:
   - `UseDualModelConfiguration`: bool (default: true)
   - `ReviewerModelName`: string (e.g. `"qwen-3.8-27b"`)
   - `CoderModelName`: string (e.g. `"qwen-3.6-35b-a3b"`)
   - `EscalatedCoderModelName`: string? (optional override)
   - `ReviewerRoutingTarget`: `OllamaRoutingTarget` (default: `Auto`)
   - `CoderRoutingTarget`: `OllamaRoutingTarget` (default: `Auto`)
3. **Profile Generation:**
   Enhance `AgentProfile.CreateStandardGlobalProfiles` to accept dual model configurations:
   ```csharp
   public static IReadOnlyList<AgentProfile> CreateDualModelProfiles(
       string reviewerModel,
       string coderModel,
       OllamaRoutingTarget reviewerTarget = OllamaRoutingTarget.Auto,
       OllamaRoutingTarget coderTarget = OllamaRoutingTarget.Auto);
   ```

### 3.2 Task Difficulty Classification & Dynamic Escalation
1. **TaskDifficulty Enum:**
   Add to `NightCodeForge.Core.Domain`:
   ```csharp
   public enum TaskDifficulty
   {
       Standard = 0,
       Difficult = 1,
       Expert = 2
   }
   ```
2. **Task Entity Integration:**
   Add `public TaskDifficulty Difficulty { get; set; } = TaskDifficulty.Standard;` to [ForgeTask](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/ForgeTask.cs).
3. **Setting Difficulty:**
   - **Ideation Agent:** May set `difficulty` parameter during `propose_tasks` (e.g. `difficulty: "difficult"`).
   - **Task Review Agent:** Can update difficulty during task specification enrichment in `docs/tasks/*.md` (parsed and synchronized via Step 9.16).
   - **Maintainer:** Can set or toggle difficulty in `CreateTask.razor` (/tasks/new) and `TaskDetailDrawer.razor`.
4. **Orchestrator Escalation Resolution:**
   In [TaskOrchestrator](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs), when resolving the effective [AgentProfile](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/AgentProfile.cs) for `StandardStageIds.Implementation`:
   - If `task.Difficulty >= TaskDifficulty.Difficult`:
     - Determine the escalated model: workspace `EscalatedCoderModelName` ?? workspace `ReviewerModelName` ?? profile `ModelName`.
     - Determine the escalated thinking effort: elevate `ThinkingEffort` to at least `ThinkingEffort.High`.
     - Clone effective profile with escalated parameters.
     - Log informational audit message:
       `"Task #{TaskId} is marked as {Difficulty}: escalating Implementation stage to reasoning model '{ModelName}' with thinking effort {ThinkingEffort}."`

### 3.3 Web UI Configuration & Observability
1. **Workspace Creation Wizard (Step 3):**
   - Provide a "Model Architecture" toggle: *Single Model* vs *Dual Model (Recommended)*.
   - Dual Model mode displays two distinct model inputs with curated presets:
     - *Preset 1 (Qwen Heterogeneous):* Reviewer = `qwen2.5:32b`, Coder = `qwen2.5-coder:32b`
     - *Preset 2 (Next-Gen Qwen Dual):* Reviewer = `qwen-3.8-27b`, Coder = `qwen-3.6-35b-a3b`
     - *Preset 3 (Custom Dual)*
   - Hardware routing targets selectable per role.
2. **Settings -> Agent Profiles Tab (`AgentProfilesTab.razor`):**
   - Group profiles into two clear functional sections:
     - 🏛️ **Architecture & Review Agents** (`triage`, `ideation`, `task-review`, `code-review-and-fix`)
     - ⚡ **Implementation Agents** (`implementation`, with standard and escalated configurations)
   - Allow batch updating the model for all Review profiles or all Coding profiles simultaneously.
3. **Task Detail Drawer & Inspector (`TaskDetailDrawer.razor` / `TaskExecutionInspector.razor`):**
   - Add a Difficulty dropdown badge: `Standard` (gray), `Difficult` (amber), `Expert` (purple).
   - When a task is escalated, show a prominent indicator on the Implementation stage stepper and transcript card: `⚡ Escalated to {EscalatedModelName}`.

---

## 4. Implementation Checklist & Deliverables

- [ ] **Core Domain:**
  - [ ] Add `TaskDifficulty` enum to `NightCodeForge.Core.Domain`.
  - [ ] Add `Difficulty` property to `ForgeTask` entity with EF Core database migration.
  - [ ] Add dual-model fields (`ReviewerModelName`, `CoderModelName`, `EscalatedCoderModelName`) to `WorkspaceCreationModel` and `Workspace`.
  - [ ] Implement `AgentProfile.CreateDualModelProfiles(...)` factory method.
- [ ] **TaskOrchestrator Escalation:**
  - [ ] Implement profile resolution logic in `TaskOrchestrator` to escalate `StandardStageIds.Implementation` based on `task.Difficulty`.
  - [ ] Elevate thinking effort and token budget when escalated.
  - [ ] Audit-log escalation decisions in execution transcripts and telemetry.
- [ ] **Web UI Enhancements:**
  - [ ] Add dual-model toggle and preset selector in `WizardStep3Environment.razor`.
  - [ ] Group profiles by role (Architecture & Review vs Implementation) in `AgentProfilesTab.razor`.
  - [ ] Add `Difficulty` selector to `CreateTask.razor` and `TaskDetailDrawer.razor`.
  - [ ] Display escalation badge on active running stages in `TaskExecutionInspector.razor`.
- [ ] **Automated Testing:**
  - [ ] Unit tests for `AgentProfile.CreateDualModelProfiles` verifying model and routing target assignments.
  - [ ] Unit test in `TaskOrchestratorTests` asserting standard task uses coder model and difficult task uses escalated reviewer model.
  - [ ] Scenario runner test validating heterogeneous stage execution across a full simulated pipeline.
  - [ ] Run test suite per AGENTS.md: `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
