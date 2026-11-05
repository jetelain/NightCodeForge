# 🛡️ Step 11.44: `web_search` Result Size Capping with `/tmp` Offload, Timeout Classification & 30-Minute Single-Timeout-Retry Policy

> **Bug Fix & Resiliency Step (Context Explosion Prevention & LLM Request Timeout Recovery)**  
> *Parent Plan:* [Bug Fixes & Resiliency Roadmap](README.md)  
> *Target Framework:* .NET 10 (C# 13) | *Status:* Completed  
> *Target Projects:* [`src/NightCodeForge.Core`](../../src/NightCodeForge.Core), [`src/NightCodeForge.Agents`](../../src/NightCodeForge.Agents), [`tests/NightCodeForge.Core.Tests`](../../tests/NightCodeForge.Core.Tests), [`tests/NightCodeForge.Agents.Tests`](../../tests/NightCodeForge.Agents.Tests)  
> *Dependencies:* [Step 11.37A: HTTP Transport Hardening & Outage Retry Policy](STEP_11_37A_HTTP_TRANSPORT_HARDENING_AND_OUTAGE_RETRY_POLICY.md)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Summary & Root Cause Analysis

### 1.1 Live Production Incident (ForgeTask #51 — `task-review`)
In the live production instance (`C:\Users\Julien\AppData\Local\NightCodeForge\data`), the first run of ForgeTask #51 (*"Generate and serve a runtime OpenAPI document for the /api/router/ management API"*, stage `task-review`, transcript `2026-11-04-task-51-task-review-cancelled.md`) was aborted after 55 minutes with status `Cancelled`.

Investigation of the live logs (`nightcodeforge-20261104.log`) and transcript revealed a compounding 4-part failure chain:

| # | Failure Link | Root Cause in Current Codebase | Observed Impact on ForgeTask #51 |
| :--- | :--- | :--- | :--- |
| **1** | **Unbounded `web_search` Output (~308 KB in 1 Turn)** | [`OllamaWebSearchService.SearchAsync`](../../src/NightCodeForge.Agents/Tools/OllamaWebSearchService.cs#L56-L107) returns full scraped page Markdown in `WebSearchResultItem.Content`, and [`CanonicalTools.WebSearchAsync`](../../src/NightCodeForge.Agents/Tools/CanonicalTools.cs#L707-L729) concatenates every result verbatim with zero per-item or total character limit. | Two parallel `web_search` calls (`max_results: 8` each) injected **135,720 chars + 172,226 chars = 307,946 chars** (~80k–125k tokens) into `chatHistory` in a single turn, ballooning context from ~55k to **180k+ tokens**. |
| **2** | **`HttpClient.Timeout` Not Retried** | In [`OllamaChatClientFactory`](../../src/NightCodeForge.Agents/Inference/OllamaChatClientFactory.cs#L56-L60) and [`OpenAiCompatibleChatClientFactory`](../../src/NightCodeForge.Agents/Inference/OpenAiCompatibleChatClientFactory.cs#L126-L129), `HttpClient.Timeout = 15m` wraps [`TransientHttpErrorRetryHandler`](../../src/NightCodeForge.Core/Resilience/TransientHttpErrorRetryHandler.cs) from the outside, and [`TransientErrorDetector.HasNonRetryableMarkerOrCancellation`](../../src/NightCodeForge.Core/Resilience/TransientErrorDetector.cs#L116-L143) treats all `TaskCanceledException` instances as user cancellations—even when `InnerException is TimeoutException`. | When prefill of the 180k+ token prompt exceeded 900s (15m) after GPU KV-cache eviction by another task, `HttpClient` cancelled the inner token and threw `TaskCanceledException` $\to$ `TimeoutException`, bypassing `TransientHttpErrorRetryHandler` with **0 retries**. |
| **3** | **5-Minute Retry Window Shorter Than Request Timeout** | [`LlmRetryPolicyOptions`](../../src/NightCodeForge.Core/Resilience/LlmRetryPolicyOptions.cs) configures `MaxRetryDuration = 300s` (5 minutes). | Even if a 15-minute request timeout were caught, a 5-minute `MaxRetryDuration` window is smaller than a single 15-minute timeout attempt. |
| **4** | **Timeout Misclassified as User Cancellation (`"Stage cancelled"`)** | [`CrucibleAgentHarness`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs#L673), [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs#L730), and [`TelemetryChatClient`](../../src/NightCodeForge.Agents/Inference/TelemetryChatClient.cs#L153) catch bare `OperationCanceledException` without checking whether the caller's `CancellationToken` (or pause/cancel state) was actually triggered, and [`HttpErrorDetailHelper.FormatLlmInvocationError`](../../src/NightCodeForge.Core/Resilience/HttpErrorDetailHelper.cs#L112-L161) formats timeouts as `"The request was canceled due to the configured HttpClient.Timeout of 900 seconds elapsing. (Inner: The operation was canceled.)"`. | The stage was recorded as `Cancelled` (`"Stage cancelled"` / `"Stage execution was cancelled."`) instead of `Failed` with a clear timeout diagnostic, leaving the task stranded in `InProgress` with a cancelled conversation. |

---

## 2. Detailed Behavioral & Component Specifications

### 2.1 `web_search` Result Size Capping & `/tmp` Full-Content Offload

1. **Named Size Ceiling Constants:**
   - Define named constants (no magic numbers per [AGENTS.md](../../AGENTS.md)) in [`WorkflowDefaults`](../../src/NightCodeForge.Core/Workflow/WorkflowDefaults.cs) (or a dedicated constants class):
     - **Per-result inline content limit:** `MaxWebSearchResultInlineChars = 3_000` characters (~750 tokens) per individual search result item.
     - **Total inline tool output limit:** `MaxWebSearchTotalInlineChars = 12_000` characters (~3,000 tokens) across the entire `web_search` response string.

2. **Automatic Full-Result Offload to `/tmp`:**
   - In [`CanonicalTools.WebSearchAsync`](../../src/NightCodeForge.Agents/Tools/CanonicalTools.cs#L707-L729):
     - Determine whether any returned `WebSearchResultItem.Content` exceeds `MaxWebSearchResultInlineChars` or whether the complete formatted output exceeds `MaxWebSearchTotalInlineChars`.
     - When no truncation is needed (all items and total length are within the inline caps), return the formatted results directly without creating a `/tmp` file.
     - When truncation is triggered:
       - Format the **complete, untruncated** search results as a structured Markdown document containing the query, result count, and each numbered `[i] Title (URL)` section with its full `Content`.
       - Persist the full document to a unique file under the virtual `/tmp` directory (e.g., `/tmp/web-search-{timestamp}-{shortId}.md`), resolving its host/WSL path via `ResolveFullPath(virtualTmpPath)` and validating it via `ValidateWithinWorkspace(fullPath, virtualTmpPath)` (creating the parent `/tmp` directory if it does not yet exist).
       - Because `/home/<user>/.nightcodeforge/wks/<id>/tmp` is bind-mounted at `/tmp` inside the agent's Docker container and mapped by [`WorkspacePathResolver.TryResolveTmpSubPath`](../../src/NightCodeForge.Infrastructure/Execution/WorkspacePathResolver.cs#L388), the agent can immediately inspect the file using either `read` (`read(path: "/tmp/web-search-...", offset: ..., limit: ...)`) or `bash` (`grep`, `rg`, `sed`, `head` on `/tmp/web-search-...`).
       - Record the 1-indexed line range (`lines {startLine}–{endLine}` in `/tmp/web-search-...`) of each `[i]` result section in the saved file.

3. **Capped Inline Output Format & Guidance:**
   - When truncation is active, format each result `[i]` in the inline tool response with:
     - Its header: `[{i}] {Title} ({Url}) [full content: {virtualTmpPath} lines {startLine}-{endLine}, {content.Length:N0} chars]`
     - Its inline snippet capped at `MaxWebSearchResultInlineChars` (with a trailing `... [truncated inline]` marker when that item was shortened).
   - If the accumulated inline output still approaches `MaxWebSearchTotalInlineChars`, stop appending full snippets for remaining items (listing at least their `[{i}] {Title} ({Url}) [full content: {virtualTmpPath} lines {startLine}-{endLine}]` headers) so the returned string strictly respects `MaxWebSearchTotalInlineChars`.
   - Append a clear actionable footer at the end of the truncated response:
     > `[Full untruncated web search results ({totalChars:N0} chars across {count} items) saved to '{virtualTmpPath}'. Use 'read' (with offset/limit) or 'bash' (e.g. grep/rg) on '{virtualTmpPath}' to inspect specific sections without flooding context.]`
   - Update the `web_search` tool description in [`CanonicalAIFunctionFactory`](../../src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs#L107-L113) to note that large results are summarized inline and saved in full under `/tmp/web-search-*.md`.

---

### 2.2 Timeout Classification, Single Retry After Timeout & 30-Minute Outage Window

1. **Extend `LlmRetryPolicyOptions` Outage Window to 30 Minutes & Add `MaxTimeoutRetries = 1`:**
   - In [`RetryPolicyOptions`](../../src/NightCodeForge.Core/Resilience/RetryPolicyOptions.cs):
     - Add `int MaxTimeoutRetries { get; set; } = 0;` (default `0` on base `RetryPolicyOptions` so non-LLM clients fail fast on request timeout).
     - Add `TimeSpan? PerAttemptTimeout { get; set; }` (defaults to `null`; optional per-attempt timeout enforced inside `TransientHttpErrorRetryHandler.SendAsync` when configured by LLM chat client factories so individual attempt timeouts do not cancel the outer `HttpClient` token across retries).
   - In [`LlmRetryPolicyOptions`](../../src/NightCodeForge.Core/Resilience/LlmRetryPolicyOptions.cs):
     - Increase `DefaultLlmMaxRetryDurationSeconds` from `300.0` (5 minutes) to **`1800.0` (30 minutes)**.
     - Set `DefaultLlmMaxTimeoutRetries = 1` and initialize `MaxTimeoutRetries = DefaultLlmMaxTimeoutRetries` (`1`). Leave `PerAttemptTimeout = null` by default on `LlmRetryPolicyOptions` so unit tests using `AutoAdvancingFakeTimeProvider` without per-attempt timeouts are unaffected.
   - Copy `MaxTimeoutRetries` and `PerAttemptTimeout` in [`LlmChatClientFactoryBase.CreateEffectiveRetryOptions`](../../src/NightCodeForge.Agents/Inference/LlmChatClientFactories.cs#L108-L127).

2. **Per-Attempt Timeout Enforcement & Single Timeout Retry in `TransientHttpErrorRetryHandler`:**
   - In [`OllamaChatClientFactory`](../../src/NightCodeForge.Agents/Inference/OllamaChatClientFactory.cs) and [`OpenAiCompatibleChatClientFactory`](../../src/NightCodeForge.Agents/Inference/OpenAiCompatibleChatClientFactory.cs):
     - Configure `retryOptions.PerAttemptTimeout ??= TimeSpan.FromMinutes(Math.Max(1, options.RequestTimeoutMinutes))` (default **15 minutes**) on the `RetryPolicyOptions` passed to `TransientHttpErrorRetryHandler`.
     - Ensure the outer `HttpClient.Timeout` does not prematurely abort `TransientHttpErrorRetryHandler` before the per-attempt timeout + retry window can execute (e.g., set `HttpClient.Timeout = Timeout.InfiniteTimeSpan` when `retryOptions.PerAttemptTimeout` is set, or set `HttpClient.Timeout` to at least `PerAttemptTimeout * (MaxTimeoutRetries + 1) + MaxRetryDuration`).
   - In [`TransientHttpErrorRetryHandler.SendAsync`](../../src/NightCodeForge.Core/Resilience/TransientHttpErrorRetryHandler.cs):
     - When `_options.PerAttemptTimeout` is positive and finite, create a per-attempt linked `CancellationTokenSource` (using `new CancellationTokenSource(_options.PerAttemptTimeout.Value, _timeProvider)` so unit tests with a controllable `TimeProvider` remain deterministic and instant) that cancels `base.SendAsync(currentRequest, attemptToken)` if a single attempt exceeds `PerAttemptTimeout`. Dispose the per-attempt `CancellationTokenSource` in a `finally` block around `base.SendAsync` (matching `HttpClient.SendAsync`).
     - Distinguish between:
       1. **Caller cancellation:** `cancellationToken.IsCancellationRequested` is `true` $\to$ rethrow `OperationCanceledException` immediately with zero retries.
       2. **Operations pause:** `_operationsControlService?.IsPaused == true` $\to$ throw `OperationsPausedException` immediately with zero retries.
       3. **Request timeout:** Either the per-attempt timeout CTS expired while `!cancellationToken.IsCancellationRequested`, or `base.SendAsync` threw an exception recognized by `TransientErrorDetector.IsHttpTimeout(ex)` while `!cancellationToken.IsCancellationRequested`.
     - Track `int timeoutRetries = 0` separately from general transient network/status `attempt`:
       - When a request timeout occurs, permit a retry **only** if `timeoutRetries < _options.MaxTimeoutRetries` **and** `attempt <= maxRetries` **and** `elapsed < maxDuration`.
       - Increment `timeoutRetries` on timeout, invoke `NotifyRetryAsync` with a clear timeout message (e.g., `"LLM request timed out after 900s"`), wait the backoff delay, and execute the single retry attempt.
       - If the timeout retry budget is exhausted (`timeoutRetries >= _options.MaxTimeoutRetries` or `attempt > maxRetries` or `elapsed >= maxDuration`), throw a `TimeoutException` (or the timeout exception tagged with `ResilienceConstants.RetriedKey = true`) with a descriptive message stating the per-attempt timeout duration and total attempts tried, so outer layers do not retry again and never mistake it for a user cancellation.

3. **Distinguish HTTP Request Timeouts from Caller Cancellations & TCP Connect Errors in `TransientErrorDetector`:**
   - In [`TransientErrorDetector`](../../src/NightCodeForge.Core/Resilience/TransientErrorDetector.cs):
     - Add `public static bool IsHttpTimeout(Exception? ex)`:
       - Returns `true` when the exception chain contains a `TimeoutException` (including the standard .NET `HttpClient.Timeout` pattern: `TaskCanceledException` / `OperationCanceledException` $\to$ `TimeoutException`), and is **not** an `OperationsPausedException` and **not** a pre-connection socket/DNS failure (`HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError or HttpRequestError.NameResolutionError }` or `SocketException`). Keeping 10-second TCP connect timeouts (`SocketsHttpHandler.ConnectTimeout` / `SocketError.TimedOut`) classified as general connection errors preserves their full 20-retry outage budget during server reboots.
     - Update `HasNonRetryableMarkerOrCancellation(Exception? ex)`:
       - Keep returning `true` if `current.Data.Contains(ResilienceConstants.RetriedKey)` or `current is OperationsPausedException`.
       - When `current is OperationCanceledException or TaskCanceledException`, return `false` if `IsHttpTimeout(current)` is `true` (so an unretried `HttpClient.Timeout` is recognized as a timeout rather than a caller cancellation), and `true` otherwise.
     - Update `IsTransient(Exception? ex)`:
       - Return `true` when `IsHttpTimeout(ex)` is `true` (provided `HasNonRetryableMarkerOrCancellation(ex)` is `false`).
   - In [`CrucibleAgentHarness`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs):
     - In the outer streaming and non-streaming retry loops, also cap mid-stream timeout retries at `_retryOptions.MaxTimeoutRetries` (`1`) when `TransientErrorDetector.IsHttpTimeout(ex)` is `true`.

---

### 2.3 Improved Timeout Error Formatting & Cancellation Guardrails

1. **Actionable Timeout Message in `HttpErrorDetailHelper.FormatLlmInvocationError`:**
   - In [`HttpErrorDetailHelper.FormatLlmInvocationError`](../../src/NightCodeForge.Core/Resilience/HttpErrorDetailHelper.cs#L112-L161):
     - Detect when `ex` represents an HTTP/LLM timeout (`TransientErrorDetector.IsHttpTimeout(ex)`).
     - Instead of returning `.NET`'s confusing `"The request was canceled due to the configured HttpClient.Timeout of 900 seconds elapsing. (Inner: The operation was canceled.)"`, format a clear, human-friendly error message:
       - Extract the configured timeout duration if present (or use the `TimeoutException` message), strip redundant `"The operation was canceled."` inner boilerplate, and clearly state that the **LLM inference request timed out** (and whether a retry was attempted), e.g.:
         > `"LLM inference request timed out after 900 seconds (retried once; 2 attempts exhausted). The prompt context may be too large or the inference server is overloaded."`

2. **Prevent False `"Stage cancelled"` Classification on Timeouts:**
   - **In [`CrucibleAgentHarness.RunStageAsync`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs#L673) (both streaming line 673 and non-streaming line 815):**
     - Guard `catch (OperationCanceledException)` so that when `capturedInterruption == null && capturedRequestChanges == null`, it only treats the exception as a stage cancellation or pause when `interruptionCts.IsCancellationRequested || cancellationToken.IsCancellationRequested || _cancellationManager.IsPauseRequested(context.ForgeTask.Id) || _operationsControlService.IsPaused`.
     - If an `OperationCanceledException` / `TaskCanceledException` was NOT triggered by caller cancellation, interruption, or pause (e.g., an inner timeout), let it be handled by the transient retry / failure handlers (`FailAsync(HttpErrorDetailHelper.FormatLlmInvocationError(ex))`), recording `AgentRunStatus.Failed` instead of `AgentRunStatus.Cancelled`.
   - **In [`TaskOrchestrator.ProcessTaskAsync`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs#L730):**
     - Guard `catch (OperationCanceledException ocex)` with `when (cancellationToken.IsCancellationRequested || _lifecycleMutationCoordinator.IsCancelRequested(task.Id) || _lifecycleMutationCoordinator.IsPauseRequested(task.Id) || _operationsControlService.IsPaused)`.
     - Any uncancelled `OperationCanceledException` (such as an unhandled timeout from a stage) must fall through to `catch (Exception ex)` and produce `StageExecutionResult.Failure(HttpErrorDetailHelper.FormatLlmInvocationError(ex))` so the stage fails cleanly with the timeout error rather than logging `"Stage execution was cancelled."` and stranding the task.
   - **In [`TelemetryChatClient`](../../src/NightCodeForge.Agents/Inference/TelemetryChatClient.cs#L153) (both `GetResponseAsync` line 153 and `GetStreamingResponseAsync` line 236):**
     - Set `Status = (ex is OperationCanceledException && !TransientErrorDetector.IsHttpTimeout(ex) && cancellationToken.IsCancellationRequested) ? LlmCallStatus.Cancelled : LlmCallStatus.Failed`, so timed-out LLM calls are recorded as `Failed` (not `"Cancelled"`) in live telemetry and logs.
   - **In [`IdeationService`](../../src/NightCodeForge.Agents/Ideation/IdeationService.cs#L441) (both streaming line 443 and non-streaming fallback line 521):**
     - Format errors via `HttpErrorDetailHelper.FormatLlmInvocationError(ex)` when broadcasting `AgentActivityKind.Error`.

---

## 3. Verification & Test Strategy

1. **`CanonicalTools` `web_search` Capping & `/tmp` Offload Tests ([`tests/NightCodeForge.Agents.Tests`](../../tests/NightCodeForge.Agents.Tests)):**
   - **Small Results (No Offload):** Verify that when `IWebSearchService` returns compact results within `MaxWebSearchResultInlineChars` and `MaxWebSearchTotalInlineChars`, `WebSearchAsync` returns the full text inline and does not write a `/tmp` file.
   - **Massive Results (Capped Inline + `/tmp` Offload, Including Read-Only Mode):** Verify that when `IWebSearchService` returns multi-tens-of-kilobytes results (e.g. 8 items $\times$ 25,000 chars = 200,000 chars), even when `CanonicalToolScope.IsReadOnly = true`:
     - The string returned by `WebSearchAsync` is strictly $\le$ `MaxWebSearchTotalInlineChars` characters.
     - Each inline item snippet is capped and includes the `/tmp/web-search-*.md` path and 1-indexed line range.
     - The full untruncated content is written to the resolved `/tmp/web-search-*.md` file and can be read back via `CanonicalTools.ReadAsync("/tmp/web-search-...", offset, limit)`.

2. **`TransientErrorDetector` & `HttpErrorDetailHelper` Timeout Tests ([`tests/NightCodeForge.Core.Tests`](../../tests/NightCodeForge.Core.Tests)):**
   - Update [`TransientErrorDetectorTests.IsTransient_ReturnsFalse_ForTimeoutAndCancellationExceptions`](../../tests/NightCodeForge.Core.Tests/TransientErrorDetectorTests.cs#L150-L156) and add dedicated tests verifying `TransientErrorDetector.IsHttpTimeout` and `IsTransient` return `true` for `TimeoutException` and for `TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 900 seconds elapsing.", new TimeoutException("The operation was canceled."))`.
   - Verify `TransientErrorDetector.IsHttpTimeout` returns `false` (while `IsTransient` returns `true`) for `SocketException(SocketError.TimedOut)` and `HttpRequestException(HttpRequestError.ConnectionError, ..., new TimeoutException())` so TCP connect timeouts keep their full outage retry budget.
   - Verify `TransientErrorDetector.IsTransient` returns `false` for caller `OperationCanceledException` / `TaskCanceledException` (without an inner `TimeoutException`) and for timeout exceptions already marked with `ResilienceConstants.RetriedKey`.
   - Verify `HttpErrorDetailHelper.FormatLlmInvocationError` formats `HttpClient.Timeout` and `TimeoutException` instances into a clear timeout message without `"The request was canceled"` or `"(Inner: The operation was canceled.)"`.

3. **`TransientHttpErrorRetryHandler` Single-Timeout-Retry & 30-Minute Window Tests ([`tests/NightCodeForge.Core.Tests`](../../tests/NightCodeForge.Core.Tests)):**
   - Verify `LlmRetryPolicyOptions` defaults to `MaxRetryDuration = 30 minutes` (`1800s`) and `MaxTimeoutRetries = 1`.
   - Using a controllable fake `TimeProvider` (note: advance the timer from inside the mock handler's `SendAsync` when simulating an attempt timeout rather than inside `CreateTimer` so healthy attempts are not cancelled before `SendAsync` executes), verify that when Attempt 1 times out after 15 minutes (`PerAttemptTimeout = 15m`), `TransientHttpErrorRetryHandler` retries **once** (Attempt 2) and succeeds when Attempt 2 returns `200 OK`.
   - Verify that when both Attempt 1 (15m) and Attempt 2 (15m) time out, `TransientHttpErrorRetryHandler` stops after **2 total attempts** (1 retry), marks the exception with `ResilienceConstants.RetriedKey`, and does **not** perform a 3rd attempt even though `MaxRetries = 20`.
   - Verify that when the caller's `CancellationToken` is cancelled, `TransientHttpErrorRetryHandler` aborts immediately on Attempt 1 with zero retries.

4. **`CrucibleAgentHarness` & `TaskOrchestrator` Timeout vs. Cancellation Tests ([`tests/NightCodeForge.Agents.Tests`](../../tests/NightCodeForge.Agents.Tests), [`tests/NightCodeForge.Core.Tests`](../../tests/NightCodeForge.Core.Tests)):**
   - Verify that when the LLM client throws an exhausted `HttpClient.Timeout` (`TaskCanceledException` wrapping `TimeoutException` with `RetriedKey`) while `CancellationToken.None` is active, `CrucibleAgentHarness.RunStageAsync` returns `AgentRunStatus.Failed` (not `Cancelled`) with the formatted timeout error message, and `TaskOrchestrator` marks the stage as `Failed` rather than `"Stage execution was cancelled."`.

---

## 4. Definition of Done (DoD) Checklist

- [x] Add `MaxWebSearchResultInlineChars` (`3_000`) and `MaxWebSearchTotalInlineChars` (`12_000`) constants and update `CanonicalTools.WebSearchAsync` to cap inline results and offload full untruncated search results to `/tmp/web-search-*.md` with 1-indexed line ranges readable via `read` and `bash`.
- [x] Update `LlmRetryPolicyOptions` defaults to `MaxRetryDuration = 30 minutes` (`1800s`) and `MaxTimeoutRetries = 1`, and add `MaxTimeoutRetries` and `PerAttemptTimeout` to `RetryPolicyOptions`.
- [x] Update `TransientHttpErrorRetryHandler`, `OllamaChatClientFactory`, and `OpenAiCompatibleChatClientFactory` so per-attempt timeouts (15m default) are enforced per attempt and retried at most once (`MaxTimeoutRetries = 1`) within the 30-minute retry window.
- [x] Update `TransientErrorDetector` (`IsHttpTimeout`, `IsTransient`, `HasNonRetryableMarkerOrCancellation`) to distinguish `HttpClient.Timeout` (`TaskCanceledException` $\to$ `TimeoutException`) from caller cancellation.
- [x] Update `HttpErrorDetailHelper.FormatLlmInvocationError`, `TelemetryChatClient`, `CrucibleAgentHarness`, `IdeationService`, and `TaskOrchestrator` so LLM request timeouts are reported as `Failed` with a clear timeout diagnostic rather than `Cancelled` (`"Stage cancelled"`).
- [x] Add unit tests in `NightCodeForge.Core.Tests` and `NightCodeForge.Agents.Tests` covering `web_search` capping and `/tmp` offload, single-retry-on-timeout behavior, 30-minute retry window, and timeout error reporting; verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

