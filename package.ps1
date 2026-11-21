<#
.SYNOPSIS
    Packages NightCodeForge Web archive and NightCodeForge.Cli NuGet tool into an output directory.
.DESCRIPTION
    Builds and packages the unreleased NightCodeForge release artifacts for local air-gapped bundles or offline testing:
      1. Packs src/NightCodeForge.Cli/NightCodeForge.Cli.csproj as NightCodeForge.Cli.<version>.nupkg.
      2. Publishes NightCodeForge.Web (--self-contained) + Native AOT NightCodeForge.Launcher (ncf-web / ncf-web.exe at archive root),
         bundles skills/, VERSION, README.md, and LICENSE, and compresses into NightCodeForge-Web-<Runtime>.zip.
      3. Optionally (-IncludeReleasedCompanionTools) downloads published Pmad.CSharpNav, Pmad.MarkdownFetch, and Pmad.CodeCondenser
         .nupkg files from nuget.org into -OutputDir.

    Full Air-Gapped Bundle Assembly Example:
      $bundleDir = ".\artifacts\installer-bundle\bundle"
      New-Item -ItemType Directory -Force -Path $bundleDir | Out-Null
      & ..\NightCodeAssay\package.ps1 -OutputDir $bundleDir
      & ..\OllamaRouter\package.ps1 -OutputDir $bundleDir
      & .\package.ps1 -OutputDir $bundleDir -IncludeReleasedCompanionTools
.PARAMETER Configuration
    Build configuration (default: Release).
.PARAMETER Runtime
    Target runtime identifier (default: win-x64).
.PARAMETER Version
    Optional explicit package/archive version override. Defaults to <Version> in NightCodeForge.Web.csproj.
.PARAMETER OutputDir
    Destination directory for generated artifacts (default: ./artifacts).
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
    [switch]$IncludeReleasedCompanionTools
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$webProjectPath = Join-Path $repoRoot "src\NightCodeForge.Web\NightCodeForge.Web.csproj"
$launcherProjectPath = Join-Path $repoRoot "src\NightCodeForge.Launcher\NightCodeForge.Launcher.csproj"
$cliProjectPath = Join-Path $repoRoot "src\NightCodeForge.Cli\NightCodeForge.Cli.csproj"

if (-not (Test-Path $webProjectPath)) { throw "NightCodeForge.Web project not found: $webProjectPath" }
if (-not (Test-Path $launcherProjectPath)) { throw "NightCodeForge.Launcher project not found: $launcherProjectPath" }
if (-not (Test-Path $cliProjectPath)) { throw "NightCodeForge.Cli project not found: $cliProjectPath" }

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$webXml = Get-Content $webProjectPath
    $xmlVersion = @($webXml.Project.PropertyGroup.Version | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)
    $Version = if ($xmlVersion.Count -gt 0) { $xmlVersion[0].Trim() } else { "1.0.0" }
}

$resolvedOutputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)
New-Item -ItemType Directory -Path $resolvedOutputDir -Force | Out-Null

$isWindowsRid = $Runtime -like "win-*"
$launcherBinaryName = if ($isWindowsRid) { "ncf-web.exe" } else { "ncf-web" }
$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ncf-pkg-" + [System.Guid]::NewGuid().ToString("N"))
$webStagingDir = Join-Path $stagingRoot "web"
$launcherStagingDir = Join-Path $stagingRoot "launcher"

try {
    New-Item -ItemType Directory -Path $webStagingDir -Force | Out-Null
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
    Copy-Item -Path $stagedLauncher -Destination (Join-Path $webStagingDir $launcherBinaryName) -Force

    # Strip PDBs and copy skills/ + metadata
    Get-ChildItem -Path $webStagingDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    $skillsSource = Join-Path $repoRoot "skills"
    if (Test-Path $skillsSource) {
        $skillsDest = Join-Path $webStagingDir "skills"
        New-Item -ItemType Directory -Path $skillsDest -Force | Out-Null
        Copy-Item -Path (Join-Path $skillsSource "*") -Destination $skillsDest -Recurse -Force
    }

    [System.IO.File]::WriteAllText((Join-Path $webStagingDir "VERSION"), "$Version`n", [System.Text.UTF8Encoding]::new($false))
    Copy-Item -Path (Join-Path $repoRoot "README.md") -Destination $webStagingDir -Force
    Copy-Item -Path (Join-Path $repoRoot "LICENSE") -Destination $webStagingDir -Force

    $archivePath = Join-Path $resolvedOutputDir "NightCodeForge-Web-$Runtime.zip"
    if (Test-Path $archivePath) {
        Remove-Item -Path $archivePath -Force
    }

    Write-Host "Creating archive $archivePath..." -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $webStagingDir "*") -DestinationPath $archivePath -Force

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
