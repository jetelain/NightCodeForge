# 🔧 Step 11.57: Crucible `ask` Tool Lenient Parsing, Error Diagnostics & Transcript Turn Timestamps

> **Parent Plan:** [docs/fixes/README.md](README.md)  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Completed  
> **Target Projects:** `NightCodeForge.Agents`, `NightCodeForge.Agents.Tests`, `NightCodeForge.Core`  
> **Dependencies:** `Pmad.AI >= 0.0.6`, `WorkflowInteractionToolModule`, `LenientToolDispatcher`, `QuestionItem`, `CrucibleConversationCoordinator`, `SessionTranscriptService`  
> **Aligns With:** `AGENTS.md`, `docs/PLAN.md`

> [!IMPORTANT]
> **Specification Disclaimer:** This document is a behavioral specification. It defines required behaviors, architectural invariants, and verification criteria. Specific internal helper names and implementation details remain at the discretion of the implementer within these constraints.

---

## 1. Context & Motivation

Analysis of the `v1-smoke-Flint-latest-20261123-214242` transcripts exposed three Crucible harness bugs affecting tool reliability and post-run diagnostics:

1. **`ask` Tool Parameter Envelope Failure (`ncf-tr-csharp-005`):** The model correctly identified blocking ambiguities and called `ask` **19 times**, wrapping arguments in `{"parameters": {"questions": [...]}}`. Because `LenientToolDispatcher` did not unwrap a top-level `parameters` / `arguments` envelope, `questions` bound as `null`, causing `WorkflowInteractionToolModule.AskAsync` to return `"Error: At least one question must be provided in 'questions'"` on every attempt until the model abandoned `ask` and produced a false `Ready` verdict.
2. **`ask` Tool `QuestionItem.Options` Deserialization Opaque Error (`ncf-tr-csharp-001`, `ncf-tr-csharp-003`):** `QuestionItem.Options` is typed as `IReadOnlyList<string>?`. When the model passed structured option objects (`[{"label": "...", "description": "..."}]`), `System.Text.Json` threw during `AIFunctionFactory` parameter binding before `AskAsync` could execute, and `FunctionInvokingChatClient` surfaced only `"Error: Function failed."`, wasting multiple turns with no actionable hint.
3. **Zeroed Transcript Turn Timestamps (all 27 transcripts):** `CrucibleConversationCoordinator.BuildTranscriptEntries(request.ChatHistory)` reconstructs transcript entries from `ChatHistory` without per-turn timestamps, causing every turn header across all saved transcripts to render as `### [ 00:00 ]` and preventing turn-by-turn latency or timeout stall diagnosis.

---

## 2. Required Behaviors & Constraints

### 2.1 Upgrade `Pmad.AI` to `0.0.6` for Parameter Envelope Unwrapping
- Upgrade the `Pmad.AI` package reference to `0.0.6` (which provides built-in top-level `parameters` / `arguments` envelope unwrapping in `Pmad.AI.Tools.LenientToolDispatcher`, including stringified-JSON envelopes and schema guards).
- Do **not** duplicate envelope-unwrapping logic inside NightCodeForge; rely on `Pmad.AI.Tools.LenientToolDispatcher`'s built-in normalization and ensure NightCodeForge's dispatcher configuration keeps envelope unwrapping enabled.
- If `Pmad.AI` `0.0.6` exposes a new envelope-unwrapping counter on `LenientDispatchTelemetrySnapshot`, project/reconcile it cleanly in NightCodeForge dispatch telemetry.
- Add NightCodeForge integration/unit tests confirming `{"parameters": {"questions": [...]}}` (and stringified variants) binds cleanly to the `ask` tool via `NightCodeForgeToolDispatcher`.

### 2.2 Lenient `QuestionItem.Options` Parsing in `ask` Tool
- `QuestionItem.Options` deserialization / normalization for the `ask` tool MUST accept both:
  - Plain strings (e.g., `"Idempotent verbs only"`), and
  - Structured choice objects containing fields such as `label` and optional `description` (e.g., `{"label": "5 seconds", "description": "5000 ms"}`), normalizing them cleanly into human-readable `string` option entries (e.g., `"Label — Description"` or `"Label"` when description is absent/empty) without throwing a JSON deserialization exception.
- Existing `QuestionItemParser.Normalize` behavior (trimming, filtering blank entries, preserving `Prompt` and `Rationale`) MUST continue to hold.

### 2.3 Actionable Tool Binding Error Messages
- When tool argument JSON deserialization, type conversion, or parameter binding fails in the Crucible tool invocation pipeline, the error returned to the model MUST provide an actionable diagnostic explaining which parameter failed and the expected shape (e.g., expected array of `{prompt, rationale?, options?}`), rather than the opaque `"Error: Function failed."` message.

### 2.4 Per-Turn Timestamps in Crucible Transcripts
- Crucible stage execution MUST track per-turn timestamps relative to stage start (or map recorded `ConversationTurn.Timestamp` / model-call timing metadata onto `TranscriptEntry`) so `CrucibleConversationCoordinator.BuildTranscriptEntries` and `InMemoryCrucibleConversationCoordinator` emit real elapsed `[ MM:SS ]` timestamps in rendered markdown transcripts rather than `[ 00:00 ]` for every turn.
- Timestamps across sequential turns MUST be non-decreasing and reflect actual elapsed stage duration up to completion, interruption, or timeout.

---

## 3. Definition of Done (DoD) Checklist

- [x] Upgrade `Pmad.AI` to `0.0.6` for built-in top-level `parameters` / `arguments` envelope unwrapping in `LenientToolDispatcher` (and project any new telemetry counter), with NightCodeForge regression tests verifying `ask` binding.
- [x] Support both `string` and `{label, description}` object entries for `QuestionItem.Options` in `ask` tool binding/parsing, with unit tests covering mixed/object/string payloads.
- [x] Replace opaque `"Error: Function failed."` binding failures with actionable schema/parameter diagnostics returned to the model, with unit tests.
- [x] Preserve and render real per-turn elapsed timestamps in Crucible transcripts (`CrucibleConversationCoordinator`, `InMemoryCrucibleConversationCoordinator`, `SessionTranscriptService`), with unit tests verifying monotonic non-zero timestamps.
- [x] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
