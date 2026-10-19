# 📱 Step 10.6: Mobile-Responsive Web UI & Remote Tailscale Experience

> **Phase 10 Implementation Step**  
> *Parent Plan:* [Phase 10 Master Plan](../10_EXTEND_FEATURES.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **First-Class Remote Mobile Experience:** Ensure the NightCodeForge Blazor dashboard is fully responsive, touch-friendly, and optimized for remote access via private mesh VPNs (such as **Tailscale** or WireGuard) on mobile phones and tablets.
2. **On-the-Go Workflow Operations:** Enable software engineers to effortlessly perform critical triage and oversight tasks from a smartphone:
   - Monitor real-time pipeline execution, stage transitions, and GPU/CPU gauges.
   - Answer pending human clarifying questions in [Inbox.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Inbox.razor) with touch-friendly selection chips.
   - Jot down spontaneous ideas into the Ideation Backlog via a one-tap quick-note capture bar.
   - Review quality gate metrics and approve/discard ideation proposal cards.
3. **Adaptive Navigation & Touch Ergonomics:** Replace desktop-oriented layouts with mobile slide-out navigation drawers, bottom-sheet drawers, 44px+ touch targets, and fixed-bottom chat input bars that remain pinned above mobile on-screen keyboards.
4. **Transient Connection Resilience:** Handle mobile network shifts (e.g. WiFi to cellular handoffs on Tailscale) with transparent SignalR auto-reconnection and state resynchronization on window focus.

---

## 2. Problem Statement & Motivation

### 2.1 The Remote Developer Pattern
- Developers hosting NightCodeForge on home workstations or dedicated LAN GPU servers frequently access the dashboard from smartphones via **Tailscale** (e.g. `http://gpu-server:5000` or `https://forge.tailnet-xyz.ts.net`).
- Currently, the web dashboard is optimized primarily for desktop resolutions:
  - The desktop navigation sidebar consumes significant horizontal width.
  - Multi-column tables in the Dev Queue and Backlog cause awkward horizontal overflow on narrow screens (< 600px).
  - Chat inputs in Ideation can be obscured when mobile virtual keyboards pop up.
  - Interactive buttons designed for mouse pointers (e.g. small icon buttons < 30px) are frustrating to tap accurately on touch screens.

### 2.2 Critical Mobile Use Cases
Engineers on mobile rarely write code; they primarily:
1. **Unblock Running Tasks:** A task pauses with a question; the developer gets notified and answers in 10 seconds from their phone.
2. **Check Status:** Checking whether a 15-minute build/test/review run passed or failed.
3. **Capture Quick Notes:** Jotting down a task idea before it is forgotten.

---

## 3. Behavioral Specifications

### 3.1 Responsive Viewport & Navigation Structure
1. **Viewport Meta Tag:**
   Ensure `App.razor` contains the correct responsive viewport configuration:
   ```html
   <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=5.0, viewport-fit=cover" />
   ```
2. **Mobile Drawer Navigation (`NavMenu.razor` & `MainLayout.razor`):**
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

#### A. Ideation Chat (`Ideation.razor`)
- **Fixed Bottom Input Bar:** The user prompt input box and "Send" button are fixed to the bottom of the viewport (`position: sticky; bottom: 0;`), remaining visible when the virtual keyboard appears.
- **Scrollable Turn History:** Messages container occupies `flex: 1; overflow-y: auto;` with auto-scrolling to the latest assistant message.
- **Card Stacking:** Proposal cards display actions (`Approve`, `Keep as Suggestion`, `Discard`) as full-width stacked buttons on mobile rather than a tight horizontal row.

#### B. Human Question Inbox (`Inbox.razor`)
- Grouped question batches render as clean full-width cards.
- Choice options render as large, touch-friendly pill chips with clear active radio states.
- The "Submit All Answers" action is anchored as a full-width primary button at the bottom of the card.

#### C. Task Execution Inspector (`TaskExecutionInspector.razor`)
- **Horizontal Stepper:** Pipeline stage circles render in a horizontal touch-swipeable scroll container (`overflow-x: auto; -webkit-overflow-scrolling: touch;`).
- **Terminal Log Viewer:** Terminal output wraps cleanly with a toggle for `line-wrap` vs `horizontal scroll`.
- **Turns Accordion:** Stage turns collapse into touch-expandable accordions to prevent excessive vertical scrolling.

#### D. Backlog & Dev Queue (`Backlog.razor`, `DevQueue.razor`)
- On mobile viewports ($< 768\text{px}$), data tables seamlessly transform into card stacks displaying Task ID, Title, Status badge, and primary action.
- [TaskDetailDrawer.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor) slides up from the bottom as a **mobile bottom sheet** ($90\text{vh}$ height) with a swipe-down or close button, replacing the desktop right-hand sidebar.

### 3.4 Tailscale & Remote Network Resilience
1. **SignalR Auto-Reconnect Indicator:**
   If the mobile device switches between WiFi and cellular over Tailscale, display a subtle non-blocking banner:
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
- [ ] **Network & Visibility Handling:**
  - [ ] Wire `visibilitychange` listener to refresh active task status on mobile tab focus.
- [ ] **Automated Testing:**
  - [ ] bUnit tests asserting responsive CSS class bindings on mobile viewports.
  - [ ] Playwright viewport test at $375\times 667\text{px}$ (iPhone SE) and $390\times 844\text{px}$ (iPhone 12/13/14) verifying zero horizontal overflow and drawer open/close toggles.
