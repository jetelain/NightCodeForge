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
| **11.43C** | [⚡ Step 11.43C: `Pmad.AI` Opt-In SLM Delegating Chat Clients (`ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction` & `ToolResultCompaction`)](STEP_11_43C_PMAD_AI_SLM_DELEGATING_CHAT_CLIENTS_OPT_IN.md) | Expose four opt-in (`default: false`) flags on `LocalOptimizationFlags` (`ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction`, `ToolResultCompaction`) tailored for small local LLMs ($\le 8\text{B}$–$14\text{B}$), wiring `ToolCallHealingChatClient`, `ToolLoopCircuitBreakerChatClient`, `ThinkingTraceCompactionChatClient`, and `ToolResultCompactionChatClient` into the `IChatClient` pipeline (`MutateInPlace = false`) alongside Settings UI toggles and transcript frontmatter metadata. | Deferred (v0.2) |
| **11.49B** | [🧹 Step 11.49B: Retire `CancellationAwareStream` Workaround Upon `OllamaSharp` PR #400 Upgrade](STEP_11_49B_RETIRE_OLLAMASHARP_STREAM_CANCELLATION_WORKAROUND.md) | Once upstream [awaescher/OllamaSharp#400](https://github.com/awaescher/OllamaSharp/pull/400) (passing `CancellationToken` to `StreamReader.ReadLineAsync` on `.NET 7+`) is released on NuGet, upgrade `OllamaSharp`, delete `CancellationAwareStream` / `CancellationAwareHttpContent`, remove ambient `CancellationToken` propagation from `StreamingCancellationScope`, and verify `StreamingIdleWatchdogIntegrationTests` pass natively. | Proposed (Blocked on upstream release) |
| **11.54** | [🔐 Step 11.54: Store OpenAI-Compatible & Ollama Cloud Search API Keys in OS Keyring via `Pmad.KeySharp`](STEP_11_54_KEYSHARP_OS_KEYRING_STORAGE_FOR_API_KEYS.md) | Store endpoint-normalized OpenAI-compatible API keys and the Ollama Cloud Search API key in the native OS keyring via `Pmad.KeySharp`, remove plaintext `ApiKey` persistence from `config/config.json` and SQLite `SystemSettings`, scrub legacy entries on save, support headless environment-variable read fallback, update runtime consumers (`OpenAiCompatibleChatClientFactory`, `OpenAiCompatibleModelCatalogService`, `OllamaWebSearchService`, `CompanionEndpoints`), and switch `OllamaRouterGatewayCard.razor` & `WebSearchSettingsTab.razor` to write-only credential UX. | Planned (v0.1.0 Common Trunk) |

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

- [ ] **Step 11.54 (Store OpenAI-Compatible & Ollama Cloud Search API Keys in OS Keyring via `Pmad.KeySharp`):**
  - [ ] Define the Core secret-storage abstraction and domain exception, and implement the `Pmad.KeySharp`-backed singleton store in `NightCodeForge.Infrastructure` with normalized endpoint keys for OpenAI-compatible endpoints, dedicated Ollama Cloud Search key coordinates, thread-safe in-memory caching, change notifications, `Keyring.IsAvailable()` / headless read fallback, and fail-fast domain exceptions on write/delete/unexpected errors.
  - [ ] Remove `ApiKey` from `OpenAiCompatibleOptions` and `WebSearchSettings` (Clean Break), scrub legacy `"ApiKey"` JSON entries in `LlmConfigurationWriter` and `WebSearchSettingsService`, stop reading `WebSearch:Ollama:ApiKey` / `OLLAMA_API_KEY` from JSON config, and emit redacted startup/load warnings when legacy plaintext keys remain on disk or in SQLite.
  - [ ] Update `OpenAiCompatibleChatClientFactory` (with client cache invalidation on key change and `<= 10` constructor parameters), `OpenAiCompatibleModelCatalogService`, `OllamaWebSearchService`, and `CompanionEndpoints` (`/api/companion/config` resolving OpenAI keys only when `ProviderType == LlmProviderType.OpenAiCompatible`).
  - [ ] Extract thin presentation helpers and update `OllamaRouterGatewayCard.razor` & `WebSearchSettingsTab.razor` to use write-only/non-echoed key inputs, source status badges, preserve-on-blank Save semantics, explicit Clear/Remove actions, endpoint-switch status refresh, and Air-Gapped mode lockdown; update `docs/CRUCIBLE.md`.
  - [ ] Add isolated `Pmad.KeySharp` integration tests plus mocked unit/component tests across `Core.Tests`, `Infrastructure.Tests`, `Agents.Tests`, and `Web.Tests`, and verify `ConstructorParameterCeilingTests` and all tests pass.

---

## 4. Completed Fixes Archive

All 47 completed Phase 11 onboarding, hardening, and bug-fix steps (`11.2` through `11.53`) are archived in:
- **Summary & DoD Record:** [`docs/archive/11_EXTEND_FEATURES.md`](../archive/11_EXTEND_FEATURES.md)
- **Step Specifications:** [`docs/archive/phase-11/`](../archive/phase-11/)
