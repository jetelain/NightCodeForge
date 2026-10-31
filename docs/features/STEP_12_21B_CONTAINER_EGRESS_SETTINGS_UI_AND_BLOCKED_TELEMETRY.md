# 🛡️ Step 12.21B: Container Egress Settings UI & Live Blocked-Connection Telemetry

> **Feature Implementation Step (Part 2 of 2 — `IEnvoyEgressAuditService`, `/settings` Tab & `/monitoring` Alerts)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.21A: Envoy Egress Proxy Sidecar & Transparent `iptables` Redirection](STEP_12_21A_ENVOY_EGRESS_PROXY_SIDECAR_AND_IPTABLES_REDIRECTION.md)  
> *Related Steps:* [Step 11.18: Live Operations Telemetry](../archive/phase-11/STEP_11_18_LIVE_OPERATIONS_TELEMETRY_LLM_SHELL_AND_ROUTER.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

With the Envoy sidecar and `iptables` redirection engine in place ([Step 12.21A](STEP_12_21A_ENVOY_EGRESS_PROXY_SIDECAR_AND_IPTABLES_REDIRECTION.md)), this step (Part 2 of Step 12.21) provides real-time egress audit telemetry and the in-app **Container Egress Whitelist** management UI:
1. **Real-Time Egress Audit Service (`IEnvoyEgressAuditService`):** Parse Envoy's structured JSON stdout access logs (`ALLOWED` / `BLOCKED`, `protocol`, `sni`, `destination`, `timestamp`) and stream high-priority security alerts into NightCodeForge's live operations telemetry (`/monitoring`) whenever a container sandbox attempts an unauthorized outbound connection.
2. **In-App Configuration UI (`ContainerEgressSettingsTab.razor`):** Provide a `/settings` tab for selecting egress profiles (`Strict Air-Gapped`, `Package Registries Only`, `Enterprise Intranet Only`, `Custom`), switching between `Transparent` and `Explicit HTTP_PROXY` modes, testing domain patterns, and inspecting recent outbound connection logs.

---

## 2. Technical Specifications

### 2.1 Egress Audit Log Parser & Live Telemetry (`IEnvoyEgressAuditService`)
- Parses Envoy JSON log lines emitted by sidecar containers:
  ```json
  {"timestamp":"2026-10-31T15:00:00Z","protocol":"TLS","sni":"malicious-domain.com","destination":"198.51.100.42:443","action":"BLOCKED"}
  ```
- Maintains a recent ring buffer of outbound connection events (`EgressConnectionLogEntry`) per workspace/task.
- When `action == "BLOCKED"`, dispatches a telemetry alert over SignalR to `/monitoring` and the active task view:
  `"⚠️ Blocked outbound connection from sandbox: Attempted to reach 'malicious-domain.com:443' (not in allowed whitelist)."`

### 2.2 In-App Settings UI (`ContainerEgressSettingsTab.razor`)
In accordance with [AGENTS.md](../../AGENTS.md), all domain matching, profile resolution, and log parsing reside in `NightCodeForge.Core` services:
- **Redirection Mode Selector:** Radio toggle between `Transparent (Istio-style iptables, Recommended)` and `Explicit HTTP_PROXY (Fallback)`, with a badge showing whether the host Docker daemon supports `--cap-add=NET_ADMIN`.
- **Profile Selector & Domain List:** Preset selector (`Strict Air-Gapped`, `Package Registries Only`, `Enterprise Intranet Only`, `Custom`), editable table of allowed domain patterns (`*.nuget.org`, `github.com`), and an interactive **"Test Domain"** input showing whether a given FQDN is allowed or blocked.
- **Live Egress Audit Stream:** Table of recent outbound connections from `IEnvoyEgressAuditService`, highlighting `BLOCKED` attempts in red.

---

## 3. Definition of Done (DoD) Checklist

- [ ] Implement `IEnvoyEgressAuditService` parsing Envoy JSON stdout logs and publishing `EgressBlockedEvent` alerts to `IOperationsTelemetryService` / `/monitoring`.
- [ ] Create `src/NightCodeForge.Web/Components/Settings/ContainerEgressSettingsTab.razor` and register it in `Settings.razor`.
- [ ] Add unit tests in `NightCodeForge.Core.Tests` for `EnvoyEgressAuditService` JSON log parsing and blocked-event notification.
- [ ] Add bUnit tests in `NightCodeForge.Web.Tests` for `ContainerEgressSettingsTab.razor` profile switching, domain tester, and audit table rendering.

