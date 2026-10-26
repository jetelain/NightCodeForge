# 🔒 Step 12.16: Remote Tailscale Mesh Access, HTTPS & Transient Network Resilience

> **Phase 12 Implementation Step (Extend Features - Deep Integrations & Remote Experience)**  
> *Parent Plan:* [Phase 12 Master Plan](../12_EXTEND_FEATURES.md)  
> *Related Steps:* [Step 12.8: Mobile-Responsive UI & PWA](STEP_12_8_MOBILE_RESPONSIVE_UI_AND_PWA.md) | [Step 12.12: Voice Support](STEP_12_12_VOICE_SUPPORT_AND_CONTINUOUS_DICTATION.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests`, `tools/` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **Zero-Configuration Private Remote Access:** Enable developers to securely manage NightCodeForge from anywhere (smartphones, tablets, laptops) via **Tailscale** private WireGuard mesh VPN without opening public firewall ports or configuring port forwarding.
2. **Automated Trusted HTTPS via Tailscale Serve:** Terminate TLS using Tailscale MagicDNS and automated Let's Encrypt certificates (`https://<node>.<tailnet>.ts.net`), satisfying modern browser **Secure Context** requirements (`window.isSecureContext === true`) for Service Workers, Web Notifications ([Step 12.8](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/docs/plan/phase-12/STEP_12_8_MOBILE_RESPONSIVE_UI_AND_PWA.md)), and Web Speech API dictation ([Step 12.12](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/docs/plan/phase-12/STEP_12_12_VOICE_SUPPORT_AND_CONTINUOUS_DICTATION.md)).
3. **Attached Process & Graceful Shutdown Lifecycle (Zero Desktop Overhead):** Avoid persistent background daemon residue (`tailscale serve --bg`). Run the Tailscale Serve proxy as an **attached managed process** tied directly to the NightCodeForge application lifecycle (via `TailscaleServeHostedService`). When NightCodeForge stops or is disabled, the serve route is automatically reset (`tailscale serve reset`), ensuring zero idle CPU/network overhead and no lingering 502 endpoints on the host machine.
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
- Navigating to a local development machine over plain HTTP using an IP or LAN hostname (e.g. `http://192.168.1.50:5000` or `http://100.x.y.z:5000`) marks the origin as **insecure**, silently disabling service workers, notifications, and speech recognition on mobile browsers.
- Generating self-signed TLS certificates introduces untrusted certificate warnings ("Your connection is not private") and requires laborious manual root CA installation on mobile devices.

### 2.3 The Pitfalls of Persistent Background Serve (`--bg`)
- Using `tailscale serve --bg` configures a persistent reverse proxy inside the operating system's Tailscale daemon that outlives the web application.
- When NightCodeForge is closed, the proxy continues listening and responding to external tailnet requests with orphaned `502 Bad Gateway` errors.
- It consumes unnecessary desktop host resources and leaves lingering external-facing entry points when the developer is no longer working.
- **The Solution:** Manage Tailscale Serve as an **attached lifecycle service**. When NightCodeForge boots, it enables the route and announces the MagicDNS URL; when NightCodeForge stops, its graceful shutdown hook immediately executes `tailscale serve reset` to return the host to a clean, idle state.

---

## 3. Technical & Behavioral Specifications

### 3.1 Network Topology & Architecture Overview

```
[Mobile Phone / Laptop (Connected to Tailnet)]
       │
       │  Encrypted WireGuard Tunnel
       │  HTTPS (Valid Let's Encrypt TLS via MagicDNS)
       │  e.g. https://forge-pc.tailnet-xyz.ts.net
       ▼
[Tailscale Serve (Managed Attached Process)]
       │  • Started on Kestrel boot
       │  • Tear-down & reset on graceful shutdown
       │  HTTP / WebSocket Loopback (127.0.0.1:5000)
       ▼
[Kestrel Web Host (NightCodeForge.Web)]
       ├── Blazor Interactive Server Circuits (/_blazor)
       ├── SignalR Orchestrator Telemetry Hub (/hubs/orchestrator)
       ├── PWA Manifest & Static App Shell Assets
       └── REST APIs & Static Content
```

---

### 3.2 Application-Managed Lifecycle (`TailscaleServeHostedService`)

Instead of manual terminal commands, NightCodeForge manages the Tailscale Serve lifecycle directly through an ASP.NET Core `IHostedService`:

#### A. Configuration Schema (`appsettings.json`)
```json
{
  "Tailscale": {
    "AutoServe": false,
    "TargetPort": 5000,
    "Protocol": "https"
  }
}
```
*(Opt-in via configuration or CLI flag `--tailscale` / `--serve-tailscale` when starting `ncf-web`.)*

#### B. Lifecycle Phases

1. **Startup (`StartAsync`):**
   - If `Tailscale:AutoServe` is false, the service remains dormant.
   - If enabled:
     1. Probes for `tailscale` CLI availability in system `PATH`. If missing, logs an informative warning and skips activation without crashing the application.
     2. Verifies the host is logged into a tailnet via `tailscale status --json`.
     3. Runs a defensive `tailscale serve reset` to clear any stale routes from prior abnormal terminations.
     4. Starts the attached process:
        ```bash
        tailscale serve https / http://127.0.0.1:5000
        ```
     5. Extracts the node's MagicDNS FQDN (e.g. `https://my-workstation.tailnet-xyz.ts.net`) and prints a prominent console banner:
        ```text
        ========================================================================
        🔒 NightCodeForge Remote Access Ready via Tailscale
        🌐 MagicDNS HTTPS: https://my-workstation.tailnet-xyz.ts.net
        📱 Scan or visit from any device on your tailnet for PWA & Notifications
        ========================================================================
        ```

2. **Graceful Shutdown (`StopAsync` / `IHostApplicationLifetime.ApplicationStopping`):**
   - When the host application begins shutting down (e.g. Ctrl+C in console, process termination signal, or container stop):
     1. Terminates the attached child process.
     2. Dispatches `tailscale serve reset` with a short timeout (3 seconds).
     3. Logs confirmation: `[Tailscale] Remote serve proxy cleanly disabled. Zero desktop overhead remaining.`

3. **Crash & Abrupt Termination Guard:**
   - On the subsequent launch of `TailscaleServeHostedService`, the defensive pre-flight reset ensures any abandoned serve state is wiped clean before re-establishing the proxy.

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

For developers preferring external script execution over in-app hosted services, provide scripts with attached process traps:

1. **`tools/tailscale-serve.ps1` (PowerShell):**
   - Validates Tailscale CLI and active login.
   - Starts `tailscale serve https / http://127.0.0.1:5000` attached.
   - Registers a `finally` block / exit trap (`trap { tailscale serve reset }`) ensuring the route resets if the PowerShell window is closed or interrupted.
2. **`tools/tailscale-serve.sh` (Bash / Linux / macOS):**
   - Traps `SIGINT`, `SIGTERM`, and `EXIT` to execute `tailscale serve reset`.

---

## 4. Implementation Checklist & Deliverables

- [ ] **Attached Process & Lifecycle Integration:**
  - [ ] Implement `TailscaleServeHostedService` inheriting from `IHostedService` with `IHostApplicationLifetime` registration.
  - [ ] Add `Tailscale` configuration section to `appsettings.json` with opt-in `AutoServe` flag.
  - [ ] Add CLI flag `--tailscale` to startup parser in `Program.cs`.
  - [ ] Implement startup pre-flight cleanup and shutdown cleanup (`tailscale serve reset`).
  - [ ] Output formatted MagicDNS HTTPS URL in startup logs.
- [ ] **Kestrel & Forwarded Headers Integration:**
  - [ ] Add `ForwardedHeadersOptions` in `Program.cs` for `X-Forwarded-For` and `X-Forwarded-Proto`.
  - [ ] Verify Blazor Server WebSockets operate cleanly behind loopback reverse proxy.
- [ ] **Connection Resilience & Reconnect UX:**
  - [ ] Ensure `OrchestratorHubClientConnection.cs` executes automatic reconnect with exponential backoff on network handoffs.
  - [ ] Verify `ReconnectModal.razor` displays non-blocking mobile-friendly reconnect banners.
  - [ ] Wire `visibilitychange` state sync to refresh dashboard data on mobile window focus.
- [ ] **Operational Tooling & Documentation:**
  - [ ] Create `tools/tailscale-serve.ps1` with exit trap and URL display.
  - [ ] Create `tools/tailscale-serve.sh` for Linux and WSL2 hosts with trap cleanup.
  - [ ] Document Tailscale Serve setup, MagicDNS prerequisites, and remote mobile workflows in `docs/` and project README.
- [ ] **Testing & Quality Assurance:**
  - [ ] Unit tests for `TailscaleServeHostedService` asserting process invocation and cleanup on shutdown.
  - [ ] Integration test asserting Kestrel respects `X-Forwarded-Proto: https` headers.
  - [ ] Verification over Tailscale HTTPS with valid SSL lock in mobile Chrome and iOS Safari.
  - [ ] Verification that stopping NightCodeForge cleanly removes the serve route without lingering 502 endpoints.

