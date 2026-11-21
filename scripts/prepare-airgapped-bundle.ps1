<#
.SYNOPSIS
    Assembles a complete Air-Gapped / Offline local bundle for NightCodeForge.Installer testing and offline deployments.
.DESCRIPTION
    1. Invokes NightCodeAssay\package.ps1, OllamaRouter\package.ps1, and NightCodeForge\package.ps1.
    2. Downloads published Pmad.CSharpNav, Pmad.MarkdownFetch, and Pmad.CodeCondenser .nupkg files from nuget.org.
    3. Generates SHA256SUMS.txt covering all archives in the bundle directory.
.PARAMETER Configuration
    Build configuration (default: Release).
.PARAMETER Runtime
    Target runtime identifier (default: win-x64).
.PARAMETER BundleDir
    Target bundle directory (default: ./artifacts/installer-bundle/bundle).
.PARAMETER NightCodeAssayRepo
    Path to the sibling NightCodeAssay repository (default: ../NightCodeAssay).
.PARAMETER OllamaRouterRepo
    Path to the sibling OllamaRouter repository (default: ../OllamaRouter).
.EXAMPLE
    .\scripts\prepare-airgapped-bundle.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$BundleDir = "./artifacts/installer-bundle/bundle",
    [string]$NightCodeAssayRepo = "",
    [string]$OllamaRouterRepo = ""
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

$global:LASTEXITCODE = 0
& $ncfScript -Configuration $Configuration -Runtime $Runtime -OutputDir $resolvedBundleDir -IncludeReleasedCompanionTools
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
