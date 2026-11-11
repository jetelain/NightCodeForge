# 🖥️ Step 12.18C: Avalonia OOBE Setup Wizard, Post-Install Maintenance Hub & Cross-Platform Packaging

> **Feature Implementation Step (Part 3 of 3 — Avalonia GUI Views, MVVM ViewModels & Single-File Publish)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Steps:* [Step 12.18A: Installer Core Services](STEP_12_18A_INSTALLER_CORE_HARDWARE_RELEASE_AND_PROVISIONER.md) | [Step 12.18B: OllamaRouter Setup Wizard Integration, Role Recommendation Sync & Protocol Shortcut](STEP_12_18B_INSTALLER_OLLAMA_MANAGER_AND_128K_MICRO_BENCHMARK.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Installer`, `tests/NightCodeForge.Installer.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13, Avalonia UI 11+)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

With the core installer services ([Step 12.18A](STEP_12_18A_INSTALLER_CORE_HARDWARE_RELEASE_AND_PROVISIONER.md)) and the delegated OllamaRouter setup handoff ([Step 12.18B](STEP_12_18B_INSTALLER_OLLAMA_MANAGER_AND_128K_MICRO_BENCHMARK.md)) in place, this step (Part 3 of Step 12.18) builds the **Avalonia UI 11+ (`CommunityToolkit.Mvvm`) desktop interface** and cross-platform single-file packaging:
1. **Zero-VRAM Software Rendering:** Configure `Program.BuildAvaloniaApp()` to prefer software rendering (`Win32RenderingMode.Software`, `X11RenderingMode.Software`) so the installer and maintenance hub consume **0 MB of GPU VRAM** during model benchmarking and inference.
2. **Dual-Mode Application Startup:** Automatically detect whether NightCodeForge is already installed on the host; launch the **5-Step OOBE Setup Wizard** on first run, or open the **Post-Install Maintenance & 1-Click Updater Hub** on subsequent runs.
3. **Forge Doctor Health Check UI:** Provide a 1-click self-test verifying WSL2 responsiveness, Docker daemon access, OllamaRouter daemon heartbeat (`GET /api/router/info`), Web UI port (`5270`) availability, and SQLite database connectivity.
4. **Self-Contained Single-File Packaging:** Configure publish profiles (`PublishSingleFile=true`, `SelfContained=true`, `IncludeNativeLibrariesForSelfExtract=true`) for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`.

---

## 2. Dual-Mode UX & Screen Flow (Avalonia UI)

### 2.1 Mode 1: Setup Wizard Mode (OOBE — 5 Steps)
- **Step 1 — Welcome & Prerequisites Plan:** Product introduction and detected host environment summary (`WSL2`, `Docker`, `.NET 10`, `Git`, `OllamaRouter`) with individual toggle options.
- **Step 2 — Host & Subsystem Provisioning:** Visual stepper with real-time log terminal and progress bar (`IEnvironmentProvisioner`), including reboot prompt if WSL2 installation returns `3010`.
- **Step 3 — Software Download & Installation:** Downloads and extracts `NightCodeForge.Web`, `ncf` CLI, and `OllamaRouter` (`ollamarouter-server`, `ollamarouter`, `ollamarouter-app`) binaries via `IReleaseManager`.
- **Step 4 — AI Fleet & Model Setup (Delegated to OllamaRouter Wizard):**
  - Launches OllamaRouter's 4-step Setup Wizard via `IOllamaRouterSetupHandoffService` (`ollamarouter setup --wizard --wait`) and waits for it to finish.
  - On exit code `0`, automatically queries `GET /api/router/recommendations` via `IOllamaRouterRecommendationSyncService`, displays the imported role-to-model assignments (`Architect / Reviewer`, `Coder / Implementation`, `Agentic Chat`, plus per-node context ceilings), and writes NightCodeForge's `config/config.json` user overlay.
  - Provides **"Re-run OllamaRouter Wizard"** and **"Skip (Configure Cloud / ACP Later)"** options.
- **Step 5 — Ready to Forge:** Summary screen with desktop/menu shortcut creation and **"Launch NightCodeForge Web"** button.

### 2.2 Mode 2: Maintenance & Updater Hub Mode (Post-Install)
- **Application Status Card:** Current installed version vs. latest GitHub Release, server running status (Running on port 5270 / Stopped), and quick actions (**Start Server**, **Stop Server**, **Open Web UI in Browser**, **Open CLI Terminal**).
- **1-Click Update:** **"Update to vX.Y.Z"** button executing atomic binary updates via `IReleaseManager` while preserving `config/config.json` and `~/.nightcodeforge/`.
- **OllamaRouter & AI Fleet Card:** Quick actions to **Open OllamaRouter App** (`ollamarouter-app` / `ollamarouter://open`), **Run OllamaRouter Setup Wizard** (`ollamarouter setup --wizard --wait` + recommendation sync), and **Sync Role Recommendations** from `/api/router/recommendations`.
- **Forge Doctor (System Diagnostics):** 1-click self-test card displaying pass/fail badges for WSL2, Docker daemon, OllamaRouter endpoint, port 5270, and database health.

---

## 3. Implementation Checklist & Deliverables

- [ ] Configure Avalonia UI 11+ and `CommunityToolkit.Mvvm` in `NightCodeForge.Installer.csproj` with software rendering (`Win32RenderingMode.Software`, `X11RenderingMode.Software`) and NightCodeForge dark theme styling.
- [ ] Implement OOBE Wizard ViewModels and Views (Steps 1–5) backed by `IEnvironmentProvisioner`, `IReleaseManager`, `IOllamaRouterSetupHandoffService`, and `IOllamaRouterRecommendationSyncService`.
- [ ] Implement Maintenance Hub ViewModel and View (Status Card, 1-Click Updater, OllamaRouter & AI Fleet Card, and Forge Doctor diagnostics).
- [ ] Configure self-contained single-file publish profiles for Windows, Linux, and macOS RIDs.
- [ ] Add ViewModel unit tests in `NightCodeForge.Installer.Tests` verifying wizard step navigation, delegated OllamaRouter wizard handoff & recommendation sync, OOBE vs. Maintenance Hub startup routing, 1-click update workflow, and Forge Doctor health check aggregation.
