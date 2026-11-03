# 🛡️ Step 11.37A: HTTP Transport Hardening, `ConnectionRefused` Classification & 5-Minute Outage Retry Policy

> **Bug Fix & Resiliency Step (Transport-Level Outage Resilience & Live Progress Telemetry)**  
> *Parent Plan:* [Bug Fixes & Resiliency Roadmap](README.md)  
> *Target Framework:* .NET 10 (C# 13) | *Status:* Implemented  
> *Target Projects:* [`src/NightCodeForge.Core`](../../src/NightCodeForge.Core), [`src/NightCodeForge.Infrastructure`](../../src/NightCodeForge.Infrastructure), [`src/NightCodeForge.Agents`](../../src/NightCodeForge.Agents), [`tests/NightCodeForge.Core.Tests`](../../tests/NightCodeForge.Core.Tests), [`tests/NightCodeForge.Agents.Tests`](../../tests/NightCodeForge.Agents.Tests)  
> *Dependencies:* [Step 11.29: Global Operations Pause & GPU VRAM Reclamation](../archive/phase-11/STEP_11_29_GLOBAL_OPERATIONS_PAUSE_AND_GPU_VRAM_RECLAMATION.md), [Step 11.33: Unified LLM Provider Architecture](../archive/phase-11/STEP_11_33_UNIFIED_LLM_PROVIDER_DYNAMIC_CONTEXT_AND_FLEET_INTEGRATION.md), [Step 11.35: OpenAI-Compatible LLM Provider](../archive/phase-11/STEP_11_35_OPENAI_COMPATIBLE_LLM_PROVIDER.md)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Summary & Root Cause Analysis

### 1.1 Problem Statement
Long-running autonomous coding stages and interactive ideation sessions frequently span dozens of turns and tens of minutes. During execution, the inference provider (local Ollama, OllamaRouter fleet, vLLM, LM Studio, or remote cloud gateway) can experience **1 to 5 minutes of downtime** due to:
- Server or container restarts (e.g., Ollama / OllamaRouter / vLLM service restart, OS update, or GPU driver reset).
- Short internet, VPN, Wi-Fi, or LAN drops.
- Model eviction and cold weight reloading under VRAM pressure.

Although NightCodeForge includes [`TransientHttpErrorRetryHandler`](../../src/NightCodeForge.Core/Resilience/TransientHttpErrorRetryHandler.cs) and [`TransientErrorDetector`](../../src/NightCodeForge.Core/Resilience/TransientErrorDetector.cs), the transport and HTTP retry layer currently only survives sub-second network blips (~3.5s–7s). Furthermore, retries happen silently at the HTTP layer with zero progress visibility in the UI, and fast health probes are conflated with inference clients.

By consolidating outage resilience into a **hardened HTTP transport layer with live progress notification hooks**, NightCodeForge survives up to 5 minutes of provider downtime cleanly without stacking fragile multi-layer retry systems.

### 1.2 Transport & Retry Root Causes Covered in Step 11.37A

| # | Limitation | Root Cause in Current Codebase | Impact During Outage |
| :--- | :--- | :--- | :--- |
| **1** | **3.5s–7s Total Retry Window** | [`RetryPolicyOptions`](../../src/NightCodeForge.Core/Resilience/RetryPolicyOptions.cs) defaults to `MaxRetries = 3`, `BaseDelay = 1s`, `MaxDelay = 15s`. With $[0.5\times, 1.0\times]$ jitter on $1\text{s}, 2\text{s}, 4\text{s}$, all retries finish in **3.5 to 7 seconds**. | Any server restart or internet drop lasting more than ~7 seconds permanently fails the stage. |
| **2** | **`ConnectionRefused` Fails on Attempt 1** | [`TransientErrorDetector.IsTransient`](../../src/NightCodeForge.Core/Resilience/TransientErrorDetector.cs#L31-L35) explicitly excludes `SocketError.ConnectionRefused`, and [`IsTransientSocketError`](../../src/NightCodeForge.Core/Resilience/TransientErrorDetector.cs#L105-L114) omits DNS/interface transition errors (`TryAgain`, `HostNotFound`, `NoData`, `OperationAborted`, `Interrupted`). | When an inference process restarts while the host OS is up, the OS kernel immediately sends TCP `RST` (`ConnectionRefused`), aborting on Attempt 1 with **0 retries**. |
| **3** | **UI Progress Black Hole During Outage Retries** | [`TransientHttpErrorRetryHandler`](../../src/NightCodeForge.Core/Resilience/TransientHttpErrorRetryHandler.cs) only writes to `ILogger.LogWarning`. It lacks any callback or event hook to surface retry progress to callers. | During a 2–3 minute Ollama restart, the web UI shows the agent spinning silently with zero feedback until it either succeeds or throws. |
| **4** | **HTTP/1.1 Dead-Socket Blindness** | [`OllamaHttpConstants`](../../src/NightCodeForge.Agents/Inference/OllamaHttpConstants.cs) sets `KeepAlivePingDelay = 30s` on `SocketsHttpHandler`, which only applies to HTTP/2+ PING frames and is a no-op on HTTP/1.1 inference endpoints. `ConnectTimeout` is unset (~21–120s OS SYN timeout). | Half-open connections after a router drop or silent reboot hang until the full 15-minute `HttpClient.Timeout` expires. |
| **5** | **Conflated SDK, Search & Probe Retries** | [`InferenceServiceCollectionExtensions`](../../src/NightCodeForge.Infrastructure/InferenceServiceCollectionExtensions.cs#L31-L44) attaches generic retry handlers to 2–3s UI health/catalog probes (`ILlmGatewayService`, `ILlmModelCatalogService`), while [`OpenAiCompatibleChatClientFactory`](../../src/NightCodeForge.Agents/Inference/OpenAiCompatibleChatClientFactory.cs#L103-L129) leaves `System.ClientModel`'s default 3-retry policy active on top of HTTP retries. | Probe failures waste time in backoff loops instead of failing fast, and OpenAI calls multiply retry counts ($4 \times 4$). |
| **6** | **OllamaRouter / Reverse Proxy HTTP 502/504 Outage Exhaustion** | In a multi-node fleet (`OllamaRouter` with YARP), when an upstream inference node (e.g. `aiserver:11434`) reboots, reclaims VRAM, or temporarily disconnects, the local proxy remains alive and immediately returns `502 Bad Gateway`. While 502 is listed in `IsTransientStatusCode`, the default 3-retry window exhausts in 3.5s–7s. Once returned to the client, `TransientErrorDetector.IsTransient` classifies it as non-transient (`httpEx.StatusCode.HasValue`), causing the outer harness loop to fail fast. | Backend GPU node restarts or cold model loads permanently abort pipeline stages after only 5–7 seconds of downtime. |

---

## 2. Detailed Behavioral & Component Specifications

### 2.1 Dedicated LLM Outage Retry Policy & Live Notification Hook

1. **LLM-Specific Retry Options (`LlmRetryPolicyOptions`):**
   - Retain [`RetryPolicyOptions`](../../src/NightCodeForge.Core/Resilience/RetryPolicyOptions.cs) with its existing fast defaults (`MaxRetries = 3`, `BaseDelay = 1s`, `MaxDelay = 15s`, `MaxRetryDuration = 30s`) so non-inference clients (e.g. `OllamaWebSearchService`) continue to fail fast.
   - Introduce `LlmRetryPolicyOptions` (inheriting from or specializing `RetryPolicyOptions`) specifically for LLM inference HTTP clients:
     - `MaxRetries = 20`
     - `BaseDelay = 1.0s`
     - `MaxDelay = 30.0s`
     - `MaxRetryDuration = 5 minutes` (300 seconds)
   - Add `TimeSpan MaxRetryDuration` to `RetryPolicyOptions` (defaulting to `30s` on base, `300s` on `LlmRetryPolicyOptions`).
   - Register `services.AddOptions<LlmRetryPolicyOptions>()` in [`ServiceCollectionExtensions.AddNightCodeForgeAgents`](../../src/NightCodeForge.Agents/ServiceCollectionExtensions.cs) and **replace** `IOptions<RetryPolicyOptions>` with `IOptions<LlmRetryPolicyOptions>` in [`LlmChatClientFactoryBase`](../../src/NightCodeForge.Agents/Inference/LlmChatClientFactories.cs) (and its subclasses) and [`CrucibleAgentHarness`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) so constructor parameter counts do not increase (preserving `<= 10` constructor parameters on all factories per [AGENTS.md](../../AGENTS.md)).
   - In [`TransientHttpErrorRetryHandler`](../../src/NightCodeForge.Core/Resilience/TransientHttpErrorRetryHandler.cs):
     - Track elapsed outage time via `_timeProvider.GetTimestamp()` and `_timeProvider.GetElapsedTime(startTimestamp)`.
     - Permit a retry only when `attempt <= MaxRetries` **and** `elapsed < MaxRetryDuration`.
     - Clamp the calculated jittered backoff delay to the remaining `MaxRetryDuration` (`remaining = MaxRetryDuration - elapsed`).

2. **Live Notification Hook for Activity Feeds:**
   - Define a lightweight notification context (`RetryNotificationContext`) in `NightCodeForge.Core.Resilience` carrying the request target URI, HTTP method, request headers, current attempt number, `MaxRetries`, calculated backoff `Delay`, `ElapsedDuration`, `MaxDuration`, error message, and optional `HttpStatusCode`.
   - Support attaching a notification callback via `HttpRequestOptionsKey` on `HttpRequestMessage.Options` (preserved across cloned requests in `TransientHttpErrorRetryHandler.CloneRequest`) and/or via `RetryPolicyOptions` / non-optional constructor overloads on `TransientHttpErrorRetryHandler` (strictly avoiding optional constructor parameters on DI services and keeping all service constructors $\le 10$ parameters per [AGENTS.md](../../AGENTS.md)).
   - When an attempt fails and enters backoff delay, invoke the notification hook if present.
   - In [`LlmChatClientFactoryBase`](../../src/NightCodeForge.Agents/Inference/LlmChatClientFactories.cs) / agent callers, register a hook that resolves the active session and task context and broadcasts an [`AgentActivityKind.Notice`](../../src/NightCodeForge.Core/Streaming/AgentActivityModels.cs#L18) event via [`IAgentActivityStreamer`](../../src/NightCodeForge.Core/Streaming/AgentActivityModels.cs#L36-L40):
     > *"Inference endpoint unreachable (HTTP 502 Bad Gateway / Connection refused at http://localhost:11434). Retrying attempt 3/20 in 8.0s (elapsed 15s / 300s)..."*
   - **Provider-Agnostic Correlation Requirement:** Because [`StandaloneOllamaChatClientFactory`](../../src/NightCodeForge.Agents/Inference/LlmChatClientFactories.cs#L190-L207) and [`OpenAiCompatibleChatClientFactory`](../../src/NightCodeForge.Agents/Inference/OpenAiCompatibleChatClientFactory.cs#L125) suppress `X-OllamaRouter-Session-Id` and `X-OllamaRouter-Agent-Id` headers on the wire, and session-less clients are cached in `LlmChatClientFactoryBase`, correlation must not rely solely on `X-OllamaRouter-Session-Id` request headers. Resolve `SessionId`, `TaskId`, and `StageId` across all three providers using dedicated HTTP correlation headers (`LlmCorrelationHeaders`), closure capture for session-bound clients, or [`IOperationsTelemetryService.GetActiveTask()`](../../src/NightCodeForge.Core/Interfaces/IOperationsTelemetryService.cs#L75) fallback (without `AsyncLocal` ambient state).
   - **Scope Boundary (Pre-Header vs. Mid-Stream Failures):** `TransientHttpErrorRetryHandler` operates on `SendAsync` and therefore covers all pre-header connection, DNS, TLS, timeout, and HTTP status failures (`408`, `429`, `500`, `502`, `503`, `504`). Specifically for `OllamaRouter`, when an upstream GPU fleet node drops or is unreachable, the proxy returns `502 Bad Gateway` before streaming headers; `TransientHttpErrorRetryHandler` keeps retrying for up to 5 minutes instead of failing fast after 3.5s. Once a streaming response (`GetStreamingResponseAsync` with `ResponseHeadersRead`) has returned `200 OK` headers and begun yielding tokens, any mid-stream socket drop or proxy forwarder truncation (`ResponseBodyDestination` error) during body enumeration surfaces to the caller via `HttpIOException` / `IOException` (assisted by the OS-level TCP Keep-Alive probes in §2.3.2 so dead connections fail within ~30s rather than hanging for 15 minutes). The outer streaming and non-streaming retry loops in [`CrucibleAgentHarness`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) must also utilize `LlmRetryPolicyOptions` (enforcing both `attempt <= MaxRetries` and `elapsed < MaxRetryDuration`, clamping backoff delays to remaining `MaxRetryDuration`, and resetting the outage timer when a stream chunk succeeds) to recover mid-stream disruptions.
   - **Global Operations Pause Respect:** If operations are paused via [`IOperationsControlService`](../../src/NightCodeForge.Core/Interfaces/IOperationsControlService.cs) while a request is in a retry backoff loop (e.g. checked in the retry notification hook / predicate throwing `OperationsPausedException`, or if the caller's `CancellationToken` is cancelled), abort the retry loop immediately rather than continuing to poll the inference endpoint during VRAM reclamation.

3. **`Retry-After` Header Compliance:**
   - When [`TransientHttpErrorRetryHandler`](../../src/NightCodeForge.Core/Resilience/TransientHttpErrorRetryHandler.cs) receives a transient status code (`429 TooManyRequests` or `503 ServiceUnavailable`) containing an HTTP `Retry-After` header (`RetryConditionHeaderValue.Delta` or `Date`), use `Max(calculatedJitteredDelay, retryAfterDelay)` clamped to the remaining `MaxRetryDuration`.

4. **Strict Separation of Fast UI Liveness Probes:**
   - In [`InferenceServiceCollectionExtensions`](../../src/NightCodeForge.Infrastructure/InferenceServiceCollectionExtensions.cs):
     - **Remove** `.AddTransientHttpErrorRetry()` from `ILlmGatewayService` and `ILlmModelCatalogService`.
     - Gateway status (`/api/ps`) and catalog queries (`/api/tags`) are fast 2–3s probes used by the UI; they must fail immediately when the server is offline so UI status badges reflect `Offline` without blocking.
     - Keep `.AddTransientHttpErrorRetry()` only on `ILlmModelPreloadService` (or configure it with `LlmRetryPolicyOptions`).

### 2.2 Comprehensive Transient Error Classification (`TransientErrorDetector`)

1. **Reclassify `SocketError.ConnectionRefused` as Transient:**
   - Remove the `IsConnectionRefused` fail-fast exclusion from [`TransientErrorDetector.IsTransient`](../../src/NightCodeForge.Core/Resilience/TransientErrorDetector.cs#L31-L35).
   - When a local inference process restarts, the host kernel responds immediately with TCP `RST` (`ConnectionRefused`). Classifying it as transient allows the 5-minute backoff loop to wait until the service binds the port again.

2. **Expand Transient Socket and DNS Errors:**
   - Expand `IsTransientSocketError` in [`TransientErrorDetector`](../../src/NightCodeForge.Core/Resilience/TransientErrorDetector.cs#L105-L114) to include transient DNS and network interface transition errors:
     - `SocketError.ConnectionRefused` (10061 / `WSAECONNREFUSED`)
     - `SocketError.TryAgain` (11002 / `WSATRY_AGAIN` temporary DNS failure)
     - `SocketError.HostNotFound` (11001 / `WSAHOST_NOT_FOUND`) and `SocketError.NoData` (11004 / `WSANO_DATA`)
     - `SocketError.OperationAborted` (995 / `WSA_OPERATION_ABORTED`)
     - `SocketError.Interrupted` (10004 / `WSAEINTR`)
     - Existing codes: `ConnectionReset`, `ConnectionAborted`, `TimedOut`, `HostUnreachable`, `NetworkUnreachable`, `NetworkReset`, `Shutdown`, `NotConnected`.

3. **Disable OpenAI SDK (`System.ClientModel`) Duplicate Retries:**
   - In [`OpenAiCompatibleChatClientFactory`](../../src/NightCodeForge.Agents/Inference/OpenAiCompatibleChatClientFactory.cs), configure `OpenAIClientOptions.RetryPolicy` with `maxRetries: 0` (`new ClientRetryPolicy(maxRetries: 0)`).
   - Because `TransientHttpErrorRetryHandler` handles all transient HTTP errors directly at the transport layer, outer SDK retries are redundant and multiplying ($4 \times 20$).

### 2.3 Socket & Transport Hardening (`OllamaHttpConstants`, `SocketsHttpHandler`)

1. **Fast TCP Connect Timeout:**
   - Configure `SocketsHttpHandler.ConnectTimeout` to **10 seconds** (`OllamaHttpConstants.DefaultConnectTimeout = TimeSpan.FromSeconds(10)`).
   - Dropped SYN packets during an internet cut or host reboot fail within 10s and enter the backoff loop instead of blocking on the OS TCP SYN timeout (21–120s).

2. **OS-Level TCP Keep-Alive for HTTP/1.1 Connections:**
   - Because `SocketsHttpHandler.KeepAlivePingDelay` only applies to HTTP/2+, configure OS-level TCP Keep-Alive on sockets created by `SocketsHttpHandler` via `ConnectCallback` (centralized via a helper on `OllamaHttpConstants` used by both [`LlmChatClientFactoryBase`](../../src/NightCodeForge.Agents/Inference/LlmChatClientFactories.cs) and [`OllamaChatClientFactory`](../../src/NightCodeForge.Agents/Inference/OllamaChatClientFactory.cs)):
     - Create a `Socket(SocketType.Stream, ProtocolType.Tcp)` with `NoDelay = true`.
     - Enable `SocketOptionName.KeepAlive` with `TcpKeepAliveTime = 15` (15s), `TcpKeepAliveInterval = 5` (5s), and `TcpKeepAliveRetryCount = 3` (detecting dead peers within ~30s).
     - Connect to `context.DnsEndPoint` using the caller's `CancellationToken` and return a `NetworkStream(socket, ownsSocket: true)`, ensuring `socket.Dispose()` is called on exception before rethrowing.
   - Configure `PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)` so idle pooled sockets are recycled proactively.

3. **Targeted 15-Minute `HttpClient.Timeout`:**
   - Keep `HttpClient.Timeout = TimeSpan.FromMinutes(Math.Max(1, options.RequestTimeoutMinutes))` (default **15 minutes**) in [`OllamaChatClientFactory`](../../src/NightCodeForge.Agents/Inference/OllamaChatClientFactory.cs) and [`OpenAiCompatibleChatClientFactory`](../../src/NightCodeForge.Agents/Inference/OpenAiCompatibleChatClientFactory.cs).
   - **Do NOT** set `HttpClient.Timeout = Timeout.InfiniteTimeSpan`. Preserving the 15-minute timeout prevents unrecoverable process hangs if a connection stalls permanently, while comfortably allowing local models to recompute large prompt KV-caches after cold reloads.

---

## 3. Verification & Test Strategy

1. **`TransientErrorDetectorTests` ([`tests/NightCodeForge.Core.Tests`](../../tests/NightCodeForge.Core.Tests)):**
   - Verify `SocketError.ConnectionRefused` (standalone and wrapped in `HttpRequestException` / `IOException`) returns `true` for `IsTransient`.
   - Verify `SocketError.TryAgain`, `HostNotFound`, `NoData`, `OperationAborted`, and `Interrupted` return `true`.
   - Verify caller `OperationCanceledException` and `OperationsPausedException` continue to return `false`.

2. **`TransientHttpErrorRetryHandlerTests` ([`tests/NightCodeForge.Core.Tests`](../../tests/NightCodeForge.Core.Tests)):**
   - Verify `ConnectionRefused` is retried across attempts and succeeds when the server comes back online.
   - Verify `MaxRetryDuration` stops retrying once total simulated time exceeds `MaxRetryDuration`.
   - Verify `RetryNotificationContext` hook is called on each retry attempt with accurate `RequestUri`, `RequestHeaders`, attempt, delay, elapsed, and remaining times.
   - Verify HTTP `Retry-After` header (delta seconds and date) on `429` and `503` responses is respected and clamped to remaining `MaxRetryDuration`.
   - **Test Safety Requirement:** All unit tests verifying retry delays must inject `FakeTimeProvider` or explicit fast test options (`BaseDelay = 1ms`, `MaxDelay = 5ms`, `MaxRetries = 2`, `MaxRetryDuration = 50ms`) to avoid stalling test execution.

3. **`OllamaSharpRetryBehaviorTests` ([`tests/NightCodeForge.Agents.Tests`](../../tests/NightCodeForge.Agents.Tests)):**
   - Update `GetResponseAsync_ConnectionRefused_FailsFastWithSingleAttempt` to verify that `ConnectionRefused` is now retried up to the configured retry budget and marked with `ResilienceConstants.RetriedKey` upon exhaustion.

---

## 4. Definition of Done (DoD) Checklist

- [x] Introduce `LlmRetryPolicyOptions` with 5-minute outage defaults (`MaxRetries = 20`, `BaseDelay = 1s`, `MaxDelay = 30s`, `MaxRetryDuration = 300s`) and add `MaxRetryDuration` support to `RetryPolicyOptions`; ensure pre-header gateway status codes (`502 Bad Gateway`, `504 Gateway Timeout`) and outer harness streaming enumerator retries leverage the 5-minute budget.
- [x] Add `RetryNotificationContext` and notification callback support to `TransientHttpErrorRetryHandler` and wire live `AgentActivityKind.Notice` broadcasting in the LLM chat client factories.
- [x] Enforce HTTP `Retry-After` header parsing (delta seconds and date) on 429 and 503 responses in `TransientHttpErrorRetryHandler`.
- [x] Remove `.AddTransientHttpErrorRetry()` from fast UI liveness and catalog probes (`ILlmGatewayService`, `ILlmModelCatalogService`) in `InferenceServiceCollectionExtensions`.
- [x] Update `TransientErrorDetector` to classify `SocketError.ConnectionRefused`, `TryAgain`, `HostNotFound`, `NoData`, `OperationAborted`, and `Interrupted` as transient.
- [x] Disable built-in `System.ClientModel` retries (`maxRetries: 0`) in `OpenAiCompatibleChatClientFactory`.
- [x] Harden `SocketsHttpHandler` in `OllamaHttpConstants` / chat client factories with `ConnectTimeout = 10s`, OS-level TCP Keep-Alive via `ConnectCallback`, and `PooledConnectionIdleTimeout = 1m`.
- [x] Retain targeted 15-minute `HttpClient.Timeout` (`RequestTimeoutMinutes`) across Ollama and OpenAI-compatible factories.
- [x] Add and update unit tests in `NightCodeForge.Core.Tests` and `NightCodeForge.Agents.Tests` using `FakeTimeProvider` or fast retry options; verify all tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
