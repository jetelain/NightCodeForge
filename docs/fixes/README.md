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
| **11.37A** | [🛡️ Step 11.37A: HTTP Transport Hardening, `ConnectionRefused` Classification & 5-Minute Outage Retry Policy](STEP_11_37A_HTTP_TRANSPORT_HARDENING_AND_OUTAGE_RETRY_POLICY.md) | Dedicated LLM 5-minute outage survival budget (`LlmRetryPolicyOptions`: `MaxRetryDuration = 5m`, `MaxDelay = 30s`, `MaxRetries = 20`) with live UI activity notice hooks and `Retry-After` compliance in `TransientHttpErrorRetryHandler`, fast UI liveness probes separation (`ILlmGatewayService`, `ILlmModelCatalogService`), `TransientErrorDetector` classification of `SocketError.ConnectionRefused` and DNS/network transition errors, disabling `System.ClientModel` duplicate retries, and `SocketsHttpHandler` hardening (`ConnectTimeout = 10s`, OS TCP Keep-Alive via `ConnectCallback`, 15-minute `HttpClient.Timeout`). | Proposed |
| **11.38B** | [🧱 Step 11.38B: Shared Base Class for `AcpAgentHarness` & `PiRpcAgentHarness` (`ExternalProcessAgentHarnessBase`)](STEP_11_38B_EXTERNAL_PROCESS_AGENT_HARNESS_BASE_AND_ACP_SLIMMING.md) | Extract `ExternalProcessAgentHarnessBase` to eliminate ~180 lines of duplicated process spawning, `HarnessConfigurationJson` parsing, Windows/WSL path resolution, prompt enrichment, activity broadcasting, and transcript persistence between `AcpAgentHarness` and `PiRpcAgentHarness`; extract ACP `fs/*` and `terminal/*` callbacks into `IAcpHostRpcHandler`, reducing `AcpAgentHarness` from 11 to `<= 8` constructor parameters. | Proposed |
| **11.38C** | [🧩 Step 11.38C: Shared Agent Session & Toolset Factorization for `CrucibleAgentHarness`, `IdeationService` & `CanonicalTools`](STEP_11_38C_CRUCIBLE_AND_IDEATION_SHARED_SESSION_AND_TOOLSET_FACTORIZATION.md) | Introduce `CanonicalToolScope` and `ICanonicalToolsetFactory` (eliminating 6 pass-through dependencies from callers), `IAgentWorkspaceContextService` (shared across Crucible, Ideation, and Triage), `IHarnessAgentFactory` (shared model/compaction/options wiring), and `ICrucibleConversationCoordinator` (conversation resumption, priming, `TurnWriter`, and transcripts), reducing `CrucibleAgentHarness` (`23 -> <= 8`), `IdeationService` (`19 -> <= 8`), and `CanonicalTools` (`13 -> <= 8`) constructor parameters. | Proposed |
| **11.38D** | [🏗️ Step 11.38D: `TaskOrchestrator` & `StartupRecoveryService` Decomposition and Zero-Exception `<= 10` Constructor Parameter Enforcement](STEP_11_38D_TASK_ORCHESTRATOR_AND_STARTUP_RECOVERY_DECOMPOSITION.md) | Decompose `TaskOrchestrator` (`19 -> <= 9` params) by extracting `ITaskWorkspacePreparer`, `ITaskMergeAndCascadeCoordinator`, and `ITaskLifecycleMutationCoordinator`; decompose `StartupRecoveryService` (`11 -> <= 8` params) by extracting `IWorkspaceGitRecoveryService`; extract `IContainerEnvironmentFactory` from `ExecutionEnvironmentProvider` (`10 -> <= 5` params); and remove the legacy allowlist from `ConstructorParameterCeilingTests` so zero classes in the solution exceed 10 constructor parameters. | Proposed |
| **11.39A** | [🎭 Step 11.39A: Reusable LLM-Free Playwright E2E Host Fixture & Core Backlog Baseline Tests](STEP_11_39A_PLAYWRIGHT_E2E_HOST_FIXTURE_AND_BACKLOG_BASELINE_TESTS.md) | Reusable `PlaywrightWebHostFixture` (`[Collection("PlaywrightUiE2E")]`, `[Trait("Category", "E2E")]`) booting `NightCodeForge.Web` on an isolated loopback port + temp SQLite DB with `SeedDatabaseAsync` / `QueryDatabaseAsync` and **zero LLM calls**, plus baseline Chromium E2E tests for Workspace Wizard & Switching, Home Dashboard & Pause/Resume toggle, Manual Task Creation (`/tasks/new`), Backlog Kanban/DAG, and `TaskDetail` page. | **Completed** |
| **11.39B** | [🎭 Step 11.39B: Baseline Playwright E2E Tests for HITL Question Inbox, Review Room, Inspector & Settings](STEP_11_39B_PLAYWRIGHT_E2E_HITL_INBOX_REVIEW_AND_INSPECTOR_TESTS.md) | LLM-free Playwright browser E2E suite covering Human Question Inbox batch answering (`/inbox`), Review Room Git branch diff rendering & task rejection (`/review/{taskId}`), Task Execution Inspector & Transcripts browser (`/inspector`, `/transcripts`), and Settings persistence (`/settings`). | **Completed** |
| **11.40** | [🛡️ Step 11.40: Bash Timeout Escalation Ladder, In-Container Process Reaper, Stale Container Recycling & WSL2 Self-Healing](STEP_11_40_BASH_TIMEOUT_CONTAINER_PROCESS_REAPER_AND_WSL_RECOVERY.md) | 3-stage self-healing escalation on `bash` timeout: (1) in-container process sweep (`SIGTERM` $\to$ 5s wait $\to$ `SIGKILL`) preserving `PID 1`/`tail` and whitelisted daemons (`csnav`) + stale `index.lock` cleanup, (2) 5s container liveness probe with container kill/recreate and 5s probe retry, (3) Windows-specific `wsl --shutdown` escalation with Docker daemon readiness check and 30s probe retry, plus `--init` zombie reaping, `--pids-limit 4096` guard, and idle container eviction for `Ready`/`WaitingForHuman`/`Failed` tasks. | Proposed |

---

## 3. Definition of Done (DoD) Checklist for Remaining Fixes

- [ ] **Step 11.37A (HTTP Transport Hardening, `ConnectionRefused` Classification & 5-Minute Outage Retry Policy):**
  - [ ] Introduce `LlmRetryPolicyOptions` with 5-minute outage defaults (`MaxRetries = 20`, `BaseDelay = 1s`, `MaxDelay = 30s`, `MaxRetryDuration = 300s`) and add `MaxRetryDuration` support to `RetryPolicyOptions`.
  - [ ] Add `RetryNotificationContext` and notification callback support to `TransientHttpErrorRetryHandler` and wire live `AgentActivityKind.Notice` broadcasting in the LLM chat client factories.
  - [ ] Enforce HTTP `Retry-After` header parsing (delta seconds and date) on 429 and 503 responses in `TransientHttpErrorRetryHandler`.
  - [ ] Remove `.AddTransientHttpErrorRetry()` from fast UI liveness and catalog probes (`ILlmGatewayService`, `ILlmModelCatalogService`) in `InferenceServiceCollectionExtensions`.
  - [ ] Update `TransientErrorDetector` to classify `SocketError.ConnectionRefused`, `TryAgain`, `HostNotFound`, `NoData`, `OperationAborted`, and `Interrupted` as transient.
  - [ ] Disable built-in `System.ClientModel` retries (`maxRetries: 0`) in `OpenAiCompatibleChatClientFactory`.
  - [ ] Harden `SocketsHttpHandler` in `OllamaHttpConstants` / chat client factories with `ConnectTimeout = 10s`, OS-level TCP Keep-Alive via `ConnectCallback`, and `PooledConnectionIdleTimeout = 1m`.
  - [ ] Retain targeted 15-minute `HttpClient.Timeout` (`RequestTimeoutMinutes`) across Ollama and OpenAI-compatible factories.
  - [ ] Add and update unit tests in `NightCodeForge.Core.Tests` and `NightCodeForge.Agents.Tests` using `FakeTimeProvider` or fast retry options; verify all tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

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

- [x] **Step 11.39A (Reusable LLM-Free Playwright E2E Host Fixture & Core Backlog Baseline Tests):**
  - [x] Implement `PlaywrightWebHostFixture` (`[Collection("PlaywrightUiE2E")]`) booting `NightCodeForge.Web` on an isolated port + temp SQLite DB with `SeedDatabaseAsync` / `QueryDatabaseAsync` and zero LLM calls.
  - [x] Tag deterministic browser E2E tests with `[Trait("Category", "E2E")]` (without `"Acceptance"`) so `dotnet test --filter "Category=E2E&Category!=Acceptance"` runs fast LLM-free Playwright tests.
  - [x] Implement `CoreBacklogAndNavigationE2ETests` covering Workspace Wizard & Switching, Home Dashboard & Pause/Resume toggle, Manual Task Creation (`/tasks/new`), Backlog Kanban/DAG, and `TaskDetail` page.

- [x] **Step 11.39B (Baseline Playwright E2E Tests for HITL Question Inbox, Review Room, Inspector & Settings):**
  - [x] Implement `HitlAndObservabilityE2ETests` covering Human Question Inbox batch answering (`/inbox`), Review Room Git branch diff rendering & task rejection (`/review/{taskId}`), Task Execution Inspector & Transcripts browser (`/inspector`, `/transcripts`), and Settings persistence (`/settings`).

- [ ] **Step 11.40 (Bash Timeout Escalation Ladder, In-Container Process Reaper, Stale Container Recycling & WSL2 Self-Healing):**
  - [ ] Add `ProcessKillWhitelist` (defaulting to `["csnav"]`) and `PidsLimit` (defaulting to `4096`) to `ContainerExecutorOptions`, and include `--init` and `--pids-limit` in `BuildDockerRunArgs`.
  - [ ] Implement Step 1 in-container process reaper (`SIGTERM` $\to$ 5s wait $\to$ `SIGKILL`) on `bash` timeout, preserving `PID 1`/`tail` and whitelisted processes and purging stale `index.lock` files.
  - [ ] Implement Step 2 container liveness probe (5s timeout), container force-recreation (`docker rm -f` + `EnsureContainerRunning`), and probe retry (5s timeout).
  - [ ] Implement Step 3 Windows-specific WSL2 shutdown escalation (`wsl --shutdown`), Docker daemon readiness check, and 30s probe retry.
  - [ ] Expand `TaskContainerReaper` and lifecycle hooks to tear down containers for `Failed`, `Ready`, and `WaitingForHuman` tasks and evict idle workspace containers.
  - [ ] Author unit tests in `NightCodeForge.Infrastructure.Tests` and `NightCodeForge.Core.Tests`.

---

## 4. Completed Fixes Archive

All 25 previously completed Phase 11 onboarding, hardening, and bug-fix steps (`11.2` through `11.38A`) are archived in:
- **Summary & DoD Record:** [`docs/archive/11_EXTEND_FEATURES.md`](../archive/11_EXTEND_FEATURES.md)
- **Step Specifications:** [`docs/archive/phase-11/`](../archive/phase-11/)
