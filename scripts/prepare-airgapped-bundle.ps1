<#
.SYNOPSIS
    Assembles a complete Air-Gapped / Offline local bundle and runnable Native AOT installer for NightCodeForge.Installer testing and offline deployments.
.DESCRIPTION
    1. Invokes NightCodeAssay\package.ps1, OllamaRouter\package.ps1, and NightCodeForge\package.ps1.
    2. Places the Native AOT NightCodeForge.Installer executable (and runtime libraries) alongside the bundle/ directory unless -SkipInstaller is set.
    3. Downloads published Pmad.CSharpNav, Pmad.MarkdownFetch, and Pmad.CodeCondenser .nupkg files from nuget.org.
    4. Generates SHA256SUMS.txt covering all archives in the bundle directory.
.PARAMETER Configuration
    Build configuration (default: Release).
.PARAMETER Runtime
    Target runtime identifier (default: win-x64).
.PARAMETER BundleDir
    Target bundle directory (default: ./artifacts/installer-bundle/bundle).
.PARAMETER InstallerDir
    Destination directory for the runnable Native AOT NightCodeForge.Installer executable. Defaults to the parent directory when BundleDir ends with 'bundle', otherwise BundleDir.
.PARAMETER NightCodeAssayRepo
    Path to the sibling NightCodeAssay repository (default: ../NightCodeAssay).
.PARAMETER OllamaRouterRepo
    Path to the sibling OllamaRouter repository (default: ../OllamaRouter).
.PARAMETER SkipInstaller
    Skips building and copying Native AOT NightCodeForge.Installer.
.EXAMPLE
    .\scripts\prepare-airgapped-bundle.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$BundleDir = "./artifacts/installer-bundle/bundle",
    [string]$InstallerDir = "",
    [string]$NightCodeAssayRepo = "",
    [string]$OllamaRouterRepo = "",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

$ncfRepo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($NightCodeAssayRepo)) {
    $NightCodeAssayRepo = Join-Path (Split-Path -Parent $ncfRepo) "NightCodeAssay"
}
if ([string]::IsNullOrWhiteSpace($OllamaRouterRepo)) {
    $OllamaRouterRepo = Join-Path (Split-Path -Parent $ncfRepo) "OllamaRouter"
}

$resolvedBundleDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BundleDir)
New-Item -ItemType Directory -Path $resolvedBundleDir -Force | Out-Null

$resolvedInstallerDir = ""
if (-not $SkipInstaller) {
    if ([string]::IsNullOrWhiteSpace($InstallerDir)) {
        $bundleLeaf = Split-Path -Leaf $resolvedBundleDir
        $resolvedInstallerDir = if ([string]::Equals($bundleLeaf, "bundle", [System.StringComparison]::OrdinalIgnoreCase)) {
            Split-Path -Parent $resolvedBundleDir
        } else {
            $resolvedBundleDir
        }
    } else {
        $resolvedInstallerDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallerDir)
    }
    New-Item -ItemType Directory -Path $resolvedInstallerDir -Force | Out-Null
}

$assayScript = Join-Path $NightCodeAssayRepo "package.ps1"
$routerScript = Join-Path $OllamaRouterRepo "package.ps1"
$ncfScript = Join-Path $ncfRepo "package.ps1"

foreach ($script in @($assayScript, $routerScript, $ncfScript)) {
    if (-not (Test-Path $script)) {
        throw "Required packaging script not found: $script"
    }
}

Write-Host "Assembling air-gapped bundle in: $resolvedBundleDir" -ForegroundColor Cyan

$global:LASTEXITCODE = 0
& $assayScript -Configuration $Configuration -Runtime $Runtime -OutputDir $resolvedBundleDir
if ($LASTEXITCODE -ne 0) { throw "NightCodeAssay packaging failed with exit code $LASTEXITCODE." }

$global:LASTEXITCODE = 0
& $routerScript -Configuration $Configuration -Runtime $Runtime -OutputDir $resolvedBundleDir
if ($LASTEXITCODE -ne 0) { throw "OllamaRouter packaging failed with exit code $LASTEXITCODE." }

$ncfParams = @{
    Configuration = $Configuration
    Runtime = $Runtime
    OutputDir = $resolvedBundleDir
    IncludeReleasedCompanionTools = $true
}
if ($SkipInstaller) {
    $ncfParams["SkipInstaller"] = $true
} else {
    $ncfParams["InstallerOutputDir"] = $resolvedInstallerDir
}

$global:LASTEXITCODE = 0
& $ncfScript @ncfParams
if ($LASTEXITCODE -ne 0) { throw "NightCodeForge packaging failed with exit code $LASTEXITCODE." }

$sumLines = @()
Get-ChildItem -Path $resolvedBundleDir -File | Where-Object {
    $_.Name -like "*.zip" -or $_.Name -like "*.tar.gz"
} | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $sumLines += "$hash  $($_.Name)"
}

$sumsPath = Join-Path $resolvedBundleDir "SHA256SUMS.txt"
$sumsContent = if ($sumLines.Count -gt 0) { ($sumLines -join "`n") + "`n" } else { "" }
[System.IO.File]::WriteAllText($sumsPath, $sumsContent, [System.Text.UTF8Encoding]::new($false))

Write-Host "Air-gapped bundle ready with SHA256SUMS.txt at: $resolvedBundleDir" -ForegroundColor Green
if (-not $SkipInstaller) {
    $installerBinaryName = if ($Runtime -like "win-*") { "NightCodeForge.Installer.exe" } else { "NightCodeForge.Installer" }
    Write-Host "Native AOT installer ready to launch at: $(Join-Path $resolvedInstallerDir $installerBinaryName)" -ForegroundColor Green
}
