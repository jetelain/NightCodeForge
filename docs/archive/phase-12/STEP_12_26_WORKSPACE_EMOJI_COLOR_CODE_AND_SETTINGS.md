# 🎨 Step 12.26: Workspace Visual Identity — Custom Emoji, Color Code & Settings Management

> **Quick-Win Feature Specification (Workspace Identity, Navigation & Settings Ergonomics)**  
> *Parent Plan:* [Phase 12 Master Plan](../12_EXTEND_FEATURES.md)  
> *Target Framework:* .NET 10 (C# 13) | *Status:* Completed  
> *Target Components:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests`  
> *Aligns With:* [AGENTS.md](../../../AGENTS.md), [docs/PLAN.md](../../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Summary & Problem Statement

Developers frequently maintain multiple active workspaces in NightCodeForge (e.g., `OllamaRouter`, `ComicGenCLI`, `NightCodeForge`, and auxiliary microservices or documentation repositories).

Currently:
1. **Generic Visual Appearance:** All workspaces use an identical default folder icon (`📁`) and unaccented card backgrounds across the Top Navigation Bar, Breadcrumbs, and Home Dashboard summary cards.
2. **Read-Only / Un-editable Identity in Settings:** In [`WorkspaceSettingsTab.razor`](../../../src/NightCodeForge.Web/Components/Workspaces/WorkspaceSettingsTab.razor), the workspace name is displayed as static text and cannot be edited. There are no controls to set an identifying emoji or color code.
3. **Cognitive Load When Context Switching:** When switching between workspaces, operators must read the full text title rather than recognizing the workspace instantly by its visual motif (e.g., 🚀 `OllamaRouter` in Indigo, 🎨 `ComicGen` in Amber, 🛡️ `NightCodeForge` in Emerald).

This quick-win step introduces **first-class Workspace Visual Identity**:
- Adds `Emoji` and `ColorCode` properties to the `Workspace` domain entity and database schema.
- Exposes direct editing of Workspace **Name**, **Emoji**, and **Color Code** in `WorkspaceSettingsTab.razor` with responsive presets and live preview.
- Surfaces custom emojis and color accents consistently across the Top Navigation Bar (`TopNavBar.razor`), the Breadcrumb trail, the Home Dashboard cards (`Home.razor`), and the Workspace Creation Wizard.
- Enforces strict UI presentation separation via a dedicated `WorkspacePresentationHelper` in `NightCodeForge.Web.Services`.

---

## 2. Detailed Behavioral & Architectural Specifications

### 2.1 Domain & Database Schema (`NightCodeForge.Core` & `NightCodeForge.Infrastructure`)

1. **Entity Extensions (`Workspace.cs`):**
   - Extend [`Workspace`](../../../src/NightCodeForge.Core/Domain/Workspace.cs) with two nullable string properties:
     - `Emoji` (`string?`): The visual emoji icon representing the workspace (e.g., `"🚀"`, `"🛡️"`, `"🎨"`, `"🌐"`, `"⚡"`). Defaults to `null` (falling back to `"📁"` in the presentation layer).
     - `ColorCode` (`string?`): The hex color code representing the workspace accent (e.g., `"#6366F1"` for indigo, `"#10B981"` for emerald). Defaults to `null` (falling back to the standard theme accent in the presentation layer).

2. **EF Core Database Migration:**
   - Add an EF Core migration in `NightCodeForge.Infrastructure.Data` (`AppDbContext`) adding `Emoji` (nullable, max length 16) and `ColorCode` (nullable, max length 32) columns to the `Workspaces` table.
   - Configure the properties with safe defaults in `WorkspaceConfiguration` or `OnModelCreating` to ensure seamless zero-downtime upgrades of existing SQLite databases.

### 2.2 Workspace Presentation & Identity Helper (`WorkspacePresentationHelper`)

To preserve strict Presentation Separation ([AGENTS.md](../../../AGENTS.md)) and prevent color/emoji logic from polluting Razor `@code` blocks:

1. **Helper Responsibilities (`NightCodeForge.Web.Services`):**
   - Create `WorkspacePresentationHelper` as a static or cohesive service:
     - **Default Fallback:** `ResolveEmoji(string? emoji)` returning `emoji` if non-empty, otherwise `"📁"`.
     - **Color Resolution:** `ResolveColor(string? colorCode, string fallback = "var(--primary)")` returning the valid sanitized hex or CSS token.
     - **Hex Color Validation:** `IsValidHexColor(string? color)` validating 3, 6, or 8 digit hex color codes (`#RGB`, `#RRGGBB`, `#RRGGBBAA`).
     - **Predefined Palette:** Provide an immutable collection of canonical color presets tailored for dark mode contrast:
       - Indigo (`#6366f1`)
       - Sky Blue (`#0ea5e9`)
       - Emerald Green (`#10b981`)
       - Amber Orange (`#f59e0b`)
       - Rose Red (`#f43f5e`)
       - Violet Purple (`#8b5cf6`)
       - Teal (`#14b8a6`)
       - Slate Gray (`#64748b`)
     - **Predefined Emoji Presets:** Provide an immutable collection of popular development presets:
       - `🚀` (Speed / Launch / Router)
       - `🛡️` (Security / Core / Forge)
       - `🎨` (Creative / Media / Frontend)
       - `🌐` (Web / Gateway / API)
       - `⚡` (Performance / Microservice)
       - `🧠` (AI / Inference / Research)
       - `📦` (Library / Package / Tools)
       - `🏢` (Enterprise / Monorepo)
       - `🔧` (CLI / DevOps / Infrastructure)
       - `🔬` (Testing / Experiments / Benchmark)

### 2.3 Workspace Settings UI (`WorkspaceSettingsTab.razor`)

1. **Editable Workspace Identity Section:**
   - In `WorkspaceSettingsTab.razor`, add a dedicated **Workspace Identity & Branding** section at the top of the tab:
     - **Name:** Editable text input bound to `ws.Name` (with required validation, preventing empty or whitespace-only names).
     - **Emoji Picker & Input:**
       - Quick-select button row of the predefined emoji presets. Clicking an emoji updates `ws.Emoji` immediately.
       - A text input allowing custom emoji input (with live character validation and a clear button).
       - Live badge preview showing the selected emoji next to the workspace title.
     - **Color Accent Picker & Palette:**
       - Palette row of circular or pill-shaped color chips for the predefined palette with a visual checkmark/ring on the active color.
       - An optional hex text input or native `<input type="color">` for custom branding colors.
     - **Live Identity Preview Card:**
       - Displays a miniature mock card showing how the workspace will appear in the Top Navigation Bar and Home Dashboard with its custom emoji and accent color.

2. **Persistence & Real-Time Notification:**
   - When the user clicks **💾 Save Workspace Settings**:
     - Persist the updated `Name`, `Emoji`, and `ColorCode` via `IWorkspaceRepository.UpdateAsync`.
     - Notify `WorkspaceStateService` that the active workspace has been updated (`NotifyWorkspaceUpdated()`), triggering re-renders across all active layout components (`TopNavBar`, `NavMenu`, `Home`).
     - Display a success toast: *"Workspace branding updated."*

### 2.4 Application-Wide Presentation Integrations

1. **Top Navigation Bar (`TopNavBar.razor`):**
   - Replace the static `📁` icon with `WorkspacePresentationHelper.ResolveEmoji(WorkspaceState.ActiveWorkspace?.Emoji)`.
   - Apply a subtle color indicator or inline style using `WorkspacePresentationHelper.ResolveColor(WorkspaceState.ActiveWorkspace?.ColorCode)`:
     - Accent dot or border on the workspace select container.
   - In the workspace selector dropdown `<select>`:
     - Render each option as `[Emoji] [Name]` (e.g. `🚀 OllamaRouter`, `🎨 ComicGenCLI`).
   - In the Breadcrumbs trail:
     - Render the workspace emoji alongside the workspace name.

2. **Home Dashboard (`Home.razor`):**
   - In the **Recent Workspaces** section (`#home-workspace-card-{ws.Id}`):
     - Apply an accent colored left border (`border-left: 4px solid {resolvedColor}`) to visually distinguish each workspace card.
     - In the card header, render `[Emoji] [Name]` with `ws.Emoji` prominently displayed.

3. **Workspace Creation Wizard (`WizardStep1PathProbe.razor` / Step 4):**
   - Allow optional emoji and color selection during new workspace onboarding, defaulting to `"📁"` and `#6366f1` if left unset.

---

## 3. Verification & Test Strategy

1. **Unit Tests (`NightCodeForge.Web.Tests`):**
   - `WorkspacePresentationHelperTests`:
     - Verify `ResolveEmoji` returns custom emoji when present and defaults to `"📁"` when null or whitespace.
     - Verify `ResolveColor` handles valid hex colors, case-insensitivity, and falls back to theme defaults on invalid input.
     - Verify `IsValidHexColor` correctly validates 3, 6, and 8 digit hex formats and rejects invalid characters or malformed strings.
     - Verify presets contain expected palettes and emojis.

2. **Component & Integration Tests (`NightCodeForge.Web.Tests`):**
   - `WorkspaceSettingsTabTests`:
     - Verify editing Name, selecting an Emoji preset, and selecting a Color preset binds correctly to the workspace model.
     - Verify clicking "Save Workspace Settings" invokes `WorkspaceRepo.UpdateAsync` with updated identity fields and emits `WorkspaceStateService.NotifyWorkspaceUpdated`.
   - `TopNavBarTests`:
     - Verify TopNavBar displays the active workspace's emoji and dropdown options include emojis.

3. **Playwright Browser E2E Tests (`NightCodeForge.Web.Tests/E2E`):**
   - Using `PlaywrightWebHostFixture` (zero LLM calls):
     - Navigate to `/w/{workspaceId}/settings`.
     - In `WorkspaceSettingsTab`, change workspace name to `"NightCodeForge Core"`, select the `🛡️` emoji preset, and choose the Emerald (`#10b981`) color preset.
     - Click **💾 Save Workspace Settings**.
     - Verify the TopNavBar dropdown and breadcrumb immediately update to `🛡️ NightCodeForge Core`.
     - Navigate to Home (`/`) and verify the recent workspace card reflects the `🛡️` emoji and emerald left border accent.

---

## 4. Definition of Done (DoD) Checklist

- [x] Add `Emoji` and `ColorCode` nullable properties to `Workspace.cs` in `NightCodeForge.Core.Domain`.
- [x] Create EF Core database migration adding `Emoji` and `ColorCode` columns to the `Workspaces` table in `NightCodeForge.Infrastructure`.
- [x] Implement `WorkspacePresentationHelper` in `NightCodeForge.Web.Services` with emoji fallback, color resolution, hex validation, and preset palettes.
- [x] Add Workspace Identity controls (Name text input, Emoji picker/presets, Color chips/picker, and Live Preview card) to `WorkspaceSettingsTab.razor`.
- [x] Update `WorkspaceStateService` to notify listeners on workspace updates.
- [x] Update `TopNavBar.razor` to render custom emoji in the selector icon, dropdown option text, and breadcrumbs with accent color.
- [x] Update `Home.razor` to render custom emoji in workspace card titles and apply colored left border accents.
- [x] Update `WorkspaceCreationWizardModal` to support optional initial emoji and color selection.
- [x] Author unit tests in `WorkspacePresentationHelperTests.cs` and component tests in `WorkspaceSettingsTabTests.cs`.
- [x] Author Playwright E2E test verifying workspace identity editing in `/settings` updates the navigation bar and home dashboard; verify all tests pass with `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

