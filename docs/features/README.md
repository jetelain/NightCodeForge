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

#### 2. Installer Stream (`v0.1.0` — Parallel) — Completed ✅
*All Installer stream steps (`12.18A`, `12.18B`, `12.18C`) have been implemented, verified, and archived (`docs/archive/phase-12/`).*

---

#### 3. Benchmark Stream (`v0.1.0` — Parallel Dedicated Standalone CLI)
*Dedicated standalone CLI (`NightCodeForge.Benchmark.Cli`) using `NightCodeForge.Agents` classes + mock/in-memory DB (no Web UI or Hangfire, keeping `NightCodeForge.Cli` lean) to evaluate Implementation, Code Review, and ForgeTask Review problems across models and track regressions across NightCodeForge commits/branches.*

| Step | Specification | Focus & Key Deliverables | Status |
| :--- | :--- | :--- | :--- |
| **12.23A** | [🏁 Step 12.23A: Standalone Crucible Benchmark Engine, Mock DB & Verifier](STEP_12_23A_CRUCIBLE_BENCHMARK_ENGINE_AND_CONTAINER_VERIFIER.md) | `IBenchmarkDatasetLoader` (Implementation, Code Review, and ForgeTask Review JSONL/JSON problems), `IBenchmarkWorkspaceManager` (leak-free `git init` + `git fetch <cache> <BaseCommit>` + category workspace setup), decoupled turn sink & mock/in-memory coordinator for `CrucibleAgentHarness`, category-accurate tool/skill gating, multi-stack (`.trx`/JUnit XML/`cargo`) container verification, deterministic review checks, and optional `IBenchmarkLlmJudge`. | Planned |
| **12.23B** | [🏁 Step 12.23B: Standalone Benchmark CLI (`NightCodeForge.Benchmark.Cli`)](STEP_12_23B_BENCHMARK_CLI_AND_LEADERBOARD_WEB_UI.md) | Dedicated `ncf-bench` CLI (`run` / `report`) sweeping models with provider/config selection, `--resume`, `BenchmarkRunProvenance` capture, live progress, `predictions.jsonl`, `results.json`, `summary.md`, `transcripts/`, and single-run + cross-commit baseline regression leaderboards with per-language breakdown (Web UI `/benchmarks` deferred post-`v0.1.0`). | Planned |
| **12.23C** | [🏁 Step 12.23C: Multi-Stack Benchmark Datasets, Importers & Crafted Scenarios](STEP_12_23C_BENCHMARK_DATASETS_IMPORTERS_AND_SCENARIOS.md) | C#-first (`SWE-Sharp-Bench`) + TS/JS + Python + Rust tiered suite manifests (`v1-smoke` / `v1-full`), `IBenchmarkDatasetImporter`, crafted Code Review (`Approve`/`MinorFix`/`RequestChanges`) & ForgeTask Review (`Ready`/`NeedsClarification`) scenarios, and `ncf-bench dataset import/validate` pre-flight container checks. | Planned |

---

#### 4. `v0.1.0` Features Stream (Parallel UI & Onboarding)
*High-value user-facing features with low coupling to `Agents` internals.*

| Step | Specification | Focus & Key Deliverables | Status |
| :--- | :--- | :--- | :--- |
| **12.40** | [🛡️ Step 12.40: Tool Output & File Read Binary Content Guard](STEP_12_40_TOOL_OUTPUT_BINARY_CONTENT_GUARD.md) | Deterministic binary content guard across `SandboxBashToolModule` (`bash`) and `WorkspaceFileToolModule` (`read`) to prevent binary/replacement-character payloads from inflating LLM context (non-bypassable even with `disable_filter: true`). | Planned |
| **12.44** | [🎨 Step 12.44: CSS Architecture Rationalization, Blazor Scoped Styles & Design Token Consolidation](STEP_12_44_CSS_ARCHITECTURE_RATIONALIZATION_AND_SCOPED_STYLES.md) | Removes unused `wwwroot/lib/bootstrap/` (~8.3 MB) and orphaned `ReconnectModal.razor.css`, enables Blazor CSS isolation (`NightCodeForge.Web.styles.css`), decomposes monolithic `app.css` (9,243L) and mixed `theme.css` (676L) into layered global sheets (`theme.css`, `base.css`, `layout.css`, `components.css`) + colocated `*.razor.css`, eliminates inline `<style>` tags, harmonizes tokens/primitives/breakpoints, cuts static inline `style=""` sprawl by $\ge 70\%$, and adds CSS hygiene/budget tests. | Planned |
| **12.4** | [💡 Step 12.4: Ideation Quick Notes, Tri-State Proposal Cards & 24h Draft Pruning](STEP_12_4_IDEATION_QUICK_NOTES_SUGGESTIONS_AND_DRAFT_PRUNING.md) | Quick notes intake bar saving directly to `Suggested` state, proposal card tri-state actions (`Approve` $\rightarrow$ `Open`, `Keep as Suggestion` $\rightarrow$ `Suggested`, `Discard` $\rightarrow$ Delete), `IdeationBacklogTab.razor`, and 24-hour `IdeationDraftPruningService`. | Planned |
| **12.7A** | [🛡️ Step 12.7A: Unified Security Findings Foundation & Diff-Scoped DevSkim SAST Backend](STEP_12_7A_UNIFIED_SECURITY_DOMAIN_AND_DEVSKIM_SAST_BACKEND.md) | *(Optional `v0.1.0`)* Shared security finding/report domain & `Sarif210Parser`, diff-scoped Microsoft DevSkim CLI integration (`Stage 4` + `Stage 6` re-run), configurable severity thresholds, and autonomous LLM loopback diagnostics. | Planned |
| **12.7B** | [🛡️ Step 12.7B: DiffGuard CLI Security Scanner, Endpoint Wiring & Post-Review Guardrail Backend](STEP_12_7B_DIFFGUARD_POST_REVIEW_GUARDRAIL_BACKEND.md) | *(Optional `v0.1.0`)* `Pmad.AI.DiffGuard` CLI tool integration, `.diffguard.json`, cross-model LLM judge & WSL gateway translation, shared SARIF ingestion, Stage 6 `security-guardrail`, and auto-merge downgrade to `ManualReview` (no LLM loopback). | Planned |
| **12.7C** | [🛡️ Step 12.7C: Unified Security Review UI (DevSkim + DiffGuard), Inspector Panel & Security Settings](STEP_12_7C_UNIFIED_SECURITY_REVIEW_AND_SETTINGS_UI.md) | *(Optional `v0.1.0`)* Single unified Review Room security card, alert banner, `SecurityReviewPanel.razor` (DevSkim + DiffGuard findings, file-jump, 1-click allowlist/ignore/comment actions), Inspector `SecurityFindingsList.razor`, and unified `SecuritySettingsTab.razor` in `/settings`. | Planned |
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
| **12.34A** | [🦉 Step 12.34A: Autonomous Nightwatch — Harness Issue Ledger, Tiered Triage Engine & Orchestrator Failure Recovery](STEP_12_34A_NIGHTWATCH_DOMAIN_LEDGER_AND_ORCHESTRATOR_REACTIVE_TRIAGE.md) | `HarnessIssue` & `NightwatchInterventionRecord` EF Core persistence, `INightwatchSettingsService`, unified `/nightcodeforge/` Nightwatch virtual root, `query_database` SQLite tool, Nightwatch admin tools, and lock-safe reactive stage/pipeline auto-recovery. | Deferred (Post-v0.1.0) |
| **12.34B** | [🆘 Step 12.34B: Agent-Invocable Nightwatch SOS Tool (`report_environment_issue`) & In-Flight Environment Remediation](STEP_12_34B_AGENT_NIGHTWATCH_SOS_TOOL_AND_ENVIRONMENT_REMEDIATION.md) | Canonical `report_environment_issue` SOS tool in `WorkflowInteractionToolModule`, in-flight environment/lock/mount probes, automated container/WSL2 repair, and fallback to `WaitingForHuman`. | Deferred (Post-v0.1.0) |
| **12.34C** | [📊 Step 12.34C: Nightwatch Telemetry UI, Harness Issue Ledger (`/diagnostics/nightwatch`) & Self-Improvement Task Promotion](STEP_12_34C_NIGHTWATCH_TELEMETRY_HARNESS_ISSUES_UI_AND_SELF_HEALING_BACKLOG.md) | `NightwatchDashboard.razor` (`/diagnostics/nightwatch`), 1-click **Promote to Backlog Task** / **Refine in Ideation**, Nightwatch timeline badges, and `NightwatchSettingsTab.razor`. | Deferred (Post-v0.1.0) |
| **12.35B** | [🦉 Step 12.35B: Idle-GPU Nightwatch Deep Transcript & Workspace Analysis (Replacing Legacy AI Diagnostics)](STEP_12_35B_NIGHTWATCH_IDLE_DEEP_ANALYSIS_AND_LEGACY_DIAGNOSTIC_REPLACEMENT.md) | `INightwatchIdleAnalysisService` using idle GPU windows to scan transcripts, codebases, and two-tier agent memory (`12.41`) and route fixes/cleanup/promotions into `Suggested` tasks and NCF improvements into `HarnessIssue` ledger. | Deferred (Post-v0.1.0) |
| **12.43** | [🔒 Step 12.43: Secure Web UI Access & User Authentication](STEP_12_43_SECURE_WEB_UI_ACCESS_AUTHENTICATION.md) | Cookie/session authentication for Blazor Web UI and SignalR hubs, OS keyring/hashed owner credentials, and safe non-loopback / Tailscale access control while preserving `default-key.dat` companion API auth. | Deferred (Post-v0.1.0) |
| **12.45A** | [📦 Step 12.45A: Cross-Platform Layered Container Images, Named Toolchain Volumes & Runtime Hardening](STEP_12_45A_CROSS_PLATFORM_LAYERED_CONTAINER_IMAGES_AND_TOOLCHAIN_VOLUMES.md) | Clean-break removal of host-ABI SDK/shared-lib bind-mounting (`HostSdkDiscoveryService`, `/usr/lib/x86_64-linux-gnu`), `ncf-agent-base:v6` + multi-stage `COPY --from=` stack images (`dotnet` + `dotnetWorkloads`/MAUI Android, `node`, `python`, `rust`, `go`, `jvm`, Playwright deps, `.nightcodeforge/Dockerfile`), architecture-qualified Docker named volumes (`ncf-ms-playwright-{arch}`, `ncf-android-sdk-{arch}`, `ncf-acp-tools-{arch}`, macOS caches), container-native `agy` provisioning, and `IContainerRuntimeDetector` (rootless Docker/Podman, SELinux `label=disable`, macOS UID `1000:1000` + `/private/*` path canonicalization, Windows/WSL2 preserved). | Deferred (Post-v0.1.0) |
| **12.45B** | [🛠️ Step 12.45B: Sandbox Toolchain Management UI, Environment Doctor & Multi-Distro/macOS Installer Alignment](STEP_12_45B_SANDBOX_TOOLCHAIN_UI_DIAGNOSTICS_AND_MULTI_DISTRO_INSTALLER.md) | Replaces `HostSdkInspectorTab.razor` with `ContainerToolchainsTab.razor` (runtime, stack images, named volumes, container-native `agy`/tools), aligns `WizardStep3Environment.razor` & `WorkspaceSettingsTab.razor` (stack image preview, MAUI Android/Playwright toggles, hiding `WslDirect` on Linux/macOS), upgrades `EnvironmentDoctorProbe`, and updates `EnvironmentProvisioner` for `dnf`/`pacman`/`zypper`/`apk`/Podman/macOS + optional host .NET 10 SDK & air-gapped OCI `.tar` loading. | Deferred (Post-v0.1.0) |

---

### ✅ Recently Completed (Phase 12)
* **12.18A, 12.18B & 12.18C:** Native AOT Desktop Installer, Companion NuGet Tool Provisioning, OllamaRouter Setup Wizard Handoff & Role Sync, Avalonia OOBE/Maintenance Hub & GitHub Actions CI/Release.
* **12.22A & 12.22B:** Container Diagnostic Shell Service, `SIGINT` Partial-Output Recovery & Interactive `/diagnostics/shell` Web Console.
* **12.32A & 12.32B:** Workspace Wizard Local Git Discovery, Provider-Agnostic Remote Hosting (`IVcsHostingProvider`), From-Scratch Clean Architecture Templates & Remote Forge Publishing.
* **12.41:** Full-Page Agent Memory Explorer (`/memory`) & Quick Ideation Cleanup Prompts.
* **12.42:** Non-Incremental Deterministic Build (`--no-incremental`) & Combined `build && test` Synthetic Conversation Seed.
* **12.46A & 12.46B:** Git Branch Repository File Viewer (`/files`), Context-Aware Markdown Link Builder & `/tasks/by-file` Resolver.

---

## 3. Completed Features Archive

Completed feature steps (`12.1A`, `12.1B`, `12.2A`, `12.2B`, `12.2C`, `12.2D`, `12.3`, `12.5A`, `12.5B`, `12.6`, `12.8A`, `12.8B`, `12.8C`, `12.11A`, `12.11B`, `12.11C`, `12.11D`, `12.13A`, `12.13B`, `12.14`, `12.16A`, `12.16B`, `12.17`, `12.18A`, `12.18B`, `12.18C`, `12.19`, `12.20A`, `12.22A`, `12.22B`, `12.24`, `12.25A`, `12.25B`, `12.26`, `12.27`, `12.29`, `12.30`, `12.31`, `12.32A`, `12.32B`, `12.33A`, `12.33B`, `12.35A`, `12.36A`, `12.36B`, `12.37`, `12.38`, `12.39`, `12.41`, `12.42`, `12.46A`, `12.46B`) are archived in:
- **Summary & DoD Record:** [`docs/archive/12_EXTEND_FEATURES.md`](../archive/12_EXTEND_FEATURES.md)
- **Step Specifications:** [`docs/archive/phase-12/`](../archive/phase-12/)
