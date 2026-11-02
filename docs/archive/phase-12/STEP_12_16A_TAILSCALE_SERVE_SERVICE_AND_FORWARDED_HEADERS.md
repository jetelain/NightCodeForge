# 🔒 Step 12.16A: Basic Tailscale Connectivity (`TailscaleServeHostedService` & Forwarded Headers MVP)

> **Feature Implementation Step (Part 1 of 2 — Backend Hosted Service, Proxy Lifecycle & Kestrel Headers)**  
> *Parent Plan:* [Phase 12 Master Plan](../12_EXTEND_FEATURES.md)  
> *Companion Step (Post-MVP UI):* [Step 12.16B: Tailscale Settings UI, QR Code Onboarding & Live Mesh Status](../../features/STEP_12_16B_TAILSCALE_SETTINGS_UI_AND_LIVE_STATUS.md)  
> *Related Steps:* [Step 12.8A: Mobile-Responsive Layout & Touch Ergonomics](STEP_12_8A_MOBILE_RESPONSIVE_LAYOUT_AND_TOUCH_ERGONOMICS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests` | *Status:* ✅ Completed | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **Zero-Configuration Private Remote Access (`tailscale serve`):** Expose NightCodeForge over the user's private Tailscale tailnet using HTTPS (`https://<node-or-service>.<tailnet-name>.ts.net`) via `TailscaleServeHostedService`, supporting both **Device Mode** (`tailscale serve --https=443 127.0.0.1:<web-port>`) and **Named Service Mode** (`tailscale serve --service=svc:ncf --https=443 127.0.0.1:<web-port>` + `tailscale serve advertise svc:ncf`).
2. **Dynamic Target Port Resolution:** Automatically detect the local Kestrel target port at runtime via `IServerAddressesFeature` or `IConfiguration["Urls"]` (e.g. `5270`) without requiring manual port configuration.
3. **Managed Lifecycle & Graceful Teardown:** Configure the route on application startup when `Tailscale:AutoServe` is enabled (or `--tailscale` is passed on the CLI) and gracefully reset/drain on shutdown so no orphaned `502 Bad Gateway` routes remain.
4. **ASP.NET Core Forwarded Headers:** Configure `ForwardedHeadersOptions` (`X-Forwarded-For`, `X-Forwarded-Proto`) in `Program.cs` so Blazor Server SignalR circuits and redirects work seamlessly behind the Tailscale local reverse proxy (including when the user runs `tailscale serve` manually outside the app).

*(The `/settings` **Remote & Tailscale** UI card and SVG QR code generator are deferred to post-MVP [Step 12.16B](STEP_12_16B_TAILSCALE_SETTINGS_UI_AND_LIVE_STATUS.md).)*

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

### 2.4 Service & Constructor Hygiene (`AGENTS.md` Compliance)
- `TailscaleServeHostedService` must have strictly $\le 6$ required constructor parameters (e.g., `IOptionsMonitor<TailscaleOptions>`, `IServer`, `IConfiguration`, `IHostApplicationLifetime`, `ITailscaleCliRunner`, `ILogger<TailscaleServeHostedService>`) and **zero optional constructor parameters**.

---

## 3. Implementation Checklist & Deliverables

- [x] Implement `ITailscaleServeService` / `TailscaleServeHostedService` ($\le 6$ required constructor parameters) with dynamic Kestrel port detection, `svc:ncf` service mode, device fallback mode, hot-apply support, and graceful shutdown drain/clear/reset.
- [x] Add factory default `Tailscale` section to `appsettings.json`, `config/config.json` overlay loading, and `--tailscale*` CLI flags in `Program.cs`.
- [x] Configure `ForwardedHeadersOptions` (`XForwardedFor | XForwardedProto`) in `Program.cs`.
- [x] Add unit and integration tests in `NightCodeForge.Web.Tests` verifying `TailscaleServeHostedService` CLI command generation, Kestrel port resolution, and shutdown teardown.
- [x] **Playwright E2E Test (Zero LLM Calls, `[Trait("Category", "E2E")]`):** Using `PlaywrightWebHostFixture` ([Step 11.39A](../fixes/STEP_11_39A_PLAYWRIGHT_E2E_HOST_FIXTURE_AND_BACKLOG_BASELINE_TESTS.md)), create a Playwright browser context configured with `ExtraHTTPHeaders = { ["X-Forwarded-Proto"] = "https", ["X-Forwarded-For"] = "100.64.0.10" }` and verify the Blazor InteractiveServer circuit connects and navigates cleanly behind simulated Tailscale reverse-proxy headers.

---

## 4. Implementation Notes

- Code: `src/NightCodeForge.Web/Tailscale/` (`TailscaleServeHostedService`/`ITailscaleServeService`, `ITailscaleCliRunner` over `IProcessRunner`, `TailscaleOptions`, target resolver, status parser, `--tailscale*` flag mapping). Tests: `TailscaleServeTests` and `E2E/TailscaleForwardedHeadersE2ETests`.
- Route is configured on `ApplicationStarted` (Kestrel addresses are only known then); option changes hot-apply (teardown then re-apply).
- Target: plain HTTP endpoint preferred; HTTPS-only apps are proxied via `https+insecure://127.0.0.1:<port>`.
- `--tailscale-service <name>` also enables `AutoServe`. Flags are layered last (override `config/config.json`).
- Hostname banner derives from `tailscale status --json` (`<service>.<tailnet>` in Service Mode).
- Forwarded headers use `KnownIPNetworks` (`KnownNetworks` is obsolete in .NET 10).
