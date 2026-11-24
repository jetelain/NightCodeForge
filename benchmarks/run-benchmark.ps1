<#
.SYNOPSIS
    Launches a NightCodeForge Crucible benchmark run against one or more models.

.EXAMPLE
    .\benchmarks\run-benchmark.ps1

.EXAMPLE
    .\benchmarks\run-benchmark.ps1 -Models "Flint:latest" -Timeout 1200 -CleanContainers

.EXAMPLE
    .\benchmarks\run-benchmark.ps1 -Category implementation -NoBuild
#>
[CmdletBinding()]
param(
    [string]$Models = "Flint:latest",
    [string]$Suite = "v1-smoke",
    [string]$Dataset = "",
    [string]$Provider = "ollama-router",
    [string]$Endpoint = "http://localhost:11434",
    [int]$Timeout = 1800,
    [int]$Concurrency = 1,
    [string]$Category = "",
    [string]$OutDir = "",
    [switch]$NoVerify,
    [switch]$NoBuild,
    [switch]$CleanContainers
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\NightCodeForge.Benchmark.Cli"

if ([string]::IsNullOrWhiteSpace($Dataset)) {
    $Dataset = Join-Path $env:USERPROFILE ".nightcodeforge\benchmarks\datasets\$Suite.jsonl"
}

if (-not (Test-Path -LiteralPath $Dataset -PathType Leaf)) {
    Write-Error "Benchmark dataset not found: $Dataset"
    exit 1
}

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $safeModel = ($Models -replace '[^A-Za-z0-9._-]+', '-').Trim('-')
    if ([string]::IsNullOrWhiteSpace($safeModel)) {
        $safeModel = "model"
    }
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutDir = Join-Path $env:USERPROFILE ".nightcodeforge\benchmarks\results\$Suite-$safeModel-$timestamp"
}

if ($CleanContainers) {
    Write-Host "[ncf-bench] Cleaning stale ncf-bench Docker containers in WSL..."
    wsl -e sh -c 'docker ps -aq --filter "name=ncf-bench-" | xargs -r docker rm -f'
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to clean stale Docker containers in WSL (exit code $LASTEXITCODE)."
        exit $LASTEXITCODE
    }
}

$dotnetArgs = @("run")
if ($NoBuild) {
    $dotnetArgs += "--no-build"
}

$dotnetArgs += @(
    "--project", $projectPath,
    "-c", "Release",
    "--", "run",
    "--dataset", $Dataset,
    "--suite", $Suite,
    "--provider", $Provider,
    "--endpoint", $Endpoint,
    "--models", $Models,
    "--timeout", "$Timeout",
    "--concurrency", "$Concurrency",
    "--out", $OutDir
)

if (-not $NoVerify) {
    $dotnetArgs += "--verify"
}

if (-not [string]::IsNullOrWhiteSpace($Category)) {
    $dotnetArgs += @("--category", $Category)
}

Write-Output "OUT_DIR=$OutDir"
& dotnet @dotnetArgs
exit $LASTEXITCODE
