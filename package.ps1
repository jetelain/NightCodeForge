<#
.SYNOPSIS
    Packages NightCodeForge Web archive, Native AOT Installer, and NightCodeForge.Cli NuGet tool into an output directory.
.DESCRIPTION
    Builds and packages the unreleased NightCodeForge release artifacts for local air-gapped bundles or offline testing:
      1. Packs src/NightCodeForge.Cli/NightCodeForge.Cli.csproj as NightCodeForge.Cli.<version>.nupkg.
      2. Publishes NightCodeForge.Web (--self-contained) + Native AOT NightCodeForge.Launcher staged to match
         the publish.ps1 installation layout (nightcodeforge-web(.exe) at root, bin/ncf-web(.exe),
         bin/nightcodeforge-web(.exe), plus bin/nightcodeforge-web bash shim on Windows), portable
         appsettings.Production.json, skills/, VERSION, README.md, and LICENSE, and compresses into
         NightCodeForge-Web-<Runtime>.zip.
      3. Unless -SkipInstaller is specified, publishes Native AOT NightCodeForge.Installer into
         NightCodeForge-Setup-<Runtime>.zip in -OutputDir (or unpacks the executable and native runtime
         libraries directly into -InstallerOutputDir when specified).
      4. Optionally (-IncludeReleasedCompanionTools) downloads published Pmad.CSharpNav, Pmad.MarkdownFetch, and Pmad.CodeCondenser
         .nupkg files from nuget.org into -OutputDir.

    Full Air-Gapped Bundle Assembly Example:
      $bundleDir = ".\artifacts\installer-bundle\bundle"
      New-Item -ItemType Directory -Force -Path $bundleDir | Out-Null
      & ..\NightCodeAssay\package.ps1 -OutputDir $bundleDir
      & ..\OllamaRouter\package.ps1 -OutputDir $bundleDir
      & .\package.ps1 -OutputDir $bundleDir -InstallerOutputDir (Split-Path -Parent $bundleDir) -IncludeReleasedCompanionTools
.PARAMETER Configuration
    Build configuration (default: Release).
.PARAMETER Runtime
    Target runtime identifier (default: win-x64).
.PARAMETER Version
    Optional explicit package/archive version override. Defaults to <Version> in NightCodeForge.Web.csproj.
.PARAMETER OutputDir
    Destination directory for generated artifacts (default: ./artifacts).
.PARAMETER InstallerOutputDir
    Optional directory to receive the unpacked Native AOT NightCodeForge.Installer executable and runtime libraries instead of creating NightCodeForge-Setup-<Runtime>.zip in -OutputDir.
.PARAMETER SkipInstaller
    Skips publishing Native AOT NightCodeForge.Installer.
.PARAMETER IncludeReleasedCompanionTools
    Downloads Pmad.CSharpNav, Pmad.MarkdownFetch, and Pmad.CodeCondenser .nupkg files from nuget.org into OutputDir.
.EXAMPLE
    .\package.ps1
.EXAMPLE
    .\package.ps1 -Runtime linux-x64 -OutputDir .\artifacts\bundle -IncludeReleasedCompanionTools
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "",
    [string]$OutputDir = "./artifacts",
    [string]$InstallerOutputDir = "",
    [switch]$SkipInstaller,
    [switch]$IncludeReleasedCompanionTools
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$webProjectPath = Join-Path $repoRoot "src\NightCodeForge.Web\NightCodeForge.Web.csproj"
$launcherProjectPath = Join-Path $repoRoot "src\NightCodeForge.Launcher\NightCodeForge.Launcher.csproj"
$cliProjectPath = Join-Path $repoRoot "src\NightCodeForge.Cli\NightCodeForge.Cli.csproj"
$installerProjectPath = Join-Path $repoRoot "src\NightCodeForge.Installer\NightCodeForge.Installer.csproj"

if (-not (Test-Path $webProjectPath)) { throw "NightCodeForge.Web project not found: $webProjectPath" }
if (-not (Test-Path $launcherProjectPath)) { throw "NightCodeForge.Launcher project not found: $launcherProjectPath" }
if (-not (Test-Path $cliProjectPath)) { throw "NightCodeForge.Cli project not found: $cliProjectPath" }
if (-not $SkipInstaller -and -not (Test-Path $installerProjectPath)) { throw "NightCodeForge.Installer project not found: $installerProjectPath" }

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$webXml = Get-Content $webProjectPath
    $xmlVersion = @($webXml.Project.PropertyGroup.Version | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)
    $Version = if ($xmlVersion.Count -gt 0) { $xmlVersion[0].Trim() } else { "1.0.0" }
}

$resolvedOutputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)
New-Item -ItemType Directory -Path $resolvedOutputDir -Force | Out-Null

$isWindowsRid = $Runtime -like "win-*"
$webBinaryName = if ($isWindowsRid) { "NightCodeForge.Web.exe" } else { "NightCodeForge.Web" }
$launcherBinaryName = if ($isWindowsRid) { "ncf-web.exe" } else { "ncf-web" }
$longLauncherBinaryName = if ($isWindowsRid) { "nightcodeforge-web.exe" } else { "nightcodeforge-web" }
$installerBinaryName = if ($isWindowsRid) { "NightCodeForge.Installer.exe" } else { "NightCodeForge.Installer" }
$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ncf-pkg-" + [System.Guid]::NewGuid().ToString("N"))
$webStagingDir = Join-Path $stagingRoot "web"
$webBinDir = Join-Path $webStagingDir "bin"
$launcherStagingDir = Join-Path $stagingRoot "launcher"
$installerStagingDir = Join-Path $stagingRoot "setup"

try {
    New-Item -ItemType Directory -Path $webStagingDir -Force | Out-Null
    New-Item -ItemType Directory -Path $webBinDir -Force | Out-Null
    New-Item -ItemType Directory -Path $launcherStagingDir -Force | Out-Null

    Write-Host "Packing NightCodeForge.Cli ($Version) -> $resolvedOutputDir..." -ForegroundColor Cyan
    & dotnet pack $cliProjectPath -c $Configuration "-p:Version=$Version" -o $resolvedOutputDir --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack for NightCodeForge.Cli failed with exit code $LASTEXITCODE."
    }

    Write-Host "Publishing NightCodeForge.Web ($Runtime, $Version)..." -ForegroundColor Cyan
    & dotnet publish $webProjectPath `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        "-p:Version=$Version" `
        -o $webStagingDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish for NightCodeForge.Web failed with exit code $LASTEXITCODE."
    }

    Write-Host "Publishing Native AOT NightCodeForge.Launcher ($Runtime, $Version)..." -ForegroundColor Cyan
    & dotnet publish $launcherProjectPath `
        -c $Configuration `
        -r $Runtime `
        -p:PublishAot=true `
        -p:StripSymbols=true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        "-p:Version=$Version" `
        -o $launcherStagingDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish for NightCodeForge.Launcher failed with exit code $LASTEXITCODE."
    }

    $stagedLauncher = Join-Path $launcherStagingDir $launcherBinaryName
    if (-not (Test-Path $stagedLauncher)) {
        throw "Published launcher binary was not found at: $stagedLauncher"
    }

    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)

    # Match publish.ps1 layout: nightcodeforge-web(.exe) at root, bin/nightcodeforge-web(.exe) and bin/ncf-web(.exe) in bin/.
    Copy-Item -Path $stagedLauncher -Destination (Join-Path $webStagingDir $longLauncherBinaryName) -Force
    Copy-Item -Path $stagedLauncher -Destination (Join-Path $webBinDir $longLauncherBinaryName) -Force
    Copy-Item -Path $stagedLauncher -Destination (Join-Path $webBinDir $launcherBinaryName) -Force

    if ($isWindowsRid) {
        $binBash = Join-Path $webBinDir "nightcodeforge-web"
        $binBashContent = @"
#!/usr/bin/env bash
DIR="`$( cd "`$( dirname "`${BASH_SOURCE[0]}" )" && pwd )"
exec "`$DIR/$launcherBinaryName" "`$@"
"@
        $binBashContent = ($binBashContent -replace "`r`n", "`n").TrimEnd("`n") + "`n"
        [System.IO.File]::WriteAllText($binBash, $binBashContent, $utf8NoBom)
    } elseif (-not [System.OperatingSystem]::IsWindows()) {
        foreach ($exePath in @(
            (Join-Path $webStagingDir $webBinaryName),
            (Join-Path $webStagingDir $longLauncherBinaryName),
            (Join-Path $webBinDir $longLauncherBinaryName),
            (Join-Path $webBinDir $launcherBinaryName)
        )) {
            if (Test-Path $exePath) {
                & chmod +x $exePath
            }
        }
    }

    # Portable appsettings.Production.json matching publish.ps1 schema with relative data/logs paths.
    $prodAppSettings = @"
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=data/nightcodeforge.db;",
    "HangfireConnection": "Data Source=data/nightcodeforge_hangfire.db;"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "NightCodeForge": "Information"
    },
    "LogsDirectory": "logs"
  },
  "Urls": "http://localhost:5270"
}
"@
    $prodAppSettings = ($prodAppSettings -replace "`r`n", "`n").TrimEnd("`n") + "`n"
    [System.IO.File]::WriteAllText((Join-Path $webStagingDir "appsettings.Production.json"), $prodAppSettings, $utf8NoBom)

    # Strip PDBs and copy skills/ + metadata
    Get-ChildItem -Path $webStagingDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    $skillsSource = Join-Path $repoRoot "skills"
    if (Test-Path $skillsSource) {
        $skillsDest = Join-Path $webStagingDir "skills"
        New-Item -ItemType Directory -Path $skillsDest -Force | Out-Null
        Copy-Item -Path (Join-Path $skillsSource "*") -Destination $skillsDest -Recurse -Force
    }

    [System.IO.File]::WriteAllText((Join-Path $webStagingDir "VERSION"), "$Version`n", $utf8NoBom)
    Copy-Item -Path (Join-Path $repoRoot "README.md") -Destination $webStagingDir -Force
    Copy-Item -Path (Join-Path $repoRoot "LICENSE") -Destination $webStagingDir -Force

    $archivePath = Join-Path $resolvedOutputDir "NightCodeForge-Web-$Runtime.zip"
    if (Test-Path $archivePath) {
        Remove-Item -Path $archivePath -Force
    }

    Write-Host "Creating archive $archivePath..." -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $webStagingDir "*") -DestinationPath $archivePath -Force

    if (-not $SkipInstaller) {
        New-Item -ItemType Directory -Path $installerStagingDir -Force | Out-Null

        Write-Host "Publishing Native AOT NightCodeForge.Installer ($Runtime, $Version)..." -ForegroundColor Cyan
        & dotnet publish $installerProjectPath `
            -c $Configuration `
            -r $Runtime `
            -p:PublishAot=true `
            -p:StripSymbols=true `
            -p:DebugType=none `
            -p:DebugSymbols=false `
            "-p:Version=$Version" `
            -o $installerStagingDir
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish for NightCodeForge.Installer failed with exit code $LASTEXITCODE."
        }

        $stagedInstaller = Join-Path $installerStagingDir $installerBinaryName
        if (-not (Test-Path $stagedInstaller)) {
            throw "Published installer binary was not found at: $stagedInstaller"
        }

        Get-ChildItem -Path $installerStagingDir -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in @('.pdb', '.dbg') } |
            Remove-Item -Force -ErrorAction SilentlyContinue

        if (-not $isWindowsRid -and -not [System.OperatingSystem]::IsWindows()) {
            & chmod +x $stagedInstaller
        }

        Copy-Item -Path (Join-Path $repoRoot "README.md") -Destination $installerStagingDir -Force
        Copy-Item -Path (Join-Path $repoRoot "LICENSE") -Destination $installerStagingDir -Force

        if (-not [string]::IsNullOrWhiteSpace($InstallerOutputDir)) {
            $resolvedInstallerOutputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallerOutputDir)
            New-Item -ItemType Directory -Path $resolvedInstallerOutputDir -Force | Out-Null
            Write-Host "Copying NightCodeForge.Installer ($Runtime) -> $resolvedInstallerOutputDir..." -ForegroundColor Cyan
            Copy-Item -Path (Join-Path $installerStagingDir "*") -Destination $resolvedInstallerOutputDir -Recurse -Force
        } else {
            $setupArchivePath = Join-Path $resolvedOutputDir "NightCodeForge-Setup-$Runtime.zip"
            if (Test-Path $setupArchivePath) {
                Remove-Item -Path $setupArchivePath -Force
            }
            Write-Host "Creating archive $setupArchivePath..." -ForegroundColor Cyan
            Compress-Archive -Path (Join-Path $installerStagingDir "*") -DestinationPath $setupArchivePath -Force
        }
    }

    if ($IncludeReleasedCompanionTools) {
        $tlsProtocol = [Net.SecurityProtocolType]::Tls12
        if ([Enum]::GetNames([Net.SecurityProtocolType]) -contains 'Tls13') {
            $tlsProtocol = $tlsProtocol -bor [Net.SecurityProtocolType]::Tls13
        }
        [Net.ServicePointManager]::SecurityProtocol = $tlsProtocol
        foreach ($pkg in @("Pmad.CSharpNav", "Pmad.MarkdownFetch", "Pmad.CodeCondenser")) {
            $idLower = $pkg.ToLowerInvariant()
            Write-Host "Downloading latest $pkg from nuget.org..." -ForegroundColor Cyan
            $index = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$idLower/index.json"
            $stableVersions = @($index.versions | Where-Object { $_ -notlike "*-*" })
            $latest = if ($stableVersions.Count -gt 0) { $stableVersions[-1] } else { $index.versions[-1] }
            $url = "https://api.nuget.org/v3-flatcontainer/$idLower/$latest/$idLower.$latest.nupkg"
            $outFile = Join-Path $resolvedOutputDir "$pkg.$latest.nupkg"
            Invoke-WebRequest -Uri $url -OutFile $outFile -UseBasicParsing
        }
    }

    Write-Host "NightCodeForge packaging complete in: $resolvedOutputDir" -ForegroundColor Green
}
finally {
    if (Test-Path $stagingRoot) {
        Remove-Item -Path $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
