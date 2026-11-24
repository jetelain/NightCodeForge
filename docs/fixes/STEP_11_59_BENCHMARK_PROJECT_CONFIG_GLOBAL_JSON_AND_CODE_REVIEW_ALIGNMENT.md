# 🔧 Step 11.59: Benchmark ProjectConfig Propagation, Clean global.json Scoping & Code Review Verdict Alignment

> **Parent Plan:** [docs/fixes/README.md](README.md)  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Proposed  
> **Target Projects:** `NightCodeForge.Benchmark.Cli`, `NightCodeForge.Benchmark.Cli.Tests`, `NightCodeForge.Agents`, `benchmarks/datasets`  
> **Dependencies:** `CrucibleBenchmarkRunner`, `DotNetGlobalJsonResilienceScope`, `BenchmarkVerifier`, `v1-code-review.jsonl`  
> **Aligns With:** `AGENTS.md`, `docs/PLAN.md`

> [!IMPORTANT]
> **Specification Disclaimer:** This document is a behavioral specification. It defines required behaviors, architectural invariants, and verification criteria. Specific internal helper names and implementation details remain at the discretion of the implementer within these constraints.

---

## 1. Context & Motivation

Inspection of the `v1-smoke-Flint-latest-20261124-123321` benchmark run (`18 / 28` completed instances at inspection time) surfaced three benchmark configuration, git-hygiene, and dataset/verdict alignment issues:

1. **Missing `WorkspaceProjectConfig` & Misleading `docs/tasks/` Guidance:**
   - `CrucibleBenchmarkRunner` constructs an ephemeral `Workspace` without supplying a language-accurate `WorkspaceProjectConfig` (`CodeStackPresets.ResolveConfig` returns an empty config when `.nightcodeforge/config.json` does not exist in external repos). As a result, `AgentsMdService.FormatExecutionGuidelines` falls back to `dotnet build` and `dotnet test --no-build` across Python, JavaScript, TypeScript, and Rust repositories—causing the model to literally run `dotnet build` on Flask (`pallets__flask-5014`).
   - Additionally, `BenchmarkStagePolicyResolver` injects the standard `Implementation` stage directive referencing `docs/tasks/` even when no task markdown file exists, leading agents on SWE-bench instances to spend initial turns searching for a non-existent `docs/tasks/` directory.
2. **`DotNetGlobalJsonResilienceScope` Dirties `git status` During Agent Execution:**
   - `DotNetGlobalJsonResilienceScope` relaxes `global.json` `rollForward` in place on disk while the agent runs. Agents inspecting `git status` or `git diff` (observed in `ncf-cr-csharp-001` and `ncf-cr-csharp-003`) notice an unexpected modified `global.json`, spend extra turns investigating it, and risk reverting the relaxation.
3. **Code Review Benchmark Verdict & Tier Boundary Misalignment (`ncf-cr-csharp-001`, `ncf-cr-csharp-006`, `ncf-cr-csharp-008`):**
   - On `ncf-cr-csharp-006` and `ncf-cr-csharp-008`, the model identified 100% of expected findings, fixed the localized single-method bugs, added regression tests, and passed the full test suite—yet failed because `expected_verdict` was `RequestChanges` while the `code-review-fix` stage directive instructs the reviewer to reserve Tier 2 rejection (`request_changes`) for major missing features or architectural redesigns and apply Tier 1 fixes for localized defects.
   - On `ncf-cr-csharp-001` (`expected_verdict: Approve`), the model found zero defects and only wrapped a single long test line for style polish; `BenchmarkVerifier.ClassifyCodeReviewVerdict` treats any non-empty `ModelPatch` as `MinorFix`, resulting in a `0.0` score.

---

## 2. Required Behaviors & Constraints

### 2.1 Language-Accurate Benchmark `WorkspaceProjectConfig` & Scope Directives
- Benchmark execution MUST supply a valid, language-appropriate `WorkspaceProjectConfig` (stack preset, build command, test command, and timeouts) derived from the instance language/stack and resolved test command rather than leaving non-.NET benchmarks to fall back to `dotnet build` / `dotnet test --no-build` in `<execution_guidelines>`.
- For benchmark categories where no `docs/tasks/` specification file exists on disk (such as external `Implementation` and `CodeReview` instances), prompt/scope framing MUST NOT mislead the agent into searching for non-existent `docs/tasks/` files.

### 2.2 Hide `DotNetGlobalJsonResilienceScope` Modifications From `git status` & `git diff`
- When `DotNetGlobalJsonResilienceScope` temporarily relaxes tracked `global.json` files during agent execution, those temporary modifications MUST NOT appear as dirty working-tree changes in `git status` or `git diff` (for example, by marking the modified path assume-unchanged in git's index during the scope and clearing that flag upon disposal, or an equivalent mechanism).
- Disposal MUST restore both the exact original `global.json` file bytes and clean git index state before final patch extraction and verification.

### 2.3 Code Review Verdict & Tier Boundary Alignment
- `v1-code-review.jsonl` `RequestChanges` instances and the `CodeReviewAndFix` stage directive MUST share an unambiguous Tier 1 (`MinorFix`) vs. Tier 2 (`RequestChanges`) contract: either `RequestChanges` benchmark cases represent true Tier 2 rejections (missing core functionality, architectural flaws, broken requirements) or the review criteria explicitly specify when missing regression tests / high-severity defects require `request_changes` rather than direct editing.
- Cosmetic or whitespace-only diffs (such as wrapping a line or adding a trailing newline) on otherwise approved PRs MUST NOT cause false `Approve` -> `MinorFix` failures: either the reviewer prompt explicitly forbids unsolicited cosmetic edits on clean PRs, or verdict classification distinguishes substantive code/test changes from pure whitespace/formatting noise.

---

## 3. Definition of Done (DoD) Checklist

- [ ] Supply a language-accurate `WorkspaceProjectConfig` (build/test commands per stack) to benchmark sessions so non-C# `<execution_guidelines>` never fall back to `dotnet build` / `dotnet test --no-build`, and avoid misleading non-TaskReview runs toward missing `docs/tasks/` paths, with unit tests.
- [ ] Update `DotNetGlobalJsonResilienceScope` so relaxed `global.json` files do not show up as dirty in `git status` / `git diff` during agent execution while restoring exact original bytes and git index state on disposal, with unit tests.
- [ ] Align `v1-code-review.jsonl` `RequestChanges` / `Approve` expectations and `CodeReviewAndFix` verdict/whitespace handling so valid reviews are not penalized by contradictory Tier 1/Tier 2 rules or trivial whitespace edits, with unit tests.
- [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
