# 🧹 Step 11.49B: Retire `CancellationAwareStream` Workaround Upon `OllamaSharp` PR #400 Upgrade

> **Parent Plan:** [docs/fixes/README.md](README.md)  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Proposed (Blocked on upstream release of [awaescher/OllamaSharp#400](https://github.com/awaescher/OllamaSharp/pull/400))  
> **Target Projects:** `NightCodeForge.Core`, `NightCodeForge.Agents`, `NightCodeForge.Agents.Tests`, `NightCodeForge.Core.Tests`  
> **Dependencies:** Step 11.49 (Two-Phase Streaming Idle Watchdog), Upstream PR [awaescher/OllamaSharp#400](https://github.com/awaescher/OllamaSharp/pull/400)  
> **Aligns With:** `AGENTS.md`, `docs/PLAN.md`

> [!IMPORTANT]
> **Specification Disclaimer:** This document is a behavioral specification. It defines required behaviors, architectural invariants, and verification criteria once upstream `OllamaSharp` ships PR #400. Specific internal helper names and implementation details remain at the discretion of the implementer within these constraints.

---

## 1. Context & Motivation

In Step 11.49, `NightCodeForge` introduced a two-phase streaming inactivity watchdog (`FirstTokenTimeout` + sliding `StreamIdleTimeout`) in `TelemetryChatClient`.

Because `OllamaSharp` ($\le 5.4.x$) invoked `await reader.ReadLineAsync().ConfigureAwait(false)` inside `OllamaApiClient.ProcessStreamedChatResponseAsync` **without passing the method's `CancellationToken`**, cancelling the token passed to `IChatClient.GetStreamingResponseAsync` did not unblock an in-flight `ReadLineAsync()` call. Moreover, `HttpClient.SendAsync(..., HttpCompletionOption.ResponseHeadersRead, cancellationToken)` disposes its internal linked `CancellationTokenSource` as soon as HTTP `200 OK` headers are returned.

To deterministically unblock `OllamaSharp`'s token-less `ReadLineAsync()` on watchdog timeouts without triggering a `System.NotSupportedException` from the C# compiler-generated `IAsyncEnumerable` state machine during `DisposeAsync()`, Step 11.49 introduced a transport-level workaround:
1. `StreamingCancellationScope.Current` (propagating `TelemetryChatClient`'s `streamCts.Token` via `AsyncLocal<T>` into `TransientHttpErrorRetryHandler`), and
2. `CancellationAwareHttpContent` / `CancellationAwareStream` (`src/NightCodeForge.Core/Resilience/CancellationAwareStream.cs`), which wraps the HTTP response stream so every `Stream.ReadAsync` links the ambient stream token and disposes the inner stream on cancellation.

### Upstream Fix ([awaescher/OllamaSharp#400](https://github.com/awaescher/OllamaSharp/pull/400))
Upstream PR [#400](https://github.com/awaescher/OllamaSharp/pull/400) updates `OllamaSharp` on `.NET 7+` (`NET7_0_OR_GREATER`, which covers `NightCodeForge` on `.NET 10`) to pass `cancellationToken` directly to `reader.ReadLineAsync(cancellationToken)` and gracefully break out of the streaming loop on cancellation.

Once a new version of `OllamaSharp` containing PR #400 is published to NuGet and referenced by `NightCodeForge`, the `CancellationAwareHttpContent` / `CancellationAwareStream` wrapper and ambient `CancellationToken` propagation become redundant and MUST be retired.

---

## 2. Scope of Cleanup

### 2.1 Upgrade `OllamaSharp` Package Reference
- Bump the `OllamaSharp` NuGet package version in `Directory.Packages.props` / `NightCodeForge.Agents.csproj` to the release that includes [awaescher/OllamaSharp#400](https://github.com/awaescher/OllamaSharp/pull/400).

### 2.2 Remove `CancellationAwareHttpContent` & `CancellationAwareStream`
- Delete `src/NightCodeForge.Core/Resilience/CancellationAwareStream.cs` (`CancellationAwareHttpContent` and `CancellationAwareStream`).
- In `TransientHttpErrorRetryHandler.SendAsync` (`src/NightCodeForge.Core/Resilience/TransientHttpErrorRetryHandler.cs`), remove the `response.Content = new CancellationAwareHttpContent(...)` wrapping block so the raw `HttpResponseMessage.Content` is returned directly.
- Delete `docs/fixes/OLLAMASHARP_STREAMING_CANCELLATION_PR_SPEC.md` (or move it to `docs/archive/phase-11/`).

### 2.3 Simplify `StreamingCancellationScope` (Retain Only Attempt / Header Lifecycle State)
- Remove the ambient `CancellationToken` propagation (`StreamingCancellationScope.Current` / `StreamingCallState.Token`) since `OllamaSharp` natively forwards the `CancellationToken` passed to `IChatClient.GetStreamingResponseAsync` directly into `StreamReader.ReadLineAsync(cancellationToken)`.
- Retain the lightweight `AsyncLocal` attempt/header state tracking (`AttemptNumber`, `ResponseHeadersReceived`, `IsInRetryBackoff`, `CurrentAttemptStartTimestamp`) between `TransientHttpErrorRetryHandler` and `TelemetryChatClient` so that:
  1. `TelemetryChatClient` continues to accurately classify Phase 1 pre-header timeouts (`StreamTimeoutPhase.PreHeaderPrefill`) vs. Phase 2A post-header pre-first-token timeouts (`StreamTimeoutPhase.FirstTokenPrefill`), and
  2. `TelemetryChatClient`'s Phase 2A watchdog continues to reset its prefill wait budget when `TransientHttpErrorRetryHandler` retries a transient pre-header error.

### 2.4 Preserve `streamCts` Watchdog Cancellation in `TelemetryChatClient`
- Retain the linked `CancellationTokenSource` (`streamCts`) in `TelemetryChatClient.GetStreamingResponseAsync`:
  - When `AwaitMoveNextWithWatchdogAsync` throws `TimeoutException`, `TelemetryChatClient` MUST still call `streamCts.Cancel()` and `await moveNextTask` before throwing `LlmStreamTimeoutException`.
  - With `OllamaSharp` PR #400, `streamCts.Cancel()` directly cancels `reader.ReadLineAsync(cancellationToken)` inside `OllamaSharp`, causing `ProcessStreamedChatResponseAsync` to exit its `while` loop cleanly (`moveNextTask` completes with `false`) and run its internal `using` blocks before `await using var enumerator` disposes.

---

## 3. Definition of Done (DoD) Checklist

- [ ] Upgrade `OllamaSharp` to the NuGet version containing [awaescher/OllamaSharp#400](https://github.com/awaescher/OllamaSharp/pull/400).
- [ ] Delete `src/NightCodeForge.Core/Resilience/CancellationAwareStream.cs` and remove `CancellationAwareHttpContent` wrapping from `TransientHttpErrorRetryHandler.SendAsync`.
- [ ] Remove ambient `CancellationToken` propagation from `StreamingCancellationScope`, keeping only the attempt/header lifecycle tracking for `PreHeaderPrefill` vs. `FirstTokenPrefill` diagnostics.
- [ ] Remove `docs/fixes/OLLAMASHARP_STREAMING_CANCELLATION_PR_SPEC.md`.
- [ ] Verify that all streaming watchdog integration tests in `StreamingIdleWatchdogIntegrationTests` (`ScenarioA`, `ScenarioB`, `ScenarioC`, `ScenarioD`) pass natively against the updated `OllamaSharp` package without `CancellationAwareStream`.
- [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

