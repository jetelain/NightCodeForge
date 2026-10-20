<#
.SYNOPSIS
    Builds, installs, and configures NightCodeForge Web locally in user AppData.
.DESCRIPTION
    Publishes NightCodeForge.Web, installs it to the user-local application directory
    (%LOCALAPPDATA%\NightCodeForge), adapts SQLite databases to a persistent data directory,
    configures rolling logging to a dedicated logs directory, sets up web launchers,
    configures user PATH idempotently, and optionally syncs skills to a target workspace.
    (Note: CLI tool shim is not required, as the CLI 'ncf' is installed as a global dotnet tool).
.PARAMETER Configuration
    Build configuration (default: Release).
.PARAMETER Runtime
    Target runtime identifier (default: win-x64).
.PARAMETER InstallDir
    Target installation folder (default: %LOCALAPPDATA%\NightCodeForge or ~/.nightcodeforge).
.PARAMETER DataDir
    Persistent directory for SQLite databases (default: <InstallDir>\data).
.PARAMETER LogsDir
    Directory for application logs (default: <InstallDir>\logs).
.PARAMETER BinDir
    Directory for the command launcher and PATH entry (default: <InstallDir>\bin).
.PARAMETER Urls
    Kestrel hosting URLs (default: http://localhost:5270).
.PARAMETER WorkspaceDir
    Optional workspace directory to sync skills to (e.g. C:\Workspace\MyProject).
.PARAMETER SelfContained
    Publishes a self-contained runtime deployment instead of framework-dependent.
.PARAMETER Uninstall
    Uninstalls NightCodeForge Web from the user directory and cleans user PATH.
.PARAMETER KeepData
    When used with -Uninstall, preserves the databases (data/) and logs (logs/) directories.
.EXAMPLE
    .\publish.ps1
.EXAMPLE
    .\publish.ps1 -Configuration Debug
.EXAMPLE
    .\publish.ps1 -Urls "http://localhost:5000"
.EXAMPLE
    .\publish.ps1 -Uninstall
.EXAMPLE
    .\publish.ps1 -Uninstall -KeepData
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$InstallDir = $(
        $localApp = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::LocalApplicationData)
        if ([string]::IsNullOrWhiteSpace($localApp)) {
            Join-Path $HOME ".nightcodeforge"
        } else {
            Join-Path $localApp "NightCodeForge"
        }
    ),
    [string]$DataDir = "",
    [string]$LogsDir = "",
    [string]$BinDir = "",
    [string]$Urls = "http://localhost:5270",
    [string]$WorkspaceDir = "",
    [switch]$SelfContained,
    [switch]$Uninstall,
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"

$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)

if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $InstallDir "data"
} else {
    $DataDir = [System.IO.Path]::GetFullPath($DataDir)
}

if ([string]::IsNullOrWhiteSpace($LogsDir)) {
    $LogsDir = Join-Path $InstallDir "logs"
} else {
    $LogsDir = [System.IO.Path]::GetFullPath($LogsDir)
}

if ([string]::IsNullOrWhiteSpace($BinDir)) {
    $BinDir = Join-Path $InstallDir "bin"
} else {
    $BinDir = [System.IO.Path]::GetFullPath($BinDir)
}

# 1. Uninstall path
if ($Uninstall) {
    Write-Host "==========================================================" -ForegroundColor Cyan
    Write-Host " Uninstalling NightCodeForge Web Application              " -ForegroundColor Cyan
    Write-Host "==========================================================" -ForegroundColor Cyan

    # Stop any running instances of NightCodeForge.Web
    $runningProcesses = Get-Process -Name "NightCodeForge.Web" -ErrorAction SilentlyContinue
    if ($runningProcesses) {
        Write-Host "Stopping running NightCodeForge.Web processes..." -ForegroundColor Yellow
        $runningProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }

    if (Test-Path $InstallDir) {
        try {
            if ($KeepData) {
                Write-Host "Preserving user databases in: $DataDir" -ForegroundColor Cyan
                Write-Host "Preserving user logs in:      $LogsDir" -ForegroundColor Cyan

                # Remove everything except DataDir and LogsDir
                Get-ChildItem -Path $InstallDir -Force | ForEach-Object {
                    $itemPath = $_.FullName
                    $isData = [string]::Equals($itemPath.TrimEnd('\/'), $DataDir.TrimEnd('\/'), [System.StringComparison]::OrdinalIgnoreCase)
                    $isLogs = [string]::Equals($itemPath.TrimEnd('\/'), $LogsDir.TrimEnd('\/'), [System.StringComparison]::OrdinalIgnoreCase)
                    if (-not $isData -and -not $isLogs) {
                        Remove-Item -Path $itemPath -Recurse -Force -ErrorAction SilentlyContinue
                    }
                }
                Write-Host "Cleaned NightCodeForge binaries while keeping data & logs." -ForegroundColor Green
            } else {
                $deleted = $false
                for ($attempt = 1; $attempt -le 6; $attempt++) {
                    try {
                        Remove-Item -Path $InstallDir -Recurse -Force
                        $deleted = $true
                        break
                    }
                    catch {
                        Start-Sleep -Milliseconds 250
                    }
                }
                if (-not $deleted) {
                    Remove-Item -Path $InstallDir -Recurse -Force
                }
                Write-Host "Removed installation directory: $InstallDir" -ForegroundColor Green
            }
        }
        catch {
            [Console]::Error.WriteLine("Error: Failed to remove directory '$InstallDir': $($_.Exception.Message)")
            exit 1
        }
    }

    # Clean user PATH
    try {
        $userPath = [System.Environment]::GetEnvironmentVariable("Path", [System.EnvironmentVariableTarget]::User)
        if ($userPath) {
            $parts = $userPath -split ';' | Where-Object {
                -not [string]::IsNullOrWhiteSpace($_) -and
                -not [string]::Equals($_.TrimEnd('\/'), $BinDir.TrimEnd('\/'), [System.StringComparison]::OrdinalIgnoreCase)
            }
            $newPath = $parts -join ';'
            [System.Environment]::SetEnvironmentVariable("Path", $newPath, [System.EnvironmentVariableTarget]::User)
        }
        $currentParts = $env:Path -split ';' | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
            -not [string]::Equals($_.TrimEnd('\/'), $BinDir.TrimEnd('\/'), [System.StringComparison]::OrdinalIgnoreCase)
        }
        $env:Path = $currentParts -join ';'
        Write-Host "NightCodeForge web launcher removed from user PATH." -ForegroundColor Green
    }
    catch {
        [Console]::Error.WriteLine("Error: Failed to update user PATH during uninstallation: $($_.Exception.Message)")
        exit 1
    }

    Write-Host "`nNightCodeForge Web uninstalled successfully." -ForegroundColor Green
    exit 0
}

# 2. Project check & version reading
$repoRoot = $PSScriptRoot
$webProjectPath = Join-Path $repoRoot "src\NightCodeForge.Web\NightCodeForge.Web.csproj"

if (-not (Test-Path $webProjectPath)) {
    throw "NightCodeForge.Web project not found: $webProjectPath"
}

[xml]$projXml = Get-Content $webProjectPath
$version = $projXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = "1.0.0"
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Publishing & Installing NightCodeForge Web Application    " -ForegroundColor Cyan
Write-Host " Version         : $version"
Write-Host " Configuration   : $Configuration"
Write-Host " Runtime         : $Runtime"
Write-Host " Self-Contained  : $([bool]$SelfContained)"
Write-Host " Install Dir     : $InstallDir"
Write-Host " Database Folder : $DataDir"
Write-Host " Logs Folder     : $LogsDir"
Write-Host " Launcher Bin Dir: $BinDir"
Write-Host " Default URLs    : $Urls"
Write-Host "==========================================================" -ForegroundColor Cyan

# 3. Stop running process to avoid publish file locks
$runningProcesses = Get-Process -Name "NightCodeForge.Web" -ErrorAction SilentlyContinue
if ($runningProcesses) {
    Write-Warning "NightCodeForge.Web is currently running. Stopping process to allow file updates..."
    $runningProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# 4. Ensure directories exist
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}
if (-not (Test-Path $DataDir)) {
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
}
if (-not (Test-Path $LogsDir)) {
    New-Item -ItemType Directory -Path $LogsDir -Force | Out-Null
}
if (-not (Test-Path $BinDir)) {
    New-Item -ItemType Directory -Path $BinDir -Force | Out-Null
}

# 5. Publish NightCodeForge.Web
Write-Host "`nPublishing NightCodeForge.Web..." -ForegroundColor Yellow

$publishArgs = @(
    "publish", $webProjectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "-p:DebugType=none",
    "-p:Version=$version",
    "-o", $InstallDir
)
if ($SelfContained) {
    $publishArgs += @("--self-contained", "true")
} else {
    $publishArgs += @("--no-self-contained")
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

# 6. Normalize the binary name and record installed version
$publishedBinary = Join-Path $InstallDir "NightCodeForge.Web.exe"
if (-not (Test-Path $publishedBinary)) {
    $candidate = Join-Path $InstallDir "NightCodeForge.Web"
    if (-not (Test-Path $candidate)) {
        throw "Published binary not found in $InstallDir."
    }
    $publishedBinary = $candidate
    Rename-Item -Path $publishedBinary -NewName "NightCodeForge.Web.exe"
}
Set-Content -Path (Join-Path $InstallDir "VERSION") -Value "$version`n"

# 7. Adapt database locations and logging in configuration files
Write-Host "`nAdapting database paths and logging configuration..." -ForegroundColor Yellow

$dbPath = (Join-Path $DataDir "nightcodeforge.db").Replace('\', '/')
$hangfireDbPath = (Join-Path $DataDir "nightcodeforge_hangfire.db").Replace('\', '/')
$logsDirPath = $LogsDir.Replace('\', '/')

# Apply adapted settings to both appsettings.json and appsettings.Production.json
$configFiles = @(
    (Join-Path $InstallDir "appsettings.json"),
    (Join-Path $InstallDir "appsettings.Production.json")
)

foreach ($configFilePath in $configFiles) {
    $settings = if (Test-Path $configFilePath) {
        try { Get-Content $configFilePath -Raw | ConvertFrom-Json } catch { [PSCustomObject]@{} }
    } else {
        [PSCustomObject]@{}
    }

    # Ensure ConnectionStrings section
    if (-not $settings.PSObject.Properties['ConnectionStrings']) {
        $settings | Add-Member -MemberType NoteProperty -Name 'ConnectionStrings' -Value ([PSCustomObject]@{})
    }
    $settings.ConnectionStrings | Add-Member -MemberType NoteProperty -Name 'DefaultConnection' -Value "Data Source=$dbPath;" -Force
    $settings.ConnectionStrings | Add-Member -MemberType NoteProperty -Name 'HangfireConnection' -Value "Data Source=$hangfireDbPath;" -Force

    # Ensure Logging section
    if (-not $settings.PSObject.Properties['Logging']) {
        $settings | Add-Member -MemberType NoteProperty -Name 'Logging' -Value ([PSCustomObject]@{})
    }
    if (-not $settings.Logging.PSObject.Properties['LogLevel']) {
        $settings.Logging | Add-Member -MemberType NoteProperty -Name 'LogLevel' -Value ([PSCustomObject]@{
            "Default" = "Information"
            "Microsoft.AspNetCore" = "Warning"
        })
    }
    $settings.Logging | Add-Member -MemberType NoteProperty -Name 'LogsDirectory' -Value $logsDirPath -Force

    # Configure default Kestrel URLs
    if (-not [string]::IsNullOrWhiteSpace($Urls)) {
        $settings | Add-Member -MemberType NoteProperty -Name 'Urls' -Value $Urls -Force
    }

    $settings | ConvertTo-Json -Depth 10 | Set-Content -Path $configFilePath -Encoding UTF8
}

Write-Host "Configured database locations:" -ForegroundColor Green
Write-Host "  - DefaultConnection : Data Source=$dbPath;" -ForegroundColor Gray
Write-Host "  - HangfireConnection: Data Source=$hangfireDbPath;" -ForegroundColor Gray
Write-Host "Configured logs directory:" -ForegroundColor Green
Write-Host "  - LogsDirectory     : $logsDirPath" -ForegroundColor Gray

# 8. Web Launcher Commands & PATH configuration
Write-Host "`nSetting up web launcher commands..." -ForegroundColor Yellow

# In InstallDir:
$installCmd = Join-Path $InstallDir "nightcodeforge-web.cmd"
$installCmdContent = @"
@echo off
setlocal
cd /d "%~dp0"
"%~dp0NightCodeForge.Web.exe" --contentRoot "%~dp0" %*
"@
Set-Content -Path $installCmd -Value $installCmdContent -Encoding ASCII

# In BinDir:
$binCmd = Join-Path $BinDir "nightcodeforge-web.cmd"
$binCmdContent = @"
@echo off
setlocal
cd /d "%~dp0.."
"%~dp0..\NightCodeForge.Web.exe" --contentRoot "%~dp0.." %*
"@
Set-Content -Path $binCmd -Value $binCmdContent -Encoding ASCII

# Short alias ncf-web.cmd in BinDir
$binShortCmd = Join-Path $BinDir "ncf-web.cmd"
$binShortCmdContent = @"
@echo off
setlocal
cd /d "%~dp0.."
"%~dp0..\NightCodeForge.Web.exe" --contentRoot "%~dp0.." %*
"@
Set-Content -Path $binShortCmd -Value $binShortCmdContent -Encoding ASCII

# Bash launcher in BinDir for WSL / Git Bash
$binBash = Join-Path $BinDir "nightcodeforge-web"
$binBashContent = @"
#!/usr/bin/env bash
DIR="`$( cd "`$( dirname "`${BASH_SOURCE[0]}" )" && pwd )"
cd "`$DIR/.."
if [ -f "`$DIR/../NightCodeForge.Web.exe" ]; then
    "`$DIR/../NightCodeForge.Web.exe" --contentRoot "`$DIR/.." "`$@"
elif [ -f "`$DIR/../NightCodeForge.Web" ]; then
    "`$DIR/../NightCodeForge.Web" --contentRoot "`$DIR/.." "`$@"
fi
"@
Set-Content -Path $binBash -Value $binBashContent -Encoding UTF8 -NoNewline

# Idempotent User PATH update for BinDir
$pathAdded = $false
try {
    $userPath = [System.Environment]::GetEnvironmentVariable("Path", [System.EnvironmentVariableTarget]::User)
    $pathParts = if ($userPath) { $userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } } else { @() }
    
    $alreadyInUserPath = $false
    foreach ($entry in $pathParts) {
        if ([string]::Equals($entry.TrimEnd('\/'), $BinDir.TrimEnd('\/'), [System.StringComparison]::OrdinalIgnoreCase)) {
            $alreadyInUserPath = $true
            break
        }
    }

    if (-not $alreadyInUserPath) {
        $newUserPath = if ([string]::IsNullOrWhiteSpace($userPath)) { $BinDir } else { "$userPath;$BinDir" }
        [System.Environment]::SetEnvironmentVariable("Path", $newUserPath, [System.EnvironmentVariableTarget]::User)
        $pathAdded = $true
    }

    # Also update current session PATH
    $currentParts = $env:Path -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $alreadyInProcess = $false
    foreach ($entry in $currentParts) {
        if ([string]::Equals($entry.TrimEnd('\/'), $BinDir.TrimEnd('\/'), [System.StringComparison]::OrdinalIgnoreCase)) {
            $alreadyInProcess = $true
            break
        }
    }
    if (-not $alreadyInProcess) {
        $env:Path = "$env:Path;$BinDir"
    }
}
catch {
    [Console]::Error.WriteLine("Error: Failed to configure user PATH: $($_.Exception.Message)")
    exit 1
}

# 9. Deploy packaged skills to global AppData and optional workspace
$globalSkillsDir = Join-Path $InstallDir "skills"
$skillsSource = Join-Path $repoRoot "skills"
if (Test-Path $skillsSource) {
    Write-Host "`nDeploying skills to global AppData: $globalSkillsDir..." -ForegroundColor Yellow
    if (-not (Test-Path $globalSkillsDir)) {
        New-Item -ItemType Directory -Path $globalSkillsDir -Force | Out-Null
    }
    Copy-Item -Path (Join-Path $skillsSource '*') -Destination $globalSkillsDir -Recurse -Force
}

if (-not [string]::IsNullOrWhiteSpace($WorkspaceDir) -and (Test-Path $WorkspaceDir)) {
    Write-Host "`nDeploying skills to workspace: $WorkspaceDir..." -ForegroundColor Yellow
    $workspaceSkillsDir = Join-Path $WorkspaceDir ".agents\skills"
    if (-not (Test-Path $workspaceSkillsDir)) {
        New-Item -ItemType Directory -Path $workspaceSkillsDir -Force | Out-Null
    }
    Get-ChildItem -Path $skillsSource -Directory | ForEach-Object {
        $targetDir = Join-Path $workspaceSkillsDir $_.Name
        if (Test-Path $targetDir) {
            Remove-Item -Path $targetDir -Recurse -Force
        }
        Copy-Item -Path $_.FullName -Destination $targetDir -Recurse -Force
    }
    Write-Host "Skills deployed to workspace: $workspaceSkillsDir" -ForegroundColor Green
}

# 10. Summary
Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " NightCodeForge Web Publish & Install Successful!          " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "Binary Location : $(Join-Path $InstallDir 'NightCodeForge.Web.exe')" -ForegroundColor Green
Write-Host "Database Folder : $DataDir" -ForegroundColor Green
Write-Host "Logs Folder     : $LogsDir" -ForegroundColor Green
Write-Host "Hosting URL     : $Urls" -ForegroundColor Green
Write-Host "Launch Command  : 'nightcodeforge-web' or 'ncf-web'" -ForegroundColor Green

if ($pathAdded) {
    Write-Host "`nNote: Added '$BinDir' to your user PATH." -ForegroundColor Yellow
    Write-Host "Please open a new terminal window for PATH changes to take effect across your system." -ForegroundColor Yellow
}
