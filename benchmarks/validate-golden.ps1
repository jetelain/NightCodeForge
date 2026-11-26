<#
.SYNOPSIS
    Revalidates NightCodeForge benchmark golden patches, CodeReview fixtures, and TaskReview fixtures.

.DESCRIPTION
    Builds NightCodeForge.Benchmark.Cli once (unless -NoBuild is specified) and invokes
    `ncf-bench dataset validate` in configurable batches (default: 1 instance per batch) with
    incremental persistence of validation.log, summary.json, and summary.md.

    Supports filtering by category, language, or instance ID, fast patch/frontmatter-only
    checks (-NoVerify), stale WSL container cleanup (-CleanContainers), and resuming interrupted
    or partially failing runs (-Resume).

.EXAMPLE
    .\benchmarks\validate-golden.ps1 -Concurrency 4 -CleanContainers

.EXAMPLE
    .\benchmarks\validate-golden.ps1 -Suite v1-smoke -Concurrency 4 -CleanContainers

.EXAMPLE
    .\benchmarks\validate-golden.ps1 -Language python,rust -CleanContainers

.EXAMPLE
    .\benchmarks\validate-golden.ps1 -Suite v1-full -NoVerify

.EXAMPLE
    .\benchmarks\validate-golden.ps1 -Resume "$env:USERPROFILE\.nightcodeforge\benchmarks\validations\v1-full-20261126-120000"
#>
[CmdletBinding()]
param(
    [string]$Suite = "v1-full",
    [string]$Dataset = "",
    [string]$Manifest = "",
    [string]$Category = "",
    [string]$Language = "",
    [string]$Filter = "",
    [int]$Limit = 0,
    [int]$BatchSize = 1,
    [int]$Concurrency = 1,
    [string]$WorkspacesDir = "",
    [switch]$NoWslWorkspaces,
    [string]$OutDir = "",
    [string]$Resume = "",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$NoVerify,
    [switch]$NoBuild,
    [switch]$CleanContainers,
    [switch]$StopOnFailure
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Get-JsonPropertyValue {
    param(
        [object]$Object,
        [string]$Name,
        [object]$Default = $null
    )

    if ($null -eq $Object) {
        return $Default
    }

    $prop = $Object.PSObject.Properties[$Name]
    if ($null -eq $prop -or $null -eq $prop.Value) {
        return $Default
    }

    return $prop.Value
}

function Normalize-CategoryName {
    param([string]$RawCategory)

    if ([string]::IsNullOrWhiteSpace($RawCategory)) {
        return @{ Cli = ""; Canonical = "" }
    }

    switch ($RawCategory.Trim().ToLowerInvariant()) {
        "implementation" { return @{ Cli = "implementation"; Canonical = "Implementation" } }
        "code-review"    { return @{ Cli = "code-review"; Canonical = "CodeReview" } }
        "codereview"     { return @{ Cli = "code-review"; Canonical = "CodeReview" } }
        "task-review"    { return @{ Cli = "task-review"; Canonical = "TaskReview" } }
        "taskreview"     { return @{ Cli = "task-review"; Canonical = "TaskReview" } }
        default {
            throw "Unsupported -Category '$RawCategory'. Valid values: implementation, code-review, task-review (or Implementation, CodeReview, TaskReview)."
        }
    }
}

function Split-CommaSeparatedValues {
    param([string]$RawValue)

    if ([string]::IsNullOrWhiteSpace($RawValue)) {
        return @()
    }

    $items = New-Object System.Collections.Generic.List[string]
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($part in ($RawValue -split ',')) {
        $trimmed = $part.Trim()
        if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
            if ($seen.Add($trimmed)) {
                $items.Add($trimmed)
            }
        }
    }

    return @($items.ToArray())
}

function Format-DurationText {
    param([long]$DurationMs)

    if ($DurationMs -lt 1000) {
        return "$DurationMs ms"
    }

    $ts = [TimeSpan]::FromMilliseconds([double]$DurationMs)
    if ($ts.TotalHours -ge 1.0) {
        return "{0:0}h {1:00}m {2:00}s" -f [Math]::Floor($ts.TotalHours), $ts.Minutes, $ts.Seconds
    }

    if ($ts.TotalMinutes -ge 1.0) {
        return "{0:0}m {1:00}s" -f [Math]::Floor($ts.TotalMinutes), $ts.Seconds
    }

    return "{0:0.0}s" -f $ts.TotalSeconds
}

function Write-Utf8Atomic {
    param(
        [string]$Path,
        [string]$Content
    )

    $tempPath = "$Path.tmp"
    [System.IO.File]::WriteAllText($tempPath, $Content, $utf8NoBom)
    Move-Item -LiteralPath $tempPath -Destination $Path -Force
}

function Append-Utf8Lines {
    param(
        [string]$Path,
        [string[]]$Lines
    )

    if ($null -eq $Lines -or $Lines.Count -eq 0) {
        return
    }

    $text = ($Lines -join [Environment]::NewLine) + [Environment]::NewLine
    [System.IO.File]::AppendAllText($Path, $text, $utf8NoBom)
}

function Escape-MarkdownCell {
    param([string]$Value)

    if ($null -eq $Value) {
        return ""
    }

    return ($Value -replace '\|', '\|' -replace "`r?`n", ' ')
}

function New-GroupBreakdown {
    param(
        [object[]]$OrderedResults,
        [string]$GroupProperty
    )

    $groups = @{}
    $order = New-Object System.Collections.Generic.List[string]

    foreach ($r in $OrderedResults) {
        $key = [string](Get-JsonPropertyValue -Object $r -Name $GroupProperty -Default "")
        if ([string]::IsNullOrWhiteSpace($key)) {
            $key = "unknown"
        }

        if (-not $groups.ContainsKey($key)) {
            $groups[$key] = @{
                Key     = $key
                Passed  = 0
                Failed  = 0
                Pending = 0
                Total   = 0
            }
            $order.Add($key)
        }

        $g = $groups[$key]
        $g.Total++
        switch ($r.status) {
            "Passed"  { $g.Passed++ }
            "Failed"  { $g.Failed++ }
            default   { $g.Pending++ }
        }
    }

    $output = New-Object System.Collections.Generic.List[object]
    foreach ($k in $order) {
        $output.Add([pscustomobject]$groups[$k])
    }

    return @($output.ToArray())
}

function Format-RerunCommand {
    param(
        [string]$SuiteName,
        [string]$DatasetPath,
        [string]$ManifestPath,
        [bool]$UseDatasetFlag,
        [bool]$ManifestExplicit,
        [bool]$VerifyWithTests,
        [string]$FilterCsv = "",
        [string]$ResumeDir = "",
        [int]$Concurrency = 1
    )

    $parts = New-Object System.Collections.Generic.List[string]
    $parts.Add(".\benchmarks\validate-golden.ps1")

    if (-not [string]::IsNullOrWhiteSpace($ResumeDir)) {
        $parts.Add("-Resume `"$ResumeDir`"")
    }
    else {
        if ($UseDatasetFlag -or [string]::IsNullOrWhiteSpace($SuiteName)) {
            $parts.Add("-Dataset `"$DatasetPath`"")
        }
        else {
            $parts.Add("-Suite $SuiteName")
        }

        if ($ManifestExplicit -and -not [string]::IsNullOrWhiteSpace($ManifestPath)) {
            $parts.Add("-Manifest `"$ManifestPath`"")
        }

        if (-not [string]::IsNullOrWhiteSpace($FilterCsv)) {
            $parts.Add("-Filter `"$FilterCsv`"")
        }
    }

    if ($Concurrency -gt 1) {
        $parts.Add("-Concurrency $Concurrency")
    }

    if (-not $VerifyWithTests) {
        $parts.Add("-NoVerify")
    }
    else {
        $parts.Add("-CleanContainers")
    }

    return ($parts -join " ")
}

function Write-ValidationSummaries {
    param(
        [string]$JsonPath,
        [string]$MarkdownPath,
        [string]$SuiteName,
        [string]$DatasetPath,
        [string]$ManifestPath,
        [bool]$UseDatasetFlag,
        [bool]$ManifestExplicit,
        [bool]$VerifyWithTests,
        [string]$BuildConfiguration,
        [string]$StartedUtc,
        [string]$OutDirectory,
        [object[]]$SelectedInstances,
        [hashtable]$ResultsById,
        [int]$Concurrency = 1
    )

    $orderedResults = New-Object System.Collections.Generic.List[object]
    $passedCount = 0
    $failedCount = 0
    $pendingCount = 0
    $resumedSkippedCount = 0
    $totalDurationMs = [long]0

    foreach ($inst in $SelectedInstances) {
        if ($ResultsById.ContainsKey($inst.instance_id)) {
            $r = $ResultsById[$inst.instance_id]
            $dur = [long](Get-JsonPropertyValue -Object $r -Name "duration_ms" -Default 0)
            $isResumed = [bool](Get-JsonPropertyValue -Object $r -Name "resumed" -Default $false)
            $totalDurationMs += $dur

            if ($r.status -eq "Passed") {
                $passedCount++
                if ($isResumed) {
                    $resumedSkippedCount++
                }
            }
            elseif ($r.status -eq "Failed") {
                $failedCount++
            }
            else {
                $pendingCount++
            }

            $orderedResults.Add([pscustomobject][ordered]@{
                instance_id = $inst.instance_id
                category    = [string](Get-JsonPropertyValue -Object $r -Name "category" -Default $inst.category)
                language    = [string](Get-JsonPropertyValue -Object $r -Name "language" -Default $inst.language)
                repo        = [string](Get-JsonPropertyValue -Object $r -Name "repo" -Default $inst.repo)
                status      = [string]$r.status
                duration_ms = $dur
                resumed     = $isResumed
                detail      = [string](Get-JsonPropertyValue -Object $r -Name "detail" -Default "")
            })
        }
        else {
            $pendingCount++
            $orderedResults.Add([pscustomobject][ordered]@{
                instance_id = $inst.instance_id
                category    = $inst.category
                language    = $inst.language
                repo        = $inst.repo
                status      = "Pending"
                duration_ms = [long]0
                resumed     = $false
                detail      = ""
            })
        }
    }

    $resultsArray = @($orderedResults.ToArray())
    $updatedUtc = (Get-Date).ToUniversalTime().ToString("o")

    $summaryObject = [ordered]@{
        suite             = $SuiteName
        dataset           = $DatasetPath
        manifest          = $ManifestPath
        verify_with_tests = $VerifyWithTests
        concurrency       = $Concurrency
        configuration     = $BuildConfiguration
        started_utc       = $StartedUtc
        updated_utc       = $updatedUtc
        total_selected    = $SelectedInstances.Count
        passed            = $passedCount
        failed            = $failedCount
        pending           = $pendingCount
        resumed_skipped   = $resumedSkippedCount
        total_duration_ms = $totalDurationMs
        results           = $resultsArray
    }

    $jsonContent = $summaryObject | ConvertTo-Json -Depth 6
    Write-Utf8Atomic -Path $JsonPath -Content $jsonContent

    $md = New-Object System.Text.StringBuilder
    [void]$md.AppendLine("# Golden Patch Validation Summary ($SuiteName)")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("- **Suite:** ``$SuiteName``")
    [void]$md.AppendLine("- **Dataset:** ``$DatasetPath``")
    if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
        [void]$md.AppendLine("- **Manifest:** ``$ManifestPath``")
    }
    $modeLabel = if ($VerifyWithTests) { "Full containerized verification (--verify)" } else { "Patch & frontmatter only (--no-verify)" }
    [void]$md.AppendLine("- **Mode:** $modeLabel")
    [void]$md.AppendLine("- **Concurrency:** $Concurrency")
    [void]$md.AppendLine("- **Started (UTC):** $StartedUtc")
    [void]$md.AppendLine("- **Updated (UTC):** $updatedUtc")
    [void]$md.AppendLine("- **Total Duration:** $(Format-DurationText -DurationMs $totalDurationMs)")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("## Scorecard")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("| Metric | Count |")
    [void]$md.AppendLine("| --- | ---: |")
    [void]$md.AppendLine("| Selected | $($SelectedInstances.Count) |")
    [void]$md.AppendLine("| Passed | $passedCount |")
    [void]$md.AppendLine("| Failed | $failedCount |")
    [void]$md.AppendLine("| Pending | $pendingCount |")
    [void]$md.AppendLine("| Resumed (skipped) | $resumedSkippedCount |")
    [void]$md.AppendLine("")

    $byCategory = @(New-GroupBreakdown -OrderedResults $resultsArray -GroupProperty "category")
    [void]$md.AppendLine("## Breakdown by Category")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("| Category | Passed | Failed | Pending | Total |")
    [void]$md.AppendLine("| --- | ---: | ---: | ---: | ---: |")
    foreach ($g in $byCategory) {
        [void]$md.AppendLine("| $($g.Key) | $($g.Passed) | $($g.Failed) | $($g.Pending) | $($g.Total) |")
    }
    [void]$md.AppendLine("")

    $byLanguage = @(New-GroupBreakdown -OrderedResults $resultsArray -GroupProperty "language")
    [void]$md.AppendLine("## Breakdown by Language")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("| Language | Passed | Failed | Pending | Total |")
    [void]$md.AppendLine("| --- | ---: | ---: | ---: | ---: |")
    foreach ($g in $byLanguage) {
        [void]$md.AppendLine("| $($g.Key) | $($g.Passed) | $($g.Failed) | $($g.Pending) | $($g.Total) |")
    }
    [void]$md.AppendLine("")

    $failedResults = @($resultsArray | Where-Object { $_.status -eq "Failed" })
    if ($failedResults.Count -gt 0) {
        $failedIds = @($failedResults | ForEach-Object { $_.instance_id }) -join ","
        [void]$md.AppendLine("## Failures ($($failedResults.Count))")
        [void]$md.AppendLine("")
        [void]$md.AppendLine("| Instance ID | Category | Language | Duration | Error |")
        [void]$md.AppendLine("| --- | --- | --- | ---: | --- |")
        foreach ($f in $failedResults) {
            [void]$md.AppendLine("| ``$($f.instance_id)`` | $($f.category) | $($f.language) | $(Format-DurationText -DurationMs $f.duration_ms) | $(Escape-MarkdownCell -Value $f.detail) |")
        }
        [void]$md.AppendLine("")
        $rerunFailedCmd = Format-RerunCommand `
            -SuiteName $SuiteName `
            -DatasetPath $DatasetPath `
            -ManifestPath $ManifestPath `
            -UseDatasetFlag $UseDatasetFlag `
            -ManifestExplicit $ManifestExplicit `
            -VerifyWithTests $VerifyWithTests `
            -FilterCsv $failedIds `
            -Concurrency $Concurrency
        $resumeCmd = Format-RerunCommand `
            -SuiteName $SuiteName `
            -DatasetPath $DatasetPath `
            -ManifestPath $ManifestPath `
            -UseDatasetFlag $UseDatasetFlag `
            -ManifestExplicit $ManifestExplicit `
            -VerifyWithTests $VerifyWithTests `
            -ResumeDir $OutDirectory `
            -Concurrency $Concurrency

        [void]$md.AppendLine("Rerun only failed instances:")
        [void]$md.AppendLine("")
        [void]$md.AppendLine('```powershell')
        [void]$md.AppendLine($rerunFailedCmd)
        [void]$md.AppendLine('```')
        [void]$md.AppendLine("")
        [void]$md.AppendLine("Or resume this validation directory (skips already-passed instances):")
        [void]$md.AppendLine("")
        [void]$md.AppendLine('```powershell')
        [void]$md.AppendLine($resumeCmd)
        [void]$md.AppendLine('```')
        [void]$md.AppendLine("")
    }

    [void]$md.AppendLine("## Instance Results")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("| Instance ID | Category | Language | Status | Duration | Notes |")
    [void]$md.AppendLine("| --- | --- | --- | --- | ---: | --- |")
    foreach ($r in $resultsArray) {
        $statusLabel = if ($r.resumed) { "$($r.status) (resumed)" } else { $r.status }
        [void]$md.AppendLine("| ``$($r.instance_id)`` | $($r.category) | $($r.language) | $statusLabel | $(Format-DurationText -DurationMs $r.duration_ms) | $(Escape-MarkdownCell -Value $r.detail) |")
    }

    Write-Utf8Atomic -Path $MarkdownPath -Content $md.ToString()

    return [pscustomobject]@{
        Passed         = $passedCount
        Failed         = $failedCount
        Pending        = $pendingCount
        ResumedSkipped = $resumedSkippedCount
        Total          = $SelectedInstances.Count
        DurationMs     = $totalDurationMs
        ByCategory     = $byCategory
        ByLanguage     = $byLanguage
        FailedResults  = $failedResults
    }
}

function Invoke-ValidateBatch {
    param(
        [string]$CliDllPath,
        [string[]]$CliArgs,
        [string]$LogFilePath
    )

    $outputLines = New-Object System.Collections.Generic.List[string]
    $exitCode = 0
    $prevErrorAction = $ErrorActionPreference

    try {
        # PowerShell 5.1 converts native stderr lines under `2>&1` into ErrorRecords that abort
        # when ErrorActionPreference is "Stop". Capture and inspect $LASTEXITCODE explicitly instead.
        $ErrorActionPreference = "Continue"
        & dotnet $CliDllPath @CliArgs 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) {
                $line = $_.ToString()
            }
            else {
                $line = [string]$_
            }

            $outputLines.Add($line)
            if ($line -match '^\[PASS\]') {
                Write-Host $line -ForegroundColor Green
            }
            elseif ($line -match '^\[FAIL\]') {
                Write-Host $line -ForegroundColor Red
            }
            else {
                Write-Host $line
            }
        }
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $prevErrorAction
    }

    $linesArray = @($outputLines.ToArray())
    Append-Utf8Lines -Path $LogFilePath -Lines $linesArray

    return [pscustomobject]@{
        ExitCode = $exitCode
        Lines    = $linesArray
    }
}

# --- Main ---

if ($Limit -lt 0) {
    Write-Error "-Limit cannot be negative ($Limit)."
    exit 1
}

if ($BatchSize -lt 1) {
    Write-Error "-BatchSize must be at least 1 ($BatchSize)."
    exit 1
}

if ($Concurrency -lt 1) {
    Write-Error "-Concurrency must be at least 1 ($Concurrency)."
    exit 1
}

if ($Concurrency -gt 1 -and -not $PSBoundParameters.ContainsKey('BatchSize')) {
    $BatchSize = $Concurrency
}

$categorySpec = Normalize-CategoryName -RawCategory $Category
$languageFilters = @(Split-CommaSeparatedValues -RawValue $Language)
$filterIds = @(Split-CommaSeparatedValues -RawValue $Filter)

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\NightCodeForge.Benchmark.Cli"

$suiteExplicit = $PSBoundParameters.ContainsKey('Suite') -and -not [string]::IsNullOrWhiteSpace($Suite)
$datasetExplicit = $PSBoundParameters.ContainsKey('Dataset') -and -not [string]::IsNullOrWhiteSpace($Dataset)
$manifestExplicit = $PSBoundParameters.ContainsKey('Manifest') -and -not [string]::IsNullOrWhiteSpace($Manifest)
$noVerifyExplicit = $PSBoundParameters.ContainsKey('NoVerify')

$resumePassedIds = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$resultsById = @{}
$resolvedResumeDir = ""
$manifestResolvedFromResume = $false
$prevVerifyProp = $null

if (-not [string]::IsNullOrWhiteSpace($Resume)) {
    $resumeJsonPath = if (Test-Path -LiteralPath $Resume -PathType Container) {
        Join-Path $Resume "summary.json"
    }
    else {
        $Resume
    }

    if (-not (Test-Path -LiteralPath $resumeJsonPath -PathType Leaf)) {
        Write-Error "Resume summary file not found: $resumeJsonPath"
        exit 1
    }

    $resumeJsonPath = (Resolve-Path -LiteralPath $resumeJsonPath).Path
    $resolvedResumeDir = Split-Path -Parent $resumeJsonPath
    $previousSummary = Get-Content -LiteralPath $resumeJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json

    $prevSuite = [string](Get-JsonPropertyValue -Object $previousSummary -Name "suite" -Default "")
    if (-not $suiteExplicit -and -not [string]::IsNullOrWhiteSpace($prevSuite)) {
        $Suite = $prevSuite
    }

    $prevDataset = [string](Get-JsonPropertyValue -Object $previousSummary -Name "dataset" -Default "")
    if (-not $datasetExplicit -and -not [string]::IsNullOrWhiteSpace($prevDataset)) {
        $Dataset = $prevDataset
    }

    $prevManifestProp = $previousSummary.PSObject.Properties["manifest"]
    if (-not $manifestExplicit -and $null -ne $prevManifestProp) {
        $Manifest = if ($null -eq $prevManifestProp.Value) { "" } else { [string]$prevManifestProp.Value }
        $manifestResolvedFromResume = $true
    }

    $prevVerifyProp = $previousSummary.PSObject.Properties["verify_with_tests"]

    $previousResults = @(Get-JsonPropertyValue -Object $previousSummary -Name "results" -Default @())

    foreach ($prev in $previousResults) {
        $prevId = [string](Get-JsonPropertyValue -Object $prev -Name "instance_id" -Default "")
        if ([string]::IsNullOrWhiteSpace($prevId)) {
            continue
        }

        $prevStatus = [string](Get-JsonPropertyValue -Object $prev -Name "status" -Default "")
        if ($prevStatus -eq "Passed") {
            [void]$resumePassedIds.Add($prevId)
            $resultsById[$prevId] = [pscustomobject]@{
                instance_id = $prevId
                category    = [string](Get-JsonPropertyValue -Object $prev -Name "category" -Default "")
                language    = [string](Get-JsonPropertyValue -Object $prev -Name "language" -Default "")
                repo        = [string](Get-JsonPropertyValue -Object $prev -Name "repo" -Default "")
                status      = "Passed"
                duration_ms = [long](Get-JsonPropertyValue -Object $prev -Name "duration_ms" -Default 0)
                resumed     = $true
                detail      = [string](Get-JsonPropertyValue -Object $prev -Name "detail" -Default "")
            }
        }
    }
}

$verifyWithTests = if ($noVerifyExplicit) {
    -not $NoVerify.IsPresent
}
elseif ($null -ne $prevVerifyProp -and $null -ne $prevVerifyProp.Value) {
    [bool]$prevVerifyProp.Value
}
else {
    $true
}

if ([string]::IsNullOrWhiteSpace($Dataset)) {
    $Dataset = Join-Path $env:USERPROFILE ".nightcodeforge\benchmarks\datasets\$Suite.jsonl"
}

if (-not (Test-Path -LiteralPath $Dataset -PathType Leaf)) {
    Write-Error "Benchmark dataset not found: $Dataset. Materialize it locally as documented in benchmarks/datasets/README.md before running golden validation."
    exit 1
}

$Dataset = (Resolve-Path -LiteralPath $Dataset).Path
if ($datasetExplicit -and -not $suiteExplicit -and [string]::IsNullOrWhiteSpace($resolvedResumeDir)) {
    $Suite = [System.IO.Path]::GetFileNameWithoutExtension($Dataset)
}

$defaultSuiteDataset = Join-Path $env:USERPROFILE ".nightcodeforge\benchmarks\datasets\$Suite.jsonl"
$useDatasetFlag = $datasetExplicit -or [string]::IsNullOrWhiteSpace($Suite) -or (-not $Dataset.Equals($defaultSuiteDataset, [System.StringComparison]::OrdinalIgnoreCase))

if ([string]::IsNullOrWhiteSpace($Manifest) -and -not $manifestResolvedFromResume) {
    $manifestSuite = if ($datasetExplicit -and -not $suiteExplicit) {
        [System.IO.Path]::GetFileNameWithoutExtension($Dataset)
    }
    else {
        $Suite
    }

    if (-not [string]::IsNullOrWhiteSpace($manifestSuite)) {
        $repoManifest = Join-Path $repoRoot "benchmarks\datasets\$manifestSuite.manifest.json"
        if (Test-Path -LiteralPath $repoManifest -PathType Leaf) {
            $Manifest = $repoManifest
        }
    }
}
elseif (-not [string]::IsNullOrWhiteSpace($Manifest)) {
    if (-not (Test-Path -LiteralPath $Manifest -PathType Leaf)) {
        Write-Error "Benchmark manifest not found: $Manifest"
        exit 1
    }
    $Manifest = (Resolve-Path -LiteralPath $Manifest).Path
}

$defaultRepoManifest = if (-not [string]::IsNullOrWhiteSpace($Suite)) {
    Join-Path $repoRoot "benchmarks\datasets\$Suite.manifest.json"
}
else {
    ""
}
$emitManifestFlag = $manifestExplicit -or (
    -not [string]::IsNullOrWhiteSpace($Manifest) -and (
        [string]::IsNullOrWhiteSpace($defaultRepoManifest) -or
        (-not $Manifest.Equals($defaultRepoManifest, [System.StringComparison]::OrdinalIgnoreCase))
    )
)

$hasNarrowingFilter = (-not [string]::IsNullOrWhiteSpace($Category)) -or ($languageFilters.Count -gt 0) -or ($filterIds.Count -gt 0) -or ($Limit -gt 0)

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    if (-not [string]::IsNullOrWhiteSpace($resolvedResumeDir) -and -not $hasNarrowingFilter) {
        $OutDir = $resolvedResumeDir
    }
    else {
        $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $safeSuite = ($Suite -replace '[^A-Za-z0-9._-]+', '-').Trim('-')
        if ([string]::IsNullOrWhiteSpace($safeSuite)) {
            $safeSuite = "validation"
        }
        $OutDir = Join-Path $env:USERPROFILE ".nightcodeforge\benchmarks\validations\$safeSuite-$timestamp"
    }
}

if (-not (Test-Path -LiteralPath $OutDir -PathType Container)) {
    [void](New-Item -ItemType Directory -Path $OutDir -Force)
}

$OutDir = (Resolve-Path -LiteralPath $OutDir).Path
$logPath = Join-Path $OutDir "validation.log"
$summaryJsonPath = Join-Path $OutDir "summary.json"
$summaryMdPath = Join-Path $OutDir "summary.md"

if (-not $NoBuild) {
    Write-Host "[ncf-validate] Building NightCodeForge.Benchmark.Cli ($Configuration)..."
    & dotnet build $projectPath -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed with exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
}

$cliDllPath = Join-Path $projectPath "bin\$Configuration\net10.0\NightCodeForge.Benchmark.Cli.dll"
if (-not (Test-Path -LiteralPath $cliDllPath -PathType Leaf)) {
    $configBinRoot = Join-Path $projectPath "bin\$Configuration"
    if (Test-Path -LiteralPath $configBinRoot -PathType Container) {
        $foundDll = Get-ChildItem -Path $configBinRoot -Filter "NightCodeForge.Benchmark.Cli.dll" -Recurse -File | Select-Object -First 1
        if ($null -ne $foundDll) {
            $cliDllPath = $foundDll.FullName
        }
    }
}

if (-not (Test-Path -LiteralPath $cliDllPath -PathType Leaf)) {
    Write-Error "Compiled NightCodeForge.Benchmark.Cli.dll not found under '$projectPath\bin\$Configuration'. Run without -NoBuild first."
    exit 1
}

if ($CleanContainers) {
    Write-Host "[ncf-validate] Cleaning stale ncf-bench Docker containers in WSL..."
    wsl -e sh -c 'docker ps -aq --filter "name=ncf-bench-" | xargs -r docker rm -f'
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to clean stale Docker containers in WSL (exit code $LASTEXITCODE)."
        exit $LASTEXITCODE
    }
}

# Load instance metadata from dataset JSONL and manifest (for overlays / metadata backfill)
$instances = New-Object System.Collections.Generic.List[object]
$instanceIndexById = @{}

$datasetLines = @(Get-Content -LiteralPath $Dataset -Encoding UTF8)
foreach ($rawLine in $datasetLines) {
    if ([string]::IsNullOrWhiteSpace($rawLine)) {
        continue
    }

    $obj = $rawLine | ConvertFrom-Json
    $id = [string](Get-JsonPropertyValue -Object $obj -Name "instance_id" -Default "")
    if ([string]::IsNullOrWhiteSpace($id)) {
        continue
    }

    $cat = [string](Get-JsonPropertyValue -Object $obj -Name "category" -Default "Implementation")
    $lang = [string](Get-JsonPropertyValue -Object $obj -Name "language" -Default "")
    $repo = [string](Get-JsonPropertyValue -Object $obj -Name "repo" -Default "")

    if (-not $instanceIndexById.ContainsKey($id)) {
        $entry = [pscustomobject]@{
            instance_id = $id
            category    = $cat
            language    = $lang
            repo        = $repo
        }
        $instanceIndexById[$id] = $entry
        $instances.Add($entry)
    }
}

$missingManifestIds = New-Object System.Collections.Generic.List[string]
if (-not [string]::IsNullOrWhiteSpace($Manifest)) {
    $manifestObj = Get-Content -LiteralPath $Manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    $manifestEntries = @(Get-JsonPropertyValue -Object $manifestObj -Name "entries" -Default @())
    foreach ($m in $manifestEntries) {
        $id = [string](Get-JsonPropertyValue -Object $m -Name "instance_id" -Default "")
        if ([string]::IsNullOrWhiteSpace($id)) {
            continue
        }

        $mCat = [string](Get-JsonPropertyValue -Object $m -Name "category" -Default "")
        $mLang = [string](Get-JsonPropertyValue -Object $m -Name "language" -Default "")
        $mRepo = [string](Get-JsonPropertyValue -Object $m -Name "repo" -Default "")

        if ($instanceIndexById.ContainsKey($id)) {
            $existing = $instanceIndexById[$id]
            if (-not [string]::IsNullOrWhiteSpace($mCat)) {
                $existing.category = $mCat
            }
            if (-not [string]::IsNullOrWhiteSpace($mLang)) {
                $existing.language = $mLang
            }
            if (-not [string]::IsNullOrWhiteSpace($mRepo)) {
                $existing.repo = $mRepo
            }
        }
        else {
            $missingManifestIds.Add($id)
        }
    }
}

$manifestWarningLine = $null
if ($missingManifestIds.Count -gt 0) {
    $manifestWarningLine = "[ncf-validate] Warning: $($missingManifestIds.Count) manifest instance(s) missing from dataset '$Dataset' and skipped: $($missingManifestIds -join ', ')"
    Write-Host $manifestWarningLine -ForegroundColor Yellow
}

if ($instances.Count -eq 0) {
    Write-Error "No instances found in dataset '$Dataset'."
    exit 1
}

if ($filterIds.Count -gt 0) {
    $missingIds = New-Object System.Collections.Generic.List[string]
    foreach ($fid in $filterIds) {
        if (-not $instanceIndexById.ContainsKey($fid)) {
            $missingIds.Add($fid)
        }
    }

    if ($missingIds.Count -gt 0) {
        Write-Error ("Requested -Filter instance_id(s) not found in '{0}': {1}" -f $Dataset, ($missingIds -join ", "))
        exit 1
    }
}

$filterSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($fid in $filterIds) {
    [void]$filterSet.Add($fid)
}

$languageSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($langItem in $languageFilters) {
    [void]$languageSet.Add($langItem)
}

$selectedList = New-Object System.Collections.Generic.List[object]
foreach ($inst in $instances) {
    if (-not [string]::IsNullOrWhiteSpace($categorySpec.Canonical)) {
        if (-not $inst.category.Equals($categorySpec.Canonical, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
    }

    if ($languageSet.Count -gt 0) {
        if (-not $languageSet.Contains($inst.language)) {
            continue
        }
    }

    if ($filterSet.Count -gt 0) {
        if (-not $filterSet.Contains($inst.instance_id)) {
            continue
        }
    }

    $selectedList.Add($inst)
    if ($Limit -gt 0 -and $selectedList.Count -ge $Limit) {
        break
    }
}

$selectedInstances = @($selectedList.ToArray())
if ($selectedInstances.Count -eq 0) {
    Write-Error "No dataset instances matched the requested filters (-Category, -Language, -Filter)."
    exit 1
}

$pendingList = New-Object System.Collections.Generic.List[object]
$resumedSkipList = New-Object System.Collections.Generic.List[object]
foreach ($inst in $selectedInstances) {
    if ($resumePassedIds.Contains($inst.instance_id)) {
        $resumedSkipList.Add($inst)
    }
    else {
        $pendingList.Add($inst)
    }
}

$pendingInstances = @($pendingList.ToArray())
$startedUtc = (Get-Date).ToUniversalTime().ToString("o")

Write-Output "OUT_DIR=$OutDir"
$headerLines = @(
    "========================================================================",
    "[ncf-validate] Started UTC: $startedUtc",
    "[ncf-validate] Suite:       $Suite",
    "[ncf-validate] Dataset:     $Dataset",
    ("[ncf-validate] Manifest:    " + $(if ([string]::IsNullOrWhiteSpace($Manifest)) { "(none)" } else { $Manifest })),
    ("[ncf-validate] Mode:        " + $(if ($verifyWithTests) { "verify (containerized tests)" } else { "no-verify (patch & frontmatter only)" })),
    "[ncf-validate] Selected:    $($selectedInstances.Count) | Resumed/Skipped: $($resumedSkipList.Count) | To Run: $($pendingInstances.Count) | BatchSize: $BatchSize | Concurrency: $Concurrency",
    "========================================================================"
)
foreach ($hl in $headerLines) {
    Write-Host $hl
}
Append-Utf8Lines -Path $logPath -Lines $headerLines
if (-not [string]::IsNullOrWhiteSpace($manifestWarningLine)) {
    Append-Utf8Lines -Path $logPath -Lines @($manifestWarningLine)
}

# Persist initial summary immediately so progress/resumed baseline exists on disk
$latestStats = Write-ValidationSummaries `
    -JsonPath $summaryJsonPath `
    -MarkdownPath $summaryMdPath `
    -SuiteName $Suite `
    -DatasetPath $Dataset `
    -ManifestPath $Manifest `
    -UseDatasetFlag $useDatasetFlag `
    -ManifestExplicit $emitManifestFlag `
    -VerifyWithTests $verifyWithTests `
    -BuildConfiguration $Configuration `
    -StartedUtc $startedUtc `
    -OutDirectory $OutDir `
    -SelectedInstances $selectedInstances `
    -ResultsById $resultsById `
    -Concurrency $Concurrency

$resultLinePattern = '^\[(PASS|FAIL)\]\s+(\S+)\s+\(([^,]+),\s*([^,]+),\s*(\d+)\s*ms\):\s*(.*)$'
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$totalPending = $pendingInstances.Count
$processedInRun = 0
$abortedEarly = $false

for ($offset = 0; $offset -lt $totalPending; $offset += $BatchSize) {
    $endIndex = [Math]::Min($offset + $BatchSize - 1, $totalPending - 1)
    $batch = @($pendingInstances[$offset..$endIndex])
    $batchIds = @($batch | ForEach-Object { $_.instance_id })
    $batchIdCsv = $batchIds -join ","

    $firstNum = $offset + 1
    $lastNum = $endIndex + 1
    $batchPreview = if ($batch.Count -eq 1) {
        "$($batch[0].instance_id) [$($batch[0].category), $($batch[0].language)]"
    }
    else {
        "$($batch.Count) instances: $batchIdCsv"
    }

    $progressLine = "[ncf-validate] [$firstNum..$lastNum/$totalPending] (elapsed $(Format-DurationText -DurationMs $stopwatch.ElapsedMilliseconds)) Validating $batchPreview"
    Write-Host $progressLine -ForegroundColor Cyan
    Append-Utf8Lines -Path $logPath -Lines @($progressLine)

    $cliArgs = New-Object System.Collections.Generic.List[string]
    $cliArgs.Add("dataset")
    $cliArgs.Add("validate")
    $cliArgs.Add("--dataset")
    $cliArgs.Add($Dataset)
    if (-not [string]::IsNullOrWhiteSpace($Manifest)) {
        $cliArgs.Add("--manifest")
        $cliArgs.Add($Manifest)
    }
    $cliArgs.Add("--filter")
    $cliArgs.Add($batchIdCsv)
    $cliArgs.Add("--concurrency")
    $cliArgs.Add($Concurrency.ToString())
    if (-not $verifyWithTests) {
        $cliArgs.Add("--no-verify")
    }
    else {
        $cliArgs.Add("--verify")
    }
    if (-not [string]::IsNullOrWhiteSpace($WorkspacesDir)) {
        $cliArgs.Add("--workspaces-dir")
        $cliArgs.Add($WorkspacesDir)
    }
    if ($NoWslWorkspaces.IsPresent) {
        $cliArgs.Add("--no-wsl-workspaces")
    }

    $batchWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $execResult = Invoke-ValidateBatch -CliDllPath $cliDllPath -CliArgs $cliArgs.ToArray() -LogFilePath $logPath
    $batchWatch.Stop()

    $parsedInBatch = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $batchHasFailure = $false

    foreach ($line in $execResult.Lines) {
        if ($line -match $resultLinePattern) {
            $tag = $matches[1]
            $parsedId = $matches[2].Trim()
            $parsedCategory = $matches[3].Trim()
            $parsedLanguage = $matches[4].Trim()
            $parsedDurationMs = [long]$matches[5]
            $parsedDetail = $matches[6].Trim()

            $meta = if ($instanceIndexById.ContainsKey($parsedId)) { $instanceIndexById[$parsedId] } else { $null }
            $repoName = if ($null -ne $meta) { $meta.repo } else { "" }
            $statusValue = if ($tag -eq "PASS") { "Passed" } else { "Failed" }
            if ($statusValue -eq "Failed") {
                $batchHasFailure = $true
            }

            [void]$parsedInBatch.Add($parsedId)
            $resultsById[$parsedId] = [pscustomobject]@{
                instance_id = $parsedId
                category    = $parsedCategory
                language    = $parsedLanguage
                repo        = $repoName
                status      = $statusValue
                duration_ms = $parsedDurationMs
                resumed     = $false
                detail      = $parsedDetail
            }
        }
    }

    # Ensure instances in the batch that failed before emitting [PASS]/[FAIL] are recorded as Failed
    $nonEmptyTail = @($execResult.Lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 3) -join " | "
    foreach ($inst in $batch) {
        if (-not $parsedInBatch.Contains($inst.instance_id)) {
            $batchHasFailure = $true
            $fallbackMsg = if ([string]::IsNullOrWhiteSpace($nonEmptyTail)) {
                "ncf-bench exited with code $($execResult.ExitCode) without emitting an instance result."
            }
            else {
                "Exit code $($execResult.ExitCode): $nonEmptyTail"
            }

            $resultsById[$inst.instance_id] = [pscustomobject]@{
                instance_id = $inst.instance_id
                category    = $inst.category
                language    = $inst.language
                repo        = $inst.repo
                status      = "Failed"
                duration_ms = [long]([Math]::Round($batchWatch.ElapsedMilliseconds / [double]$batch.Count))
                resumed     = $false
                detail      = $fallbackMsg
            }
        }
    }

    $processedInRun += $batch.Count
    $latestStats = Write-ValidationSummaries `
        -JsonPath $summaryJsonPath `
        -MarkdownPath $summaryMdPath `
        -SuiteName $Suite `
        -DatasetPath $Dataset `
        -ManifestPath $Manifest `
        -UseDatasetFlag $useDatasetFlag `
        -ManifestExplicit $emitManifestFlag `
        -VerifyWithTests $verifyWithTests `
        -BuildConfiguration $Configuration `
        -StartedUtc $startedUtc `
        -OutDirectory $OutDir `
        -SelectedInstances $selectedInstances `
        -ResultsById $resultsById `
        -Concurrency $Concurrency

    if ($StopOnFailure -and $batchHasFailure) {
        $stopMsg = "[ncf-validate] Stopping early (-StopOnFailure) after batch failure at [$firstNum..$lastNum/$totalPending]."
        Write-Host $stopMsg -ForegroundColor Yellow
        Append-Utf8Lines -Path $logPath -Lines @($stopMsg)
        $abortedEarly = $true
        break
    }
}

$stopwatch.Stop()

Write-Host ""
Write-Host "========================================================================"
Write-Host ("[ncf-validate] Validation Complete ({0})" -f $(Format-DurationText -DurationMs $stopwatch.ElapsedMilliseconds))
Write-Host ("  Selected: $($latestStats.Total) | Passed: $($latestStats.Passed) | Failed: $($latestStats.Failed) | Pending: $($latestStats.Pending) | Resumed: $($latestStats.ResumedSkipped)")
Write-Host "  By Category:"
foreach ($g in $latestStats.ByCategory) {
    Write-Host ("    - {0,-15} {1}/{2} passed ({3} failed, {4} pending)" -f $g.Key, $g.Passed, $g.Total, $g.Failed, $g.Pending)
}
Write-Host "  By Language:"
foreach ($g in $latestStats.ByLanguage) {
    Write-Host ("    - {0,-15} {1}/{2} passed ({3} failed, {4} pending)" -f $g.Key, $g.Passed, $g.Total, $g.Failed, $g.Pending)
}

if ($latestStats.Failed -gt 0) {
    Write-Host "  Failed Instances:" -ForegroundColor Red
    foreach ($f in $latestStats.FailedResults) {
        Write-Host ("    - {0} ({1}, {2}): {3}" -f $f.instance_id, $f.category, $f.language, $f.detail) -ForegroundColor Red
    }
    $failedFilterCsv = @($latestStats.FailedResults | ForEach-Object { $_.instance_id }) -join ","
    $rerunFailedCmd = Format-RerunCommand `
        -SuiteName $Suite `
        -DatasetPath $Dataset `
        -ManifestPath $Manifest `
        -UseDatasetFlag $useDatasetFlag `
        -ManifestExplicit $emitManifestFlag `
        -VerifyWithTests $verifyWithTests `
        -FilterCsv $failedFilterCsv `
        -Concurrency $Concurrency
    $resumeCmd = Format-RerunCommand `
        -SuiteName $Suite `
        -DatasetPath $Dataset `
        -ManifestPath $Manifest `
        -UseDatasetFlag $useDatasetFlag `
        -ManifestExplicit $emitManifestFlag `
        -VerifyWithTests $verifyWithTests `
        -ResumeDir $OutDir `
        -Concurrency $Concurrency
    Write-Host ""
    Write-Host "  Rerun failed only:" -ForegroundColor Yellow
    Write-Host "    $rerunFailedCmd"
    Write-Host "  Resume remaining/failed:" -ForegroundColor Yellow
    Write-Host "    $resumeCmd"
}

Write-Host "  Summary JSON: $summaryJsonPath"
Write-Host "  Summary MD:   $summaryMdPath"
Write-Host "  Full Log:     $logPath"
Write-Host "========================================================================"

if ($latestStats.Failed -gt 0 -or $latestStats.Pending -gt 0 -or $abortedEarly) {
    exit 1
}

exit 0
