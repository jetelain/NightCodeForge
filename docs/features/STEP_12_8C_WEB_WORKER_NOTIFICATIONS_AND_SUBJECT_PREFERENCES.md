# 🔔 Step 12.8C: Web Worker Notifications, SignalR Catch-Up & Per-Subject Preferences

> **Feature Implementation Step (Part 3 of 3 — Service Worker Notifications & Settings UI)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Steps:* [Step 12.8A: Mobile-Responsive Layout](STEP_12_8A_MOBILE_RESPONSIVE_LAYOUT_AND_TOUCH_ERGONOMICS.md) | [Step 12.8B: PWA Manifest & Service Worker Shell](STEP_12_8B_PROGRESSIVE_WEB_APP_MANIFEST_AND_SERVICE_WORKER.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Developers monitoring tasks on mobile frequently switch between apps, keep the browser tab in the background, or lock their phone screen while waiting for multi-minute LLM reasoning and build runs. On mobile platforms (Chrome on Android, iOS Safari 16.4+), standard `new Notification()` constructors in the window context are forbidden (`TypeError: Illegal constructor`); notifications **must** be dispatched via `ServiceWorkerRegistration.showNotification()`.

This step (Part 3 of Step 12.8) delivers:
1. **Three Operational Notification Subjects:**
   - **Clarification Question Waiting (`question_inbox`):** Deep-links to `/inbox?taskId={TaskId}`.
   - **Review Room Sign-off Required (`review_room`):** Deep-links to `/review/{TaskId}`.
   - **Task Merged (`task_merged`):** Deep-links to `/tasks/{TaskId}`.
2. **Service Worker Dispatch & `notificationclick` Deep-Linking:** Dispatch notifications via `ServiceWorkerRegistration.showNotification()` in `wwwroot/js/notifications.js` (with desktop fallback) and focus/navigate the client window on `notificationclick` in `service-worker.js`.
3. **Real-Time SignalR Hook + Wakeup Catch-Up + Deduplication (`WebNotificationService`):** Subscribe to [`OrchestratorHubClientConnection`](../../src/NightCodeForge.Web/Services/OrchestratorHubClientConnection.cs), catch up on missed state transitions when a locked phone wakes up (`visibilitychange` / reconnect), and deduplicate notification signatures.
4. **Device-Local Subject Preferences (`WebNotificationsSettingsTab.razor`):** Store per-device toggles in `localStorage` (`ncf_notification_preferences_v1`) and provide a dedicated **🔔 Web Notifications** tab in [`Settings.razor`](../../src/NightCodeForge.Web/Components/Pages/Settings.razor) with permission request and test notification buttons.

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
   - Displays current browser permission state (`granted`, `denied`, `default`, or `unsupported`).
   - Provides **"Request Notification Permission"** button when `default`, advisory instructions when `denied`, and **"Send Test Notification"** button when `granted`.
   - Renders individual toggle switches for `MasterEnabled`, `NotifyOnQuestionInbox`, `NotifyOnReviewRoom`, and `NotifyOnTaskMerged`.

### 2.3 Service Worker Dispatching & `notificationclick` Deep-Linking

1. **JavaScript Interop Layer (`wwwroot/js/notifications.js`):**
   - Exposes `window.NightCodeForge.notifications`: `isSupported()`, `getPermissionState()`, `requestPermission()`, and `showNotification(title, options)` (acquiring `navigator.serviceWorker.getRegistration()` to call `registration.showNotification(title, options)`, falling back to `new Notification(title, options)` on desktop when no service worker is active).
2. **Service Worker Click Handler (`wwwroot/service-worker.js`):**
   - Handles `notificationclick` by closing the notification, focusing an existing client window if open and navigating it to `event.notification.data.url`, or opening a new window via `clients.openWindow(targetUrl)`.

### 2.4 Telemetry Integration, Wakeup Catch-Up & Deduplication (`WebNotificationService`)

1. **SignalR Telemetry Hook:** Hosted in [`MainLayout.razor`](../../src/NightCodeForge.Web/Components/Layout/MainLayout.razor), subscribes to `OrchestratorHubClientConnection` events (`OnStageTransitionReceived`, `OnWorkerStatusReceived`) and resolves task metadata via `ITaskRepository`.
2. **Wakeup / Reconnect State Catch-Up:** Hooks into SignalR reconnection (`OnConnectionStateChanged` $\rightarrow$ `Connected`) and page wakeup (`visibilitychange` $\rightarrow$ `visible`), comparing current workspace task states against its baseline snapshot to dispatch notifications for transitions that occurred while the mobile device was asleep.
3. **Deduplication Guard:** Maintains a ring buffer of dispatched notification signatures (`{Subject}:{TaskId}:{Status}:{UpdatedAtTicks}`) to prevent duplicate notifications across reconnects or page navigations.
4. **Active Route Suppression:** If `document.visibilityState === 'visible'` and the user is already viewing the target route (e.g. `/inbox`), suppresses the OS notification in favor of an in-app toast via `ToastService`.

---

## 3. Implementation Checklist & Deliverables

- [ ] **JS Interop & Service Worker Click Handler:**
  - [ ] Implement `wwwroot/js/notifications.js` (`ServiceWorkerRegistration.showNotification` + permission management).
  - [ ] Add `notificationclick` deep-link handler to `wwwroot/service-worker.js`.
- [ ] **Notification Service & Preferences:**
  - [ ] Implement `NotificationPreferences` model and `WebNotificationService` in `NightCodeForge.Web/Services` with SignalR subscription, wakeup catch-up, and signature deduplication.
  - [ ] Mount `WebNotificationService` listener in `MainLayout.razor`.
- [ ] **Settings UI (`WebNotificationsSettingsTab.razor`):**
  - [ ] Build `WebNotificationsSettingsTab.razor` and register it under Global System Settings in `Settings.razor`.
- [ ] **Automated Testing:**
  - [ ] Unit tests for `WebNotificationService` covering preference filtering, task transition matching (`question_inbox`, `review_room`, `task_merged`), wakeup catch-up, and deduplication.
  - [ ] bUnit tests for `WebNotificationsSettingsTab.razor` permission states, test notification trigger, and subject toggles.

