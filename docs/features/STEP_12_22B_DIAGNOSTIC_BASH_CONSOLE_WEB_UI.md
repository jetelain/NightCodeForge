# 🖥️ Step 12.22B: Interactive Container Diagnostic Bash Console Web UI (`/diagnostics/shell`)

> **Feature Implementation Step (Part 2 of 2 — `/diagnostics/shell` Blazor Page, Presets & Live Streaming Terminal)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.22A: Container Diagnostic Shell Service & `SIGINT` Recovery](STEP_12_22A_DIAGNOSTIC_SHELL_SERVICE_AND_CONTAINER_SIGINT_RECOVERY.md)  
> *Related Steps:* [Step 11.18: Live Operations Telemetry](../archive/phase-11/STEP_11_18_LIVE_OPERATIONS_TELEMETRY_LLM_SHELL_AND_ROUTER.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Building on `IEnvironmentDiagnosticShellService` ([Step 12.22A](STEP_12_22A_DIAGNOSTIC_SHELL_SERVICE_AND_CONTAINER_SIGINT_RECOVERY.md)), this step (Part 2 of Step 12.22) delivers the interactive `/diagnostics/shell` Blazor page (`EnvironmentDiagnosticShell.razor`):
1. **Target Context Selector:** Switch between Workspace Canonical scope (`TaskId = null`) and Task Worktree scope (`TaskId = N`), `Standard` vs. `RestrictedReadOnly` security tier, and `Agent 'bash' Tool` vs. `Raw IExecutionEnvironment` pipeline.
2. **Environment & Volume Mounts Inspector Card:** Display live badges (`ExecutionTier`, `ContainerName`, `ContainerImage`, `Running/Stopped`, `UID:GID`, `WSL2 Distro`), container drift warnings, expandable `ConfiguredMounts` table (`HostPath -> ContainerPath`, `RO`/`RW`), synthesized `PATH`, and **🔄 Refresh Inspection**, **🩺 Run Environment Doctor**, and **♻️ Recreate Container** actions.
3. **One-Click Diagnostic Presets & Doctor Report Card:** Provide 1-click preset buttons (`Identity & Directories`, `Environment & PATH`, `SDKs & Companion Tools`, `Mount & Write Probe`, `Git & Worktree Status`, `.NET & ICU Probe`) and render `EnvironmentDoctorCheckItem` cards.
4. **Interactive Terminal & Live Streaming Output:** Monospace command input with command history (`Up`/`Down`), **▶ Execute** and **⏹ Stop / SIGINT** buttons, real-time line-by-line output streaming via `IOperationsTelemetryService`, and chronological session transcript log with exit code and duration badges.

---

## 2. Blazor Page Specifications (`EnvironmentDiagnosticShell.razor`)

Routes:
- `@page "/diagnostics/shell"`
- `@page "/w/{WorkspaceId:int}/diagnostics/shell"`

Linked in [`NavMenu.razor`](../../src/NightCodeForge.Web/Components/Layout/NavMenu.razor) under `System` as **🐚 Container Shell** via `href="@WorkspaceState.GetNavHref("diagnostics/shell")"`. When navigated via `/w/{WorkspaceId:int}/diagnostics/shell`, the page activates the route's `WorkspaceId` in `WorkspaceStateService` and syncs the Target Context Selector accordingly.

### 2.1 One-Click Preset Commands

| Preset Name | Command | Purpose |
| :--- | :--- | :--- |
| **Identity & Directories** | `id && pwd && echo "HOME=$HOME" && ls -la` | Verifies UID/GID mapping, working directory, `$HOME` fallback, and file ownership. |
| **Environment & PATH** | `echo "PATH=$PATH" && env \| sort` | Inspects deduplicated `PATH`, locale (`LANG`, `LC_ALL`), ICU, and cache env vars. |
| **SDKs & Companion Tools** | `for cmd in git rg dotnet node npm python3 csnav mdfetch codecondenser; do printf "%-15s: %s\n" "$cmd" "$(which $cmd 2>/dev/null \|\| echo MISSING)"; done` | Checks availability of all core toolchains and Phase 12 companion tools on `PATH`. |
| **Mount & Write Probe** | `df -h && for dir in /workspace /workspace/clone "$HOME" /tmp /cache/nuget /cache/npm /opt/ncf-dotnet-tools /workspace/skills; do [ -e "$dir" ] && ([ -w "$dir" ] && echo "RW: $dir" \|\| echo "RO: $dir") \|\| echo "MISSING: $dir"; done` | Verifies which mounted directories exist and whether they are writable (`RW`) or read-only (`RO`). |
| **Git & Worktree Status** | `git status && git rev-parse --git-dir --git-common-dir && git branch -a && git worktree list` | Diagnoses Git repository validity, relative worktree pointers, and `safe.directory`. |
| **.NET & ICU Probe** | `dotnet --info` | Verifies mounted host `.NET` SDKs, runtime packs, and `libicu` globalization readiness. |

### 2.2 Presentation Separation & Live Streaming Guarantee
In accordance with [AGENTS.md](../../AGENTS.md), `EnvironmentDiagnosticShell.razor` remains focused on layout and user interaction, delegating environment inspection, command execution, doctor checks, and container recreation to `IEnvironmentDiagnosticShellService` and subscribing to `IOperationsTelemetryService` (`OnShellCommandChanged` / `OnShellCommandOutput`) for real-time line-by-line output streaming.

---

## 3. Definition of Done (DoD) Checklist

- [ ] Create `src/NightCodeForge.Web/Components/Pages/EnvironmentDiagnosticShell.razor` (`@page "/diagnostics/shell"` and `@page "/w/{WorkspaceId:int}/diagnostics/shell"`) and add `🐚 Container Shell` (`WorkspaceState.GetNavHref("diagnostics/shell")`) to `NavMenu.razor`.
- [ ] Implement target scope selector, Environment & Mounts Inspector card, 1-click preset buttons, and Environment Doctor report view.
- [ ] Implement interactive bash console with live output streaming via `IOperationsTelemetryService` shell events, command history, exit code badges, and **⏹ Stop / SIGINT** cancellation.
- [ ] Add bUnit component tests in `NightCodeForge.Web.Tests` verifying workspace-scoped routing, preset execution, inspection rendering, doctor results display, live streaming output, command execution, and cancellation UI states.

