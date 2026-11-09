# 🚀 Step 12.33A: OllamaRouter Session Affinity Lease Release & Session Cost Tracking Engine

> **Feature Specification (Part 1 of 2 — Backend Engine, Session Lifecycle, Gateway API & Delta Accounting)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisites:* [Step 11.33: Unified LLM Provider & Fleet Integration](../archive/phase-11/STEP_11_33_UNIFIED_LLM_PROVIDER_DYNAMIC_CONTEXT_AND_FLEET_INTEGRATION.md), [Step 12.29: Smart Router Model Preload & Context-Intent Warmup](../archive/phase-12/STEP_12_29_SMART_ROUTER_MODEL_PRELOAD_AND_CONTEXT_INTENT.md)  
> *Target Components:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Agents`, `tests/NightCodeForge.Infrastructure.Tests`, `tests/NightCodeForge.Agents.Tests`  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *OpenAPI Specification:* OllamaRouter OpenAPI 3.1.1 (`/api/router/sessions/affinity/release`, `/api/router/monitor/sessions/{sessionId}`)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Summary & Problem Statement

### 1.1 Sticky Routing Affinity Without Explicit Release
In NightCodeForge, multi-turn LLM workflows (Crucible task stages and Ideation discovery chats) propagate a session identifier via the `X-OllamaRouter-Session-Id` header (e.g. `ncf-task{taskId}-{stageId}` or `ideation-{workspaceId}-{guid}`) to pin inference turns to the same target node across consecutive turns. This preserves `llama.cpp` prompt prefix KV-cache evaluation, avoiding 10–30 second re-evaluation penalties.

However, once a stage or conversation finishes (whether successfully, via error, cancellation, or yielding for human input), the target node retains that session's affinity lease until its default sliding TTL (30 minutes) expires. During multi-task concurrent execution, stale affinity leases needlessly constrain OllamaRouter's capability-aware placement engine and KV-cache lease allocations.

OllamaRouter (Task #86) introduced an explicit lease release endpoint:
- **`POST /api/router/sessions/affinity/release`** with request header `X-OllamaRouter-Session-Id: <sessionId>`:
  Releases the sticky-routing lease immediately and idempotently (HTTP 200 `{ "sessionId": ..., "released": bool }`).

### 1.2 Lack of Task Cost & Real Spend Attribution
Users currently have no visibility into the actual monetary or electricity cost of running a task. OllamaRouter (Tasks #84, #85, #87) now computes real-time session cost and savings via:
- **`GET /api/router/monitor/sessions/{sessionId}`**:
  Returns accumulated token counters (`inputTokens`, `cachedInputTokens`, `outputTokens`, `requests`, `elapsedMs`), local electricity cost (`electricityCost`), actual cloud spend (`actualCloudCost`), total actual cost (`cost = round(actualCloudCost + electricityCost, 4)`), cloud baseline value (`cloudValue`), savings vs all-cloud baseline (`savings`), currency symbol (`currency`), and per-target breakdown (`perTarget`).

Because OllamaRouter maintains a monotonically accumulating session store keyed by `sessionId` (and stage retries reuse `LlmSessionIds.ForStage(taskId, stageId)` for KV-cache stickiness), querying the session endpoint only at the end of a stage would double-count tokens and cost across retries and resumed turns.

### 1.3 Key Objectives of Step 12.33A
1. **Gateway Client Integration:**
   Extend `ILlmGatewayService` (defined in `src/NightCodeForge.Core/Interfaces/IOllamaRouterGatewayService.cs`) and `OllamaRouterGatewayService` (with polymorphic no-op fallback in `OllamaGatewayServiceBase` and `OpenAiCompatibleGatewayService`) to support:
   - Releasing session affinity: `POST /api/router/sessions/affinity/release` using a per-request `X-OllamaRouter-Session-Id` header.
   - Querying session usage and cost: `GET /api/router/monitor/sessions/{sessionId}`.
2. **Delta-Based Session Cost Accounting (Double-Count Prevention):**
   - At conversation / stage run startup (`BeginAsync`), capture a baseline snapshot $S_{\text{start}}$ (if 404 or unconfigured, baseline is zero).
   - At conversation / stage run conclusion (completion, cancellation, failure, or wait-for-human), capture a final snapshot $S_{\text{final}}$.
   - Compute the exact delta: $\Delta = S_{\text{final}} - S_{\text{start}}$, preserving `null` on cost fields when router pricing is unconfigured (`S_final.Cost is null`).
3. **Session Affinity Lease Release at Conversation End:**
   - Immediately following final cost snapshot capture, call `POST /api/router/sessions/affinity/release` to free the target node lease.
4. **Domain & Persistence Model Updates:**
   - Store session cost and token metrics (`Cost`, `ActualCloudCost`, `ElectricityCost`, `CloudSavings`, `Currency`, `CachedInputTokens`) on `StageExecutionRecord` (per-run delta via `AgentRunMetrics` $\rightarrow$ `TaskOrchestrator`) and `Conversation` (cumulative across resumptions and turns in both `EfCoreTaskRepository` and `InMemoryTaskRepository`).
   - Add EF Core database migration and pre-migration baseline check in `AppDbContext`.
5. **Lifecycle Wiring:**
   - Integrate into `CrucibleConversationCoordinator` and `CrucibleAgentHarness` (without exceeding the 10-parameter constructor limit on `CrucibleAgentHarness`).
   - Integrate into `IdeationService` and `Ideation.razor` (using a stable per-conversation session ID `LlmSessionIds.ForIdeation` and releasing affinity on session archive/switch/cancel/dispose).

---

## 2. OllamaRouter API Specifications

### 2.1 Affinity Lease Release (`POST /api/router/sessions/affinity/release`)

- **Method & Route:** `POST /api/router/sessions/affinity/release`
- **Request Headers:**
  - `X-OllamaRouter-Session-Id: <sessionId>` (Required, max length 128 characters, whitespace-trimmed).
- **Request Body:** Empty.
- **Responses:**
  - `200 OK`:
    ```json
    {
      "sessionId": "ncf-task42-stage-3-implementation",
      "released": true
    }
    ```
    *Semantics:* Always 200 OK for valid headers. `released: true` indicates a live lease was found and evicted; `released: false` indicates the session had no lease, had already expired/been released, or exceeded max length.
  - `400 Bad Request`:
    ```json
    {
      "error": "X-OllamaRouter-Session-Id header is required"
    }
    ```
    Returned if the header is missing, empty, or whitespace-only.
- **Provider Semantics:** Public endpoint under default router mode. If the active LLM provider is Standalone Ollama or OpenAI-Compatible, release requests are gracefully skipped (no-op).

### 2.2 Session Usage & Cost Breakdown (`GET /api/router/monitor/sessions/{sessionId}`)

- **Method & Route:** `GET /api/router/monitor/sessions/{sessionId}` (URL-encoded `sessionId`).
- **Responses:**
  - `200 OK`:
    ```json
    {
      "currency": "$",
      "sessionId": "ncf-task42-stage-3-implementation",
      "requests": 8,
      "inputTokens": 34500,
      "cachedInputTokens": 26000,
      "outputTokens": 4120,
      "elapsedMs": 28430.2,
      "lastSeenAt": "2026-11-09T20:45:12.345Z",
      "cloudValue": 0.0890,
      "electricityCost": 0.0052,
      "savings": 0.0838,
      "actualCloudCost": 0.0000,
      "cost": 0.0052,
      "perTarget": [
        {
          "target": "aiserver1",
          "requests": 8,
          "inputTokens": 34500,
          "cachedInputTokens": 26000,
          "outputTokens": 4120,
          "elapsedMs": 28430.2,
          "cloudValue": 0.0890,
          "electricityCost": 0.0052,
          "savings": 0.0838
        }
      ]
    }
    ```
  - `404 Not Found`:
    ```json
    {
      "error": "session not found: ncf-task42-stage-3-implementation"
    }
    ```
    *Semantics:* Indicates no request has been processed under this session ID since router startup or LRU eviction. Treat as zero usage.
- **Null Cost Fields:** When router pricing is not configured, `currency`, `cloudValue`, `electricityCost`, `savings`, `actualCloudCost`, and `cost` are null/omitted. Token counters (`requests`, `inputTokens`, `cachedInputTokens`, `outputTokens`, `elapsedMs`) remain valid and populated.

---

## 3. Delta Accounting & Lifecycle Integration

### 3.1 The Double-Counting Problem
1. **Stable Stage Session IDs:** In Crucible, `LlmSessionIds.ForStage(taskId, stageId)` generates `ncf-task{taskId}-{stageId}`. If a stage fails or is interrupted and subsequently retried (attempt 2), the same session ID is sent to OllamaRouter so prompt prefix KV-cache can be reused on the same node.
2. **Cumulative Store:** OllamaRouter's LRU monitor accumulates counters across all requests matching `sessionId`. If attempt 1 used 10,000 tokens ($0.01) and attempt 2 used an additional 12,000 tokens ($0.012), OllamaRouter reports total usage of 22,000 tokens ($0.022) at the end of attempt 2.
3. **Resumed Turns:** Similarly, if a stage pauses with `WaitingForHuman` and resumes when the user answers, multiple conversation executions share the session ID.

### 3.2 Delta Resolution Algorithm
For any conversation lifecycle session with identifier `sessionId`:
1. **Start of Conversation / Turn (`BeginAsync` / Turn Start):**
   - Query `GetSessionUsageAsync(sessionId)`.
   - If found (200 OK), record snapshot as $S_{\text{start}}$.
   - If not found (404) or provider has no session monitoring, $S_{\text{start}} = \text{ZeroSnapshot}$.
2. **End of Conversation / Turn (Success, Failure, Interruption, or `WaitingForHuman`):**
   - Query `GetSessionUsageAsync(sessionId)` to obtain $S_{\text{final}}$ (using a short non-cancelled timeout token if the parent `CancellationToken` was already cancelled).
   - **Null-Preserving Cost & Token Deltas:**
     - If $S_{\text{final}}$ is `null` (404 / non-router provider), the session usage delta is `null` (or zero tokens and `null` costs).
     - If $S_{\text{final}}$ is non-null, compute token deltas:
       $$\Delta \text{InputTokens} = \max(0, S_{\text{final}}.\text{InputTokens} - S_{\text{start}}.\text{InputTokens})$$
       $$\Delta \text{OutputTokens} = \max(0, S_{\text{final}}.\text{OutputTokens} - S_{\text{start}}.\text{OutputTokens})$$
       $$\Delta \text{CachedInputTokens} = \max(0, S_{\text{final}}.\text{CachedInputTokens} - S_{\text{start}}.\text{CachedInputTokens})$$
     - **Important — Unconfigured Router Pricing:** When router pricing is not configured, $S_{\text{final}}.\text{Cost}$ is `null`. In that case, $\Delta \text{Cost}$, $\Delta \text{ActualCloudCost}$, $\Delta \text{ElectricityCost}$, and $\Delta \text{CloudSavings}$ **must remain `null`** (never coerced to `0.0`), so Step 12.33B's `HasCostData` accurately detects unpriced sessions and hides `$0.00` badges. When $S_{\text{final}}.\text{Cost}$ is non-null:
       $$\Delta \text{Cost} = \max(0, S_{\text{final}}.\text{Cost.Value} - (S_{\text{start}}.\text{Cost} ?? 0))$$
       $$\Delta \text{ActualCloudCost} = \max(0, (S_{\text{final}}.\text{ActualCloudCost} ?? 0) - (S_{\text{start}}.\text{ActualCloudCost} ?? 0))$$
       $$\Delta \text{ElectricityCost} = \max(0, (S_{\text{final}}.\text{ElectricityCost} ?? 0) - (S_{\text{start}}.\text{ElectricityCost} ?? 0))$$
       $$\Delta \text{CloudSavings} = \max(0, (S_{\text{final}}.\text{Savings} ?? 0) - (S_{\text{start}}.\text{Savings} ?? 0))$$
   - **Per-Run `StageExecutionRecord` vs. Cumulative `Conversation` Persistence:**
     - **`StageExecutionRecord` (Per-Run Delta):** Each stage execution attempt or resumed run creates a distinct `StageExecutionRecord` row in `TaskOrchestrator.ProcessTaskAsync`. Therefore, each `StageExecutionRecord` stores the **delta ($\Delta$)** of that single run via `AgentRunMetrics` $\rightarrow$ `TaskOrchestrator.MapAgentRunResultToStageResult`, ensuring `task.ExecutionRecords.Sum(...)` in Step 12.33B never double-counts across retries or human-question resumptions.
     - **`Conversation` (Cumulative Across Resumptions & Multi-Turn Ideation):** Because `CrucibleConversationCoordinator.BeginAsync` reopens an existing `Conversation` row when resuming from `WaitingForHuman` or `Interrupted`, and an Ideation `Conversation` spans multiple interactive user turns on the same `Conversation` row, `ITaskRepository` (`CompleteConversationAsync` / `UpdateConversationUsageAsync`) **accumulates (`+= Δ`)** `Cost`, `ActualCloudCost`, `ElectricityCost`, `CloudSavings`, `InputTokens`, `OutputTokens`, and `CachedInputTokens` onto the `Conversation` entity.
3. **Affinity Lease Release (Including `WaitingForHuman` / Pause):**
   - Execute `ReleaseSessionAffinityAsync(sessionId)` (using a short non-cancelled timeout token if the parent token is cancelled).
   - **Crucial Rule on Pause (`WaitingForHuman`):** When a Crucible stage pauses to ask a question to a human, the session lease is **released immediately**. While waiting for human answers (which can take minutes or hours), target nodes in the fleet must not remain artificially locked or penalized by an idle lease. When the user eventually answers and execution resumes, the subsequent request dynamically routes and re-pins fresh affinity.
   - Wrap in `try/catch` with warning logging so a lease release failure never crashes the stage result or interrupts transcript persistence.

---

## 4. Architectural & Implementation Requirements

### 4.1 Gateway Contracts, Constants & DTOs
1. **Route Constants (`src/NightCodeForge.Infrastructure/Inference/LlmEndpointOptions.cs`):**
   - In `OllamaApiPaths`, add relative route constants (matching existing no-leading-slash convention):
     - `public const string RouterSessionAffinityRelease = "api/router/sessions/affinity/release";`
     - `public const string RouterMonitorSessionsPrefix = "api/router/monitor/sessions/";`
2. **Gateway Contracts (`src/NightCodeForge.Core/Interfaces/IOllamaRouterGatewayService.cs`):**
   - Add record types:
     - `RouterSessionUsage`:
       - `string SessionId`
       - `int Requests`
       - `int InputTokens`
       - `int CachedInputTokens`
       - `int OutputTokens`
       - `double ElapsedMs`
       - `DateTimeOffset? LastSeenAt`
       - `double? CloudValue`
       - `double? ElectricityCost`
       - `double? Savings`
       - `double? ActualCloudCost`
       - `double? Cost`
       - `string? Currency`
       - `IReadOnlyList<RouterSessionUsageTargetItem>? PerTarget`
     - `RouterSessionUsageTargetItem(string Target, int Requests, int InputTokens, int CachedInputTokens, int OutputTokens, double ElapsedMs, double? CloudValue, double? ElectricityCost, double? Savings)`
     - `RouterSessionAffinityReleaseResult(string SessionId, bool Released)`
     - `SessionUsageDelta(int InputTokens, int OutputTokens, int CachedInputTokens, double? Cost, double? ActualCloudCost, double? ElectricityCost, double? CloudSavings, string? Currency)`
   - Extend `ILlmGatewayService` (implemented by `OllamaRouterGatewayService`, `OllamaGatewayServiceBase`, and `OpenAiCompatibleGatewayService`):
     - `Task<RouterSessionUsage?> GetSessionUsageAsync(string sessionId, CancellationToken cancellationToken = default);`
     - `Task<bool> ReleaseSessionAffinityAsync(string sessionId, CancellationToken cancellationToken = default);`
   - In `OllamaRouterGatewayService.ReleaseSessionAffinityAsync`:
     - Construct a per-request `HttpRequestMessage(HttpMethod.Post, OllamaApiPaths.RouterSessionAffinityRelease)` and attach `request.Headers.TryAddWithoutValidation(OllamaRoutingHeaders.SessionId, sessionId.Trim())` (never mutate shared `Http.DefaultRequestHeaders`).

### 4.2 Domain Entity, Metrics & Repository Updates
1. **`StageExecutionRecord` (`src/NightCodeForge.Core/Domain/StageExecutionRecord.cs`):**
   - `double? Cost { get; set; }` (Total actual cost in currency units: cloud spend + local electricity)
   - `double? ActualCloudCost { get; set; }`
   - `double? ElectricityCost { get; set; }`
   - `double? CloudSavings { get; set; }`
   - `string? Currency { get; set; }`
   - `int? CachedInputTokens { get; set; }`
2. **`Conversation` (`src/NightCodeForge.Core/Domain/Conversation.cs`):**
   - `double? Cost { get; set; }`
   - `double? ActualCloudCost { get; set; }`
   - `double? ElectricityCost { get; set; }`
   - `double? CloudSavings { get; set; }`
   - `string? Currency { get; set; }`
   - `int? InputTokens { get; set; }`
   - `int? OutputTokens { get; set; }`
   - `int? CachedInputTokens { get; set; }`
3. **`AgentRunMetrics` (`src/NightCodeForge.Core/Domain/AgentRunResult.cs`) & `TaskOrchestrator.MapAgentRunResultToStageResult` (`src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs`):**
   - Extend `AgentRunMetrics` with optional delta fields (`int? CachedInputTokens = null`, `double? Cost = null`, `double? ActualCloudCost = null`, `double? ElectricityCost = null`, `double? CloudSavings = null`, `string? Currency = null`).
   - When router session token deltas ($\Delta \text{InputTokens} > 0$ or $\Delta \text{OutputTokens} > 0$) are available and the underlying SDK reported `0` tokens, populate `InputTokens` / `OutputTokens` from the router delta as well.
   - Map `runResult.Metrics` cost and cached token fields onto `StageExecutionRecord` in `TaskOrchestrator.MapAgentRunResultToStageResult`.
4. **Repository Parity (`ITaskRepository`, `EfCoreTaskRepository`, `InMemoryTaskRepository`):**
   - Update `CompleteConversationAsync` (and/or add `UpdateConversationUsageAsync(int conversationId, SessionUsageDelta usageDelta, CancellationToken cancellationToken = default)`) in both `EfCoreTaskRepository` and `InMemoryTaskRepository` to accumulate (`+=`) non-null cost and token deltas onto `Conversation`.
5. **EF Core Database Migration & Pre-Migration Baselining (`src/NightCodeForge.Infrastructure/Data/AppDbContext.cs`):**
   - Add an EF Core migration for `AppDbContext` adding the nullable cost and token columns to `StageExecutionRecords` and `Conversations`.
   - Register the new migration ID constant and column check in `AppDbContext.BaselineExistingPreMigrationDatabaseAsync` so existing SQLite databases created before this migration upgrade seamlessly.

### 4.3 Lifecycle Wiring & Constructor Constraints
1. **Crucible Stage Lifecycle (`CrucibleConversationCoordinator` & `CrucibleAgentHarness`):**
   - **Constructor Parameter Ceiling (`AGENTS.md` $\le 10$ rule):** `CrucibleAgentHarness` already has **10 constructor parameters** (the maximum allowed), whereas `CrucibleConversationCoordinator` has **3 constructor parameters**. Inject `ILlmGatewayService` into `CrucibleConversationCoordinator` (bringing it to 4 parameters) rather than adding a dependency to `CrucibleAgentHarness`.
   - In `CrucibleConversationCoordinator.BeginAsync`:
     - Derive `sessionId = LlmSessionIds.ForStage(taskId, stageId)`, query `GetSessionUsageAsync(sessionId)` to capture $S_{\text{start}}$, and store both `SessionId` and `BaselineUsage` on `CrucibleConversationState`.
   - In `CrucibleConversationCoordinator.SaveTranscriptAsync`:
     - Note that every exit path in `CrucibleAgentHarness` (`Completed`, `RequestChanges`, `WaitingForHuman`, `Failed`, `AbortOnCancellationAsync`, and `SuspendOnPauseAsync`) already invokes `SaveAsync` $\rightarrow$ `SaveTranscriptAsync`.
     - Inside `SaveTranscriptAsync`, query `GetSessionUsageAsync(state.SessionId)` to compute `SessionUsageDelta`, store `state.LastUsageDelta`, accumulate usage onto the `Conversation` entity, and call `ReleaseSessionAffinityAsync(state.SessionId)`.
     - `CrucibleAgentHarness.CurrentMetrics(...)` reads `conversationState.LastUsageDelta` so `AgentRunResult.Metrics` carries the computed cost/token delta into `TaskOrchestrator` and live telemetry events.
2. **Ideation Lifecycle (`LlmSessionIds`, `IdeationService`, & `Ideation.razor`):**
   - **Stable Per-Conversation Session ID:** Add `LlmSessionIds.ForIdeation(int workspaceId, int? conversationId = null)` in `src/NightCodeForge.Agents/Inference/OllamaRoutingHeaders.cs`, returning `ncf-ideation-ws{workspaceId}-conv{conversationId}` when `conversationId` is provided (falling back to `ncf-ideation-ws{workspaceId}-{Guid.NewGuid():N}` only for transient/unpersisted calls). Pass `conversationId: Session.ActiveConversationId` into `IdeationService.ConverseStreamingAsync` so all turns of an Ideation conversation reuse the same router session ID and KV-cache lease.
   - **Per-Turn Delta Capture (Without Premature Lease Release):** At the start of each `ConverseStreamingAsync` turn, capture $S_{\text{start}}$ for the ideation `sessionId`; at the end of the turn (in `finally`, using a short timeout token if cancelled), capture $S_{\text{final}}$, compute `SessionUsageDelta`, accumulate it onto the active `Conversation` via `ITaskRepository` (and attach it to `IdeationResult`), **without** releasing session affinity between active user turns so the next turn stays warm on the same node.
   - **Explicit Affinity Release When Leaving/Ending Session:** Add a method on `IIdeationService` (e.g., `ReleaseConversationSessionAsync(int workspaceId, int conversationId, CancellationToken cancellationToken = default)`) or call `ILlmGatewayService.ReleaseSessionAffinityAsync` when:
     - The user clicks "Clear Chat" (`ResetSession` in `Ideation.razor`),
     - The user switches to a different saved session (`HandleSessionSelectedAsync` in `Ideation.razor`),
     - The user cancels an active generation (`CancelGenerationAsync`), or
     - The workspace changes / `Ideation.razor` is disposed (`HandleWorkspaceChanged` / `Dispose`).
3. **Fail-Fast & Provider Agnosticism:**
   - Network timeouts during telemetry or cost inspection must log structured warnings and fall back to a null/zero delta, while unexpected deserialization breaks must never be swallowed with bare `catch { }` blocks.
   - Calling `ReleaseSessionAffinityAsync` or `GetSessionUsageAsync` when connected to standalone Ollama (`OllamaGatewayService`) or OpenAI-Compatible (`OpenAiCompatibleGatewayService`) providers returns `false` / `null` without throwing or emitting warnings.

---

## 5. Verification & Acceptance Criteria

### 5.1 Automated Unit & Integration Tests
1. **`OllamaRouterGatewayServiceTests`:**
   - Verify `ReleaseSessionAffinityAsync` issues `POST api/router/sessions/affinity/release` with a per-request `X-OllamaRouter-Session-Id` header and parses `released: true/false`.
   - Verify `GetSessionUsageAsync` issues `GET api/router/monitor/sessions/{sessionId}`, returns parsed DTO on 200, and returns `null` on 404.
   - Verify standalone `OllamaGatewayService` and `OpenAiCompatibleGatewayService` return `false` / `null` as no-ops.
2. **`SessionDeltaAccountingTests`:**
   - Test delta math with baseline zero, baseline existing (stage retry / resumed session), and unconfigured pricing (`Cost == null` preserved as `null`, never coerced to `0.0`).
   - Verify tokens and costs are clamped to $\ge 0$.
3. **`CrucibleConversationCoordinatorLifecycleTests`:**
   - Verify `BeginAsync` captures baseline $S_{\text{start}}$ for `LlmSessionIds.ForStage(taskId, stageId)`.
   - Verify `SaveTranscriptAsync` captures $S_{\text{final}}$, computes `state.LastUsageDelta`, accumulates cost/tokens onto `Conversation`, and invokes `ReleaseSessionAffinityAsync`.
   - Verify affinity release and delta capture occur across all termination paths (`Completed`, `WaitingForHuman`, `Failed`, and cancellation).
   - Verify `TaskOrchestrator` maps `AgentRunMetrics` cost fields onto `StageExecutionRecord`.
4. **`IdeationLifecycleTests`:**
   - Verify `LlmSessionIds.ForIdeation(workspaceId, conversationId)` produces a deterministic session ID across turns of the same conversation.
   - Verify each turn accumulates delta usage onto the `Conversation` without releasing affinity mid-chat, and that session reset/switch/cancel/dispose triggers `ReleaseSessionAffinityAsync`.

