# 📊 Step 11.56: Benchmark Timeout Telemetry Preservation, Summary Aggregation & Language-Scoped Skills

> **Parent Plan:** [docs/fixes/README.md](README.md)  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Implemented  
> **Target Projects:** `NightCodeForge.Benchmark.Cli`, `NightCodeForge.Benchmark.Cli.Tests`, `NightCodeForge.Agents`  
> **Dependencies:** `CrucibleBenchmarkRunner`, `BenchmarkSummaryAggregator`, `BenchmarkStagePolicyResolver`  
> **Aligns With:** `AGENTS.md`, `docs/PLAN.md`

> [!IMPORTANT]
> **Specification Disclaimer:** This document is a behavioral specification. It defines required behaviors, architectural invariants, and verification criteria. Specific internal helper names and implementation details remain at the discretion of the implementer within these constraints.

---

## 1. Context & Motivation

In the `v1-smoke-Flint-latest-20261123-214242` benchmark run, four telemetry and prompt-scoping issues distorted metrics and wasted context tokens:

1. **Token Telemetry Loss on Timeout (6/28 instances):** When an instance hits `InstanceTimeoutSeconds` (e.g., `autofac__autofac-1424`, `burntsushi__ripgrep-2576`, `messagepack-csharp__messagepack-csharp-1798`, `ncf-cr-csharp-008`, `ncf-cr-rust-001`, `spectreconsole__spectre-console-1338`), `CrucibleAgentHarness` saves the transcript containing real accumulated token counts (e.g., `6,798,607` tokens in `autofac__autofac-1424.md`) and rethrows `OperationCanceledException`. `CrucibleBenchmarkRunner` catches the timeout and builds `AgentRunResult.Cancelled(..., new AgentRunMetrics(DurationMs: ...))`, zeroing out `PromptTokens`, `CompletionTokens`, `TotalTokens`, and `TokensPerSecond` in `results.json`.
2. **Skewed Summary Averages:** `BenchmarkSummaryAggregator` averages `TotalTokens` and `TokensPerSecond` across all `total_instances`—including `0`-token timeouts and unstarted `0`-turn provisioning errors (`facebook__docusaurus-9897`)—artificially depressing `Avg Tokens` and `Tokens/s` (`23.2` vs ~`30.6` tok/s on completed runs).
3. **Unscoped C# Stack Skills on Non-C# Repositories:** `BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills` enables global stack skills unconditionally across all languages, injecting C#-only skills (`csharp-nav`, `codecondenser`) into Python, JavaScript, TypeScript, and Rust system prompts (`load_skill_calls = 0` across all 28 runs) and wasting system prompt tokens.
4. **Dispatcher Repair Sub-Counter Discrepancy:** In `ncf-tr-py-001`, `results.json` recorded `dispatcher_repairs: 5` while `tool_aliases_resolved: 0`, `parameter_aliases_resolved: 1`, and `stringified_json_repaired: 0`, leaving 4 repair hits unaccounted for in the breakdown fields.

---

## 2. Required Behaviors & Constraints

### 2.1 Preserve Token Metrics on Timeout & Abnormal Termination
- When `CrucibleAgentHarness` is cancelled due to benchmark timeout (or terminates abnormally after making model calls), the real accumulated `PromptTokens`, `CompletionTokens`, and `TotalTokens` MUST be propagated to `CrucibleBenchmarkRunner` (for example via `InMemoryConversationCapture` populated during `SaveTranscriptAsync` or via cancellation-safe metric capture) and recorded on the resulting `BenchmarkInstanceResult`.
- `TokensPerSecond` MUST be computed from the preserved `CompletionTokens` and active agent duration for timed-out instances that generated tokens rather than defaulting to `0`.

### 2.2 Accurate Summary Aggregation
- `BenchmarkSummaryAggregator.AggregateCandidate` MUST compute `AverageTokensPerSecond` (and token throughput summaries) without letting unstarted / zero-token error instances (such as workspace provisioning failures with `0` turns) pull down generation throughput averages.
- Timeout instances whose token metrics are preserved under §2.1 MUST contribute their real token counts and throughput accurately to candidate summaries.

### 2.3 Language-Scoped Stack Skill Injection
- C#-specific stack skills (`csharp-nav`, `codecondenser`) MUST only be enabled/injected when the benchmark instance (or workspace project config) targets C# (`BenchmarkLanguages.CSharp`).
- Non-C# benchmark runs (Python, JavaScript, TypeScript, Rust) MUST NOT have C#-only stack skills injected into their system prompt or `ActiveSkills` list, while preserving `load_skill` behavior for any genuinely applicable non-C# skills if configured.

### 2.4 Dispatcher Repair Telemetry Accounting Consistency
- Every repair counted in `LenientDispatchTelemetrySnapshot.TotalHits` (`ToolCallTelemetryBreakdown.DispatcherRepairs`) MUST be attributed to an explicit category counter in `ToolCallTelemetryBreakdown` so the breakdown counters reconcile with `DispatcherRepairs` (adding any missing repair category such as argument wrapping, coercion, or uncategorized fallback repairs if needed).

---

## 3. Definition of Done (DoD) Checklist

- [x] Propagate real accumulated `PromptTokens`, `CompletionTokens`, `TotalTokens`, and `TokensPerSecond` on benchmark timeout / abnormal termination into `BenchmarkInstanceResult`, with unit tests.
- [x] Update `BenchmarkSummaryAggregator` so unstarted / zero-token error runs do not depress `AverageTokensPerSecond`, with unit tests covering mixed passed/timeout/error result sets.
- [x] Scope C# stack skills (`csharp-nav`, `codecondenser`) to C# workspaces/instances in benchmark policy/skill resolution, with unit tests verifying exclusion on Python/JS/TS/Rust runs.
- [x] Reconcile `DispatcherRepairs` (`TotalHits`) with explicit breakdown counters in `LenientDispatchTelemetry` / `ToolCallTelemetryBreakdown`, with unit tests verifying exact counter reconciliation.
- [x] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
