# 🔒 Step 12.16A: Tailscale Serve Hosted Service (`svc:ncf`), Forwarded Headers & Operational Scripts

> **Feature Implementation Step (Part 1 of 2 — Backend Hosted Service, Proxy Lifecycle & Kestrel Headers)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Step:* [Step 12.16B: Tailscale Settings UI, QR Code Onboarding & Live Mesh Status](STEP_12_16B_TAILSCALE_SETTINGS_UI_AND_LIVE_STATUS.md)  
> *Related Steps:* [Step 12.8B: PWA Manifest & Service Worker](STEP_12_8B_PROGRESSIVE_WEB_APP_MANIFEST_AND_SERVICE_WORKER.md) | [Step 12.12: Voice Support](STEP_12_12_VOICE_SUPPORT_AND_CONTINUOUS_DICTATION.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests`, `tools/` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **Zero-Configuration Private Remote Access (`svc:ncf`):** Publish NightCodeForge as a dedicated named **Tailscale Service** (`svc:ncf`) over HTTPS with automated Let's Encrypt certificates (`https://ncf.<tailnet-name>.ts.net`) using modern Tailscale CLI syntax (`tailscale serve --service=svc:ncf --https=443 127.0.0.1:<web-port>` and `tailscale serve advertise svc:ncf`), with device-level fallback (`tailscale serve --https=443 127.0.0.1:<web-port>`) when `ServiceName` is empty.
2. **Dynamic Target Port Resolution:** Automatically detect the local Kestrel target port at runtime via `IServerAddressesFeature` or `IConfiguration["Urls"]` (e.g. `5270`) without requiring user port configuration.
3. **Managed Lifecycle & Graceful Teardown (Zero Desktop Overhead):** In `TailscaleServeHostedService`, configure and advertise the route on startup, and gracefully drain (`tailscale serve drain svc:ncf`), clear (`tailscale serve clear svc:ncf`), and reset (`tailscale serve reset`) on shutdown so no orphaned `502 Bad Gateway` routes remain.
4. **ASP.NET Core Forwarded Headers & Standalone CLI Scripts:** Configure `ForwardedHeadersOptions` (`X-Forwarded-For`, `X-Forwarded-Proto`) in `Program.cs` and provide standalone `tools/tailscale-serve.ps1` and `tools/tailscale-serve.sh` scripts.

*(The `/settings` **Remote & Tailscale** UI card, hot-apply controls, and SVG QR code generator are specified in [Step 12.16B](STEP_12_16B_TAILSCALE_SETTINGS_UI_AND_LIVE_STATUS.md).)*

---

## 2. Technical & Behavioral Specifications

### 2.1 Configuration Model (`appsettings.json` & `config/config.json` Overlay)

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
- Supports durable persistence via `config/config.json` (and `SystemSettings`) and CLI flags (`--tailscale`, `--tailscale-service <name>`, `--tailscale-port <port>`) in `Program.cs`.

### 2.2 Lifecycle Phases (`TailscaleServeHostedService`)

1. **Startup (`StartAsync`) & Hot-Apply (`ApplyConfigurationAsync`):**
   - If `Tailscale:AutoServe` is false, the service remains dormant.
   - If enabled:
     1. Probes for `tailscale` CLI availability in `PATH` (`tailscale version`). If missing, logs a warning and skips activation without crashing.
     2. Verifies tailnet authentication via `tailscale status --json`.
     3. Queries the active Kestrel listening port (e.g. `5270`) via `IServerAddressesFeature` or `IConfiguration["Urls"]`.
     4. Runs defensive pre-flight cleanup (`tailscale serve reset`) to clear any stale routes from prior abnormal terminations.
     5. Configures the serve proxy:
        - **Service Mode (`ServiceName` set, e.g. `"svc:ncf"`):**
          ```bash
          tailscale serve --service=svc:ncf --https=443 127.0.0.1:5270
          tailscale serve advertise svc:ncf
          ```
        - **Device Mode (`ServiceName` empty/null):**
          ```bash
          tailscale serve --https=443 127.0.0.1:5270
          ```
     6. Extracts the MagicDNS FQDN from `tailscale serve status --json` or `tailscale status --json` and logs the startup HTTPS banner.

2. **Graceful Shutdown (`StopAsync`):**
   - When the host application stops:
     1. If in Service Mode: runs `tailscale serve drain svc:ncf` (3s timeout) and `tailscale serve clear svc:ncf`.
     2. Runs `tailscale serve reset` and logs confirmation.

### 2.3 ASP.NET Core Forwarded Headers (`Program.cs`)
Configure `ForwardedHeadersOptions` in [`Program.cs`](../../src/NightCodeForge.Web/Program.cs):
```csharp
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
```

### 2.4 Standalone Operational Scripts (`tools/tailscale-serve.ps1` & `tools/tailscale-serve.sh`)
Provide standalone PowerShell and Bash scripts with `try/finally` and `trap ... EXIT SIGINT SIGTERM` blocks that configure `tailscale serve` and execute `drain`, `clear`, and `reset` on exit.

---

## 3. Implementation Checklist & Deliverables

- [ ] Implement `ITailscaleServeService` / `TailscaleServeHostedService` inheriting from `IHostedService` with dynamic Kestrel port detection, `svc:ncf` service mode, device fallback mode, hot-apply support, and graceful shutdown drain/clear/reset.
- [ ] Add factory default `Tailscale` section to `appsettings.json`, `config/config.json` overlay loading, and `--tailscale*` CLI flags in `Program.cs`.
- [ ] Configure `ForwardedHeadersOptions` (`XForwardedFor | XForwardedProto`) in `Program.cs`.
- [ ] Create `tools/tailscale-serve.ps1` and `tools/tailscale-serve.sh` with exit traps.
- [ ] Add unit and integration tests in `NightCodeForge.Web.Tests` verifying `TailscaleServeHostedService` CLI command generation, Kestrel port resolution, shutdown teardown, and `X-Forwarded-Proto: https` header handling.

