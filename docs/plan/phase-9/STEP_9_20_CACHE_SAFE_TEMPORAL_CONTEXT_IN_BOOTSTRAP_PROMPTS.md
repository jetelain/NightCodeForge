# 📅 Step 9.20: Cache-Safe Temporal Context Injection in Bootstrap Prompts

> **Phase 9 Implementation Step**  
> *Parent Plan:* [Phase 9 Master Plan](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md) | [LOCAL_LLM_OPTIMIZATIONS.md](../../LOCAL_LLM_OPTIMIZATIONS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Agents`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Agents.Tests`  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **Provide Temporal Awareness Without KV Cache Degradation:** Inject the current calendar date (`YYYY-MM-DD`) into the initial user bootstrap prompt and task context, enabling coding agents to author dated task specifications (e.g. `docs/tasks/YYYY-MM-DD-*.md`), write accurate changelogs, and evaluate time-sensitive dependencies without hallucinating obsolete training-cutoff years (e.g. 2023 or 2024).
2. **Strict System Prompt Protection:** Prohibit placing dynamic dates or timestamps in the system instructions (Layers 1 and 2 of [PromptPrefixStabilizer](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Optimizations/PromptPrefixStabilizer.cs)), preserving 100% byte-identical prefix stability for GPU KV cache reuse (90%+ hit rate in Ollama, `llama.cpp`, and vLLM).
3. **Date-Only Granularity:** Mandate strict ISO-8601 calendar date granularity (`yyyy-MM-dd`) without hours, minutes, or seconds, ensuring all turns within the same day remain deterministic and cache-stable.
4. **Deterministic TimeProvider Inversion:** Leverage .NET's `TimeProvider` abstraction across prompt builders and harnesses to ensure clean, controllable unit and integration testing without static clock coupling.

---

## 2. Problem Statement & Architectural Constraints

### 2.1 LLM Training Cutoff & Hallucinated Dates
- Modern LLMs (e.g. Qwen 2.5, DeepSeek, Llama 3) have knowledge cutoffs prior to the current execution year. When instructed to create or reference task specifications under `docs/tasks/`, agents frequently generate obsolete year prefixes (e.g. `docs/tasks/2024-03-15-feature.md` instead of `2026-10-18-feature.md`).
- Running `bash: date` to determine the current date wastes a full tool invocation turn and token round-trip.

### 2.2 The KV Cache Invalidation Risk
- In local GPU inference, evaluating 3,000–5,000 input tokens takes **4–8 seconds** per turn. With KV cache reuse, time-to-first-token drops to **<100ms**.
- As established in [LOCAL_LLM_OPTIMIZATIONS.md](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/docs/LOCAL_LLM_OPTIMIZATIONS.md#L226-L254):
  > *"If dynamic content (e.g. current timestamp `2026-10-13 07:30:15`, dynamic turn counters, or random IDs) is placed near the top of the system prompt, the **entire KV cache is invalidated**, forcing a full prompt re-computation on every single turn! Dynamic metadata are strictly forbidden in Layers 1–2."*
- Placing timestamps or dates in the system prompt instructions invalidates the GPU KV cache across runs, evicting pre-computed attention keys and drastically degrading inference throughput.

---

## 3. Behavioral Specifications

### 3.1 Injection Placement Rules
1. **Forbidden in System Instructions (Layers 1 & 2):**
   [PromptPrefixStabilizer](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Optimizations/PromptPrefixStabilizer.cs) Layers 1 (Global Rules) and 2 (Stage Persona) must remain completely free of dynamic dates, timestamps, or clocks.
2. **Permitted in User Bootstrap Prompt (Turn 1 User Message):**
   In [DefaultBootstrapPromptBuilder](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/DefaultBootstrapPromptBuilder.cs), expose `context.current_date` in the Scriban template model formatted as `yyyy-MM-dd`.
   If the active prompt template does not explicitly reference `current_date`, the builder appends a standardized context block:
   ```markdown
   [Context]:
   - Current Date: YYYY-MM-DD
   ```
   Because this appears in the first user message (`ChatRole.User`), the system instruction prefix remains identical and cached in VRAM.
3. **Permitted in Layer 3 Task Scope `<task>` Attribute:**
   In [CrucibleAgentHarness.BuildTaskScope](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs#L921), optionally annotate the task XML tag:
   ```xml
   <task id="42" title="Add Multiply" path="docs/tasks/..." date="YYYY-MM-DD">
   ```
   Layer 3 is already task-specific and stable across all turns of that task's stage, preserving intra-stage KV cache hits.
4. **Ideation Session Ingress:**
   In [IdeationService](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Ideation/IdeationService.cs), supply `Current Date: YYYY-MM-DD` in the initial user prompt so ideation discovery agents author task proposals with the correct calendar date prefix.

### 3.2 Granularity & Determinism
- **Date-Only Policy:** The injected value must strictly follow `yyyy-MM-dd` format (e.g. `2026-10-18`).
- **No Timestamps or Seconds:** Hours, minutes, seconds, or millisecond timestamps are strictly prohibited in prompt bodies to prevent per-turn or intra-hour cache invalidation.
- **Clock Abstraction:** Use `TimeProvider` (defaulting to `TimeProvider.System`) injected into constructors, enabling unit tests to freeze time predictably without system clock dependencies.

---

## 4. Implementation Checklist & Deliverables

- [ ] **DefaultBootstrapPromptBuilder:**
  - [ ] Add `current_date` property to Scriban model under `context.current_date`.
  - [ ] Render `[Current Date]: {currentDate}` in the bootstrap prompt output.
  - [ ] Support injecting `TimeProvider` (or passing date string parameter).
- [ ] **CrucibleAgentHarness & PromptPrefixStabilizer:**
  - [ ] Verify that Layers 1 and 2 in `PromptPrefixStabilizer.cs` contain zero date/timestamp strings.
  - [ ] Add optional `date="yyyy-MM-dd"` attribute to `<task>` in `CrucibleAgentHarness.BuildTaskScope`.
- [ ] **IdeationService:**
  - [ ] Inject current calendar date into initial ideation conversation context.
- [ ] **Automated Testing:**
  - [ ] Test `DefaultBootstrapPromptBuilder` produces `Current Date: yyyy-MM-dd` in output with custom `TimeProvider`.
  - [ ] Test `PromptPrefixStabilizer` Layer 1 and Layer 2 output is byte-identical across calls on different dates.
  - [ ] Run full test suite per AGENTS.md: `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
