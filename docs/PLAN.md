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
| **Phase 11 (Archive)** | [docs/archive/11_EXTEND_FEATURES.md](archive/11_EXTEND_FEATURES.md) | **Completed (24 steps)** — Real-project onboarding, `ncf` CLI, packaged skills, temporal audit trail, live telemetry console, container worktree portability, fine-grained workspace locking, autonomous conflict resolution, global operations pause, stage cohort chaining, dynamic compaction, and unified Ollama / OllamaRouter / OpenAI-compatible providers. |
| **Phase 12 (Archive)** | [docs/archive/12_EXTEND_FEATURES.md](archive/12_EXTEND_FEATURES.md) | **Completed (4 steps)** — Conversational UI unification & review room ergonomics (12.3), global stack-specific skills & companion tools `csnav`/`mdfetch`/`codecondenser` (12.14), follow-trail auto-scroll (12.17), and air-gapped web app hardening (12.19). |
| **Fixes & Resiliency** | [**docs/fixes/README.md**](fixes/README.md) | **Active** — Remaining bug fixes and resiliency improvements (e.g., Step 11.37: Outage-Resilient LLM Inference, Turn-Level Recovery & Transport Hardening). |
| **Features & Integrations** | [**docs/features/README.md**](features/README.md) | **Active** — Remaining feature roadmap across Tier 1 (intelligent dependencies, task attachments, ideation quick notes & backlog mutation tools), Tier 2 (difficulty escalation, CLI output filtering, DevSkim SAST, diagnostic bash console, SWE benchmark runner), and Tier 3 (mobile PWA, custom scripts, MCP, GitHub, voice, fleet setup, Tailscale, desktop installer, air-gapped search & Envoy egress proxy). |
