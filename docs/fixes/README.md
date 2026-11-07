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
| **11.40** | [🛡️ Step 11.40: Bash Timeout Escalation Ladder, In-Container Process Reaper, Stale Container Recycling & WSL2 Self-Healing](STEP_11_40_BASH_TIMEOUT_CONTAINER_PROCESS_REAPER_AND_WSL_RECOVERY.md) | 3-stage self-healing escalation on `bash` timeout: (1) in-container process sweep (`SIGTERM` $\to$ 5s wait $\to$ `SIGKILL`) preserving `PID 1`/`tail` and whitelisted daemons (`csnav`) + stale `index.lock` cleanup, (2) 5s container liveness probe with container kill/recreate and 5s probe retry, (3) Windows-specific `wsl --shutdown` escalation with Docker daemon readiness check and 30s probe retry, plus `--init` zombie reaping, `--pids-limit 4096` guard, and idle container eviction for `Ready`/`WaitingForHuman`/`Failed` tasks. | Proposed |
| **11.43C** | [⚡ Step 11.43C: `Pmad.AI` Opt-In SLM Delegating Chat Clients (`ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction` & `ToolResultCompaction`)](STEP_11_43C_PMAD_AI_SLM_DELEGATING_CHAT_CLIENTS_OPT_IN.md) | Expose four opt-in (`default: false`) flags on `LocalOptimizationFlags` (`ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction`, `ToolResultCompaction`) tailored for small local LLMs ($\le 8\text{B}$–$14\text{B}$), wiring `ToolCallHealingChatClient`, `ToolLoopCircuitBreakerChatClient`, `ThinkingTraceCompactionChatClient`, and `ToolResultCompactionChatClient` into the `IChatClient` pipeline (`MutateInPlace = false`) alongside Settings UI toggles and transcript frontmatter metadata. | Proposed |
| **11.46** | [🔔 Step 11.46: Live Question Inbox & Review Room Badge Synchronization](STEP_11_46_LIVE_QUESTION_INBOX_AND_REVIEW_ROOM_BADGE_SYNCHRONIZATION.md) | Real-time synchronization of `PendingQuestionCount` and `PendingReviewCount` in `WorkerStateService`, `NavMenu.razor`, and `TopNavBar.razor`: hydrate badge counts on layout initialization and tab foregrounding, update immediately when an agent raises a question via `ask` or a task enters/leaves `Ready`, add a `.nav-badge` counter to the **Review Room** sidebar entry, skip pending questions on task rejection/cancellation, and prevent workspace filtering in `Inbox.razor` from overwriting the global badge count. | Proposed |

---

## 3. Definition of Done (DoD) Checklist for Remaining Fixes

- [ ] **Step 11.40 (Bash Timeout Escalation Ladder, In-Container Process Reaper, Stale Container Recycling & WSL2 Self-Healing):**
  - [ ] Add `ProcessKillWhitelist` (defaulting to `["csnav"]`) and `PidsLimit` (defaulting to `4096`) to `ContainerExecutorOptions`, and include `--init` and `--pids-limit` in `BuildDockerRunArgs`.
  - [ ] Implement Step 1 in-container process reaper (`SIGTERM` $\to$ 5s wait $\to$ `SIGKILL`) on `bash` timeout, preserving `PID 1`/`tail` and whitelisted processes and purging stale `index.lock` files.
  - [ ] Implement Step 2 container liveness probe (5s timeout), container force-recreation (`docker rm -f` + `EnsureContainerRunning`), and probe retry (5s timeout).
  - [ ] Implement Step 3 Windows-specific WSL2 shutdown escalation (`wsl --shutdown`), Docker daemon readiness check, and 30s probe retry.
  - [ ] Expand `TaskContainerReaper` and lifecycle hooks to tear down containers for `Failed`, `Ready`, and `WaitingForHuman` tasks and evict idle workspace containers.
  - [ ] Author unit tests in `NightCodeForge.Infrastructure.Tests` and `NightCodeForge.Core.Tests`.

- [ ] **Step 11.43C (`Pmad.AI` Opt-In SLM Delegating Chat Clients — `ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction` & `ToolResultCompaction`):**
  - [ ] Add `ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction`, and `ToolResultCompaction` (`default: false`, opt-in) to `LocalOptimizationFlags` and `LocalOptimizationFlags.Clone()`.
  - [ ] Wire `ThinkingTraceCompactionChatClient`, `ToolResultCompactionChatClient`, `ToolCallHealingChatClient`, and `ToolLoopCircuitBreakerChatClient` into the `IChatClient` pipeline in `CrucibleAgentHarness` and `IdeationService` (preserving uncompacted `chatHistory` via `MutateInPlace = false`).
  - [ ] Update `SessionTranscriptService`, `TranscriptBrowserService`, `TranscriptMetadataCard.razor`, `GlobalOptimizationSettingsTab.razor`, and `AgentProfilesTab.razor` to serialize, parse, display, and toggle the four opt-in SLM flags.
  - [ ] Add unit tests in `NightCodeForge.Agents.Tests` and `NightCodeForge.Core.Tests` verifying default opt-out behavior, opt-in activation, non-destructive history preservation, and transcript frontmatter round-tripping.
  - [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [ ] **Step 11.46 (Live Question Inbox & Review Room Badge Synchronization):**
  - [ ] Add `PendingReviewCount` to `WorkerStatusSnapshot` and `UpdatePendingReviews(int count)` to `WorkerStateService`, and render `.nav-badge` on the **Review Room** sidebar entry in `NavMenu.razor` when `PendingReviewCount > 0`.
  - [ ] Hydrate `PendingQuestionCount` and `PendingReviewCount` on `MainLayout.razor` initialization and tab foregrounding, and synchronize both counts in real time whenever questions are raised (`ask`), answered, or skipped, and whenever tasks enter or leave `Ready` (Gate 2 completion, Approve & Merge, Request Changes, Reject, Cancel, Restart, or manual mutation).
  - [ ] Mark pending questions as `QuestionStatus.Skipped` on task rejection (`RejectTaskAsync`) and cancellation (`ProcessTaskAsync`), preserve global `PendingQuestionCount` when workspace filtering is toggled in `Inbox.razor`, and auto-reload `Inbox.razor` and `Review.razor` when counts change.
  - [ ] Add unit and bUnit component tests in `NightCodeForge.Core.Tests` and `NightCodeForge.Web.Tests`.

---

## 4. Completed Fixes Archive

All 38 previously completed Phase 11 onboarding, hardening, and bug-fix steps (`11.2` through `11.47`) are archived in:
- **Summary & DoD Record:** [`docs/archive/11_EXTEND_FEATURES.md`](../archive/11_EXTEND_FEATURES.md)
- **Step Specifications:** [`docs/archive/phase-11/`](../archive/phase-11/)
