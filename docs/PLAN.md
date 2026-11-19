# 🗺️ NightCodeForge - Implementation Plan & Roadmap

## 1. Active Roadmaps & Archive Index

NightCodeForge's planning specifications are organized into three areas:

| Area | Index Document | Directory | Scope |
| :--- | :--- | :--- | :--- |
| 🛡️ **Bug Fixes & Resiliency** | [**docs/fixes/README.md**](fixes/README.md) | [`docs/fixes/`](fixes/) | Active bug fixes, system hardening, and resilience improvements (remaining steps from Phase 11). |
| 🚀 **Features & Integrations** | [**docs/features/README.md**](features/README.md) | [`docs/features/`](features/) | Active new features, developer ergonomics, and ecosystem integrations (remaining steps from Phase 12). |
| 🏛️ **Completed Archive** | [**docs/archive/README.md**](archive/README.md) | [`docs/archive/`](archive/) | Completed MVP phases (Phases 1–10) and all completed Phase 11 & Phase 12 step specifications. |

### 🔥 `v0.1.0` Parallel Execution Plan (4 Streams)

To accelerate delivery of **`v0.1.0`**, active work is organized into four streams (see [docs/features/README.md](features/README.md) for full details):

| Stream | Focus | Included Steps | Parallelism |
| :--- | :--- | :--- | :--- |
| **1. Common Trunk** | **Stabilize Internals** (`Core`, `Agents`, `Orchestrator`, `config/config.json`) | [12.31](features/STEP_12_31_INTERACTIVE_IDEATION_AGENT_COORDINATOR_AND_WARM_EXECUTION_CONTEXT.md) *(Next in Line)*, [12.2D](features/STEP_12_2D_MULTIMODAL_VISION_AND_IN_LOOP_IMAGE_INSPECTION.md), [12.39](features/STEP_12_39_TWO_TIER_AGENT_MEMORY_MANAGEMENT_AND_UI_NOTIFICATIONS.md) *(+ opportunistic [11.49B](fixes/STEP_11_49B_RETIRE_OLLAMASHARP_STREAM_CANCELLATION_WORKAROUND.md))* | Trunk (freeze APIs) |
| **2. Installer** | **Avalonia OOBE Wizard & Atomic Updater** | [12.18A](features/STEP_12_18A_INSTALLER_CORE_HARDWARE_RELEASE_AND_PROVISIONER.md), [12.18B](features/STEP_12_18B_INSTALLER_OLLAMA_MANAGER_AND_128K_MICRO_BENCHMARK.md), [12.18C](features/STEP_12_18C_INSTALLER_AVALONIA_OOBE_WIZARD_AND_MAINTENANCE_HUB.md) | Parallel |
| **3. Benchmark** | **Standalone CLI** (`Agents` + mock DB for Implementation, Code Review, Task Review) | [12.23A](features/STEP_12_23A_CRUCIBLE_BENCHMARK_ENGINE_AND_CONTAINER_VERIFIER.md), [12.23B](features/STEP_12_23B_BENCHMARK_CLI_AND_LEADERBOARD_WEB_UI.md) | Parallel |
| **4. `v0.1` Features** | **Onboarding & Diagnostic UI** | [12.32A](features/STEP_12_32A_WORKSPACE_WIZARD_REPO_DISCOVERY_AND_REMOTE_PROJECT_PICKER.md), [12.32B](features/STEP_12_32B_WORKSPACE_TEMPLATES_NATIVE_CLI_SCAFFOLDING_AND_REMOTE_PUBLISH.md), [12.4](features/STEP_12_4_IDEATION_QUICK_NOTES_SUGGESTIONS_AND_DRAFT_PRUNING.md), [12.22A](features/STEP_12_22A_DIAGNOSTIC_SHELL_SERVICE_AND_CONTAINER_SIGINT_RECOVERY.md), [12.22B](features/STEP_12_22B_DIAGNOSTIC_BASH_CONSOLE_WEB_UI.md) *(+ optional [12.7](features/STEP_12_7_DEVSKIM_SAST_QUALITY_GATE.md), [12.9](features/STEP_12_9_CUSTOM_SCRIPTS_FOR_DETERMINISTIC_STEPS.md))* | Parallel |

*(Deferred to **Post-`v0.1.0` / `v0.2`**: SLM delegating clients `11.43C`, MCP `12.10A/B`, Voice `12.12`, `mdfetch` config `12.20B`, Envoy `12.21A/B`, DiffGuard `12.28A/B`, and Nightwatch `12.34A/B/C` + `12.35B`.)*

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
| **NightCodeForge.Infrastructure** | `CliWrap`<br>`Hangfire.AspNetCore`<br>`Hangfire.Storage.SQLite`<br>`Microsoft.EntityFrameworkCore.Sqlite`<br>`Microsoft.AspNetCore.SignalR` | Native Git CLI execution, stage-priority background queue (`WorkerCount = 1` by default, scaling to `N` concurrent workers via `MaxConcurrentInferenceTasks`), database persistence & real-time telemetry |
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
| **Phase 11 (Archive)** | [docs/archive/11_EXTEND_FEATURES.md](archive/11_EXTEND_FEATURES.md) | **Completed (47 steps)** — Real-project onboarding, `ncf` CLI, packaged skills, temporal audit trail, live telemetry console, container worktree portability, fine-grained workspace locking, autonomous conflict resolution, global operations pause, stage cohort chaining, dynamic compaction, unified Ollama / OllamaRouter / OpenAI-compatible providers, pre-refactoring characterization & constructor ceiling guard (11.38A), reusable Playwright E2E fixture & baseline suites (11.39A, 11.39B), in-container process reaper & WSL self-healing (11.40), HTTP transport outage resiliency (11.37A), external harness base & ACP host RPC (11.38B), agent session & toolset factorization (11.38C), orchestrator decomposition (11.38D), task review specification enforcement (11.41), stream continuation & empty-diff gates (11.42), Pmad.AI guardrails & tool dispatcher (11.43A, 11.43B), web search capping & timeout retry (11.44), measured-token compaction (11.45), live question inbox & review badge synchronization (11.46), persistent compaction checkpoints & correlation telemetry (11.47), large-context timeout recovery compaction (11.48), two-phase streaming idle watchdog & token cap (11.49), OpenAI streaming tool-call deltas & 10m buffering watchdog (11.49C), task disk sync coordinator performance & WSL parallelization (11.50), removing implicit `propose_tasks` sequential dependencies (11.51), ideation proposal view sync on agent updates (11.52), and scheduling `Open` tasks on dependency removal (11.53). |
| **Phase 12 (Archive)** | [docs/archive/12_EXTEND_FEATURES.md](archive/12_EXTEND_FEATURES.md) | **Completed (38 steps)** — Attachments store, intake, `Pmad.Markitdown` conversion, `/workspace/attachments` read/bash access & Web UI (`12.2A`, `12.2B`, `12.2C`), conversational UI unification & review room ergonomics (`12.3`), Antigravity ACP harness catalog & container rework with `PiRpc` removal (`12.5A`), task difficulty & role-based escalation matrix (`12.5B`), smart CLI & build tool output filtering (`12.6`), global stack-specific skills & companion tools `csnav`/`mdfetch`/`codecondenser` (`12.14`), follow-trail auto-scroll (`12.17`), air-gapped web app hardening (`12.19`), backlog context digest (`12.1A`), dynamic task suspension on unclosed prerequisites & UI blocker badges (`12.1B`), mobile layout & touch ergonomics (`12.8A`), installable PWA shell (`12.8B`), web worker notifications (`12.8C`), GitHub App Device Flow, KeySharp OS keyring & settings UI (`12.11A`), GitHub outbound PR delivery (`12.11B`), GitHub untrusted issue intake & triage (`12.11C`), GitHub bi-directional task mirroring & PR-merge closure (`12.11D`), ideation backlog mutation tools & prompt policy (`12.13A`), refine in ideation workflow (`12.13B`), Tailscale serve connectivity (`12.16A`), Tailscale settings & QR code (`12.16B`), air-gapped web search deactivation (`12.20A`), home page pending code reviews (`12.24`), $N$-worker concurrent inference & multi-task telemetry (`12.25A`), multi-worker telemetry UI & concurrency settings (`12.25B`), workspace visual identity & emoji (`12.26`), dynamic Ollama thinking effort discovery & mapping (`12.27`), smart OllamaRouter model preload & context-intent warmup (`12.29`), tasks QoL highlights, 7-day cutoff, all-tasks list & inspector ordering (`12.30`), OllamaRouter session affinity release & cost collection engine (`12.33A`), task estimated cost telemetry & UI presentation (`12.33B`), deterministic transcript friction categorization & recent-session windowing (`12.35A`), declarative sandbox mount table & container lifecycle redesign (`12.36A`), `CanonicalTools` modular decomposition & unified interruption pipeline (`12.36B`), companion API key store & NightCode Assay handoff endpoints (`12.37`), and triage task lifecycle, verbatim intake persistence & scoped backlog mutations (`12.38`). |
| **Fixes & Resiliency** | [**docs/fixes/README.md**](fixes/README.md) | **Active** — Remaining bug fixes, resiliency, and code factorization steps (Step 11.43C: Pmad.AI Opt-In SLM Delegating Chat Clients; Step 11.49B: Retire `CancellationAwareStream` Workaround Upon `OllamaSharp` PR #400 Upgrade). |
| **Features & Integrations** | [**docs/features/README.md**](features/README.md) | **Active** — **Next in Line:** Interactive ideation agent coordinator (`12.31`); **Tier 2:** Quick notes (`12.4`), multimodal vision (`12.2D`), DevSkim SAST quality gate (`12.7`), diagnostic shell (`12.22A/B`), benchmarks (`12.23A/B`), DiffGuard security scanner (`12.28A/B`), workspace wizard templates (`12.32A/B`), autonomous Nightwatch & Harness Issue ledger (`12.34A/B/C`), idle-GPU Nightwatch deep analysis (`12.35B`), and two-tier agent memory management (`12.39`); **Tier 3:** Custom scripts (`12.9`), MCP (`12.10A/B`), voice (`12.12`), installer & OllamaRouter wizard integration (`12.18A/B/C`), and egress proxy (`12.20B`, `12.21A/B`). |
