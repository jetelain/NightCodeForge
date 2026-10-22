<#
.SYNOPSIS
    Automated bootstrap and installation script for NightCodeForge.
.DESCRIPTION
    Sets up a complete developer and host execution environment for NightCodeForge:
    1. Checks and installs WSL2 if not already present.
    2. Checks and provisions the target WSL2 Linux distribution (default: Ubuntu).
    3. Checks and installs .NET 10 SDK on the Windows host if not already present.
    4. Provisions the WSL2 distribution with required toolchains:
       - .NET 10 SDK
       - Docker Engine (with systemd/service configuration)
       - ripgrep (rg)
       - ICU libraries (libicu-dev), locales (en_US.UTF-8)
       - Git and standard development utilities
    5. Clones or locates the NightCodeForge repository, then executes:
       - publish.ps1 (builds and installs NightCodeForge Web locally)
       - install-cli.ps1 (builds and installs the global 'ncf' CLI tool)
.PARAMETER Distro
    Name of the WSL2 Linux distribution to install or configure (default: Ubuntu).
.PARAMETER RepoUrl
    Git repository URL to clone if not already in a NightCodeForge checkout (default: https://github.com/jetelain/NightCodeForge.git).
.PARAMETER CloneDir
    Target directory for cloning the repository if running standalone (default: $HOME\source\repos\NightCodeForge).
.PARAMETER Configuration
    Build configuration for publish and CLI pack (default: Release).
.PARAMETER SkipGitCheck
    Skips verifying that Git is installed on Windows.
.PARAMETER SkipOllamaCheck
    Skips verifying that Ollama is installed on Windows.
.PARAMETER SkipWsl
    Skips WSL2 installation and version verification.
.PARAMETER SkipDistroSetup
    Skips installing and configuring packages inside the WSL2 Linux distribution.
.PARAMETER SkipDotNetHost
    Skips installing .NET 10 SDK on the Windows host.
.PARAMETER SkipClone
    Skips cloning the repository (assumes repository is present).
.PARAMETER SkipPublish
    Skips running publish.ps1.
.PARAMETER SkipCli
    Skips running install-cli.ps1.
.PARAMETER Force
    Forces reinstall/update flags where applicable.
.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -Distro "Ubuntu"
.EXAMPLE
    .\install.ps1 -SkipWsl -Configuration Release
#>
[CmdletBinding()]
param(
    [string]$Distro = "Ubuntu",
    [string]$RepoUrl = "https://github.com/jetelain/NightCodeForge.git",
    [string]$CloneDir = $(Join-Path $HOME "source\repos\NightCodeForge"),
    [string]$Configuration = "Release",
    [switch]$SkipGitCheck,
    [switch]$SkipOllamaCheck,
    [switch]$SkipWsl,
    [switch]$SkipDistroSetup,
    [switch]$SkipDotNetHost,
    [switch]$SkipClone,
    [switch]$SkipPublish,
    [switch]$SkipCli,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# Constants
$Net10VersionPrefix = "10."
$DotNetInstallScriptUrl = "https://dot.net/v1/dotnet-install.ps1"
$WindowsWingetPackageId = "Microsoft.DotNet.SDK.10"
$ExitCodeRebootRequired = 3010

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " NightCodeForge - Complete Environment Installer          " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

function Find-GitCommand {
    $cmd = Get-Command "git.exe" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $knownPaths = @(
        "$env:ProgramFiles\Git\cmd\git.exe",
        "$env:ProgramFiles\Git\bin\git.exe",
        "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe"
    )
    foreach ($p in $knownPaths) {
        if (Test-Path $p) {
            $dir = Split-Path -Parent $p
            if ($env:PATH -notlike "*$dir*") {
                $env:PATH = "$dir;$env:PATH"
            }
            return $p
        }
    }
    return $null
}

function Find-OllamaCommand {
    $cmd = Get-Command "ollama.exe" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $knownPaths = @(
        "$env:LOCALAPPDATA\Programs\Ollama\ollama.exe",
        "$env:ProgramFiles\Ollama\ollama.exe"
    )
    foreach ($p in $knownPaths) {
        if (Test-Path $p) {
            $dir = Split-Path -Parent $p
            if ($env:PATH -notlike "*$dir*") {
                $env:PATH = "$dir;$env:PATH"
            }
            return $p
        }
    }
    return $null
}

function Install-Git {
    Write-Host "Attempting automated installation of Git..." -ForegroundColor Cyan
    $wingetCmd = Get-Command "winget" -ErrorAction SilentlyContinue
    if ($wingetCmd) {
        Write-Host "Installing Git via winget (Git.Git)..." -ForegroundColor Gray
        try {
            & winget install --id Git.Git -e --source winget --accept-source-agreements --accept-package-agreements --silent
            Start-Sleep -Seconds 2
            $path = Find-GitCommand
            if ($path) { return $path }
        } catch {
            Write-Warning "Winget installation of Git encountered an error: $_"
        }
    }

    Write-Host "Querying GitHub releases for latest Git for Windows installer..." -ForegroundColor Gray
    $downloadUrl = $null
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/git-for-windows/git/releases/latest" -Headers @{ "User-Agent" = "NightCodeForge-Installer" } -TimeoutSec 5
        $asset = $release.assets | Where-Object { $_.name -match '^Git-.*-64-bit\.exe$' } | Select-Object -First 1
        if ($asset) {
            $downloadUrl = $asset.browser_download_url
        }
    } catch {
        Write-Warning "Could not query GitHub releases API for Git installer: $_"
    }

    if (-not $downloadUrl) {
        Write-Warning "Could not resolve a direct download URL for Git for Windows."
        return $null
    }

    Write-Host "Downloading Git for Windows installer from $downloadUrl..." -ForegroundColor Gray
    $gitInstaller = Join-Path $env:TEMP "Git-Setup.exe"
    try {
        Invoke-WebRequest -Uri $downloadUrl -OutFile $gitInstaller -UseBasicParsing
        Write-Host "Running Git installer silently..." -ForegroundColor Gray
        $proc = Start-Process -FilePath $gitInstaller -ArgumentList "/VERYSILENT", "/NORESTART", "/NOCANCEL", "/SP-", "/CLOSEAPPLICATIONS", "/RESTARTAPPLICATIONS" -Wait -PassThru
        Start-Sleep -Seconds 2
        return (Find-GitCommand)
    } catch {
        Write-Warning "Direct installer download or installation of Git failed: $_"
        return $null
    } finally {
        if (Test-Path $gitInstaller) {
            Remove-Item $gitInstaller -Force -ErrorAction SilentlyContinue
        }
    }
}

function Install-Ollama {
    Write-Host "Attempting automated installation of Ollama..." -ForegroundColor Cyan
    $wingetCmd = Get-Command "winget" -ErrorAction SilentlyContinue
    if ($wingetCmd) {
        Write-Host "Installing Ollama via winget (Ollama.Ollama)..." -ForegroundColor Gray
        try {
            & winget install --id Ollama.Ollama -e --source winget --accept-source-agreements --accept-package-agreements --silent
            Start-Sleep -Seconds 2
            $path = Find-OllamaCommand
            if ($path) { return $path }
        } catch {
            Write-Warning "Winget installation of Ollama encountered an error: $_"
        }
    }

    Write-Host "Downloading Ollama installer..." -ForegroundColor Gray
    $ollamaInstaller = Join-Path $env:TEMP "OllamaSetup.exe"
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
        Invoke-WebRequest -Uri "https://ollama.com/download/OllamaSetup.exe" -OutFile $ollamaInstaller -UseBasicParsing
        Write-Host "Running Ollama installer silently..." -ForegroundColor Gray
        $proc = Start-Process -FilePath $ollamaInstaller -ArgumentList "/silent" -Wait -PassThru
        Start-Sleep -Seconds 2
        return (Find-OllamaCommand)
    } catch {
        Write-Warning "Direct installer download of Ollama failed: $_"
        return $null
    } finally {
        if (Test-Path $ollamaInstaller) {
            Remove-Item $ollamaInstaller -Force -ErrorAction SilentlyContinue
        }
    }
}

function Test-IsAdmin {
    $currentPrincipal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    return $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-WslInstalled {
    $wslCmd = Get-Command "wsl.exe" -ErrorAction SilentlyContinue
    if (-not $wslCmd) { return $false }
    try {
        $status = & wsl.exe --status 2>&1
        $statusText = ($status | Out-String) -replace "`0", ""
        if ($LASTEXITCODE -eq 0 -or $statusText -match "Default Version" -or $statusText -match "Version par d") {
            return $true
        }
    } catch {
        # Fallback to listing distributions
    }

    try {
        $list = & wsl.exe -l -v 2>&1
        $listText = ($list | Out-String) -replace "`0", ""
        if ($listText -match "NAME" -or $listText -match "VERSION" -or $LASTEXITCODE -eq 0) {
            return $true
        }
    } catch {
        # Fall through
    }

    return $false
}

function Get-InstalledWslDistros {
    $raw = & wsl.exe -l -q 2>$null
    if (-not $raw) { return @() }
    $distros = @()
    foreach ($line in $raw) {
        $clean = ($line -replace "`0", "").Trim()
        if (-not [string]::IsNullOrWhiteSpace($clean)) {
            $distros += $clean
        }
    }
    return $distros
}

function Test-HostDotNet10Installed {
    $dotnetCmd = Get-Command "dotnet" -ErrorAction SilentlyContinue
    if (-not $dotnetCmd) { return $false }
    try {
        $sdks = & dotnet --list-sdks 2>$null
        return (($sdks | Where-Object { $_ -match "^$Net10VersionPrefix" }) -ne $null)
    } catch {
        return $false
    }
}

function Invoke-WslBashScript {
    param(
        [Parameter(Mandatory = $true)][string]$TargetDistro,
        [Parameter(Mandatory = $true)][string]$ScriptContent,
        [string]$User = "root"
    )

    $tempScript = [System.IO.Path]::GetTempFileName() + ".sh"
    try {
        $cleanContent = $ScriptContent.Replace("`r`n", "`n")
        $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::WriteAllText($tempScript, $cleanContent, $utf8NoBom)

        $winPath = $tempScript.Replace("\", "/")
        $wslPathRaw = & wsl.exe -d $TargetDistro -u $User -- wslpath -u "$winPath"
        $wslPath = ($wslPathRaw -replace "`0", "").Trim()

        & wsl.exe -d $TargetDistro -u $User -- bash "$wslPath"
        if ($LASTEXITCODE -ne 0) {
            throw "WSL command failed inside distro '$TargetDistro' with exit code $LASTEXITCODE."
        }
    }
    finally {
        if (Test-Path $tempScript) {
            Remove-Item $tempScript -Force -ErrorAction SilentlyContinue
        }
    }
}

# -------------------------------------------------------------
# STEP 1: Check Windows Host Prerequisites (Git, Ollama)
# -------------------------------------------------------------
Write-Host "`n[1/6] Checking Windows host prerequisites (Git, Ollama)..." -ForegroundColor Gray
$hasMissingPrerequisites = $false

# 1.1 Check Git
if ($SkipGitCheck) {
    Write-Host "Skipping Git check (-SkipGitCheck specified)." -ForegroundColor Yellow
} else {
    $gitPath = Find-GitCommand
    if (-not $gitPath) {
        Write-Host "Git is not detected on Windows. Attempting automated installation..." -ForegroundColor Cyan
        $gitPath = Install-Git
    }

    if ($gitPath) {
        $gitVersion = (& git --version 2>$null)
        Write-Host "Git is ready: $gitVersion ($gitPath)" -ForegroundColor Green
    } else {
        Write-Host "`n[ACTION REQUIRED] Automatic installation of Git could not be completed." -ForegroundColor Red
        Write-Host "Git is required to clone repositories, switch branches, and track changes." -ForegroundColor Yellow
        Write-Host "Please install Git on Windows manually:" -ForegroundColor Cyan
        Write-Host "  Option 1 (winget):    winget install --id Git.Git -e --source winget" -ForegroundColor White
        Write-Host "  Option 2 (installer): Download from https://git-scm.com/download/win" -ForegroundColor White
        $hasMissingPrerequisites = $true
    }
}

# 1.2 Check Ollama
if ($SkipOllamaCheck) {
    Write-Host "Skipping Ollama check (-SkipOllamaCheck specified)." -ForegroundColor Yellow
} else {
    $ollamaPath = Find-OllamaCommand
    if (-not $ollamaPath) {
        Write-Host "Ollama is not detected on Windows. Attempting automated installation..." -ForegroundColor Cyan
        $ollamaPath = Install-Ollama
    }

    if ($ollamaPath) {
        $ollamaVer = (& ollama --version 2>$null)
        Write-Host "Ollama is ready: $ollamaVer ($ollamaPath)" -ForegroundColor Green

        # Check if Ollama daemon service is active
        try {
            $tagsResponse = Invoke-RestMethod -Uri "http://127.0.0.1:11434/api/tags" -Method Get -TimeoutSec 2 -ErrorAction SilentlyContinue
            $modelCount = if ($tagsResponse.models) { $tagsResponse.models.Count } else { 0 }
            Write-Host "Ollama service is active at http://127.0.0.1:11434 ($modelCount local model(s) available)." -ForegroundColor Green
        } catch {
            Write-Host "Note: Ollama is installed, but service is not currently responding at http://127.0.0.1:11434." -ForegroundColor Yellow
            Write-Host "      Ensure Ollama is started ('ollama serve' or open the Ollama desktop app)." -ForegroundColor Gray
        }
    } else {
        Write-Host "`n[ACTION REQUIRED] Automatic installation of Ollama could not be completed." -ForegroundColor Red
        Write-Host "NightCodeForge requires Ollama (or an Ollama-compatible endpoint) for local LLM inference." -ForegroundColor Yellow
        Write-Host "Please install Ollama on Windows manually:" -ForegroundColor Cyan
        Write-Host "  Option 1 (winget):    winget install Ollama.Ollama" -ForegroundColor White
        Write-Host "  Option 2 (installer): Download from https://ollama.com/download/windows" -ForegroundColor White
        $hasMissingPrerequisites = $true
    }
}

if ($hasMissingPrerequisites) {
    Write-Host "`nPlease install the missing prerequisite(s) above, then restart your PowerShell terminal and re-run install.ps1." -ForegroundColor Yellow
    exit 1
}

# -------------------------------------------------------------
# STEP 2: Check and Install WSL2
# -------------------------------------------------------------
Write-Host "`n[2/6] Checking WSL2 installation..." -ForegroundColor Gray
if ($SkipWsl) {
    Write-Host "Skipping WSL check (-SkipWsl specified)." -ForegroundColor Yellow
} else {
    $hasWsl = Test-WslInstalled
    if ($hasWsl) {
        Write-Host "WSL2 is installed." -ForegroundColor Green
        # Ensure default version is set to 2
        & wsl.exe --set-default-version 2 2>$null | Out-Null
    } else {
        Write-Host "WSL2 is not installed on this system." -ForegroundColor Yellow
        Write-Host "`nTo install WSL2, please open PowerShell as Administrator and run:" -ForegroundColor Cyan
        Write-Host "    wsl.exe --install --no-distribution" -ForegroundColor White
        Write-Host "`nNotes:" -ForegroundColor Gray
        Write-Host "  - Windows may require a system restart to complete component installation." -ForegroundColor Gray
        Write-Host "  - After restarting, simply re-run '.\install.ps1' to continue setup." -ForegroundColor Gray
        exit 1
    }
}

# -------------------------------------------------------------
# STEP 3: Setup WSL2 Distribution
# -------------------------------------------------------------
Write-Host "`n[3/6] Checking WSL2 distribution '$Distro'..." -ForegroundColor Gray
if ($SkipWsl) {
    Write-Host "Skipping distribution setup (-SkipWsl specified)." -ForegroundColor Yellow
} else {
    $distros = @(Get-InstalledWslDistros)
    $targetDistroFound = @($distros | Where-Object { $_ -eq $Distro -or $_ -like "$Distro*" })

    if ($targetDistroFound.Count -gt 0) {
        $actualDistroName = $targetDistroFound[0]
        Write-Host "Found existing WSL2 distribution: $actualDistroName" -ForegroundColor Green
        $Distro = $actualDistroName
    } else {
        Write-Host "Distribution '$Distro' is not installed. Installing..." -ForegroundColor Cyan

        # Attempt installation via wsl --install
        & wsl.exe --install -d $Distro --web-download
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Retrying installation without --web-download..." -ForegroundColor Yellow
            & wsl.exe --install -d $Distro
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Failed to install WSL distribution '$Distro'."
                exit $LASTEXITCODE
            }
        }

        & wsl.exe --set-version $Distro 2 2>$null | Out-Null
        Write-Host "WSL distribution '$Distro' installed successfully." -ForegroundColor Green
    }
}

# -------------------------------------------------------------
# STEP 4: Check and Install .NET 10 SDK on Windows Host
# -------------------------------------------------------------
Write-Host "`n[4/6] Checking .NET 10 SDK on Windows host..." -ForegroundColor Gray
if ($SkipDotNetHost) {
    Write-Host "Skipping host .NET 10 SDK check (-SkipDotNetHost specified)." -ForegroundColor Yellow
} else {
    $hasDotNet10 = Test-HostDotNet10Installed
    if ($hasDotNet10) {
        $sdkVersions = (& dotnet --list-sdks 2>$null | Where-Object { $_ -match "^$Net10VersionPrefix" }) -join ", "
        Write-Host ".NET 10 SDK is installed on Windows host: $sdkVersions" -ForegroundColor Green
    } else {
        Write-Host ".NET 10 SDK not found on Windows host. Installing..." -ForegroundColor Cyan

        $installed = $false
        $wingetCmd = Get-Command "winget" -ErrorAction SilentlyContinue
        if ($wingetCmd) {
            Write-Host "Attempting install via winget ($WindowsWingetPackageId)..." -ForegroundColor Gray
            try {
                & winget install $WindowsWingetPackageId --accept-source-agreements --accept-package-agreements --silent
                if ($LASTEXITCODE -eq 0 -and (Test-HostDotNet10Installed)) {
                    $installed = $true
                    Write-Host ".NET 10 SDK installed successfully via winget." -ForegroundColor Green
                }
            } catch {
                Write-Warning "Winget installation encountered an error: $_"
            }
        }

        if (-not $installed) {
            Write-Host "Downloading official Microsoft dotnet-install.ps1..." -ForegroundColor Gray
            $tempInstaller = Join-Path $env:TEMP "dotnet-install.ps1"
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
            Invoke-WebRequest -Uri $DotNetInstallScriptUrl -OutFile $tempInstaller -UseBasicParsing

            $installDir = if (Test-IsAdmin) { "$env:ProgramFiles\dotnet" } else { "$env:LOCALAPPDATA\Microsoft\dotnet" }
            Write-Host "Running dotnet-install.ps1 to $installDir..." -ForegroundColor Gray
            & $tempInstaller -Channel 10.0 -InstallDir $installDir
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Failed to install .NET 10 SDK via dotnet-install.ps1."
                exit $LASTEXITCODE
            }

            # Update PATH for the current session
            if ($env:PATH -notlike "*$installDir*") {
                $env:PATH = "$installDir;$env:PATH"
            }

            if (-not (Test-HostDotNet10Installed)) {
                Write-Warning ".NET 10 SDK installed, but 'dotnet' command not immediately detected in PATH. You may need to restart your terminal after installation."
            } else {
                Write-Host ".NET 10 SDK installed successfully." -ForegroundColor Green
            }
        }
    }
}

# -------------------------------------------------------------
# STEP 5: Provision Tools in WSL2 Distro (.NET 10 SDK, Docker, ripgrep)
# -------------------------------------------------------------
Write-Host "`n[5/6] Provisioning tools in WSL2 distro '$Distro'..." -ForegroundColor Gray
if ($SkipDistroSetup) {
    Write-Host "Skipping distro tool setup (-SkipDistroSetup specified)." -ForegroundColor Yellow
} else {
    $distroProvisioningScript = @'
#!/usr/bin/env bash
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive

echo "--> Updating apt package repository..."
apt-get update -y

echo "--> Installing core dependencies (curl, ca-certificates, git, ripgrep, libicu-dev, locales)..."
apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    gnupg \
    lsb-release \
    git \
    build-essential \
    procps \
    jq \
    tar \
    ripgrep \
    libicu-dev \
    locales

echo "--> Generating locales..."
locale-gen en_US.UTF-8 || true
update-locale LANG=en_US.UTF-8 || true

# 1. Install .NET 10 SDK via apt
if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    echo "--> .NET 10 SDK is already installed in WSL."
else
    echo "--> Installing .NET 10 SDK via apt (dotnet-sdk-10.0)..."
    if ! apt-get install -y --no-install-recommends dotnet-sdk-10.0; then
        echo "--> dotnet-sdk-10.0 not in current apt feed. Registering Microsoft package repository..."
        UBUNTU_CODENAME=$(lsb_release -cs 2>/dev/null || echo "noble")
        curl -fsSL "https://packages.microsoft.com/config/ubuntu/${UBUNTU_CODENAME}/packages-microsoft-prod.deb" -o /tmp/packages-microsoft-prod.deb
        dpkg -i /tmp/packages-microsoft-prod.deb
        rm -f /tmp/packages-microsoft-prod.deb
        apt-get update -y
        apt-get install -y --no-install-recommends dotnet-sdk-10.0
    fi
fi

# Ensure DOTNET_ROOT profile exists
cat << 'EOF' > /etc/profile.d/dotnet.sh
if [ -d /usr/lib/dotnet ]; then
    export DOTNET_ROOT=/usr/lib/dotnet
elif [ -d /usr/share/dotnet ]; then
    export DOTNET_ROOT=/usr/share/dotnet
fi
export PATH=$PATH:$DOTNET_ROOT:$HOME/.dotnet/tools
EOF
chmod +x /etc/profile.d/dotnet.sh

# 2. Install Docker via apt
if command -v docker >/dev/null 2>&1; then
    echo "--> Docker is already installed in WSL."
else
    echo "--> Installing Docker via apt (docker.io)..."
    apt-get install -y --no-install-recommends docker.io docker-buildx docker-compose-v2 || apt-get install -y --no-install-recommends docker.io
fi

# Ensure systemd is enabled in /etc/wsl.conf
if [ ! -f /etc/wsl.conf ] || ! grep -q "systemd=true" /etc/wsl.conf; then
    echo "--> Enabling systemd in /etc/wsl.conf..."
    mkdir -p /etc
    if ! grep -q "\[boot\]" /etc/wsl.conf 2>/dev/null; then
        printf "\n[boot]\nsystemd=true\n" >> /etc/wsl.conf
    else
        sed -i '/\[boot\]/a systemd=true' /etc/wsl.conf
    fi
fi

# Start Docker daemon service
service docker status >/dev/null 2>&1 || service docker start >/dev/null 2>&1 || true

# Add regular user(s) (UID >= 1000) to docker group
for u in $(awk -F: '$3 >= 1000 && $3 < 60000 {print $1}' /etc/passwd); do
    echo "--> Adding user '$u' to docker group..."
    usermod -aG docker "$u" || true
done

# Configure Git safe directory for seamless Windows-to-WSL mount operations
git config --system --add safe.directory '*' || true

echo "--> WSL2 toolchain verification:"
echo "    dotnet : $(dotnet --version 2>/dev/null || echo 'not found')"
echo "    docker : $(docker --version 2>/dev/null || echo 'not found')"
echo "    ripgrep: $(rg --version 2>/dev/null | head -n 1 || echo 'not found')"
echo "    git    : $(git --version 2>/dev/null || echo 'not found')"
'@

    Write-Host "Running provisioning script inside WSL2 distribution '$Distro'..." -ForegroundColor Cyan
    Invoke-WslBashScript -TargetDistro $Distro -ScriptContent $distroProvisioningScript -User "root"
    Write-Host "WSL2 distribution '$Distro' provisioned successfully." -ForegroundColor Green
}

# -------------------------------------------------------------
# STEP 6: Prepare NightCodeForge Repository and Install Application
# -------------------------------------------------------------
Write-Host "`n[6/6] Preparing NightCodeForge repository and installing application..." -ForegroundColor Gray

# Determine repository root
$repoRoot = $null
if (Test-Path "$PSScriptRoot\NightCodeForge.slnx") {
    $repoRoot = $PSScriptRoot
    Write-Host "Detected NightCodeForge repository at current location: $repoRoot" -ForegroundColor Green
} elseif (Test-Path "$CloneDir\.git") {
    $repoRoot = $CloneDir
    Write-Host "Using existing NightCodeForge repository at: $repoRoot" -ForegroundColor Green
} else {
    if ($SkipClone) {
        Write-Error "SkipClone was specified, but NightCodeForge repository was not found at '$PSScriptRoot' or '$CloneDir'."
        exit 1
    }

    Write-Host "Cloning repository from $RepoUrl to $CloneDir..." -ForegroundColor Cyan
    $gitCmd = Get-Command "git" -ErrorAction SilentlyContinue
    if (-not $gitCmd) {
        Write-Error "Git command 'git' was not found on the Windows host. Please install Git for Windows or add it to PATH."
        exit 1
    }

    $parentDir = Split-Path -Parent $CloneDir
    if (-not (Test-Path $parentDir)) {
        New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
    }

    & git clone $RepoUrl $CloneDir
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to clone repository from $RepoUrl."
        exit $LASTEXITCODE
    }
    $repoRoot = $CloneDir
    Write-Host "Repository cloned successfully to: $repoRoot" -ForegroundColor Green
}

# 6.1 Run publish.ps1
if (-not $SkipPublish) {
    Write-Host "`nExecuting publish.ps1 (-Configuration $Configuration)..." -ForegroundColor Cyan
    $publishScript = Join-Path $repoRoot "publish.ps1"
    if (-not (Test-Path $publishScript)) {
        Write-Error "publish.ps1 not found at: $publishScript"
        exit 1
    }

    $publishParams = @{
        Configuration = $Configuration
    }
    & $publishScript @publishParams
    if ($LASTEXITCODE -ne 0) {
        Write-Error "publish.ps1 failed with exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
} else {
    Write-Host "Skipping publish.ps1 (-SkipPublish specified)." -ForegroundColor Yellow
}

# 6.2 Run install-cli.ps1
if (-not $SkipCli) {
    Write-Host "`nExecuting install-cli.ps1 (-Configuration $Configuration)..." -ForegroundColor Cyan
    $installCliScript = Join-Path $repoRoot "install-cli.ps1"
    if (-not (Test-Path $installCliScript)) {
        Write-Error "install-cli.ps1 not found at: $installCliScript"
        exit 1
    }

    $cliParams = @{
        Configuration = $Configuration
    }
    if ($Force) {
        $cliParams["Force"] = $true
    }
    & $installCliScript @cliParams
    if ($LASTEXITCODE -ne 0) {
        Write-Error "install-cli.ps1 failed with exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
} else {
    Write-Host "Skipping install-cli.ps1 (-SkipCli specified)." -ForegroundColor Yellow
}

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " NightCodeForge Installation Complete!                    " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "NightCodeForge Web and CLI are now installed and configured." -ForegroundColor Green
Write-Host "Run 'ncf --help' to verify the CLI tool or start the web server via 'ncf-web'." -ForegroundColor Cyan
