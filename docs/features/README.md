# 🚀 Features & Integrations Roadmap

> **Active Feature & Integration Specifications**  
> *Parent Index:* [docs/PLAN.md](../PLAN.md) | *Completed Features Archive:* [docs/archive/12_EXTEND_FEATURES.md](../archive/12_EXTEND_FEATURES.md) (`docs/archive/phase-12/`)  
> *Target Framework:* .NET 10 (C# 13)

---

## 1. Overview & Strategic Prioritization

This directory contains the active technical specifications for remaining **new features, developer ergonomics, and ecosystem integrations** (historically tracked under Phase 12).

Once a feature step is implemented and verified, its specification document is moved to [`docs/archive/phase-12/`](../archive/phase-12/) and recorded in [`docs/archive/12_EXTEND_FEATURES.md`](../archive/12_EXTEND_FEATURES.md).

The remaining feature steps are organized into three delivery tiers:

1. **Tier 1: Real-Project Backlog Management & Dependencies (Top Priority):**
   - **Step 12.1:** Intelligent Task Dependency Detection in Ideation & Review Agents — lightweight backlog context injection, DAG relationship inference, cycle rejection, and prerequisite enforcement.
   - **Step 12.2:** Task Attachments & Context Documents — workspace-level read-only `attachments/` store populated from file drops, host path symlinks, and URL captures (MarkItDown companions), citable by ideation (`@attachment:{id}`) and readable by workflow stage agents.
   - **Step 12.4:** Ideation Quick Notes, Suggestions Backlog & Conversational Refinement — quick notes backlog intake ("Think Now, Chat Later"); proposal card lifecycle (`Approve` $\rightarrow$ Open, `Keep as Suggestion` $\rightarrow$ Suggested, `Discard` $\rightarrow$ Delete); "Refine in Ideation" pre-seeded chat; 24h draft pruning.
   - **Step 12.13:** Conversational Backlog Management, Task Refinement & Scoped Mutation Tools in Ideation — enabling the ideation agent to inspect existing tasks (`get_task`), enrich details and scope (`edit_task`), rework priorities (`set_task_priority`, `rework_priorities`), and remove unstarted tasks (`remove_task`) with DAG cycle checks, audit attribution, and markdown synchronization.

2. **Tier 2: Intelligence, Efficiency & Quality Gates:**
   - **Step 12.5:** Task Difficulty Classification & Difficulty-Aware Model Escalation — building on the dual-model profile generation and dynamic context discovery delivered in Steps 11.33 & 11.35 to add `TaskDifficulty` (`Standard`, `Difficult`, `Expert`), automatic escalation of `Implementation` to the Architect/Reviewer model with `High` thinking effort on difficult/expert tasks, and difficulty-aware JIT preload lookahead.
   - **Step 12.6:** Smart CLI & Build Tool Output Filtering (Context Compression) — compiler and test noise suppression for 95% token savings, workspace path relativization, and preserving raw transcripts on disk.
   - **Step 12.7:** Free & Open-Source SAST Quality Gate via Microsoft DevSkim CLI — deterministic SAST quality gate using Microsoft DevSkim CLI across C#, JS/TS, Python, Go; SARIF report parsing; and automatic loopback routing with diagnostics.
   - **Step 12.22:** Interactive Container & Execution Environment Diagnostic Bash Console — dedicated Web UI page (`/diagnostics/shell`) executing `bash` commands inside a workspace or task container using the exact same `CanonicalTools.BashAsync` and `NightCodeForgeContainerExecutor` pipeline as an agent, with live mount/drift inspection, 1-click diagnostic presets, and an automated "Environment Doctor" checklist.
   - **Step 12.23:** Crucible Benchmark Runner for SWE-Sharp-Bench & Multi-Model Evaluation — standalone single-shot Crucible benchmark runner to evaluate SWE-Sharp-Bench, SWE-bench, or custom JSONL datasets across candidate models, quantizations, thinking efforts, and `LocalOptimizationFlags`, with `predictions.jsonl` generation, containerized `fail_to_pass`/`pass_to_pass` verification, `ncf benchmark` CLI, and a `/benchmarks` Leaderboard UI.

3. **Tier 3: Deep Integrations & Remote Experience:**
   - **Step 12.8:** Mobile-Responsive Web UI, PWA & Web Worker Notifications — off-canvas hamburger navigation drawer, touch targets $\ge 44\text{px}$, bottom sheet drawer, installable Progressive Web App (`manifest.webmanifest`, `service-worker.js`), and browser web notifications for human intervention and task merges.
   - **Step 12.9:** Custom Repository Scripts for Deterministic Steps (Bash, Python, PowerShell) — execution of custom repository scripts in `.nightcodeforge/scripts/` during deterministic stages with `PipelineDataBag` environment variable injection.
   - **Step 12.10:** Model Context Protocol (MCP) Integration: HTTP Servers & ContainerSandbox Stdio — first-class MCP tool discovery and execution via `ModelContextProtocol`, supporting streamable HTTP/SSE remote servers and isolated stdio MCP servers in `ContainerSandbox`.
   - **Step 12.11:** GitHub Integration: Untrusted Intake Triage, PR Delivery & Task Mirroring — untrusted issue intake routed to sandboxed triage to generate proposals, Pull Request delivery mode, and bi-directional status mirroring.
   - **Step 12.12:** Voice Support, Continuous Dictation & Hands-Free Operations — hands-free voice operations via Web Speech API (`SpeechRecognition`) and Blazor JS Interop, continuous dictation with 1500ms silence detection, and visual audio feedback.
   - **Step 12.15:** Deep OllamaRouter Fleet Provisioning & In-App Setup — in-app fleet management UI (`/settings` -> OllamaRouter tab) enabling developers to add, test, configure, and remove LAN compute nodes, pre-flight connectivity testing, and Cloud Overflow API key management.
   - **Step 12.16:** Remote Tailscale Mesh Access, HTTPS & Transient Network Resilience — zero-firewall private mesh VPN access, stable service identity (`svc:ncf`), automated MagicDNS Let's Encrypt TLS termination via `TailscaleServeHostedService`, and transient network reconnection handling.
   - **Step 12.18:** Cross-Platform Desktop Installer, Updater & Setup Wizard (Avalonia UI) — standalone single-file desktop application (`NightCodeForge.Installer`) for Windows, Linux, and macOS delivering a zero-clone OOBE, automated setup micro-benchmark (`num_ctx >= 131,072`), and maintenance/updater hub.
   - **Step 12.20:** Air-Gapped Search Deactivation & MarkdownFetch Documentation Governance — automatic deactivation of `web_search` in Air-Gapped Mode, `AirGappedOperationException` guardrails, and in-app `mdfetch` URL whitelist & SSRF governance.
   - **Step 12.21:** Container Sandbox Egress Filtering & Envoy Proxy — Istio-style transparent container egress redirection using Linux `iptables` and shared network namespaces (`--net=container:ncf-envoy-task<id>`) with Envoy TLS SNI domain filtering.

---

## 2. Remaining Feature Steps

### Priority Tier 1: Real-Project Backlog Management & Dependencies (Top Priority)

| Step | Specification | Focus & Key Deliverables | Status |
| :--- | :--- | :--- | :--- |
| **12.1** | [🔗 Step 12.1: Intelligent Task Dependency Detection in Ideation & Review Agents](STEP_12_1_INTELLIGENT_TASK_DEPENDENCY_DETECTION.md) | Backlog context injection for Ideation and Stage 1 Review agents; system prompts requiring identification of prerequisites, shared interfaces, and ordering; auto-population of `dependsOnTitles` and DAG dependency chips in Kanban and Drawer. | **Top Priority** |
| **12.2** | [📎 Step 12.2: Task Attachments & Context Documents](STEP_12_2_TASK_ATTACHMENTS_AND_CONTEXT_DOCS.md) | Workspace-level read-only `attachments/` directory (sibling of clone & worktrees): intake via file drop, system host filenames mapped as symlinks, and URL captures with Pmad.Markitdown markdown companions; SHA-256 dedup & reusability; `@attachment:{id}` citations in tasks; read-only `list_attachments`/`read_attachment` tools for ideation and the three workflow stage agents; attachments picker on `/tasks/new`. | **Top Priority** |
| **12.4** | [💡 Step 12.4: Ideation Quick Notes, Suggestions & Conversational Refinement](STEP_12_4_IDEATION_QUICK_NOTES_SUGGESTIONS_AND_REFINEMENT.md) | Quick notes backlog intake ("Think Now, Chat Later"); proposal card lifecycle (`Approve` $\rightarrow$ Open, `Keep as Suggestion` $\rightarrow$ Suggested, `Discard` $\rightarrow$ Delete); "Refine in Ideation" pre-seeded chat; scoped `edit_task` tools; 24h draft pruning. | Planned |
| **12.13** | [📋 Step 12.13: Conversational Backlog Management, Task Refinement & Scoped Mutation Tools in Ideation](STEP_12_13_IDEATION_BACKLOG_MANAGEMENT_AND_TASK_MUTATION_TOOLS.md) | Equips read-only ideation agent with scoped host-level tools (`get_task`, `edit_task`, `set_task_priority`, `rework_priorities`, `remove_task`) to inspect, refine details/sub-tasks, rework priorities, and remove unstarted tasks (`Draft`, `Suggested`, `Open`) while enforcing read-only workspace safety, DAG cycle validation, audit logging, and markdown disk sync. | Planned |

### Priority Tier 2: Intelligence, Efficiency & Quality Gates

| Step | Specification | Focus & Key Deliverables | Status |
| :--- | :--- | :--- | :--- |
| **12.5** | [🧠 Step 12.5: Task Difficulty Classification & Difficulty-Aware Model Escalation](STEP_12_5_DUAL_MODEL_ROLE_CONFIGURATION_AND_DIFFICULTY_ESCALATION.md) | Builds on Steps 11.33 & 11.35 dual-model foundations: introduces `TaskDifficulty` (`Standard`, `Difficult`, `Expert`), dynamic escalation of `Implementation` to the Architect/Reviewer model (`ThinkingEffort >= High`) for difficult/expert tasks, difficulty-aware JIT preload lookahead, and UI difficulty badges & role grouping. | Partially Implemented |
| **12.6** | [🧹 Step 12.6: Smart CLI & Build Tool Output Filtering (Context Compression)](STEP_12_6_SMART_CLI_AND_BASH_OUTPUT_FILTERING.md) | Semantic output filtering for verbose CLI tools (`dotnet build`, `dotnet test`, `npm`). Relativizes paths, strips SDK boilerplates, drops non-fatal warnings when errors exist, preserving raw transcripts on disk. | Planned |
| **12.7** | [🛡️ Step 12.7: Free & Open-Source SAST Quality Gate via Microsoft DevSkim CLI](STEP_12_7_DEVSKIM_SAST_QUALITY_GATE.md) | Deterministic SAST quality gate using Microsoft DevSkim CLI across C#, JS/TS, Python, Go. SARIF report parsing, configurable severity thresholds, and automatic loopback routing with diagnostics. | Planned |
| **12.22** | [🖥️ Step 12.22: Interactive Container & Execution Environment Diagnostic Bash Console](STEP_12_22_CONTAINER_DIAGNOSTIC_BASH_CONSOLE.md) | Interactive `/diagnostics/shell` page executing `bash` commands in workspace or task containers via `CanonicalTools.BashAsync` / `IExecutionEnvironment`, live mount & drift inspector, container reset action, 1-click diagnostic presets, and automated "Environment Doctor" checks. | Planned |
| **12.23** | [🏁 Step 12.23: Crucible Benchmark Runner for SWE-Sharp-Bench & Multi-Model Evaluation](STEP_12_23_CRUCIBLE_SWE_BENCHMARK_RUNNER.md) | Direct single-shot Crucible benchmark runner for SWE-Sharp-Bench / SWE-bench datasets (without the full multi-stage workflow), multi-model candidate matrix evaluation, standard `predictions.jsonl` export, built-in containerized `fail_to_pass`/`pass_to_pass` verification, `ncf benchmark` CLI, and `/benchmarks` Leaderboard & Diff Comparator UI. | Planned |

### Priority Tier 3: Deep Integrations & Remote Experience

| Step | Specification | Focus & Key Deliverables | Status |
| :--- | :--- | :--- | :--- |
| **12.8** | [📱 Step 12.8: Mobile-Responsive Web UI, PWA & Web Worker Notifications](STEP_12_8_MOBILE_RESPONSIVE_UI_AND_PWA.md) | First-class mobile/tablet responsiveness: off-canvas hamburger navigation drawer, touch targets $\ge 44\text{px}$, fixed-bottom ideation chat, mobile bottom sheet drawer, installable PWA (`manifest.webmanifest`, `service-worker.js`), and browser web notifications with per-subject user preferences. | Planned |
| **12.9** | [📜 Step 12.9: Custom Repository Scripts for Deterministic Steps (Bash, Python, PowerShell)](STEP_12_9_CUSTOM_SCRIPTS_FOR_DETERMINISTIC_STEPS.md) | Execution of custom repository scripts in `.nightcodeforge/scripts/` during deterministic stages across Bash, Python, and PowerShell. Injection of `PipelineDataBag` environment variables and output directives. | Planned |
| **12.10** | [⚡ Step 12.10: Model Context Protocol (MCP) Integration: HTTP Servers & ContainerSandbox Stdio](STEP_12_10_MCP_HTTP_AND_CONTAINER_SANDBOX_INTEGRATION.md) | First-class MCP tool discovery and execution via Microsoft Agent Framework standards (`ModelContextProtocol`), streamable HTTP/SSE remote servers, and isolated stdio MCP servers in `ContainerSandbox`. | Planned |
| **12.11** | [🐙 Step 12.11: GitHub Integration (Untrusted Intake Triage, PR Delivery & Task Mirroring)](STEP_12_11_GITHUB_INTEGRATION_ISSUES_AND_PRS.md) | Modular GitHub integration: untrusted issue intake routed to sandboxed triage to generate proposals, Pull Request delivery mode with rich PR metadata, and bi-directional status mirroring. | Planned |
| **12.12** | [🎙️ Step 12.12: Voice Support, Continuous Dictation & Hands-Free Operations](STEP_12_12_VOICE_SUPPORT_AND_CONTINUOUS_DICTATION.md) | Hands-free voice operations via Web Speech API (`SpeechRecognition`) and Blazor JS Interop. Continuous dictation with 1500ms silence detection and visual audio feedback. | Planned |
| **12.15** | [🌐 Step 12.15: Deep OllamaRouter Fleet Provisioning & In-App Setup](STEP_12_15_DEEP_OLLAMAROUTER_FLEET_PROVISIONING_AND_IN_APP_SETUP.md) | In-app fleet provisioning UI in `/settings` to add, test, configure, and remove LAN compute nodes, live connectivity testing, secure Cloud Overflow API key management, and dynamic context visual tuning. | Planned |
| **12.16** | [🔒 Step 12.16: Remote Tailscale Mesh Access, HTTPS & Transient Network Resilience](STEP_12_16_TAILSCALE_REMOTE_ACCESS.md) | Zero-firewall private mesh VPN remote access, stable service identity (`svc:ncf` -> `https://ncf.<tailnet>.ts.net`), automated MagicDNS TLS via `TailscaleServeHostedService`, graceful shutdown cleanup, WebSocket reverse proxying, and network shift resilience. | Planned |
| **12.18** | [🖥️ Step 12.18: Cross-Platform Desktop Installer, Updater & Setup Wizard](STEP_12_18_CROSS_PLATFORM_DESKTOP_INSTALLER_AND_ENVIRONMENT_WIZARD.md) | Standalone self-contained Avalonia UI desktop application across Windows, Linux, and macOS. Zero-clone GitHub Releases binary deployment, automated host/WSL2/Docker/.NET provisioning, in-wizard micro-benchmark, OllamaRouter mesh fleet setup, and 1-click update hub. | Planned |
| **12.20** | [🛡️ Step 12.20: Air-Gapped Search Deactivation & MarkdownFetch Whitelisting](STEP_12_20_AIR_GAPPED_SEARCH_DEACTIVATION_AND_MDFETCH_WHITELISTING.md) | Automatic deactivation of `web_search` and outbound calls to `ollama.com` in Air-Gapped Mode; agent dispatcher tool filtering; UI warning banner in Web Search settings; in-app `mdfetch` allowed URL whitelist manager with cloud metadata (`169.254.169.254`) hard-deny. | Planned |
| **12.21** | [🛡️ Step 12.21: Container Sandbox Egress Filtering & Envoy Proxy](STEP_12_21_CONTAINER_SANDBOX_EGRESS_FILTERING_AND_ENVOY_PROXY.md) | Istio-style transparent kernel redirection (`iptables`) and Envoy SNI domain filtering for containers in shared network namespaces (`--net=container:...`); zero-decryption TLS inspection (`tls_inspector`); fallback explicit HTTP proxy mode; in-app egress profiles; and live audit telemetry streaming to `/monitoring`. | Planned |

---

## 3. Definition of Done (DoD) Checklist for Remaining Features

### Tier 1 Deliverables (Top Priority)
- [ ] **Step 12.1 (Intelligent Task Dependency Detection):**
  - [ ] Implement `IWorkspaceBacklogSummaryService` generating compact `<existing_workspace_tasks>` digests (excluding archived/closed tasks, capped to top 15–20 active tasks by priority order).
  - [ ] Inject existing backlog digest into `IdeationService` system prompt with circular dependency prohibition.
  - [ ] Inject existing backlog digest and dependency directives into Stage 1 Task Review directive in `CrucibleAgentHarness`.
  - [ ] Direct Stage 1 Review Agent to populate `depends_on` in YAML frontmatter (with rationale under `## Dependencies`), enforce cycle rejection loopback, and verify `TaskMarkdownSyncService` dependency synchronization.
  - [ ] Implement dynamic task suspension and full workflow reset in `TaskOrchestrator`: if Stage 1 reveals unclosed prerequisites, reset `task.Status = Open` and `CurrentStageId = TaskReview` to rerun full workflow once prerequisites close.
  - [ ] Render clickable prerequisite and downstream dependency chips in `KanbanCard.razor` and `TaskDetailDrawer.razor`.

- [ ] **Step 12.2 (Task Attachments & Context Documents):**
  - [ ] Create `attachments/` store sibling to the canonical clone and task worktrees, with `manifest.json` registry and `TaskAttachments` / `TaskAttachmentLinks` EF Core tables (including `IsSymlink` support).
  - [ ] Implement `IAttachmentIntakeService`: file drop, system host filename symlink mapping (`File.CreateSymbolicLink`), and URL capture (SSRF-guarded) with MarkItDown markdown companions and SHA-256 deduplication.
  - [ ] Register read-only `list_attachments` / `read_attachment` tools for ideation and the three workflow stage agents; inject attachments digests into ideation and stage prompts.
  - [ ] Support `@attachment:{id}` citations in task descriptions, materialized as `TaskAttachmentLinks` on task creation (hard error on unknown IDs).
  - [ ] Build Web UI: chat drop zone + "Attach Host File" input + attachment chips with symlink indicators, workspace attachments panel with preview drawer, attachments picker on `/tasks/new`, and user-only delete.
  - [ ] Configure Docker management layer (`NightCodeForgeContainerExecutor` & `ExecutionEnvironmentProvider`) to bind-mount host `attachments/` read-only to `/workspace/attachments:ro` in `ContainerSandbox` mode.

- [ ] **Step 12.4 (Ideation Quick Notes & Suggestions):**
  - [ ] Quick notes intake bar with instant save to `Suggested` state.
  - [ ] Proposal card tri-state actions (`Approve`, `Keep as Suggestion`, `Discard`).
  - [ ] "Refine in Ideation" action pre-seeding context and questions.
  - [ ] Scoped `edit_task` agent tools.
  - [ ] 24-hour draft pruning.

- [ ] **Step 12.13 (Conversational Backlog Management & Mutation Tools):**
  - [ ] Define `TaskCannotBeModifiedException` and enforce fail-fast hard errors on in-progress or closed tasks.
  - [ ] Implement `get_task` tool fetching full task specifications on demand.
  - [ ] Implement `edit_task` tool updating title, description, sub-tasks, priority, and dependencies for unstarted tasks (`Draft`, `Suggested`, `Open`).
  - [ ] Implement `set_task_priority` and `rework_priorities` tools for conversational backlog reordering.
  - [ ] Implement `remove_task` tool with active dependent validation.
  - [ ] Enforce audit logging via `AuditActors.IdeationAgent` and markdown disk synchronization via `ITaskMarkdownSyncService`.
  - [ ] Mandate upfront functional and business ambiguity resolution in `IdeationService` prompts (`<task_planning_guidelines>`) and `AgentProfile` persona, explicitly clarifying that ideation is not mandated to identify all technical impacts (e.g. files).
  - [ ] Inject `<backlog_management_guidelines>` into `IdeationService` system prompt.
  - [ ] Add "💡 Refine in Ideation" button to `TaskDetail.razor` and `TaskDetailDrawer.razor` with `?taskId={id}` routing.
  - [ ] Align `skills/ncf-backlog/SKILL.md` with zero-surprise scheduling and mandatory interactive interview.

### Tier 2 Deliverables (Intelligence, Efficiency & Quality Gates)
- [ ] **Step 12.5 (Task Difficulty Classification & Model Escalation):**
  - [x] Architect/Reviewer model vs Coder model configuration in `AgentProfile` and dynamic context window discovery (delivered in Steps 11.33 & 11.35).
  - [ ] `TaskDifficulty` (`Standard`, `Difficult`, `Expert`) on `ForgeTask`, `TaskProposal`, and markdown frontmatter.
  - [ ] Dynamic escalation of `Implementation` stage and `DeterministicBuildStage` JIT warmup lookahead on difficult/expert tasks.
- [ ] **Step 12.6 (Smart CLI Output Filtering):**
  - [ ] Compiler and test noise suppression for verbose tools (`dotnet build`, `dotnet test`, `npm`).
  - [ ] Workspace path relativization.
- [ ] **Step 12.7 (DevSkim SAST Quality Gate):**
  - [ ] Microsoft DevSkim CLI integration with SARIF report parsing.
  - [ ] Loopback diagnostics on security violations.
- [ ] **Step 12.22 (Interactive Container & Execution Environment Diagnostic Bash Console):**
  - [ ] Implement `IEnvironmentDiagnosticShellService` orchestrating `IExecutionEnvironmentProvider`, `CanonicalTools.BashAsync`, container mount/drift inspection, and container recreation.
  - [ ] Build `/diagnostics/shell` (`EnvironmentDiagnosticShell.razor`) with Workspace/Task worktree scope selector, `Standard` vs `RestrictedReadOnly` security tier toggle, and `AgentBashTool` vs `RawExecutionEnvironment` pipeline selector.
  - [ ] Provide 1-click diagnostic presets, structured "Environment Doctor" multi-point health check, and live streaming terminal output with `SIGINT` cancellation.
- [ ] **Step 12.23 (Crucible Benchmark Runner for SWE-Sharp-Bench & Multi-Model Evaluation):**
  - [ ] Implement `IBenchmarkDatasetLoader` supporting SWE-Sharp-Bench and SWE-bench `.jsonl`/`.json` datasets (`instance_id`, `repo`, `base_commit`, `problem_statement`, `test_patch`, `fail_to_pass`, `pass_to_pass`).
  - [ ] Implement `IBenchmarkWorkspaceManager` and `ICrucibleBenchmarkRunner` running isolated single-shot Crucible sessions at `base_commit` inside `ContainerSandbox` (with `read`, `edit`, `write`, `bash`, and stack skills; excluding `ask`, `propose_tasks`, and `web_search`).
  - [ ] Support standard `predictions.jsonl` export and built-in containerized `fail_to_pass` / `pass_to_pass` test verification.
  - [ ] Add `ncf benchmark` CLI commands and `/benchmarks` (`Benchmarks.razor`) Leaderboard & Model Patch vs. Gold Patch comparator UI.

### Tier 3 Deliverables (Deep Integrations & Remote Experience)
- [ ] **Step 12.8 (Mobile Responsive UI & PWA):**
  - [ ] Off-canvas navigation drawer and touch targets $\ge 44\text{px}$.
  - [ ] Mobile bottom sheet drawer and touch-friendly question cards.
  - [ ] Progressive Web App (PWA) manifest (`manifest.webmanifest`) and service worker (`service-worker.js`).
  - [ ] Browser web notifications (dispatched via Web Worker / Service Worker on mobile) for human intervention (question inbox, review room) and task merges with per-subject preference toggles.
- [ ] **Step 12.9 (Custom Repository Scripts):**
  - [ ] Support `.nightcodeforge/scripts/` (.sh, .py, .ps1).
  - [ ] PipelineDataBag environment variable injection.
- [ ] **Step 12.10 (MCP Integration):**
  - [ ] Streamable HTTP/SSE and containerized stdio MCP transports.
- [ ] **Step 12.11 (GitHub Integration):**
  - [ ] Sandboxed issue triage, Pull Request delivery, and bi-directional status mirroring.
- [ ] **Step 12.12 (Voice Support & Continuous Dictation):**
  - [ ] Web Speech API JS interop, 1500ms silence detection, and `VoiceInputButton.razor` component.
  - [ ] Integration with Ideation prompt bar, Quick Notes modal, and Question Inbox.
- [ ] **Step 12.15 (Deep OllamaRouter Fleet Provisioning & In-App Setup):**
  - [ ] In-app fleet provisioning UI in `/settings` to add, test, configure, and remove LAN compute nodes, live connectivity testing, secure Cloud Overflow API key management, and dynamic context visual tuning.
- [ ] **Step 12.16 (Remote Tailscale Mesh Access):**
  - [ ] Kestrel `ForwardedHeadersOptions` for `X-Forwarded-Proto` and `X-Forwarded-For`.
  - [ ] Tailscale Serve configuration with MagicDNS Let's Encrypt TLS termination.
  - [ ] Transient network reconnection resilience and operational tooling (`tools/tailscale-serve.ps1`).
- [ ] **Step 12.18 (Cross-Platform Desktop Installer, Updater & Setup Wizard):**
  - [ ] Scaffold `NightCodeForge.Installer` with Avalonia UI 11+ and `CommunityToolkit.Mvvm` targeting .NET 10.
  - [ ] Implement `IHardwareInspectorService` detecting CPU architecture, RAM, and GPU VRAM across Windows, Linux, and macOS.
  - [ ] Implement `IHardwareMicroBenchmarkService` running automated setup micro-benchmarks on curated `jetelain/*` Unsloth Dynamic models (`qwen3.6-35b-a3b`, `qwen3.8-27b` Q3/Q4, `qwen3.5-9b`), enforcing a strict $\ge 128\text{k}$ context guarantee (`num_ctx >= 131,072`) per role with KV cache quantization probing (`q4_0` / `q8_0`).
  - [ ] Implement `IEnvironmentProvisioner` managing JIT elevation for WSL2 setup, Docker Engine, .NET 10 SDK, and companion CLI tools.
  - [ ] Implement `IOllamaManager` handling Ollama service verification, model streaming/pulling, and OllamaRouter mesh node setup.
  - [ ] Implement `IReleaseManager` querying GitHub Releases API, streaming binary assets with SHA-256 verification, and atomic unpacking.
  - [ ] Implement dual-mode UX: OOBE Wizard on first run with benchmark visualization, Maintenance & Updater Hub on subsequent launches with "Doctor" diagnostics and 1-click update.
  - [ ] Configure multi-platform self-contained single-file publish workflow for `win-x64`, `linux-x64`, and `osx-arm64`.
- [ ] **Step 12.20 (Air-Gapped Search Deactivation & MarkdownFetch Whitelisting):**
  - [ ] Add centralized `NetworkSecurityOptions` model with `AirGappedMode` and `DocumentationFetcher` options in `NightCodeForge.Core`.
  - [ ] Intercept `web_search` in `OllamaWebSearchService` and agent tool dispatcher to disable external searches to `ollama.com` when `AirGappedMode` is active, throwing `AirGappedOperationException`.
  - [ ] Update `WebSearchSettingsTab.razor` with Air-Gapped warning banner and disabled input state.
  - [ ] Build in-app management UI in `/settings` (`MdfetchSecuritySettingsTab.razor`) to configure `mdfetch` (`Pmad.MarkdownFetch`) allowed URL whitelist patterns, test URLs against rules, and toggle security sanitization.
  - [ ] Implement `IMarkdownFetchSafetyService` generating `~/.nightcodeforge/mdfetch-safety.json` configuration with hardcoded cloud metadata (`169.254.169.254`) blocking.
  - [ ] Inject `--safety-config` into `mdfetch` tool execution pipelines and global skill runners ([Step 12.14](../archive/phase-12/STEP_12_14_GLOBAL_SKILLS_INJECTION_AND_STACK_TOOLING.md)).
- [ ] **Step 12.21 (Container Sandbox Egress Filtering & Envoy Proxy):**
  - [ ] Implement `IEnvoyConfigGenerator` generating dynamic `envoy.yaml` with transparent HTTP/HTTPS listeners (ports 15001/15002), `tls_inspector` SNI filtering, and default 403 / TCP RST deny rules.
  - [ ] Implement `IEnvoyEgressProxyManager` handling lifecycle of `ncf-envoy-task<id>` sidecar container with `--cap-add=NET_ADMIN` and `iptables` redirection rules in the shared network namespace (`--net=container:ncf-envoy-task<id>`).
  - [ ] Update `NightCodeForgeContainerExecutor.BuildDockerRunArgs` to support `--net=container:ncf-envoy-task<id>` (Mode 1: Transparent) and `--network ncf-sandbox-net` + `http_proxy` injection (Mode 2: Explicit Proxy fallback).
  - [ ] Implement `IEnvoyEgressAuditService` parsing egress access logs and streaming security alerts to `/monitoring` live operations telemetry ([Step 11.18](../archive/phase-11/STEP_11_18_LIVE_OPERATIONS_TELEMETRY_LLM_SHELL_OLLAMAROUTER.md)).
  - [ ] Build in-app UI in `/settings` (`ContainerEgressSettingsTab.razor`) to toggle redirection mode (Transparent vs Explicit), manage container allowed domains, and view live egress logs.

---

## 4. Completed Features Archive

Completed feature steps (`12.3`, `12.14`, `12.17`, `12.19`) are archived in:
- **Summary & DoD Record:** [`docs/archive/12_EXTEND_FEATURES.md`](../archive/12_EXTEND_FEATURES.md)
- **Step Specifications:** [`docs/archive/phase-12/`](../archive/phase-12/)

