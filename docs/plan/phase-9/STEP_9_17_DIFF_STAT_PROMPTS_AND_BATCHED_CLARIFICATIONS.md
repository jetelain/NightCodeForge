# 🔍 Step 9.17: Diff Stat Review Prompts & Multi-Question Clarification Rework

> **Phase 9 Implementation Step**  
> *Parent Plan:* [Phase 9 Master Plan](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Agents`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Agents.Tests`, `tests/NightCodeForge.Web.Tests`  
> *Status:* Completed | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations.

---

## 1. Executive Objective

1. **Diff stat instead of full diff in review/fix prompts:** the bootstrap prompt contains only the modified-file summary (`git diff --stat`, wide enough to avoid path truncation), fenced as a text block. The full diff is not injected; agents inspect files on demand.
2. **Clean-break multi-question `ask` tool:** the tool accepts only a list of structured questions (prompt, optional rationale, optional options). The legacy single-`prompt` form is removed everywhere. Blank prompts or an empty list are rejected; no synthetic fallback questions.
3. **Batched clarification directive:** the `task-review` agent investigates first and gathers all questions into one `ask` call. Follow-up questions are allowed only for blockers discovered after answers arrive.
4. **Atomic batch resumption and Inbox UI:** a task resumes only when every question of the current interruption batch is answered. Only answers of the current batch are injected into the resumed run.

---

## 2. Problem Statement & Findings Inventory

| # | Subsystem | Issue | Impact |
| :-- | :--- | :--- | :--- |
| D1 | Bootstrap prompt builder / Crucible harness | Full review diff injected into the initial prompt. | Context blow-up, token cost, degraded review. |
| D2 | Git workspace manager | No stat-generation operation; the stat path-width default truncates long paths. | Callers parse diffs or run ad-hoc commands. |
| D3 | `ask` tool factory / canonical tools | Schema accepts a single prompt only. | Agents cannot consolidate questions. |
| D4 | Interruption exception / run result / stage result | Carry a single question and fabricate fallback questions. | No native multi-question support; violates AGENTS.md. |
| D5 | `task-review` directive | "Ask immediately" on first ambiguity. | Repeated pause/resume loops. |
| D6 | Ideation service | Cancellation registrations are not all disposed; answers are returned as an unpaired concatenation. | Leaks; ambiguous Q/A mapping. |
| D7 | Resumption | Answer filtering by stage alone can include answers from earlier batches (second `ask` in same stage, re-run after review rejection); the original `ask` tool call remains unanswered in saved chat history. | Answer leakage; providers may reject history containing a tool call without a result. |

---

## 3. Behavioral Specifications

### 3.1 Diff stat generation
- Add a git workspace manager operation producing the stat between the base branch and the task feature branch (three-dot semantic, same as the existing diff), using an extended stat width and name width so deep namespaces are not truncated.
- It applies the **same exclusion list** as the full-diff operation (lock files, designer/generated files, test results, TRX). Extract the exclusion list into one shared constant used by both.
- **Uncommitted work:** if review/fix agents can see uncommitted changes, provide an equivalent stat for the live working tree; otherwise document that review always follows a commit.
- Output is capped (maximum number of files shown, with a "N more files" indicator). An empty result is rendered explicitly as "no changes", never as an absent section.
- Failures raise `GitCommandException`; cancellation propagates unchanged (no blanket catch-and-wrap).

### 3.2 Prompt construction
- Add `ReviewDiffStat` to the pipeline data bag keys and to the agent session context. The orchestrator computes it on transitions to review/verification stages.
- **Single injection point:** the stat is rendered in exactly one place (decide between the bootstrap builder and the harness task scope; not both).
- The stat is fenced as a text block. The prompt tells the agent the **exact command and base branch** for inspecting a single file's diff in its worktree (three-dot, restricted to the file).
- Clean break: the raw `ReviewDiff` is no longer injected. Remove the `ReviewDiff` key/property if no other consumer (e.g. UI diff view) uses it; otherwise state the remaining consumer explicitly.

### 3.3 `ask` tool
- A question item has: prompt (required), rationale (optional), options (optional). Options present → single-choice; absent → free text.
- The tool factory registers `ask` with only the question list; its description tells the model to consolidate all questions in one call.
- **Validation errors are returned to the model as a tool error message** so it can correct and retry (verify function-invocation client behavior for thrown exceptions; they must not terminate the stage). Interruption itself remains an exception.
- Behavior with a registered ask handler (ideation): all questions are posted, all answers awaited, and the result is returned as **numbered question/answer pairs**. Every cancellation registration is disposed after completion, success or failure.
- Without a handler: throw the interruption exception carrying the full question list.
- Interruption exception, run result and stage result carry a question **collection** (non-empty, validated), with all legacy scalar properties and factory overloads removed.

### 3.4 Atomic batch resumption
- Questions asked by one interruption share a **batch identifier** (new property on `Question`, assigned when the interruption is persisted). Answers to inject are selected by batch identifier, not by stage ID.
- The orchestrator exposes a batch-submit operation taking question-ID → answer pairs.
  - Unknown question IDs, IDs not belonging to the task, already answered questions, and blank answers raise domain exceptions (no silent ignore).
  - Partial submission is allowed and keeps the task waiting.
  - When none of the batch's questions remain pending, resumption happens **once**: clear the waiting flag and enqueue the task. The check-and-clear must be atomic so concurrent submissions cannot enqueue twice.
- On resume, the formatted numbered Q&A of the current batch is stored under the last-human-answer data bag key **and** used to complete the pending `ask` tool call in the saved conversation (as the tool result), rather than appended as a standalone user message after an unanswered tool call. A short "proceed with your plan" instruction follows.
- `Inbox.razor` groups pending questions by task/batch: prompt, option chips and a text field per question, one primary action to submit all answers.
- Interplay with Step 9.16: edits an agent made before `ask` must survive the resume (see 9.16 section 3.6).

### 3.5 Task review directive
Update the directive in the standard workflow definitions, the test workflow pipelines and the harness fallback directive so that it states, in order: (1) investigate the task and codebase; (2) mandatory edit of the task specification in `docs/tasks/` with architecture, affected files, plan and acceptance criteria; (3) gather **all** questions and ambiguities into a single `ask` call using the question list, expecting all answers at once; follow-up asks only for newly discovered blockers; (4) no production-code changes in this stage.

---

## 4. Implementation Checklist & Deliverables

- [x] **Git:** stat operation (interface + implementation), shared exclusion constant, cap/empty handling, live-tree variant if applicable.
- [x] **Prompt plumbing:** data bag key, session context property, orchestrator computation, single rendering point, per-file command hint, removal or justification of raw diff.
- [x] **`ask` tool:** question item model, factory registration, validation returned to model, handler flow with numbered pairs, ideation registration disposal.
- [x] **Clean break:** collection-based interruption exception, run result and stage result; delete scalar members and fallback questions.
- [x] **Resumption:** batch ID on `Question` (+ migration), batch submit operation with strict validation and atomic resume, tool-result completion in conversation history, last-human-answer population.
- [x] **UI:** `Inbox.razor` grouping and submit-all.
- [x] **Directives:** standard workflow, test pipelines, harness fallback.
- [x] **Tests**
  - [x] Stat generation (width, exclusions, cap, empty, failure).
  - [x] Prompt contains fenced stat, hint command, no raw diff, stat rendered once.
  - [x] `ask`: empty list and blank prompt produce a model-visible error; valid list produces an interruption with all questions.
  - [x] Interruption/run/stage results preserve all questions and reject empty collections.
  - [x] Ideation: batched questions, numbered answers, cancellation, no leaked registrations.
  - [x] Orchestrator: partial answers keep waiting; full answers resume exactly once; unknown/blank/duplicate answers throw; concurrent submit enqueues once.
  - [x] **Re-ask in the same stage:** only the latest batch is injected.
  - [x] Resume produces a history with the `ask` call answered by a tool result.
  - [x] Inbox bUnit: grouping and submit-all.
- [x] Run the full test suite including E2E, per AGENTS.md.
