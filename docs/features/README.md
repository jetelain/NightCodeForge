# 🚀 Features & Integrations Roadmap

> **Active Feature & Integration Specifications**  
> *Parent Index:* [docs/PLAN.md](../PLAN.md) | *Completed Features Archive:* [docs/archive/12_EXTEND_FEATURES.md](../archive/12_EXTEND_FEATURES.md) (`docs/archive/phase-12/`)  
> *Target Framework:* .NET 10 (C# 13)

---

## 1. Overview & Strategic Prioritization

This directory contains the active technical specifications for remaining **new features, developer ergonomics, and ecosystem integrations** (historically tracked under Phase 12). Multi-layer features are decomposed into focused, self-contained `A` / `B` / `C` steps so each step is straightforward to implement, test, and review.

Once a feature step is implemented and verified, its specification document is moved to [`docs/archive/phase-12/`](../archive/phase-12/) and recorded in [`docs/archive/12_EXTEND_FEATURES.md`](../archive/12_EXTEND_FEATURES.md).

---

## 2. Remaining Feature Steps

### 🎯 Priority Tier 1: MVP Focus (Ideation Backlog Management & Mobile/Tailscale Status) — Completed ✅

All Tier 1 MVP feature specifications have been implemented, verified, and archived:
- **Track 1A — Manage Backlog with Ideation Chat (`12.1A`, `12.13A`, `12.13B`):** Archived in [`docs/archive/12_EXTEND_FEATURES.md`](../archive/12_EXTEND_FEATURES.md) (`docs/archive/phase-12/`).
- **Track 1B — Check Status on Phone / Tablet & Basic Tailscale Connectivity (`12.24`, `12.16A`, `12.8A`):** Archived in [`docs/archive/12_EXTEND_FEATURES.md`](../archive/12_EXTEND_FEATURES.md) (`docs/archive/phase-12/`).

---

### 🔥 `v0.1.0` Parallel Execution Plan (4 Streams)

To accelerate delivery of **`v0.1.0`**, remaining work is organized into four streams. Once the **Common Trunk** stabilizes `NightCodeForge.Core` and `NightCodeForge.Agents` contracts, the **Installer**, **Benchmark**, and **`v0.1` Features** streams execute in parallel:

#### 1. Common Trunk — Stabilize Internals (`v0.1.0` Critical Path) — Completed ✅
*All `v0.1.0` Common Trunk steps (`12.31`, `12.2D`, `12.39`, `11.54`) have been implemented, verified, and archived (`docs/archive/phase-12/` and `docs/archive/phase-11/`).*  
*(Note: Fix [Step 11.49B](../fixes/STEP_11_49B_RETIRE_OLLAMASHARP_STREAM_CANCELLATION_WORKAROUND.md) remains opportunistic on the trunk if `OllamaSharp` releases upstream; [Step 11.43C](../fixes/STEP_11_43C_PMAD_AI_SLM_DELEGATING_CHAT_CLIENTS_OPT_IN.md) is deferred to `v0.2`.)*

---

#### 2. Installer Stream (`v0.1.0` — Parallel)
*Native AOT OOBE wizard, companion NuGet tool pre-installation, air-gapped local bundle support, delegated OllamaRouter setup handoff, OS-integrated uninstaller, and GitHub Actions CI/Release.*

| Step | Specification | Focus & Key Deliverables | Status |
| :--- | :--- | :--- | :--- |
| **12.18A** | [🖥️ Step 12.18A: Installer Core Services, NuGet Tooling, Air-Gapped Bundle & Uninstaller](STEP_12_18A_INSTALLER_CORE_HARDWARE_RELEASE_AND_PROVISIONER.md) | Native AOT `NightCodeForge.Installer` scaffold, `IEnvironmentProvisioner`, `IInstallationDetector`, `ICompanionNuGetProvisioner` pre-installing/upgrading `NightCodeForge.Cli` (`ncf`), `NightCodeForge.Assay` (`nightcodeassay`), `Pmad.CSharpNav`, `Pmad.MarkdownFetch`, and `Pmad.CodeCondenser` as NuGet global tools, `IReleaseManager` supporting Online GitHub Releases, Air-Gapped local bundles (`.zip`/`.tar.gz` + `.nupkg`), and in-place upgrades of existing/`publish.ps1` installations, plus `IUninstallService` / `IOsIntegrationService` with OS uninstall system integration. | Planned |
| **12.18B** | [🖥️ Step 12.18B: OllamaRouter Setup Wizard Integration, Role Recommendation Sync & Custom Protocol Shortcut](STEP_12_18B_INSTALLER_OLLAMA_MANAGER_AND_128K_MICRO_BENCHMARK.md) | `IOllamaRouterSetupHandoffService` delegating LLM/fleet setup to `ollamarouter setup --wizard --wait`, `IOllamaRouterRecommendationSyncService` (source-generated `System.Text.Json`) importing `/api/router/recommendations` (`software_architect`, `senior_developer`, `agentic_chat`) into `config/config.json`, and `ollamarouter://` custom protocol shortcut (`ollamarouter://open` & `ollamarouter://wizard`) + role differentiation in `OllamaRouterGatewayCard.razor`. | Planned |
| **12.18C** | [🖥️ Step 12.18C: Avalonia OOBE Setup Wizard, Maintenance Hub, Interactive Uninstaller & GitHub Actions Release](STEP_12_18C_INSTALLER_AVALONIA_OOBE_WIZARD_AND_MAINTENANCE_HUB.md) | Native AOT 0-VRAM pure software-rendered Avalonia 12.1.6 (`Semi.Avalonia` + `NightCodeForgeTheme.axaml` from `NightCodeAssay`) 5-step OOBE Wizard (with Online vs. Air-Gapped Bundle source picker), Maintenance & All-Component In-Place Updater Hub (adopting existing/`publish.ps1` installs), Interactive/Headless Uninstaller (`uninstall`), Forge Doctor UI, and `.github/workflows/ci.yml` publishing `NightCodeForge.Cli` to NuGet.org and multi-RID GitHub Releases. | Planned |

---

#### 3. Benchmark Stream (`v0.1.0` — Parallel Standalone CLI)
*Strictly standalone CLI using `NightCodeForge.Agents` classes + mock/in-memory DB (no Web UI or Hangfire) to evaluate Implementation, Code Review, and Task Review problems across models and tooling versions.*

| Step | Specification | Focus & Key Deliverables | Status |
| :--- | :--- | :--- | :--- |
| **12.23A** | [🏁 Step 12.23A: Standalone Crucible Benchmark Engine, Mock DB & Verifier](STEP_12_23A_CRUCIBLE_BENCHMARK_ENGINE_AND_CONTAINER_VERIFIER.md) | `IBenchmarkDatasetLoader` (Implementation, Code Review, and Task Review JSONL problems), `IBenchmarkWorkspaceManager` (leak-free `git init` + `git fetch <cache> <BaseCommit>`), mock/in-memory DB & turn sink decoupling `CrucibleAgentHarness` from SQLite, and containerized `FailToPass`/`PassToPass` + review verdict evaluators. | Planned |
| **12.23B** | [🏁 Step 12.23B: Standalone Benchmark CLI (`run` & `report`)](STEP_12_23B_BENCHMARK_CLI_AND_LEADERBOARD_WEB_UI.md) | Standalone CLI commands (`run` / `report`) sweeping across models and tooling versions (stack skills, optimization flags, harness config) with live progress, `predictions.jsonl`, `results.json`, `summary.md`, `transcripts/`, and terminal leaderboard rendering (Web UI `/benchmarks` deferred post-`v0.1.0`). | Planned |

---

#### 4. `v0.1.0` Features Stream (Parallel UI & Onboarding)
*High-value user-facing features with low coupling to `Agents` internals.*

| Step | Specification | Focus & Key Deliverables | Status |
| :--- | :--- | :--- | :--- |
| **12.32A** | [📁 Step 12.32A: Workspace Wizard Local Git Discovery, Provider-Agnostic Remote Hosting & Existing Project Picker](STEP_12_32A_WORKSPACE_WIZARD_REPO_DISCOVERY_AND_REMOTE_PROJECT_PICKER.md) | `ILocalRepositoryDiscoveryService` scanning standard developer directories (`source/repos`, `repos`, `projects`), searchable autocomplete dropdown in `WizardStep1PathProbe.razor`, provider-agnostic `IVcsHostingProvider` / `IVcsHostingProviderRegistry` abstraction (GitHub/GitLab), remote repository browser, and 1-click clone into local disk. | Planned |
| **12.32B** | [⚡ Step 12.32B: From-Scratch Workspace Templates (Native CLI Clean Architecture) & Remote Forge Publishing](STEP_12_32B_WORKSPACE_TEMPLATES_NATIVE_CLI_SCAFFOLDING_AND_REMOTE_PUBLISH.md) | Wizard mode switcher ("Onboard Existing" vs "Create New from Template"), template engine executing native stack CLIs (`dotnet new`), 4 built-in Clean Architecture templates (Web App, Web API, CLI, Class Library) with canonical `docs/`, `src/`, `tests/` layout, automated `git init -b main`, and optional remote repository publishing via `IVcsHostingProvider`. | Planned |
| **12.4** | [💡 Step 12.4: Ideation Quick Notes, Tri-State Proposal Cards & 24h Draft Pruning](STEP_12_4_IDEATION_QUICK_NOTES_SUGGESTIONS_AND_DRAFT_PRUNING.md) | Quick notes intake bar saving directly to `Suggested` state, proposal card tri-state actions (`Approve` $\rightarrow$ `Open`, `Keep as Suggestion` $\rightarrow$ `Suggested`, `Discard` $\rightarrow$ Delete), `IdeationBacklogTab.razor`, and 24-hour `IdeationDraftPruningService`. | Planned |
| **12.22A** | [🖥️ Step 12.22A: Container Diagnostic Shell Service, Environment Doctor & `SIGINT` Cancellation Fix](STEP_12_22A_DIAGNOSTIC_SHELL_SERVICE_AND_CONTAINER_SIGINT_RECOVERY.md) | `IEnvironmentDiagnosticShellService` (`AgentBashTool` via `SandboxBashToolModule` vs `RawExecutionEnvironment` pipelines, `RunEnvironmentDoctorAsync`, `RecreateEnvironmentContainerAsync`) across workspace, task, and Nightwatch scopes, plus in-process shell telemetry events and cancelled partial output surfacing. | Planned |
| **12.22B** | [🖥️ Step 12.22B: Interactive Container Diagnostic Bash Console Web UI (`/diagnostics/shell`)](STEP_12_22B_DIAGNOSTIC_BASH_CONSOLE_WEB_UI.md) | `EnvironmentDiagnosticShell.razor` (`/diagnostics/shell`) with Workspace/Task scope selector, Environment & Mounts Inspector card, 1-click diagnostic presets, Environment Doctor report view, and live streaming terminal with `SIGINT` stop button. | Planned |
| **12.7** | [🛡️ Step 12.7: Free & Open-Source SAST Quality Gate via Microsoft DevSkim CLI](STEP_12_7_DEVSKIM_SAST_QUALITY_GATE.md) | *(Optional `v0.1.0`)* Deterministic SAST quality gate using Microsoft DevSkim CLI across C#, JS/TS, Python, Go. SARIF report parsing, configurable severity thresholds, and automatic loopback routing with diagnostics. | Planned |
| **12.9** | [📜 Step 12.9: Custom Repository Scripts for Deterministic Steps (Bash, Python, PowerShell)](STEP_12_9_CUSTOM_SCRIPTS_FOR_DETERMINISTIC_STEPS.md) | *(Optional `v0.1.0`)* Execution of custom repository scripts in `.nightcodeforge/scripts/` during deterministic stages across Bash, Python, and PowerShell. Injection of `PipelineDataBag` environment variables and output directives. | Planned |

---

### 🌐 Post-`v0.1.0` Backlog (Deferred — Minimal Internal Impact)

The following specifications are deferred until after `v0.1.0` ships:

| Step | Specification | Focus & Key Deliverables | Status |
| :--- | :--- | :--- | :--- |
| **12.10A** | [⚡ Step 12.10A: Remote HTTP/SSE Model Context Protocol (MCP) Server Integration](STEP_12_10A_MCP_HTTP_SERVER_INTEGRATION.md) | Official `ModelContextProtocol` (`v2.2.0`) integration, `IMcpSession`, `HttpClientTransport`, tool discovery & namespacing (`mcp_{server}_{tool}`), `CrucibleAgentHarness` wiring, and transcript/telemetry logging. | Deferred (Post-v0.1.0) |
| **12.10B** | [⚡ Step 12.10B: Isolated Stdio MCP Servers inside `ContainerSandbox`](STEP_12_10B_MCP_CONTAINER_SANDBOX_STDIO_INTEGRATION.md) | Interactive `docker exec -i -w ...` `StdioClientTransport` builder, WSL/Container/Host dispatch matrix, and sandbox security tier enforcement. | Deferred (Post-v0.1.0) |
| **12.12** | [🎙️ Step 12.12: Voice Support, Continuous Dictation & Hands-Free Operations](STEP_12_12_VOICE_SUPPORT_AND_CONTINUOUS_DICTATION.md) | Hands-free voice operations via Web Speech API (`SpeechRecognition`) and Blazor JS Interop. Continuous dictation with 1500ms silence detection and `VoiceInputButton.razor` embedded in Ideation, Quick Notes, and Inbox. | Deferred (Post-v0.1.0) |
| **12.20B** | [🛡️ Step 12.20B: `mdfetch` Documentation Whitelist & SSRF Governance](STEP_12_20B_MDFETCH_DOCUMENTATION_WHITELIST_AND_SSRF_GOVERNANCE.md) | `IMarkdownFetchSafetyService`, `~/.nightcodeforge/mdfetch-safety.json` generation (with hardcoded `169.254.169.254` cloud metadata deny), `--safety-config` CLI injection, and `MdfetchSecuritySettingsTab.razor`. | Deferred (Post-v0.1.0) |
| **12.21A** | [🛡️ Step 12.21A: Envoy Egress Proxy Sidecar & Transparent `iptables` Container Redirection](STEP_12_21A_ENVOY_EGRESS_PROXY_SIDECAR_AND_IPTABLES_REDIRECTION.md) | `IEnvoyConfigGenerator` (`tls_inspector` SNI filtering), `IEnvoyEgressProxyManager` (`--net=container:ncf-envoy-task<id>` + explicit `HTTP_PROXY` fallback), and `NightCodeForgeContainerExecutor` integration. | Deferred (Post-v0.1.0) |
| **12.21B** | [🛡️ Step 12.21B: Container Egress Settings UI & Live Blocked-Connection Telemetry](STEP_12_21B_CONTAINER_EGRESS_SETTINGS_UI_AND_BLOCKED_TELEMETRY.md) | `IEnvoyEgressAuditService` streaming blocked outbound connection alerts to `/monitoring`, and `ContainerEgressSettingsTab.razor` (egress profiles, redirection mode switcher, domain tester, and live audit log table). | Deferred (Post-v0.1.0) |
| **12.28A** | [🛡️ Step 12.28A: DiffGuard CLI Security Scanner, Endpoint Wiring & Post-Review Guardrail Stage](STEP_12_28A_DIFFGUARD_SECURITY_SCANNER_AND_POST_REVIEW_GUARDRAIL.md) | `Pmad.AI.DiffGuard` CLI tool integration, companion tool auto-provisioning, `.diffguard.json`, cross-model diversity enforcement, SARIF 2.1.0 ingestion, `DeterministicSecurityGuardrailStage`, and automatic downgrade from `MergeMode.Automatic` to `MergeMode.ManualReview`. | Deferred (Post-v0.1.0) |
| **12.28B** | [🛡️ Step 12.28B: Review Room Risk Report UI & Security Guardrail Settings (`SecurityGuardrailSettingsTab.razor`)](STEP_12_28B_REVIEW_ROOM_RISK_REPORT_AND_GUARDRAIL_SETTINGS_UI.md) | Review Room security guardrail alert banner, 4th quality gate metric card, interactive `SecurityGuardrailReportPanel.razor`, and `SecurityGuardrailSettingsTab.razor` in `/settings`. | Deferred (Post-v0.1.0) |
| **12.34A** | [🦉 Step 12.34A: Autonomous Nightwatch — Harness Issue Ledger, Tiered Triage Engine & Orchestrator Failure Recovery](STEP_12_34A_NIGHTWATCH_DOMAIN_LEDGER_AND_ORCHESTRATOR_REACTIVE_TRIAGE.md) | `HarnessIssue` & `NightwatchInterventionRecord` EF Core persistence, `INightwatchSettingsService`, unified `/nightcodeforge/` Nightwatch virtual root, `query_database` SQLite tool, Nightwatch admin tools, and lock-safe reactive stage/pipeline auto-recovery. | Deferred (Post-v0.1.0) |
| **12.34B** | [🆘 Step 12.34B: Agent-Invocable Nightwatch SOS Tool (`report_environment_issue`) & In-Flight Environment Remediation](STEP_12_34B_AGENT_NIGHTWATCH_SOS_TOOL_AND_ENVIRONMENT_REMEDIATION.md) | Canonical `report_environment_issue` SOS tool in `WorkflowInteractionToolModule`, in-flight environment/lock/mount probes, automated container/WSL2 repair, and fallback to `WaitingForHuman`. | Deferred (Post-v0.1.0) |
| **12.34C** | [📊 Step 12.34C: Nightwatch Telemetry UI, Harness Issue Ledger (`/diagnostics/nightwatch`) & Self-Improvement Task Promotion](STEP_12_34C_NIGHTWATCH_TELEMETRY_HARNESS_ISSUES_UI_AND_SELF_HEALING_BACKLOG.md) | `NightwatchDashboard.razor` (`/diagnostics/nightwatch`), 1-click **Promote to Backlog Task** / **Refine in Ideation**, Nightwatch timeline badges, and `NightwatchSettingsTab.razor`. | Deferred (Post-v0.1.0) |
| **12.35B** | [🦉 Step 12.35B: Idle-GPU Nightwatch Deep Transcript & Workspace Analysis (Replacing Legacy AI Diagnostics)](STEP_12_35B_NIGHTWATCH_IDLE_DEEP_ANALYSIS_AND_LEGACY_DIAGNOSTIC_REPLACEMENT.md) | `INightwatchIdleAnalysisService` using idle GPU windows to route codebase fixes into `Suggested` tasks and NCF improvements into `HarnessIssue` ledger. | Deferred (Post-v0.1.0) |

---

### ✅ Recently Completed (Phase 12)
* **12.2D:** Multimodal Vision Support, In-Loop Image Inspection & Context Compaction.
* **12.31:** Interactive Ideation Agent Coordinator & Warm Execution Context.
* **12.38:** Triage Task Lifecycle, Verbatim Intake Persistence, Scoped Backlog Mutations & Inspector Visibility.
* **12.39:** Two-Tier Agent Memory Management, Selective `:rw` Memory Mount & Live UI Notifications.

---

## 3. Completed Features Archive

Completed feature steps (`12.1A`, `12.1B`, `12.2A`, `12.2B`, `12.2C`, `12.2D`, `12.3`, `12.5A`, `12.5B`, `12.6`, `12.8A`, `12.8B`, `12.8C`, `12.11A`, `12.11B`, `12.11C`, `12.11D`, `12.13A`, `12.13B`, `12.14`, `12.16A`, `12.16B`, `12.17`, `12.19`, `12.20A`, `12.24`, `12.25A`, `12.25B`, `12.26`, `12.27`, `12.29`, `12.30`, `12.31`, `12.33A`, `12.33B`, `12.35A`, `12.36A`, `12.36B`, `12.37`, `12.38`, `12.39`) are archived in:
- **Summary & DoD Record:** [`docs/archive/12_EXTEND_FEATURES.md`](../archive/12_EXTEND_FEATURES.md)
- **Step Specifications:** [`docs/archive/phase-12/`](../archive/phase-12/)
