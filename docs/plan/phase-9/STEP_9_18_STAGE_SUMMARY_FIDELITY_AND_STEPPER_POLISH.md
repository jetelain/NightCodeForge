# 📊 Step 9.18: Agentic Stage Summary Fidelity & Stepper Readability Polish

> **Phase 9 Implementation Step**  
> *Parent Plan:* [Phase 9 Master Plan](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Agents`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Agents.Tests`, `tests/NightCodeForge.Web.Tests`  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations.

---

## 1. Executive Objective

1. **Stage summary fidelity:** the stage summary (run result summary, conversation summary, stage-summary data bag entry) is the **concluding agent message** only, not a concatenation of every intermediate utterance. This applies uniformly to the Crucible, ACP and Pi RPC harnesses.
2. **Routing tags:** routing tags in the concluding message (request-changes, approved) are parsed for routing/feedback, then removed from the human-facing summary. Only the known tags are removed; other angle-bracket content (generics, code) is preserved.
3. **Minimal, accessible pipeline stepper:** no visible stage title/status text under the step nodes; full information is available via accessibility attributes and a tooltip; connectors stay aligned.
4. **Reconciliation with Step 9.16:** the task description holds the technical architecture (approved plan); the stage summary is the concise concluding synthesis.

---

## 2. Problem Statement & Findings Inventory

| # | Subsystem | Issue | Impact |
| :-- | :--- | :--- | :--- |
| M1 | ACP / Pi RPC harnesses | Streaming text is appended across all turns and tool calls into one summary. | Summary is an unformatted dump of every utterance. |
| M2 | Crucible harness | Text buffer is not separated per turn; pre-tool text merges into the final message. | Intermediate chatter pollutes the final turn and summary. |
| M3 | Summary post-processing | Stripping all angle-bracket content corrupts summaries containing generics/code; tag parsing is implemented separately per harness. | Corrupted text; inconsistent routing across harnesses. |
| M4 | Pipeline stepper | Long visible titles/status under each node wrap and misalign (6–8 stages). | Crowded, illegible on desktop and mobile. |
| M5 | Accessibility | Removing text without replacement markup breaks screen readers, keyboard use and text-based tests. | WCAG regressions. |

---

## 3. Behavioral Specifications

### 3.1 Turn isolation and concluding summary
- **No data loss:** at a tool-call boundary, the text and reasoning accumulated so far are **flushed as their own assistant message** in the conversation history (reasoning stays attached to its turn, as some providers require paired thinking and tool-use blocks). Buffers are then reset. Do **not** simply discard buffered text/reasoning.
- At end of stream, remaining buffered text becomes the final assistant message, unless it has already been recorded.
- The summary is the text of the **last assistant message with non-empty text**. If the run ended on a tool call with no concluding text, the summary may be an earlier message; this limitation is acknowledged, and the empty case falls back to the existing default completed-summary constant.
- **Shared extractor:** one shared component (used by all three harnesses) takes the concluding text and returns: the clean summary, optional reviewer feedback and optional target routing. Tag names and regexes are named constants. Handling covers both request-changes and approved tags. It removes only the recognized routing tags.
- ACP and Pi RPC: detect turn boundaries using the protocol's own turn/message-end and tool-call events (not ad-hoc heuristics), exclude thought/reasoning chunks from message text, and flush the final turn at completion. Their summaries go through the same shared extractor.

### 3.2 Pipeline stepper
- Remove the visible stage title and status text beneath nodes; keep node circle, icon, status overlay (success, failed, waiting), badge, index, and metrics badge.
- Each step exposes the full stage name and status through: an `aria-label`, a native tooltip, and a visually hidden text element. Avoid triple announcement: keep the label **or** hidden text for screen readers as the single announced source, and keep the tooltip for pointer users. Document that native tooltips do not cover touch devices; status remains conveyed on touch by the icon overlays (not color alone).
- **Interactivity semantics:** button role, focusability and key handling apply **only when the step is clickable**. Non-clickable steps are not focusable.
- **Keyboard:** Enter and Space activate the step; Space must not scroll the page (conditional default-prevention, since Blazor static `preventDefault` would block other keys).
- **Layout:** geometry uses CSS variables derived from the circle size; the connector's vertical offset accounts for the header row (badge/index) above the circle as well as half the circle height. Steps use a minimum width and flexible sizing; the stepper container may shrink (`min-width: 0`).
- Remove obsolete title/status CSS rules. Reuse an existing visually-hidden utility if one already exists (framework or `app.css`) instead of redefining it.

---

## 4. Implementation Checklist & Deliverables

- [ ] **Shared summary extractor:** concluding-text selection, routing-tag parsing (request-changes, approved), tag-only stripping, constants; used by all harnesses.
- [ ] **Crucible harness:** per-turn flush at tool boundaries (text and reasoning preserved in history), final-turn recording, summary from last non-empty assistant message.
- [ ] **ACP and Pi RPC harnesses:** remove unbounded concatenation; protocol-based turn tracking; thought chunks excluded; shared extractor.
- [ ] **Stepper markup:** remove visible title/status; accessible name and hidden text without duplication; conditional role/tabindex/keyboard handling.
- [ ] **Stepper CSS:** variable-based geometry including header offset, `min-width` handling, removal of obsolete rules, no duplicate visually-hidden definition.
- [ ] **Tests**
  - [ ] Crucible multi-turn tool-calling: summary equals the concluding message; intermediate messages preserved in history; reasoning kept with its tool-call turn.
  - [ ] ACP and Pi RPC multi-turn: concluding message only; thought chunks excluded.
  - [ ] Extractor: generics/code preserved; request-changes and approved parsed; both tags stripped; empty text falls back to the default.
  - [ ] Stepper (bUnit): accessible name/label, hidden text present, no visible title/status elements, non-clickable steps not focusable, Enter/Space activation, existing tests still pass.
  - [ ] Review any E2E tests that match visible stage text and update them.
- [ ] Run the full test suite including E2E, per AGENTS.md.
