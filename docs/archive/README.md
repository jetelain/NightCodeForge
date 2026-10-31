# 🏛️ Completed Planning & Specifications Archive

> **Historical MVP Phases & Completed Post-MVP Steps**  
> *Parent Index:* [docs/PLAN.md](../PLAN.md) | *Active Fixes:* [docs/fixes/README.md](../fixes/README.md) | *Active Features:* [docs/features/README.md](../features/README.md)

---

## 1. Completed MVP & Foundation Phases (Phases 1–10)

| Phase | Archived Specification | Sub-Steps Directory | Focus & Delivered Capabilities |
| :--- | :--- | :--- | :--- |
| **Phase 1** | [01_FOUNDATION_DOMAIN_AND_WORKFLOW.md](01_FOUNDATION_DOMAIN_AND_WORKFLOW.md) | — | .NET 10 solution setup, domain entities, DAG dependency engine, workspace concurrency lock, extensible workflow engine with `PipelineDataBag`, differential quality gates, and `docs/tasks/` markdown sync. |
| **Phase 2** | [02_GIT_SUBSYSTEM_AND_EXT4_MIRROR.md](02_GIT_SUBSYSTEM_AND_EXT4_MIRROR.md) | — | Native Git CLI (`CliWrap`), branch isolation, WIP commits, noise-filtered diffs (`-U2`), working clone at `~/.nightcodeforge/wks/{id}`, remote path translation, and host mirror synchronization. |
| **Phase 3** | [03_QUEUE_AND_ORCHESTRATION_ENGINE.md](03_QUEUE_AND_ORCHESTRATION_ENGINE.md) | — | Hangfire single-worker GPU queue (`WorkerCount = 1`), clean `WaitingForHuman` interruption lifecycle, cascade unblocking when dependencies close, and startup crash recovery (`IStartupRecoveryService`). |
| **Phase 4** | [04_HARNESSES_TOOLS_AND_LOCAL_OPTIMIZATIONS.md](04_HARNESSES_TOOLS_AND_LOCAL_OPTIMIZATIONS.md) | — | Unified harness abstraction, OllamaRouter integration, canonical tools & `EditorConfigFileApplier`, container sandbox sentinel REPL, Crucible / Pi / ACP engines, modular skills & MCP, and the 7 local LLM optimizations. |
| **Phase 4B** | [04B_EXECUTION_ENVIRONMENT_AND_WORKFLOW_ALIGNMENT.md](04B_EXECUTION_ENVIRONMENT_AND_WORKFLOW_ALIGNMENT.md) | — | Unix-only agent enforcement, Windows WSL2 mandatory requirement, Windows Crucible UNC path access, container `/workspace` tool call remapping, 3-channel task intake, manual promotion, and 7-stage main workflow progression. |
| **Phase 5** | [05_BLAZOR_WEB_UI_AND_TELEMETRY.md](05_BLAZOR_WEB_UI_AND_TELEMETRY.md) | [`phase-5/`](phase-5/) (14 steps) | Blazor Interactive Server UI, obsidian/indigo dark theme, workspace provisioning wizard, SignalR telemetry hub, ideation chat, visual DAG backlog, question inbox, review room with diffs, and task execution inspector. |
| **Phase 6** | [06_END_TO_END_TESTING_AND_HARDENING.md](06_END_TO_END_TESTING_AND_HARDENING.md) | — | End-to-end workflow simulation, chaos & sudden reboot resilience, multi-workspace VRAM protection validation, and empirical benchmark validation. |
| **Phase 7** | [07_PLAYWRIGHT_END_TO_END_ACCEPTANCE_TEST_PLAN.md](07_PLAYWRIGHT_END_TO_END_ACCEPTANCE_TEST_PLAN.md) | — | Playwright automated browser test suite driving workspace creation, task ideation, promotion, execution, deterministic gates, and diff review. |
| **Phase 8** | [08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md](08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md) | [`phase-8/`](phase-8/) (8 steps) | Database-persisted `WorkflowDefinition` & `StageDefinition`, dynamic C# stage dispatcher, default 6-stage seeding, and permanent ideation chat persistence in SQLite. |
| **Phase 9** | [09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md](09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md) | [`phase-9/`](phase-9/) (20 steps) | Architectural cleanup aligning with `AGENTS.md`: elimination of optional constructor parameters, hard errors on missing database records, constants/enums, and clean-break pruning. |
| **Phase 10** | [10_MVP_FINAL_POLISH.md](10_MVP_FINAL_POLISH.md) | [`phase-10/`](phase-10/) (4 steps) | MVP Final Polish: EF Core migrations, comprehensive namespace and module README.md documentation hierarchy, overall 360° code review, and interactive Crucible `ask` tool. |

---

## 2. Completed Bug Fixes, Onboarding & Hardening (Phase 11 Archive)

- **Master Archive Summary:** [11_EXTEND_FEATURES.md](11_EXTEND_FEATURES.md)
- **Step Specifications Directory:** [`phase-11/`](phase-11/) (24 completed steps: `11.2`, `11.7`, `11.12`, `11.13`, `11.15`, `11.17`, `11.18`, `11.20`, `11.21`, `11.22`, `11.23`, `11.24`, `11.25`, `11.26`, `11.27`, `11.28`, `11.29`, `11.30`, `11.31`, `11.32`, `11.33`, `11.34`, `11.35`, `11.36`)
- **Remaining Fixes:** Tracked in [`docs/fixes/README.md`](../fixes/README.md)

---

## 3. Completed Post-MVP Features (Phase 12 Archive)

- **Master Archive Summary:** [12_EXTEND_FEATURES.md](12_EXTEND_FEATURES.md)
- **Step Specifications Directory:** [`phase-12/`](phase-12/) (4 completed steps: `12.3`, `12.14`, `12.17`, `12.19`)
- **Remaining Features:** Tracked in [`docs/features/README.md`](../features/README.md)

