# 🛡️ Bug Fixes, Resiliency & Hardening Roadmap

> **Active Bug Fixes & Resiliency Specifications**  
> *Parent Index:* [docs/PLAN.md](../PLAN.md) | *Completed Fixes Archive:* [docs/archive/11_EXTEND_FEATURES.md](../archive/11_EXTEND_FEATURES.md) (`docs/archive/phase-11/`)  
> *Target Framework:* .NET 10 (C# 13)

---

## 1. Overview

This directory contains the active technical specifications for remaining **bug fixes, system hardening, and resilience improvements** (historically tracked under Phase 11).

Once a step is implemented and all unit, integration, and end-to-end tests pass, its specification document is moved to [`docs/archive/phase-11/`](../archive/phase-11/) and recorded in [`docs/archive/11_EXTEND_FEATURES.md`](../archive/11_EXTEND_FEATURES.md).

---

## 2. Remaining Steps

| Step | Specification | Focus & Key Deliverables | Status |
| :--- | :--- | :--- | :--- |
| **11.37** | [🛡️ Step 11.37: Outage-Resilient LLM Inference, Turn-Level Recovery & Transport Hardening](STEP_11_37_OUTAGE_RESILIENT_LLM_INFERENCE_AND_TURN_RECOVERY.md) | 5-minute outage survival budget (`MaxRetryDuration = 5m`, `MaxDelay = 30s`, `MaxRetries = 20`) with `Retry-After` support, retry on `ConnectionRefused` and DNS/network transitions, turn-level `ResilientChatClient` with 15-minute stream stall watchdog (`RequestTimeoutMinutes`), pre-call tool result persistence on mid-stream drops, and TCP Keep-Alive transport hardening. | Proposed |

---

## 3. Definition of Done (DoD) Checklist for Remaining Fixes

- [ ] **Step 11.37 (Outage-Resilient LLM Inference, Turn-Level Recovery & Transport Hardening):**
  - [ ] Update `RetryPolicyOptions` with 5-minute outage survival defaults (`MaxRetries = 20`, `BaseDelay = 1s`, `MaxDelay = 30s`, `MaxRetryDuration = 5m`) and add `MaxRetryDuration` + `Retry-After` header support in `TransientHttpErrorRetryHandler`.
  - [ ] Separate UI liveness/catalog probes (`ILlmGatewayService`, `ILlmModelCatalogService`) from the 5-minute inference retry loop in `InferenceServiceCollectionExtensions`.
  - [ ] Update `TransientErrorDetector` to classify `SocketError.ConnectionRefused`, `TryAgain`, `HostNotFound`, `NoData`, `OperationAborted`, `Interrupted`, `ClientResultException` transient statuses, and per-attempt/stream stall timeouts as transient.
  - [ ] Disable built-in `System.ClientModel` retries (`maxRetries: 0`) in `OpenAiCompatibleChatClientFactory` to prevent retry multiplication.
  - [ ] Harden `SocketsHttpHandler` with `ConnectTimeout = 10s`, OS-level TCP Keep-Alive, and `HttpClient.Timeout = Timeout.InfiniteTimeSpan`.
  - [ ] Implement turn-level `ResilientChatClient` (`DelegatingChatClient`) with 5-minute outage budget, pre-first-chunk streaming retry, and 15-minute stream stall watchdog (`RequestTimeoutMinutes`).
  - [ ] Update `PerServiceCallChatHistoryPersistingChatClient` to persist incoming `chatMessages` on entry and `ThinkingAndActivityTracingChatClient` to suppress partial turn commits on stream error.
  - [ ] Update `CrucibleAgentHarness` and `IdeationService` outer streaming retry loops to respect `MaxRetryDuration` and recover cleanly from mid-stream drops.
  - [ ] Author and update unit and integration tests using `TimeProvider` fakes verifying multi-minute outage recovery, `ConnectionRefused` retries, 15-minute stall watchdog, and multi-turn mid-stream tool history integrity.

---

## 4. Completed Fixes Archive

All 24 previously completed Phase 11 onboarding, hardening, and bug-fix steps (`11.2` through `11.36`) are archived in:
- **Summary & DoD Record:** [`docs/archive/11_EXTEND_FEATURES.md`](../archive/11_EXTEND_FEATURES.md)
- **Step Specifications:** [`docs/archive/phase-11/`](../archive/phase-11/)

