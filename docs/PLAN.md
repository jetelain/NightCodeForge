# 🗺️ NightCodeForge - Implementation Plan & Roadmap

## 1. Active Roadmaps & Archive Index

NightCodeForge's planning specifications are organized into three areas:

| Area | Index Document | Directory | Scope |
| :--- | :--- | :--- | :--- |
| 🛡️ **Bug Fixes & Resiliency** | [**docs/fixes/README.md**](fixes/README.md) | [`docs/fixes/`](fixes/) | Active bug fixes, system hardening, and resilience improvements (remaining steps from Phase 11). |
| 🚀 **Features & Integrations** | [**docs/features/README.md**](features/README.md) | [`docs/features/`](features/) | Active new features, developer ergonomics, and ecosystem integrations (remaining steps from Phase 12). |
| 🏛️ **Completed Archive** | [**docs/archive/README.md**](archive/README.md) | [`docs/archive/`](archive/) | Completed MVP phases (Phases 1–10) and all completed Phase 11 & Phase 12 step specifications. |

---

## 2. Project Directory & Solution Layout

The solution is organized using clean architecture principles, separating core domain models, workflow pipeline, infrastructure, AI harnesses, and the interactive Blazor UI.

```text
NightCodeForge/
├── docs/
│   ├── ARCHITECTURE.md          # Complete architectural specification of Forge orchestrator
│   ├── CRUCIBLE.md              # Native Crucible agent harness specification
│   ├── PI.md                    # Pi headless JSONL RPC daemon protocol & integration
│   ├── ACP.md                   # Agent Client Protocol (ACP) specification & modernization
│   ├── LOCAL_LLM_OPTIMIZATIONS.md # High-value local LLM optimization blueprint (<think> pruning, loop breakers, symbol index)
│   ├── REFERENCES.md            # Source code mapping to MS Agent Framework, Pi & ACP
│   ├── PLAN.md                  # Master roadmap & executive index
│   ├── fixes/                   # Active bug fixes, hardening & resiliency specifications:
│   │   ├── README.md            # Remaining bug fixes & resiliency roadmap
│   │   └── STEP_11_*.md         # Remaining fix specifications
│   ├── features/                # Active new feature & integration specifications:
│   │   ├── README.md            # Remaining features & integrations roadmap
│   │   └── STEP_12_*.md         # Remaining feature specifications
│   └── archive/                 # Completed phase plans and finished step specifications:
│       ├── README.md            # Archive index
│       ├── 01_FOUNDATION_DOMAIN_AND_WORKFLOW.md .. 12_EXTEND_FEATURES.md
│       ├── phase-5/ .. phase-10/
│       ├── phase-11/            # Completed Phase 11 fix/hardening steps
│       └── phase-12/            # Completed Phase 12 feature steps
├── src/
│   ├── NightCodeForge.Core/
│   ├── NightCodeForge.Infrastructure/
│   ├── NightCodeForge.Agents/
│   ├── NightCodeForge.Cli/
│   └── NightCodeForge.Web/
└── tests/
    ├── NightCodeForge.Core.Tests/
    ├── NightCodeForge.Infrastructure.Tests/
    ├── NightCodeForge.Agents.Tests/
    ├── NightCodeForge.Cli.Tests/
    ├── NightCodeForge.Web.Tests/
    └── NightCodeForge.Acceptance.Tests/
```

---

## 3. Technology Stack & NuGet Dependencies

| Project | Key Dependencies | Purpose |
| :--- | :--- | :--- |
| **NightCodeForge.Core** | *No external heavy dependencies* (Clean pure C#) | Domain models, extensible workflow engine, DAG dependency resolution, resilience primitives |
| **NightCodeForge.Infrastructure** | `CliWrap`<br>`Hangfire.AspNetCore`<br>`Hangfire.Storage.SQLite`<br>`Microsoft.EntityFrameworkCore.Sqlite`<br>`Microsoft.AspNetCore.SignalR` | Native Git CLI execution, stage-priority background queue (`WorkerCount = 1`), database persistence & real-time telemetry |
| **NightCodeForge.Agents** | `Microsoft.Agents.AI.Harness`<br>`Microsoft.Extensions.AI`<br>`OllamaSharp`<br>`Microsoft.Extensions.AI.OpenAI`<br>`StreamJsonRpc` | Crucible, Pi & ACP agent harnesses, canonical tools, context compaction, skills & MCP |
| **NightCodeForge.Cli** | `System.CommandLine` | `ncf` global CLI tool (`ncf init`, `ncf audit`) for deterministic repository onboarding |
| **NightCodeForge.Web** | `Microsoft.AspNetCore.Components.Web`<br>`DiffPlex` | Reactive Blazor Interactive Server UI, live operations telemetry, side-by-side Git diff rendering |
| **NightCodeForge.Tests** | `xunit`<br>`Moq`<br>`bunit`<br>`Microsoft.Playwright` | Automated unit, integration, component, and end-to-end acceptance testing |

---

## 4. Completed Phases & Active Roadmaps Summary

| Phase / Track | Guide / Index | Status & Key Deliverables |
| :--- | :--- | :--- |
| **Phase 1** | [docs/archive/01_FOUNDATION_DOMAIN_AND_WORKFLOW.md](archive/01_FOUNDATION_DOMAIN_AND_WORKFLOW.md) | **Completed** — .NET 10 solution setup, domain entities, DAG dependency engine, workspace concurrency lock, extensible workflow engine with `PipelineDataBag`, differential quality gates, and `docs/tasks/` markdown sync. |
| **Phase 2** | [docs/archive/02_GIT_SUBSYSTEM_AND_EXT4_MIRROR.md](archive/02_GIT_SUBSYSTEM_AND_EXT4_MIRROR.md) | **Completed** — Native Git CLI (`CliWrap`), branch isolation, WIP commits, noise-filtered diffs (`-U2`), working clone at `~/.nightcodeforge/wks/{id}`, remote path translation, and host mirror synchronization. |
| **Phase 3** | [docs/archive/03_QUEUE_AND_ORCHESTRATION_ENGINE.md](archive/03_QUEUE_AND_ORCHESTRATION_ENGINE.md) | **Completed** — Hangfire single-worker GPU queue (`WorkerCount = 1`), clean `WaitingForHuman` interruption lifecycle, cascade unblocking when dependencies close, and startup crash recovery (`IStartupRecoveryService`). |
| **Phase 4** | [docs/archive/04_HARNESSES_TOOLS_AND_LOCAL_OPTIMIZATIONS.md](archive/04_HARNESSES_TOOLS_AND_LOCAL_OPTIMIZATIONS.md) | **Completed** — Unified harness abstraction, OllamaRouter integration, canonical tools & `EditorConfigFileApplier`, container sandbox sentinel REPL, Crucible / Pi / ACP engines, modular skills & MCP, and local LLM optimizations. |
| **Phase 4B** | [docs/archive/04B_EXECUTION_ENVIRONMENT_AND_WORKFLOW_ALIGNMENT.md](archive/04B_EXECUTION_ENVIRONMENT_AND_WORKFLOW_ALIGNMENT.md) | **Completed** — Unix-only agent enforcement, Windows WSL2 mandatory requirement, Windows Crucible UNC path access, container `/workspace` tool call remapping, 3-channel task intake, manual promotion, and 7-stage main workflow progression. |
| **Phase 5** | [docs/archive/05_BLAZOR_WEB_UI_AND_TELEMETRY.md](archive/05_BLAZOR_WEB_UI_AND_TELEMETRY.md) | **Completed** — Blazor Interactive Server UI, obsidian/indigo dark theme, workspace provisioning wizard, SignalR telemetry hub, ideation chat, visual DAG backlog, question inbox, review room with diffs, and task execution inspector. |
| **Phase 6** | [docs/archive/06_END_TO_END_TESTING_AND_HARDENING.md](archive/06_END_TO_END_TESTING_AND_HARDENING.md) | **Completed** — End-to-end workflow simulation, chaos & sudden reboot resilience, multi-workspace VRAM protection validation, and empirical benchmark validation. |
| **Phase 7** | [docs/archive/07_PLAYWRIGHT_END_TO_END_ACCEPTANCE_TEST_PLAN.md](archive/07_PLAYWRIGHT_END_TO_END_ACCEPTANCE_TEST_PLAN.md) | **Completed** — Playwright automated browser test suite driving workspace creation, task ideation, promotion, execution, deterministic gates, and diff review. |
| **Phase 8** | [docs/archive/08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md](archive/08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md) | **Completed** — Database-persisted `WorkflowDefinition` & `StageDefinition`, dynamic C# stage dispatcher, default 6-stage seeding, and permanent ideation chat persistence in SQLite. |
| **Phase 9** | [docs/archive/09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md](archive/09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md) | **Completed** — Architectural cleanup aligning with `AGENTS.md`: elimination of optional constructor parameters, hard errors on missing database records, constants/enums, and clean-break pruning. |
| **Phase 10** | [docs/archive/10_MVP_FINAL_POLISH.md](archive/10_MVP_FINAL_POLISH.md) | **Completed** — MVP Final Polish: EF Core migrations, comprehensive namespace and module README.md documentation hierarchy, overall 360° code review, and interactive Crucible `ask` tool. |
| **Phase 11 (Archive)** | [docs/archive/11_EXTEND_FEATURES.md](archive/11_EXTEND_FEATURES.md) | **Completed (37 steps)** — Real-project onboarding, `ncf` CLI, packaged skills, temporal audit trail, live telemetry console, container worktree portability, fine-grained workspace locking, autonomous conflict resolution, global operations pause, stage cohort chaining, dynamic compaction, unified Ollama / OllamaRouter / OpenAI-compatible providers, pre-refactoring characterization & constructor ceiling guard (11.38A), reusable Playwright E2E fixture & baseline suites (11.39A, 11.39B), HTTP transport outage resiliency (11.37A), external harness base & ACP host RPC (11.38B), agent session & toolset factorization (11.38C), orchestrator decomposition (11.38D), task review specification enforcement (11.41), stream continuation & empty-diff gates (11.42), Pmad.AI guardrails & tool dispatcher (11.43A, 11.43B), web search capping & timeout retry (11.44), and measured-token compaction (11.45). |
| **Phase 12 (Archive)** | [docs/archive/12_EXTEND_FEATURES.md](archive/12_EXTEND_FEATURES.md) | **Completed (11 steps)** — Conversational UI unification & review room ergonomics (12.3), global stack-specific skills & companion tools `csnav`/`mdfetch`/`codecondenser` (12.14), follow-trail auto-scroll (12.17), air-gapped web app hardening (12.19), backlog context digest (12.1A), mobile layout & touch ergonomics (12.8A), ideation backlog mutation tools & prompt policy (12.13A), refine in ideation workflow (12.13B), Tailscale serve connectivity (12.16A), home page pending code reviews (12.24), and workspace visual identity & emoji (12.26). |
| **Fixes & Resiliency** | [**docs/fixes/README.md**](fixes/README.md) | **Active** — Remaining bug fixes, resiliency, and code factorization steps (Step 11.40: Bash Timeout Container Process Reaper & WSL2 Recovery; Step 11.43C: Pmad.AI Opt-In SLM Delegating Chat Clients). |
| **Features & Integrations** | [**docs/features/README.md**](features/README.md) | **Active** — **Tier 2 Focus:** $N$-worker concurrent inference (`12.25A/B`), prerequisite suspension (`12.1B`), quick notes (`12.4`), attachments (`12.2A/B/C`), quality gates, benchmarks & rogue-model security guardrails (`12.6`, `12.7`, `12.22A/B`, `12.23A/B`, `12.28A/B/C`), dynamic Ollama thinking effort (`12.27`), smart router model preload & context intent (`12.29`); **Tier 3:** Post-MVP mobile PWA/notifications (`12.8B/C`), Tailscale settings UI/QR (`12.16B`), MCP (`12.10A/B`), GitHub (`12.11A/B`), voice (`12.12`), deep OllamaRouter fleet provisioning (`12.15`), installer (`12.18A/B/C`), and egress proxy (`12.20A/B`, `12.21A/B`). |
