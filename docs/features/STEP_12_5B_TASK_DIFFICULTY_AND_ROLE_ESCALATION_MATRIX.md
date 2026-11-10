# 🧠 Step 12.5B: Task Difficulty Classification & Role-Based Harness/Model Escalation Matrix

> **Phase 12 Implementation Step (Extend Features - Intelligence & Multi-Harness Escalation)** *(formerly Step 11.1 / Step 12.5)*  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Dependencies:* [Step 12.5A: ACP Harness Catalog, Antigravity (`agy acp`) Authentication & Container-Ready Rework](STEP_12_5A_ACP_HARNESS_CATALOG_ANTIGRAVITY_AND_CONTAINER_REWORK.md), [Step 11.33: Unified LLM Provider & Dynamic Fleet Integration](../archive/phase-11/STEP_11_33_UNIFIED_LLM_PROVIDER_DYNAMIC_CONTEXT_AND_FLEET_INTEGRATION.md), [Step 12.27: Dynamic Ollama & OllamaRouter Thinking Effort Discovery & Mapping](STEP_12_27_DYNAMIC_OLLAMA_THINKING_EFFORT_MAPPING.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Agents`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Web.Tests`  
> *Status:* Partially Implemented (Foundations completed in 11.33, 11.35 & 12.27; Difficulty Matrix Planned) | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Summary & Architectural Alignment

### 1.1 Foundations in Place (Steps 11.33, 11.35, 12.27 & 12.5A)
1. **Role-Specialized Baseline Profile Generation (`Completed`):**
   - [`LlmProviderOptions`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Inference/LlmProviderOptions.cs) and [`AgentProfile.CreateStandardGlobalProfiles(ILlmProviderOptions)`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/AgentProfile.cs#L112-L195) configure `triage`, `ideation`, `task-review`, and `code-review-and-fix` with the Architect/Reviewer model, and `implementation` with the Coder model.
2. **Dynamic Thinking Effort Mapping (`Completed in Step 12.27`):**
   - [`OllamaThinkingEffortMapper`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Inference/OllamaThinkingEffortMapper.cs) maps `ThinkingEffort.High` to `"xhigh"` and `ThinkingEffort.Medium` to `"medium"` on Qwen 3.8 via `/api/show` capability discovery.
3. **Antigravity ACP Catalog & Container-Ready ACP Harness (`Step 12.5A`):**
   - [`Step 12.5A`](STEP_12_5A_ACP_HARNESS_CATALOG_ANTIGRAVITY_AND_CONTAINER_REWORK.md) drops `PiRpc`, introduces `IAcpHarnessCatalogService` (auto-discovering `agy acp` in WSL2/Host, authentication status, and available models like `gemini-4-argon`), and reworks `AcpAgentHarness` to execute seamlessly inside `ContainerSandbox` and `WslDirect`.

### 1.2 Scope of Step 12.5B: Role-Based Difficulty Escalation Across Harnesses & Models
Real-world development backlogs span a wide cognitive spectrum where both the **agent execution harness** (`Crucible` vs `Antigravity ACP`) and the **model / thinking effort** should adapt to a task's difficulty tier:

| Difficulty Tier | 🏛️ Architecture & Review Role (`task-review`, `code-review-and-fix`) | ⚡ Coder / Implementation Role (`implementation`) | Typical Workload |
| :--- | :--- | :--- | :--- |
| **`Standard`** | **`Crucible`** with `Qwen 3.8 (Dense)` at `ThinkingEffort.High` (`xhigh`) | **`Crucible`** with `Qwen 3.8 (Dense)` at `ThinkingEffort.Medium` (`medium`) **or** `Flint (MoE)` | Routine feature work, CRUD endpoints, UI components, unit test additions, localized bug fixes. |
| **`Difficult`** | **`Crucible`** with `Qwen 3.8 (Dense)` at `ThinkingEffort.High` (`xhigh`) | **`Crucible`** with `Qwen 3.8 (Dense)` at `ThinkingEffort.High` (`xhigh`) | Multi-file refactoring, state machine updates, non-trivial algorithmic or concurrency changes on local GPU. |
| **`Expert`** | **`ACP` (`antigravity`)** with `Gemini 4 Argon` (`high` / `medium`) | **`ACP` (`antigravity`)** with `Gemini 4 Argon` (`high` / `medium`) | Cross-cutting architectural migrations, deep race-condition debugging, complex multi-project redesigns escalated to a frontier cloud agent. |

*(Pre-task / workspace-level profiles `triage` and `ideation` operate before a task enters the execution pipeline and remain configured independently of task difficulty.)*

---

## 2. Detailed Behavioral & Architectural Specifications

### 2.1 Task Difficulty Classification & Mid-Pipeline Dynamic Upgrade

1. **`TaskDifficulty` Enum (`NightCodeForge.Core.Domain`):**
   ```csharp
   namespace NightCodeForge.Core.Domain;

   public enum TaskDifficulty
   {
       Standard = 0,
       Difficult = 1,
       Expert = 2
   }
   ```
2. **Domain, Persistence & History Integration:**
   - Add `public TaskDifficulty Difficulty { get; set; } = TaskDifficulty.Standard;` to [`ForgeTask`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/ForgeTask.cs) and [`ForgeTaskHistory`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/ForgeTaskHistory.cs) with an EF Core migration (and register the migration ID constant and `pragma_table_info` baseline check in [`AppDbContext.BaselineExistingPreMigrationDatabaseAsync`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Data/AppDbContext.cs#L470-L580)).
   - Include `Difficulty` in [`TaskWorkspacePreparer.CopyTaskMetadata`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskWorkspacePreparer.cs#L530-L542) and `TaskBaselineEntry` / `TaskMarkdownSyncService` change detection so edits to `difficulty` in `docs/tasks/*.md` are detected and persisted.
3. **Agent Tools & Markdown Frontmatter 3-Way Synchronization:**
   - Extend [`TaskProposal`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/ForgeTask.cs) and `CanonicalTools.propose_tasks` / [`BacklogMutationToolService`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/BacklogMutationToolService.cs) (`GetTaskAsync`, `EditTaskAsync` / `TaskEditRequest`, `ProposeTasksAsync`) with an optional `difficulty` parameter (`"standard"`, `"difficult"`, `"expert"`, defaulting to `TaskDifficulty.Standard`).
   - Extend [`TaskFrontmatter`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Services/TaskFrontmatter.cs) (`[YamlMember(Alias = "difficulty")] public string? Difficulty { get; set; }`) and [`TaskMarkdownSyncService`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Services/TaskMarkdownSyncService.cs):
     - Serialize and parse `difficulty: standard | difficult | expert` in `docs/tasks/*.md` YAML frontmatter.
     - **Content Hash & 3-Way Merge:** Include `difficulty:{(frontmatter.Difficulty ?? string.Empty).Trim()}` in `TaskMarkdownSyncService.ComputeTaskContentHash(...)`. In `SyncAllTasksFromDiskAsync` and `ProcessFileContentAsync`, compute `difficultyChanged` via 3-way merge against `baseline.Frontmatter.Difficulty` (if disk altered `difficulty` relative to baseline, take disk's `difficulty`; otherwise if DB altered `difficulty`, preserve DB's `difficulty`) and include `difficultyChanged` in `anyFieldChanged` and `maintainerFieldsSynced` so `task.Version` increments and `UpdateWithHistoryAsync` persists the updated `Difficulty`.
   - **Stage 1 (`task-review`) Dynamic Upgrade:**
     - Update the default `task-review` stage directive / system prompt instructions to explicitly instruct the Task Review Agent to evaluate the task's true technical complexity (`standard`, `difficult`, or `expert`) and update `difficulty:` in the task's markdown frontmatter when escalation is warranted.
     - When `task-review` finishes and `TaskOrchestrator` runs post-stage markdown synchronization (`SyncAllTasksFromDiskAsync`), `task.Difficulty` (and `task.Version`) are updated in memory and in SQLite.
     - Consequently, all subsequent stages (`PreImplementationBaseline` JIT warmup lookahead, `Implementation`, and `CodeReviewAndFix`) automatically resolve their effective `AgentProfile` for the newly upgraded `task.Difficulty`.

---

### 2.2 Role-Based Difficulty Matrix on `AgentProfile`

To preserve NightCodeForge's profile inheritance model (Global profiles vs. Workspace-dedicated profiles) without adding duplicate fields to `Workspace`:

1. **`DifficultyStageOverride` (`NightCodeForge.Core.Domain`):**
   ```csharp
   namespace NightCodeForge.Core.Domain;

   /// <summary>
   /// Defines harness, model, and reasoning overrides applied to a pipeline stage profile
   /// when executing a task of a specific <see cref="TaskDifficulty"/> (Difficult or Expert).
   /// </summary>
   public class DifficultyStageOverride
   {
       public string HarnessId { get; set; } = StandardHarnessIds.Crucible;
       public string ModelName { get; set; } = string.Empty;
       public ThinkingEffort ThinkingEffort { get; set; } = ThinkingEffort.High;
       public int? ThinkingTokenBudget { get; set; }
       public OllamaRoutingTarget RoutingTarget { get; set; } = OllamaRoutingTarget.Auto;
       public int? ContextWindowTokens { get; set; }
       public int? MaxOutputTokens { get; set; }
       public string? HarnessConfigurationJson { get; set; }

       public DifficultyStageOverride Clone() => new()
       {
           HarnessId = HarnessId,
           ModelName = ModelName,
           ThinkingEffort = ThinkingEffort,
           ThinkingTokenBudget = ThinkingTokenBudget,
           RoutingTarget = RoutingTarget,
           ContextWindowTokens = ContextWindowTokens,
           MaxOutputTokens = MaxOutputTokens,
           HarnessConfigurationJson = HarnessConfigurationJson
       };
   }
   ```
2. **`AgentProfile.DifficultyOverrides` Property:**
   - Add `public Dictionary<TaskDifficulty, DifficultyStageOverride>? DifficultyOverrides { get; set; }` to [`AgentProfile`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/AgentProfile.cs), persisted via a JSON `ValueConverter` and `ValueComparer` in [`AppDbContext`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Data/AppDbContext.cs).
   - The base properties on `AgentProfile` (`HarnessId`, `ModelName`, `ThinkingEffort`, `ThinkingTokenBudget`, `RoutingTarget`, `ContextWindowTokens`, `HarnessConfigurationJson`) define the **`TaskDifficulty.Standard`** configuration.
   - `DifficultyOverrides[TaskDifficulty.Difficult]` and `DifficultyOverrides[TaskDifficulty.Expert]` define the stage's configuration for `Difficult` and `Expert` tasks respectively.
3. **Default Baseline Initialization in `AgentProfile.CreateStandardGlobalProfiles(ILlmProviderOptions)`:**
   - **Standard Tier (base properties):**
     - **Architecture & Review (`task-review`, `code-review-and-fix`):** `HarnessId = Crucible`, `ModelName = archModel`, `ThinkingEffort = archEffort` (`High` by default, which maps to `"xhigh"` on Qwen 3.8).
     - **Coder (`implementation`):** `HarnessId = Crucible`, `ModelName = coderModel`, `ThinkingEffort = coderEffort` (`Medium` by default).
   - **Difficult Tier (`DifficultyOverrides[TaskDifficulty.Difficult]`):**
     - **Architecture & Review (`task-review`, `code-review-and-fix`):** `HarnessId = Crucible`, `ModelName = archModel`, `ThinkingEffort = High`.
     - **Coder (`implementation`):** Escalates to the Architect/Review configuration (`HarnessId = Crucible`, `ModelName = archModel`, `ThinkingEffort = High`), so `Difficult` tasks run `Crucible` + `archModel` (`xhigh`) across both review and implementation.
   - **Expert Tier (`DifficultyOverrides[TaskDifficulty.Expert]`):**
     - Configurable in `LlmProviderOptions` (optional `ExpertHarnessId`, `ExpertModelId`, `ExpertThinkingEffort`) or in the Settings UI (e.g., `HarnessId = StandardHarnessIds.Acp`, `ModelName = "gemini-4-argon"`, `ThinkingEffort = ThinkingEffort.High`). When no separate Expert option is set in `appsettings.json`, defaults to `HarnessId = StandardHarnessIds.Acp`, `ModelName = "gemini-4-argon"` if Antigravity ACP is configured, or falls back to the `Difficult` tier configuration.

---

### 2.3 Effective Profile Resolution & Harness-Aware Preload Lookahead

1. **Difficulty-Aware Profile Resolution (`IWorkspaceRepository` / `TaskWorkspacePreparer`):**
   - Add an overload or helper:
     `Task<AgentProfile?> GetEffectiveAgentProfileAsync(int? workspaceId, string stageId, TaskDifficulty difficulty, CancellationToken cancellationToken = default)` (or resolve via `AgentProfile.ResolveForDifficulty(TaskDifficulty difficulty, AgentProfile? fallbackArchitectProfile)`).
   - **Resolution Rules for Task Pipeline Stages (`task-review`, `implementation`, `code-review-and-fix`):**
     1. Resolve the base `AgentProfile` for `(workspaceId, stageId)` (workspace-dedicated override first, then global profile fallback).
     2. If `difficulty == TaskDifficulty.Standard`, return the base profile as-is.
     3. If `difficulty` is `TaskDifficulty.Difficult` or `TaskDifficulty.Expert`:
        - Check `baseProfile.DifficultyOverrides` for `difficulty` (and if `difficulty == TaskDifficulty.Expert` has no entry, check `TaskDifficulty.Difficult`).
        - If a `DifficultyStageOverride` entry exists with a non-empty `ModelName`, return a cloned `AgentProfile` applying the override's `HarnessId`, `ModelName`, `ThinkingEffort`, `ThinkingTokenBudget`, `RoutingTarget`, `ContextWindowTokens`, and `HarnessConfigurationJson` while preserving the stage's `Id`, `Name`, `Mode`, `SecurityTier`, `PermittedTools`, `StageDirectives`, `SystemPrompt`, `MaxTurns`, `Temperature`, and `OptimizationFlags`.
        - **Fallback when `DifficultyOverrides` is null/empty on a legacy DB row:** If `stageId == StandardStageIds.Implementation`, escalate to the workspace's effective `task-review` (Architect) profile's `HarnessId`, `ModelName`, `ContextWindowTokens`, `RoutingTarget`, `HarnessConfigurationJson`, and `ThinkingEffort >= High`.
   - **Execution Logging & Telemetry (`TaskOrchestrator`):**
     - Resolve the effective `AgentProfile` (with `task.Difficulty`) **before** calling `_telemetryService.RecordActiveTask(...)` so `hardwareTarget` reflects the actual execution backend (`"Antigravity ACP"` when `profile?.HarnessId == StandardHarnessIds.Acp`, `"Local GPU"` for `Crucible`, and `"Deterministic CPU"` for deterministic stages).
     - Whenever a task executes a stage with `task.Difficulty > TaskDifficulty.Standard`, log an informational `ExecutionLog` and broadcast stage metadata:
       `"Task #{TaskId} [{Difficulty}]: running stage '{StageId}' with harness '{HarnessId}', model '{ModelName}' (thinking: {ThinkingEffort})."`

2. **Harness-Aware & Difficulty-Aware JIT Preload Lookahead (`DeterministicBuildStage`):**
   - In [`DeterministicBuildStage`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicBuildStage.cs#L120-L190):
     - Resolve the upcoming `WorkflowStageKind.Agentic` stage's effective profile using `context.ForgeTask?.Difficulty ?? TaskDifficulty.Standard` (including global profile fallback and `DifficultyOverrides` resolution).
     - **Guard on `HarnessId == StandardHarnessIds.Crucible`:** Only trigger `_preloadService.PreloadModelAsync(...)` when `string.Equals(effectiveProfile.HarnessId, StandardHarnessIds.Crucible, StringComparison.OrdinalIgnoreCase)` and `!string.IsNullOrWhiteSpace(effectiveProfile.ModelName)`.
     - When the upcoming stage uses `StandardHarnessIds.Acp` (e.g., an `Expert` task configured with Antigravity ACP + Gemini 4 Argon), skip `_preloadService` so local GPU VRAM is not touched.

---

### 2.4 Web UI & Presentation Specifications

> **UI & Presentation Separation Rule ([AGENTS.md](../../AGENTS.md)):** Keep `.razor` components focused on layout and interaction. Extract difficulty-matrix mapping, role synchronization, and badge formatting into a dedicated service/helper (e.g., `AgentProfileDifficultyMatrixHelper` / `TaskDifficultyPresentationHelper`).

1. **Settings $\rightarrow$ Agent Profiles Tab ([`AgentProfilesTab.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Settings/AgentProfilesTab.razor)):**
   - **Section 1: 🎯 Role-Based Task Difficulty Matrix (`Standard` / `Difficult` / `Expert`):**
     - Provide a visual matrix (or tabbed difficulty tier editor: `Standard`, `Difficult`, `Expert`) configuring the two task-pipeline roles for each difficulty tier:
       - 🏛️ **Architecture & Review Role** (synchronizes across `task-review` and `code-review-and-fix`)
       - ⚡ **Coder / Implementation Role** (applies to `implementation`)
     - Include a **"Same as Architecture & Review"** quick button on the Coder role in `Difficult` and `Expert` tiers for 1-click setup.
     - For each role in each difficulty tier, allow selecting:
       - **Execution Harness (`HarnessId`):** `Crucible (Local / Open-Weight & API)` vs `Antigravity ACP (agy acp)`.
       - **Inference Model (`ModelName`):**
         - When `HarnessId == Crucible`: Discovered Ollama/OllamaRouter/OpenAI model dropdown (with context window and `🧠 Reasoning: low, medium, xhigh` badges).
         - When `HarnessId == Acp`: Discovered Antigravity ACP model dropdown from `IAcpHarnessCatalogService` (`Gemini 4 Argon`, `Gemini 3.8 Flash`, `Claude Opus 5.5`, `Claude Sonnet 5.5`, `GPT-OSS 120B`).
       - **Thinking Effort (`ThinkingEffort`):** `None`, `Low`, `Medium`, `High` (maps to `xhigh` on Qwen 3.8 in Crucible, or `high` `thought_level` in Antigravity ACP), `CustomBudget`.
       - **OllamaRouter Target (`RoutingTarget`):** Displayed when `HarnessId == Crucible` and provider is `OllamaRouter`.
   - **Section 2: 🌐 Standalone Workspace Agents (`triage`, `ideation`) & Per-Stage Advanced Settings:**
     - Display standalone profiles (`triage`, `ideation`) and expandable per-stage settings (`MaxTurns`, `Temperature`, `OptimizationFlags`, `SystemPrompt`).
   - **Profile Persistence (`SaveProfilesAsync` & `CreateDedicatedProfilesAsync`):**
     - Ensure `SaveProfilesAsync` and `CreateDedicatedProfilesAsync` in `AgentProfilesTab.razor` persist `HarnessId`, `HarnessConfigurationJson`, and deep-cloned `DifficultyOverrides` on both global and workspace-dedicated `AgentProfile` entities.
2. **Task Authoring & Backlog UI (`CreateTask.razor`, `TaskDetailDrawer.razor`, `TaskDetail.razor`, `KanbanCard.razor`):**
   - Add a `Difficulty` selector and badge (`Standard` teal/neutral, `Difficult` amber, `Expert` purple/indigo).
3. **Task Execution Inspector (`TaskExecutionInspector.razor`):**
   - Display the task's `Difficulty` badge in the inspector header and an `⚡ Escalated ({Difficulty}: {HarnessId} / {ModelName})` badge on stage conversation cards when executed above `Standard` difficulty.

---

## 3. Implementation Checklist & Deliverables

- [ ] **1. Core Domain, Difficulty Matrix & Markdown Synchronization:**
  - [ ] Add `TaskDifficulty` enum (`Standard`, `Difficult`, `Expert`) and `DifficultyStageOverride` to `NightCodeForge.Core.Domain`.
  - [ ] Add `Difficulty` to `ForgeTask` and `ForgeTaskHistory`, and `DifficultyOverrides` (`Dictionary<TaskDifficulty, DifficultyStageOverride>?`) to `AgentProfile` with an EF Core migration.
  - [ ] Seed default `DifficultyOverrides` in `AgentProfile.CreateStandardGlobalProfiles`.
  - [ ] Extend `TaskProposal`, `CanonicalTools.propose_tasks`, `BacklogMutationToolService`, `TaskFrontmatter`, and `TaskMarkdownSyncService` with `difficulty` serialization and parsing, and update `task-review` prompt instructions so Stage 1 can upgrade task difficulty mid-pipeline.
- [ ] **2. Orchestrator Escalation & Harness-Aware Preload Lookahead:**
  - [ ] Implement difficulty-aware effective `AgentProfile` resolution for `(workspaceId, stageId, task.Difficulty)` in `IWorkspaceRepository` / `TaskWorkspacePreparer` and `TaskOrchestrator`, logging harness/model escalation.
  - [ ] Update `DeterministicBuildStage` lookahead to resolve the difficulty-aware effective profile and skip `ILlmModelPreloadService` preload when `effectiveProfile.HarnessId != StandardHarnessIds.Crucible`.
- [ ] **3. Web UI & Presentation Services:**
  - [ ] Extract `AgentProfileDifficultyMatrixHelper` and `TaskDifficultyPresentationHelper` (keeping logic out of `.razor` `@code` blocks).
  - [ ] Update `AgentProfilesTab.razor` with the **Role-Based Task Difficulty Matrix** (`Standard`, `Difficult`, `Expert` $\times$ **Architecture & Review** vs **Coder / Implementation**).
  - [ ] Add `Difficulty` selectors and badges in `CreateTask.razor`, `TaskDetail.razor`, `TaskDetailDrawer.razor`, `KanbanCard.razor`, and `TaskExecutionInspector.razor`.
- [ ] **4. Automated Testing:**
  - [ ] Unit tests in `TaskOrchestratorTests` verifying `Standard`, `Difficult`, and `Expert` role resolution (including switching from `Crucible` to `ACP` on `Expert` tasks, and mid-pipeline difficulty upgrade after `task-review` markdown sync).
  - [ ] Unit tests in `DeterministicBuildStageTests` verifying lookahead preloads the escalated model for `Crucible` and skips preload when the resolved profile uses `ACP`.
  - [ ] Unit tests for `TaskMarkdownSyncService` round-tripping `difficulty` frontmatter.
  - [ ] Run test suite per AGENTS.md: `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
