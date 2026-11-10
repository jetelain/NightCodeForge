# 🛡️ Step 11.49C: Streaming Tool-Call Delta Telemetry (`OpenAI`) & 10-Minute `StreamIdleTimeoutMinutes` for Ollama Native Buffering

> **Parent Plan:** [docs/fixes/README.md](README.md)  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Completed  
> **Target Projects:** `NightCodeForge.Core`, `NightCodeForge.Agents`, `NightCodeForge.Agents.Tests`, `NightCodeForge.Core.Tests`  
> **Dependencies:** Step 11.49 (Two-Phase Streaming Idle Watchdog)  
> **Aligns With:** `AGENTS.md`, `docs/PLAN.md`

> [!IMPORTANT]
> **Specification & Architectural Decision Record:** This document records the root-cause findings and architectural decisions around live tool-call token telemetry during streaming inference and the `10`-minute default `StreamIdleTimeoutMinutes` ceiling.

---

## 1. Context & Root Cause Analysis

When an LLM generates a large tool call (such as `write_file` or `edit_file` containing thousands of tokens of code), `Microsoft.Extensions.AI.FunctionCallContent` cannot be emitted incrementally token-by-token because its `Arguments` property (`IDictionary<string, object?>?`) requires a complete, parseable JSON object rather than partial JSON string fragments.

Investigation of live streaming behavior across both supported inference protocols revealed two distinct buffering mechanisms:

### 1.1 Ollama / OllamaRouter Native Protocol (`POST /api/chat` via `OllamaSharp.OllamaApiClient`)
1. **Wire Format Constraint:**
   - In Ollama's native `/api/chat` NDJSON stream (`OllamaSharp.Models.Chat.ChatResponseStream`), `message.tool_calls[].function.arguments` is defined as a structured JSON object (`map[string]any` / `IDictionary<string, object?>`), not a string delta.
2. **Server-Side Buffering Behavior:**
   - While a reasoning model (e.g., `Qwen3.8-27B`) emits `<think>` tokens, Ollama streams `message.thinking` chunks (`TextReasoningContent`) in real time.
   - As soon as the model transitions from `<think>` into `<tool_call>`, **the Ollama server buffers all generated tool-call tokens in memory** until the model closes the tool call and the JSON object can be parsed.
   - During the entire tool-call generation window, **zero NDJSON lines are sent over the HTTP response stream**, leaving `TelemetryChatClient`'s `enumerator.MoveNextAsync()` suspended on `StreamReader.ReadLineAsync()`.
   - Once the tool call finishes, Ollama flushes a single NDJSON chunk containing the complete `tool_calls` payload, immediately followed by the final `done: true` chunk (`eval_count` / `UsageContent`).
3. **Why `DefaultStreamIdleTimeoutMinutes` Must Be `10` Minutes:**
   - On a large context (`100k–130k` KV cache) running at `15–18 tokens/sec`, generating a large `5,000–9,000` token tool call takes **5 to 9 minutes** of continuous GPU decoding during which Ollama emits no HTTP chunks.
   - A 5-minute `StreamIdleTimeoutMinutes` ceiling risks falsely aborting a healthy, actively decoding Ollama tool call right before completion. Raising `DefaultStreamIdleTimeoutMinutes` to **`10` minutes** (`LlmProviderOptions.DefaultStreamIdleTimeoutMinutes = 10` and `LlmRetryPolicyOptions.DefaultStreamIdleTimeoutMinutes = 10.0`) protects native Ollama `/api/chat` tool-call buffering while still bounding true mid-stream server hangs.

### 1.2 OpenAI-Compatible Protocol (`POST /v1/chat/completions` via `Microsoft.Extensions.AI.OpenAIChatClient`)
1. **Client-Side Buffering in `Microsoft.Extensions.AI.OpenAI`:**
   - Unlike Ollama's `/api/chat`, OpenAI-compatible SSE streams *do* emit incremental tool-call fragments over the wire via `choices[0].delta.tool_calls[].function.arguments` (`OpenAI.Chat.StreamingChatToolCallUpdate`).
   - However, `Microsoft.Extensions.AI.OpenAIChatClient.FromOpenAIStreamingChatCompletionAsync` intercepts `item.ToolCallUpdates`, appends `FunctionArgumentsUpdate` to a private internal `StringBuilder`, and yields a `ChatResponseUpdate` whose **`Contents` collection is empty** (`Count == 0`, `Text == ""`), storing the `StreamingChatCompletionUpdate` only in `update.RawRepresentation`.
   - `OpenAIChatClient` only parses and yields the `FunctionCallContent` item in a final synthesized `ChatResponseUpdate` **after** the SSE stream loop ends.
2. **Telemetry & Watchdog Impact Without Raw Delta Inspection:**
   - Because `update.Contents` is empty on every streamed tool-call delta chunk, inspecting only `update.Contents` for `FunctionCallContent` leaves the live token counter in **Live Developer Operations & Hardware Telemetry** completely frozen until the stream finishes.
   - Furthermore, if a model begins streaming a tool call without prior text or reasoning chunks, `HasNonEmptyStreamContent(update)` would return `false` for every tool-call delta chunk unless `update.RawRepresentation` is inspected.

---

## 2. Architectural Solution & Behavioral Specifications

### 2.1 Live OpenAI Tool-Call Delta Extraction & Deduplication (`TelemetryChatClient`)
1. **Inspect `StreamingChatCompletionUpdate.ToolCallUpdates` in `GetStreamingResponseAsync`:**
   - When `update.RawRepresentation is OpenAI.Chat.StreamingChatCompletionUpdate openAiUpdate` and `openAiUpdate.ToolCallUpdates is { Count: > 0 }`, extract non-empty `FunctionName` and `FunctionArgumentsUpdate` fragments on each chunk.
   - Append the raw fragments to a per-stream tool-call buffer, re-estimate the cumulative tool-call tokens via `ModelCalibratedTokenEstimator`, and increment `tokenCount` by the positive delta on each chunk so `UpdateLlmCallProgress` broadcasts live token counts and `tok/s` rates to the UI.
   - Append an in-progress `[Tool Call: {FunctionName}] {argsDelta}` preview to `textBuffer` so that if the stream aborts mid-generation, `CompletionPreview` preserves diagnostic visibility into the partial tool call.
2. **Deduplicate Against the Final Synthesized `FunctionCallContent`:**
   - When `OpenAIChatClient` yields the final synthesized `FunctionCallContent` at the end of the stream (after `sawOpenAiToolCallUpdates == true`), do **not** add `EstimateFunctionCallTokens(fcc)` a second time to `tokenCount`.
   - Rewind `textBuffer` to the pre-tool-call offset and replace the raw JSON delta fragments with the clean formatted `[Tool Call: {Name}] key=value` preview.
3. **First-Token Watchdog Transition (`HasNonEmptyStreamContent`):**
   - `HasNonEmptyStreamContent(ChatResponseUpdate update)` MUST return `true` when `update.RawRepresentation is StreamingChatCompletionUpdate` contains any `StreamingChatToolCallUpdate` with a non-empty `FunctionName` or non-empty `FunctionArgumentsUpdate`.

### 2.2 10-Minute Default `StreamIdleTimeoutMinutes` (`LlmProviderOptions` & `LlmRetryPolicyOptions`)
1. Set `LlmProviderOptions.DefaultStreamIdleTimeoutMinutes = 10` (10 minutes).
2. Set `LlmRetryPolicyOptions.DefaultStreamIdleTimeoutMinutes = 10.0` (10 minutes).

---

## 3. Future Considerations

- If Ollama's `/api/chat` protocol or `OllamaSharp` introduces incremental streaming of partial tool-call argument deltas in a future release (or if `Microsoft.Extensions.AI` introduces a first-class streaming tool-call delta `AIContent` type), `TelemetryChatClient` can be extended to consume those deltas directly.

---

## 4. Definition of Done (DoD) Checklist

- [x] Update `LlmProviderOptions.DefaultStreamIdleTimeoutMinutes` and `LlmRetryPolicyOptions.DefaultStreamIdleTimeoutMinutes` from `5` to `10` minutes.
- [x] Extract live tool-call argument deltas from `OpenAI.Chat.StreamingChatCompletionUpdate.ToolCallUpdates` in `TelemetryChatClient.GetStreamingResponseAsync` and update `HasNonEmptyStreamContent`.
- [x] Prevent token double-counting when `OpenAIChatClient` emits the final synthesized `FunctionCallContent` at stream completion while replacing raw JSON fragments in `CompletionPreview` with the formatted tool call preview.
- [x] Add unit tests in `TelemetryChatClientTests` verifying live token increments across `StreamingChatCompletionUpdate.ToolCallUpdates` chunks and deduplication on the final `FunctionCallContent` update.
- [x] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

