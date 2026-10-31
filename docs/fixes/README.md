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
| **11.37A** | [🛡️ Step 11.37A: HTTP Transport Hardening, `ConnectionRefused` Classification & 5-Minute Outage Retry Policy](STEP_11_37A_HTTP_TRANSPORT_HARDENING_AND_OUTAGE_RETRY_POLICY.md) | 5-minute outage survival budget (`MaxRetryDuration = 5m`, `MaxDelay = 30s`, `MaxRetries = 20`) with `Retry-After` compliance in `TransientHttpErrorRetryHandler`, separation of fast UI liveness probes, `TransientErrorDetector` classification of `SocketError.ConnectionRefused` and DNS/network transition errors, disabling `System.ClientModel` duplicate retries, and `SocketsHttpHandler` hardening (`ConnectTimeout = 10s`, OS TCP Keep-Alive, infinite `HttpClient.Timeout`). | Proposed |
| **11.37B** | [🛡️ Step 11.37B: Turn-Level `ResilientChatClient`, 15-Minute Stream Stall Watchdog & Multi-Turn Tool Recovery](STEP_11_37B_TURN_LEVEL_RESILIENT_CHAT_CLIENT_AND_TOOL_RECOVERY.md) | Turn-level `ResilientChatClient` (`DelegatingChatClient`) with 5-minute outage retry budget, Two-Phase Streaming Retry Protocol, **15-minute stream stall watchdog** (`RequestTimeoutMinutes = 15`), pre-call `chatMessages` persistence in `PerServiceCallChatHistoryPersistingChatClient` (preserving prior `ToolResult` on mid-stream drops), suppressing partial turn commits on stream error in `ThinkingAndActivityTracingChatClient`, and outer streaming retry alignment in `CrucibleAgentHarness` & `IdeationService`. | Proposed |
| **11.38B** | [🧱 Step 11.38B: Shared Base Class for `AcpAgentHarness` & `PiRpcAgentHarness` (`ExternalProcessAgentHarnessBase`)](STEP_11_38B_EXTERNAL_PROCESS_AGENT_HARNESS_BASE_AND_ACP_SLIMMING.md) | Extract `ExternalProcessAgentHarnessBase` to eliminate ~180 lines of duplicated process spawning, `HarnessConfigurationJson` parsing, Windows/WSL path resolution, prompt enrichment, activity broadcasting, and transcript persistence between `AcpAgentHarness` and `PiRpcAgentHarness`; extract ACP `fs/*` and `terminal/*` callbacks into `IAcpHostRpcHandler`, reducing `AcpAgentHarness` from 11 to `<= 8` constructor parameters. | Proposed |
| **11.38C** | [🧩 Step 11.38C: Shared Agent Session & Toolset Factorization for `CrucibleAgentHarness`, `IdeationService` & `CanonicalTools`](STEP_11_38C_CRUCIBLE_AND_IDEATION_SHARED_SESSION_AND_TOOLSET_FACTORIZATION.md) | Introduce `CanonicalToolScope` and `ICanonicalToolsetFactory` (eliminating 6 pass-through dependencies from callers), `IAgentWorkspaceContextService` (shared across Crucible, Ideation, and Triage), `IHarnessAgentFactory` (shared model/compaction/options wiring), and `ICrucibleConversationCoordinator` (conversation resumption, priming, `TurnWriter`, and transcripts), reducing `CrucibleAgentHarness` (`23 -> <= 8`), `IdeationService` (`19 -> <= 8`), and `CanonicalTools` (`13 -> <= 8`) constructor parameters. | Proposed |
| **11.38D** | [🏗️ Step 11.38D: `TaskOrchestrator` & `StartupRecoveryService` Decomposition and Zero-Exception `<= 10` Constructor Parameter Enforcement](STEP_11_38D_TASK_ORCHESTRATOR_AND_STARTUP_RECOVERY_DECOMPOSITION.md) | Decompose `TaskOrchestrator` (`19 -> <= 9` params) by extracting `ITaskWorkspacePreparer`, `ITaskMergeAndCascadeCoordinator`, and `ITaskLifecycleMutationCoordinator`; decompose `StartupRecoveryService` (`11 -> <= 8` params) by extracting `IWorkspaceGitRecoveryService`; extract `IContainerEnvironmentFactory` from `ExecutionEnvironmentProvider` (`10 -> <= 5` params); and remove the legacy allowlist from `ConstructorParameterCeilingTests` so zero classes in the solution exceed 10 constructor parameters. | Proposed |
| **11.39A** | [🎭 Step 11.39A: Reusable LLM-Free Playwright E2E Host Fixture & Core Backlog Baseline Tests](STEP_11_39A_PLAYWRIGHT_E2E_HOST_FIXTURE_AND_BACKLOG_BASELINE_TESTS.md) | Reusable `PlaywrightWebHostFixture` (`[Collection("PlaywrightUiE2E")]`, `[Trait("Category", "E2E")]`) booting `NightCodeForge.Web` on an isolated loopback port + temp SQLite DB with `SeedDatabaseAsync` / `QueryDatabaseAsync` and **zero LLM calls**, plus baseline Chromium E2E tests for Workspace Wizard & Switching, Home Dashboard & Pause/Resume toggle, Manual Task Creation (`/tasks/new`), Backlog Kanban/DAG, and `TaskDetail` page. | **MVP E2E Foundation** |
| **11.39B** | [🎭 Step 11.39B: Baseline Playwright E2E Tests for HITL Question Inbox, Review Room, Inspector & Settings](STEP_11_39B_PLAYWRIGHT_E2E_HITL_INBOX_REVIEW_AND_INSPECTOR_TESTS.md) | LLM-free Playwright browser E2E suite covering Human Question Inbox batch answering (`/inbox`), Review Room Git branch diff rendering & task rejection (`/review/{taskId}`), Task Execution Inspector & Transcripts browser (`/inspector`, `/transcripts`), and Settings persistence (`/settings`). | Proposed |

---

## 3. Definition of Done (DoD) Checklist for Remaining Fixes

- [ ] **Step 11.37A (HTTP Transport Hardening, `ConnectionRefused` Classification & 5-Minute Outage Retry Policy):**
  - [ ] Update `RetryPolicyOptions` with 5-minute outage survival defaults (`MaxRetries = 20`, `BaseDelay = 1s`, `MaxDelay = 30s`, `MaxRetryDuration = 5m`) and add `MaxRetryDuration` + `Retry-After` header support in `TransientHttpErrorRetryHandler`.
  - [ ] Separate UI liveness/catalog probes (`ILlmGatewayService`, `ILlmModelCatalogService`) from the 5-minute inference retry loop in `InferenceServiceCollectionExtensions`.
  - [ ] Update `TransientErrorDetector` to classify `SocketError.ConnectionRefused`, `TryAgain`, `HostNotFound`, `NoData`, `OperationAborted`, `Interrupted`, `ClientResultException` transient statuses, and `LlmStreamStallException` as transient.
  - [ ] Disable built-in `System.ClientModel` retries (`maxRetries: 0`) in `OpenAiCompatibleChatClientFactory` to prevent retry multiplication.
  - [ ] Harden `SocketsHttpHandler` with `ConnectTimeout = 10s`, OS-level TCP Keep-Alive, `PooledConnectionIdleTimeout = 1m`, and `HttpClient.Timeout = Timeout.InfiniteTimeSpan`.
  - [ ] Author and update unit tests in `NightCodeForge.Core.Tests` and `NightCodeForge.Agents.Tests` using `TimeProvider` fakes.

- [ ] **Step 11.37B (Turn-Level `ResilientChatClient`, 15-Minute Stream Stall Watchdog & Multi-Turn Tool Recovery):**
  - [ ] Implement turn-level `ResilientChatClient` (`DelegatingChatClient`) with 5-minute outage budget, pre-first-chunk streaming retry, and 15-minute stream stall watchdog (`RequestTimeoutMinutes`).
  - [ ] Update `PerServiceCallChatHistoryPersistingChatClient` to persist incoming `chatMessages` on entry and `ThinkingAndActivityTracingChatClient` to suppress partial turn commits on stream error.
  - [ ] Update `CrucibleAgentHarness` and `IdeationService` outer streaming retry loops to respect `MaxRetryDuration` and recover cleanly from mid-stream drops.
  - [ ] Author unit and integration tests in `NightCodeForge.Agents.Tests` using `TimeProvider` fakes verifying multi-minute outage recovery, 15-minute stall watchdog, and multi-turn mid-stream tool history integrity.

- [ ] **Step 11.38B (Shared Base Class for `AcpAgentHarness` & `PiRpcAgentHarness`):**
  - [ ] Implement `ExternalProcessAgentHarnessBase` and refactor `AcpAgentHarness` and `PiRpcAgentHarness` to inherit from it.
  - [ ] Extract ACP `fs/*` and `terminal/*` host RPC methods into `IAcpHostRpcHandler` (`AcpHostRpcHandler`).
  - [ ] Reduce `AcpAgentHarness` constructor to `<= 8` parameters and remove it from `LegacyConstructorDebtAllowlist`.

- [ ] **Step 11.38C (Shared Agent Session & Toolset Factorization for `CrucibleAgentHarness`, `IdeationService` & `CanonicalTools`):**
  - [ ] Introduce `CanonicalToolScope` and `ICanonicalToolsetFactory`, reducing `CanonicalTools` constructor to `<= 8` parameters.
  - [ ] Extract `IAgentWorkspaceContextService`, `IHarnessAgentFactory`, and `ICrucibleConversationCoordinator`.
  - [ ] Refactor `CrucibleAgentHarness` (`23 -> <= 8` params) and `IdeationService` (`19 -> <= 8` params) to use the shared services and remove `CrucibleAgentHarness`, `IdeationService`, and `CanonicalTools` from `LegacyConstructorDebtAllowlist`.

- [ ] **Step 11.38D (`TaskOrchestrator` & `StartupRecoveryService` Decomposition and Zero-Exception `<= 10` Constructor Parameter Enforcement):**
  - [ ] Extract `ITaskWorkspacePreparer`, `ITaskMergeAndCascadeCoordinator`, and `ITaskLifecycleMutationCoordinator` from `TaskOrchestrator`, reducing its constructor from `19` to `<= 9` parameters.
  - [ ] Extract `IWorkspaceGitRecoveryService` from `StartupRecoveryService` (`11 -> <= 8` params) and `IContainerEnvironmentFactory` from `ExecutionEnvironmentProvider` (`10 -> <= 5` params).
  - [ ] Delete `LegacyConstructorDebtAllowlist` from `ConstructorParameterCeilingTests` so that zero classes in the solution exceed 10 constructor parameters.

- [ ] **Step 11.39A (Reusable LLM-Free Playwright E2E Host Fixture & Core Backlog Baseline Tests):**
  - [ ] Implement `PlaywrightWebHostFixture` (`[Collection("PlaywrightUiE2E")]`) booting `NightCodeForge.Web` on an isolated port + temp SQLite DB with `SeedDatabaseAsync` / `QueryDatabaseAsync` and zero LLM calls.
  - [ ] Tag deterministic browser E2E tests with `[Trait("Category", "E2E")]` (without `"Acceptance"`) so `dotnet test --filter "Category=E2E&Category!=Acceptance"` runs fast LLM-free Playwright tests.
  - [ ] Implement `CoreBacklogAndNavigationE2ETests` covering Workspace Wizard & Switching, Home Dashboard & Pause/Resume toggle, Manual Task Creation (`/tasks/new`), Backlog Kanban/DAG, and `TaskDetail` page.

- [ ] **Step 11.39B (Baseline Playwright E2E Tests for HITL Question Inbox, Review Room, Inspector & Settings):**
  - [ ] Implement `HitlAndObservabilityE2ETests` covering Human Question Inbox batch answering (`/inbox`), Review Room Git branch diff rendering & task rejection (`/review/{taskId}`), Task Execution Inspector & Transcripts browser (`/inspector`, `/transcripts`), and Settings persistence (`/settings`).

---

## 4. Completed Fixes Archive

All 25 previously completed Phase 11 onboarding, hardening, and bug-fix steps (`11.2` through `11.38A`) are archived in:
- **Summary & DoD Record:** [`docs/archive/11_EXTEND_FEATURES.md`](../archive/11_EXTEND_FEATURES.md)
- **Step Specifications:** [`docs/archive/phase-11/`](../archive/phase-11/)
