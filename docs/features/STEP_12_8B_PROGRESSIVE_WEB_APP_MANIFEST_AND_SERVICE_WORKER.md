# 📱 Step 12.8B: Installable Progressive Web App (PWA) Manifest & Service Worker Shell

> **Feature Implementation Step (Part 2 of 3 — PWA Packaging, Service Worker & CSP Alignment)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.8A: Mobile-Responsive Layout & Touch Ergonomics](STEP_12_8A_MOBILE_RESPONSIVE_LAYOUT_AND_TOUCH_ERGONOMICS.md)  
> *Companion Step:* [Step 12.8C: Web Worker Notifications & Subject Preferences](STEP_12_8C_WEB_WORKER_NOTIFICATIONS_AND_SUBJECT_PREFERENCES.md)  
> *Related Steps:* [Step 12.16A: Tailscale Serve & HTTPS](STEP_12_16A_TAILSCALE_SERVE_SERVICE_AND_FORWARDED_HEADERS.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Running NightCodeForge inside standard mobile browser tabs wastes vertical screen space on address bars and navigation controls, and mobile browsers frequently discard permissions (such as microphone access for voice dictation or notification permissions) when run as transient tabs.

This step (Part 2 of Step 12.8) configures NightCodeForge as an **installable Progressive Web App (PWA)**:
1. **Web App Manifest (`wwwroot/manifest.webmanifest`) & Icons:** Provide a valid web manifest with `display: "standalone"` and raster icons (`icon-192.png` at $192\times 192\text{px}$ and `icon-512.png` at $512\times 512\text{px}$) alongside `favicon.svg`.
2. **Service Worker (`wwwroot/service-worker.js`):** Implement a network-first static asset caching service worker that strictly bypasses non-`GET` requests, dynamic Blazor Server circuits (`/_blazor`), SignalR hubs (`/hubs/`), and Hangfire (`/hangfire`).
3. **CSP & `App.razor` Registration:** Update [`ContentSecurityPolicyMiddleware.cs`](../../src/NightCodeForge.Web/Middleware/ContentSecurityPolicyMiddleware.cs) to allow `manifest-src 'self';` and register the manifest, Apple/mobile meta tags, and service worker in [`App.razor`](../../src/NightCodeForge.Web/Components/App.razor).

---

## 2. Technical Specifications

### 2.1 Web App Manifest (`wwwroot/manifest.webmanifest`) & Icons
Provide a valid web manifest meeting modern Chrome/Android and iOS Safari PWA installability requirements:

```json
{
  "name": "NightCodeForge Autonomous Dev",
  "short_name": "NightCodeForge",
  "description": "Autonomous AI Software Engineering Dashboard",
  "start_url": "/",
  "scope": "/",
  "display": "standalone",
  "orientation": "any",
  "theme_color": "#4f46e5",
  "background_color": "#090d16",
  "icons": [
    {
      "src": "icon-192.png",
      "sizes": "192x192",
      "type": "image/png",
      "purpose": "any"
    },
    {
      "src": "icon-512.png",
      "sizes": "512x512",
      "type": "image/png",
      "purpose": "any"
    },
    {
      "src": "favicon.svg",
      "sizes": "any",
      "type": "image/svg+xml",
      "purpose": "maskable"
    }
  ]
}
```

### 2.2 Service Worker (`wwwroot/service-worker.js`)
A lightweight service worker providing a network-first static asset strategy (with offline cache fallback for app shell assets) while strictly bypassing non-`GET` requests, `/_blazor`, `/hubs/`, and `/hangfire`:

```javascript
// NightCodeForge PWA & Notification Service Worker
const CACHE_NAME = 'ncf-cache-v1';
const PRECACHE_URLS = [
    '/',
    '/css/fonts.css',
    '/css/theme.css',
    '/css/app.css',
    '/favicon.svg',
    '/favicon.png',
    '/icon-192.png',
    '/icon-512.png',
    '/manifest.webmanifest'
];

self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(CACHE_NAME).then((cache) => cache.addAll(PRECACHE_URLS))
    );
    self.skipWaiting();
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys().then((keys) =>
            Promise.all(
                keys.filter((k) => k !== CACHE_NAME).map((k) => caches.delete(k))
            )
        )
    );
    self.clients.claim();
});

// Network-first with cache fallback for GET requests; bypass Blazor Server, SignalR, and Hangfire
self.addEventListener('fetch', (event) => {
    if (event.request.method !== 'GET') {
        return;
    }
    const url = new URL(event.request.url);
    if (url.origin !== self.location.origin ||
        url.pathname.startsWith('/_blazor') ||
        url.pathname.startsWith('/hubs/') ||
        url.pathname.startsWith('/hangfire')) {
        return;
    }
    event.respondWith(
        fetch(event.request).catch(() => caches.match(event.request))
    );
});
```

### 2.3 Registration in [`App.razor`](../../src/NightCodeForge.Web/Components/App.razor) & CSP Alignment
1. **CSP Header Update:** Ensure [`ContentSecurityPolicyMiddleware.cs`](../../src/NightCodeForge.Web/Middleware/ContentSecurityPolicyMiddleware.cs) explicitly includes `manifest-src 'self';` alongside `worker-src 'self' blob:;`.
2. **Manifest & Meta Tags in `App.razor`:** Add the manifest link, mobile web-app meta tags (`mobile-web-app-capable`, `apple-mobile-web-app-capable`, `apple-touch-icon`), and service worker registration when `window.isSecureContext` is true.

---

## 3. Implementation Checklist & Deliverables

- [ ] **PWA Assets & Manifest:**
  - [ ] Create `wwwroot/manifest.webmanifest` and raster icons (`wwwroot/icon-192.png`, `wwwroot/icon-512.png`).
- [ ] **Service Worker & CSP:**
  - [ ] Implement `wwwroot/service-worker.js` with `GET`-only network-first static asset caching and `/_blazor` + `/hubs/` + `/hangfire` bypass.
  - [ ] Update [`ContentSecurityPolicyMiddleware.cs`](../../src/NightCodeForge.Web/Middleware/ContentSecurityPolicyMiddleware.cs) to include `manifest-src 'self';`.
  - [ ] Wire manifest link, meta tags, and Service Worker registration in [`App.razor`](../../src/NightCodeForge.Web/Components/App.razor).
- [ ] **Automated Testing:**
  - [ ] Unit/integration tests verifying `ContentSecurityPolicyMiddleware` emits `manifest-src 'self';` and `/manifest.webmanifest` + `/service-worker.js` are served with valid content types.

