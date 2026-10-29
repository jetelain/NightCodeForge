# 🛡️ Step 12.19: Air-Gapped & Offline Web Application Hardening (Zero External CDNs, Bundled Local Fonts & Self-Contained App Shell)

> **Phase 12 Implementation Step (Extend Features - Deep Integrations & Remote Experience - Quick Win)**  
> *Parent Plan:* [Phase 12 Master Plan](../12_EXTEND_FEATURES.md)  
> *Related Steps:* [Step 12.8: Mobile-Responsive UI & PWA](STEP_12_8_MOBILE_RESPONSIVE_UI_AND_PWA.md) | [Step 12.18: Cross-Platform Desktop Installer](STEP_12_18_CROSS_PLATFORM_DESKTOP_INSTALLER_AND_ENVIRONMENT_WIZARD.md) | [Step 12.20: Air-Gapped Search Deactivation & mdfetch Whitelisting](STEP_12_20_AIR_GAPPED_SEARCH_DEACTIVATION_AND_MDFETCH_WHITELISTING.md) | [Step 12.21: Container Sandbox Egress Filtering & Envoy Proxy](STEP_12_21_CONTAINER_SANDBOX_EGRESS_FILTERING_AND_ENVOY_PROXY.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **Zero External Network Dependencies (True Air-Gapped Operation):** Guarantee that `NightCodeForge.Web` loads, renders, and functions with 100% visual fidelity in completely network-isolated environments:
   - True air-gapped corporate and defense networks (zero WAN outbound connectivity).
   - Local area networks (LAN) without public internet access.
   - Offline transit environments (in-flight, trains, remote off-grid development).
   - Strict firewall/DMZ enclaves that drop or block all unauthorized outbound HTTP/HTTPS requests.
2. **Complete Elimination of External Fonts & CDNs:** Remove third-party font calls (`https://fonts.googleapis.com`, `https://fonts.gstatic.com`) and audit the entire web dashboard to ensure zero external CDN dependencies exist anywhere in stylesheets, scripts, templates, or HTML shells.
3. **Self-Hosted Bundled Typography & Robust System Fallbacks:**
   - Package modern, highly compressed WOFF2 font files locally under `wwwroot/fonts/` for **Inter** (sans-serif) and **JetBrains Mono** (monospace).
   - Declare local `@font-face` definitions using `font-display: swap;` to eliminate render-blocking network stalls, Flash of Invisible Text (FOIT), and layout shifts (Cumulative Layout Shift / CLS).
   - Configure a comprehensive, high-quality native system font stack as fallback (`system-ui`, `-apple-system`, `BlinkMacSystemFont`, `Segoe UI`, `Roboto`, `ui-monospace`, `Cascadia Code`, `Consolas`), ensuring flawless rendering even on lightweight or headless platforms.
4. **Content Security Policy (CSP) & Air-Gap Guardrails:** Configure an ASP.NET Core Content Security Policy header (configurable via `appsettings.json`) that strictly restricts script, style, font, image, and connection sources to `'self'`, local WebSockets (`ws:`, `wss:`), and data URIs, actively prohibiting browser subresources from pinging external servers.
5. **Zero Telemetry & Absolute Privacy Guarantee:** Enforce that no client-side analytics, error beacons, font trackers, or background telemetry pings are transmitted to external servers, aligning the presentation layer with the privacy invariants of local LLM inference engines.
6. **Automated Air-Gapped Static Analysis Gate:** Provide an automated test in `tests/NightCodeForge.Web.Tests` that scans all client-facing assets (`.razor`, `.html`, `.css`, `.js`) to ensure no external URL schemes (`http://`, `https://`) exist in `<link>`, `<script>`, `<style>`, or `@import` declarations.

---

## 2. Problem Statement & Motivation

### 2.1 The Render-Blocking Timeout Trap in Offline Environments
- Currently, [App.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/App.razor) includes external Google Fonts stylesheets:
  ```html
  <link rel="preconnect" href="https://fonts.googleapis.com">
  <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
  <link href="https://fonts.googleapis.com/css2?family=Inter:wght@300;400;500;600;700&family=JetBrains+Mono:wght@400;500;600&display=swap" rel="stylesheet">
  ```
- When a developer runs NightCodeForge offline (e.g. traveling, working off-grid) or inside an isolated network without WAN access, the browser attempts to resolve and connect to `fonts.googleapis.com`.
- Because stylesheets loaded via `<link rel="stylesheet">` are **render-blocking**, browsers wait up to **20 to 30 seconds** for DNS resolution and TCP SYN timeouts before failing and rendering fallback fonts.
- This creates an unacceptable 30-second blank screen freeze on application startup, severely degrading developer experience.

### 2.2 Enterprise Enclaves & Air-Gapped Security Policies
- NightCodeForge need to be deployed in secure, air-gapped enterprise environments (defense, financial, aerospace, government, healthcare) where source code and IP must never leave the local enclave.
- In these environments:
  - Workstations and servers have zero internet connectivity.
  - Intrusion Detection Systems (IDS) and proxy filters immediately trigger security alerts if any process or browser tab attempts to contact public internet domains (like `fonts.googleapis.com` or `fonts.gstatic.com`).
  - An application that fails to render or attempts external calls without internet access is deemed non-compliant with air-gap security baselines.

### 2.3 Privacy & Local LLM Invariant Alignment
- NightCodeForge's foundational premise is local, autonomous software development powered by local LLMs (Ollama, local vLLM, Unsloth Dynamic models).
- Querying Google Fonts leaks metadata on every page load:
  - Developer IP address
  - Browser user-agent and OS version
  - Exact timestamp and frequency of usage
- To maintain strict confidentiality, all visual assets, fonts, icons, scripts, and styling must reside entirely within the local binary and `wwwroot` bundle.

### 2.4 Self-Containment Across Distribution Models
- Under [Step 12.8 (Mobile PWA)](STEP_12_8_MOBILE_RESPONSIVE_UI_AND_PWA.md), the app shell is cached for offline use via `service-worker.js`. If font files reside on third-party servers, they can fail CORS checks or expire from browser cache.
- Under [Step 12.18 (Standalone Desktop Installer)](STEP_12_18_CROSS_PLATFORM_DESKTOP_INSTALLER_AND_ENVIRONMENT_WIZARD.md), NightCodeForge is distributed as a single self-contained application. Bundling all fonts directly within `NightCodeForge.Web` ensures the single-file executable functions out-of-the-box with zero post-install downloads.

---

## 3. Technical & Behavioral Specifications

### 3.1 Local Font Bundling & Directory Structure

Font files will be stored in modern, compressed WOFF2 format under `src/NightCodeForge.Web/wwwroot/fonts/`:

```text
src/NightCodeForge.Web/wwwroot/
├── fonts/
│   ├── inter-latin-variable.woff2         # Inter Variable (wght: 300..700, Latin subset, ~70 KB)
│   └── jetbrains-mono-latin-variable.woff2 # JetBrains Mono Variable (wght: 400..600, Latin subset, ~50 KB)
├── css/
│   ├── fonts.css                          # Local @font-face declarations
│   ├── theme.css                          # Theme variables & typography stacks
│   └── app.css                            # Layout & component styling
├── js/
│   └── app.js                             # Local client-side interop
├── lib/
│   └── bootstrap/                         # Local bundled Bootstrap assets
├── favicon.svg                            # Local SVG favicon
└── favicon.png                            # Local PNG fallback favicon
```

#### Size & Performance Budget:
- Variable WOFF2 fonts consolidate multiple weights (Regular, Medium, SemiBold, Bold) into a single optimized binary file.
- The total size of both variable font files is $< 150\text{ KB}$ uncompressed, and $< 120\text{ KB}$ served with HTTP compression.
- Font files are immutable and configured with long-term caching (`Cache-Control: public, max-age=31536000, immutable`).

---

### 3.2 CSS `@font-face` Declarations (`fonts.css`)

Create `src/NightCodeForge.Web/wwwroot/css/fonts.css` containing explicit local `@font-face` declarations:

```css
/* Inter Variable (Latin Subset) */
@font-face {
    font-family: 'Inter';
    font-style: normal;
    font-weight: 300 700;
    font-display: swap;
    src: url('../fonts/inter-latin-variable.woff2') format('woff2');
    unicode-range: U+0000-00FF, U+0131, U+0152-0153, U+02BB-02BC, U+02C6, U+02DA, U+02DC, U+0304, U+0308, U+0329, U+2000-206F, U+20AC, U+2122, U+2191, U+2193, U+2212, U+2215, U+FEFF, U+FFFD;
}

/* JetBrains Mono Variable (Latin Subset) */
@font-face {
    font-family: 'JetBrains Mono';
    font-style: normal;
    font-weight: 400 600;
    font-display: swap;
    src: url('../fonts/jetbrains-mono-latin-variable.woff2') format('woff2');
    unicode-range: U+0000-00FF, U+0131, U+0152-0153, U+02BB-02BC, U+02C6, U+02DA, U+02DC, U+0304, U+0308, U+0329, U+2000-206F, U+20AC, U+2122, U+2191, U+2193, U+2212, U+2215, U+FEFF, U+FFFD;
}
```

---

### 3.3 Robust Native System Font Fallbacks (`theme.css`)

Update typography variables in `src/NightCodeForge.Web/wwwroot/css/theme.css` to provide high-quality fallback font stacks:

```css
:root {
    /* Typography: Local Primary Fonts with Premium Native Fallbacks */
    --font-sans: 'Inter', system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
    --font-mono: 'JetBrains Mono', ui-monospace, 'Cascadia Code', 'Source Code Pro', Menlo, Consolas, 'DejaVu Sans Mono', monospace;
}
```

**Benefits:**
- Even if font files are somehow missing or disabled by user browser policies, the UI instantly falls back to modern OS system fonts (`system-ui` / `Segoe UI` on Windows, `SF Pro` on macOS/iOS, `Roboto` on Android) without layout breakage.

---

### 3.4 HTML Shell Cleanup (`App.razor`)

Refactor `<head>` in `src/NightCodeForge.Web/Components/App.razor`:

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <base href="/" />
    <!-- Local Typography & Theme (Air-Gapped: Zero External CDN Links) -->
    <link rel="stylesheet" href="css/fonts.css" />
    <link rel="stylesheet" href="css/theme.css" />
    <link rel="stylesheet" href="css/app.css" />
    <link rel="icon" type="image/svg+xml" href="favicon.svg" />
    <link rel="alternate icon" type="image/png" href="favicon.png" />
    <HeadOutlet />
</head>
<body class="forge-body">
    <Routes @rendermode="InteractiveServer" />
    <script src="_framework/blazor.web.js"></script>
    <script src="js/app.js"></script>
</body>
</html>
```

*Note:* All `preconnect` and Google Fonts stylesheet references are completely excised.

---

### 3.5 Content Security Policy (CSP) & Air-Gapped Middleware

To provide defense-in-depth and guarantee no browser subresource connects to unauthorized third-party hosts, add a security headers configuration to `NightCodeForge.Web`:

#### A. Configuration Schema (`appsettings.json`)
```json
{
  "Security": {
    "AirGappedMode": true,
    "EnableContentSecurityPolicy": true
  }
}
```

#### B. Content Security Policy Directives
When enabled, the application emits the following CSP header on all responses:

```http
Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline' 'unsafe-eval'; style-src 'self' 'unsafe-inline'; font-src 'self' data:; img-src 'self' data: blob:; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'self';
```

| Directive | Permitted Sources | Purpose |
| :--- | :--- | :--- |
| `default-src` | `'self'` | Disallow all fallback requests to external domains |
| `script-src` | `'self' 'unsafe-inline' 'unsafe-eval'` | Allow local `blazor.web.js`, `app.js`, and Blazor server DOM interop |
| `style-src` | `'self' 'unsafe-inline'` | Allow local stylesheets and component style attributes |
| `font-src` | `'self' data:` | Allow bundled local fonts under `wwwroot/fonts/` and inline SVG fonts |
| `img-src` | `'self' data: blob:` | Allow local icons, uploaded attachments, and generated diff previews |
| `connect-src` | `'self' ws: wss:` | Allow Blazor Interactive Server circuits (`/_blazor`) and SignalR telemetry |
| `frame-ancestors` | `'none'` | Prevent clickjacking by disallowing embedding in iframes |
| `base-uri` | `'self'` | Enforce local `<base href="/" />` integrity |

---

### 3.6 PWA Service Worker & Standalone Desktop Alignment

1. **Step 12.8 (PWA Service Worker):**
   - The service worker caching manifest (`service-worker.js`) explicitly includes:
     - `/css/fonts.css`
     - `/fonts/inter-latin-variable.woff2`
     - `/fonts/jetbrains-mono-latin-variable.woff2`
   - This ensures the Progressive Web App launches with full custom typography even when completely offline.
2. **Step 12.18 (Avalonia UI Desktop Installer):**
   - The self-contained desktop distribution packages the entire `wwwroot/` folder including local fonts into the embedded web host bundle, guaranteeing zero external dependencies during runtime.

---

### 3.7 Automated Air-Gapped Subresource Test Suite

Add an automated static regression test class in `tests/NightCodeForge.Web.Tests` (`AirGappedSubresourcesTests.cs`):

```csharp
[Fact]
public void WebApp_ShouldNotReferenceExternalHttpResources_InTemplatesOrStylesheets()
{
    // Scan all .razor, .html, .css, .js files in src/NightCodeForge.Web
    // Assert that no active subresource references (link href, script src, @import, url())
    // point to http:// or https:// (ignoring documentation comments and XML namespaces).
}
```

This test ensures that any future PR or component change that accidentally introduces a CDN dependency (e.g. `cdnjs`, `unpkg`, `jsdelivr`, `googleapis`) immediately fails CI before merging.

---

## 4. Definition of Done (DoD) Checklist

- [ ] **Typography & Static Asset Bundling:**
  - [ ] Obtain and place optimized, Latin-subset WOFF2 font files for **Inter** and **JetBrains Mono** in `src/NightCodeForge.Web/wwwroot/fonts/`.
  - [ ] Create `src/NightCodeForge.Web/wwwroot/css/fonts.css` with local `@font-face` definitions using `font-display: swap;`.
  - [ ] Verify `--font-sans` and `--font-mono` in `src/NightCodeForge.Web/wwwroot/css/theme.css` use local font names with native system fallback stacks.
- [ ] **HTML Shell & Razor Component Cleanup:**
  - [ ] Remove `https://fonts.googleapis.com` and `https://fonts.gstatic.com` preconnect and stylesheet tags from `src/NightCodeForge.Web/Components/App.razor`.
  - [ ] Add `<link rel="stylesheet" href="css/fonts.css" />` to `App.razor`.
  - [ ] Verify that no other Razor, HTML, or CSS file references external HTTP/HTTPS assets.
- [ ] **Content Security Policy (CSP):**
  - [ ] Implement CSP middleware or configuration emitting `Content-Security-Policy` restricting subresources to `'self'`, local WebSockets, and data URIs.
  - [ ] Add `Security:AirGappedMode` configuration option to `appsettings.json`.
- [ ] **Automated Testing & CI Guard:**
  - [ ] Author `AirGappedSubresourcesTests` in `NightCodeForge.Web.Tests` asserting zero external subresource URLs in Web project files.
  - [ ] Validate that all tests pass without errors under `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
- [ ] **Documentation & Plan Updates:**
  - [ ] Register Step 12.19 in `docs/plan/12_EXTEND_FEATURES.md` (roadmap table, tier summary, and DoD checklist).
  - [ ] Register Step 12.19 in `docs/PLAN.md`.

---

## 5. Verification & Testing Matrix

| ID | Test Scenario | Execution / Method | Expected Outcome |
| :--- | :--- | :--- | :--- |
| **TC-12.19-01** | Static Subresource Scan | Run `AirGappedSubresourcesTests` via `dotnet test` | Passes with 0 external HTTP/HTTPS subresource violations found across all `.razor`, `.html`, `.css`, and `.js` files. |
| **TC-12.19-02** | Cold Boot Rendering Without Internet | Disconnect network adapter (or block WAN DNS), launch `ncf-web`, open `http://localhost:5000` | Web page renders immediately without 30-second DNS hang; fonts render with Inter and JetBrains Mono locally. |
| **TC-12.19-03** | Browser Network Tab Inspection | Open Chrome/Edge DevTools $\rightarrow$ Network tab, filter by "Domain" | 100% of network requests target `localhost:5000` (or `127.0.0.1`); zero requests dispatched to `fonts.googleapis.com` or `fonts.gstatic.com`. |
| **TC-12.19-04** | Content Security Policy Enforcement | Inspect HTTP Response Headers on `GET /` | `Content-Security-Policy` header is present with `default-src 'self'`. Attempting to inject `<img src="https://example.com/test.png">` in DevTools is blocked by CSP. |
| **TC-12.19-05** | Fallback Font Degradation | Delete or rename `wwwroot/fonts/` temporarily in test environment | UI falls back gracefully to `system-ui` and `ui-monospace` without missing characters, unreadable text, or layout distortion. |
