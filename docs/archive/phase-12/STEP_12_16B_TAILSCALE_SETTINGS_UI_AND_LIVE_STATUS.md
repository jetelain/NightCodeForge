# 🔒 Step 12.16B: Tailscale Settings UI, QR Code Mobile Onboarding & Live Mesh Status

> **Feature Implementation Step (Part 2 of 2 — `/settings` UI Card, Hot-Apply & SVG QR Code)**  
> *Parent Plan:* [Phase 12 Master Plan](../12_EXTEND_FEATURES.md)  
> *Prerequisite Step:* [Step 12.16A: Tailscale Serve Hosted Service & Forwarded Headers](STEP_12_16A_TAILSCALE_SERVE_SERVICE_AND_FORWARDED_HEADERS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests` | *Status:* ✅ Completed | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

With [`TailscaleServeHostedService`](../../../src/NightCodeForge.Web/Tailscale/TailscaleServeHostedService.cs) in place ([Step 12.16A](STEP_12_16A_TAILSCALE_SERVE_SERVICE_AND_FORWARDED_HEADERS.md)), this step (Part 2 of Step 12.16) provides a first-class **Remote & Tailscale** settings tab (`src/NightCodeForge.Web/Components/Settings/TailscaleSettingsCard.razor`) under `/settings` so developers can enable, reconfigure, and onboard mobile devices without touching terminal commands or configuration files:
1. **Interactive Hot-Apply Controls:** Toggle `AutoServe`, switch between **Dedicated Tailscale Service (`svc:ncf`)** and **Device Node Mode**, configure `ServiceName`, `ListenPort`, and `Advertise`, persist to `config/config.json` (preserving other user settings sections like `LlmProvider`), and hot-apply changes immediately via [`ITailscaleServeService`](../../../src/NightCodeForge.Web/Tailscale/TailscaleServeHostedService.cs).
2. **Live Status & Diagnostics Card:** Display real-time daemon status (`🟢 Active`, `🟡 Draining / Applying`, `⚪ Inactive`, `🔴 Tailscale Daemon Offline / Unauthenticated`), Tailscale CLI version, tailnet domain, WireGuard IP (`100.x.y.z`), and read-only auto-detected loopback target (`127.0.0.1:5270`).
3. **One-Click MagicDNS URL & Dynamic Inline SVG QR Code:** Render the resolved HTTPS MagicDNS URL (`https://ncf.<tailnet-name>.ts.net` or `https://<node-name>.<tailnet-name>.ts.net`) with a "Copy Link" button and a scannable ISO/IEC 18004 inline SVG QR code so developers can scan with their phone camera to immediately open and install the mobile PWA.

---

## 2. Backend & Presentation Specifications

In accordance with [AGENTS.md](../../../AGENTS.md), all CLI status parsing, configuration persistence, and SVG QR code matrix generation reside in [`NightCodeForge.Web/Tailscale`](../../../src/NightCodeForge.Web/Tailscale) / `NightCodeForge.Web/Services` (with zero optional constructor parameters and $\le 10$ constructor parameters per service), keeping `TailscaleSettingsCard.razor` strictly presentational.

### 2.1 Service & Persistence Extensions (`NightCodeForge.Web/Tailscale`)
- **Live Status Contract (`ITailscaleServeService` & `TailscaleStatusParser`):**
  - Extend [`ITailscaleServeService`](../../../src/NightCodeForge.Web/Tailscale/TailscaleServeHostedService.cs) to query a live `TailscaleStatusSnapshot` (state enum: `Active`, `Applying`, `Inactive`, `DaemonOfflineOrUnauthenticated`; CLI version from `tailscale version`; node DNS name, tailnet suffix, and WireGuard IPv4 from `tailscale status --json`; resolved Kestrel loopback target via [`TailscaleTargetResolver`](../../../src/NightCodeForge.Web/Tailscale/TailscaleServeHelpers.cs); and resolved `https://...ts.net` MagicDNS URL).
  - Extend [`TailscaleStatusParser`](../../../src/NightCodeForge.Web/Tailscale/TailscaleServeHelpers.cs) to extract `TailscaleIPs` (WireGuard IPv4) and tailnet domain alongside `IsAuthenticated`, `GetNodeDnsName`, and `GetServeHostName`.
- **Atomic `config/config.json` Persistence & Immediate Hot-Apply:**
  - Persist updated [`TailscaleOptions`](../../../src/NightCodeForge.Web/Tailscale/TailscaleModels.cs) into the `"Tailscale"` section of `config/config.json` (using `HostConfigurationSources.GetFullPath(contentRootPath)` and atomic temp-file swap while preserving existing root JSON sections, matching [`LlmConfigurationWriter`](../../../src/NightCodeForge.Infrastructure/Configuration/LlmConfigurationWriter.cs)).
  - Because `config/config.json` may not have existed at host startup (when `HostConfigurationSources` skips provider registration) and file-watcher reloads are asynchronous, passing updated `TailscaleOptions` to `ITailscaleServeService` must immediately update the in-memory effective options and run `ApplyConfigurationAsync` without waiting for `IOptionsMonitor<TailscaleOptions>` file-watcher debounce.

### 2.2 Interactive Controls (`TailscaleSettingsCard.razor`)
- **Master Toggle:** Enable / Disable Remote Access (`AutoServe`).
- **Exposition Mode Switcher:**
  - 🌟 **Dedicated Tailscale Service (`svc:ncf`)** *(Recommended)* — Publishes NightCodeForge under `https://ncf.<tailnet-name>.ts.net` (`ServiceName` non-empty).
  - 💻 **Device Node Mode** — Publishes directly to `https://<node-name>.<tailnet-name>.ts.net` (`ServiceName` cleared/empty).
- **Service Name:** Custom text input when in Service Mode (default: `svc:ncf`).
- **Port Configuration:** Tailscale listen port (default: `443`), plus read-only display of the auto-detected Kestrel loopback target (`127.0.0.1:<web-port>`).
- **Auto-Advertise Toggle:** Automatically advertise service route across the tailnet on start (`Advertise`, Service Mode only).
- **First-Time Setup & Prerequisites Notice:** Displays a callout reminding the user of the two required external Tailscale setup actions:
  1. Enabling **Allow incoming connections** from the host's Tailscale system tray / menu bar icon.
  2. Creating and approving the service in the [Tailscale Admin Console — Services](https://console.tailscale.com/admin/services?refreshed=true) the first time a Dedicated Tailscale Service (`svc:ncf`) is configured (with a reference link to the [Tailscale Services documentation](https://tailscale.com/docs/features/tailscale-services)).
- **"Save & Apply" Action:** Atomically persists updated settings to `config/config.json` and hot-applies routes via `ITailscaleServeService` without restarting the web app.

### 2.3 Live Status, Diagnostics & Mobile Onboarding Card
- **Real-Time Status Pill:** `🟢 Active`, `🟡 Draining / Applying`, `⚪ Inactive`, or `🔴 Tailscale Daemon Offline / Unauthenticated`.
- **MagicDNS Link & Copy Button:** Displays the active `https://...ts.net` URL with clipboard copy feedback.
- **Inline SVG QR Code:** Renders a self-contained, scannable ISO/IEC 18004 SVG QR code for the active MagicDNS HTTPS URL (zero external CDN/network calls, preserving [Step 12.19](STEP_12_19_AIR_GAPPED_OFFLINE_WEB_APP_HARDENING.md) offline/air-gapped subresource invariants).
- **Daemon Telemetry:** Displays Tailscale CLI version, tailnet name, node WireGuard IP, and active loopback target.

---

## 3. Implementation Checklist & Deliverables

- [x] Extend [`TailscaleStatusParser`](../../../src/NightCodeForge.Web/Tailscale/TailscaleServeHelpers.cs) and [`ITailscaleServeService`](../../../src/NightCodeForge.Web/Tailscale/TailscaleServeHostedService.cs) / `TailscaleServeHostedService` with live status snapshot reporting, in-memory option override for immediate hot-apply, and atomic `config/config.json` `"Tailscale"` section persistence.
- [x] Implement a self-contained SVG QR code generator helper in `NightCodeForge.Web`.
- [x] Build [`src/NightCodeForge.Web/Components/Settings/TailscaleSettingsCard.razor`](../../../src/NightCodeForge.Web/Components/Settings/TailscaleSettingsCard.razor) and integrate it into [`Settings.razor`](../../../src/NightCodeForge.Web/Components/Pages/Settings.razor) (Global Scope $\rightarrow$ `🔒 Remote & Tailscale` tab).
- [x] Add unit and bUnit tests in `NightCodeForge.Web.Tests` (and update `SettingsComponentTests` global tab assertions) verifying `TailscaleStatusParser` telemetry fields, `config/config.json` persistence + hot-apply, `TailscaleSettingsCard.razor` mode switching, status pill rendering, and QR code SVG output.

---

## 4. Implementation Notes

- **Live Telemetry & Hot-Apply (`src/NightCodeForge.Web/Tailscale/`):**
  - Extended [`ITailscaleServeService`](../../../src/NightCodeForge.Web/Tailscale/TailscaleServeHostedService.cs) / [`TailscaleServeHostedService`](../../../src/NightCodeForge.Web/Tailscale/TailscaleServeHostedService.cs) with `GetStatusSnapshotAsync`, `GetCurrentOptions`, and `ApplyConfigurationAsync(TailscaleOptions overrideOptions)`.
  - `GetStatusSnapshotAsync` resolves `TailscaleStatusSnapshot` (`State`, `CliVersion`, `NodeDnsName`, `TailnetDomain`, `TailscaleIpv4`, `LoopbackTarget` via `TailscaleTargetResolver.TryResolve`, `MagicDnsUrl` when `Active`, and `LastErrorMessage`).
  - `OnOptionsChangedAsync` deduplicates file-watcher reload notifications against `_lastAppliedOptions` (`TailscaleOptions.ConfigurationEquals`) so hot-applying from the UI does not trigger a redundant route teardown/re-apply when `IOptionsMonitor<TailscaleOptions>` reloads `config/config.json`.
  - Extended [`TailscaleStatusParser`](../../../src/NightCodeForge.Web/Tailscale/TailscaleServeHelpers.cs) with `GetTailscaleIpv4` and `GetTailnetDomain`.
- **Atomic Configuration Persistence:**
  - Added [`TailscaleConfigurationWriter`](../../../src/NightCodeForge.Web/Tailscale/TailscaleConfigurationWriter.cs) (`ITailscaleConfigurationWriter`) backed by shared [`UserConfigJsonFile`](../../../src/NightCodeForge.Infrastructure/Configuration/UserConfigJsonFile.cs) in `NightCodeForge.Infrastructure.Configuration` (shared with [`LlmConfigurationWriter`](../../../src/NightCodeForge.Infrastructure/Configuration/LlmConfigurationWriter.cs) for synchronized atomic temp-file swap and section preservation).
- **Offline Inline SVG QR Code & UI Card:**
  - Added [`QrCodeSvgGenerator`](../../../src/NightCodeForge.Web/Tailscale/QrCodeSvgGenerator.cs) using `Net.Codecrete.QrCodeGenerator` (`3.2.1`) to render self-contained ISO/IEC 18004 SVG QR codes with zero external network calls.
  - Added [`TailscaleSettingsCard.razor`](../../../src/NightCodeForge.Web/Components/Settings/TailscaleSettingsCard.razor) wired into [`Settings.razor`](../../../src/NightCodeForge.Web/Components/Pages/Settings.razor) under Global Scope (`🔒 Remote & Tailscale`), with tests in [`TailscaleServeTests.cs`](../../../tests/NightCodeForge.Web.Tests/TailscaleServeTests.cs), [`TailscaleSettingsCardTests.cs`](../../../tests/NightCodeForge.Web.Tests/TailscaleSettingsCardTests.cs), and [`SettingsComponentTests.cs`](../../../tests/NightCodeForge.Web.Tests/SettingsComponentTests.cs).


