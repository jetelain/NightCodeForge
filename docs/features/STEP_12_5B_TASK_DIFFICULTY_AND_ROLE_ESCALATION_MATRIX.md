# 🧠 Step 12.5B: Task Difficulty Classification & Role-Based Harness/Model Escalation Matrix

> **Phase 12 Implementation Step (Extend Features - Intelligence & Multi-Harness Escalation)** *(formerly Step 11.1 / Step 12.5)*  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion / Upstream Specs:* [Step 12.5A: ACP Harness Catalog, Antigravity (`agy acp`) Authentication & Container-Ready Rework](STEP_12_5A_ACP_HARNESS_CATALOG_ANTIGRAVITY_AND_CONTAINER_REWORK.md) *(order-independent via optional catalog resolution & built-in ACP model fallback)*, [Step 11.33: Unified LLM Provider & Dynamic Fleet Integration](../archive/phase-11/STEP_11_33_UNIFIED_LLM_PROVIDER_DYNAMIC_CONTEXT_AND_FLEET_INTEGRATION.md), [Step 12.27: Dynamic Ollama & OllamaRouter Thinking Effort Discovery & Mapping](../archive/phase-12/STEP_12_27_DYNAMIC_OLLAMA_THINKING_EFFORT_MAPPING.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Agents`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Agents.Tests`, `tests/NightCodeForge.Infrastructure.Tests`, `tests/NightCodeForge.Web.Tests`  
> *Status:* Partially Implemented (Foundations completed in 11.33, 11.35 & 12.27; Difficulty Matrix Planned) | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Summary & Architectural Alignment

### 1.1 Foundations in Place & Order-Independent ACP Integration
1. **Role-Specialized Baseline Profile Generation (`Completed`):**
   - [`LlmProviderOptions`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Inference/LlmProviderOptions.cs), [`ILlmProviderOptions`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Interfaces/ILlmProviderOptions.cs), and [`AgentProfile.CreateStandardGlobalProfiles(ILlmProviderOptions)`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/AgentProfile.cs#L118-L201) configure `triage`, `ideation`, `task-review`, and `code-review-fix` with the Architect/Reviewer model, and `implementation` with the Coder model.
2. **Dynamic Thinking Effort Mapping (`Completed in Step 12.27`):**
   - [`OllamaThinkingEffortMapper`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Inference/OllamaThinkingEffortMapper.cs) maps `ThinkingEffort.High` to `"xhigh"` and `ThinkingEffort.Medium` to `"medium"` on Qwen 3.8 via `/api/show` capability discovery.
3. **Canonical Harness Identifiers & Order-Independent ACP Support:**
   - [`StandardHarnessIds`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/StandardHarnessIds.cs) already defines `StandardHarnessIds.Crucible` (`"crucible"`) and `StandardHarnessIds.Acp` (`"acp"`).
   - [`Step 12.5A`](STEP_12_5A_ACP_HARNESS_CATALOG_ANTIGRAVITY_AND_CONTAINER_REWORK.md) adds `IAcpHarnessCatalogService` and reworks `AcpAgentHarness` for `agy acp` inside `ContainerSandbox` and `WslDirect`. To keep Step 12.5B **order-independent** with Step 12.5A, the UI matrix helper resolves `IAcpHarnessCatalogService` optionally via `IServiceProvider` and provides built-in Antigravity ACP model options (`gemini-4-argon`, `gemini-3.8-flash`, `claude-opus-5-5`, `claude-sonnet-5-5`, `gpt-oss-120b-medium`) whenever `IAcpHarnessCatalogService` is absent or returns no models, while consuming live discovered ACP models automatically once Step 12.5A is present.

### 1.2 Scope of Step 12.5B: Role-Based Difficulty Escalation Across Harnesses & Models
Real-world development backlogs span a wide cognitive spectrum where both the **agent execution harness** (`Crucible` vs `Antigravity ACP`) and the **model / thinking effort** should adapt to a task's difficulty tier:

| Difficulty Tier | 🏛️ Architecture & Review Role (`task-review`, `code-review-fix`) | ⚡ Coder / Implementation Role (`implementation`) | Typical Workload |
| :--- | :--- | :--- | :--- |
| **`Standard`** | **`Crucible`** with `Qwen 3.8 (Dense)` at `ThinkingEffort.High` (`xhigh`) | **`Crucible`** with `Qwen 3.8 (Dense)` at `ThinkingEffort.Medium` (`medium`) **or** `Flint (MoE)` | Routine feature work, CRUD endpoints, UI components, unit test additions, localized bug fixes. |
| **`Difficult`** | **`Crucible`** with `Qwen 3.8 (Dense)` at `ThinkingEffort.High` (`xhigh`) | **`Crucible`** with `Qwen 3.8 (Dense)` at `ThinkingEffort.High` (`xhigh`) | Multi-file refactoring, state machine updates, non-trivial algorithmic or concurrency changes on local GPU. |
| **`Expert`** | **`ACP` (`antigravity`)** with `Gemini 4 Argon` (`high` / `medium`) *(or local `Difficult` tier until configured)* | **`ACP` (`antigravity`)** with `Gemini 4 Argon` (`high` / `medium`) *(or local `Difficult` tier until configured)* | Cross-cutting architectural migrations, deep race-condition debugging, complex multi-project redesigns escalated to a frontier cloud agent. |

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
2. **Domain, Persistence, Optimistic Concurrency & History Integration:**
   - **Domain Entities:** Add `public TaskDifficulty Difficulty { get; set; } = TaskDifficulty.Standard;` to [`ForgeTask`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/ForgeTask.cs) and [`ForgeTaskHistory`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/ForgeTaskHistory.cs).
   - **EF Core Configuration & Migration ([`AppDbContext`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Data/AppDbContext.cs#L530-L701)):**
     - Map `ForgeTask.Difficulty` and `ForgeTaskHistory.Difficulty` via `.HasConversion<int>().HasDefaultValue(TaskDifficulty.Standard)` in `OnModelCreating`.
     - Add an EF Core migration adding `Difficulty` (`INTEGER`, non-null, default `0`) to `Tasks` and `TaskHistories` and `DifficultyOverrides` (`TEXT`, nullable) to `AgentProfiles`.
     - Register the migration ID constant and a `pragma_table_info` baseline guard in `AppDbContext.BaselineExistingPreMigrationDatabaseAsync` so pre-existing/seeded SQLite databases transition without duplicate-column errors.
   - **Repository Persistence & History Snapshots ([`EfCoreTaskRepository`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Data/Repositories/EfCoreTaskRepository.cs), [`InMemoryTaskRepository`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Repositories/ITaskRepository.cs)):**
     - Copy `Difficulty = task.Difficulty` into `ForgeTaskHistory` snapshots in both `CreateWithBaselineAsync` and `UpdateWithHistoryAsync` across both repository implementations.
     - In `EfCoreTaskRepository.UpdateAsync` and `InMemoryTaskRepository.UpdateAsync`, capture and preserve `existing.Difficulty` inside the `authoritativeVersion > task.Version` optimistic-concurrency block (matching `MergeMode`, `Priority`, `Title`, and `Description`) so stale background execution updates never regress a concurrent difficulty change owned by `UpdateWithHistoryAsync`.
   - **History Timeline Diffing ([`TaskHistoryDiffService`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/TaskHistoryDiffService.cs)):**
     - Add `public TaskDifficulty Difficulty { get; set; }` to `TaskHistoryRevisionModel`, populate it in `BuildTimeline`, and emit a `TaskFieldChange("Difficulty", prev.Difficulty.ToString(), curr.Difficulty.ToString(), "badge-purple")` badge whenever `prev.Difficulty != curr.Difficulty`.
   - **Metadata Copy ([`TaskWorkspacePreparer.CopyTaskMetadata`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskWorkspacePreparer.cs#L607-L623)):**
     - Copy `target.Difficulty = source.Difficulty;` so post-stage and pre-resume sync metadata propagation preserves `Difficulty`.
3. **Agent Tools, Proposals & Lifecycle Mutation Contracts:**
   - **Proposal & Edit Records (Backward-Compatible Signatures):**
     - Extend [`TaskProposal`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/ForgeTask.cs) with an optional trailing `string? difficulty = null` parameter (`Difficulty` property), preserving the existing 4-argument and 10-argument constructors.
     - Extend [`TaskEditRequest`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/BacklogMutationToolService.cs) with `string? Difficulty = null`.
     - Extend [`BacklogManagementToolModule`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/BacklogManagementToolModule.cs) (`propose_tasks`, `edit_task`) and [`CanonicalTools.Backlog.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/CanonicalTools.Backlog.cs) with an optional `difficulty` parameter (`"standard"`, `"difficult"`, `"expert"`, defaulting to `null` / `TaskDifficulty.Standard` on creation), keeping existing `ProposeTasks` / `ProposeTasksAsync` / `EditTaskAsync` overloads intact for existing callers.
     - Extend [`BacklogMutationToolService`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/BacklogMutationToolService.cs):
       - Include `difficulty` in the JSON payload returned by `GetTaskAsync`.
       - Validate and apply `request.Difficulty` in `EditTaskAsync` (recording the change in the audit reason and broadcasting/syncing markdown when modified; throwing `ArgumentException` on invalid non-empty difficulty strings).
       - Validate and apply `proposal.Difficulty` in `ProposeTasksAsync` (defaulting to `TaskDifficulty.Standard` when omitted; throwing `ArgumentException` on invalid non-empty difficulty strings).
   - **Downstream Proposal & Task Creation Pipelines:**
     - Extend [`IdeationProposal`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Interfaces/IIdeationService.cs) with optional trailing `string? Difficulty = null`, propagate `p.Difficulty` in [`IdeationService`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Ideation/IdeationService.cs) (both tool proposals and `IdeationProposalDto` JSON fallback), map it onto [`IdeationProposalViewModel`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/IdeationSessionState.cs#L156-L318) (`FromProposal` / `SyncFromTask`), and persist it in [`IdeationPresentationHelper.PromoteOrCreateProposalTaskAsync`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/IdeationPresentationHelper.cs#L72-L152).
     - Propagate parsed `TaskDifficulty` in [`TriageService.TriageExternalReportAsync`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Triage/TriageService.cs) and in [`TaskOrchestrator.ProcessTaskAsync`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) when persisting `PendingProposals`.
     - Add optional trailing `TaskDifficulty Difficulty = TaskDifficulty.Standard` to [`TaskCreationRequest`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/TaskCreationUiService.cs) and assign it to the created `ForgeTask` in `TaskCreationUiService.CreateTaskAsync`.
     - Add `UpdateDifficultyAsync(ForgeTask task, TaskDifficulty newDifficulty, IEnumerable<int>? prerequisiteIds = null, CancellationToken cancellationToken = default)` to [`ITaskMutationService`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/TaskMutationService.cs) / `TaskMutationService`.
     - Extend [`ITaskOrchestrator`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/ITaskOrchestrator.cs), [`ITaskLifecycleMutationCoordinator`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/ITaskLifecycleMutationCoordinator.cs), and [`TaskLifecycleMutationCoordinator`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskLifecycleMutationCoordinator.cs) with a backward-compatible `PromoteSuggestedTaskAsync` overload (accepting `TaskDifficulty? difficulty`) so maintainers can adjust difficulty directly during promotion from `Suggested` to `Open`.
4. **Markdown Frontmatter & 3-Way Synchronization ([`TaskFrontmatter`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Services/TaskFrontmatter.cs), [`TaskMarkdownSyncService`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Services/TaskMarkdownSyncService.cs)):**
   - Add `[YamlMember(Alias = "difficulty")] public string? Difficulty { get; set; }` to `TaskFrontmatter`.
   - Provide canonical helpers `FormatDifficulty(TaskDifficulty)` (`"standard"`, `"difficult"`, `"expert"`) and `ParseDifficulty(string?)` (returns `null` for null/whitespace, parses `"standard"`, `"difficult"`, `"expert"` case-insensitively, and throws `TaskMarkdownSyncException` on unrecognized non-empty values in markdown sync so typos fail fast rather than silently falling back).
   - **Content Hash:** In `GenerateMarkdownWithFrontmatter`, populate `frontmatter.Difficulty = FormatDifficulty(task.Difficulty)`. In `ComputeTaskContentHash`, include `$"difficulty:{(frontmatter.Difficulty ?? string.Empty).Trim()}"` in the canonical hash payload.
   - **All Sync Paths in `TaskMarkdownSyncService`:**
     - `SyncAllTasksFromDiskAsync` Step 4 (new tasks): set `Difficulty = ParseDifficulty(nt.Item.Frontmatter.Difficulty) ?? TaskDifficulty.Standard` and rewrite `nt.Item.Frontmatter.Difficulty = FormatDifficulty(created.Difficulty)`.
     - `SyncAllTasksFromDiskAsync` Step 5 (existing tasks):
       - When `baselineSnapshot != null` (3-way merge against `baseline.Frontmatter.Difficulty`):
         - `diskDifficulty = ParseDifficulty(item.Frontmatter.Difficulty)` and `baselineDifficulty = ParseDifficulty(baseline.Frontmatter.Difficulty) ?? TaskDifficulty.Standard`.
         - `diskDifficultyChanged = diskDifficulty.HasValue && diskDifficulty.Value != baselineDifficulty`.
         - If disk altered `difficulty` relative to baseline, take `diskDifficulty.Value`; otherwise preserve `task.Difficulty` (which retains any concurrent DB edit).
       - When `baselineSnapshot == null` in the concurrent-maintainer-edit branch (`currentTaskId == taskId && task.Version > frontmatter.Version`): preserve `task.Difficulty` (consistent with `Priority`, `MergeMode`, `BaseBranch`). Otherwise take `ParseDifficulty(item.Frontmatter.Difficulty) ?? task.Difficulty`.
       - Include `difficultyChanged = newDifficulty != task.Difficulty` in `anyFieldChanged` so `task.Difficulty = newDifficulty` persists via `UpdateWithHistoryAsync` and increments `task.Version`.
       - Include `task.Difficulty != (ParseDifficulty(item.Frontmatter.Difficulty) ?? TaskDifficulty.Standard)` in `maintainerFieldsSynced` so reconciled DB values rewrite back to disk.
     - `ProcessFileContentAsync`: parse `Difficulty` in both the existing-task update branch (including `difficultyChanged` in `anyFieldChanged`) and the new-task creation branch (`Difficulty = ParseDifficulty(frontmatter.Difficulty) ?? TaskDifficulty.Standard`, normalizing `frontmatter.Difficulty` on rewrite).
5. **Stage 1 (`task-review`) Dynamic Upgrade:**
   - Update the `StandardStageIds.TaskReview` directive in [`StandardWorkflowDefinitions.CreateStandard3AgentWorkflow`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/StandardWorkflowDefinitions.cs) (automatically synchronized to existing databases by `AppDbContext.EnsureDefaultWorkflowsAsync`) to instruct the Task Review Agent to evaluate the task's true technical complexity (`standard`, `difficult`, or `expert`) after inspecting the codebase and update `difficulty:` in the task's markdown YAML frontmatter when escalation is warranted.
   - Include `task.Difficulty` comparison alongside `Title` and `Description` in post-stage modification detection in [`TaskOrchestrator`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) and pre-resume modification detection in [`TaskWorkspacePreparer.SyncPreStageMarkdownAsync`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskWorkspacePreparer.cs#L213-L293).
   - When `task-review` finishes and `TaskOrchestrator` executes post-stage markdown synchronization (`SyncAllTasksFromDiskAsync`) followed by `CopyTaskMetadata`, `task.Difficulty` and `task.Version` update in memory and in SQLite, so all subsequent stages (`PreImplementationBaseline` JIT warmup lookahead, `Implementation`, and `CodeReviewAndFix`) automatically resolve their effective `AgentProfile` for the upgraded `task.Difficulty`.

---

### 2.2 Role-Based Difficulty Matrix on `AgentProfile`

To preserve NightCodeForge's profile inheritance model (Global profiles vs. Workspace-dedicated profiles) without duplicating stage configuration fields onto `Workspace`:

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
2. **`AgentProfile.DifficultyOverrides` Property & Persistence:**
   - Add `public Dictionary<TaskDifficulty, DifficultyStageOverride>? DifficultyOverrides { get; set; }` to [`AgentProfile`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/AgentProfile.cs).
   - Persist via a JSON `ValueConverter` and deep-serialization `ValueComparer` in [`AppDbContext.OnModelCreating`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Data/AppDbContext.cs) (matching `StageDirectives` and `OptimizationFlags`).
   - Provide a `CloneDifficultyOverrides()` helper on `AgentProfile` (deep-cloning dictionary entries via `DifficultyStageOverride.Clone()`).
   - The base properties on `AgentProfile` (`HarnessId`, `ModelName`, `ThinkingEffort`, `ThinkingTokenBudget`, `RoutingTarget`, `ContextWindowTokens`, `MaxOutputTokens`, `HarnessConfigurationJson`) define the **`TaskDifficulty.Standard`** configuration.
   - `DifficultyOverrides[TaskDifficulty.Difficult]` and `DifficultyOverrides[TaskDifficulty.Expert]` define the stage's configuration for `Difficult` and `Expert` tasks respectively.
3. **Configuration Options & Deterministic Baseline Initialization (`ILlmProviderOptions`, `AgentProfile.CreateStandardGlobalProfiles`, `AppDbContext`):**
   - Extend [`ILlmProviderOptions`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Interfaces/ILlmProviderOptions.cs) with default interface properties (`string? ExpertHarnessId => null;`, `string? ExpertModelId => null;`, `ThinkingEffort? ExpertThinkingEffort => null;`), and add matching properties to [`LlmProviderOptions`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Inference/LlmProviderOptions.cs) and `DefaultLlmProviderOptions`.
   - Seed `DifficultyOverrides` on task-pipeline profiles (`StandardStageIds.TaskReview`, `StandardStageIds.Implementation`, `StandardStageIds.CodeReviewAndFix`) in `AgentProfile.CreateStandardGlobalProfiles(ILlmProviderOptions)` while leaving `DifficultyOverrides` as `null` on `triage` and `ideation`:
     - **Standard Tier (base properties):**
       - **Architecture & Review (`task-review`, `code-review-fix`):** `HarnessId = Crucible`, `ModelName = archModel`, `ThinkingEffort = archEffort` (`High` by default, mapping to `"xhigh"` on Qwen 3.8).
       - **Coder (`implementation`):** `HarnessId = Crucible`, `ModelName = coderModel`, `ThinkingEffort = coderEffort` (`Medium` by default).
     - **Difficult Tier (`DifficultyOverrides[TaskDifficulty.Difficult]`):**
       - **Architecture & Review (`task-review`, `code-review-fix`):** `HarnessId = Crucible`, `ModelName = archModel`, `ThinkingEffort = ThinkingEffort.High`.
       - **Coder (`implementation`):** Escalates to the Architect/Review configuration (`HarnessId = Crucible`, `ModelName = archModel`, `ThinkingEffort = ThinkingEffort.High`), so `Difficult` tasks run `Crucible` + `archModel` (`xhigh`) across both review and implementation.
     - **Expert Tier (`DifficultyOverrides[TaskDifficulty.Expert]`):**
       - When `options.ExpertHarnessId` or `options.ExpertModelId` is configured (non-whitespace), set `HarnessId` (defaulting to `StandardHarnessIds.Crucible` if only `ExpertModelId` is supplied, or the trimmed `ExpertHarnessId`), `ModelName` (defaulting to `"gemini-4-argon"` when `HarnessId == StandardHarnessIds.Acp` and `ExpertModelId` is omitted, or `archModel` otherwise), and `ThinkingEffort = options.ExpertThinkingEffort ?? ThinkingEffort.High`.
       - **Safe Default When Unconfigured:** When neither `ExpertHarnessId` nor `ExpertModelId` is set in `ILlmProviderOptions`, initialize `DifficultyOverrides[TaskDifficulty.Expert]` to the `Difficult` tier configuration (`Crucible` + `archModel` + `ThinkingEffort.High`) rather than forcing `acp`/`gemini-4-argon` on machines or automated test suites where `agy` is not installed or configured. Users can opt into `Antigravity ACP` / `gemini-4-argon` either via `LlmProvider` config or in Settings $\rightarrow$ Agent Profiles.
   - **Existing Global Profile Backfill (`AppDbContext.EnsureDefaultGlobalAgentProfilesAsync`):**
     - For existing global task-pipeline profiles (`task-review`, `implementation`, `code-review-fix`) whose `DifficultyOverrides` is `null`, backfill `DifficultyOverrides` from the generated standard profiles (cloning entries and preserving any already-populated overrides). Leave `triage` and `ideation` untouched.

---

### 2.3 Effective Profile Resolution & Harness-Aware Preload Lookahead

1. **Difficulty-Aware Resolution Rules (`AgentProfile.ResolveForDifficulty` & `IWorkspaceRepository`):**
   - Implement `AgentProfile ResolveForDifficulty(TaskDifficulty difficulty, AgentProfile? fallbackArchitectProfile = null)` on [`AgentProfile`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/AgentProfile.cs) (and a helper indicating whether `fallbackArchitectProfile` is needed):
     1. If `difficulty == TaskDifficulty.Standard`, return `this` as-is.
     2. If `difficulty` is `TaskDifficulty.Difficult` or `TaskDifficulty.Expert`:
        - Check `DifficultyOverrides` for `difficulty`; if `difficulty == TaskDifficulty.Expert` has no non-empty entry, check `TaskDifficulty.Difficult`.
        - If a matching `DifficultyStageOverride` exists with a non-empty `ModelName`, return a cloned `AgentProfile` applying the override's `HarnessId` (defaulting to `this.HarnessId` if blank), `ModelName`, `ThinkingEffort`, `ThinkingTokenBudget`, `RoutingTarget`, `ContextWindowTokens` (falling back to `this.ContextWindowTokens` when null and `HarnessId` matches `this.HarnessId`), `MaxOutputTokens` (falling back to `this.MaxOutputTokens` when null), and `HarnessConfigurationJson` (falling back to `this.HarnessConfigurationJson` when null and `HarnessId` matches `this.HarnessId`), while preserving the stage's `Id`, `Name`, `Mode`, `SecurityTier`, `PermittedTools`, `StageDirectives`, `SystemPrompt`, `MaxTurns`, `AllowNoTests`, `Temperature`, `OptimizationFlags`, and `WorkspaceId`.
        - **Fallback when `DifficultyOverrides` is null/empty on a legacy DB row:** If `Id` represents `StandardStageIds.Implementation` (either `"implementation"` or `"ws-{id}:implementation"`) and `fallbackArchitectProfile != null`, return a cloned `AgentProfile` escalating `HarnessId`, `ModelName`, `ContextWindowTokens`, `MaxOutputTokens`, `RoutingTarget`, `HarnessConfigurationJson`, and `ThinkingEffort` (`>= ThinkingEffort.High`, or `fallbackArchitectProfile.ThinkingEffort` if `CustomBudget`/higher) from `fallbackArchitectProfile`, while preserving `Implementation`'s identity, permissions, directives, and flags.
   - Add a 4-argument overload `Task<AgentProfile?> GetEffectiveAgentProfileAsync(int? workspaceId, string stageId, TaskDifficulty difficulty, CancellationToken cancellationToken = default)` on [`IWorkspaceRepository`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Repositories/IWorkspaceRepository.cs), `InMemoryWorkspaceRepository`, and [`EfCoreWorkspaceRepository`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Data/Repositories/EfCoreWorkspaceRepository.cs).
   - **Moq-Safe Integration in [`TaskWorkspacePreparer.ResolveAgentProfileAsync`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskWorkspacePreparer.cs#L64-L94):**
     - Continue resolving `baseProfile` via `workspace.AgentProfiles` (when `InterruptedAgentProfileId` is set) or the existing 3-argument `_workspaceRepository.GetEffectiveAgentProfileAsync(workspace.Id, stageId, cancellationToken)` + `workspace.AgentProfiles` fallback so existing `Mock<IWorkspaceRepository>` setups in unit tests remain 100% compatible.
     - For task pipeline stages when `task.Difficulty != TaskDifficulty.Standard`: only fetch `StandardStageIds.TaskReview` via the 3-argument `GetEffectiveAgentProfileAsync` (or `workspace.AgentProfiles`) if `baseProfile` requires an architect fallback for `task.Difficulty`, then return `baseProfile.ResolveForDifficulty(task.Difficulty, fallbackArchitectProfile)`.
2. **Execution Telemetry & Escalation Logging ([`TaskOrchestrator`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs)):**
   - Resolve `var profile = await _workspacePreparer.ResolveAgentProfileAsync(workspace, task, stage.StageId, cancellationToken);` **before** calling `_telemetryService.RecordActiveTask(...)`, so `hardwareTarget` reflects the actual execution backend:
     - `"Deterministic CPU"` when `stage.Kind == WorkflowStageKind.Deterministic`,
     - `"Antigravity ACP"` when `string.Equals(profile?.HarnessId, StandardHarnessIds.Acp, StringComparison.OrdinalIgnoreCase)`,
     - `"Local GPU"` otherwise.
   - Whenever `stage.Kind == WorkflowStageKind.Agentic && profile != null && task.Difficulty > TaskDifficulty.Standard`, persist an informational `ExecutionLog` (`_taskRepository.AddExecutionLogAsync`) and include escalation metadata on `AgentActivityKind.StageStarted` (`"difficulty"`, `"harnessId"`, `"modelName"`):
     `"Task #{TaskId} [{Difficulty}]: running stage '{StageId}' with harness '{HarnessId}', model '{ModelName}' (thinking: {ThinkingEffort})."`
3. **Harness-Aware & Difficulty-Aware JIT Preload Lookahead ([`DeterministicBuildStage`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicBuildStage.cs#L119-L190)):**
   - Resolve the upcoming `WorkflowStageKind.Agentic` stage's base profile (via `context.Workspace?.AgentProfiles` or the 3-argument `_workspaceRepository.GetEffectiveAgentProfileAsync(context.Workspace.Id, nextStage.StageId, cancellationToken)` for Moq compatibility), then apply `ResolveForDifficulty(context.ForgeTask?.Difficulty ?? TaskDifficulty.Standard, fallbackArchitectProfile)` (resolving `StandardStageIds.TaskReview` only if needed for legacy fallback).
   - **Guard on `HarnessId == StandardHarnessIds.Crucible`:** Only invoke `_preloadService.PreloadModelAsync(...)` when `string.Equals(effectiveProfile.HarnessId, StandardHarnessIds.Crucible, StringComparison.OrdinalIgnoreCase)` and `!string.IsNullOrWhiteSpace(effectiveProfile.ModelName)`.
   - When the resolved effective profile uses `StandardHarnessIds.Acp` (or any non-Crucible harness), skip `_preloadService` so local GPU VRAM is not touched.
4. **Constructor Ceiling Policy ([`AGENTS.md`](../../AGENTS.md)):**
   - Keep all service constructors free of optional parameters and strictly $\le 10$ parameters (`ConstructorParameterCeilingTests`). `TaskWorkspacePreparer` (10), `TaskOrchestrator` (9), `DeterministicBuildStage` (9), `BacklogMutationToolService` (9), `TaskCreationUiService` (9), and `TaskMutationService` (6) require zero additional constructor parameters.

---

### 2.4 Web UI & Presentation Specifications

> **UI & Presentation Separation Rule ([AGENTS.md](../../AGENTS.md)):** Keep `.razor` components focused on layout and interaction. Extract difficulty-matrix mapping, role synchronization, built-in ACP model fallback lists, and badge formatting into dedicated services/helpers (`AgentProfileDifficultyMatrixHelper` and `TaskDifficultyPresentationHelper` in `NightCodeForge.Web.Services`).

1. **Settings $\rightarrow$ Agent Profiles Tab ([`AgentProfilesTab.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Settings/AgentProfilesTab.razor)):**
   - **Section 1: 🎯 Role-Based Task Difficulty Matrix (`#difficulty-escalation-matrix`):**
     - Render a matrix/tier editor across `Standard`, `Difficult`, and `Expert` configuring the two task-pipeline roles for each tier:
       - 🏛️ **Architecture & Review Role** (synchronizes across `task-review` and `code-review-fix`)
       - ⚡ **Coder / Implementation Role** (applies to `implementation`)
     - Include a **"Same as Architecture & Review"** quick button on the Coder role in `Difficult` and `Expert` tiers for 1-click alignment.
     - For each `(Tier, Role)` cell, allow configuring:
       - **Execution Harness (`HarnessId`):** `Crucible (Local / Open-Weight & API)` vs `Antigravity ACP (agy acp)`.
       - **Inference Model (`ModelName`):**
         - When `HarnessId == Crucible`: Discovered Ollama/OllamaRouter/OpenAI model dropdown (with context window and `🧠 Reasoning` badges) plus custom model input toggle.
         - When `HarnessId == Acp`: Discovered Antigravity ACP model dropdown from optional `IAcpHarnessCatalogService` (resolved via `IServiceProvider`), falling back to built-in Antigravity models (`Gemini 4 Argon` / `gemini-4-argon`, `Gemini 3.8 Flash` / `gemini-3.8-flash`, `Claude Opus 5.5` / `claude-opus-5-5`, `Claude Sonnet 5.5` / `claude-sonnet-5-5`, `GPT-OSS 120B` / `gpt-oss-120b-medium`) when `IAcpHarnessCatalogService` is not yet registered or offline, plus custom model input toggle.
       - **Thinking Effort (`ThinkingEffort`):** `None`, `Low`, `Medium`, `High` (maps to `xhigh` on Qwen 3.8 in Crucible, or `high` `thought_level` in Antigravity ACP), `CustomBudget`.
       - **OllamaRouter Target (`RoutingTarget`):** Displayed **only** when `HarnessId == Crucible` and `_providerType == LlmProviderType.OllamaRouter` (hidden for `OpenAiCompatible`, standalone `Ollama`, and `Acp`).
   - **Section 2: 🤖 Per-Stage Profile Cards (`triage`, `ideation`, `task-review`, `implementation`, `code-review-fix`):**
     - Preserve all 5 per-stage profile cards ordered by `Id` (`code-review-fix`, `ideation`, `implementation`, `task-review`, `triage`), keeping `<select class="forge-input font-mono">` on each card's `ModelName` selector and `input[type='number']` on `MaxTurns` / `Temperature`.
     - **Existing bUnit Selector Compatibility (`SettingsComponentTests`):**
       - On the **Role-Based Task Difficulty Matrix** above the profile cards, apply `class="forge-input matrix-model-select"` with inline `style="font-family: var(--font-mono);"` (do **not** use the `.font-mono` class on matrix `<select>` elements), and only render `input[type='number']` inside the matrix when `ThinkingEffort == ThinkingEffort.CustomBudget`. This preserves `cut.Find("select.font-mono")` targeting `code-review-fix`, `cut.Find("input[type='number']")` targeting `MaxTurns`, and `Assert.Empty(cut.FindAll("select option[value='Local']"))` in OpenAI-Compatible mode.
       - Keep matrix changes synchronized into `_profiles` on change, while saving `_profiles` directly in `SaveProfilesAsync` so direct edits to an individual profile card (such as `code-review-fix`) persist cleanly without overwriting `implementation`.
   - **Profile Persistence (`SaveProfilesAsync` & `CreateDedicatedProfilesAsync`):**
     - Ensure `SaveProfilesAsync` persists `HarnessId`, `HarnessConfigurationJson`, and deep-cloned `DifficultyOverrides` (normalized via `AgentProfileDifficultyMatrixHelper`) on existing `AgentProfile` rows.
     - Ensure `CreateDedicatedProfilesAsync` copies `HarnessConfigurationJson` and deep-cloned `DifficultyOverrides` (`g.CloneDifficultyOverrides()`) when cloning global profiles into workspace-scoped profiles.
2. **Task Authoring, Promotion & Backlog UI:**
   - **Authoring & Ideation:** Add a `Difficulty` selector (`Standard`, `Difficult`, `Expert`) in [`CreateTask.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/CreateTask.razor), [`TaskCreationModal.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Backlog/TaskCreationModal.razor), and [`IdeationProposalCard.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Ideation/IdeationProposalCard.razor).
   - **Promotion & Detail Editing:** Add a `Difficulty` selector / interactive chips (persisted via `TaskMutationService.UpdateDifficultyAsync` or `TaskOrchestrator.PromoteSuggestedTaskAsync`) in [`MaintainerPromotionModal.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Backlog/MaintainerPromotionModal.razor), [`TaskDetailDrawer.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor), and [`TaskDetail.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/TaskDetail.razor).
   - **Kanban Card & Badges:** Display the `Difficulty` badge formatted by `TaskDifficultyPresentationHelper` (`Standard` teal/neutral, `Difficult` amber, `Expert` purple/indigo) in [`KanbanCard.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Backlog/KanbanCard.razor) (shown prominently when `Task.Difficulty > TaskDifficulty.Standard` or with consistent badge styling) and in the headers of `TaskDetail.razor` and `TaskDetailDrawer.razor`.
3. **Task Execution Inspector & Stage Cards ([`TaskExecutionInspector.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Monitoring/TaskExecutionInspector.razor), [`StageConversationViewer.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Monitoring/StageConversationViewer.razor)):**
   - Display the task's `Difficulty` badge in the `TaskExecutionInspector.razor` header.
   - Display an `⚡ Escalated ({Difficulty}: {HarnessId} / {ModelName})` badge in `StageConversationViewer.razor` when `Task.Difficulty > TaskDifficulty.Standard` and `Conversation.StageKind == WorkflowStageKind.Agentic`.

---

## 3. Implementation Checklist & Deliverables

- [ ] **1. Core Domain, Persistence, History & Markdown Synchronization:**
  - [ ] Add `TaskDifficulty` enum (`Standard`, `Difficult`, `Expert`) and `DifficultyStageOverride` to `NightCodeForge.Core.Domain`.
  - [ ] Add `Difficulty` to `ForgeTask` and `ForgeTaskHistory`, add `DifficultyOverrides` (`Dictionary<TaskDifficulty, DifficultyStageOverride>?`) and `ResolveForDifficulty(...)` to `AgentProfile`, configure EF Core mappings & migration in `AppDbContext` (with `BaselineExistingPreMigrationDatabaseAsync` `pragma_table_info` guard), update `EfCoreTaskRepository` & `InMemoryTaskRepository` (`CreateWithBaselineAsync`, `UpdateWithHistoryAsync`, and `UpdateAsync` concurrency preservation), update `TaskWorkspacePreparer.CopyTaskMetadata`, and extend `TaskHistoryDiffService`.
  - [ ] Add `ExpertHarnessId`, `ExpertModelId`, and `ExpertThinkingEffort` to `ILlmProviderOptions` / `LlmProviderOptions` / `DefaultLlmProviderOptions`, seed `DifficultyOverrides` in `AgentProfile.CreateStandardGlobalProfiles` (with safe local `Difficult` fallback when Expert options are unset), and backfill missing `DifficultyOverrides` on global task-pipeline profiles in `AppDbContext.EnsureDefaultGlobalAgentProfilesAsync`.
  - [ ] Extend `TaskProposal`, `TaskEditRequest`, `BacklogManagementToolModule` (`propose_tasks`, `edit_task`), `CanonicalTools.Backlog.cs`, `BacklogMutationToolService` (`GetTaskAsync`, `EditTaskAsync`, `ProposeTasksAsync`), `IdeationProposal`, `IdeationProposalViewModel`, `IdeationPresentationHelper`, `TriageService`, `TaskCreationRequest` / `TaskCreationUiService`, `ITaskMutationService`, `ITaskOrchestrator` / `TaskLifecycleMutationCoordinator`, `TaskFrontmatter`, and `TaskMarkdownSyncService` (all 4 sync paths + `ComputeTaskContentHash`), and update the `task-review` directive in `StandardWorkflowDefinitions` so Stage 1 can upgrade task difficulty mid-pipeline.
- [ ] **2. Orchestrator Escalation & Harness-Aware Preload Lookahead:**
  - [ ] Add the 4-argument `GetEffectiveAgentProfileAsync(workspaceId, stageId, difficulty, cancellationToken)` overload to `IWorkspaceRepository`, `InMemoryWorkspaceRepository`, and `EfCoreWorkspaceRepository`, and apply `ResolveForDifficulty` in `TaskWorkspacePreparer.ResolveAgentProfileAsync` using Moq-safe 3-arg calls.
  - [ ] Update `TaskOrchestrator` to resolve the effective `AgentProfile` before `RecordActiveTask` (`"Antigravity ACP"` vs `"Local GPU"` vs `"Deterministic CPU"`), detect `Difficulty` changes in post-stage / pre-resume sync, and log escalation notices.
  - [ ] Update `DeterministicBuildStage` lookahead to resolve the difficulty-aware effective profile and skip `ILlmModelPreloadService` preload whenever `effectiveProfile.HarnessId != StandardHarnessIds.Crucible`, keeping all constructors free of optional parameters and $\le 10$ parameters.
- [ ] **3. Web UI & Presentation Services:**
  - [ ] Extract `AgentProfileDifficultyMatrixHelper` and `TaskDifficultyPresentationHelper` in `NightCodeForge.Web.Services` (keeping non-trivial logic out of `.razor` `@code` blocks).
  - [ ] Update `AgentProfilesTab.razor` with the **Role-Based Task Difficulty Matrix** (`Standard`, `Difficult`, `Expert` $\times$ **Architecture & Review** vs **Coder / Implementation**), optional `IAcpHarnessCatalogService` + built-in ACP model fallback, bUnit-safe selectors, and persistence of `HarnessId`, `HarnessConfigurationJson`, and deep-cloned `DifficultyOverrides`.
  - [ ] Add `Difficulty` selectors and badges in `CreateTask.razor`, `TaskCreationModal.razor`, `IdeationProposalCard.razor`, `MaintainerPromotionModal.razor`, `TaskDetail.razor`, `TaskDetailDrawer.razor`, `KanbanCard.razor`, `TaskExecutionInspector.razor`, and `StageConversationViewer.razor`.
- [ ] **4. Automated Testing:**
  - [ ] Unit & integration tests in `TaskOrchestratorTests` verifying `Standard`, `Difficult`, and `Expert` role resolution (including switching from `Crucible` to `ACP` on `Expert` tasks, `hardwareTarget` telemetry reporting, and mid-pipeline difficulty upgrade after `task-review` markdown sync).
  - [ ] Unit tests in `DeterministicStageTests` verifying lookahead preloads the escalated model for `Crucible` and skips preload when the resolved profile uses `ACP`.
  - [ ] Unit & SQLite integration tests for `TaskMarkdownSyncService` 3-way `difficulty` frontmatter sync, `BacklogMutationToolService` / `BacklogManagementToolModule` `difficulty` validation, `EfCoreTaskRepository.UpdateAsync` optimistic-concurrency preservation, `AgentProfile.DifficultyOverrides` JSON round-trip & migration baseline, `TaskHistoryDiffService`, and bUnit tests for `AgentProfileDifficultyMatrixHelper` / `AgentProfilesTab.razor` / `SettingsComponentTests` / `CreateTaskPageTests` / `TaskDetailPageTests` / `TaskExecutionInspectorComponentTests`.
  - [ ] Run full test suite upon completion per AGENTS.md: `dotnet test` (including Acceptance & E2E suites).
