# 🖥️ Step 12.18A: Installer Core Services — Environment Provisioner, NuGet Tooling, Air-Gapped Bundle & Uninstaller

> **Feature Implementation Step (Part 1 of 3 — Installer Core, Host Provisioning, Offline Bundles & OS Uninstall Integration)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Steps:* [Step 12.18B: OllamaRouter Setup Wizard Integration, Role Recommendation Sync & Protocol Shortcut](STEP_12_18B_INSTALLER_OLLAMA_MANAGER_AND_128K_MICRO_BENCHMARK.md) | [Step 12.18C: Avalonia OOBE Setup Wizard, Maintenance Hub & GitHub Actions Release](STEP_12_18C_INSTALLER_AVALONIA_OOBE_WIZARD_AND_MAINTENANCE_HUB.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Installer`, `src/NightCodeForge.Cli`, `tests/NightCodeForge.Installer.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13, Native AOT)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

To eliminate the friction of script-based bootstrapping (`install.ps1`), this step (Part 1 of Step 12.18) scaffolds `NightCodeForge.Installer` (compiled as **Native AOT**) and implements the core cross-platform provisioning, package/release delivery, offline bundle, and uninstallation services:
1. **Subsystem & Environment Provisioning (`IEnvironmentProvisioner`):** Detect and provision WSL2 (on Windows with JIT UAC elevation and reboot detection `3010`), Ubuntu distribution, .NET 10 SDK, Docker Engine, and system toolchain dependencies (`ripgrep`, `git`, `libicu-dev`, `curl`, `ca-certificates`, `locales`).
2. **Binary Release Delivery & Atomic Updater (`IReleaseManager`):** Resolve `NightCodeForge-Web-{rid}.zip` and `OllamaRouter-{rid}.zip` / `.tar.gz` either online from GitHub Releases (`jetelain/NightCodeForge` and `jetelain/OllamaRouter`) or offline from an **Air-Gapped Local Bundle**, verify SHA-256 checksums, perform atomic binary swaps with `.backup` rollback, configure `appsettings.json` / `appsettings.Production.json`, deploy the Native AOT `ncf-web` launcher and `skills/`, and preserve user configuration (`config/config.json`, `config/default-key.dat`, `~/.nightcodeforge/`, and OllamaRouter `config/` & `state/`).
3. **NuGet Global Tool Pre-Installation (`ICompanionNuGetProvisioner`):** Distribute `NightCodeForge.Cli` (`ncf`) as a NuGet Global Tool (`NightCodeForge.Cli`) and pre-install all companion NuGet tools (`NightCodeForge.Cli`, `NightCodeForge.Assay`, `Pmad.CSharpNav`, `Pmad.MarkdownFetch`, `Pmad.CodeCondenser`) on the host and inside WSL2 (supporting both NuGet.org online and Air-Gapped `--add-source <bundleDir> --no-cache`).
4. **OS Uninstall & Desktop Integration (`IOsIntegrationService` & `IUninstallService`):** Provide a dedicated `uninstall` command integrated with the host OS uninstall mechanism (Windows `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\NightCodeForge`, Linux XDG `.desktop` entries, macOS app wrapper) and desktop/menu shortcuts.

*(Hardware GPU/VRAM discovery, local Ollama `:11435` port migration, candidate model micro-benchmarking, and role-to-model recommendations are delegated to **OllamaRouter's Setup Wizard** as specified in [Step 12.18B](STEP_12_18B_INSTALLER_OLLAMA_MANAGER_AND_128K_MICRO_BENCHMARK.md); the 0-VRAM software-rendered Avalonia OOBE Wizard, Maintenance Hub, Interactive Uninstaller UI, and GitHub Actions CI/Release pipeline are specified in [Step 12.18C](STEP_12_18C_INSTALLER_AVALONIA_OOBE_WIZARD_AND_MAINTENANCE_HUB.md).)*

---

## 2. Technical Specifications

### 2.1 Project Layout, Native AOT Constraints & Domain Models (`NightCodeForge.Installer`)
- **Project File:** `src/NightCodeForge.Installer/NightCodeForge.Installer.csproj` (`net10.0`)
- **Test Project:** `tests/NightCodeForge.Installer.Tests/NightCodeForge.Installer.Tests.csproj`
- **Native AOT & Trim-Safe Requirements:**
  - Enable `<PublishAot>true</PublishAot>`, `<IsAotCompatible>true</IsAotCompatible>`, and `<ServerGarbageCollection>false</ServerGarbageCollection>`.
  - Exclude the global `Newtonsoft.Json` reference from `Directory.Build.props` via `<PackageReference Remove="Newtonsoft.Json" />` (matching `NightCodeForge.Launcher.csproj`).
  - All JSON serialization/deserialization (GitHub Releases payloads, `config/config.json`, `appsettings.json` / `appsettings.Production.json`, `RouterRecommendationsDto`, `uninstall-manifest.json`) MUST use `System.Text.Json` source generation (`[JsonSerializable]` on `InstallerJsonContext : JsonSerializerContext`) with zero reflection-based serialization.

```csharp
namespace NightCodeForge.Installer.Core.Models;

public enum HostPlatformType { Windows, Linux, MacOS }

public enum InstallSourceMode { Online, AirGappedBundle }

public enum ProvisioningStatus
{
    NotStarted,
    InProgress,
    Completed,
    RebootRequired,
    Failed,
    Skipped
}

public sealed record ComponentReleaseAsset(
    string ComponentName,
    string Version,
    string SourceLocation,
    string? Sha256Checksum,
    long FileSizeBytes,
    bool IsLocalBundle);

public sealed record CompanionNuGetToolSpec(
    string ToolCommand,
    string PackageId,
    bool InstallOnHost,
    bool InstallInWsl,
    string Description);

public sealed record UninstallOptions(
    bool KeepData,
    bool RemoveCompanionTools,
    bool RemoveOllamaRouter,
    bool Quiet);
```

---

### 2.2 Subsystem & Environment Provisioning (`IEnvironmentProvisioner`)
1. **Windows Subsystem for Linux (WSL2):**
   - Runs `wsl --status` and `wsl --list --verbose`.
   - If missing/disabled, triggers JIT elevation (`Start-Process wsl -ArgumentList '--install --no-launch' -Verb RunAs -Wait`), detects exit code `3010` (`ProvisioningStatus.RebootRequired`), and provisions the default `Ubuntu` distribution.
2. **Host & Unix Toolchain & Sandboxing (Windows Host, WSL / Native Linux):**
   - Verifies/installs .NET 10 SDK on Windows host (`winget install Microsoft.DotNet.SDK.10` or `dotnet-install.ps1 -Channel 10.0`) and inside WSL/Linux (`dotnet-sdk-10.0` via `apt` / Microsoft package feed), ensuring `/etc/profile.d/dotnet.sh` exports `DOTNET_ROOT` and `$HOME/.dotnet/tools` on `PATH`.
   - Installs Docker Engine (`docker.io` / `docker-ce`, `systemd=true` in `/etc/wsl.conf`, `usermod -aG docker $USER`) and companion OS packages (`ripgrep`, `git`, `libicu-dev`, `curl`, `ca-certificates`, `locales`, `jq`, `build-essential`).
3. **macOS Host Environment:**
   - Verifies Docker runtime (Docker Desktop / Colima / OrbStack), Git, `ripgrep`, and .NET 10 SDK.

---

### 2.3 `NightCodeForge.Cli` as a NuGet Package & Companion NuGet Pre-Installation (`ICompanionNuGetProvisioner`)

1. **`NightCodeForge.Cli` NuGet Packaging (`src/NightCodeForge.Cli/NightCodeForge.Cli.csproj`):**
   - `NightCodeForge.Cli` (`ncf`) is distributed exclusively as a **NuGet Global Tool package (`NightCodeForge.Cli.*.nupkg`)**, NOT as a `NightCodeForge-Cli-{rid}.zip` GitHub Release binary archive.
   - Ensure `src/NightCodeForge.Cli/NightCodeForge.Cli.csproj` includes complete NuGet package metadata (`PackAsTool=true`, `ToolCommandName=ncf`, `PackageId=NightCodeForge.Cli`, `Authors=Pmad`, `PackageLicenseExpression=MIT`, `PackageProjectUrl=https://github.com/jetelain/NightCodeForge`, `RepositoryUrl=https://github.com/jetelain/NightCodeForge`, `RepositoryType=git`, `PackageReadmeFile=README.md`, `IsPackable=true`) plus `<None Include="..\..\README.md" Pack="true" PackagePath="\" />` (required to prevent `NU5039` during `dotnet pack`), matching `NightCodeAssay.App.csproj`.
2. **Pre-Installed Companion NuGet Global Tools:**
   During initial installation and update flows, `ICompanionNuGetProvisioner` pre-installs (or updates) all 5 companion NuGet global tools via `dotnet tool install -g` / `dotnet tool update -g`:

   | Tool Command | NuGet `PackageId` | Host OS Install | WSL2 Install (Windows Hosts) | Purpose |
   | :--- | :--- | :---: | :---: | :--- |
   | `ncf` | `NightCodeForge.Cli` | Yes | Yes | Deterministic repository preparation, onboarding (`ncf init`), and readiness audit (`ncf audit`). |
   | `nightcodeassay` | `NightCodeForge.Assay` | Yes | No *(Desktop GUI)* | Zero-VRAM Avalonia exploratory testing companion desktop app ([NightCodeAssay](https://github.com/jetelain/NightCodeAssay)). |
   | `csnav` | `Pmad.CSharpNav` | Yes | Yes | C# symbol navigation & BCL/NuGet assembly decompiler for agents. |
   | `mdfetch` | `Pmad.MarkdownFetch` | Yes | Yes | Web documentation to LLM-ready Markdown converter for agents. |
   | `codecondenser` | `Pmad.CodeCondenser` | Yes | Yes | C# structural clone detection & comment condensation CLI for agents. |

3. **Pre-Flight Container Tool Readiness & Upgrades:**
   - Pre-installing `ncf`, `csnav`, `mdfetch`, and `codecondenser` on both the Host and inside the target WSL2 distro (on Windows) guarantees `~/.dotnet/tools` exists and contains ELF binaries before any Docker container mounts `/tmp/ncf-home/.dotnet/tools:ro`.
   - `ICompanionNuGetProvisioner` probes installed tools via `dotnet tool list -g` (and `wsl -d <distro> -- dotnet tool list -g` on Windows), installs missing tools or upgrades existing ones, and handles `"is already installed"` cleanly:
     - **Online Upgrade:** Runs `dotnet tool update -g <PackageId>` for already-installed tools (or `dotnet tool install -g <PackageId>` for newly added tools such as `NightCodeForge.Assay`).
     - **Air-Gapped Bundle Upgrade (including same-version debug rebuilds):** Clears `~/.nuget/packages/<packageid-lowercase>` (on Host and inside WSL2), then executes `dotnet tool update -g <PackageId> --add-source "<bundleDir>" --no-cache` — falling back to `dotnet tool uninstall -g <PackageId>` followed by `dotnet tool install -g <PackageId> --add-source "<bundleDir>" --no-cache` when reinstalling an identical package version during local debugging.

---

### 2.4 Binary Release Delivery, Air-Gapped Local Bundle Mode & Post-Extract Setup (`IReleaseManager`)

1. **Dual Install Source Modes (`Online` vs. `AirGappedBundle`):**
   - **Auto-Detection & Overrides:**
     - Automatically defaults to `InstallSourceMode.AirGappedBundle` when `<InstallerBaseDir>/bundle/` (or `<InstallerBaseDir>/`) already contains the required `.zip`/`.tar.gz` archives and `.nupkg` packages.
     - Explicitly configurable via CLI flags (`--bundle <path>`, `--air-gapped`) or the Setup Wizard / Maintenance Hub UI source selector.
   - **Online Mode (GitHub Releases & NuGet.org):**
     - Queries `https://api.github.com/repos/jetelain/NightCodeForge/releases/latest` (`NightCodeForge-Web-{rid}.zip` and `SHA256SUMS.txt`) and `https://api.github.com/repos/jetelain/OllamaRouter/releases/latest` (`OllamaRouter-{rid}.zip` on Windows or `OllamaRouter-{rid}.tar.gz` on Linux/macOS, bundling `ollamarouter-server`, `ollamarouter`, and `ollamarouter-app`).
     - Downloads assets with resumable `.part` streaming and SHA-256 verification.
     - Installs NuGet global tools from default NuGet sources (`nuget.org`).
   - **Air-Gapped / Offline Local Bundle Mode:**
     - Designed for air-gapped environments and pre-release local debugging when GitHub Releases or NuGet.org packages are not yet published.
     - Resolves required artifacts directly from `<bundleDir>` without network access:
       - `NightCodeForge-Web-{rid}.zip` (containing published `NightCodeForge.Web`, Native AOT `ncf-web` / `ncf-web.exe` launcher binary from `NightCodeForge.Launcher` at the archive root, `skills/`, `VERSION`, `README.md`, and `LICENSE`).
       - `OllamaRouter-{rid}.zip` (Windows) or `OllamaRouter-{rid}.tar.gz` (Linux/macOS) (containing `ollamarouter-server`, `ollamarouter`, `ollamarouter-app`, `VERSION`, `README.md`, and `LICENSE` at the archive root).
       - `*.nupkg` tool packages (`NightCodeForge.Cli.*.nupkg`, `NightCodeForge.Assay.*.nupkg`, `Pmad.CSharpNav.*.nupkg`, `Pmad.MarkdownFetch.*.nupkg`, `Pmad.CodeCondenser.*.nupkg`).
       - Optional `SHA256SUMS.txt` checksum file (verified when present).
     - `IReleaseManager` supports extracting both `.zip` (`System.IO.Compression.ZipFile`) and `.tar.gz` (`GZipStream` + `System.Formats.Tar.TarFile`) archives.
     - `ICompanionNuGetProvisioner` clears any cached `~/.nuget/packages/<packageid-lowercase>` folder, then executes `dotnet tool install -g <PackageId> --add-source "<bundleDir>" --no-cache` (or `dotnet tool update -g <PackageId> --add-source "<bundleDir>" --no-cache`).
     - For WSL2 on Windows in air-gapped mode, translates `<bundleDir>` via `wslpath -u "<bundleDir>"`, clears `~/.nuget/packages/<packageid-lowercase>` inside WSL, and executes `wsl -d <distro> -- dotnet tool install -g <PackageId> --add-source "<wslBundleDir>" --no-cache` (or `update`).
   - **Preparing an Initial Air-Gapped Debug Bundle (`package.ps1` + NuGet.org Downloads):**
     - **Unreleased Repositories (`package.ps1` scripts):** Repositories without an existing GitHub Release or NuGet release provide a standalone `package.ps1` script supporting `-Configuration` (default `Release`), `-Runtime` (default `win-x64`), `-Version`, and `-OutputDir` (default `./artifacts`):
       - **`NightCodeAssay/package.ps1`** (`C:\Users\Julien\source\repos\jetelain\NightCodeAssay\package.ps1`): Packs the .NET Global Tool NuGet package `NightCodeForge.Assay.<version>.nupkg` (`dotnet pack src/NightCodeAssay.App/NightCodeAssay.App.csproj -c <Configuration> -o <OutputDir>`) and optionally publishes the portable Native AOT archive `NightCodeAssay-{rid}.zip`.
       - **`OllamaRouter/package.ps1`** (`C:\Users\Julien\source\repos\jetelain\OllamaRouter\package.ps1`): Publishes the three Native AOT executables (`src/OllamaRouter/OllamaRouter.csproj` $\rightarrow$ `ollamarouter-server`, `src/OllamaRouter.Cli/OllamaRouter.Cli.csproj` $\rightarrow$ `ollamarouter`, `src/OllamaRouter.App/OllamaRouter.App.csproj` $\rightarrow$ `ollamarouter-app`), includes `VERSION`, `README.md`, and `LICENSE`, and creates `OllamaRouter-{rid}.zip` (Windows) or `OllamaRouter-{rid}.tar.gz` (Linux/macOS) in `<OutputDir>`.
       - **`NightCodeForge/package.ps1`** (`c:\Users\Julien\source\repos\jetelain\NightCodeForge\package.ps1`): Packs `NightCodeForge.Cli.<version>.nupkg` (`ncf`), publishes `NightCodeForge.Web` + Native AOT `NightCodeForge.Launcher` (`ncf-web` / `ncf-web.exe` at archive root) + `skills/`, `VERSION`, `README.md`, and `LICENSE` into `NightCodeForge-Web-{rid}.zip`, and outputs both into `<OutputDir>`.
     - **Already-Released Companion Packages on `nuget.org` (`Pmad.CSharpNav`, `Pmad.MarkdownFetch`, `Pmad.CodeCondenser`):**
       - Do **not** rebuild `PmadCSharpNav`, `MarkdownFetch`, or `PmadCodeCondenser` from source; download their published `.nupkg` files directly from `nuget.org` into `<bundleDir>` via NuGet's v3 flatcontainer API (`https://api.nuget.org/v3-flatcontainer/{id-lower}/index.json` / `{id-lower}.{version}.nupkg`) or v2 package endpoint (`https://www.nuget.org/api/v2/package/{PackageId}`):
         - `Pmad.CSharpNav` (`csnav`, e.g. `0.1.3`)
         - `Pmad.MarkdownFetch` (`mdfetch`, e.g. `0.0.2`)
         - `Pmad.CodeCondenser` (`codecondenser`, e.g. `0.1.0`)
     - **Example PowerShell Assembly Flow (Populating `<bundleDir>`):**
       ```powershell
       $bundleDir = ".\artifacts\installer-bundle\bundle"
       New-Item -ItemType Directory -Force -Path $bundleDir | Out-Null

       # 1. Build unreleased companion & host archives/packages
       & ..\NightCodeAssay\package.ps1 -OutputDir $bundleDir
       & ..\OllamaRouter\package.ps1 -OutputDir $bundleDir
       & .\package.ps1 -OutputDir $bundleDir

       # 2. Download already-released companion NuGet tools from nuget.org
       foreach ($pkg in @("Pmad.CSharpNav", "Pmad.MarkdownFetch", "Pmad.CodeCondenser")) {
           $idLower = $pkg.ToLowerInvariant()
           $index = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$idLower/index.json"
           $latest = $index.versions[-1]
           $url = "https://api.nuget.org/v3-flatcontainer/$idLower/$latest/$idLower.$latest.nupkg"
           Invoke-WebRequest -Uri $url -OutFile (Join-Path $bundleDir "$pkg.$latest.nupkg")
       }
       ```

2. **Standard Installation Paths & Existing Installation Discovery (`IInstallationDetector`):**
   Default installation paths align with existing `publish.ps1` deployments so script-installed environments are detected and upgraded in-place without relocating databases or losing API keys:
   - **NightCodeForge:**
     - **Windows Default:** `%LOCALAPPDATA%\NightCodeForge\` (probing in order: `$NIGHTCODEFORGE_HOME`, `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\NightCodeForge` `InstallLocation`, `%LOCALAPPDATA%\NightCodeForge\`, and `%LOCALAPPDATA%\Programs\NightCodeForge\`).
     - **Linux Default:** `~/.nightcodeforge/` (probing `$NIGHTCODEFORGE_HOME`, `~/.nightcodeforge/`, and `~/.local/share/nightcodeforge/`).
     - **macOS Default:** `~/Applications/NightCodeForge/` (probing `$NIGHTCODEFORGE_HOME`, `~/.nightcodeforge/`, and `~/Applications/NightCodeForge/`).
   - **OllamaRouter:**
     - **Windows:** `%LOCALAPPDATA%\OllamaRouter\` (probing `$OLLAMAROUTER_HOME` and `%LOCALAPPDATA%\OllamaRouter\`, with `%LOCALAPPDATA%\OllamaRouter\bin` added to User `PATH`).
     - **Linux / macOS:** `~/.ollamarouter/` (probing `$OLLAMAROUTER_HOME` and `~/.ollamarouter/`, with `~/.ollamarouter/bin` added to `PATH`).
   - **Installed Version & Legacy `publish.ps1` Detection:**
     - `IInstallationDetector` detects an existing NightCodeForge installation by checking for `NightCodeForge.Web.exe` / `NightCodeForge.Web` / `NightCodeForge.Web.dll`, reading `<InstallDir>/VERSION` (falling back to assembly metadata when `VERSION` is absent), and inspecting existing `config/`, `data/`, and `logs/` folders.
     - Detects an existing OllamaRouter installation by checking for `ollamarouter-server(.exe)` and reading `<OllamaRouterDir>/VERSION`.

3. **Post-Extraction Web & OllamaRouter Configuration, Native AOT Launchers, Protocol Registration & Self-Copy:**
   Mirrors `publish.ps1` and `OllamaRouter/publish.ps1` post-extraction setup inside `IReleaseManager`:
   - **NightCodeForge Web (`<InstallDir>`):**
     - Ensures `<InstallDir>/config`, `<InstallDir>/data`, `<InstallDir>/logs`, `<InstallDir>/bin`, and `<InstallDir>/skills` exist.
     - Adapts `appsettings.json` and `appsettings.Production.json` using source-generated `System.Text.Json`:
       - `ConnectionStrings:DefaultConnection` = `Data Source=<DataDir>/nightcodeforge.db;`
       - `ConnectionStrings:HangfireConnection` = `Data Source=<DataDir>/nightcodeforge_hangfire.db;`
       - `Logging:LogsDirectory` = `<LogsDir>`
       - `Urls` = `http://localhost:5270` (preserving user overrides when already configured)
     - Fans out the archive-root `ncf-web(.exe)` launcher binary into `<InstallDir>/nightcodeforge-web(.exe)`, `<InstallDir>/bin/nightcodeforge-web(.exe)`, and `<InstallDir>/bin/ncf-web(.exe)` (removing the raw archive-root `ncf-web(.exe)` copy and legacy `.cmd` files, writing a Unix bash companion shim `bin/nightcodeforge-web` on Windows for WSL/Git Bash), and idempotently registers `<InstallDir>/bin` in the user `PATH`.
     - Copies the running `NightCodeForge.Installer` Native AOT binary into `<InstallDir>/NightCodeForge.Installer.exe` (or `nightcodeforge-installer` on Linux/macOS) when launched from outside `<InstallDir>` (e.g., `Downloads` or a local bundle folder) so it serves as the permanent Maintenance Hub & Uninstaller binary.
   - **OllamaRouter (`<OllamaRouterDir>`):**
     - On initial installation (as well as upgrades), ensures `<OllamaRouterDir>/config`, `<OllamaRouterDir>/state`, `<OllamaRouterDir>/logs`, and `<OllamaRouterDir>/bin` exist.
     - Configures `OllamaRouter:StateDirectory` = `<StateDir>` and `Logging:LogsDirectory` = `<LogsDir>` (with default `LogLevel` when absent) in `<OllamaRouterDir>/appsettings.Production.json`.
     - Writes launcher shims in `<OllamaRouterDir>/bin/` (`ollamarouter.cmd`, `ollamarouter-app.cmd`, and bash `ollamarouter` on Windows; wrappers/symlinks on Linux/macOS) plus `<OllamaRouterDir>/ollamarouter.cmd` on Windows, and idempotently registers `<OllamaRouterDir>/bin` in User `PATH`.
     - Registers the user-scoped `ollamarouter://` URI protocol handler (`HKCU\Software\Classes\ollamarouter` on Windows, `~/.local/share/applications/ollamarouter.desktop` + `xdg-mime` on Linux) pointing to `<OllamaRouterDir>/ollamarouter-app(.exe)` so `ollamarouter://open` and `ollamarouter://wizard` work from the Web UI even if the user skips OllamaRouter setup during OOBE.
   - **Unix Executable Permissions (Linux / macOS):**
     - Sets executable file mode (`chmod +x` / `File.SetUnixFileMode`) on extracted binaries (`NightCodeForge.Web`, `ncf-web`, `nightcodeforge-web`, `ollamarouter-server`, `ollamarouter`, `ollamarouter-app`, `nightcodeforge-installer`) and bash wrapper scripts.

4. **In-Place Upgrade Workflow, Atomic Binary Swaps & Rollback:**
   Upgrading an existing installation (including legacy `publish.ps1` installations or prior installer-managed installations) executes an atomic in-place upgrade:
   - **Pre-Upgrade Process Shutdown & Lock Release:**
     - Stops `ncf-web` and `nightcodeforge-web` watchdog launcher processes first (preventing automatic respawn of `NightCodeForge.Web`), then stops `NightCodeForge.Web` and waits briefly for native library file locks (`e_sqlite3.dll`, `skeychain.dll`) to release.
     - When upgrading OllamaRouter, invokes `ollamarouter stop` (bounded 30s timeout) and stops any remaining `ollamarouter-server` and `ollamarouter-app` processes.
     - Records whether `NightCodeForge.Web` and `ollamarouter-server` were running prior to the upgrade so they can be automatically restarted once the swap completes.
   - **Strict User Data & Configuration Preservation:**
     - Never overwrites or deletes NightCodeForge `<InstallDir>/config/` (`config.json`, `default-key.dat`, `api-keys.json`), `<InstallDir>/data/` (`nightcodeforge.db`, `nightcodeforge_hangfire.db`), `<InstallDir>/logs/`, `<InstallDir>/launcher.conf`, or `~/.nightcodeforge/`, nor OllamaRouter `<OllamaRouterDir>/config/`, `<OllamaRouterDir>/state/` (`targets.json`, `recommended.json`, `activity-statistics.json`), or `<OllamaRouterDir>/logs/`.
     - Re-applies `ConnectionStrings`, `Logging:LogsDirectory`, and preserved `Urls` into the newly extracted `appsettings.json` and `appsettings.Production.json` (and `StateDirectory` / `LogsDirectory` into OllamaRouter's `appsettings.Production.json`).
   - **Atomic Binary Swap, `.backup` Rollback & Legacy Adoption:**
     - Moves existing application binaries and static assets (`*.dll`, `*.exe`, `wwwroot/`, `skills/`, etc., excluding `config/`, `data/`, `logs/`, `state/`, `launcher.conf`) into a `.backup` staging directory before extracting the new archive, restoring `.backup` automatically if extraction or checksum validation fails.
     - Updates `<InstallDir>/VERSION`, copies the newer running `NightCodeForge.Installer` binary into `<InstallDir>`, and updates the OS Uninstall registry entry (`DisplayVersion`, `UninstallString`, `EstimatedSize`) via `IOsIntegrationService` — seamlessly adopting legacy `publish.ps1` installations into OS-registered installations.

---

### 2.5 Uninstaller & OS Uninstall Integration (`IUninstallService` & `IOsIntegrationService`)

1. **OS Uninstall System & Shortcut Registration (`IOsIntegrationService`):**
   - **Windows (User-Scoped, No Admin Elevation Required):**
     - Writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\NightCodeForge` with:
       - `DisplayName` = `"NightCodeForge"`
       - `DisplayVersion` = installed version
       - `Publisher` = `"Pmad"`
       - `InstallLocation` = `<InstallDir>`
       - `DisplayIcon` = `<InstallDir>\NightCodeForge.Installer.exe,0`
       - `UninstallString` = `"\"<InstallDir>\\NightCodeForge.Installer.exe\" uninstall"`
       - `QuietUninstallString` = `"\"<InstallDir>\\NightCodeForge.Installer.exe\" uninstall --quiet"`
       - `ModifyPath` = `"\"<InstallDir>\\NightCodeForge.Installer.exe\""`
       - `NoModify` = `0` (DWORD), `NoRepair` = `1` (DWORD)
       - `EstimatedSize` (DWORD KB), `InstallDate` (`yyyyMMdd`)
     - Creates user Start Menu shortcut (`%APPDATA%\Microsoft\Windows\Start Menu\Programs\NightCodeForge.lnk`) and optional Desktop shortcut.
   - **Linux:**
     - Writes XDG desktop entries `~/.local/share/applications/nightcodeforge.desktop` (launching `ncf-web` / web UI) and `~/.local/share/applications/nightcodeforge-installer.desktop` (Maintenance Hub / Uninstaller) plus user `PATH` registration (`~/.profile` / `~/.bashrc`).
   - **macOS:**
     - Creates `~/Applications/NightCodeForge.app` wrapper / launcher and user `PATH` registration.

2. **`uninstall` Command & Uninstallation Execution Flow (`IUninstallService`):**
   - CLI entry points: `nightcodeforge-installer uninstall [--keep-data] [--remove-tools] [--remove-ollamarouter] [--quiet]` (and `--uninstall` alias). Launching `uninstall` without `--quiet` opens the Avalonia Interactive Uninstaller view ([Step 12.18C](STEP_12_18C_INSTALLER_AVALONIA_OOBE_WIZARD_AND_MAINTENANCE_HUB.md)); passing `--quiet` executes headless uninstallation and returns exit code `0` or `1`.
   - Uninstallation steps:
     1. Stop running `ncf-web`, `nightcodeforge-web`, and `NightCodeForge.Web` processes.
     2. Remove `<InstallDir>/bin` from user `PATH`.
     3. Unregister OS uninstall entry (`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\NightCodeForge`) and delete Start Menu / Desktop shortcuts, XDG `.desktop` files, or macOS `.app` wrapper.
     4. When `RemoveCompanionTools == true` (`--remove-tools` or UI checkbox), uninstall the 5 companion NuGet global tools (`NightCodeForge.Cli`, `NightCodeForge.Assay`, `Pmad.CSharpNav`, `Pmad.MarkdownFetch`, `Pmad.CodeCondenser`) on the host (and WSL2 where applicable).
     5. When `RemoveOllamaRouter == true` (`--remove-ollamarouter` or UI checkbox; default `false` so standalone OllamaRouter setups remain intact), invoke `ollamarouter stop` (bounded 30s timeout) and stop remaining `ollamarouter-server` / `ollamarouter-app` processes, remove `<OllamaRouterDir>/bin` from user `PATH`, unregister the user-scoped `ollamarouter://` protocol handler (`HKCU\Software\Classes\ollamarouter` on Windows, `~/.local/share/applications/ollamarouter.desktop` on Linux), and delete `<OllamaRouterDir>` files (preserving `config/`, `state/`, and `logs/` when `KeepData == true`).
     6. Delete `<InstallDir>` files — preserving `config/`, `data/`, and `logs/` when `KeepData == true` (`--keep-data`), or removing the entire directory when `KeepData == false`.
     7. On Windows, when the uninstaller binary itself is running from inside `<InstallDir>`, schedule deferred self-cleanup of `<InstallDir>` (when not keeping data, or cleanup of `NightCodeForge.Installer.exe` itself when keeping data) via a detached process (`cmd.exe /c ping 127.0.0.1 -n 2 > nul & ...`) immediately before exit.

---

## 3. Implementation Checklist & Deliverables

- [ ] Create `src/NightCodeForge.Installer` and `tests/NightCodeForge.Installer.Tests` and add them to `NightCodeForge.slnx`, configured for Native AOT (`PublishAot=true`, `PackageReference Remove="Newtonsoft.Json"`, and `InstallerJsonContext` source-generated `System.Text.Json`).
- [ ] Update `src/NightCodeForge.Cli/NightCodeForge.Cli.csproj` with full NuGet tool package metadata (`PackageId=NightCodeForge.Cli`, `ToolCommandName=ncf`, `PackAsTool=true`, `PackageReadmeFile=README.md`, and `<None Include="..\..\README.md" Pack="true" PackagePath="\" />`).
- [ ] Implement `IEnvironmentProvisioner` with WSL2 detection, JIT privilege elevation, reboot `3010` detection, and host/Linux/macOS Docker/.NET/OS toolchain provisioning.
- [ ] Implement `ICompanionNuGetProvisioner` pre-installing `NightCodeForge.Cli`, `NightCodeForge.Assay`, `Pmad.CSharpNav`, `Pmad.MarkdownFetch`, and `Pmad.CodeCondenser` across Host & WSL2, supporting both Online NuGet and Air-Gapped `--add-source <bundleDir> --no-cache`.
- [ ] Implement `IInstallationDetector` and `IReleaseManager` supporting Online GitHub Releases (`jetelain/NightCodeForge` and `jetelain/OllamaRouter`), Air-Gapped Local Bundle mode (`.zip` and `.tar.gz`), SHA-256 verification, in-place upgrades of existing & legacy `publish.ps1` installations (`%LOCALAPPDATA%\NightCodeForge` / `~/.nightcodeforge`), post-extract `appsettings` / launcher fan-out / `skills/` / initial OllamaRouter (`appsettings.Production.json`, `bin/` shims, `ollamarouter://` URI registration) / Unix executable-bit configuration, self-copy to `<InstallDir>`, service restart preservation, and atomic `.backup` swap/rollback.
- [ ] Implement `IOsIntegrationService` and `IUninstallService` for Windows `HKCU\...\Uninstall\NightCodeForge` registry integration, Start Menu/Desktop/XDG/macOS shortcuts, user `PATH` registration, and `uninstall` command (with `--keep-data`, `--remove-tools`, `--remove-ollamarouter`, `--quiet`, and deferred Windows self-cleanup).
- [ ] Create `package.ps1` scripts in `NightCodeAssay`, `OllamaRouter`, and `NightCodeForge`, and document/provide NuGet.org download steps for `Pmad.CSharpNav`, `Pmad.MarkdownFetch`, and `Pmad.CodeCondenser` to populate an initial air-gapped debug bundle.
- [ ] Add unit tests in `NightCodeForge.Installer.Tests` with mocked process runners, HTTP handlers, and temp directories covering provisioning state transitions, legacy `publish.ps1` & existing installation detection, in-place upgrade preservation, NuGet tool host/WSL & air-gapped same-version reinstallation, checksum verification, `.zip`/`.tar.gz` extraction, atomic rollback, Windows/Linux uninstall plan generation, and `uninstall` execution.
