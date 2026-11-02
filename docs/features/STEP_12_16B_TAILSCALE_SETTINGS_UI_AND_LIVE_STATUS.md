# 🔒 Step 12.16B: Tailscale Settings UI, QR Code Mobile Onboarding & Live Mesh Status

> **Feature Implementation Step (Part 2 of 2 — `/settings` UI Card, Hot-Apply & SVG QR Code)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.16A: Tailscale Serve Hosted Service & Forwarded Headers](../archive/phase-12/STEP_12_16A_TAILSCALE_SERVE_SERVICE_AND_FORWARDED_HEADERS.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

With `TailscaleServeHostedService` in place ([Step 12.16A](../archive/phase-12/STEP_12_16A_TAILSCALE_SERVE_SERVICE_AND_FORWARDED_HEADERS.md)), this step (Part 2 of Step 12.16) provides a first-class **Remote & Tailscale** settings tab (`TailscaleSettingsCard.razor`) under `/settings` so developers can enable, reconfigure, and onboard mobile devices without touching terminal commands or configuration files:
1. **Interactive Hot-Apply Controls:** Toggle `AutoServe`, switch between **Dedicated Tailscale Service (`svc:ncf`)** and **Device Node Mode**, configure `ServiceName` and `ListenPort`, persist to `config/config.json` / `SystemSettings`, and hot-apply changes immediately via `ITailscaleServeService`.
2. **Live Status & Diagnostics Card:** Display real-time daemon status (`🟢 Active`, `🟡 Draining / Applying`, `⚪ Inactive`, `🔴 Tailscale Daemon Offline / Unauthenticated`), Tailscale CLI version, tailnet domain, WireGuard IP, and read-only auto-detected loopback target (`127.0.0.1:5270`).
3. **One-Click MagicDNS URL & Dynamic Inline SVG QR Code:** Render the resolved HTTPS MagicDNS URL (`https://ncf.<tailnet-name>.ts.net`) with a "Copy Link" button and an inline SVG QR code so developers can scan with their phone camera to immediately open and install the mobile PWA.

---

## 2. UI & Presentation Specifications (`TailscaleSettingsCard.razor`)

In accordance with [AGENTS.md](../../AGENTS.md), all CLI status parsing, configuration persistence, and SVG QR code matrix generation reside in `ITailscaleServeService` / helper classes in `NightCodeForge.Web/Services`, keeping `TailscaleSettingsCard.razor` strictly presentational.

### 2.1 Interactive Controls
- **Master Toggle:** Enable / Disable Remote Access (`AutoServe`).
- **Exposition Mode Switcher:**
  - 🌟 **Dedicated Tailscale Service (`svc:ncf`)** *(Recommended)* — Publishes NightCodeForge under `https://ncf.<tailnet-name>.ts.net`.
  - 💻 **Device Node Mode** — Publishes directly to `https://<node-name>.<tailnet-name>.ts.net`.
- **Service Name:** Custom text input (default: `svc:ncf`).
- **Port Configuration:** Tailscale listen port (default: `443`), plus read-only display of the auto-detected Kestrel loopback target (`127.0.0.1:<web-port>`).
- **Auto-Advertise Toggle:** Automatically advertise service route across the tailnet on start.
- **"Save & Apply" Action:** Atomically persists updated settings and signals `ITailscaleServeService` to hot-apply routes without restarting the web app.

### 2.2 Live Status, Diagnostics & Mobile Onboarding Card
- **Real-Time Status Pill:** `🟢 Active`, `🟡 Draining / Applying`, `⚪ Inactive`, or `🔴 Tailscale Daemon Offline / Unauthenticated`.
- **MagicDNS Link & Copy Button:** Displays the active `https://...ts.net` URL.
- **Inline SVG QR Code:** Renders a self-contained SVG QR code for the active MagicDNS HTTPS URL (zero external CDN/network calls, preserving offline/air-gapped integrity).
- **Daemon Telemetry:** Displays Tailscale CLI version, tailnet name, node IP, and active serve endpoints.

---

## 3. Implementation Checklist & Deliverables

- [ ] Implement SVG QR code generator helper and status model in `NightCodeForge.Web/Services`.
- [ ] Build `TailscaleSettingsCard.razor` and integrate it into [`Settings.razor`](../../src/NightCodeForge.Web/Components/Pages/Settings.razor) (Global Scope -> Remote & Tailscale tab).
- [ ] Wire "Save & Apply" to persist settings to `config/config.json` / `SystemSettings` and invoke `ITailscaleServeService` hot-apply.
- [ ] Add bUnit tests in `NightCodeForge.Web.Tests` verifying `TailscaleSettingsCard.razor` mode switching, status pill rendering, QR code SVG output, and hot-apply invocation.

