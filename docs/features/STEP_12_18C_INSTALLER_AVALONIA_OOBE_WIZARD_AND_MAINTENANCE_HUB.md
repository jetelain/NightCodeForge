# 🖥️ Step 12.18C: Avalonia OOBE Setup Wizard, Maintenance Hub, Interactive Uninstaller & GitHub Actions Release

> **Feature Implementation Step (Part 3 of 3 — Avalonia GUI Views, MVVM ViewModels, Native AOT Packaging & CI/Release Pipeline)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Steps:* [Step 12.18A: Installer Core Services, NuGet Tooling, Air-Gapped Bundle & Uninstaller](STEP_12_18A_INSTALLER_CORE_HARDWARE_RELEASE_AND_PROVISIONER.md) | [Step 12.18B: OllamaRouter Setup Wizard Integration, Role Recommendation Sync & Protocol Shortcut](STEP_12_18B_INSTALLER_OLLAMA_MANAGER_AND_128K_MICRO_BENCHMARK.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Installer`, `tests/NightCodeForge.Installer.Tests`, `.github/workflows/ci.yml` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13, Avalonia UI 12.1.6, Semi.Avalonia 12.1.0.1, Native AOT)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

With the core installer services ([Step 12.18A](STEP_12_18A_INSTALLER_CORE_HARDWARE_RELEASE_AND_PROVISIONER.md)) and the delegated OllamaRouter setup handoff ([Step 12.18B](STEP_12_18B_INSTALLER_OLLAMA_MANAGER_AND_128K_MICRO_BENCHMARK.md)) in place, this step (Part 3 of Step 12.18) builds the **Avalonia UI 12.1.6 + `Semi.Avalonia` (`CommunityToolkit.Mvvm`) desktop interface**, Native AOT packaging, and GitHub Actions release automation:
1. **Zero-VRAM Pure Software Rendering:** Configure `Program.BuildAvaloniaApp()` for strict software rendering (`Win32RenderingMode.Software`, `X11RenderingMode.Software`) so the installer and maintenance hub allocate **0 MB of GPU VRAM** during model benchmarking and inference.
2. **NightCodeForge Theme Reuse (`NightCodeAssay`):** Reuse `NightCodeForgeTheme.axaml` from [`NightCodeAssay.App`](file:///C:/Users/Julien/source/repos/jetelain/NightCodeAssay/src/NightCodeAssay.App/Themes/NightCodeForgeTheme.axaml) (`Semi.Avalonia` `12.1.0.1` + obsidian/indigo semantic token overrides and `RequestedThemeVariant="Dark"`) for complete visual consistency across `NightCodeForge.Web`, `NightCodeAssay`, and `NightCodeForge.Installer`.
3. **3-Mode Application Startup & CLI Routing:**
   - **CLI / Headless Mode:** Handles `uninstall --quiet [--keep-data] [--remove-tools] [--remove-ollamarouter]` and `--bundle <dir>` / `--air-gapped` source overrides without launching a window.
   - **Mode 1 — OOBE Setup Wizard (5 Steps):** First-run setup supporting both **Online** and **Air-Gapped Local Bundle** sources.
   - **Mode 2 — Maintenance & Updater Hub (Post-Install):** Status, 1-click updater (Online or Air-Gapped), Companion NuGet tools panel (`ncf`, `nightcodeassay`, `csnav`, `mdfetch`, `codecondenser`), OllamaRouter handoff, Forge Doctor diagnostics, and Uninstall trigger.
   - **Mode 3 — Interactive Uninstaller View:** Launched via `nightcodeforge-installer uninstall` (e.g., from Windows "Installed Apps" or Maintenance Hub).
4. **Native AOT Packaging & GitHub Actions CI/Release:** Compile `NightCodeForge.Installer` as pure **Native AOT** (`PublishAot=true`, `AvaloniaUseCompiledBindingsByDefault=true`, `StripSymbols=true`, no `Newtonsoft.Json`), and add `.github/workflows/ci.yml` to build, test, pack & publish `NightCodeForge.Cli` to NuGet.org, and publish multi-platform `NightCodeForge-Web-{rid}.zip` and `NightCodeForge-Setup-{rid}.zip` GitHub Releases.

---

## 2. UI Framework, Theme & Native AOT Configuration

### 2.1 Package Versions & Theme Integration
Align package references in `src/NightCodeForge.Installer/NightCodeForge.Installer.csproj` with `NightCodeAssay.App` and `OllamaRouter.App`:
- `Avalonia` `12.1.6`
- `Avalonia.Desktop` `12.1.6`
- `Avalonia.Themes.Fluent` `12.1.6`
- `Avalonia.Fonts.Inter` `12.1.6`
- `Semi.Avalonia` `12.1.0.1`
- `CommunityToolkit.Mvvm` `8.4.2`
- `Tmds.DBus.Protocol` `0.95.2` *(explicit transitive pin overriding `0.21.2` from `Avalonia.Desktop` to resolve `NU1903` / `GHSA-xrw6-gwf8-vvr9` under `TreatWarningsAsErrors=true`, matching `OllamaRouter.App.csproj`)*

Copy [`NightCodeForgeTheme.axaml`](file:///C:/Users/Julien/source/repos/jetelain/NightCodeAssay/src/NightCodeAssay.App/Themes/NightCodeForgeTheme.axaml) into `src/NightCodeForge.Installer/Themes/NightCodeForgeTheme.axaml` and configure `App.axaml`:
```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:semi="https://irihi.tech/semi"
             x:Class="NightCodeForge.Installer.App"
             RequestedThemeVariant="Dark">
  <Application.Resources>
    <ResourceDictionary>
      <ResourceDictionary.MergedDictionaries>
        <ResourceInclude Source="avares://NightCodeForge.Installer/Themes/NightCodeForgeTheme.axaml" />
      </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
  </Application.Resources>
  <Application.Styles>
    <semi:SemiTheme Locale="en-US" />
  </Application.Styles>
</Application>
```

### 2.2 Pure Software Rendering & Native AOT Project Properties
- **Project Properties (`NightCodeForge.Installer.csproj`):**
  - `<OutputType>WinExe</OutputType>`
  - `<PublishAot>true</PublishAot>`
  - `<IsAotCompatible>true</IsAotCompatible>`
  - `<ServerGarbageCollection>false</ServerGarbageCollection>`
  - `<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>`
  - `<StripSymbols>true</StripSymbols>`, `<DebugType>none</DebugType>`, `<DebugSymbols>false</DebugSymbols>`
  - `<PackageReference Remove="Newtonsoft.Json" />` (replaces `PublishSingleFile`/`IncludeNativeLibrariesForSelfExtract` with true Native AOT compilation)
- **`Program.BuildAvaloniaApp()`:**
  ```csharp
  public static AppBuilder BuildAvaloniaApp() =>
      AppBuilder.Configure<App>()
          .UsePlatformDetect()
          .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
          .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] });
  ```

---

## 3. Application Startup Modes & Screen Flow

### 3.1 CLI Argument Parsing & Startup Routing (`Program.cs`)
- Parse command-line arguments before initializing Avalonia:
  - `uninstall` / `--uninstall`: Enters Uninstaller mode. When `--quiet` is supplied, runs `IUninstallService.UninstallAsync(...)` headlessly without creating an Avalonia window and returns exit code `0` or `1`; otherwise launches **Mode 3 (Interactive Uninstaller View)**. Supports `--keep-data`, `--remove-tools`, `--remove-ollamarouter`, and `--quiet`.
  - `upgrade` / `--upgrade`: Enters Maintenance & Updater Hub focused on upgrading the detected installation (or invokes headless upgrade if `--quiet` is added).
  - `--wizard`: Forces **Mode 1 (5-step OOBE Setup Wizard)** in Upgrade/Repair mode even when NightCodeForge is already installed on the host.
  - `--bundle <dir>` / `--air-gapped`: Forces/selects `InstallSourceMode.AirGappedBundle` pointing to `<dir>` (or `<InstallerBaseDir>/bundle/`).
  - Default startup (no `uninstall` verb): Uses `IInstallationDetector` to check whether NightCodeForge is already installed (including legacy `publish.ps1` installations at `%LOCALAPPDATA%\NightCodeForge` / `~/.nightcodeforge`) — launches **Mode 1 (OOBE Setup Wizard)** on first run or **Mode 2 (Maintenance & Updater Hub)** pre-populated with the detected installation path, installed version, and available Online/Bundle upgrade version.

### 3.2 Mode 1: Setup Wizard Mode (OOBE — 5 Steps)
- **Step 1 — Welcome, Install Source & Prerequisites Plan:**
  - Product introduction and detected host environment summary (`WSL2`, `Docker`, `.NET 10 SDK`, `Git`, `OllamaRouter`, companion NuGet tools) with individual toggle options.
  - **Installation Source Selector:** Radio/toggle between **Online (GitHub Releases & NuGet.org)** and **Air-Gapped / Local Bundle (`bundle/`)** — auto-selected when a local `bundle/` folder is detected next to the installer, with a folder picker (`IStorageProvider`) to browse for any local bundle directory and display detected `.zip`/`.tar.gz`/`.nupkg` assets.
- **Step 2 — Host & Subsystem Provisioning:**
  - Visual stepper with real-time log terminal and progress bar (`IEnvironmentProvisioner`), including reboot prompt if WSL2 installation returns `3010`.
- **Step 3 — Software, Companion NuGet Tools & OS Registration:**
  - Installs `NightCodeForge-Web-{rid}.zip` (including Native AOT `ncf-web` / `nightcodeforge-web` launcher, `appsettings` configuration, and `skills/`) and `OllamaRouter-{rid}.zip` / `.tar.gz` (including `appsettings.Production.json`, `bin/` shims, User `PATH`, and `ollamarouter://` protocol registration) via `IReleaseManager` (from Online GitHub Releases or Air-Gapped Local Bundle).
  - Pre-installs all 5 NuGet global tools (`NightCodeForge.Cli` / `ncf`, `NightCodeForge.Assay` / `nightcodeassay`, `Pmad.CSharpNav` / `csnav`, `Pmad.MarkdownFetch` / `mdfetch`, `Pmad.CodeCondenser` / `codecondenser`) on Host and WSL2 via `ICompanionNuGetProvisioner` (using `--add-source <bundleDir> --no-cache` when in Air-Gapped mode).
  - Copies installer binary into `<InstallDir>` and registers OS uninstall entry & shortcuts via `IOsIntegrationService`.
- **Step 4 — AI Fleet & Model Setup (Delegated to OllamaRouter Wizard):**
  - Launches OllamaRouter's 5-step Setup Wizard via `IOllamaRouterSetupHandoffService` (`ollamarouter setup --wizard --wait`) and waits for it to finish.
  - On exit code `0`, automatically queries `GET /api/router/recommendations` via `IOllamaRouterRecommendationSyncService`, displays the imported role-to-model assignments (`Architect / Reviewer`, `Coder / Implementation`, `Agentic Chat`, plus per-node context ceilings), and writes NightCodeForge's `config/config.json` user overlay.
  - Provides **"Re-run OllamaRouter Wizard"** and **"Skip (Configure Cloud / ACP Later)"** options.
- **Step 5 — Ready to Forge:**
  - Summary screen with desktop/Start Menu shortcut toggles, **"Launch NightCodeForge Web"** button, and **"Launch NightCode Assay"** (`nightcodeassay`) button.

### 3.3 Mode 2: Maintenance & Updater Hub Mode (Post-Install & In-Place Upgrades)
- **Application Status Card:** Detected installation directory (including legacy `publish.ps1` installations), current installed version vs. available version (Online GitHub Release or detected local `bundle/`), server running status (Running on port 5270 / Stopped), and quick actions (**Start Server**, **Stop Server**, **Open Web UI in Browser**, **Open CLI Terminal**).
- **1-Click In-Place Upgrade (Online & Air-Gapped):**
  - **"⬆️ Upgrade All Components"** (when a newer Online/Bundle release is available) or **"🔄 Reinstall / Upgrade from Local Bundle"** (always enabled when a local `bundle/` is present, including same-version local debug rebuilds).
  - Atomically upgrades NightCodeForge Web + Native AOT `ncf-web`, OllamaRouter, and all 5 Companion NuGet Tools via `IReleaseManager` and `ICompanionNuGetProvisioner`, updates the installed `NightCodeForge.Installer` copy and OS Uninstall entry, preserves `config/`, `data/`, `logs/`, `state/`, and `~/.nightcodeforge/`, and restarts daemons that were running before the upgrade.
  - Provides a **"🧙 Re-run Full Setup Wizard (Upgrade Mode)"** action to re-verify/repair WSL2, Docker, or OllamaRouter setup during an upgrade.
- **Companion Tools Card:** Displays installation status and versions for `ncf` (`NightCodeForge.Cli`), `nightcodeassay` (`NightCodeForge.Assay`), `csnav` (`Pmad.CSharpNav`), `mdfetch` (`Pmad.MarkdownFetch`), and `codecondenser` (`Pmad.CodeCondenser`), with **Reinstall / Update All Tools** and **Launch NightCode Assay** actions.
- **OllamaRouter & AI Fleet Card:** Quick actions to **Open OllamaRouter App** (`ollamarouter-app` / `ollamarouter://open`), **Run OllamaRouter Setup Wizard** (`ollamarouter setup --wizard --wait` + recommendation sync), and **Sync Role Recommendations** from `/api/router/recommendations`.
- **Forge Doctor (System Diagnostics):** 1-click self-test card displaying pass/fail badges for WSL2, Docker daemon, OllamaRouter endpoint, port 5270, companion global tools, and database health.
- **Uninstall Action:** **"🗑️ Uninstall NightCodeForge..."** button transitioning to Mode 3 (Interactive Uninstaller View).

### 3.4 Mode 3: Interactive Uninstaller View (`nightcodeforge-installer uninstall`)
- Triggered when launching from Windows "Installed Apps" / "Add or Remove Programs" or clicking Uninstall in the Maintenance Hub.
- Displays:
  - Installation path and installed version summary.
  - Checkbox: *"Keep user databases, logs, and configuration (`data/`, `config/`, `logs/`)"* (default: **checked**).
  - Checkbox: *"Also uninstall companion .NET global tools (`ncf`, `nightcodeassay`, `csnav`, `mdfetch`, `codecondenser`)"* (default: **unchecked**).
  - Checkbox: *"Also uninstall OllamaRouter (`ollamarouter-server`, `ollamarouter`, `ollamarouter-app`, and `ollamarouter://` protocol)"* (default: **unchecked**).
  - Real-time uninstallation progress log and **Uninstall** / **Cancel** / **Close** controls.

---

## 4. GitHub Actions CI & Release Workflow (`.github/workflows/ci.yml`)

Create `.github/workflows/ci.yml` automating build, tests, NuGet tool packaging, Native AOT cross-platform publishing, and GitHub Releases:

1. **Triggers & Permissions:**
   - `on: push` (`branches: [main]`, `tags: ['v*.*.*']`) and `pull_request` (`branches: [main]`).
   - `permissions: contents: write, id-token: write` (required for `softprops/action-gh-release@v2` and `NuGet/login@v1` OIDC Trusted Publishing).
2. **Version Extraction:**
   - On `refs/tags/v*`: `VERSION="${GITHUB_REF_NAME#v}"`.
   - On branch/PR builds: `VERSION="0.1.0-preview.${GITHUB_RUN_NUMBER}"`.
3. **`build-and-test` Job (`windows-latest`):**
   - Setup .NET 10 SDK (`actions/setup-dotnet@v4`, `dotnet-version: '10.0.x'`).
   - `dotnet restore NightCodeForge.slnx`
   - `dotnet build NightCodeForge.slnx -c Release --no-restore -p:Version=${{ env.VERSION }}`
   - `dotnet test NightCodeForge.slnx -c Release --no-build --filter "Category!=Acceptance&Category!=E2E"`
   - `dotnet pack src/NightCodeForge.Cli/NightCodeForge.Cli.csproj -c Release --no-build -p:Version=${{ env.VERSION }} -o ./artifacts/nuget`
   - Upload `nuget-package` artifact (`./artifacts/nuget/*.nupkg`).
4. **`publish` Matrix Job:**
   - Target RID matrix: `win-x64` (`windows-latest`), `win-arm64` (`windows-latest`), `linux-x64` (`ubuntu-latest`), `linux-arm64` (`ubuntu-24.04-arm`), `osx-x64` (`macos-15-intel`), `osx-arm64` (`macos-latest`). *(Pin `osx-x64` strictly to `macos-15-intel` and `osx-arm64` strictly to `macos-latest` because Native AOT does not cross-compile architectures on macOS with default linker flags.)*
   - On Linux runners, install Native AOT + Avalonia X11 dependencies: `clang lldb zlib1g-dev libicu-dev libssl-dev libx11-dev libx11-xcb-dev libxcb1-dev libxcb-dri3-dev libfontconfig1-dev`.
   - Publish **Web Bundle (`NightCodeForge-Web-${{ matrix.rid }}.zip`)**:
     - `dotnet publish src/NightCodeForge.Web/NightCodeForge.Web.csproj -c Release -r ${{ matrix.rid }} --self-contained true -p:DebugType=none -p:Version=${{ env.VERSION }}`
     - `dotnet publish src/NightCodeForge.Launcher/NightCodeForge.Launcher.csproj -c Release -r ${{ matrix.rid }} -p:PublishAot=true -p:DebugType=none -p:Version=${{ env.VERSION }}` (places `ncf-web` / `ncf-web.exe` at the archive root for `IReleaseManager` fan-out).
     - Include `skills/`, `VERSION`, `README.md`, `LICENSE`.
   - Publish **Native AOT Installer Bundle (`NightCodeForge-Setup-${{ matrix.rid }}.zip`)**:
     - `dotnet publish src/NightCodeForge.Installer/NightCodeForge.Installer.csproj -c Release -r ${{ matrix.rid }} -p:PublishAot=true -p:StripSymbols=true -p:DebugType=none -p:DebugSymbols=false -p:Version=${{ env.VERSION }}`
     - Include `README.md`, `LICENSE`.
   - Generate `SHA256SUMS-${{ matrix.rid }}.txt` and upload artifacts.
5. **`release` Job (Tag Builds Only: `startsWith(github.ref, 'refs/tags/v')`):**
   - Download `nuget-package` and all `publish` artifacts; merge into a unified `SHA256SUMS.txt`.
   - Authenticate via `NuGet/login@v1` (`user: ${{ secrets.NUGET_USER || 'jetelain' }}`) and push `NightCodeForge.Cli.*.nupkg` with `dotnet nuget push ... --source https://api.nuget.org/v3/index.json --skip-duplicate`.
   - Publish GitHub Release via `softprops/action-gh-release@v2` (`generate_release_notes: true`) attaching `NightCodeForge-Web-*.zip`, `NightCodeForge-Setup-*.zip`, `NightCodeForge.Cli.*.nupkg`, and `SHA256SUMS.txt`.

---

## 5. Implementation Checklist & Deliverables

- [ ] Configure `NightCodeForge.Installer.csproj` with Avalonia `12.1.6`, `Semi.Avalonia` `12.1.0.1`, `CommunityToolkit.Mvvm` `8.4.2`, `Tmds.DBus.Protocol` `0.95.2` (pinning transitive `NU1903` fix), Native AOT properties (`PublishAot=true`, `IsAotCompatible=true`, `AvaloniaUseCompiledBindingsByDefault=true`, `PackageReference Remove="Newtonsoft.Json"`), and pure software rendering (`Win32RenderingMode.Software`, `X11RenderingMode.Software`).
- [ ] Copy and apply [`NightCodeForgeTheme.axaml`](file:///C:/Users/Julien/source/repos/jetelain/NightCodeAssay/src/NightCodeAssay.App/Themes/NightCodeForgeTheme.axaml) from `NightCodeAssay.App` in `App.axaml` (`SemiTheme`, `RequestedThemeVariant="Dark"`).
- [ ] Implement CLI argument routing (`uninstall`, `upgrade`, `--wizard`, `--quiet`, `--keep-data`, `--remove-tools`, `--remove-ollamarouter`, `--bundle <dir>`, `--air-gapped`) and 3-mode UI startup (OOBE Wizard, Maintenance Hub with existing/`publish.ps1` install detection, Interactive Uninstaller).
- [ ] Implement OOBE Wizard ViewModels and Views (Steps 1–5) backed by `IEnvironmentProvisioner`, `IInstallationDetector`, `IReleaseManager`, `ICompanionNuGetProvisioner`, `IOsIntegrationService`, `IOllamaRouterSetupHandoffService`, and `IOllamaRouterRecommendationSyncService` (including Online vs. Air-Gapped Local Bundle selection and Upgrade/Repair wizard mode).
- [ ] Implement Maintenance Hub ViewModel and View (Status Card, 1-Click All-Component Online/Bundle Upgrader, Re-run Setup Wizard trigger, Companion Tools Card, OllamaRouter & AI Fleet Card, Forge Doctor diagnostics, Uninstall trigger) and Interactive Uninstaller ViewModel and View.
- [ ] Configure Native AOT publish profiles under `src/NightCodeForge.Installer/Properties/PublishProfiles/` (`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`).
- [ ] Create `.github/workflows/ci.yml` with build/test, `NightCodeForge.Cli` NuGet packing/pushing, cross-platform Web + Native AOT Installer matrix publishing, `SHA256SUMS.txt` generation, and GitHub Release creation.
- [ ] Add ViewModel unit tests in `NightCodeForge.Installer.Tests` verifying CLI startup routing (including `upgrade` & `--wizard`), legacy `publish.ps1` installation detection/adoption, wizard step navigation, Air-Gapped vs. Online source switching, companion NuGet tool status/install/upgrade triggers, delegated OllamaRouter wizard handoff & recommendation sync, Maintenance Hub 1-click all-component upgrade, Forge Doctor aggregation, and Interactive Uninstaller workflow.
