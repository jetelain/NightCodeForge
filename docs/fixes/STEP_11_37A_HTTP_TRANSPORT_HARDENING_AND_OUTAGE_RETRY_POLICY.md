# 🛡️ Step 11.37A: HTTP Transport Hardening, `ConnectionRefused` Classification & 5-Minute Outage Retry Policy

> **Bug Fix & Resiliency Step (Part 1 of 2 — Core & HTTP Transport Layer)**  
> *Parent Plan:* [Bug Fixes & Resiliency Roadmap](README.md)  
> *Companion Step:* [Step 11.37B: Turn-Level `ResilientChatClient`, 15m Stall Watchdog & Tool Recovery](STEP_11_37B_TURN_LEVEL_RESILIENT_CHAT_CLIENT_AND_TOOL_RECOVERY.md)  
> *Target Framework:* .NET 10 (C# 13) | *Status:* Proposed  
> *Target Projects:* [`src/NightCodeForge.Core`](../../src/NightCodeForge.Core), [`src/NightCodeForge.Infrastructure`](../../src/NightCodeForge.Infrastructure), [`src/NightCodeForge.Agents`](../../src/NightCodeForge.Agents), [`tests/NightCodeForge.Core.Tests`](../../tests/NightCodeForge.Core.Tests), [`tests/NightCodeForge.Agents.Tests`](../../tests/NightCodeForge.Agents.Tests)  
> *Dependencies:* [Step 11.29: Global Operations Pause & GPU VRAM Reclamation](../archive/phase-11/STEP_11_29_GLOBAL_OPERATIONS_PAUSE_AND_GPU_VRAM_RECLAMATION.md), [Step 11.33: Unified LLM Provider Architecture](../archive/phase-11/STEP_11_33_UNIFIED_LLM_PROVIDER_DYNAMIC_CONTEXT_AND_FLEET_INTEGRATION.md), [Step 11.35: OpenAI-Compatible LLM Provider](../archive/phase-11/STEP_11_35_OPENAI_COMPATIBLE_LLM_PROVIDER.md)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

---

## 1. Executive Summary & Root Cause Analysis

### 1.1 Problem Statement
Long-running autonomous coding stages and interactive ideation sessions frequently span dozens of turns and tens of minutes. During execution, the inference provider (local Ollama, OllamaRouter fleet, vLLM, LM Studio, or remote cloud gateway) can experience **1 to 5 minutes of downtime** due to:
- Server or container restarts (e.g., Ollama / OllamaRouter / vLLM service restart, OS update, or GPU driver reset).
- Short internet, VPN, Wi-Fi, or LAN drops.
- Model eviction and cold weight reloading under VRAM pressure.

Although NightCodeForge includes [`TransientHttpErrorRetryHandler`](../../src/NightCodeForge.Core/Resilience/TransientHttpErrorRetryHandler.cs) and [`TransientErrorDetector`](../../src/NightCodeForge.Core/Resilience/TransientErrorDetector.cs), the transport and HTTP retry layer currently only survives sub-second network blips.

### 1.2 Transport & Retry Root Causes Covered in Step 11.37A

| # | Limitation | Root Cause in Current Codebase | Impact During Outage |
| :--- | :--- | :--- | :--- |
| **1** | **3.5s–7s Total Retry Window** | [`RetryPolicyOptions`](../../src/NightCodeForge.Core/Resilience/RetryPolicyOptions.cs) defaults to `MaxRetries = 3`, `BaseDelay = 1s`, `MaxDelay = 15s`. With $[0.5\times, 1.0\times]$ jitter on $1\text{s}, 2\text{s}, 4\text{s}$, all retries finish in **3.5 to 7 seconds**. | Any server restart or internet drop lasting more than ~7 seconds permanently fails the stage. |
| **2** | **`ConnectionRefused` Fails on Attempt 1** | [`TransientErrorDetector.IsTransient`](../../src/NightCodeForge.Core/Resilience/TransientErrorDetector.cs#L31-L35) explicitly excludes `SocketError.ConnectionRefused`, and [`IsTransientSocketError`](../../src/NightCodeForge.Core/Resilience/TransientErrorDetector.cs#L105-L114) omits DNS/interface transition errors (`TryAgain`, `HostNotFound`, `NoData`, `OperationAborted`, `Interrupted`). | When an inference process restarts while the host OS is up, the OS kernel immediately sends TCP `RST` (`ConnectionRefused`), aborting on Attempt 1 with **0 retries**. |
| **3** | **HTTP/1.1 Dead-Socket Blindness & Coupled `HttpClient.Timeout`** | [`OllamaHttpConstants`](../../src/NightCodeForge.Agents/Inference/OllamaHttpConstants.cs) sets `KeepAlivePingDelay = 30s` on `SocketsHttpHandler`, which only applies to HTTP/2+ PING frames and is a no-op on HTTP/1.1 inference endpoints. `ConnectTimeout` is unset (~21–120s OS SYN timeout), and `HttpClient.Timeout` (15m) wraps the entire retry handler. | Half-open connections after a router drop or silent reboot hang during connection/streaming, or exhaust the shared `HttpClient.Timeout` across retry attempts. |
| **4** | **Uncoordinated SDK & Probe Retries** | [`OpenAiCompatibleChatClientFactory`](../../src/NightCodeForge.Agents/Inference/OpenAiCompatibleChatClientFactory.cs#L103-L129) leaves `System.ClientModel`'s default 3-retry policy active on top of `TransientHttpErrorRetryHandler`, and [`InferenceServiceCollectionExtensions`](../../src/NightCodeForge.Infrastructure/InferenceServiceCollectionExtensions.cs#L31-L44) attaches inference retry handlers to 2–3s UI health/catalog probes. | OpenAI-compatible calls multiply retries ($4 \times 4$), and multi-minute retry budgets would block fast UI liveness probes if not separated. |

*(Turn-level `ResilientChatClient`, 15-minute stream stall watchdog, and multi-turn tool history recovery are specified in [Step 11.37B](STEP_11_37B_TURN_LEVEL_RESILIENT_CHAT_CLIENT_AND_TOOL_RECOVERY.md).)*

---

## 2. Detailed Behavioral & Component Specifications

### 2.1 Outage-Grade Retry Budget & `Retry-After` Support (`RetryPolicyOptions` & `TransientHttpErrorRetryHandler`)

1. **Default Outage Survival Configuration:**
   - Update [`RetryPolicyOptions`](../../src/NightCodeForge.Core/Resilience/RetryPolicyOptions.cs) defaults to survive a **5-minute** provider outage while recovering within 1–2 seconds from momentary glitches:
     - `DefaultMaxRetries = 20` (`MaxRetries`)
     - `DefaultBaseDelaySeconds = 1.0` (`BaseDelay = 1s`)
     - `DefaultMaxDelaySeconds = 30.0` (`MaxDelay = 30s`)
     - `DefaultMaxRetryDurationSeconds = 300.0` (`MaxRetryDuration = 5 minutes`)
   - Backoff progression follows exponential growth ($1\text{s} \to 2\text{s} \to 4\text{s} \to 8\text{s} \to 16\text{s} \to 30\text{s}$ ceiling) with random jitter in $[0.5\times, 1.0\times]$.
   - A retry attempt is permitted when **both** `attempt <= MaxRetries` **and** `elapsedOutage < MaxRetryDuration` (when `MaxRetryDuration > TimeSpan.Zero`). If the calculated delay would exceed the remaining `MaxRetryDuration`, clamp the delay to the remaining duration.

2. **`Retry-After` Header Compliance:**
   - When [`TransientHttpErrorRetryHandler`](../../src/NightCodeForge.Core/Resilience/TransientHttpErrorRetryHandler.cs) receives a transient status code (`429 TooManyRequests` or `503 ServiceUnavailable`) containing an HTTP `Retry-After` header (`RetryConditionHeaderValue.Delta` or `Date`), use `Max(calculatedJitteredDelay, retryAfterDelay)` clamped to the remaining `MaxRetryDuration`.

3. **Separation of Fast UI Liveness Probes from Long-Horizon Inference Retries:**
   - Gateway status and model catalog discovery services (`ILlmGatewayService`, `ILlmModelCatalogService`) in [`InferenceServiceCollectionExtensions`](../../src/NightCodeForge.Infrastructure/InferenceServiceCollectionExtensions.cs) execute short 2–3 second probes to report cluster liveness to the UI. They must **not** enter a 5-minute retry loop when the inference server is offline; keep them without long-horizon retries (or bounded to their short probe cancellation tokens) so UI status indicators immediately reflect `Offline` during a restart.

### 2.2 Comprehensive Transient Error & Timeout Classification (`TransientErrorDetector`)

1. **Server Restart & Network Transition Socket Errors:**
   - Update [`TransientErrorDetector`](../../src/NightCodeForge.Core/Resilience/TransientErrorDetector.cs) so `SocketError.ConnectionRefused` (`WSAECONNREFUSED`) is classified as **transient** (removing the `IsConnectionRefused` exclusion).
   - Expand `IsTransientSocketError` to include transient DNS and network interface transition errors that occur during short internet cuts or router restarts:
     - `SocketError.ConnectionRefused`
     - `SocketError.TryAgain` (temporary DNS failure `EAI_AGAIN`)
     - `SocketError.HostNotFound` and `SocketError.NoData` (DNS resolution failure while WAN/router link is down)
     - `SocketError.OperationAborted` and `SocketError.Interrupted`
     - Existing codes: `ConnectionReset`, `ConnectionAborted`, `TimedOut`, `HostUnreachable`, `NetworkUnreachable`, `NetworkReset`, `Shutdown`, `NotConnected`.

2. **OpenAI SDK (`System.ClientModel`) Exception & Retry Alignment:**
   - In [`OpenAiCompatibleChatClientFactory`](../../src/NightCodeForge.Agents/Inference/OpenAiCompatibleChatClientFactory.cs), disable the built-in `System.ClientModel` retry policy (`clientOptions.RetryPolicy = new ClientRetryPolicy(maxRetries: 0)`) so retries are never multiplied across layers.
   - Ensure `TransientErrorDetector` and `HttpErrorDetailHelper` recognize `ClientResultException` (or exceptions exposing an HTTP status code or inner transport exception) when evaluating transient failures outside the HTTP handler.

3. **Per-Attempt / Stream Stall Timeouts vs. User Cancellation & Global Pause:**
   - Introduce a dedicated transient timeout exception (`LlmStreamStallException`) produced when an attempt or stream watchdog timer expires while the caller's `CancellationToken` is not cancelled and operations are not paused.
   - `TransientErrorDetector.IsTransient` must return `true` for `LlmStreamStallException` (unless already marked with `ResilienceConstants.RetriedKey`), while continuing to return `false` for explicit caller `OperationCanceledException` / `TaskCanceledException` and `OperationsPausedException`.

### 2.3 Socket & Transport Hardening (`OllamaHttpConstants`, `OllamaChatClientFactory`, `OpenAiCompatibleChatClientFactory`)

1. **Fast TCP Connect Timeout:**
   - Configure `SocketsHttpHandler.ConnectTimeout` to **10 seconds** (`OllamaHttpConstants.DefaultConnectTimeout = TimeSpan.FromSeconds(10)`) so dropped SYN packets during an internet cut or host reboot fail within 10s and enter the backoff loop instead of blocking on a 21–120s OS timeout.

2. **OS-Level TCP Keep-Alive for HTTP/1.1 Connections:**
   - Because `SocketsHttpHandler.KeepAlivePingDelay` only operates on HTTP/2+, configure OS-level TCP Keep-Alive on sockets created by `SocketsHttpHandler` (via `ConnectCallback` or socket options) with:
     - `SocketOptionName.KeepAlive = true`
     - `TcpKeepAliveTime = 15s`
     - `TcpKeepAliveInterval = 5s`
     - `TcpKeepAliveRetryCount = 3`
   - Lower `PooledConnectionIdleTimeout` to **1 minute** so idle pooled sockets are recycled proactively.

3. **Decouple `HttpClient.Timeout` from the Multi-Minute Retry Loop:**
   - Set `HttpClient.Timeout = Timeout.InfiniteTimeSpan` on the `HttpClient` instances created in [`OllamaChatClientFactory`](../../src/NightCodeForge.Agents/Inference/OllamaChatClientFactory.cs) and [`OpenAiCompatibleChatClientFactory`](../../src/NightCodeForge.Agents/Inference/OpenAiCompatibleChatClientFactory.cs) so `HttpClient` does not abort an in-progress multi-minute retry cycle across attempts (per-attempt/per-step 15-minute timeouts are enforced in [Step 11.37B](STEP_11_37B_TURN_LEVEL_RESILIENT_CHAT_CLIENT_AND_TOOL_RECOVERY.md)).

---

## 3. Verification & Test Strategy

1. **`TransientErrorDetectorTests` ([`tests/NightCodeForge.Core.Tests`](../../tests/NightCodeForge.Core.Tests)):**
   - Verify `SocketError.ConnectionRefused` (direct and wrapped in `HttpRequestException` / `IOException`) returns `true` for `IsTransient`.
   - Verify `SocketError.TryAgain`, `HostNotFound`, `NoData`, `OperationAborted`, and `Interrupted` return `true`.
   - Verify `LlmStreamStallException` returns `true`, whereas caller `OperationCanceledException` and `OperationsPausedException` return `false`.
2. **`TransientHttpErrorRetryHandlerTests` ([`tests/NightCodeForge.Core.Tests`](../../tests/NightCodeForge.Core.Tests)):**
   - Verify `ConnectionRefused` is retried across attempts and succeeds when the server comes back online.
   - Verify `MaxRetryDuration` (time-bounded 5-minute outage budget) stops retrying once total elapsed simulated time (`FakeTimeProvider`) exceeds `MaxRetryDuration`, and permits multiple retries within the 5-minute window with zero real wall-clock delay in tests.
   - Verify HTTP `Retry-After` header (delta seconds and date) on `429` and `503` responses is respected and clamped to remaining `MaxRetryDuration`.
3. **`OllamaSharpRetryBehaviorTests` ([`tests/NightCodeForge.Agents.Tests`](../../tests/NightCodeForge.Agents.Tests)):**
   - Update `GetResponseAsync_ConnectionRefused_FailsFastWithSingleAttempt` to verify that `ConnectionRefused` is now retried up to the configured retry budget and marked as retried upon exhaustion.

---

## 4. Definition of Done (DoD) Checklist

- [ ] Update `RetryPolicyOptions` with outage-grade defaults (`MaxRetries = 20`, `BaseDelay = 1s`, `MaxDelay = 30s`, `MaxRetryDuration = 5 minutes`) and enforce `MaxRetryDuration` + `Retry-After` header support in `TransientHttpErrorRetryHandler`.
- [ ] Separate UI liveness/catalog probes (`ILlmGatewayService`, `ILlmModelCatalogService`) from the 5-minute inference retry loop in `InferenceServiceCollectionExtensions`.
- [ ] Update `TransientErrorDetector` to classify `SocketError.ConnectionRefused`, `TryAgain`, `HostNotFound`, `NoData`, `OperationAborted`, `Interrupted`, `ClientResultException` transient statuses, and `LlmStreamStallException` as transient.
- [ ] Disable built-in `System.ClientModel` retries (`maxRetries: 0`) in `OpenAiCompatibleChatClientFactory` to prevent retry multiplication.
- [ ] Harden `SocketsHttpHandler` in `OllamaHttpConstants` / chat client factories with `ConnectTimeout = 10s`, OS-level TCP Keep-Alive sockets, `PooledConnectionIdleTimeout = 1m`, and `HttpClient.Timeout = Timeout.InfiniteTimeSpan`.
- [ ] Add and update unit tests in `NightCodeForge.Core.Tests` and `NightCodeForge.Agents.Tests` using `TimeProvider` fakes; verify all tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

