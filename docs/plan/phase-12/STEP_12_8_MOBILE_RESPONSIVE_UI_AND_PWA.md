# 📱 Step 12.8: Mobile-Responsive Web UI, PWA & Web Worker Notifications

> **Phase 12 Implementation Step (Extend Features - Deep Integrations & Remote Experience)** *(formerly Step 11.6)*  
> *Parent Plan:* [Phase 12 Master Plan](../12_EXTEND_FEATURES.md)  
> *Related Steps:* [Step 12.4: Ideation Quick Notes](STEP_12_4_IDEATION_QUICK_NOTES_SUGGESTIONS_AND_REFINEMENT.md) | [Step 12.12: Voice Support](STEP_12_12_VOICE_SUPPORT_AND_CONTINUOUS_DICTATION.md) | [Step 12.16: Remote Tailscale Access](STEP_12_16_TAILSCALE_REMOTE_ACCESS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **First-Class Mobile Experience:** Ensure the NightCodeForge Blazor dashboard is fully responsive, touch-friendly, and ergonomically optimized for mobile phones and tablets (iOS and Android).
2. **On-the-Go Workflow Operations:** Enable software engineers to effortlessly perform critical triage and oversight tasks from a smartphone:
   - Monitor real-time pipeline execution, stage transitions, and GPU/CPU gauges.
   - Answer pending human clarifying questions in [Inbox.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Inbox.razor) with touch-friendly selection chips.
   - Jot down spontaneous ideas into the Ideation Backlog via a one-tap quick-note capture bar ([Step 12.4](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/docs/plan/phase-12/STEP_12_4_IDEATION_QUICK_NOTES_SUGGESTIONS_AND_REFINEMENT.md)).
   - Review quality gate metrics and approve/discard ideation proposal cards.
3. **Adaptive Navigation & Touch Ergonomics:** Replace desktop-oriented layouts with mobile slide-out navigation drawers, bottom-sheet drawers, 44px+ touch targets, and fixed-bottom chat input bars that remain pinned above mobile on-screen keyboards.
4. **Installable Progressive Web App (PWA):** Configure a Web App Manifest (`manifest.webmanifest`) and Service Worker (`service-worker.js`) to support "Add to Home Screen" with standalone fullscreen display mode and persistent permissions.
5. **Web Worker Notifications & Subject Preferences:** Deliver native browser Web Notifications dispatched via the Web Worker / Service Worker on mobile devices (and desktop browsers) when tasks require human intervention (question inbox clarification, review room sign-off) or when tasks merge to the base branch, paired with granular user preferences to toggle notifications per subject.

*(Note: Dedicated remote network infrastructure, MagicDNS TLS termination, and reverse proxy configuration are specified in [Step 12.16: Remote Tailscale Access](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/docs/plan/phase-12/STEP_12_16_TAILSCALE_REMOTE_ACCESS.md).)*

---

## 2. Problem Statement & Motivation

### 2.1 The Remote Developer Pattern
- Developers hosting NightCodeForge on home workstations or dedicated GPU servers frequently access the dashboard from smartphones while away from their desks.
- Currently, the web dashboard is optimized primarily for desktop resolutions:
  - The desktop navigation sidebar consumes significant horizontal width.
  - Multi-column tables in the Dev Queue and Backlog cause awkward horizontal overflow on narrow screens (< 600px).
  - Chat inputs in Ideation can be obscured when mobile virtual keyboards pop up.
  - Interactive buttons designed for mouse pointers (e.g. small icon buttons < 30px) are frustrating to tap accurately on touch screens.

### 2.2 Critical Mobile Use Cases
Engineers on mobile rarely write code; they primarily:
1. **Unblock Running Tasks:** A task pauses with a question; the developer gets notified and answers in 10 seconds from their phone.
2. **Review & Sign Off Code:** A task completes all automated gates and enters the Review Room; the developer inspects the diff and approves the merge.
3. **Check Status & Merges:** Tracking when an autonomous task successfully merges to main.
4. **Capture Quick Notes:** Jotting down a task idea before it is forgotten.

### 2.3 The Mobile Browser & Notification Gap
- Developers monitoring tasks on mobile frequently keep the browser tab in the background or lock their phone screen while waiting for multi-minute LLM reasoning and build runs.
- Without proactive notifications, developers must constantly unlock their phones to see if an agent asked a clarifying question or if a task is waiting for review.
- On mobile platforms (Chrome on Android, iOS Safari), standard `new Notification()` constructors in the window context are forbidden (`TypeError: Illegal constructor`) or blocked in background tabs. Notifications **must** be dispatched by the Web Worker / Service Worker registration interface (`ServiceWorkerRegistration.showNotification()`).
- Furthermore, developers require fine-grained control: some may want alerts only when their input is blocking progress (questions or review approvals) without alerts on task completions, or vice-versa.

### 2.4 Browser Chrome & Permission Ephemerality
- Running NightCodeForge inside standard mobile browser tabs wastes vertical screen space on address bars and navigation controls.
- Mobile browsers frequently discard permissions (such as microphone access for [Step 12.12](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/docs/plan/phase-12/STEP_12_12_VOICE_SUPPORT_AND_CONTINUOUS_DICTATION.md) voice dictation) when run as transient browser tabs.
- Transforming the web app into an installable **Progressive Web App (PWA)** provides a distraction-free, app-like standalone window while permanently maintaining granted permissions.

---

## 3. Behavioral & Technical Specifications

### 3.1 Responsive Viewport & Navigation Structure

1. **Viewport Meta Tag:**
   Ensure [App.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/App.razor) contains the correct responsive viewport configuration:
   ```html
   <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=5.0, viewport-fit=cover" />
   ```

2. **Mobile Drawer Navigation ([NavMenu.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Layout/NavMenu.razor) & [MainLayout.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Layout/MainLayout.razor)):**
   - On screens $\ge 768\text{px}$: Display standard desktop sidebar.
   - On screens $< 768\text{px}$:
     - Sidebar collapses into a hidden off-canvas drawer.
     - Top navigation bar renders a prominent hamburger button ($\ge 44\times 44\text{px}$).
     - Tapping the hamburger opens the navigation drawer with smooth CSS slide-in and dark backdrop overlay. Tapping any nav item or the backdrop closes the drawer.

3. **Top Bar Quick Status Strip:**
   - Display a compact active worker pill (e.g. `⚡ Task #42: Implementation`) and a fast "+ Note" shortcut button directly in the mobile top bar.

### 3.2 Touch Target Ergonomics & CSS Polish

1. **Touch Target Size:** All buttons, nav links, dropdown triggers, and proposal action chips must have a minimum clickable/tappable area of **$44\times 44\text{px}$** (using transparent padding if visual badge is smaller).
2. **Hover Elimination:** Any critical action that currently relies on `:hover` must be persistently visible or accessible via direct tap on touch devices.
3. **No Horizontal Scroll on Root:** The `<body>` and `#app` container must have `overflow-x: hidden;` to eliminate accidental horizontal jitter during vertical page scrolling.

### 3.3 Page-by-Page Mobile Adaptations

#### A. Ideation Chat ([Ideation.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Ideation.razor))
- **Fixed Bottom Input Bar:** The user prompt input box and "Send" button are fixed to the bottom of the viewport (`position: sticky; bottom: 0;`), remaining visible when the virtual keyboard appears.
- **Scrollable Turn History:** Messages container occupies `flex: 1; overflow-y: auto;` with auto-scrolling to the latest assistant message.
- **Card Stacking:** Proposal cards display actions (`Approve`, `Keep as Suggestion`, `Discard`) as full-width stacked buttons on mobile rather than a tight horizontal row.

#### B. Human Question Inbox ([Inbox.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Inbox.razor))
- Grouped question batches render as clean full-width cards using [QuestionCard.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Inbox/QuestionCard.razor).
- Choice options render as large, touch-friendly pill chips with clear active radio states.
- The "Submit All Answers" action is anchored as a full-width primary button at the bottom of the card.

#### C. Task Execution Inspector ([TaskExecutionInspector.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Monitoring/TaskExecutionInspector.razor))
- **Horizontal Stepper:** Pipeline stage circles in [PipelineStepper.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Monitoring/PipelineStepper.razor) render in a horizontal touch-swipeable scroll container (`overflow-x: auto; -webkit-overflow-scrolling: touch;`).
- **Terminal Log Viewer:** Terminal output wraps cleanly with a toggle for `line-wrap` vs `horizontal scroll`.
- **Turns Accordion:** Stage turns collapse into touch-expandable accordions to prevent excessive vertical scrolling.

#### D. Backlog & Dev Queue ([Backlog.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Backlog.razor), [DevQueue.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/DevQueue.razor))
- On mobile viewports ($< 768\text{px}$), data tables seamlessly transform into card stacks displaying Task ID, Title, Status badge, and primary action.
- [TaskDetailDrawer.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor) slides up from the bottom as a **mobile bottom sheet** ($90\text{vh}$ height) with a swipe-down or close button, replacing the desktop right-hand sidebar.

---

### 3.4 Progressive Web App (PWA) Architecture

Transforming NightCodeForge into an installable PWA enables standalone window execution, eliminates browser chrome, and allows persistent background operations on mobile.

#### 3.4.1 Web App Manifest (`wwwroot/manifest.webmanifest`)
Provide a valid web manifest meeting modern PWA installability requirements:

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
      "src": "favicon.png",
      "sizes": "192x192",
      "type": "image/png",
      "purpose": "any"
    },
    {
      "src": "favicon.png",
      "sizes": "512x512",
      "type": "image/png",
      "purpose": "any"
    },
    {
      "src": "favicon.svg",
      "sizes": "512x512",
      "type": "image/svg+xml",
      "purpose": "maskable"
    }
  ]
}
```

#### 3.4.2 Service Worker (`wwwroot/service-worker.js`)
A lightweight service worker caching core offline/app shell assets while safely bypassing dynamic Blazor Server circuits and SignalR telemetry:

```javascript
// NightCodeForge PWA & Notification Service Worker
const CACHE_NAME = 'ncf-cache-v1';
const PRECACHE_URLS = [
    '/',
    '/css/theme.css',
    '/css/app.css',
    '/favicon.svg',
    '/favicon.png',
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

// Cache-first for static precached assets, network bypass for Blazor Server and SignalR
self.addEventListener('fetch', (event) => {
    if (event.request.url.includes('/_blazor') || event.request.url.includes('/hubs/')) {
        return;
    }
    event.respondWith(
        fetch(event.request).catch(() => caches.match(event.request))
    );
});
```

#### 3.4.3 Registration in [App.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/App.razor)
Add the manifest link, mobile theme meta tags, and service worker registration script:

```html
<head>
    ...
    <link rel="manifest" href="manifest.webmanifest" />
    <meta name="theme-color" content="#4f46e5" />
    <meta name="apple-mobile-web-app-capable" content="yes" />
    <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent" />
</head>
<body class="forge-body">
    ...
    <script>
        if ('serviceWorker' in navigator && window.isSecureContext) {
            navigator.serviceWorker.register('/service-worker.js')
                .then(reg => console.log('[ServiceWorker] Registered:', reg.scope))
                .catch(err => console.warn('[ServiceWorker] Registration failed:', err));
        }
    </script>
</body>
```

---

### 3.5 Web Worker Notifications & Subject Preference Toggles

To enable prompt responsiveness while away from the desktop, NightCodeForge provides native browser Web Notifications driven by real-time SignalR telemetry events and dispatched by the Web Worker / Service Worker on mobile.

#### 3.5.1 Notification Event Subjects & Triggers

Notifications are dispatched for three distinct operational subjects:

1. **Clarification Question Waiting (`question_inbox`):**
   - **Trigger Condition:** An autonomous agent pauses execution with [ForgeTask.IsWaitingForHuman](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/ForgeTask.cs) `== true` (or [WorkerStatusConstants.WaitingForHuman](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/WorkerStatusConstants.cs)).
   - **Notification Payload:**
     - **Title:** `❓ Human Input Needed: Task #{TaskId}`
     - **Body:** `{TaskTitle} — The agent has a clarifying question waiting for your answer.`
     - **Tag:** `ncf-question-{TaskId}` (replaces stale question notifications for the same task).
     - **Click Target:** Focuses the window/tab and navigates to [Inbox.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Inbox.razor) (`/inbox?taskId={TaskId}`).

2. **Review Room Sign-off Required (`review_room`):**
   - **Trigger Condition:** A task successfully completes all automated quality gates (Stage 6 Deterministic Post-Review Verification) and transitions to [TaskOverallStatus.Ready](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/Enums.cs#L47-L58) with [MergeMode.ManualReviewRequired](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/Enums.cs#L63-L67).
   - **Notification Payload:**
     - **Title:** `🔍 Ready for Review: Task #{TaskId}`
     - **Body:** `{TaskTitle} — All automated gates passed. Ready for code review and merge approval.`
     - **Tag:** `ncf-review-{TaskId}`
     - **Click Target:** Focuses the window/tab and navigates directly to [Review.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Review.razor) (`/review/{TaskId}`).

3. **Task Merged (`task_merged`):**
   - **Trigger Condition:** A task is successfully merged into the base branch (either automatically when [MergeMode.Automatic](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/Enums.cs#L63-L67) or manually approved in the Review Room), transitioning to [TaskOverallStatus.Closed](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/Enums.cs#L47-L58).
   - **Notification Payload:**
     - **Title:** `🚀 Task Merged: Task #{TaskId}`
     - **Body:** `{TaskTitle} — Successfully merged into {BaseBranch}.`
     - **Tag:** `ncf-merged-{TaskId}`
     - **Click Target:** Focuses the window/tab and navigates to [TaskDetail.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/TaskDetail.razor) (`/tasks/{TaskId}`).

#### 3.5.2 Per-Subject User Preferences

Users can configure notification delivery preferences independently per subject to fit their workflow and avoid notification fatigue.

1. **Preference Configuration Model (`NotificationPreferences`):**
   - `MasterEnabled` (`bool`, default: `true`): Global master switch enabling/disabling web notifications.
   - `NotifyOnQuestionInbox` (`bool`, default: `true`): Notify when an agent asks a clarifying question.
   - `NotifyOnReviewRoom` (`bool`, default: `true`): Notify when a task enters the Review Room awaiting approval.
   - `NotifyOnTaskMerged` (`bool`, default: `true`): Notify when a task is merged to the base branch.

2. **Client-Side Persistence:**
   - Preferences are stored in the client browser (`localStorage` via JS Interop) using a versioned schema key (`ncf_notification_preferences_v1`).
   - Storing preferences on the client ensures preferences are device-specific (e.g. an engineer can enable all notifications on mobile while muting merge notifications on desktop).

3. **Settings UI Integration ([Settings.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Settings.razor)):**
   - Render a dedicated **🔔 Web Notifications** tab or card in [Settings.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Settings.razor) under Global System Settings.
   - **Browser Permission Status:**
     - Displays current browser permission state (`granted`, `denied`, or `default`).
     - If `default` (unrequested), displays a prominent **"Request Notification Permission"** button.
     - If `denied`, displays an advisory banner explaining that notifications are blocked by browser/OS settings with instructions on how to re-enable them in site permissions.
     - If `granted`, displays a **"Send Test Notification"** button to allow instantaneous verification.
   - **Subject Toggles:** Individual toggle switches for each subject with clear descriptions. Toggles are disabled if master switch is off or browser permission is denied.

#### 3.5.3 Web Worker / Service Worker Dispatching & Mobile Deep-Linking

On mobile devices (such as Chrome on Android and iOS Safari 16.4+), calling `new Notification()` directly in the window context is either strictly prohibited by the browser (`TypeError: Illegal constructor`) or blocked when the tab is backgrounded. Mobile operating systems mandate using the Web Worker / Service Worker API (`ServiceWorkerRegistration.showNotification()`) to display system notifications.

1. **JavaScript Interop Layer (`notifications.js`):**
   - Exposes interop methods for Blazor:
     - `isSupported()`: Checks if `'Notification' in window` and `'serviceWorker' in navigator`.
     - `getPermissionState()`: Returns `Notification.permission`.
     - `requestPermission()`: Invokes `Notification.requestPermission()`.
     - `showNotification(title, options)`: 
       - On mobile (and any browser with an active service worker), acquires `navigator.serviceWorker.ready` and dispatches via `registration.showNotification(title, options)`.
       - On desktop browsers without an active service worker, falls back safely to `new Notification(title, options)`.
   - Notification options payload includes:
     - `icon`: `/favicon.ico` or `/favicon.png`.
     - `badge`: Monochromatic badge icon for mobile status bars.
     - `tag`: Unique per-event tag (e.g. `ncf-question-{TaskId}`) so newer updates for the same task replace stale notifications rather than piling up.
     - `data.url`: Deep link target path (e.g. `/inbox?taskId={TaskId}`, `/review/{TaskId}`, `/tasks/{TaskId}`).

2. **Web Worker Click Handling (`notificationclick`):**
   - In `service-worker.js`, notifications clicked on mobile fire the worker's `notificationclick` event listener:
     ```javascript
     self.addEventListener('notificationclick', (event) => {
         event.notification.close();
         const targetUrl = event.notification.data?.url || '/';

         event.waitUntil(
             clients.matchAll({ type: 'window', includeUncontrolled: true }).then((clientList) => {
                 for (const client of clientList) {
                     if (client.url && 'focus' in client) {
                         return client.focus().then(() => {
                             if ('navigate' in client) {
                                 return client.navigate(targetUrl);
                             }
                         });
                     }
                 }
                 if (clients.openWindow) {
                     return clients.openWindow(targetUrl);
                 }
             })
         );
     });
     ```
   - This ensures that tapping a notification on a mobile phone immediately brings NightCodeForge to the foreground and navigates to the target page, whether the tab was backgrounded or closed.

#### 3.5.4 Telemetry Integration & Deduplication Guard

1. **SignalR Telemetry Hook:**
   - A scoped/singleton service (`WebNotificationService`) listens to [OrchestratorHubClientConnection](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/OrchestratorHubClientConnection.cs) events:
     - `OnWorkerStatusReceived` / task state changes indicating `WaitingForHuman`.
     - `OnStageTransitionReceived` / task status reaching `Ready`.
     - Task status transitions to `Closed` (merged).
2. **Deduplication & Anti-Spam Guard:**
   - Maintain an in-memory/in-session cache of recently dispatched notification signatures (e.g. `{Subject}:{TaskId}:{StateHash}`).
   - Discards redundant events triggered during SignalR reconnections or repeated telemetry polls within a debounce window.
3. **Tab Active Suppression (Optional/Configurable):**
   - If the user is currently actively viewing the exact target page (e.g. already on `/inbox` when a question arrives), the native OS notification may either be suppressed or delivered as a standard in-app toast notification via [ToastService](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/ToastService.cs) to prevent redundant clutter.

---

### 3.6 Connection State & Visibility Synchronization

1. **Connection Reconnect Indicator:**
   If the mobile device switches network interfaces or experiences momentary packet loss, display a subtle non-blocking banner:
   `"Reconnecting to Forge..."` with automatic backoff retry.
2. **Page Visibility State Sync:**
   When the mobile browser tab returns to the foreground (`visibilitychange` event), trigger a lightweight state refresh of active task status to ensure displayed data is fresh without requiring a manual pull-to-refresh.

---

## 4. Implementation Checklist & Deliverables

- [ ] **Global Layout & Responsive Styles:**
  - [ ] Add mobile viewport meta tag and CSS touch variables to `app.css` / `theme.css`.
  - [ ] Implement hamburger toggle and off-canvas mobile drawer in `MainLayout.razor` and `NavMenu.razor`.
  - [ ] Enforce minimum $44\times 44\text{px}$ touch targets across all interactive buttons and chips.
- [ ] **Ideation Mobile Experience:**
  - [ ] Style fixed-bottom chat input bar pinned above virtual keyboards.
  - [ ] Make proposal card buttons full-width stackable on viewports $< 768\text{px}$.
  - [ ] Add quick-capture note input modal/drawer accessible from top bar.
- [ ] **Inbox Mobile Experience:**
  - [ ] Optimize `QuestionCard.razor` for mobile touch selection and full-width submit actions.
- [ ] **Task Inspector & Drawer:**
  - [ ] Transform `TaskDetailDrawer.razor` into a bottom sheet on mobile screens.
  - [ ] Make `PipelineStepper.razor` horizontally scrollable on mobile.
- [ ] **Responsive Tables:**
  - [ ] Implement responsive card-stack view for `DevQueue.razor` and `Backlog.razor` on narrow screens.
- [ ] **PWA Configuration (Blazor Web App):**
  - [ ] Create `wwwroot/manifest.webmanifest` defining `standalone` mode, colors, and application icons.
  - [ ] Implement `wwwroot/service-worker.js` with cache-first static asset strategy, SignalR/WebSocket bypass, and `notificationclick` handler.
  - [ ] Wire manifest link, theme meta tags, and Service Worker registration in [App.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/App.razor).
- [ ] **Web Notifications & User Preferences:**
  - [ ] Implement browser notification JS interop module (`notifications.js`) dispatching via `ServiceWorkerRegistration.showNotification()` on mobile (with desktop fallback) and handling permission requests.
  - [ ] Implement `WebNotificationService` and `NotificationPreferences` client store with `localStorage` persistence.
  - [ ] Build **Web Notifications** settings tab/card in `Settings.razor` with permission state badge, test button, master switch, and per-subject toggles (`question_inbox`, `review_room`, `task_merged`).
  - [ ] Wire SignalR telemetry and task transition listeners to trigger notifications when human intervention is required or tasks are merged.
  - [ ] Implement deduplication guard preventing duplicate notifications during network shifts and SignalR reconnects.
  - [ ] Add unit tests for preference evaluation, deduplication filtering, and permission state handling.
- [ ] **Network & Visibility Handling:**
  - [ ] Wire `visibilitychange` listener to refresh active task status on mobile tab focus.
- [ ] **Automated Testing:**
  - [ ] bUnit tests asserting responsive CSS class bindings on mobile viewports.
  - [ ] Playwright viewport test at $375\times 667\text{px}$ (iPhone SE) and $390\times 844\text{px}$ (iPhone 12/13/14) verifying zero horizontal overflow, drawer open/close toggles, and PWA manifest presence.
