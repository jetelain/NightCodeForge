# 🚀 Phase 10: MVP Final Polish, EF Migrations & Codebase Hardening

> **Architectural Specification & Subagent Execution Roadmap**  
> *Parent Plan:* [docs/PLAN.md](../PLAN.md) | *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Target Framework:* .NET 10 (C# 13) | *Status:* Ready for Execution  

---

## 1. Executive Summary & Objective

As **NightCodeForge** approaches its first MVP release, this plan defines the final three essential preparation tracks before packaging and building v1:
1. **Turn on `dotnet ef migrations`:** Move from ad-hoc schema creation (`Database.EnsureCreatedAsync()`) to production-grade EF Core migrations applied automatically at startup and accessible via the `dotnet ef` CLI.
2. **Namespace & Module `README.md` Documentation:** Establish a modular documentation architecture across every project and functional namespace in `src/` to guide both autonomous AI agents and human contributors without polluting LLM context windows.
3. **Comprehensive Code Review & AGENTS.md Policy Remediation:** Execute a multi-stream audit across Core, Infrastructure, Agents, and Web to eliminate bare catch blocks, verify required constructor DI, prevent resource leaks, validate concurrency primitives, and resolve technical debt.

To ensure execution success under LLM context-window constraints, each track is decomposed into **atomic, self-contained subagent tasks** with explicit file boundaries, standardized templates, and concrete verification commands.

---

## 2. Roadmap & Subagent Task Decomposition

```mermaid
flowchart TD
    subgraph Track1 ["Track 1: EF Core Migrations (Step 10.1)"]
        T1_1["Action 1.1: Web CLI & Design Tooling Setup"]
        T1_2["Action 1.2: Runtime Database.MigrateAsync() & PRAGMA Preservation"]
        T1_3["Action 1.3: Migration Integration Tests & Verification"]
        T1_1 --> T1_2 --> T1_3
    end

    subgraph Track2 ["Track 2: Namespace Documentation (Step 10.2)"]
        T2_1["Action 2.1: NightCodeForge.Core READMEs (10 namespaces)"]
        T2_2["Action 2.2: NightCodeForge.Infrastructure READMEs (7 namespaces)"]
        T2_3A["Action 2.3A: Agents Core & Harnesses (7 namespaces)"]
        T2_3B["Action 2.3B: Agents Protocols & Roles (7 namespaces)"]
        T2_4["Action 2.4: Web, ScenarioRunner & Test Architecture"]
    end

    subgraph Track3 ["Track 3: Overall Code Review (Step 10.3)"]
        T3_1["Action 3.1: Strict AGENTS.md Policy Grep & Static Audit"]
        T3_2["Action 3.2: Core & Infrastructure Deep Audit"]
        T3_3["Action 3.3: Agents & Inference Deep Audit"]
        T3_4["Action 3.4: Web Blazor & UI Resilience Audit"]
        T3_5["Action 3.5: Consolidated Fixes & Full Test Hardening"]
        T3_1 --> T3_5
        T3_2 --> T3_5
        T3_3 --> T3_5
        T3_4 --> T3_5
    end

    T1_3 --> Ready["🎉 MVP v1 Build Ready"]
    T2_1 --> Ready
    T2_2 --> Ready
    T2_3A --> Ready
    T2_3B --> Ready
    T2_4 --> Ready
    T3_5 --> Ready
```

---

## 3. Subagent Work Packages

| Step | Specification Document | Subagent Action | Focus & Deliverables | Est. Context | Status |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **10.1** | [STEP_10_1_EF_MIGRATIONS.md](phase-10/STEP_10_1_EF_MIGRATIONS.md) | **Action 1.1** | Add `Microsoft.EntityFrameworkCore.Design` to `NightCodeForge.Web.csproj`, verify `dotnet ef` CLI works seamlessly with both projects. | ~10k tokens | Completed |
| | | **Action 1.2** | Switch `EnsureDatabaseCreatedAndWalAsync` to `Database.MigrateAsync()`, preserve SQLite WAL pragmas and sequence seeding. | ~15k tokens | Completed |
| | | **Action 1.3** | Implement automated migration test in `NightCodeForge.Infrastructure.Tests` verifying `__EFMigrationsHistory` and schema integrity. | ~12k tokens | Completed |
| **10.2** | [STEP_10_2_NAMESPACE_DOCUMENTATION.md](phase-10/STEP_10_2_NAMESPACE_DOCUMENTATION.md) | **Action 2.1** | Document `NightCodeForge.Core` (Project root + 10 namespaces: Concurrency, Dependencies, Domain, Exceptions, Execution, Interfaces, Repositories, Services, Streaming, Workflow). | ~25k tokens | Planned |
| | | **Action 2.2** | Document `NightCodeForge.Infrastructure` (Project root + 7 namespaces: BackgroundJobs, Data, Execution, Git, Hubs, Inference, Migrations). | ~20k tokens | Planned |
| | | **Action 2.3A** | Document `NightCodeForge.Agents` Part A (Root + 7 namespaces: Crucible, Inference, Tools, Dispatcher, Context, Streaming, Transcripts). | ~25k tokens | Planned |
| | | **Action 2.3B** | Document `NightCodeForge.Agents` Part B (7 namespaces: Acp, PiRpc, Mcp, Skills, Optimizations, Ideation, Triage). | ~25k tokens | Planned |
| | | **Action 2.4** | Document `NightCodeForge.Web` (Components, Models, Services), update `ScenarioRunner/README.md`, create `tests/README.md`. | ~25k tokens | Planned |
| **10.3** | [STEP_10_3_OVERALL_CODE_REVIEW_AND_REMEDIATION.md](phase-10/STEP_10_3_OVERALL_CODE_REVIEW_AND_REMEDIATION.md) | **Action 3.1** | AGENTS.md policy audit: bare `catch`, optional constructor DI (`= null`), hard errors on missing DB entities, magic strings/numbers. | ~20k tokens | Planned |
| | | **Action 3.2** | Core & Infrastructure deep audit: DbContext concurrency/lifecycles, lock safety, process disposal, cancellation propagation. | ~25k tokens | Planned |
| | | **Action 3.3** | Agents deep audit: tool sandboxing, prompt caching stability, KV cache preservation, token estimation, streaming error propagation. | ~25k tokens | Planned |
| | | **Action 3.4** | Web UI deep audit: Blazor component disposal, SignalR auto-reconnect & group retention, UI error boundaries, responsive layout. | ~25k tokens | Planned |
| | | **Action 3.5** | Consolidated remediation: execute fixes for all High/Critical findings, run regression suites, verify 0 warnings. | ~30k tokens | Planned |

---

## 4. Policy Alignment & Quality Constraints

All subagents executing tasks under Phase 10 must adhere strictly to repository policies from [AGENTS.md](../../AGENTS.md):
- **Test Command Discipline:** Run non-E2E tests with `dotnet test --filter "Category!=Acceptance&Category!=E2E"`. Run all tests at the end of each major step.
- **Fail Fast DI:** No optional parameters (`= null`, `= default`) in service constructors. Require all dependencies.
- **No Silent Failures:** Bare `catch { }` without logging or justification are forbidden.
- **Clean Break Policy:** Do not maintain legacy shims or backwards-compatibility hacks.
- **Git Safety:** Never execute git mutations (`git commit`, `git add`, etc.) unless explicitly instructed by the user.
