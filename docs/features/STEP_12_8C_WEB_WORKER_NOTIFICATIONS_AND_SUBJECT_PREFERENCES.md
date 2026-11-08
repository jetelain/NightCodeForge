# 🔔 Step 12.8C: Web Worker Notifications, SignalR Catch-Up & Per-Subject Preferences

> **Feature Implementation Step (Part 3 of 3 — Service Worker Notifications & Settings UI)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Steps:* [Step 12.8A: Mobile-Responsive Layout](../archive/phase-12/STEP_12_8A_MOBILE_RESPONSIVE_LAYOUT_AND_TOUCH_ERGONOMICS.md) | [Step 12.8B: PWA Manifest & Service Worker Shell](STEP_12_8B_PROGRESSIVE_WEB_APP_MANIFEST_AND_SERVICE_WORKER.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests` | *Status:* ✅ Completed | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Developers monitoring tasks on mobile frequently switch between apps, keep the browser tab in the background, or lock their phone screen while waiting for multi-minute LLM reasoning and build runs. On mobile platforms (Chrome on Android, iOS Safari 16.4+), standard `new Notification()` constructors in the window context are forbidden (`TypeError: Illegal constructor`); notifications **must** be dispatched via `ServiceWorkerRegistration.showNotification()`.

This step (Part 3 of Step 12.8) delivers:
1. **Three Operational Notification Subjects:**
   - **Clarification Question Waiting (`question_inbox`):** Deep-links to `/inbox?taskId={TaskId}` (already wired via query parameter in `Inbox.razor`).
   - **Review Room Sign-off Required (`review_room`):** Deep-links to `/review/{TaskId}`.
   - **Task Merged (`task_merged`):** Deep-links to `/tasks/{TaskId}`.
2. **Service Worker Dispatch & `notificationclick` Deep-Linking:** Dispatch notifications via `ServiceWorkerRegistration.showNotification()` in `wwwroot/js/notifications.js` (with desktop fallback) and focus/navigate the client window on `notificationclick` in `service-worker.js`.
3. **Real-Time SignalR Hook + Wakeup Catch-Up + Deduplication (`WebNotificationService`):** Subscribe to [`OrchestratorHubClientConnection`](../../src/NightCodeForge.Web/Services/OrchestratorHubClientConnection.cs), catch up on missed state transitions when a locked phone wakes up (`visibilitychange` / reconnect), and deduplicate notification signatures.
4. **Device-Local Subject Preferences (`WebNotificationsSettingsTab.razor`):** Store per-device toggles in `localStorage` (`ncf_notification_preferences_v1`) and provide a dedicated **🔔 Web Notifications** tab in [`Settings.razor`](../../src/NightCodeForge.Web/Components/Pages/Settings.razor) with permission request, test notification buttons, and iOS Safari installation guidance.

---

## 2. Technical Specifications

### 2.1 Notification Event Subjects & Triggers

1. **Clarification Question Waiting (`question_inbox`):**
   - **Trigger Condition:** [`ForgeTask.IsWaitingForHuman`](../../src/NightCodeForge.Core/Domain/ForgeTask.cs) `== true` (or stage transition to `StageExecutionStatus.WaitingForHuman` / `WorkerStatusConstants.WaitingForHuman`).
   - **Payload:** Title: `❓ Human Input Needed: Task #{TaskId}`, Body: `{TaskTitle} — The agent has a clarifying question waiting for your answer.`, Tag: `ncf-question-{TaskId}`, Click URL: `/inbox?taskId={TaskId}`.

2. **Review Room Sign-off Required (`review_room`):**
   - **Trigger Condition:** Task transitions to `TaskOverallStatus.Ready` with `MergeMode.ManualReviewRequired`.
   - **Payload:** Title: `🔍 Ready for Review: Task #{TaskId}`, Body: `{TaskTitle} — All automated gates passed. Ready for code review and merge approval.`, Tag: `ncf-review-{TaskId}`, Click URL: `/review/{TaskId}`.

3. **Task Merged (`task_merged`):**
   - **Trigger Condition:** Task transitions to `TaskOverallStatus.Closed` (merged into base branch).
   - **Payload:** Title: `🚀 Task Merged: Task #{TaskId}`, Body: `{TaskTitle} — Successfully merged into {BaseBranch}.`, Tag: `ncf-merged-{TaskId}`, Click URL: `/tasks/{TaskId}`.

### 2.2 Per-Subject User Preferences & Settings UI

1. **Preference Configuration Model (`NotificationPreferences`):**
   - `MasterEnabled` (`bool`, default: `true`)
   - `NotifyOnQuestionInbox` (`bool`, default: `true`)
   - `NotifyOnReviewRoom` (`bool`, default: `true`)
   - `NotifyOnTaskMerged` (`bool`, default: `true`)
   - Stored in browser `localStorage` under `ncf_notification_preferences_v1` so preferences remain device-specific (e.g. mobile vs. desktop).

2. **Settings UI (`WebNotificationsSettingsTab.razor` in [`Settings.razor`](../../src/NightCodeForge.Web/Components/Pages/Settings.razor)):**
   - Located under Global System Settings (`_activeScope == SettingsScope.Global`) as tab key `"notifications"` (Label: `🔔 Web Notifications`).
   - Displays current browser permission state (`granted`, `denied`, `default`, or `unsupported`).
   - Provides **"Request Notification Permission"** button when `default` (invoked strictly on direct user click), advisory instructions when `denied`, and **"Send Test Notification"** button when `granted`.
   - When `unsupported` (e.g., on standard iOS Safari before Home Screen installation), displays an advisory message: *"On iOS Safari, tap Share → Add to Home Screen to install NightCodeForge and enable Web Push notifications."*
   - Renders individual toggle switches for `MasterEnabled`, `NotifyOnQuestionInbox`, `NotifyOnReviewRoom`, and `NotifyOnTaskMerged`.

### 2.3 Service Worker Dispatching & `notificationclick` Deep-Linking

1. **JavaScript Interop Layer (`wwwroot/js/notifications.js`):**
   - Exposes `window.NightCodeForge.notifications`:
     - `isSupported()`: Returns whether `Notification` and/or `ServiceWorkerRegistration` are available in the current context.
     - `getPermissionState()`: Returns `'granted'`, `'denied'`, `'default'`, or `'unsupported'`.
     - `requestPermission()`: Requests browser notification permission via `Notification.requestPermission()`.
     - `showNotification(title, options)`: Dispatches via active `ServiceWorkerRegistration.showNotification(title, options)`, falling back to `new Notification(title, options)` on desktop if no registration is active. Includes multi-tab deduplication guard (e.g., discarding duplicate dispatches within 2 seconds across open tabs using `sessionStorage`/`localStorage`).
     - `getPreferences()`, `savePreferences(prefs)`: Helper functions for reading and persisting `ncf_notification_preferences_v1` in `localStorage`.
2. **Service Worker Click Handler (`wwwroot/service-worker.js`):**
   - Handles `notificationclick` by closing the notification and focusing or navigating an existing window:
     ```javascript
     self.addEventListener('notificationclick', (event) => {
         event.notification.close();
         const targetUrl = (event.notification.data && event.notification.data.url) ? event.notification.data.url : '/';
         const fullTargetUrl = new URL(targetUrl, self.location.origin).href;

         event.waitUntil(
             clients.matchAll({ type: 'window', includeUncontrolled: true }).then((clientList) => {
                 for (const client of clientList) {
                     if (client.url === fullTargetUrl && 'focus' in client) {
                         return client.focus();
                     }
                 }
                 for (const client of clientList) {
                     if ('focus' in client && 'navigate' in client) {
                         client.focus();
                         return client.navigate(fullTargetUrl);
                     }
                 }
                 if (clients.openWindow) {
                     return clients.openWindow(fullTargetUrl);
                 }
             })
         );
     });
     ```

### 2.4 Telemetry Integration, Wakeup Catch-Up & Deduplication (`WebNotificationService`)

1. **SignalR Telemetry Hook:** Hosted in [`MainLayout.razor`](../../src/NightCodeForge.Web/Components/Layout/MainLayout.razor) as a scoped service, subscribes to `OrchestratorHubClientConnection` events (`OnStageTransitionReceived`, `OnWorkerStatusReceived`) and resolves task metadata via `ITaskRepository`.
2. **Wakeup / Reconnect State Catch-Up:** Hooks into SignalR reconnection (`OnConnectionStateChanged` $\rightarrow$ `Connected`) and tab wakeup (`OnTabForegrounded`), comparing current task states against its baseline snapshot to dispatch notifications for transitions that occurred while the mobile device was asleep.
3. **Deduplication Guard:** Maintains a ring buffer of dispatched notification signatures (`{Subject}:{TaskId}:{Status}:{UpdatedAtTicks}`) to prevent duplicate notifications across reconnects or page navigations.
4. **Active Route Suppression:** If `document.visibilityState === 'visible'` and the user is already viewing the target route, suppresses the OS notification in favor of an in-app toast via `ToastService`. Route comparison must use [`WorkspaceUrlHelper`](../../src/NightCodeForge.Web/Services/WorkspaceUrlHelper.cs) to account for workspace-prefixed paths (e.g., viewing `/w/1/inbox` suppresses notifications for `/inbox`).

---

## 3. Implementation Checklist & Deliverables

- [x] **JS Interop & Service Worker Click Handler:**
  - [x] Implement `wwwroot/js/notifications.js` (`ServiceWorkerRegistration.showNotification` + permission management + preferences storage + multi-tab guard).
  - [x] Add `notificationclick` deep-link handler to `wwwroot/service-worker.js`.
  - [x] Include `<script src="js/notifications.js"></script>` in `App.razor`.
- [x] **Notification Service & Preferences:**
  - [x] Implement `NotificationPreferences` model and `WebNotificationService` in `NightCodeForge.Web/Services` with SignalR subscription, wakeup catch-up, workspace-aware active route suppression, and signature deduplication.
  - [x] Mount `WebNotificationService` listener in `MainLayout.razor`.
- [x] **Settings UI (`WebNotificationsSettingsTab.razor`):**
  - [x] Build `WebNotificationsSettingsTab.razor` with permission state detection, user-click permission request, test notification trigger, iOS Safari guidance, and per-subject preference toggles.
  - [x] Register `WebNotificationsSettingsTab` under Global System Settings in `Settings.razor`.
- [x] **Automated Testing:**
  - [x] Unit tests for `WebNotificationService` covering preference filtering, task transition matching (`question_inbox`, `review_room`, `task_merged`), wakeup catch-up, and deduplication.
  - [x] bUnit tests for `WebNotificationsSettingsTab.razor` permission states, test notification trigger, and subject toggles.

---

## 4. Implementation Notes

- **Service Worker Dispatch & Deep-Linking (`src/NightCodeForge.Web/wwwroot/`):**
  - Implemented [`notifications.js`](../../src/NightCodeForge.Web/wwwroot/js/notifications.js) (`window.NightCodeForge.notifications`) with `isSupported()`, `getPermissionState()`, `requestPermission()`, `isDocumentVisible()`, `getPreferences()` / `savePreferences(prefs)` backed by `localStorage` (`ncf_notification_preferences_v1`), and `showNotification(title, options)` using `ServiceWorkerRegistration.showNotification` with desktop `new Notification()` fallback and a 2-second multi-tab deduplication window.
  - Added the `notificationclick` listener in [`service-worker.js`](../../src/NightCodeForge.Web/wwwroot/service-worker.js) to close the notification and focus/navigate an existing client window (or open a new window) to `event.notification.data.url`.
  - Registered `<script src="js/notifications.js"></script>` in [`App.razor`](../../src/NightCodeForge.Web/Components/App.razor).
- **Telemetry Hook, Wakeup Catch-Up & Deduplication (`src/NightCodeForge.Web/Services/`):**
  - Added [`NotificationPreferences.cs`](../../src/NightCodeForge.Web/Services/NotificationPreferences.cs) (`NotificationSubject`, `NotificationPreferences`), [`NotificationConstants.cs`](../../src/NightCodeForge.Web/Services/NotificationConstants.cs), [`RingBufferDeduplicator.cs`](../../src/NightCodeForge.Web/Services/RingBufferDeduplicator.cs), [`IWebNotificationService.cs`](../../src/NightCodeForge.Web/Services/IWebNotificationService.cs), and [`WebNotificationService.cs`](../../src/NightCodeForge.Web/Services/WebNotificationService.cs).
  - `WebNotificationService.InitializeAsync` seeds the baseline snapshot and deduplicator from existing `Ready`, `Closed`, and active runnable (`Open`, `Reviewed`, `Implemented` with `IsWaitingForHuman`) tasks as well as pending `Question` records, and starts `OrchestratorHubClientConnection` so real-time SignalR transitions (`OnStageTransitionReceived`, `OnWorkerStatusReceived`, `OnConnectionStateChanged`) are received on every page.
  - Wired `NotificationService.InitializeAsync()` and `NotificationService.HandleTabForegroundedAsync()` in [`MainLayout.razor`](../../src/NightCodeForge.Web/Components/Layout/MainLayout.razor) (`OnTabForegrounded` JS interop callback) and registered `IWebNotificationService` as a scoped service in [`Program.cs`](../../src/NightCodeForge.Web/Program.cs).
- **Settings UI & Automated Tests:**
  - Added [`WebNotificationsSettingsTab.razor`](../../src/NightCodeForge.Web/Components/Settings/WebNotificationsSettingsTab.razor) and registered the `🔔 Web Notifications` tab (`"notifications"`) under Global System Settings in [`Settings.razor`](../../src/NightCodeForge.Web/Components/Pages/Settings.razor).
  - Added unit and bUnit test suites in [`WebNotificationServiceTests.cs`](../../tests/NightCodeForge.Web.Tests/WebNotificationServiceTests.cs), [`WebNotificationsSettingsTabTests.cs`](../../tests/NightCodeForge.Web.Tests/WebNotificationsSettingsTabTests.cs), and updated [`SettingsComponentTests.cs`](../../tests/NightCodeForge.Web.Tests/SettingsComponentTests.cs).

