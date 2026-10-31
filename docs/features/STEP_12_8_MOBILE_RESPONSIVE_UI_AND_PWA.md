# 📱 Step 12.8: Mobile-Responsive Web UI, PWA & Web Worker Notifications

> **Phase 12 Implementation Step (Extend Features - Deep Integrations & Remote Experience)** *(formerly Step 11.6)*  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Related Steps:* [Step 12.4: Ideation Quick Notes](STEP_12_4_IDEATION_QUICK_NOTES_SUGGESTIONS_AND_REFINEMENT.md) | [Step 12.12: Voice Support](STEP_12_12_VOICE_SUPPORT_AND_CONTINUOUS_DICTATION.md) | [Step 12.16: Remote Tailscale Access](STEP_12_16_TAILSCALE_REMOTE_ACCESS.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **First-Class Mobile Experience:** Ensure the NightCodeForge Blazor dashboard is fully responsive, touch-friendly, and ergonomically optimized for mobile phones and tablets (iOS and Android).
2. **On-the-Go Workflow Operations:** Enable software engineers to effortlessly perform critical triage and oversight tasks from a smartphone:
   - Monitor real-time pipeline execution, stage transitions, and GPU/CPU gauges.
   - Answer pending human clarifying questions in [Inbox.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Inbox.razor) with touch-friendly selection chips.
   - Jot down spontaneous ideas into the Ideation Backlog via a one-tap quick-note capture bar ([Step 12.4](STEP_12_4_IDEATION_QUICK_NOTES_SUGGESTIONS_AND_REFINEMENT.md)).
   - Review quality gate metrics and approve/discard ideation proposal cards.
3. **Adaptive Navigation & Touch Ergonomics:** Replace desktop-oriented layouts with mobile slide-out navigation drawers, bottom-sheet drawers, 44px+ touch targets, and fixed-bottom chat input bars that remain pinned above mobile on-screen keyboards.
4. **Installable Progressive Web App (PWA):** Configure a Web App Manifest (`manifest.webmanifest`) and Service Worker (`service-worker.js`) to support "Add to Home Screen" with standalone fullscreen display mode and persistent permissions.
5. **Web Worker Notifications & Subject Preferences:** Deliver native browser Web Notifications dispatched via the Web Worker / Service Worker on mobile devices (and desktop browsers) when tasks require human intervention (question inbox clarification, review room sign-off) or when tasks merge to the base branch, paired with granular user preferences to toggle notifications per subject.

*(Note: Dedicated remote network infrastructure, MagicDNS TLS termination, and reverse proxy configuration are specified in [Step 12.16: Remote Tailscale Access](STEP_12_16_TAILSCALE_REMOTE_ACCESS.md).)*

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
- Developers monitoring tasks on mobile frequently switch between apps, keep the browser tab in the background, or lock their phone screen while waiting for multi-minute LLM reasoning and build runs.
- Without proactive notifications, developers must constantly poll the dashboard to see if an agent asked a clarifying question or if a task is waiting for review.
- On mobile platforms (Chrome on Android, iOS Safari 16.4+), standard `new Notification()` constructors in the window context are forbidden (`TypeError: Illegal constructor`) or blocked in background tabs. Notifications **must** be dispatched by the Web Worker / Service Worker registration interface (`ServiceWorkerRegistration.showNotification()`).
- Furthermore, when a mobile OS suspends a backgrounded tab or locks the screen, the browser may pause active WebSocket/SignalR connections until the tab wakes or reconnects. Combining real-time SignalR telemetry notifications (when the connection is active) with a state catch-up check upon `visibilitychange` / circuit reconnection ensures that transitions occurring while the device was asleep immediately surface a notification on wakeup.
- Developers also require fine-grained control: some may want alerts only when their input is blocking progress (questions or review approvals) without alerts on task completions, or vice-versa.

### 2.4 Browser Chrome & Permission Ephemerality
- Running NightCodeForge inside standard mobile browser tabs wastes vertical screen space on address bars and navigation controls, and `100vh` layouts get clipped by dynamic mobile browser toolbars unless dynamic viewport units (`100dvh`) and safe-area insets (`env(safe-area-inset-*)`) are used.
- Mobile browsers frequently discard permissions (such as microphone access for [Step 12.12](STEP_12_12_VOICE_SUPPORT_AND_CONTINUOUS_DICTATION.md) voice dictation) when run as transient browser tabs.
- Transforming the web app into an installable **Progressive Web App (PWA)** provides a distraction-free, app-like standalone window while permanently maintaining granted permissions.

---

## 3. Behavioral & Technical Specifications

### 3.1 Responsive Viewport & Navigation Structure

1. **Viewport Meta Tag & Safe-Area Insets:**
   Ensure [App.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/App.razor) contains the responsive viewport configuration:
   ```html
   <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=5.0, viewport-fit=cover" />
   ```
   - Update `.forge-layout` in `app.css` to use `height: 100dvh;` (with `100vh` fallback) so dynamic mobile browser address bars do not clip bottom-pinned UI elements.
   - Apply `env(safe-area-inset-top)`, `env(safe-area-inset-bottom)`, `env(safe-area-inset-left)`, and `env(safe-area-inset-right)` padding to the top navbar, mobile navigation drawer, fixed-bottom chat bars, and bottom sheets to prevent overlap with iOS notches and home indicators.

2. **Mobile Drawer Navigation ([NavMenu.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Layout/NavMenu.razor), [TopNavBar.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Layout/TopNavBar.razor) & [MainLayout.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Layout/MainLayout.razor)):**
   - On desktop screens (`> 1024px`): Display full `260px` desktop sidebar (or `72px` when manually collapsed).
   - On tablet screens (`768px` – `1024px`): Display compact `72px` icon-only sidebar (existing `@media (max-width: 1024px)` rule in `app.css`).
   - On mobile screens (`< 768px`):
     - `.forge-layout` collapses to a single column (`grid-template-columns: 1fr`), overriding the `1024px` rule.
     - Sidebar (`NavMenu.razor`) transforms into a fixed off-canvas drawer (`position: fixed; top: 0; left: 0; bottom: 0; width: 280px; z-index: 1050;`) with full labels visible when open.
     - `TopNavBar.razor` renders a prominent hamburger button ($\ge 44\times 44\text{px}$) on the far left.
     - Tapping the hamburger opens the navigation drawer with smooth CSS slide-in (`transform: translateX(0)`) and a dark backdrop overlay. Tapping any `NavLink` or the backdrop closes the drawer automatically.

3. **Top Bar Quick Status Strip (`TopNavBar.razor`):**
   - On `< 768px`, hide secondary desktop breadcrumb text and compact the workspace selector and action buttons so they fit without horizontal overflow.
   - Display a compact active worker pill (e.g. `⚡ #42`) and a fast "+ Note" shortcut button ([Step 12.4](STEP_12_4_IDEATION_QUICK_NOTES_SUGGESTIONS_AND_REFINEMENT.md)) directly in the mobile top bar.

### 3.2 Touch Target Ergonomics & CSS Polish

1. **Touch Target Size:** All interactive buttons, nav links, dropdown triggers, filter chips, and proposal action chips must have a minimum clickable/tappable area of **$44\times 44\text{px}$** (using transparent padding or `min-height: 44px; min-width: 44px;` on mobile viewports if the visual badge is smaller).
2. **Hover Elimination:** Any critical action that currently relies on `:hover` must be persistently visible or accessible via direct tap on touch devices (`@media (hover: none)`).
3. **No Horizontal Scroll on Root:** `html`, `body`, `.forge-layout`, and `.forge-main-content` must enforce `overflow-x: hidden; max-width: 100vw;` to eliminate accidental horizontal page jitter during vertical scrolling.

### 3.3 Page-by-Page Mobile Adaptations

#### A. Ideation Chat ([Ideation.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Ideation.razor))
- **Mobile Pane Switcher (`< 768px`):** On desktop, `.ideation-grid` displays `.chat-pane` (~60%) and `.proposals-pane` (~40%) side-by-side. On mobile viewports (`< 768px`), collapse `.ideation-grid` into a single-column view with a sticky top segmented switcher bar (`💬 Chat` | `📋 Proposals (@Session.Proposals.Count)` | `💡 Suggestions` from Step 12.4) so the proposals pane does not get pushed off-screen or collide with the chat input bar.
- **Fixed Bottom Input Bar:** When viewing the Chat tab on mobile, the user prompt input box, voice dictation button ([Step 12.12](STEP_12_12_VOICE_SUPPORT_AND_CONTINUOUS_DICTATION.md)), and "Send" button are pinned to the bottom of the viewport (`position: sticky; bottom: 0; padding-bottom: env(safe-area-inset-bottom);`), remaining visible when the virtual keyboard appears.
- **Scrollable Turn History:** Messages container occupies `flex: 1; overflow-y: auto;` with auto-scrolling to the latest assistant message via `NightCodeForge.initAutoScroll`.
- **Card Stacking:** Proposal cards ([IdeationProposalCard.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Ideation/IdeationProposalCard.razor)) display actions (`Approve`, `Keep as Suggestion`, `Discard`) as full-width stacked buttons on mobile rather than a tight horizontal row.

#### B. Human Question Inbox ([Inbox.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Inbox.razor), [QuestionCard.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Inbox/QuestionCard.razor) & [AgentQuestionTurnCard.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Monitoring/AgentQuestionTurnCard.razor))
- **Deep-Link Query Parameter (`taskId`):** Add `[SupplyParameterFromQuery(Name = "taskId")] public int? TaskId { get; set; }` to `Inbox.razor`. When navigated to via `/inbox?taskId={TaskId}` (e.g. from a mobile notification tap), automatically ensure the task's workspace/questions are visible and scroll/highlight the matching `QuestionCard` (`#question-card-task-{TaskId}`).
- **Touch-Friendly Options & Submission:** In `AgentQuestionTurnCard.razor` (which renders the UI for `QuestionCard.razor`), style `.agent-option-chip` choice buttons as large, full-width or flex-wrapped touch-friendly pill chips ($\ge 44\text{px}$ height) with clear active states.
- Anchor the `.submit-answer-btn` ("Submit & Resume ForgeTask" / "Submit All Clarifications") as a full-width primary button at the bottom of the card on mobile.

#### C. Task Execution Inspector ([TaskExecutionInspector.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Monitoring/TaskExecutionInspector.razor))
- **Responsive Stage & Run Selector:** Replace the inline two-column grid style (`grid-template-columns: minmax(260px, 320px) minmax(0, 1fr)`) in the Stage Conversations tab with a responsive CSS class (`.inspector-conversations-grid`). On `< 768px`, collapse to a single column (`grid-template-columns: 1fr`) where `StageRunSelector` renders as a compact collapsible top selector/dropdown above `StageConversationViewer`.
- **Horizontal Stepper:** Pipeline stage circles in [PipelineStepper.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Monitoring/PipelineStepper.razor) render in a horizontal touch-swipeable scroll container (`overflow-x: auto; -webkit-overflow-scrolling: touch;`).
- **Terminal Log Viewer:** Terminal output in [TerminalConsole.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Monitoring/TerminalConsole.razor) wraps cleanly with a toggle for `line-wrap` vs `horizontal scroll`.
- **Turns Accordion:** Stage reasoning traces (`.think-accordion`) and tool call cards collapse into touch-expandable accordions to prevent excessive vertical scrolling.

#### D. Backlog, Task Detail, Review Room & Dev Queue ([Backlog.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Backlog.razor), [TaskDetail.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/TaskDetail.razor), [Review.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Review.razor), [DevQueue.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/DevQueue.razor))
- **Backlog & Kanban Board (`Backlog.razor` & `KanbanBoard.razor`):**
  - On `< 768px`, Kanban columns in `KanbanBoard.razor` either stack vertically as collapsible status sections or scroll horizontally with CSS scroll-snap (`scroll-snap-type: x mandatory;`), while the status filter chips in `.backlog-stats-bar` scroll horizontally without wrapping into a tall wall of chips.
  - When [TaskDetailDrawer.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor) is used, it slides up from the bottom as a **mobile bottom sheet** (`max-height: 90dvh; border-radius: 16px 16px 0 0;`) with a close button and backdrop tap-to-dismiss, replacing the desktop right-hand side drawer.
- **Task Detail Page (`TaskDetail.razor`):**
  - On `< 768px`, `.task-detail-grid` collapses its two columns (`.grid-column-main` and `.grid-column-sidebar`) into a single vertical stack, and `.detail-nav-tabs` becomes horizontally scrollable.
- **Review Room & Diff Workspace (`Review.razor` & `DiffWorkspace.razor`):**
  - On `< 768px`, stack the Review header action buttons (`Request Changes`, `Approve & Merge`) into a full-width action row, collapse `.gate-metrics-grid` into a single-column or 3-up compact strip, and collapse the `DiffWorkspace.razor` file tree sidebar into a collapsible file-picker dropdown above a horizontally scrollable diff canvas.
- **Dev Queue Tables (`DevQueue.razor`, `DevQueueTable.razor`, `FailedJobsTable.razor`, `ActiveLocksGrid.razor`):**
  - On `< 768px`, data tables seamlessly transform into responsive card stacks (`display: flex; flex-direction: column;`) displaying Task/Job ID, Queue badge, Title, Wait Age, and touch-friendly action buttons (`Prioritize`, `Cancel`, `Requeue`, `Emergency Unlock`).

---

### 3.4 Progressive Web App (PWA) Architecture

Transforming NightCodeForge into an installable PWA enables standalone window execution, eliminates browser chrome, and allows persistent permissions on mobile.

#### 3.4.1 Web App Manifest (`wwwroot/manifest.webmanifest`) & Icons
Provide a valid web manifest meeting modern Chrome/Android and iOS Safari PWA installability requirements, backed by properly sized raster icons (`icon-192.png` at $192\times 192\text{px}$ and `icon-512.png` at $512\times 512\text{px}$) alongside `favicon.svg`:

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

#### 3.4.2 Service Worker (`wwwroot/service-worker.js`)
A lightweight service worker providing a network-first static asset strategy (with offline cache fallback for app shell assets) while strictly bypassing non-`GET` requests, dynamic Blazor Server circuits (`/_blazor`), SignalR hubs (`/hubs/`), and the Hangfire dashboard (`/hangfire`):

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

#### 3.4.3 Registration in [App.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/App.razor) & CSP Alignment
1. **CSP Header Update:** Ensure [ContentSecurityPolicyMiddleware.cs](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Middleware/ContentSecurityPolicyMiddleware.cs) explicitly includes `manifest-src 'self';` alongside `worker-src 'self' blob:;`.
2. **Manifest & Meta Tags in `App.razor`:** Add the manifest link, mobile web-app meta tags (both standard `mobile-web-app-capable` and Apple-specific tags), and service worker registration inside `wwwroot/js/app.js` or `App.razor`:

```html
<head>
    ...
    <link rel="manifest" href="manifest.webmanifest" />
    <meta name="theme-color" content="#4f46e5" />
    <meta name="mobile-web-app-capable" content="yes" />
    <meta name="apple-mobile-web-app-capable" content="yes" />
    <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent" />
    <link rel="apple-touch-icon" href="icon-192.png" />
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

To enable prompt responsiveness while away from the desktop, NightCodeForge provides native browser Web Notifications driven by real-time SignalR telemetry events (plus foreground/reconnect state synchronization) and dispatched by the Web Worker / Service Worker (`ServiceWorkerRegistration.showNotification()`) on mobile and desktop.

#### 3.5.1 Notification Event Subjects & Triggers

Notifications are dispatched for three distinct operational subjects:

1. **Clarification Question Waiting (`question_inbox`):**
   - **Trigger Condition:** An autonomous agent pauses execution with [ForgeTask.IsWaitingForHuman](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/ForgeTask.cs) `== true` (or stage transition to `StageExecutionStatus.WaitingForHuman` / `WorkerStatusConstants.WaitingForHuman`).
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
   - Render a dedicated **🔔 Web Notifications** tab (`WebNotificationsSettingsTab.razor`) in [Settings.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Settings.razor) under Global System Settings.
   - **Browser Permission Status:**
     - Displays current browser permission state (`granted`, `denied`, `default`, or `unsupported` if not in a secure context).
     - If `default` (unrequested), displays a prominent **"Request Notification Permission"** button.
     - If `denied`, displays an advisory banner explaining that notifications are blocked by browser/OS settings with instructions on how to re-enable them in site permissions.
     - If `granted`, displays a **"Send Test Notification"** button to allow instantaneous verification.
   - **Subject Toggles:** Individual toggle switches for each subject with clear descriptions. Toggles are disabled if master switch is off or browser permission is denied.

#### 3.5.3 Web Worker / Service Worker Dispatching & Mobile Deep-Linking

On mobile devices (such as Chrome on Android and iOS Safari 16.4+), calling `new Notification()` directly in the window context is strictly prohibited (`TypeError: Illegal constructor`). Mobile operating systems mandate using the Web Worker / Service Worker API (`ServiceWorkerRegistration.showNotification()`) to display system notifications.

1. **JavaScript Interop Layer (`wwwroot/js/notifications.js`):**
   - Exposes interop methods for Blazor (`window.NightCodeForge.notifications`):
     - `isSupported()`: Checks if `'Notification' in window` and `'serviceWorker' in navigator`.
     - `getPermissionState()`: Returns `Notification.permission` (`'granted'`, `'denied'`, or `'default'`).
     - `requestPermission()`: Invokes `Notification.requestPermission()`.
     - `showNotification(title, options)`: 
       - On mobile (and any browser with an active service worker), acquires `navigator.serviceWorker.ready` (or `navigator.serviceWorker.getRegistration()`) and dispatches via `registration.showNotification(title, options)`.
       - On desktop browsers without an active service worker registration (e.g., non-HTTPS local development), falls back safely to `new Notification(title, options)`.
   - Notification options payload includes:
     - `icon`: `/icon-192.png` (or `/favicon.png`).
     - `badge`: `/favicon.png` (badge icon for mobile status bars).
     - `tag`: Unique per-event tag (e.g. `ncf-question-{TaskId}`) so newer updates for the same task replace stale notifications rather than piling up.
     - `data.url`: Deep link target path (e.g. `/inbox?taskId={TaskId}`, `/review/{TaskId}`, `/tasks/{TaskId}`).

2. **Web Worker Click Handling (`notificationclick`):**
   - In `service-worker.js`, notifications clicked on mobile fire the worker's `notificationclick` event listener:
     ```javascript
     self.addEventListener('notificationclick', (event) => {
         event.notification.close();
         const targetUrl = new URL(event.notification.data?.url || '/', self.location.origin).href;

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

#### 3.5.4 Telemetry Integration, Task Resolution & Deduplication Guard

1. **Global Layout Host & SignalR Telemetry Hook:**
   - Mount a global notification listener component or scoped service (`WebNotificationService`) in [MainLayout.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Layout/MainLayout.razor) so it remains active across all pages of the Blazor circuit.
   - `WebNotificationService` subscribes to [OrchestratorHubClientConnection](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/OrchestratorHubClientConnection.cs) events (`OnStageTransitionReceived`, `OnWorkerStatusReceived`) and resolves the current [`ForgeTask`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/ForgeTask.cs) from `ITaskRepository` (or receives enriched task metadata via SignalR) to inspect `Title`, `Status`, `MergeMode`, `BaseBranch`, and `IsWaitingForHuman`:
     - When `IsWaitingForHuman == true` (or stage transition status is `WaitingForHuman`): triggers `question_inbox`.
     - When `Status == TaskOverallStatus.Ready` and `MergeMode == MergeMode.ManualReviewRequired`: triggers `review_room`.
     - When `Status == TaskOverallStatus.Closed` (e.g. upon completion of `StandardStageIds.Merge` or manual review approval): triggers `task_merged`.
2. **Wakeup / Reconnect State Catch-Up:**
   - Because mobile OSes suspend background WebSocket connections when a phone screen is locked, `WebNotificationService` also hooks into SignalR reconnection (`OnConnectionStateChanged` $\rightarrow$ `Connected`) and page wakeup (`visibilitychange` $\rightarrow$ `visible`). On wakeup/reconnect, it compares current workspace task states against its baseline snapshot and dispatches any notification whose transition occurred while the socket was suspended.
3. **Deduplication & Anti-Spam Guard:**
   - Maintain an in-memory/session cache (and `sessionStorage` / `localStorage` ring buffer) of recently dispatched notification signatures (`{Subject}:{TaskId}:{Status}:{UpdatedAtTicks}`).
   - Discards redundant events triggered during SignalR reconnections, page navigations, or repeated telemetry polls.
4. **Tab Active Suppression (Optional/Configurable):**
   - If the document is currently visible (`document.visibilityState === 'visible'`) and the user is already viewing the exact target route (e.g. already on `/inbox` when a question arrives), the native OS notification may be suppressed in favor of an in-app toast via [ToastService](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/ToastService.cs) to prevent redundant clutter.

---

### 3.6 Connection State & Visibility Synchronization

1. **Connection Reconnect Indicator:**
   If the mobile device switches network interfaces or experiences momentary packet loss, display a subtle non-blocking banner in `MainLayout.razor`:
   `"Reconnecting to Forge..."` driven by `OrchestratorHubClientConnection.OnConnectionStateChanged` and Blazor's circuit reconnect state, auto-dismissing upon reconnection.
2. **Page Visibility State Sync:**
   When the mobile browser tab returns to the foreground (`visibilitychange` event in `app.js` / `notifications.js`), notify the active Blazor circuit via `[JSInvokable]` callback to ensure `OrchestratorHubClientConnection` is connected and trigger a lightweight state refresh of active task status without requiring a manual page reload.

---

## 4. Implementation Checklist & Deliverables

- [ ] **Global Layout & Responsive Styles:**
  - [ ] Update viewport meta tag in `App.razor` (`viewport-fit=cover`) and add dynamic viewport height (`100dvh`), safe-area insets (`env(safe-area-inset-*)`), and mobile breakpoint variables to `app.css` / `theme.css`.
  - [ ] Implement hamburger toggle in `TopNavBar.razor` and off-canvas mobile drawer with backdrop in `MainLayout.razor` and `NavMenu.razor` for viewports `< 768px`.
  - [ ] Compact `TopNavBar.razor` on mobile viewports and include active worker status pill and "+ Note" shortcut.
  - [ ] Enforce minimum $44\times 44\text{px}$ touch targets across interactive buttons and chips, and eliminate horizontal root overflow.
- [ ] **Ideation Mobile Experience (`Ideation.razor`):**
  - [ ] Add mobile pane switcher (`💬 Chat` | `📋 Proposals` | `💡 Suggestions`) on `< 768px` so the two-pane `.ideation-grid` adapts cleanly on narrow screens.
  - [ ] Style fixed-bottom chat input bar pinned above virtual keyboards with safe-area bottom padding.
  - [ ] Make `IdeationProposalCard.razor` action buttons full-width stackable on viewports `< 768px`.
- [ ] **Inbox Mobile Experience (`Inbox.razor`, `QuestionCard.razor`, `AgentQuestionTurnCard.razor`):**
  - [ ] Support `[SupplyParameterFromQuery] int? TaskId` in `Inbox.razor` to filter/highlight the target question card when deep-linked from notifications.
  - [ ] Optimize `AgentQuestionTurnCard.razor` choice chips ($\ge 44\text{px}$ touch targets) and full-width submit buttons for mobile.
- [ ] **Task Inspector, Backlog, Review Room & Dev Queue:**
  - [ ] Extract `TaskExecutionInspector.razor` conversations grid to a responsive CSS class that collapses `StageRunSelector` above `StageConversationViewer` on `< 768px`.
  - [ ] Make `PipelineStepper.razor` horizontally scrollable on mobile and add line-wrap toggle in `TerminalConsole.razor`.
  - [ ] Adapt `Backlog.razor`, `KanbanBoard.razor`, `TaskDetail.razor`, and `Review.razor` (`DiffWorkspace.razor`) for single-column mobile viewports, and style `TaskDetailDrawer.razor` as a `90dvh` bottom sheet on `< 768px`.
  - [ ] Implement responsive card-stack layout for `DevQueueTable.razor`, `FailedJobsTable.razor`, and `ActiveLocksGrid.razor` on narrow screens.
- [ ] **PWA Configuration (Blazor Web App):**
  - [ ] Create `wwwroot/manifest.webmanifest` and raster icons (`icon-192.png`, `icon-512.png`) defining `standalone` mode and theme colors.
  - [ ] Implement `wwwroot/service-worker.js` with `GET`-only network-first static asset caching, `/_blazor` + `/hubs/` + `/hangfire` bypass, and `notificationclick` deep-link handler.
  - [ ] Update `ContentSecurityPolicyMiddleware.cs` to include `manifest-src 'self';` and wire manifest link, meta tags, and Service Worker registration in [App.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/App.razor).
- [ ] **Web Notifications & User Preferences:**
  - [ ] Implement browser notification JS interop (`wwwroot/js/notifications.js`) dispatching via `ServiceWorkerRegistration.showNotification()` on mobile (with desktop fallback) and handling permission requests.
  - [ ] Implement `WebNotificationService` (hosted in `MainLayout.razor`) and `NotificationPreferences` client store with `localStorage` persistence (`ncf_notification_preferences_v1`).
  - [ ] Build **🔔 Web Notifications** settings tab (`WebNotificationsSettingsTab.razor`) in `Settings.razor` with permission state badge, test button, master switch, and per-subject toggles (`question_inbox`, `review_room`, `task_merged`).
  - [ ] Wire SignalR telemetry, `ITaskRepository` state resolution, and reconnect/visibility wakeup catch-up to trigger notifications when human intervention is required or tasks are merged.
  - [ ] Implement deduplication guard preventing duplicate notifications during network shifts and SignalR reconnects.
  - [ ] Add unit tests for preference evaluation, task transition matching, deduplication filtering, and permission state handling.
- [ ] **Network & Visibility Handling:**
  - [ ] Add non-blocking `"Reconnecting to Forge..."` banner in `MainLayout.razor` on transient SignalR/circuit drops.
  - [ ] Wire `visibilitychange` listener to refresh active task status and notification state on mobile tab focus.
- [ ] **Automated Testing:**
  - [ ] bUnit tests asserting responsive mobile navigation drawer toggles, `Inbox.razor?taskId=` deep-linking, and `WebNotificationsSettingsTab.razor` behavior.
  - [ ] Playwright viewport tests at $375\times 667\text{px}$ (iPhone SE) and $390\times 844\text{px}$ (iPhone 12/13/14) verifying zero horizontal overflow, drawer open/close toggles, and PWA manifest presence.
