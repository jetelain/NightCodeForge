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

---

## 4. Completed Fixes Archive

All 44 completed Phase 11 onboarding, hardening, and bug-fix steps (`11.2` through `11.50`) are archived in:
- **Summary & DoD Record:** [`docs/archive/11_EXTEND_FEATURES.md`](../archive/11_EXTEND_FEATURES.md)
- **Step Specifications:** [`docs/archive/phase-11/`](../archive/phase-11/)
