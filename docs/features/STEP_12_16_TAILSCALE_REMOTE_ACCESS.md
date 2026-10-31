# 🔒 Step 12.16: Remote Tailscale Mesh Access, HTTPS & Transient Network Resilience

> **Phase 12 Implementation Step (Extend Features - Deep Integrations & Remote Experience)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Related Steps:* [Step 12.8: Mobile-Responsive UI & PWA](STEP_12_8_MOBILE_RESPONSIVE_UI_AND_PWA.md) | [Step 12.12: Voice Support](STEP_12_12_VOICE_SUPPORT_AND_CONTINUOUS_DICTATION.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests`, `tools/` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **Zero-Configuration Private Remote Access:** Enable developers to securely manage NightCodeForge from anywhere (smartphones, tablets, laptops) via **Tailscale** private WireGuard mesh VPN without opening public firewall ports or configuring port forwarding.
2. **Dedicated Service Identity & Automated Trusted HTTPS (`svc:ncf`):** Publish NightCodeForge as a dedicated named **Tailscale Service** (`svc:ncf`) rather than binding to the physical workstation's host name. Expose the service over HTTPS with automated Let's Encrypt certificates (`https://ncf.<tailnet-name>.ts.net`) using modern Tailscale CLI syntax (`tailscale serve --service=svc:ncf --https=443 127.0.0.1:<web-port>` and `tailscale serve advertise svc:ncf`). The target port is automatically detected from the actual Web UI runtime binding (e.g. `5270`, configured by `publish.ps1` or Step 12.18 OOBE) without requiring user configuration. This satisfies modern browser **Secure Context** requirements (`window.isSecureContext === true`) for Service Workers, Web Notifications ([Step 12.8](STEP_12_8_MOBILE_RESPONSIVE_UI_AND_PWA.md)), and Web Speech API dictation ([Step 12.12](STEP_12_12_VOICE_SUPPORT_AND_CONTINUOUS_DICTATION.md)), with an intuitive and memorable URL. A fallback to device-level serve (`tailscale serve --https=443 127.0.0.1:<web-port>`) is supported when running in tailnets without Services configured.
3. **Managed Service Lifecycle & Graceful Teardown (Zero Desktop Overhead):** Avoid persistent background daemon residue and orphaned `502 Bad Gateway` endpoints. Because Tailscale Services persist in the daemon background by default, `TailscaleServeHostedService` actively manages the service lifecycle:
   - On startup, the service route is configured and advertised (`tailscale serve advertise svc:ncf`).
   - On graceful shutdown, incoming connections are gracefully drained (`tailscale serve drain svc:ncf`), the endpoint is cleared (`tailscale serve clear svc:ncf`), and stale routes are reset (`tailscale serve reset`), ensuring zero idle CPU/network overhead when NightCodeForge is stopped.
4. **Seamless WebSocket Proxying:** Ensure full, bi-directional WebSocket pass-through for Blazor Interactive Server circuits (`/_blazor`) and real-time SignalR telemetry streams (`/hubs/orchestrator`).
5. **Transient Network Resilience:** Transparently handle mobile network shifts (e.g. WiFi to cellular handoffs, IP roaming, tunnel reconnection) with automatic exponential backoff, circuit reconnection, and non-blocking status banners.

---

## 2. Problem Statement & Motivation

### 2.1 The Remote Developer Pattern
- NightCodeForge is frequently deployed on a developer's high-performance workstation or dedicated home/office LAN server equipped with local GPUs (NVIDIA RTX / Ollama nodes).
- Developers need to supervise pipelines, answer agent questions, review code diffs, or capture ideas while commuting or away from their workstations.

### 2.2 The Insecure Context Barrier (`window.isSecureContext`)
- Modern web security standards mandate a **Secure Context** for critical APIs:
  - `navigator.serviceWorker.register` (required for PWA and Web Notifications)
  - `Notification.requestPermission` and `showNotification`
  - `navigator.mediaDevices.getUserMedia` & `SpeechRecognition` (required for voice dictation)
- Navigating to a local development machine over plain HTTP using an IP or LAN hostname (e.g. `http://192.168.1.50:5270` or `http://100.x.y.z:5270`) marks the origin as **insecure**, silently disabling service workers, notifications, and speech recognition on mobile browsers.
- Generating self-signed TLS certificates introduces untrusted certificate warnings ("Your connection is not private") and requires laborious manual root CA installation on mobile devices.

### 2.3 Host-Coupled Node Addressing vs. Tailscale Services (`svc:ncf`)
- **Host Coupling Limitation:** Standard device-level `tailscale serve` binds the proxy directly to the host machine's MagicDNS name (e.g., `https://julien-workstation.tailnet-xyz.ts.net`).
  - If the developer switches machines, moves NCF to a home server, or runs inside a container/WSL2, the URL changes, breaking browser bookmarks, PWA home-screen installations, and notification subscriptions.
  - Workstation hostnames can be lengthy, difficult to remember, or prone to typing errors on mobile keyboards.
- **The Solution: Tailscale Service `svc:ncf`:**
  - Modern Tailscale allows advertising named **Services** decoupled from specific device identities.
  - Exposing NightCodeForge as `svc:ncf` yields a clean, stable MagicDNS URL: `https://ncf.<tailnet-name>.ts.net`.
  - Service advertisement (`tailscale serve advertise svc:ncf`) allows easy discovery and route propagation across all tailnet devices.
  - If NCF is later moved to a different machine or secondary host, it simply advertises `svc:ncf`, preserving the same URL without reconfiguring clients.

### 2.4 The Pitfalls of Persistent Background Serve & Clean Teardown
- Using `tailscale serve --bg` or running Tailscale Services leaves persistent reverse proxy configuration in the host's background Tailscale daemon that outlives the web application.
- When NightCodeForge terminates, the proxy continues listening and returning orphaned `502 Bad Gateway` errors to tailnet users.
- **The Solution:** Manage the lifecycle directly in `TailscaleServeHostedService`:
  - When NightCodeForge starts: configure the route and advertise `svc:ncf`.
  - When NightCodeForge stops: execute `tailscale serve drain svc:ncf` (to let active streams complete cleanly), then `tailscale serve clear svc:ncf` and `tailscale serve reset` to return the host to a clean, idle state with zero background overhead.

---

## 3. Technical & Behavioral Specifications

### 3.1 Network Topology & Architecture Overview

```
[Mobile Phone / Tablet / Laptop (Connected to Tailnet)]
       │
       │  Encrypted WireGuard Tunnel
       │  HTTPS (Automated Let's Encrypt TLS via MagicDNS)
       │  e.g. https://ncf.tailnet-xyz.ts.net (or https://node.tailnet-xyz.ts.net)
       ▼
[Tailscale Service (Virtual IP) / Tailscale Serve Reverse Proxy]
       │  • Configured: `tailscale serve --service=svc:ncf --https=443 127.0.0.1:<web-port>`
       │  • Advertised: `tailscale serve advertise svc:ncf`
       │  • Graceful Drain & Teardown on shutdown
       │  HTTP / WebSocket Loopback (127.0.0.1:<web-port>, e.g. 5270)
       ▼
[Kestrel Web Host (NightCodeForge.Web)]
       ├── Blazor Interactive Server Circuits (/_blazor)
       ├── SignalR Orchestrator Telemetry Hub (/hubs/orchestrator)
       ├── PWA Manifest & Static App Shell Assets
       └── REST APIs & Static Content
```

---

### 3.2 Layered Configuration & Application-Managed Lifecycle

To ensure an effortless out-of-the-box experience, NightCodeForge adopts a **zero-file-editing user philosophy**:
- **Factory Settings (`appsettings.json`):** Shipped directly with the binary distribution, Docker container, or installer package. Treated as **immutable read-only defaults**. Users must **never** be instructed to edit `appsettings.json` directly, as future software updates or package reinstallations will overwrite it.
- **Persistent User Configuration Overlay (`config/config.json` & Database):** All user-defined settings are persisted in a durable user layer—either the `config/config.json` file overlay (loaded via `builder.Configuration.AddJsonFile("config/config.json", optional: true, reloadOnChange: true)`) or the SQLite `SystemSettings` table (`nightcodeforge.db`). This file/database resides in the persistent user data directory and survives application upgrades, Docker container redeployments, and 1-click updates.
- **First-Class Web UI Configuration:** Developers can configure and toggle everything directly within the NightCodeForge Web UI (`/settings`) without touching any configuration file or terminal.

#### A. Factory Defaults (`appsettings.json`)
```json
{
  "Tailscale": {
    "AutoServe": false,
    "ListenPort": 443,
    "Protocol": "https",
    "ServiceName": "svc:ncf",
    "Advertise": true
  }
}
```

*Configuration Parameters:*
- `AutoServe` (`bool`): Enables or disables automatic Tailscale Serve management on application startup (factory default: `false`).
- `ListenPort` (`int`): Tailscale external port (default: `443` for HTTPS).
- `Protocol` (`string`): Transport protocol (`"https"` or `"http"`, default: `"https"`).
- `ServiceName` (`string`): The Tailscale Service name (default: `"svc:ncf"`). If set to empty or `null`, the service falls back to device-level node serve (`https://<node>.<tailnet>.ts.net`).
- `Advertise` (`bool`): Whether to automatically execute `tailscale serve advertise <service>` after configuring the service route (default: `true`).

*Dynamic Target Port Resolution (No User Configuration Needed):*
- The local proxy target port is **not configured by the user**.
- `TailscaleServeHostedService` automatically queries the active Kestrel listening addresses at runtime (via ASP.NET Core `IServerAddressesFeature` or `IConfiguration["Urls"]` / `ASPNETCORE_URLS`).
- By default, setup scripts ([`publish.ps1`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/publish.ps1)) and the Step 12.18 OOBE setup wizard configure `"Urls": "http://localhost:5270"`, so Tailscale automatically routes to `127.0.0.1:5270` without manual user intervention or port guessing.

*(Opt-in via Web UI Settings, durable `config/config.json` overlay, or CLI flags `--tailscale` / `--serve-tailscale`, with optional `--tailscale-service <name>` and `--tailscale-port <port>` when starting `ncf-web`.)*

#### B. First-Class Web UI Settings & Dynamic Lifecycle Controls (`TailscaleSettingsCard.razor`)

A dedicated **Remote & Tailscale** card/tab is available under `/settings` (Global Scope):

1. **Interactive Controls:**
   - **Master Toggle:** Enable / Disable Remote Access (`AutoServe`).
   - **Exposition Mode Switcher:**
     - 🌟 **Dedicated Tailscale Service (`svc:ncf`)** *(Recommended)* — Publishes NightCodeForge under a virtual IP and MagicDNS name (`https://ncf.<tailnet-name>.ts.net`), completely decoupled from the machine's hardware identity.
     - 💻 **Device Node Mode** — Publishes directly to the machine's MagicDNS name (`https://<node-name>.<tailnet-name>.ts.net`).
   - **Service Name:** Custom text input (default: `svc:ncf`).
   - **Port Configuration:** Tailscale listen port (default: `443`). The target Kestrel loopback port is automatically detected from the active Web UI binding and displayed as read-only diagnostic info (e.g. `127.0.0.1:5270`).
   - **Auto-Advertise Toggle:** Automatically advertise service route across the tailnet on start.
   - **"Save & Apply" Action:**
     - Atomically persists the updated configuration to `config/config.json` (and `SystemSettings`).
     - Immediately signals `TailscaleServeHostedService` to hot-apply the new settings (starting, stopping, or re-advertising routes without requiring an application restart).

2. **Live Status, Diagnostics & Mobile Onboarding Card:**
   - **Real-Time Status Indicator:** Visual pill displaying current state (`🟢 Active`, `🟡 Draining / Applying`, `⚪ Inactive`, `🔴 Tailscale Daemon Offline / Unauthenticated`).
   - **One-Click MagicDNS Link:** Displays the resolved HTTPS URL (`https://ncf.<tailnet-name>.ts.net`) with a "Copy Link" button.
   - **Instant Dynamic QR Code:** Renders an inline SVG QR code pointing directly to the MagicDNS URL. Developers can point their smartphone or tablet camera at the screen to immediately launch the mobile PWA on their tailnet without typing long URLs.
   - **Daemon Telemetry:** Displays detected Tailscale CLI version, tailnet domain name, node WireGuard IP, detected loopback target (`127.0.0.1:<web-port>`), and active serve endpoints derived from `tailscale serve status --json`.

#### C. Lifecycle Phases (`TailscaleServeHostedService`)

1. **Startup (`StartAsync`):**
   - If `Tailscale:AutoServe` is false, the service remains dormant.
   - If enabled:
     1. Probes for `tailscale` CLI availability in system `PATH` (e.g. `tailscale version`). If missing, logs an informative warning and skips activation without crashing the application.
     2. Verifies the host is logged into a tailnet via `tailscale status --json`.
     3. Queries the active Kestrel listening port (e.g. `5270`) via `IServerAddressesFeature` or `IConfiguration["Urls"]`.
     4. Runs a defensive pre-flight cleanup (`tailscale serve reset` or inspecting `tailscale serve get-config --all`) to clear any stale or orphaned routes from prior abnormal terminations.
     5. Configures the serve proxy using modern Tailscale CLI v1.52+ syntax targeting the auto-detected Web UI port:
        - **Service Mode (`ServiceName` configured, e.g. `"svc:ncf"`):**
          ```bash
          # Port 5270 auto-detected from active Web UI Kestrel binding
          tailscale serve --service=svc:ncf --https=443 127.0.0.1:5270
          ```
          If `Advertise` is true:
          ```bash
          tailscale serve advertise svc:ncf
          ```
        - **Device Mode (`ServiceName` omitted or null):**
          ```bash
          tailscale serve --https=443 127.0.0.1:5270
          ```
     6. Extracts the MagicDNS FQDN from `tailscale serve status --json` or `tailscale status --json` and prints a prominent console banner:
        - **For Service Mode:**
          ```text
          ========================================================================
          🔒 NightCodeForge Remote Access Ready via Tailscale Service
          🌐 Service URL: https://ncf.tailnet-xyz.ts.net
          📱 Accessible from any authorized device on your tailnet
          ========================================================================
          ```
        - **For Device Mode:**
          ```text
          ========================================================================
          🔒 NightCodeForge Remote Access Ready via Tailscale (Device Mode)
          🌐 MagicDNS HTTPS: https://my-workstation.tailnet-xyz.ts.net
          📱 Scan or visit from any device on your tailnet for PWA & Notifications
          ========================================================================
          ```

2. **Graceful Shutdown (`StopAsync` / `IHostApplicationLifetime.ApplicationStopping`):**
   - When the host application begins shutting down (e.g. Ctrl+C in console, process termination signal, or container stop):
     1. If running in Service Mode:
        - Dispatches `tailscale serve drain svc:ncf` with a short timeout (3 seconds) to allow active HTTP/WebSocket requests to complete gracefully.
        - Dispatches `tailscale serve clear svc:ncf` (or `tailscale serve --service=svc:ncf --https=443 off`) to remove the endpoint configuration.
     2. Dispatches `tailscale serve reset` to ensure all serve routes on the host are cleanly restored.
     3. Terminates any attached child processes.
     4. Logs confirmation: `[Tailscale] Remote serve proxy drained and cleanly reset. Zero desktop overhead remaining.`

3. **Crash & Abrupt Termination Guard:**
   - On subsequent launches of `TailscaleServeHostedService`, the defensive pre-flight check and reset ensure any abandoned serve state or un-drained service endpoints are wiped clean before establishing the proxy.

---

### 3.3 ASP.NET Core Forwarded Headers & Kestrel Configuration

When running behind Tailscale Serve, Kestrel receives requests proxied from `127.0.0.1`. The application must accurately recognize the original client protocol (`https`) and client IP.

1. **Forwarded Headers Middleware:**
   Configure `ForwardedHeadersOptions` in [Program.cs](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Program.cs):
   ```csharp
   builder.Services.Configure<ForwardedHeadersOptions>(options =>
   {
       options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
       options.KnownNetworks.Clear();
       options.KnownProxies.Clear();
   });
   ```
2. **WebSocket Support:**
   Tailscale Serve natively supports HTTP/1.1 WebSocket upgrade requests. Kestrel requires no custom WebSocket protocol handlers beyond standard Blazor Server and SignalR endpoints.

---

### 3.4 Transient Network Resilience & Reconnection Handling

Mobile connections over mesh VPNs frequently transition across networks (e.g. switching between home WiFi, office WiFi, and cellular 5G), causing momentary IP shifts and socket disruptions.

1. **SignalR Client Auto-Reconnect:**
   In [OrchestratorHubClientConnection.cs](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/OrchestratorHubClientConnection.cs), configure robust reconnection policies:
   - Exponential backoff intervals: `0s, 2s, 5s, 10s, 30s`.
   - On reconnect, automatically re-subscribe to active task telemetry groups.
2. **Blazor Server Circuit Recovery:**
   - Utilize Blazor's built-in circuit reconnection handler customized with theme-consistent styling in `ReconnectModal.razor`.
   - Non-blocking notification banner: `"Reconnecting to Forge..."` on transient drops, auto-dismissing as soon as the circuit resumes.
3. **Visibility & State Synchronization:**
   - When returning to the foreground (`visibilitychange` event), client triggers a fast telemetry state poll to synchronize active task cards and stage progress without full page reload.

---

### 3.5 Standalone Operational Tooling (Optional CLI Mode)

For developers preferring external script execution over in-app hosted services, provide standalone scripts with modern CLI commands and exit traps:

1. **`tools/tailscale-serve.ps1` (PowerShell):**
   - Validates Tailscale CLI and active tailnet login.
   - Configures the service and advertises it, defaulting target to the standard NightCodeForge port (`127.0.0.1:5270`):
     ```powershell
     param(
         [string]$Service = "svc:ncf",
         [int]$Port = 443,
         [string]$Target = "127.0.0.1:5270"
     )
     try {
         if ($Service) {
             tailscale serve --service=$Service --https=$Port $Target
             tailscale serve advertise $Service
         } else {
             tailscale serve --https=$Port $Target
         }
         Write-Host "Tailscale Serve active. Press Ctrl+C to stop."
         while ($true) { Start-Sleep -Seconds 1 }
     } finally {
         Write-Host "Cleaning up Tailscale Serve..."
         if ($Service) {
             tailscale serve drain $Service
             tailscale serve clear $Service
         }
         tailscale serve reset
     }
     ```
2. **`tools/tailscale-serve.sh` (Bash / Linux / macOS):**
   - Traps `SIGINT`, `SIGTERM`, and `EXIT` to execute `tailscale serve drain <service>`, `tailscale serve clear <service>`, and `tailscale serve reset`. Defaults target to `127.0.0.1:5270`.

---

## 4. Implementation Checklist & Deliverables

- [ ] **Attached Process & Lifecycle Integration:**
  - [ ] Implement `TailscaleServeHostedService` inheriting from `IHostedService` with `IHostApplicationLifetime` registration.
  - [ ] Add factory defaults `Tailscale` configuration section to `appsettings.json` with `AutoServe: false`, `ListenPort: 443`, `Protocol: "https"`, `ServiceName: "svc:ncf"`, and `Advertise: true`.
  - [ ] Implement runtime Web UI target port auto-detection via `IServerAddressesFeature` / `Urls` (default `5270` from `publish.ps1` / OOBE), completely eliminating manual target port configuration friction.
  - [ ] Implement layered user configuration overlay support (`config/config.json` and `SystemSettings` table persistence) to preserve user configuration across software updates.
  - [ ] Add CLI flags (`--tailscale`, `--tailscale-service`, `--tailscale-port`) to startup parser in `Program.cs`.
  - [ ] Implement modern Tailscale CLI v1.52+ command execution (`tailscale serve [flags] <target>`).
  - [ ] Support Tailscale Service mode (`--service=svc:ncf`) with automatic service advertisement (`tailscale serve advertise svc:ncf`).
  - [ ] Support fallback to device-level serve when `ServiceName` is empty or null.
  - [ ] Implement startup pre-flight cleanup and shutdown graceful drain (`tailscale serve drain`), endpoint clearing (`tailscale serve clear`), and reset (`tailscale serve reset`).
  - [ ] Output formatted MagicDNS HTTPS service/node URL banner in startup logs.
- [ ] **First-Class Web UI Settings & Dynamic Hot-Apply:**
  - [ ] Implement `TailscaleSettingsCard.razor` under `/settings` (Global Scope -> Remote & Tailscale tab).
  - [ ] Add interactive toggles for Remote Access (`AutoServe`), Mode Selection (`svc:ncf` vs. Device Node), Service Name, Listen Port (443), and Auto-Advertise.
  - [ ] Display auto-detected target port (`127.0.0.1:5270`) as read-only diagnostic info without exposing editable target port inputs.
  - [ ] Implement "Save & Apply" action triggering atomic persistence to `config/config.json` and hot start/restart/stop of `TailscaleServeHostedService`.
  - [ ] Build real-time connection status card (active state, MagicDNS URL, copy button, daemon version).
  - [ ] Implement dynamic inline SVG QR Code generation for instant mobile onboarding.
- [ ] **Kestrel & Forwarded Headers Integration:**
  - [ ] Add `ForwardedHeadersOptions` in `Program.cs` for `X-Forwarded-For` and `X-Forwarded-Proto`.
  - [ ] Verify Blazor Server WebSockets operate cleanly behind loopback reverse proxy.
- [ ] **Connection Resilience & Reconnect UX:**
  - [ ] Ensure `OrchestratorHubClientConnection.cs` executes automatic reconnect with exponential backoff on network handoffs.
  - [ ] Verify `ReconnectModal.razor` displays non-blocking mobile-friendly reconnect banners.
  - [ ] Wire `visibilitychange` state sync to refresh dashboard data on mobile window focus.
- [ ] **Operational Tooling & Documentation:**
  - [ ] Create `tools/tailscale-serve.ps1` with modern CLI flags, default target `127.0.0.1:5270`, service advertisement, and drain/reset exit traps.
  - [ ] Create `tools/tailscale-serve.sh` for Linux and WSL2 hosts with trap cleanup.
  - [ ] Document Tailscale Serve setup, Tailscale Service (`svc:ncf`) prerequisites (tags/approvals), Web UI management, and remote mobile workflows in `docs/` and project README.
- [ ] **Testing & Quality Assurance:**
  - [ ] Unit tests for `TailscaleServeHostedService` asserting process invocation with dynamically resolved Kestrel port, service vs node mode selection, hot-apply updates, and graceful drain/cleanup on shutdown.
  - [ ] Integration test asserting Kestrel respects `X-Forwarded-Proto: https` headers.
  - [ ] Verification over Tailscale HTTPS with valid SSL lock in mobile Chrome and iOS Safari.
  - [ ] Verification that stopping NightCodeForge cleanly drains and clears the serve route without lingering 502 endpoints.


