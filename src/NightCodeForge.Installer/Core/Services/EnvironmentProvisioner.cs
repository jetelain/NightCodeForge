namespace NightCodeForge.Installer.Core.Services;

using System.Text;
using System.Text.RegularExpressions;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Cross-platform implementation of <see cref="IEnvironmentProvisioner"/>.
/// </summary>
public sealed partial class EnvironmentProvisioner : IEnvironmentProvisioner
{
    private const string StepWsl = "WSL2";
    private const string StepWslDistro = "WSL2 Distro";
    private const string StepHostDotNet = "Host .NET 10 SDK";
    private const string StepToolchain = "Subsystem Toolchain";
    private const string StepMacOsPrerequisites = "macOS Prerequisites";
    private const string WslProbeCommand =
        "if [ -f /etc/profile.d/dotnet.sh ]; then . /etc/profile.d/dotnet.sh >/dev/null 2>&1 || true; fi; " +
        "echo \"DOTNET=$(dotnet --list-sdks 2>/dev/null | grep -c '^10\\.' || true)\"; " +
        "echo \"DOCKER=$((command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1) && echo 1 || echo 0)\"; " +
        "echo \"RG=$(command -v rg >/dev/null 2>&1 && echo 1 || echo 0)\"";

    private const double ProgressWslStart = 10.0;
    private const double ProgressDistroStart = 25.0;
    private const double ProgressDistroRetry = 28.0;
    private const double ProgressHostDotNetStart = 40.0;
    private const double ProgressHostDotNetFallback = 48.0;
    private const double ProgressMacOsStart = 50.0;
    private const double ProgressToolchainStart = 60.0;
    private const double ProgressComplete = 100.0;

    private readonly IPlatformEnvironment _platform;
    private readonly IProcessRunner _processRunner;

    public EnvironmentProvisioner(
        IPlatformEnvironment platform,
        IProcessRunner processRunner)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<EnvironmentProbeResult> ProbeAsync(
        string wslDistroName,
        CancellationToken cancellationToken = default)
    {
        var requestedDistro = string.IsNullOrWhiteSpace(wslDistroName)
            ? InstallerConstants.DefaultWslDistro
            : wslDistroName.Trim();
        var notes = new List<string>();

        var hostDotNet10 = await ProbeDotNet10Async(null, cancellationToken).ConfigureAwait(false);
        var hostGit = await ProbeSimpleSuccessAsync(InstallerConstants.GitExecutable, ["--version"], cancellationToken).ConfigureAwait(false);
        var hostRipgrep = await ProbeSimpleSuccessAsync(InstallerConstants.RipgrepExecutable, ["--version"], cancellationToken).ConfigureAwait(false);

        if (_platform.Platform == HostPlatformType.Windows)
        {
            var (wslInstalled, distros) = await ProbeWindowsWslAsync(cancellationToken).ConfigureAwait(false);
            var resolvedDistro = ResolveWslDistroName(distros, requestedDistro);
            var distroInstalled = resolvedDistro is not null;

            var wslDotNet10 = false;
            var wslDocker = false;
            var wslRipgrep = false;

            if (wslInstalled && distroInstalled && resolvedDistro is not null)
            {
                (wslDotNet10, wslDocker, wslRipgrep) = await ProbeWslToolchainAsync(resolvedDistro, cancellationToken).ConfigureAwait(false);
            }
            else if (!wslInstalled)
            {
                notes.Add("WSL2 is not enabled or installed on this Windows host.");
            }
            else
            {
                notes.Add($"WSL2 distribution '{requestedDistro}' is not installed.");
            }

            return new EnvironmentProbeResult(
                Platform: _platform.Platform,
                RuntimeIdentifier: _platform.CurrentRuntimeIdentifier,
                WslInstalled: wslInstalled,
                WslDistroInstalled: distroInstalled,
                ResolvedWslDistroName: resolvedDistro,
                HostDotNet10Installed: hostDotNet10,
                HostGitInstalled: hostGit,
                HostDockerAvailable: wslDocker,
                HostRipgrepInstalled: hostRipgrep,
                WslDotNet10Installed: wslDotNet10,
                WslDockerInstalled: wslDocker,
                WslRipgrepInstalled: wslRipgrep,
                DiagnosticNotes: notes);
        }

        var hostDocker = await ProbeDockerRuntimeAsync(_platform.Platform, cancellationToken).ConfigureAwait(false);
        if (!hostDocker)
        {
            notes.Add("Docker runtime is not reachable.");
        }

        if (!hostDotNet10)
        {
            notes.Add(".NET 10 SDK is not installed.");
        }

        if (!hostRipgrep)
        {
            notes.Add("ripgrep (rg) is not installed.");
        }

        if (!hostGit)
        {
            notes.Add("git is not installed.");
        }

        return new EnvironmentProbeResult(
            Platform: _platform.Platform,
            RuntimeIdentifier: _platform.CurrentRuntimeIdentifier,
            WslInstalled: false,
            WslDistroInstalled: false,
            ResolvedWslDistroName: null,
            HostDotNet10Installed: hostDotNet10,
            HostGitInstalled: hostGit,
            HostDockerAvailable: hostDocker,
            HostRipgrepInstalled: hostRipgrep,
            WslDotNet10Installed: false,
            WslDockerInstalled: false,
            WslRipgrepInstalled: false,
            DiagnosticNotes: notes);
    }

    public async Task<ProvisioningReport> ProvisionAsync(
        ProvisioningOptions options,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var distro = string.IsNullOrWhiteSpace(options.WslDistroName)
            ? InstallerConstants.DefaultWslDistro
            : options.WslDistroName.Trim();

        var steps = new List<ProvisioningStepResult>();
        var probe = await ProbeAsync(distro, cancellationToken).ConfigureAwait(false);

        switch (_platform.Platform)
        {
            case HostPlatformType.Windows:
                var rebootOrFailure = await ProvisionWindowsAsync(options, distro, probe, steps, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (rebootOrFailure is not null)
                {
                    var postEarly = await ProbeAsync(distro, cancellationToken).ConfigureAwait(false);
                    return new ProvisioningReport(rebootOrFailure.Value, steps, postEarly);
                }

                break;

            case HostPlatformType.Linux:
                await ProvisionLinuxAsync(options, probe, steps, progress, cancellationToken).ConfigureAwait(false);
                break;

            case HostPlatformType.MacOS:
                await ProvisionMacOsAsync(options, probe, steps, progress, cancellationToken).ConfigureAwait(false);
                break;
        }

        var postProbe = await ProbeAsync(distro, cancellationToken).ConfigureAwait(false);
        var overall = DetermineOverallStatus(steps);
        return new ProvisioningReport(overall, steps, postProbe);
    }

    public static string BuildLinuxToolchainScript(bool isWsl, bool skipDocker)
    {
        var sb = new StringBuilder();
        sb.Append("#!/usr/bin/env bash\n");
        sb.Append("set -euo pipefail\n\n");
        sb.Append("if ! command -v apt-get >/dev/null 2>&1; then\n");
        sb.Append("    echo \"Unsupported Linux distribution: automated toolchain provisioning requires apt-get (Ubuntu/Debian).\" >&2\n");
        sb.Append("    exit 1\n");
        sb.Append("fi\n\n");
        sb.Append("export DEBIAN_FRONTEND=noninteractive\n\n");
        sb.Append("echo \"--> Updating apt package repository...\"\n");
        sb.Append("apt-get update -y\n\n");
        sb.Append("echo \"--> Installing core dependencies (curl, ca-certificates, git, ripgrep, libicu-dev, locales)...\"\n");
        sb.Append("apt-get install -y --no-install-recommends \\\n");
        sb.Append("    ca-certificates \\\n");
        sb.Append("    curl \\\n");
        sb.Append("    gnupg \\\n");
        sb.Append("    lsb-release \\\n");
        sb.Append("    git \\\n");
        sb.Append("    build-essential \\\n");
        sb.Append("    procps \\\n");
        sb.Append("    jq \\\n");
        sb.Append("    tar \\\n");
        sb.Append("    ripgrep \\\n");
        sb.Append("    libicu-dev \\\n");
        sb.Append("    locales\n\n");
        sb.Append("echo \"--> Generating locales...\"\n");
        sb.Append("locale-gen en_US.UTF-8 || true\n");
        sb.Append("update-locale LANG=en_US.UTF-8 || true\n\n");
        sb.Append("if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^10\\.'; then\n");
        sb.Append("    echo \"--> .NET 10 SDK is already installed.\"\n");
        sb.Append("else\n");
        sb.Append("    echo \"--> Installing .NET 10 SDK via apt (dotnet-sdk-10.0)...\"\n");
        sb.Append("    if ! apt-get install -y --no-install-recommends dotnet-sdk-10.0; then\n");
        sb.Append("        echo \"--> Registering Microsoft package feed...\"\n");
        sb.Append("        DISTRO_ID=$(. /etc/os-release 2>/dev/null && printf '%s' \"${ID:-ubuntu}\" || echo \"ubuntu\")\n");
        sb.Append("        DISTRO_VERSION=$(. /etc/os-release 2>/dev/null && printf '%s' \"${VERSION_ID:-}\" || true)\n");
        sb.Append("        if [ -z \"$DISTRO_VERSION\" ]; then\n");
        sb.Append("            DISTRO_VERSION=$(lsb_release -rs 2>/dev/null || echo \"24.04\")\n");
        sb.Append("        fi\n");
        sb.Append("        DEB_TMP=$(mktemp /tmp/packages-microsoft-prod.XXXXXX.deb)\n");
        sb.Append("        trap 'rm -f \"$DEB_TMP\"' EXIT\n");
        sb.Append("        curl -fsSL \"https://packages.microsoft.com/config/${DISTRO_ID}/${DISTRO_VERSION}/packages-microsoft-prod.deb\" -o \"$DEB_TMP\"\n");
        sb.Append("        dpkg -i \"$DEB_TMP\"\n");
        sb.Append("        rm -f \"$DEB_TMP\"\n");
        sb.Append("        apt-get update -y\n");
        sb.Append("        apt-get install -y --no-install-recommends dotnet-sdk-10.0\n");
        sb.Append("    fi\n");
        sb.Append("fi\n\n");
        sb.Append("cat << 'EOF' > /etc/profile.d/dotnet.sh\n");
        sb.Append("if [ -d /usr/lib/dotnet ]; then\n");
        sb.Append("    export DOTNET_ROOT=/usr/lib/dotnet\n");
        sb.Append("elif [ -d /usr/share/dotnet ]; then\n");
        sb.Append("    export DOTNET_ROOT=/usr/share/dotnet\n");
        sb.Append("elif [ -d \"$HOME/.dotnet\" ]; then\n");
        sb.Append("    export DOTNET_ROOT=\"$HOME/.dotnet\"\n");
        sb.Append("fi\n");
        sb.Append("export PATH=\"$PATH${DOTNET_ROOT:+:$DOTNET_ROOT}:$HOME/.dotnet/tools\"\n");
        sb.Append("EOF\n");
        sb.Append("chmod +x /etc/profile.d/dotnet.sh\n\n");

        if (!skipDocker)
        {
            sb.Append("if command -v docker >/dev/null 2>&1; then\n");
            sb.Append("    echo \"--> Docker is already installed.\"\n");
            sb.Append("else\n");
            sb.Append("    echo \"--> Installing Docker Engine...\"\n");
            sb.Append("    apt-get install -y --no-install-recommends docker.io docker-buildx docker-compose-v2 || apt-get install -y --no-install-recommends docker.io\n");
            sb.Append("fi\n\n");

            if (isWsl)
            {
                sb.Append("if [ ! -f /etc/wsl.conf ] || ! grep -Eq '^[[:space:]]*systemd[[:space:]]*=[[:space:]]*true' /etc/wsl.conf; then\n");
                sb.Append("    echo \"--> Enabling systemd=true in /etc/wsl.conf...\"\n");
                sb.Append("    mkdir -p /etc\n");
                sb.Append("    if [ -f /etc/wsl.conf ] && grep -Eq '^[[:space:]]*systemd[[:space:]]*=' /etc/wsl.conf; then\n");
                sb.Append("        sed -i -E 's/^[[:space:]]*systemd[[:space:]]*=.*/systemd=true/' /etc/wsl.conf\n");
                sb.Append("    elif ! grep -Eq '^[[:space:]]*\\[boot\\]' /etc/wsl.conf 2>/dev/null; then\n");
                sb.Append("        printf \"\\n[boot]\\nsystemd=true\\n\" >> /etc/wsl.conf\n");
                sb.Append("    else\n");
                sb.Append("        sed -i -E '/^[[:space:]]*\\[boot\\]/a systemd=true' /etc/wsl.conf\n");
                sb.Append("    fi\n");
                sb.Append("fi\n\n");
                sb.Append("service docker status >/dev/null 2>&1 || service docker start >/dev/null 2>&1 || true\n\n");
            }
            else
            {
                sb.Append("systemctl enable --now docker >/dev/null 2>&1 || service docker start >/dev/null 2>&1 || true\n\n");
            }

            sb.Append("for u in $(awk -F: '$3 >= 1000 && $3 < 60000 {print $1}' /etc/passwd); do\n");
            sb.Append("    usermod -aG docker \"$u\" || true\n");
            sb.Append("done\n\n");
        }

        return sb.ToString();
    }

    internal static string StripNullCharacters(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
            .Replace("\uFFFD", string.Empty, StringComparison.Ordinal);
    }

    internal static string? ResolveWslDistroName(IReadOnlyList<string> installedDistros, string requestedDistro)
    {
        foreach (var candidate in installedDistros)
        {
            if (string.Equals(candidate, requestedDistro, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        foreach (var candidate in installedDistros)
        {
            if (candidate.StartsWith(requestedDistro, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<ProvisioningStatus?> ProvisionWindowsAsync(
        ProvisioningOptions options,
        string distro,
        EnvironmentProbeResult initialProbe,
        List<ProvisioningStepResult> steps,
        IProgress<InstallerProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        var activeDistro = initialProbe.ResolvedWslDistroName ?? distro;
        var distroInstalled = initialProbe.WslDistroInstalled;

        if (options.ProvisionWsl)
        {
            if (!initialProbe.WslInstalled)
            {
                Report(progress, StepWsl, "WSL2 not detected. Requesting elevated installation (wsl --install --no-launch)...", InstallerLogLevel.Info, ProgressWslStart);
                var wslInstallResult = await _processRunner.RunAsync(
                    CreateWslRequest(["--install", "--no-launch"], useShellExecute: true, runAsAdmin: true),
                    cancellationToken).ConfigureAwait(false);

                if (wslInstallResult.ExitCode == InstallerConstants.ExitCodeRebootRequired)
                {
                    var rebootMessage = "WSL2 feature was enabled and requires a Windows reboot (exit code 3010). Please reboot and re-run the installer.";
                    steps.Add(new ProvisioningStepResult(StepWsl, ProvisioningStatus.RebootRequired, rebootMessage));
                    Report(progress, StepWsl, rebootMessage, InstallerLogLevel.Warning, ProgressComplete);
                    return ProvisioningStatus.RebootRequired;
                }

                if (wslInstallResult.ExitCode != InstallerConstants.ExitCodeSuccess)
                {
                    var failMessage = $"WSL2 installation failed with exit code {wslInstallResult.ExitCode}. {wslInstallResult.StandardError}".Trim();
                    steps.Add(new ProvisioningStepResult(StepWsl, ProvisioningStatus.Failed, failMessage));
                    Report(progress, StepWsl, failMessage, InstallerLogLevel.Error, null);
                    return ProvisioningStatus.Failed;
                }

                steps.Add(new ProvisioningStepResult(StepWsl, ProvisioningStatus.Completed, "WSL2 installed successfully."));

                var (_, refreshedDistros) = await ProbeWindowsWslAsync(cancellationToken).ConfigureAwait(false);
                var resolvedAfterInstall = ResolveWslDistroName(refreshedDistros, distro);
                if (resolvedAfterInstall is not null)
                {
                    distroInstalled = true;
                    activeDistro = resolvedAfterInstall;
                }
            }
            else
            {
                steps.Add(new ProvisioningStepResult(StepWsl, ProvisioningStatus.Skipped, "WSL2 is already installed."));
            }

            var setDefaultVersionResult = await _processRunner.RunAsync(
                CreateWslRequest(["--set-default-version", "2"]),
                cancellationToken).ConfigureAwait(false);
            if (setDefaultVersionResult.ExitCode != InstallerConstants.ExitCodeSuccess)
            {
                Report(
                    progress,
                    StepWsl,
                    $"Warning: 'wsl --set-default-version 2' exited with code {setDefaultVersionResult.ExitCode}.",
                    InstallerLogLevel.Warning,
                    null);
            }

            if (!distroInstalled)
            {
                Report(progress, StepWslDistro, $"Installing WSL2 distribution '{distro}'...", InstallerLogLevel.Info, ProgressDistroStart);
                var distroResult = await _processRunner.RunAsync(
                    CreateWslRequest(["--install", "-d", distro, "--web-download", "--no-launch"]),
                    cancellationToken).ConfigureAwait(false);

                if (distroResult.ExitCode == InstallerConstants.ExitCodeRebootRequired)
                {
                    var rebootMessage = $"WSL2 distribution '{distro}' requires a Windows reboot (exit code 3010).";
                    steps.Add(new ProvisioningStepResult(StepWslDistro, ProvisioningStatus.RebootRequired, rebootMessage));
                    return ProvisioningStatus.RebootRequired;
                }

                if (distroResult.ExitCode != InstallerConstants.ExitCodeSuccess)
                {
                    Report(progress, StepWslDistro, $"Retrying installation of '{distro}' without --web-download...", InstallerLogLevel.Warning, ProgressDistroRetry);
                    distroResult = await _processRunner.RunAsync(
                        CreateWslRequest(["--install", "-d", distro, "--no-launch"]),
                        cancellationToken).ConfigureAwait(false);

                    if (distroResult.ExitCode == InstallerConstants.ExitCodeRebootRequired)
                    {
                        var rebootMessage = $"WSL2 distribution '{distro}' requires a Windows reboot (exit code 3010).";
                        steps.Add(new ProvisioningStepResult(StepWslDistro, ProvisioningStatus.RebootRequired, rebootMessage));
                        return ProvisioningStatus.RebootRequired;
                    }
                }

                if (distroResult.ExitCode != InstallerConstants.ExitCodeSuccess)
                {
                    var failMessage = $"Failed to install WSL2 distribution '{distro}' (exit code {distroResult.ExitCode}): {distroResult.StandardError}".Trim();
                    steps.Add(new ProvisioningStepResult(StepWslDistro, ProvisioningStatus.Failed, failMessage));
                    return ProvisioningStatus.Failed;
                }

                var setDistroVersionResult = await _processRunner.RunAsync(
                    CreateWslRequest(["--set-version", distro, "2"]),
                    cancellationToken).ConfigureAwait(false);
                if (setDistroVersionResult.ExitCode != InstallerConstants.ExitCodeSuccess)
                {
                    Report(
                        progress,
                        StepWslDistro,
                        $"Warning: 'wsl --set-version {distro} 2' exited with code {setDistroVersionResult.ExitCode}.",
                        InstallerLogLevel.Warning,
                        null);
                }

                steps.Add(new ProvisioningStepResult(StepWslDistro, ProvisioningStatus.Completed, $"Installed WSL2 distribution '{distro}'."));
                activeDistro = distro;
            }
            else
            {
                steps.Add(new ProvisioningStepResult(StepWslDistro, ProvisioningStatus.Skipped, $"WSL2 distribution '{activeDistro}' is already installed."));
            }
        }
        else
        {
            steps.Add(new ProvisioningStepResult(StepWsl, ProvisioningStatus.Skipped, "WSL2 provisioning skipped by options."));
        }

        if (options.ProvisionHostDotNet)
        {
            if (initialProbe.HostDotNet10Installed)
            {
                steps.Add(new ProvisioningStepResult(StepHostDotNet, ProvisioningStatus.Skipped, ".NET 10 SDK is already installed on Windows host."));
            }
            else
            {
                var dotNetStep = await ProvisionWindowsHostDotNet10Async(progress, cancellationToken).ConfigureAwait(false);
                steps.Add(dotNetStep);
                if (dotNetStep.Status == ProvisioningStatus.Failed)
                {
                    return ProvisioningStatus.Failed;
                }
            }
        }
        else
        {
            steps.Add(new ProvisioningStepResult(StepHostDotNet, ProvisioningStatus.Skipped, "Host .NET 10 SDK provisioning skipped by options."));
        }

        if (options.ProvisionLinuxOrWslToolchain)
        {
            var wslAlreadyReady = initialProbe.WslInstalled &&
                                  initialProbe.WslDistroInstalled &&
                                  initialProbe.WslDotNet10Installed &&
                                  initialProbe.WslRipgrepInstalled &&
                                  (options.SkipDocker || initialProbe.WslDockerInstalled);

            if (wslAlreadyReady)
            {
                steps.Add(new ProvisioningStepResult(StepToolchain, ProvisioningStatus.Skipped, $"WSL2 toolchain in '{activeDistro}' is already installed."));
            }
            else
            {
                Report(progress, StepToolchain, $"Provisioning .NET 10, Docker, ripgrep, and toolchain inside WSL2 '{activeDistro}'...", InstallerLogLevel.Info, ProgressToolchainStart);
                var script = BuildLinuxToolchainScript(isWsl: true, skipDocker: options.SkipDocker);
                var wslExecResult = await ExecuteWslRootBashScriptAsync(activeDistro, script, cancellationToken).ConfigureAwait(false);

                if (wslExecResult.ExitCode != InstallerConstants.ExitCodeSuccess)
                {
                    var err = string.IsNullOrWhiteSpace(wslExecResult.StandardError) ? wslExecResult.StandardOutput : wslExecResult.StandardError;
                    var message = $"WSL2 toolchain provisioning failed (exit code {wslExecResult.ExitCode}): {err}".Trim();
                    steps.Add(new ProvisioningStepResult(StepToolchain, ProvisioningStatus.Failed, message));
                    Report(progress, StepToolchain, message, InstallerLogLevel.Error, null);
                    return ProvisioningStatus.Failed;
                }

                steps.Add(new ProvisioningStepResult(StepToolchain, ProvisioningStatus.Completed, $"Provisioned toolchain inside WSL2 '{activeDistro}'."));
            }
        }
        else
        {
            steps.Add(new ProvisioningStepResult(StepToolchain, ProvisioningStatus.Skipped, "WSL2 toolchain provisioning skipped by options."));
        }

        return null;
    }

    private async Task<ProvisioningStepResult> ProvisionWindowsHostDotNet10Async(
        IProgress<InstallerProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, StepHostDotNet, "Installing .NET 10 SDK via winget...", InstallerLogLevel.Info, ProgressHostDotNetStart);
        var wingetResult = await _processRunner.RunAsync(
            new ProcessRunRequest
            {
                FileName = InstallerConstants.WingetExecutable,
                Arguments =
                [
                    "install",
                    "--id",
                    InstallerConstants.WingetDotNet10PackageId,
                    "-e",
                    "--accept-source-agreements",
                    "--accept-package-agreements",
                    "--silent"
                ]
            },
            cancellationToken).ConfigureAwait(false);

        if (wingetResult.ExitCode == InstallerConstants.ExitCodeSuccess)
        {
            _platform.RefreshProcessPath();
            if (await ProbeDotNet10Async(null, cancellationToken).ConfigureAwait(false))
            {
                return new ProvisioningStepResult(StepHostDotNet, ProvisioningStatus.Completed, "Installed .NET 10 SDK via winget.");
            }
        }

        Report(progress, StepHostDotNet, "Winget unavailable or incomplete; installing .NET 10 SDK via dotnet-install.ps1...", InstallerLogLevel.Warning, ProgressHostDotNetFallback);
        var installScriptCmd =
            $"$ErrorActionPreference='Stop'; " +
            $"$p=Join-Path $env:TEMP 'dotnet-install-{Guid.NewGuid():N}.ps1'; " +
            $"try {{ Invoke-WebRequest -Uri '{InstallerConstants.DotNetInstallScriptUrl}' -OutFile $p -UseBasicParsing; & $p -Channel {InstallerConstants.DotNet10Channel}; " +
            $"$dotnetDir=Join-Path $env:LOCALAPPDATA 'Microsoft\\dotnet'; " +
            $"if (Test-Path $dotnetDir) {{ " +
            $"$userPath=[Environment]::GetEnvironmentVariable('Path','User'); " +
            $"$parts=if($userPath){{$userPath -split ';'|Where-Object{{$_}}}}else{{@()}}; " +
            $"if(-not ($parts|Where-Object{{$_.TrimEnd('\\/') -ieq $dotnetDir.TrimEnd('\\/')}})){{ " +
            $"$newUserPath=if([string]::IsNullOrWhiteSpace($userPath)){{$dotnetDir}}else{{\"$userPath;$dotnetDir\"}}; " +
            $"[Environment]::SetEnvironmentVariable('Path',$newUserPath,'User') }}; " +
            $"if($env:Path -notlike \"*$dotnetDir*\"){{ $env:Path=\"$dotnetDir;$env:Path\" }} }} }} " +
            $"finally {{ if (Test-Path $p) {{ Remove-Item $p -Force -ErrorAction SilentlyContinue }} }}";

        var psResult = await _processRunner.RunAsync(
            new ProcessRunRequest
            {
                FileName = InstallerConstants.PowerShellExecutable,
                Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", installScriptCmd]
            },
            cancellationToken).ConfigureAwait(false);

        if (psResult.ExitCode != InstallerConstants.ExitCodeSuccess)
        {
            return new ProvisioningStepResult(
                StepHostDotNet,
                ProvisioningStatus.Failed,
                $"Failed to install .NET 10 SDK on Windows host (exit code {psResult.ExitCode}): {psResult.StandardError}".Trim());
        }

        _platform.RefreshProcessPath();
        return new ProvisioningStepResult(StepHostDotNet, ProvisioningStatus.Completed, "Installed .NET 10 SDK via dotnet-install.ps1.");
    }

    private async Task ProvisionLinuxAsync(
        ProvisioningOptions options,
        EnvironmentProbeResult probe,
        List<ProvisioningStepResult> steps,
        IProgress<InstallerProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        if (!options.ProvisionLinuxOrWslToolchain && !options.ProvisionHostDotNet)
        {
            steps.Add(new ProvisioningStepResult(StepToolchain, ProvisioningStatus.Skipped, "Linux toolchain provisioning skipped by options."));
            return;
        }

        var alreadyReady = (!options.ProvisionHostDotNet || probe.HostDotNet10Installed) &&
                           (!options.ProvisionLinuxOrWslToolchain ||
                            (probe.HostGitInstalled &&
                             probe.HostRipgrepInstalled &&
                             (options.SkipDocker || probe.HostDockerAvailable)));
        if (alreadyReady)
        {
            steps.Add(new ProvisioningStepResult(StepToolchain, ProvisioningStatus.Skipped, "Linux toolchain prerequisites are already installed."));
            return;
        }

        Report(progress, StepToolchain, "Provisioning Linux toolchain (.NET 10 SDK, Docker Engine, ripgrep, git, ICU, locales)...", InstallerLogLevel.Info, ProgressHostDotNetStart);
        var script = BuildLinuxToolchainScript(isWsl: false, skipDocker: options.SkipDocker);
        var tempScript = Path.Combine(Path.GetTempPath(), $"ncf-provision-{Guid.NewGuid():N}.sh");

        try
        {
            await File.WriteAllTextAsync(tempScript, script.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            var request = BuildLinuxElevatedScriptRequest(tempScript);
            var result = await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != InstallerConstants.ExitCodeSuccess)
            {
                var err = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
                steps.Add(new ProvisioningStepResult(StepToolchain, ProvisioningStatus.Failed, $"Linux toolchain provisioning failed: {err}".Trim()));
                return;
            }

            steps.Add(new ProvisioningStepResult(StepToolchain, ProvisioningStatus.Completed, "Linux toolchain provisioned successfully."));
        }
        finally
        {
            if (File.Exists(tempScript))
            {
                File.Delete(tempScript);
            }
        }
    }

    private ProcessRunRequest BuildLinuxElevatedScriptRequest(string tempScriptPath)
    {
        var isRoot = string.Equals(
            _platform.GetEnvironmentVariable(InstallerConstants.EnvUserUnix),
            InstallerConstants.RootUserUnix,
            StringComparison.OrdinalIgnoreCase);
        if (isRoot)
        {
            return new ProcessRunRequest { FileName = InstallerConstants.BashExecutable, Arguments = [tempScriptPath] };
        }

        var hasGuiSession = !string.IsNullOrWhiteSpace(_platform.GetEnvironmentVariable(InstallerConstants.EnvDisplayUnix)) ||
                            !string.IsNullOrWhiteSpace(_platform.GetEnvironmentVariable(InstallerConstants.EnvWaylandDisplayUnix));
        if (hasGuiSession && _platform.IsCommandOnPath(InstallerConstants.PkexecExecutable))
        {
            return new ProcessRunRequest
            {
                FileName = InstallerConstants.PkexecExecutable,
                Arguments = [InstallerConstants.BashExecutable, tempScriptPath]
            };
        }

        return new ProcessRunRequest
        {
            FileName = InstallerConstants.SudoExecutable,
            Arguments = ["-n", InstallerConstants.BashExecutable, tempScriptPath]
        };
    }

    private async Task ProvisionMacOsAsync(
        ProvisioningOptions options,
        EnvironmentProbeResult probe,
        List<ProvisioningStepResult> steps,
        IProgress<InstallerProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        if (!options.ProvisionHostDotNet && !options.ProvisionLinuxOrWslToolchain)
        {
            steps.Add(new ProvisioningStepResult(StepMacOsPrerequisites, ProvisioningStatus.Skipped, "macOS prerequisites check skipped by options."));
            return;
        }

        Report(progress, StepMacOsPrerequisites, "Verifying macOS Docker runtime, Git, ripgrep, and .NET 10 SDK...", InstallerLogLevel.Info, ProgressMacOsStart);

        if (options.ProvisionLinuxOrWslToolchain && !probe.HostRipgrepInstalled && _platform.IsCommandOnPath(InstallerConstants.BrewExecutable))
        {
            var brewRg = await _processRunner.RunAsync(
                new ProcessRunRequest { FileName = InstallerConstants.BrewExecutable, Arguments = ["install", "ripgrep"] },
                cancellationToken).ConfigureAwait(false);
            if (brewRg.ExitCode != InstallerConstants.ExitCodeSuccess)
            {
                Report(progress, StepMacOsPrerequisites, $"Warning: 'brew install ripgrep' exited with code {brewRg.ExitCode}: {brewRg.StandardError}".Trim(), InstallerLogLevel.Warning, null);
            }
        }

        if (options.ProvisionLinuxOrWslToolchain && !probe.HostGitInstalled && _platform.IsCommandOnPath(InstallerConstants.BrewExecutable))
        {
            var brewGit = await _processRunner.RunAsync(
                new ProcessRunRequest { FileName = InstallerConstants.BrewExecutable, Arguments = ["install", InstallerConstants.GitExecutable] },
                cancellationToken).ConfigureAwait(false);
            if (brewGit.ExitCode != InstallerConstants.ExitCodeSuccess)
            {
                Report(progress, StepMacOsPrerequisites, $"Warning: 'brew install git' exited with code {brewGit.ExitCode}: {brewGit.StandardError}".Trim(), InstallerLogLevel.Warning, null);
            }
        }

        var refreshed = await ProbeAsync(InstallerConstants.DefaultWslDistro, cancellationToken).ConfigureAwait(false);
        var missing = new List<string>();
        if (options.ProvisionLinuxOrWslToolchain && !options.SkipDocker && !refreshed.HostDockerAvailable)
        {
            missing.Add("Docker runtime (Docker Desktop, Colima, or OrbStack)");
        }

        if (options.ProvisionHostDotNet && !refreshed.HostDotNet10Installed)
        {
            missing.Add(".NET 10 SDK");
        }

        if (options.ProvisionLinuxOrWslToolchain && !refreshed.HostGitInstalled)
        {
            missing.Add(InstallerConstants.GitExecutable);
        }

        if (options.ProvisionLinuxOrWslToolchain && !refreshed.HostRipgrepInstalled)
        {
            missing.Add("ripgrep");
        }

        if (missing.Count > 0)
        {
            steps.Add(new ProvisioningStepResult(
                StepMacOsPrerequisites,
                ProvisioningStatus.Failed,
                $"Missing macOS prerequisite(s): {string.Join(", ", missing)}."));
            return;
        }

        steps.Add(new ProvisioningStepResult(StepMacOsPrerequisites, ProvisioningStatus.Completed, "macOS prerequisites verified."));
    }

    private async Task<ProcessExecutionResult> ExecuteWslRootBashScriptAsync(
        string distro,
        string scriptContent,
        CancellationToken cancellationToken)
    {
        var tempScript = Path.Combine(Path.GetTempPath(), $"ncf-wsl-provision-{Guid.NewGuid():N}.sh");
        try
        {
            var normalized = scriptContent.Replace("\r\n", "\n", StringComparison.Ordinal);
            await File.WriteAllTextAsync(tempScript, normalized, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            var forwardPath = tempScript.Replace('\\', '/');
            var pathResult = await _processRunner.RunAsync(
                CreateWslRequest(["-d", distro, "-u", InstallerConstants.RootUserUnix, "--", "wslpath", "-u", forwardPath]),
                cancellationToken).ConfigureAwait(false);

            if (pathResult.ExitCode != InstallerConstants.ExitCodeSuccess)
            {
                return pathResult;
            }

            var wslPath = StripNullCharacters(pathResult.StandardOutput).Trim();
            return await _processRunner.RunAsync(
                CreateWslRequest(["-d", distro, "-u", InstallerConstants.RootUserUnix, "--", InstallerConstants.BashExecutable, wslPath]),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempScript))
            {
                File.Delete(tempScript);
            }
        }
    }

    private async Task<(bool WslInstalled, List<string> Distros)> ProbeWindowsWslAsync(CancellationToken cancellationToken)
    {
        var statusResult = await _processRunner.RunAsync(
            CreateWslRequest(["--status"]),
            cancellationToken).ConfigureAwait(false);

        var statusOutput = StripNullCharacters(statusResult.StandardOutput + statusResult.StandardError);
        var wslInstalled = statusResult.ExitCode == InstallerConstants.ExitCodeSuccess ||
                           statusOutput.Contains("Default Version", StringComparison.OrdinalIgnoreCase) ||
                           statusOutput.Contains("Version par d", StringComparison.OrdinalIgnoreCase);

        if (!wslInstalled)
        {
            var verboseResult = await _processRunner.RunAsync(
                CreateWslRequest(["--list", "--verbose"]),
                cancellationToken).ConfigureAwait(false);
            var verboseOutput = StripNullCharacters(verboseResult.StandardOutput + verboseResult.StandardError);
            wslInstalled = verboseResult.ExitCode == InstallerConstants.ExitCodeSuccess ||
                           (verboseOutput.Contains("NAME", StringComparison.OrdinalIgnoreCase) &&
                            verboseOutput.Contains("VERSION", StringComparison.OrdinalIgnoreCase));
        }

        if (!wslInstalled)
        {
            return (false, []);
        }

        var listResult = await _processRunner.RunAsync(
            CreateWslRequest(["-l", "-q"]),
            cancellationToken).ConfigureAwait(false);

        var distros = new List<string>();
        if (listResult.ExitCode == InstallerConstants.ExitCodeSuccess)
        {
            var cleaned = StripNullCharacters(listResult.StandardOutput);
            foreach (var rawLine in cleaned.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    distros.Add(line);
                }
            }
        }

        return (true, distros);
    }

    private async Task<(bool DotNet10, bool Docker, bool Ripgrep)> ProbeWslToolchainAsync(
        string distro,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            CreateWslRequest(["-d", distro, "--", InstallerConstants.BashExecutable, "-lc", WslProbeCommand]),
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != InstallerConstants.ExitCodeSuccess)
        {
            return (false, false, false);
        }

        var cleaned = StripNullCharacters(result.StandardOutput);
        var hasDotNet = DotNetCountRegex().Match(cleaned) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var count) && count > 0;
        var hasDocker = cleaned.Contains("DOCKER=1", StringComparison.Ordinal);
        var hasRipgrep = cleaned.Contains("RG=1", StringComparison.Ordinal);
        return (hasDotNet, hasDocker, hasRipgrep);
    }

    private async Task<bool> ProbeDotNet10Async(string? workingDirectory, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            new ProcessRunRequest
            {
                FileName = InstallerConstants.DotNetExecutable,
                Arguments = ["--list-sdks"],
                WorkingDirectory = workingDirectory
            },
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != InstallerConstants.ExitCodeSuccess)
        {
            return false;
        }

        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.TrimStart().StartsWith(InstallerConstants.DotNet10VersionPrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> ProbeDockerRuntimeAsync(HostPlatformType platform, CancellationToken cancellationToken)
    {
        if (await ProbeSimpleSuccessAsync(InstallerConstants.DockerExecutable, ["info"], cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (platform == HostPlatformType.MacOS)
        {
            if (await ProbeSimpleSuccessAsync(InstallerConstants.ColimaExecutable, ["status"], cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (await ProbeSimpleSuccessAsync(InstallerConstants.OrbExecutable, ["status"], cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> ProbeSimpleSuccessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            new ProcessRunRequest
            {
                FileName = fileName,
                Arguments = arguments
            },
            cancellationToken).ConfigureAwait(false);

        return result.ExitCode == InstallerConstants.ExitCodeSuccess;
    }

    private static ProvisioningStatus DetermineOverallStatus(IReadOnlyList<ProvisioningStepResult> steps)
    {
        if (steps.Any(s => s.Status == ProvisioningStatus.RebootRequired))
        {
            return ProvisioningStatus.RebootRequired;
        }

        if (steps.Any(s => s.Status == ProvisioningStatus.Failed))
        {
            return ProvisioningStatus.Failed;
        }

        return steps.Any(s => s.Status == ProvisioningStatus.Completed)
            ? ProvisioningStatus.Completed
            : ProvisioningStatus.Skipped;
    }

    private static ProcessRunRequest CreateWslRequest(
        IReadOnlyList<string> arguments,
        bool useShellExecute = false,
        bool runAsAdmin = false) =>
        new()
        {
            FileName = InstallerConstants.WslExecutable,
            Arguments = arguments,
            UseShellExecute = useShellExecute,
            RunAsAdmin = runAsAdmin,
            EnvironmentVariables = useShellExecute || runAsAdmin
                ? null
                : new Dictionary<string, string> { [InstallerConstants.WslUtf8EnvVar] = "1" }
        };

    private static void Report(
        IProgress<InstallerProgressEvent>? progress,
        string step,
        string message,
        InstallerLogLevel level,
        double? percent)
    {
        progress?.Report(new InstallerProgressEvent(step, message, level, percent));
    }

    [GeneratedRegex(@"DOTNET=(\d+)")]
    private static partial Regex DotNetCountRegex();
}
