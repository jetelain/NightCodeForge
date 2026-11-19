# 🔗 Step 11.51: Remove Implicit Sequential Dependencies in `propose_tasks`

> **Parent Plan:** [docs/fixes/README.md](README.md)  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Completed  
> **Target Projects:** `NightCodeForge.Agents`, `NightCodeForge.Agents.Tests`  
> **Aligns With:** `AGENTS.md`, `docs/PLAN.md`

> [!IMPORTANT]
> **Specification Disclaimer:** This document is a behavioral specification. It defines required behaviors, architectural invariants, and verification criteria. Specific internal helper names and implementation details remain at the discretion of the implementer within these constraints.

---

## 1. Context & Motivation

When `propose_tasks` is invoked with multiple tasks in a single call, implicit sequential dependency chaining currently occurs in two places:
1. `BacklogManagementToolModule.ProposeTasksAsync` (`src/NightCodeForge.Agents/Tools/BacklogManagementToolModule.cs`, lines 357–371) injects an implicit dependency on the preceding task (`discovered[i - 1].Title`) whenever a task (`i > 0`) omits both `DependsOnTitles` and `DependsOnIds`.
2. `BacklogMutationToolService.ProposeTasksAsync` / `BacklogMutationToolService.ResolveProposalDependencies` (`src/NightCodeForge.Agents/Tools/BacklogMutationToolService.cs`, lines 467–549) also tracks `previousTaskId` across multi-task batches and injects `if (deps.Count == 0 && !hasTitleDependencies && previousTaskId.HasValue) deps.Add(previousTaskId.Value);`.

Without removing the fallback in `BacklogMutationToolService` as well, multi-task `propose_tasks` calls in Ideation would still chain task `i` to task `i - 1` in SQLite even after `BacklogManagementToolModule.ProposeTasksAsync` is updated. A matching fallback also exists in `IdeationService.ParseProposals`, and the Ideation system prompt states that multiple proposed tasks will have sequential dependencies established automatically.

Previously, this convenience prevented independent tasks from racing without explicit wiring. However, the Ideation agent now has full access to host-level backlog management tools (`propose_tasks` with `depends_on_ids` / `dependsOnTitles`, `get_task`, `edit_task`) and can express dependencies explicitly whenever a genuine ordering constraint exists. Automatically chaining tasks in the same `propose_tasks` batch forces parallelizable tasks into unnecessary sequential execution and surprises maintainers when tasks are independent.

---

## 2. Required Behavior & Constraints

### 2.1 No Implicit Intra-Batch Dependency Injection
- `BacklogManagementToolModule.ProposeTasksAsync` (`src/NightCodeForge.Agents/Tools/BacklogManagementToolModule.cs`) MUST NOT automatically add `discovered[i - 1].Title` to `DependsOnTitles` when multiple tasks are proposed in a single `propose_tasks` call.
- `BacklogMutationToolService.ProposeTasksAsync` and `BacklogMutationToolService.ResolveProposalDependencies` (`src/NightCodeForge.Agents/Tools/BacklogMutationToolService.cs`) MUST NOT track or inject `previousTaskId` across multi-task batches when a proposal omits `DependsOnIds` and `DependsOnTitles`.
- `IdeationService.ParseProposals` (`src/NightCodeForge.Agents/Ideation/IdeationService.cs`) MUST NOT automatically add `parsed[i - 1].Title` to `DependsOnTitles` when multiple tasks are parsed from a legacy JSON array.
- Tasks proposed together without `depends_on_ids` or `dependsOnTitles` MUST remain independent (empty dependency list).

### 2.2 Preserve Explicit Dependencies
- Any explicit `depends_on_ids` or `dependsOnTitles` supplied on a task in a `propose_tasks` call MUST continue to be preserved and resolved as before—including references to existing workspace tasks or earlier tasks in the same batch (resolved via `createdEntities` in `BacklogMutationToolService.ResolveProposalDependencies` and `createdTasks` in `TriageService.ResolveProposalDependencies`).

### 2.3 Update Ideation Agent Prompt Guidance
- Update the Ideation system prompt in `IdeationService` (`src/NightCodeForge.Agents/Ideation/IdeationService.cs`) to remove the claim that multiple proposed tasks have sequential dependencies established automatically, and clarify that tasks proposed together are independent unless `depends_on_ids` / `dependsOnTitles` (or `edit_task`) are explicitly specified.

---

## 3. Definition of Done (DoD) Checklist

- [x] Remove the implicit `discovered[i - 1].Title` dependency injection from `BacklogManagementToolModule.ProposeTasksAsync`.
- [x] Remove the implicit `previousTaskId` dependency chaining from `BacklogMutationToolService.ProposeTasksAsync` and `BacklogMutationToolService.ResolveProposalDependencies`.
- [x] Remove the implicit `parsed[i - 1].Title` dependency injection from `IdeationService.ParseProposals`.
- [x] Update the Ideation system prompt in `IdeationService` to reflect that intra-batch tasks are independent unless explicitly linked.
- [x] Update existing unit tests in:
  - `tests/NightCodeForge.Agents.Tests/BacklogMutationToolServiceTests.cs` (`ProposeTasks_MultipleProposals_CreatesSequentialDraftTasks_AndReturnsJsonArray`),
  - `tests/NightCodeForge.Agents.Tests/Tools/BacklogManagementToolModuleTests.cs` (`ProposeTasks_HandlesAllInputShapes_SequentialAutoChaining_AndDerivedTitles`), and
  - `tests/NightCodeForge.Agents.Tests/IdeationServiceTests.cs` (`ConverseStreamingAsync_ProposeTasks_EstablishesSequentialDependencies_WhenMultipleTasksProposed`, `ConverseStreamingAsync_ProposeTasks_HandlesSerializedJsonStringArgument`, `ConverseStreamingAsync_SystemPrompt_InstructsProposeTasksInsteadOfMarkdownJson`),
  and add/verify tests confirming multi-task `propose_tasks` calls produce independent tasks when no dependencies are specified while still honoring explicit `depends_on_ids` / `dependsOnTitles` (including intra-batch references).
- [x] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
