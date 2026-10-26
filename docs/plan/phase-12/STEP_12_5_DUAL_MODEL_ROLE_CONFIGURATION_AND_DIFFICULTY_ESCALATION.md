# 🧠 Step 12.5: Dual Model Role Configuration & Difficulty-Aware Escalation

> **Phase 12 Implementation Step (Extend Features - Intelligence & Quality Gates)** *(formerly Step 11.1)*  
> *Parent Plan:* [Phase 12 Master Plan](../12_EXTEND_FEATURES.md)  
> *Dependencies:* [Step 11.33: Unified LLM Provider & Dynamic Fleet Integration](../phase-11/STEP_11_33_UNIFIED_LLM_PROVIDER_DYNAMIC_CONTEXT_AND_FLEET_INTEGRATION.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Agents`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Web.Tests`  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **Role-Specialized Dual Model Architecture:** Introduce first-class configuration supporting heterogeneous LLM models assigned by functional stage role:
   - **Architect / Reviewer Role:** High-reasoning, deep critique models (e.g. Qwen3.8-27B:latest) powering `ideation`, `task-review`, `code-review-and-fix`, and untrusted `triage`.
   - **Coder / Implementation Role:** High-throughput, instruction-tuned coding models (e.g. Qwen3.6-35B-A3B-Code:latest) powering the fast iterative `implementation` stage.
2. **Difficulty-Aware Escalation Policy:** Enable dynamic cognitive scaling. Tasks classified as **Difficult** or **Expert** automatically escalate the implementation stage to the Architect / heavy reasoning model (or a designated high-capacity model) with boosted thinking budgets, ensuring complex algorithmic or architectural changes receive top-tier reasoning while standard tasks execute rapidly.
3. **Hardware Affinity & VRAM Optimization:** Permit independent hardware routing policies ([OllamaRoutingTarget](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/AgentProfile.cs#L13-L26)) per role (e.g. pinning the fast Coder model to developer Host GPU `Local` and the heavy Reviewer model to a dedicated high-VRAM LAN Server `Remote`), maximizing VRAM efficiency and context cache persistence.
4. **Deterministic Stage JIT Warmup (Swap Latency Softening):** Capitalize on discrete deterministic workflow stages (e.g. [DeterministicBuildStage](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicBuildStage.cs), `DeterministicTestStage`, git checkouts) running purely on the host CPU (0 GPU tokens) to asynchronously trigger JIT model preloading ([IOllamaModelPreloadService](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Interfaces/IOllamaModelCatalogService.cs#L22)), softening or completely hiding PCIe model swap latencies between Architect/Reviewer and Coder models.
5. **Per-Model Context Window & Compaction Boundaries:** Introduce explicit `ContextWindowTokens` management per model and role. Heterogeneous models possess divergent context capacities (e.g. 32K coder vs 128K/225K reviewer); per-model context tracking ensures [ContextWindowCompactionStrategy](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Inference/ModelCapabilityResolver.cs#L97-L105) compacts at the accurate boundary without premature eviction or context overflow crashes.
6. **First-Class UI & Presets:** Streamline workspace provisioning and profile management in the Blazor dashboard with pre-configured dual-model presets and task difficulty badges.

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

### 2.3 Model Swapping Overhead Between Heterogeneous Roles
- In single-GPU or VRAM-constrained topologies where both models cannot reside concurrently in GPU memory, swapping between heavy reviewer models (e.g. 27B–35B) and coder models incurs a PCIe loading penalty of **10 to 18 seconds**.
- Running this swap synchronously during agent stage activation causes dead-time stalls.
- However, our pipeline architecture naturally executes CPU-bound deterministic stages between agent turns (e.g. git isolation, build verification, unit tests, coverage gates). Triggering JIT warmup asynchronously during these deterministic windows softens and masks the model swap cost entirely.

### 2.4 Heterogeneous Context Windows & Compaction Risk
- Heterogeneous models possess radically divergent native context window limits and VRAM footprints (e.g. 32,768 tokens for quantized local coding weights vs 131,072 or 225,280 tokens for extended-context reasoning models).
- Enforcing a single global [ContextWindowTokens](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Inference/LlmProviderOptions.cs#L37) creates a failure dichotomy:
  - Setting the global context window to a high value (e.g. 225K) causes smaller coder models to crash with out-of-memory or hard context truncation in Ollama / llama.cpp.
  - Setting the global context window to a conservative low value (e.g. 32K) causes premature compaction on high-context reviewer models, unnecessarily stripping away rich conversation history, diffs, and architectural specifications.
- Managing `ContextWindowTokens` on a per-model and per-profile basis is essential for robust, crash-free execution.

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
   - `ReviewerModelName`: string (e.g. `"Qwen3.8-27B:latest"`)
   - `CoderModelName`: string (e.g. `"Qwen3.6-35B-A3B-Code:latest"`)
   - `EscalatedCoderModelName`: string? (optional override)
   - `ReviewerRoutingTarget`: `OllamaRoutingTarget` (default: `Auto`)
   - `CoderRoutingTarget`: `OllamaRoutingTarget` (default: `Auto`)
   - `ReviewerContextWindowTokens`: int? (default: 225280 or model default)
   - `CoderContextWindowTokens`: int? (default: 32768 or model default)
   - `EscalatedCoderContextWindowTokens`: int? (optional override)
3. **AgentProfile Entity Enhancement:**
   Add `public int? ContextWindowTokens { get; set; }` to [AgentProfile](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/AgentProfile.cs).
4. **Profile Generation:**
   Enhance `AgentProfile.CreateStandardGlobalProfiles` to accept dual model configurations and context windows:
   ```csharp
   public static IReadOnlyList<AgentProfile> CreateDualModelProfiles(
       string reviewerModel,
       string coderModel,
       OllamaRoutingTarget reviewerTarget = OllamaRoutingTarget.Auto,
       OllamaRoutingTarget coderTarget = OllamaRoutingTarget.Auto,
       int? reviewerContextWindowTokens = null,
       int? coderContextWindowTokens = null);
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
     - Determine the escalated context window: workspace `EscalatedCoderContextWindowTokens` ?? workspace `ReviewerContextWindowTokens` ?? profile `ContextWindowTokens`.
     - Determine the escalated thinking effort: elevate `ThinkingEffort` to at least `ThinkingEffort.High`.
     - Clone effective profile with escalated parameters (`ModelName`, `ContextWindowTokens`, `ThinkingEffort`).
     - Log informational audit message:
       `"Task #{TaskId} is marked as {Difficulty}: escalating Implementation stage to reasoning model '{ModelName}' (Context: {ContextWindowTokens} tokens) with thinking effort {ThinkingEffort}."`

### 3.3 Deterministic Stage JIT Warmup & Swap Latency Softening
1. **Overlapping PCIe Model Swap with CPU Execution:**
   - Stage transitions (e.g. Task Review -> Implementation or Implementation -> Code Review) often require loading a new model into VRAM.
   - Deterministic stages (e.g. [DeterministicBuildStage](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicBuildStage.cs), `DeterministicTestStage`, git checkout, linting) execute purely on the host CPU for 10–45 seconds with 0 GPU load.
   - During deterministic stage execution, the orchestrator/stage issues an asynchronous, non-blocking JIT preload request via [IOllamaModelPreloadService](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Interfaces/IOllamaModelCatalogService.cs#L22) (`POST /api/generate` with `keep_alive: "15m"`).
   - **Cancellation & Failure Guard:** The preload task accepts the deterministic stage's `CancellationToken`. If the deterministic stage encounters an immediate failure (e.g. compile error after 1 second) or is cancelled, the preload HTTP request is aborted, preventing Ollama from evicting the previous model on a single GPU.
   - By the time the deterministic CPU step completes, the target model weights are warm and resident in VRAM, eliminating perceived model swap delays.
2. **Difficulty-Aware Preload Lookahead:**
   - When a deterministic stage looks ahead in the pipeline to determine the upcoming `Agentic` stage's model, it must apply the difficulty escalation rules:
     - If the next stage is `StandardStageIds.Implementation` and `task.Difficulty >= TaskDifficulty.Difficult`, warmup targets the **escalated coder/reviewer model** (`EscalatedCoderModelName` ?? `ReviewerModelName`) instead of the standard coder model.
     - Prevents premature loading of the wrong model weights and avoids double swaps.
3. **Dual Routing Target Coexistence:**
   - When `ReviewerRoutingTarget` and `CoderRoutingTarget` map to separate hardware endpoints (e.g. Reviewer on LAN Server `Remote`, Coder on Host GPU `Local`), both models stay warm simultaneously without VRAM contention, reducing swap overhead to zero. JIT warmup serves as the safeguard when models share a physical GPU.

### 3.4 Per-Model Context Window Management & Compaction Isolation
1. **Resolution Priority:**
   When constructing the agent harness and compaction strategy for any active agent turn, [ModelCapabilityResolver](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Inference/ModelCapabilityResolver.cs) resolves the effective `ContextWindowTokens` using the following hierarchy:
   ```csharp
   int contextWindow = activeProfile.ContextWindowTokens
       ?? capabilityProfile?.ContextWindowTokens
       ?? options?.ContextWindowTokens
       ?? LlmProviderOptions.DefaultContextWindowTokens;
   ```
2. **Compaction Strategy Sizing:**
   - In [CrucibleAgentHarness](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs), pass the effective profile or its `ContextWindowTokens` to [ModelCapabilityResolver.BuildCompactionStrategy](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Inference/ModelCapabilityResolver.cs#L97-L105).
   - [ContextWindowCompactionStrategy](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Inference/ModelCapabilityResolver.cs#L97-L105) calibrates its compaction threshold (and max output reservation) against the model's exact context limit:
     - Standard Coder (e.g. 32,768 tokens): Compaction triggers early enough to keep turns within physical VRAM limits.
     - Architect/Reviewer (e.g. 131,072 or 225,280 tokens): Compaction triggers only when necessary, preserving exhaustive specification transcripts and multi-file review diffs.

### 3.5 Web UI Configuration & Observability
1. **Workspace Creation Wizard (Step 3):**
   - Provide a "Model Architecture" toggle: *Single Model* vs *Dual Model (Recommended)*.
   - Dual Model mode displays two distinct model inputs with curated presets:
     - *Preset 1 (Qwen Dual):* Reviewer = `Qwen3.8-27B:latest` (Context: 225,280), Coder = `Qwen3.6-35B-A3B-Code:latest` (Context: 32,768)
     - *Preset 2 (Custom Dual)*
   - Hardware routing targets and context window sizes selectable per role.
2. **Settings -> Agent Profiles Tab (`AgentProfilesTab.razor`):**
   - Group profiles into two clear functional sections:
     - 🏛️ **Architecture & Review Agents** (`triage`, `ideation`, `task-review`, `code-review-and-fix`)
     - ⚡ **Implementation Agents** (`implementation`, with standard and escalated configurations)
   - Allow batch updating the model and context window for all Review profiles or all Coding profiles simultaneously.
3. **Task Detail Drawer & Inspector (`TaskDetailDrawer.razor` / `TaskExecutionInspector.razor`):**
   - Add a Difficulty dropdown badge: `Standard` (gray), `Difficult` (amber), `Expert` (purple).
   - When a task is escalated, show a prominent indicator on the Implementation stage stepper and transcript card: `⚡ Escalated to {EscalatedModelName}`.
   - Display active model name and context window budget in stage telemetry tooltips.

---

## 4. Implementation Checklist & Deliverables

- [ ] **Core Domain & Entities:**
  - [ ] Add `TaskDifficulty` enum to `NightCodeForge.Core.Domain`.
  - [ ] Add `Difficulty` property to `ForgeTask` entity with EF Core database migration.
  - [ ] Add `ContextWindowTokens` nullable int property to `AgentProfile`.
  - [ ] Add dual-model fields (`ReviewerModelName`, `CoderModelName`, `EscalatedCoderModelName`, `ReviewerContextWindowTokens`, `CoderContextWindowTokens`, `EscalatedCoderContextWindowTokens`) to `WorkspaceCreationModel` and `Workspace`.
  - [ ] Implement `AgentProfile.CreateDualModelProfiles(...)` factory method supporting models, targets, and context windows.
- [ ] **TaskOrchestrator Escalation & Pipeline Execution:**
  - [ ] Implement profile resolution logic in `TaskOrchestrator` to escalate `StandardStageIds.Implementation` based on `task.Difficulty`.
  - [ ] Escalate model name, context window tokens, and thinking effort when escalated.
  - [ ] Audit-log escalation decisions in execution transcripts and telemetry.
- [ ] **Deterministic Stage JIT Warmup:**
  - [ ] Update lookahead model resolution in `DeterministicBuildStage` (and deterministic stages) to be difficulty-aware, preloading the escalated model when the upcoming implementation task is `Difficult` or `Expert`.
  - [ ] Ensure `IOllamaModelPreloadService` runs asynchronously without blocking CPU compilation or test execution.
- [ ] **Agents & Compaction Inference:**
  - [ ] Update `ModelCapabilityResolver.BuildCompactionStrategy` and `CrucibleAgentHarness` to consume `AgentProfile.ContextWindowTokens`.
  - [ ] Ensure compaction thresholds and token estimators calibrate against the specific model's context window.
- [ ] **Web UI Enhancements:**
  - [ ] Add dual-model toggle, context window inputs, and preset selector in `WizardStep3Environment.razor`.
  - [ ] Group profiles by role (Architecture & Review vs Implementation) in `AgentProfilesTab.razor`.
  - [ ] Add `Difficulty` selector to `CreateTask.razor` and `TaskDetailDrawer.razor`.
  - [ ] Display escalation badge on active running stages in `TaskExecutionInspector.razor`.
- [ ] **Automated Testing:**
  - [ ] Unit tests for `AgentProfile.CreateDualModelProfiles` verifying model, routing target, and context window assignments.
  - [ ] Unit test in `TaskOrchestratorTests` asserting standard task uses coder model and difficult task uses escalated reviewer model.
  - [ ] Unit test verifying deterministic stage lookahead preloads the escalated model on difficult tasks.
  - [ ] Unit tests verifying compaction strategy sizing respects profile-level `ContextWindowTokens`.
  - [ ] Run test suite per AGENTS.md: `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

