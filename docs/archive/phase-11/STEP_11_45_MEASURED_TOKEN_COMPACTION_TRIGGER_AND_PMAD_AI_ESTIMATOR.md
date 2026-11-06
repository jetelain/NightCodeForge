# 🛡️ Step 11.45: Measured-Token Compaction Trigger, `Pmad.AI` Next-Turn Token Estimation & Non-Retryable Template Error Guard

> **Bug Fix & Resiliency Step (Context Overflow Prevention via Server-Measured Token Baselines & Model-Calibrated Delta Estimation)**  
> *Parent Plan:* [Phase 11 Master Plan](../11_EXTEND_FEATURES.md)  
> *Target Framework:* .NET 10 (C# 13) | *Status:* Completed  
> *Target Projects:* [`src/NightCodeForge.Core`](../../../src/NightCodeForge.Core), [`src/NightCodeForge.Agents`](../../../src/NightCodeForge.Agents), [`tests/NightCodeForge.Core.Tests`](../../../tests/NightCodeForge.Core.Tests), [`tests/NightCodeForge.Agents.Tests`](../../../tests/NightCodeForge.Agents.Tests)  
> *Dependencies:* [Step 11.43A: `Pmad.AI` 0.0.2 Upgrade](STEP_11_43A_PMAD_AI_READONLY_COMMAND_VALIDATOR_MIGRATION.md), [Step 11.44: `web_search` Capping & Timeout Retry Resilience](STEP_11_44_WEB_SEARCH_RESULT_CAPPING_TMP_OFFLOAD_AND_TIMEOUT_RETRY_RESILIENCE.md)  
> *Aligns With:* [AGENTS.md](../../../AGENTS.md), [docs/PLAN.md](../../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Summary & Root Cause Analysis

### 1.1 Live Production Incident (ForgeTask #51 — `implementation` Attempt 1, `Conversation 503`)
In the live production instance (`C:\Users\Julien\AppData\Local\NightCodeForge\data`), the first `implementation` run of ForgeTask #51 (*"Generate and serve a runtime OpenAPI document for the /api/router/ management API"*, `Conversation.Id = 503`, model `Qwen3.8-27B:latest` on `Remote` / `aiserver`, transcript `2026-11-05-task-51-implementation-failed.md`) failed after 3 hours and 134 model calls with:

```text
HTTP 500 (InternalServerError): no user query found in messages
```

Cross-referencing `NightCodeForge` (`nightcodeforge-20261105.log`), `OllamaRouter` (`token-metrics.csv`), and `aiserver` (`journalctl -u ollama`) established the exact 4-part causal chain:

| # | Failure Link | Root Cause in Current Codebase | Observed Telemetry on ForgeTask #51 (`Conversation 503`) |
| :--- | :--- | :--- | :--- |
| **1** | **MAF `CompactionMessageIndex` Underestimates Code/Symbol Tokens (`UTF8ByteCount / 4`) and Ignores System/Tool Overhead** | `Microsoft.Agents.AI.Compaction.CompactionProvider` builds `CompactionMessageIndex` with `Tokenizer = null`, which estimates tokens strictly as `UTF8ByteCount / 4` across `RequestMessages` only (excluding `ChatOptions.Instructions`, `ChatOptions.Tools`, and chat-template framing). Both `CompactionStrategy.ShouldCompact` (via `CompactionTriggers.TokensExceed`) and [`PiStyleCrucibleCompactionStrategy.CompactCoreAsync`](../../../src/NightCodeForge.Agents/Compaction/PiStyleCrucibleCompactionStrategy.cs#L113-L152) gate compaction on `index.IncludedTokenCount <= TriggerThresholdTokens`. | On Call 133 (`13:00:39 UTC`, 267 messages, `1,018,498` chars, `125,325` words, `126,669` symbols), Ollama measured **`261,833` actual prompt tokens** + **`209` output tokens** = **`262,041` total tokens** in a `262,144` slot (`n_ctx_slot = 262144`). Yet `CompactionMessageIndex.IncludedTokenCount` estimated only **`~251,200` tokens** (`< TriggerThresholdTokens = 253,952`), so compaction never triggered. |
| **2** | **Server-Measured Token Counts From Previous Calls Are Discarded by Compaction** | [`OllamaSharpChatClientAdapter`](../../../src/NightCodeForge.Agents/Inference/OllamaSharpChatClientAdapter.cs#L402-L408) and [`TelemetryChatClient`](../../../src/NightCodeForge.Agents/Inference/TelemetryChatClient.cs#L268-L277) receive the exact `prompt_eval_count` (`InputTokenCount`) and `eval_count` (`OutputTokenCount`) on every completed turn, but [`PiStyleCrucibleCompactionStrategy`](../../../src/NightCodeForge.Agents/Compaction/PiStyleCrucibleCompactionStrategy.cs) never inspects the previous turn's measured usage. | Calls 123 through 133 all returned actual measured prompt token counts above `255,000` (`255,001` $\to$ `261,833`), exceeding `TriggerThresholdTokens` (`253,952`) for 11 consecutive turns without triggering compaction. |
| **3** | **Ollama Prompt Truncation Evicts the Sole `User` Message at `num_ctx = 262,144`** | In an autonomous Crucible stage run, `messages[1]` (Turn 0) is the **only** `"role": "user"` message in the conversation; all subsequent turns (`messages[2..269]`) are `"role": "assistant"` (tool calls) and `"role": "tool"` (tool results). When Call 134's prompt crossed `num_ctx = 262,144`, Ollama (`routes.go:3297`) truncated the oldest non-system message (`messages[1]`) to fit the context slot and re-invoked the `qwen3.8` renderer. | With `messages[1]` dropped, zero `"role": "user"` messages remained in the slice passed to the `qwen3.8` chat renderer, which immediately aborted with `level=ERROR source=routes.go:3297 msg="chat prompt error" error="no user query found in messages"` (`HTTP 500`). |
| **4** | **20x Retry Loop on Deterministic Prompt Error** | [`TransientHttpErrorRetryHandler`](../../../src/NightCodeForge.Core/Resilience/TransientHttpErrorRetryHandler.cs) treats all `HTTP 500` responses as transient server outages regardless of the error payload. | NightCodeForge retried the identical overflowing Call 134 payload **20 times** (`21` total requests over ~6.5 minutes, `14:00:40` $\to$ `14:07:09 +01:00`), every attempt failing in ~680ms with `"no user query found in messages"`. |

---

## 2. Detailed Behavioral & Component Specifications

### 2.1 Capture Actual Measured Token Usage from Previous LLM Calls

1. **Per-Turn Measured Token Baseline:**
   - Every completed LLM call (both streaming and non-streaming) through the agent's inner chat pipeline reports token usage (via `ChatResponse.Usage`, streaming `UsageContent` in `ChatResponseUpdate.Contents`, or OpenAI-compatible `ChatResponseUpdate.AdditionalProperties["usage"]`).
   - Capture the latest completed call's measured token counts and associate them with the active conversation state:
     - **`LastMeasuredInputTokens`** (`InputTokenCount` / `prompt_eval_count`): The exact server-measured token count of the prompt sent in Call $N$ (which inherently includes system instructions, tool schemas, chat-template framing, and all included messages sent in Call $N$).
     - **`LastMeasuredOutputTokens`** (`OutputTokenCount` / `eval_count`): The exact server-measured token count of the assistant response generated in Call $N$ (including reasoning/thinking traces and tool call arguments).
     - **`LastMeasuredMessageCount`**: The exact count of **included** (non-excluded) conversation messages that were sent in Call $N$'s prompt (excluding any messages in `CompactionMessageIndex` with `IsExcluded == true` from prior compactions).
     - **`LastMeasuredResponseMessageCount`**: The number of assistant response messages produced by Call $N$ (typically `1`) whose tokens are already accounted for in `LastMeasuredOutputTokens`.

2. **Compaction Baseline Reset:**
   - Whenever [`PiStyleCrucibleCompactionStrategy`](../../../src/NightCodeForge.Agents/Compaction/PiStyleCrucibleCompactionStrategy.cs) performs a compaction pass (excluding summarized groups and inserting a checkpoint summary), reset the measured baseline (`LastMeasuredInputTokens`, `LastMeasuredOutputTokens`, `LastMeasuredMessageCount`, `LastMeasuredResponseMessageCount`) so that the post-compaction size is estimated cleanly for the immediate request and re-anchored to actual server measurements once that request completes.

---

### 2.2 Estimate Next-Request Additional Tokens via `Pmad.AI.Tokens.HeuristicTokenEstimator`

1. **Model-Calibrated `Pmad.AI` Estimator Selection:**
   - Use `Pmad.AI.Tokens.HeuristicTokenEstimator` (already referenced via `Pmad.AI` in [`NightCodeForge.Agents.csproj`](../../../src/NightCodeForge.Agents/NightCodeForge.Agents.csproj)):
     - Select `HeuristicTokenEstimator.Qwen` when the target model identifier contains `"qwen"` (case-insensitive).
     - Select `HeuristicTokenEstimator.Gemma` when the target model identifier contains `"gemma"` (case-insensitive).
     - Select `HeuristicTokenEstimator.Default` for all other models.
   - Extract all text, reasoning traces (`TextReasoningContent`), tool call names/serialized arguments (`FunctionCallContent`), and tool result payloads (`FunctionResultContent`) when estimating a `ChatMessage` or `CompactionMessageGroup`, including a small per-message framing allowance.
   - Replace the legacy `text.Length / 4.0` heuristic in [`TelemetryChatClient`](../../../src/NightCodeForge.Agents/Inference/TelemetryChatClient.cs) with the model-calibrated `Pmad.AI.Tokens.HeuristicTokenEstimator` so telemetry fallback estimates are consistent with compaction and `OllamaRouter`.

2. **Next-Request Projected Prompt Token Calculation:**
   - Let `includedMessages` be the ordered list of messages across all currently included (`!g.IsExcluded`) groups in `CompactionMessageIndex`.
   - Before Call $N+1$ is dispatched to the model, compute the **projected prompt token count** (`ProjectedPromptTokens`) that Call $N+1$ will consume:
     - **Case A — Measured Baseline Available from Call $N$ (`LastMeasuredInputTokens > 0` and `includedMessages.Count >= LastMeasuredMessageCount`):**
       - Start with `LastMeasuredInputTokens` (exact measured tokens for the first `LastMeasuredMessageCount` included messages + system prompt + tools).
       - For the `LastMeasuredResponseMessageCount` assistant response message(s) produced by Call $N$ (immediately following the first `LastMeasuredMessageCount` included messages): use `LastMeasuredOutputTokens` when positive; otherwise estimate those response message(s) using the model-calibrated `HeuristicTokenEstimator`.
       - Let `accountedMessageCount = LastMeasuredMessageCount + LastMeasuredResponseMessageCount` (when `LastMeasuredOutputTokens > 0` and the response messages are present in `includedMessages`, otherwise `LastMeasuredMessageCount`).
       - For all subsequent included messages at index `>= accountedMessageCount` (i.e., the new `tool` result message(s) from tool execution or newly injected user/system messages appended *after* Call $N$'s assistant response, avoiding double-counting Call $N$'s assistant response): estimate their additional tokens using the model-calibrated `HeuristicTokenEstimator` and add them to the baseline:
         $$\text{ProjectedPromptTokens}_{N+1} = \text{LastMeasuredInputTokens}_N + \text{LastMeasuredOutputTokens}_N + \text{EstimatedTokens}(\text{includedMessages}[\text{accountedMessageCount}..])$$
       - Also compute the full `HeuristicTokenEstimator` estimate of all currently included messages, and take the maximum of the measured-plus-delta projection and the full estimator count so that if tool results were mutated or `LastMeasuredInputTokens` is stale, the projection never underestimates.
     - **Case B — No Measured Baseline Available Yet (Turn 1, Newly Resumed Session, or Immediately Post-Compaction):**
       - Estimate the tokens of all currently included messages in `CompactionMessageIndex` using the model-calibrated `HeuristicTokenEstimator` (plus any known system instruction / tool schema token estimate when recorded).

---

### 2.3 Trigger Compaction Before Next Message Reaches the Context Limit

1. **Replace MAF's `UTF8ByteCount / 4` `ShouldCompact` Gate:**
   - In [`PiStyleCrucibleCompactionStrategy`](../../../src/NightCodeForge.Agents/Compaction/PiStyleCrucibleCompactionStrategy.cs), do not gate the base `CompactionStrategy` constructor on `CompactionTriggers.TokensExceed(options.TriggerThresholdTokens)` (which reads `CompactionMessageIndex.IncludedTokenCount` computed via `UTF8ByteCount / 4` and prevents `CompactCoreAsync` from being called).
   - Instead, configure `ShouldCompact` and `CompactCoreAsync` to evaluate `ProjectedPromptTokens > Options.TriggerThresholdTokens` (`ContextWindowTokens - ReserveTokens`).

2. **Pre-Flight Limit Protection & Debounce Override:**
   - Trigger compaction whenever `ProjectedPromptTokens > Options.TriggerThresholdTokens` (meaning the next request's prompt plus `ReserveTokens` for generation will reach or exceed `ContextWindowTokens`).
   - Evaluate emergency compaction (`isEmergency`) using `ProjectedPromptTokens`:
     - Set `isEmergency = true` (bypassing both `DebounceTurnCooldown` and `DebounceTokenDelta`) whenever `ProjectedPromptTokens >= (int)(Options.ContextWindowTokens * Options.EmergencyThresholdRatio)` **or** `ProjectedPromptTokens >= Options.ContextWindowTokens`.
     - Additionally, if `turnsSinceLastCompaction < Options.DebounceTurnCooldown` after a recent compaction, bypass `DebounceTurnCooldown` whenever `ProjectedPromptTokens > Options.TriggerThresholdTokens` **and** `tokensAddedSinceLastCompaction >= Options.DebounceTokenDelta` (protecting against a single turn with a large tool output that pushes the post-compaction prompt back above `TriggerThresholdTokens`).

3. **Model-Calibrated Token Counts for Anchor, Recent-Window Slicing & Debounce Tracking:**
   - Inside `PiStyleCrucibleCompactionStrategy.CompactCoreAsync`:
     - Compute `tokensAnchor`, `accumulatedRecentTokens` (against `effectiveKeepRecent`), and total conversation tokens for `tokensAddedSinceLastCompaction` / `_lastCompactedTokenCount` using the model-calibrated `Pmad.AI.Tokens.HeuristicTokenEstimator` over each group's messages rather than `CompactionMessageGroup.TokenCount` (`UTF8ByteCount / 4`), ensuring `_lastCompactedTokenCount` and `tokensAddedSinceLastCompaction` use matching estimator units.
     - Recompute group token estimates after `ClampOversizedToolResults` so emergency clamping is accurately reflected in `accumulatedRecentTokens`.

4. **Accurate Context Window Resolution Source:**
   - In [`HarnessAgentFactory`](../../../src/NightCodeForge.Agents/Inference/HarnessAgentFactory.cs) and [`ModelCapabilityResolver`](../../../src/NightCodeForge.Agents/Inference/ModelCapabilityResolver.cs):
     - Distinguish between an explicit `AgentProfile.ContextWindowTokens` override (`ResolutionSource = "AgentProfile"`) and a dynamically discovered context window from `ILlmGatewayService.ResolveContextWindowAsync` (`ResolutionSource = "GatewayDiscovery"`).

---

### 2.4 Fail Fast on Deterministic Chat Template / Prompt Errors

1. **Non-Retryable Prompt/Template Error Classification:**
   - In [`TransientHttpErrorRetryHandler`](../../../src/NightCodeForge.Core/Resilience/TransientHttpErrorRetryHandler.cs) / [`TransientErrorDetector`](../../../src/NightCodeForge.Core/Resilience/TransientErrorDetector.cs):
     - When an `HTTP 500 InternalServerError` response body contains a deterministic chat prompt or template rendering error (specifically `"no user query found in messages"` or `"chat prompt error"`), do **not** classify the response as transient and do **not** enter the 20-attempt retry loop; return/throw immediately on the first response.

---

## 3. Verification & Test Strategy

1. **Measured-Token + `Pmad.AI` Delta Compaction Trigger Tests ([`tests/NightCodeForge.Agents.Tests`](../../../tests/NightCodeForge.Agents.Tests)):**
   - **ForgeTask #51 Regression Scenario (`UTF8ByteCount / 4` Below Threshold, Measured + Delta Above Threshold):**
     - Construct a `PiStyleCrucibleCompactionStrategy` with `ContextWindowTokens = 262_144`, `ReserveTokens = 8_192` (`TriggerThresholdTokens = 253_952`), and `SummarizerModelId = "Qwen3.8-27B:latest"`.
     - Populate a conversation whose raw `UTF8ByteCount / 4` (`index.IncludedTokenCount`) is below `253,952` (e.g., `~240,000` bytes/4), record a previous call measurement of `InputTokenCount = 253_000`, `OutputTokenCount = 500`, and append a new tool result whose `HeuristicTokenEstimator.Qwen` token estimate is `~1,000` tokens (`ProjectedPromptTokens = 254,500 > 253,952`).
     - Verify that `CompactAsync` triggers (`returns true`) and compacts the conversation before the next call is sent.
   - **Pre-Limit Emergency Override After Recent Compaction:**
     - Verify that if a compaction occurred 1 turn ago (`turnsSinceLastCompaction < DebounceTurnCooldown`), and a subsequent tool output pushes `ProjectedPromptTokens` past the emergency/context limit, debounce is bypassed and compaction triggers to prevent overflowing `ContextWindowTokens`.
   - **Baseline Reset on Compaction:**
     - Verify that after compaction succeeds, the previous pre-compaction `LastMeasuredInputTokens` is reset so the next call does not falsely re-trigger using the pre-compaction token count.

2. **`ThinkingAndActivityTracingChatClient` Usage Capture Tests ([`tests/NightCodeForge.Agents.Tests`](../../../tests/NightCodeForge.Agents.Tests)):**
   - Verify that both `GetResponseAsync` (via `ChatResponse.Usage`) and `GetStreamingResponseAsync` (via `UsageContent` in the stream) capture `InputTokenCount`, `OutputTokenCount`, and the sent message count and forward them to the compaction strategy / token tracker.

3. **Model-Calibrated Estimator Selection Tests ([`tests/NightCodeForge.Agents.Tests`](../../../tests/NightCodeForge.Agents.Tests)):**
   - Verify that `Qwen*` models resolve `HeuristicTokenEstimator.Qwen`, `Gemma*` models resolve `HeuristicTokenEstimator.Gemma`, and other models resolve `HeuristicTokenEstimator.Default`, and that tool calls (`FunctionCallContent`), tool results (`FunctionResultContent`), and reasoning traces (`TextReasoningContent`) are all included in message token estimation.

4. **Non-Retryable Template Error Tests ([`tests/NightCodeForge.Core.Tests`](../../../tests/NightCodeForge.Core.Tests)):**
   - Verify that when a mock backend returns `HTTP 500 InternalServerError` with body `{"error":"no user query found in messages"}`, `TransientHttpErrorRetryHandler` does **not** retry and fails on attempt 1.
   - Verify that standard `HTTP 500` responses (e.g. `{"error":"internal server error"}` or model load failures) continue to be retried normally.

---

## 4. Definition of Done (DoD) Checklist

- [x] Capture server-measured `InputTokenCount` (`prompt_eval_count`), `OutputTokenCount` (`eval_count`), and sent message count on each completed LLM call in `ThinkingAndActivityTracingChatClient` and feed them into `PiStyleCrucibleCompactionStrategy`.
- [x] Integrate `Pmad.AI.Tokens.HeuristicTokenEstimator` (`Qwen`, `Gemma`, `Default`) for estimating message/group token counts (including text, reasoning traces, tool calls, and tool results) in `PiStyleCrucibleCompactionStrategy` and `TelemetryChatClient`.
- [x] Update `PiStyleCrucibleCompactionStrategy` (`ShouldCompact` and `CompactCoreAsync`) to project the next request's prompt token count from `LastMeasuredInputTokens + LastMeasuredOutputTokens + EstimatedNewMessageTokens` (with full-history `HeuristicTokenEstimator` fallback and post-compaction baseline reset) and trigger compaction before the next message reaches the limit.
- [x] Use model-calibrated `HeuristicTokenEstimator` group token counts for anchor budget, keep-recent window slicing, and debounce calculations in `PiStyleCrucibleCompactionStrategy`, and bypass debounce when projected tokens reach the emergency/context ceiling.
- [x] Distinguish `"GatewayDiscovery"` from `"AgentProfile"` in `HarnessAgentFactory` / `ModelCapabilityResolver` when resolving context window tokens.
- [x] Guard `TransientHttpErrorRetryHandler` against retrying deterministic `HTTP 500` chat prompt/template errors (`"no user query found in messages"`, `"chat prompt error"`).
- [x] Add unit tests in `NightCodeForge.Agents.Tests` and `NightCodeForge.Core.Tests` covering measured-token + `Pmad.AI` delta compaction triggering, streaming/non-streaming usage capture, baseline reset after compaction, and fast-fail on deterministic template errors; verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

