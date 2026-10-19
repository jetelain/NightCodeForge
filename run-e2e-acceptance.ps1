<#
.SYNOPSIS
    Executes the NightCodeForge Playwright End-to-End Acceptance Test Suite.
.DESCRIPTION
    Runs the automated 5-step synthetic lifecycle test plan defined in:
    docs/plan/07_PLAYWRIGHT_END_TO_END_ACCEPTANCE_TEST_PLAN.md
.PARAMETER Headed
    Launches Chromium in visible mode (default is headless).
.PARAMETER BaseUrl
    Base URL of the running NightCodeForge Web application (default: http://127.0.0.1:5272).
.PARAMETER KeepArtifacts
    Preserves the ephemeral synthetic Git repository on disk for inspection.
.PARAMETER WorkspaceIdSeed
    Workspace ID seed for E2E tests to avoid ID collisions with real workspaces (default: 1000).
#>
param(
    [switch]$Headed,
    [string]$BaseUrl = "http://127.0.0.1:5272",
    [switch]$KeepArtifacts,
    [int]$WorkspaceIdSeed = 1000
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " NightCodeForge End-to-End Playwright Acceptance Suite    " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

if ($Headed) {
    $env:HEADED = "true"
    Write-Host "[Mode] Headed browser execution enabled." -ForegroundColor Yellow
} else {
    $env:HEADED = "false"
}

if ($KeepArtifacts) {
    $env:KEEP_TEST_ARTIFACTS = "true"
    Write-Host "[Artifacts] Ephemeral test repositories will be kept." -ForegroundColor Yellow
}

$env:NCF_BASE_URL = $BaseUrl
Write-Host "[Target URL] $BaseUrl" -ForegroundColor Gray

if (-not $env:NCF_WORKSPACE_ID_SEED) {
    $env:NCF_WORKSPACE_ID_SEED = $WorkspaceIdSeed.ToString()
}
Write-Host "[Workspace ID Seed] $env:NCF_WORKSPACE_ID_SEED" -ForegroundColor Gray

Write-Host "`n[Prep] Cleaning any stale NightCodeForge processes and test databases..." -ForegroundColor Gray
Get-Process -Name dotnet -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -match "NightCodeForge" } | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item -Path "src/NightCodeForge.Web/nightcodeforge*.db*" -Force -ErrorAction SilentlyContinue

Write-Host "`n[1/2] Ensuring Playwright browser dependencies..." -ForegroundColor Gray
& powershell -ExecutionPolicy Bypass -File tests/NightCodeForge.Web.Tests/bin/Debug/net10.0/playwright.ps1 install chromium

Write-Host "`n[2/2] Running Playwright Acceptance Test Plan..." -ForegroundColor Green
dotnet test tests/NightCodeForge.Web.Tests/NightCodeForge.Web.Tests.csproj `
    --filter "Category=Acceptance" `
    --logger "console;verbosity=normal"

$testExitCode = $LASTEXITCODE
if ($testExitCode -eq 0) {
    Write-Host "`n==========================================================" -ForegroundColor Green
    Write-Host " Acceptance Test Passed: 100% Synthetic Lifecycle Verified!" -ForegroundColor Green
    Write-Host "==========================================================" -ForegroundColor Green
} else {
    Write-Host "`n==========================================================" -ForegroundColor Red
    Write-Host " Acceptance Test Failed! Exit code: $testExitCode" -ForegroundColor Red
    Write-Host " Check artifacts/videos for Playwright recording traces." -ForegroundColor Red
    Write-Host "==========================================================" -ForegroundColor Red
}

exit $testExitCode
