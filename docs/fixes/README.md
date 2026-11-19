# 🛡️ Bug Fixes, Resiliency & Hardening Roadmap

> **Active Bug Fixes & Resiliency Specifications**  
> *Parent Index:* [docs/PLAN.md](../PLAN.md) | *Completed Fixes Archive:* [docs/archive/11_EXTEND_FEATURES.md](../archive/11_EXTEND_FEATURES.md) (`docs/archive/phase-11/`)  
> *Target Framework:* .NET 10 (C# 13)

---

## 1. Overview

This directory contains the active technical specifications for remaining **bug fixes, system hardening, code factorization, and resilience improvements** (historically tracked under Phase 11).

Once a step is implemented and all unit, integration, and end-to-end tests pass, its specification document is moved to [`docs/archive/phase-11/`](../archive/phase-11/) and recorded in [`docs/archive/11_EXTEND_FEATURES.md`](../archive/11_EXTEND_FEATURES.md).

---

## 2. Remaining Steps

| Step | Specification | Focus & Key Deliverables | Status |
| :--- | :--- | :--- | :--- |
| **11.43C** | [⚡ Step 11.43C: `Pmad.AI` Opt-In SLM Delegating Chat Clients (`ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction` & `ToolResultCompaction`)](STEP_11_43C_PMAD_AI_SLM_DELEGATING_CHAT_CLIENTS_OPT_IN.md) | Expose four opt-in (`default: false`) flags on `LocalOptimizationFlags` (`ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction`, `ToolResultCompaction`) tailored for small local LLMs ($\le 8\text{B}$–$14\text{B}$), wiring `ToolCallHealingChatClient`, `ToolLoopCircuitBreakerChatClient`, `ThinkingTraceCompactionChatClient`, and `ToolResultCompactionChatClient` into the `IChatClient` pipeline (`MutateInPlace = false`) alongside Settings UI toggles and transcript frontmatter metadata. | Proposed |
| **11.49B** | [🧹 Step 11.49B: Retire `CancellationAwareStream` Workaround Upon `OllamaSharp` PR #400 Upgrade](STEP_11_49B_RETIRE_OLLAMASHARP_STREAM_CANCELLATION_WORKAROUND.md) | Once upstream [awaescher/OllamaSharp#400](https://github.com/awaescher/OllamaSharp/pull/400) (passing `CancellationToken` to `StreamReader.ReadLineAsync` on `.NET 7+`) is released on NuGet, upgrade `OllamaSharp`, delete `CancellationAwareStream` / `CancellationAwareHttpContent`, remove ambient `CancellationToken` propagation from `StreamingCancellationScope`, and verify `StreamingIdleWatchdogIntegrationTests` pass natively. | Proposed (Blocked on upstream release) |
| **11.51** | [🔗 Step 11.51: Remove Implicit Sequential Dependencies in `propose_tasks`](STEP_11_51_REMOVE_IMPLICIT_PROPOSE_TASKS_DEPENDENCIES.md) | Remove automatic `i - 1` / `previousTaskId` sequential dependency chaining across tasks created in the same `propose_tasks` call (`BacklogManagementToolModule` and `BacklogMutationToolService`) and legacy `IdeationService.ParseProposals`, keeping explicit `depends_on_ids` / `dependsOnTitles` and updating Ideation prompt guidance. | Completed |
| **11.52** | [🔄 Step 11.52: Synchronize Ideation Proposal View When Agent Updates Proposed Tasks](STEP_11_52_IDEATION_PROPOSAL_VIEW_SYNC_ON_AGENT_UPDATES.md) | Clear stale `DependsOnTitles` and reconcile `SelectedDependencyIds` / `SelectedAttachmentIds` authoritatively from the persisted `ForgeTask` in `IdeationProposalViewModel.FromProposal` & `SyncFromTask`, and prevent end-of-turn `Ideation.razor` reconciliation from resurrecting cancelled proposals. | Completed |
| **11.53** | [⚡ Step 11.53: Schedule `Open` Task When Blocking Dependencies Are Removed](STEP_11_53_SCHEDULE_OPEN_TASK_ON_DEPENDENCY_REMOVAL.md) | Add `ITaskJobQueue.IsJobActive` and `ITaskOrchestrator.TryScheduleOpenTaskIfReadyAsync` (verifying `Open` status, `!IsWaitingForHuman`, `!HasExecutionRun`, closed prerequisites across repository & store, and no active Hangfire job while keeping `BacklogMutationToolService` `<= 10` constructor parameters), invoked from `TaskMutationService.UpdateDependenciesAsync` and `BacklogMutationToolService.EditTaskAsync`. | Proposed |

---

## 3. Definition of Done (DoD) Checklist for Remaining Fixes

- [ ] **Step 11.43C (`Pmad.AI` Opt-In SLM Delegating Chat Clients — `ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction` & `ToolResultCompaction`):**
  - [ ] Add `ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction`, and `ToolResultCompaction` (`default: false`, opt-in) to `LocalOptimizationFlags` and `LocalOptimizationFlags.Clone()`.
  - [ ] Wire `ThinkingTraceCompactionChatClient`, `ToolResultCompactionChatClient`, `ToolCallHealingChatClient`, and `ToolLoopCircuitBreakerChatClient` into the `IChatClient` pipeline in `CrucibleAgentHarness` and `IdeationService` (preserving uncompacted `chatHistory` via `MutateInPlace = false`).
  - [ ] Update `SessionTranscriptService`, `TranscriptBrowserService`, `TranscriptMetadataCard.razor`, `GlobalOptimizationSettingsTab.razor`, and `AgentProfilesTab.razor` to serialize, parse, display, and toggle the four opt-in SLM flags.
  - [ ] Add unit tests in `NightCodeForge.Agents.Tests` and `NightCodeForge.Core.Tests` verifying default opt-out behavior, opt-in activation, non-destructive history preservation, and transcript frontmatter round-tripping.
  - [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [ ] **Step 11.49B (Retire `CancellationAwareStream` Workaround Upon `OllamaSharp` PR #400 Upgrade):**
  - [ ] Upgrade `OllamaSharp` to the NuGet release containing [awaescher/OllamaSharp#400](https://github.com/awaescher/OllamaSharp/pull/400).
  - [ ] Delete `src/NightCodeForge.Core/Resilience/CancellationAwareStream.cs` and remove `CancellationAwareHttpContent` wrapping from `TransientHttpErrorRetryHandler.SendAsync`.
  - [ ] Remove ambient `CancellationToken` propagation from `StreamingCancellationScope`, keeping only attempt/header lifecycle tracking for `PreHeaderPrefill` vs. `FirstTokenPrefill` diagnostics.
  - [ ] Remove `docs/fixes/OLLAMASHARP_STREAMING_CANCELLATION_PR_SPEC.md`.
  - [ ] Verify all `StreamingIdleWatchdogIntegrationTests` and non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [x] **Step 11.51 (Remove Implicit Sequential Dependencies in `propose_tasks`):**
  - [x] Remove implicit `i - 1` / `previousTaskId` dependency chaining from `BacklogManagementToolModule.ProposeTasksAsync`, `BacklogMutationToolService.ProposeTasksAsync` / `ResolveProposalDependencies`, and `IdeationService.ParseProposals`.
  - [x] Update the Ideation system prompt in `IdeationService` to reflect that intra-batch tasks are independent unless explicitly linked.
  - [x] Update/add unit tests in `NightCodeForge.Agents.Tests` (`BacklogMutationToolServiceTests.cs`, `BacklogManagementToolModuleTests.cs`, `IdeationServiceTests.cs`) and verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [x] **Step 11.52 (Synchronize Ideation Proposal View When Agent Updates Proposed Tasks):**
  - [x] Update `IdeationProposalViewModel.FromProposal` and `IdeationProposalViewModel.SyncFromTask` in `IdeationSessionState.cs` so `SyncFromTask` runs authoritatively after initial `proposal.AttachmentIds` mapping, clears `DependsOnTitles`, and replaces `SelectedDependencyIds` and `SelectedAttachmentIds` from the persisted `ForgeTask`.
  - [x] Ensure `Ideation.razor` (both `ToolCallCompleted` and `IdeationStreamUpdateKind.Completed`) and `IdeationSessionState.SyncProposalsWithWorkspaceTasks` reflect agent backlog mutations without re-injecting stale dependencies or re-adding cancelled/rejected proposals.
  - [x] Add unit tests in `NightCodeForge.Web.Tests` (`IdeationSessionStateTests.cs`) and verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [ ] **Step 11.53 (Schedule `Open` Task When Blocking Dependencies Are Removed):**
  - [ ] Add `ITaskJobQueue.IsJobActive` (checking `Enqueued`, `Scheduled`, `Processing`, `Awaiting` states in `HangfireTaskJobQueue`) and clear stale `LastHangfireJobId` on early exit at `EnsureReadyForExecutionAsync`.
  - [ ] Add `ITaskOrchestrator.TryScheduleOpenTaskIfReadyAsync` verifying `Open` status, `!IsWaitingForHuman`, `!HasExecutionRun`, closed prerequisites across both `_taskRepository` and `_dependencyStore`, and `!IsJobActive(task.LastHangfireJobId)`.
  - [ ] Invoke it from both `TaskMutationService.UpdateDependenciesAsync` and `BacklogMutationToolService.EditTaskAsync` after dependency updates while keeping `BacklogMutationToolService` within the `<= 10` constructor parameter ceiling (`ConstructorParameterCeilingTests`).
  - [ ] Add unit tests in `NightCodeForge.Core.Tests`, `NightCodeForge.Infrastructure.Tests`, `NightCodeForge.Web.Tests`, and `NightCodeForge.Agents.Tests`, and verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

---

## 4. Completed Fixes Archive

All 44 completed Phase 11 onboarding, hardening, and bug-fix steps (`11.2` through `11.50`) are archived in:
- **Summary & DoD Record:** [`docs/archive/11_EXTEND_FEATURES.md`](../archive/11_EXTEND_FEATURES.md)
- **Step Specifications:** [`docs/archive/phase-11/`](../archive/phase-11/)
