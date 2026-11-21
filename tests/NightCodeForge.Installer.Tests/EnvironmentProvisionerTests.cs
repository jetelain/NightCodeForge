namespace NightCodeForge.Installer.Tests;

using Moq;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

public sealed class EnvironmentProvisionerTests : IDisposable
{
    private readonly TempTestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task ProbeAsync_Windows_StripsNullCharsAndDetectsDistroAndToolchain()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var runner = new Mock<IProcessRunner>();

        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessRunRequest req, CancellationToken _) =>
            {
                var joined = string.Join(' ', req.Arguments);
                if (req.FileName == "dotnet" && joined == "--list-sdks")
                {
                    return new ProcessExecutionResult(0, "10.0.403 [C:\\Program Files\\dotnet\\sdk]", string.Empty);
                }

                if (req.FileName == "git")
                {
                    return new ProcessExecutionResult(0, "git version 2.47.0", string.Empty);
                }

                if (req.FileName == "rg")
                {
                    return new ProcessExecutionResult(1, string.Empty, "not found");
                }

                if (req.FileName == "wsl.exe" && joined == "--status")
                {
                    return new ProcessExecutionResult(0, "D\0e\0f\0a\0u\0l\0t\0 \0V\0e\0r\0s\0i\0o\0n\0:\0 \02\0", string.Empty);
                }

                if (req.FileName == "wsl.exe" && joined == "-l -q")
                {
                    return new ProcessExecutionResult(0, "U\0b\0u\0n\0t\0u\0-\02\04\0.\00\04\0\r\n", string.Empty);
                }

                if (req.FileName == "wsl.exe" && joined.Contains("bash -lc", StringComparison.Ordinal))
                {
                    return new ProcessExecutionResult(0, "DOTNET=1\nDOCKER=1\nRG=1\n", string.Empty);
                }

                return new ProcessExecutionResult(0, string.Empty, string.Empty);
            });

        var sut = new EnvironmentProvisioner(platform.Object, runner.Object);
        var probe = await sut.ProbeAsync("Ubuntu");

        Assert.True(probe.WslInstalled);
        Assert.True(probe.WslDistroInstalled);
        Assert.Equal("Ubuntu-24.04", probe.ResolvedWslDistroName);
        Assert.True(probe.HostDotNet10Installed);
        Assert.True(probe.HostGitInstalled);
        Assert.True(probe.WslDotNet10Installed);
        Assert.True(probe.WslDockerInstalled);
        Assert.True(probe.WslRipgrepInstalled);
    }

    [Fact]
    public async Task ProvisionAsync_WindowsWslInstallReturns3010_ShortCircuitsWithRebootRequired()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var runner = new Mock<IProcessRunner>();
        var elevatedWslInstallCalled = false;

        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessRunRequest req, CancellationToken _) =>
            {
                var joined = string.Join(' ', req.Arguments);
                if (req.FileName == "wsl.exe" && joined == "--status")
                {
                    return new ProcessExecutionResult(1, string.Empty, "WSL not installed");
                }

                if (req.FileName == "wsl.exe" && joined == "--list --verbose")
                {
                    return new ProcessExecutionResult(1, string.Empty, "WSL not installed");
                }

                if (req.FileName == "wsl.exe" && joined == "--install --no-launch" && req.RunAsAdmin)
                {
                    elevatedWslInstallCalled = true;
                    return new ProcessExecutionResult(InstallerConstants.ExitCodeRebootRequired, string.Empty, string.Empty);
                }

                return new ProcessExecutionResult(1, string.Empty, string.Empty);
            });

        var sut = new EnvironmentProvisioner(platform.Object, runner.Object);
        var report = await sut.ProvisionAsync(new ProvisioningOptions(
            WslDistroName: "Ubuntu",
            ProvisionWsl: true,
            ProvisionHostDotNet: true,
            ProvisionLinuxOrWslToolchain: true,
            SkipDocker: false));

        Assert.True(elevatedWslInstallCalled);
        Assert.Equal(ProvisioningStatus.RebootRequired, report.OverallStatus);
        Assert.Contains(report.Steps, s => s.Status == ProvisioningStatus.RebootRequired);
        runner.Verify(
            r => r.RunAsync(
                It.Is<ProcessRunRequest>(q => q.FileName == "winget"),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProvisionAsync_WindowsHostDotNet_FallsBackToDotNetInstallScriptWhenWingetFails()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var runner = new Mock<IProcessRunner>();
        var psFallbackCalled = false;

        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessRunRequest req, CancellationToken _) =>
            {
                var joined = string.Join(' ', req.Arguments);
                if (req.FileName == "dotnet" && joined == "--list-sdks")
                {
                    return new ProcessExecutionResult(0, psFallbackCalled ? "10.0.403" : "8.0.400", string.Empty);
                }

                if (req.FileName == "winget")
                {
                    return new ProcessExecutionResult(1, string.Empty, "winget failed");
                }

                if (req.FileName == "powershell.exe" && joined.Contains("dotnet-install.ps1", StringComparison.Ordinal))
                {
                    psFallbackCalled = true;
                    return new ProcessExecutionResult(0, "Installed .NET 10", string.Empty);
                }

                return new ProcessExecutionResult(0, string.Empty, string.Empty);
            });

        var sut = new EnvironmentProvisioner(platform.Object, runner.Object);
        var report = await sut.ProvisionAsync(new ProvisioningOptions(
            WslDistroName: "Ubuntu",
            ProvisionWsl: false,
            ProvisionHostDotNet: true,
            ProvisionLinuxOrWslToolchain: false,
            SkipDocker: true));

        Assert.True(psFallbackCalled);
        Assert.Equal(ProvisioningStatus.Completed, report.OverallStatus);
        Assert.Contains(report.Steps, s => s.StepName == "Host .NET 10 SDK" && s.Status == ProvisioningStatus.Completed);
    }

    [Fact]
    public void BuildLinuxToolchainScript_ContainsRequiredPackagesSystemdAndProfile()
    {
        var wslScript = EnvironmentProvisioner.BuildLinuxToolchainScript(isWsl: true, skipDocker: false);

        Assert.Contains("dotnet-sdk-10.0", wslScript, StringComparison.Ordinal);
        Assert.Contains("/etc/profile.d/dotnet.sh", wslScript, StringComparison.Ordinal);
        Assert.Contains("ripgrep", wslScript, StringComparison.Ordinal);
        Assert.Contains("libicu-dev", wslScript, StringComparison.Ordinal);
        Assert.Contains("locales", wslScript, StringComparison.Ordinal);
        Assert.Contains("docker.io", wslScript, StringComparison.Ordinal);
        Assert.Contains("systemd=true", wslScript, StringComparison.Ordinal);
        Assert.Contains("/etc/wsl.conf", wslScript, StringComparison.Ordinal);
        Assert.Contains("usermod -aG docker", wslScript, StringComparison.Ordinal);
        Assert.DoesNotContain("safe.directory '*'", wslScript, StringComparison.Ordinal);

        var nativeLinuxNoDocker = EnvironmentProvisioner.BuildLinuxToolchainScript(isWsl: false, skipDocker: true);
        Assert.DoesNotContain("/etc/wsl.conf", nativeLinuxNoDocker, StringComparison.Ordinal);
        Assert.DoesNotContain("docker.io", nativeLinuxNoDocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeAsync_MacOs_DetectsColimaDockerRuntime()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.MacOS, InstallerConstants.RuntimeOsxArm64, _workspace.RootPath);
        var runner = new Mock<IProcessRunner>();

        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessRunRequest req, CancellationToken _) =>
            {
                if (req.FileName == "dotnet")
                {
                    return new ProcessExecutionResult(0, "10.0.100", string.Empty);
                }

                if (req.FileName == "docker")
                {
                    return new ProcessExecutionResult(1, string.Empty, "cannot connect");
                }

                if (req.FileName == "colima")
                {
                    return new ProcessExecutionResult(0, "colima is running", string.Empty);
                }

                return new ProcessExecutionResult(0, "ok", string.Empty);
            });

        var sut = new EnvironmentProvisioner(platform.Object, runner.Object);
        var probe = await sut.ProbeAsync("Ubuntu");

        Assert.True(probe.HostDockerAvailable);
        Assert.True(probe.HostDotNet10Installed);
        Assert.True(probe.HostGitInstalled);
        Assert.True(probe.HostRipgrepInstalled);
    }

    [Fact]
    public async Task ProbeAsync_Windows_DoesNotFalselyDetectWslWhenErrorContainsVersionWord()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var runner = new Mock<IProcessRunner>();

        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessRunRequest req, CancellationToken _) =>
            {
                var joined = string.Join(' ', req.Arguments);
                if (req.FileName == "wsl.exe" && joined == "--status")
                {
                    return new ProcessExecutionResult(1, string.Empty, "Windows Subsystem for Linux is not installed.");
                }

                if (req.FileName == "wsl.exe" && joined == "--list --verbose")
                {
                    return new ProcessExecutionResult(1, string.Empty, "Usage: wsl.exe --version");
                }

                return new ProcessExecutionResult(1, string.Empty, string.Empty);
            });

        var sut = new EnvironmentProvisioner(platform.Object, runner.Object);
        var probe = await sut.ProbeAsync("Ubuntu");

        Assert.False(probe.WslInstalled);
        Assert.False(probe.WslDistroInstalled);
    }

    [Fact]
    public void StripNullCharacters_RemovesNullsUtf16BomAndReplacementChars()
    {
        var raw = "\uFEFF\uFFFD" + "U\0b\0u\0n\0t\0u\0\r\n";
        var cleaned = EnvironmentProvisioner.StripNullCharacters(raw);
        Assert.Equal("Ubuntu\r\n", cleaned);
    }

    [Fact]
    public void BuildLinuxToolchainScript_UsesOsReleaseVersionIdSafeDotNetRootAndUpdatesSystemdFalse()
    {
        var script = EnvironmentProvisioner.BuildLinuxToolchainScript(isWsl: true, skipDocker: false);

        Assert.Contains("/etc/os-release", script, StringComparison.Ordinal);
        Assert.Contains("${DISTRO_ID}/${DISTRO_VERSION}/packages-microsoft-prod.deb", script, StringComparison.Ordinal);
        Assert.DoesNotContain("packages.microsoft.com/config/ubuntu/${UBUNTU_CODENAME}", script, StringComparison.Ordinal);
        Assert.Contains("${DOTNET_ROOT:+:$DOTNET_ROOT}", script, StringComparison.Ordinal);
        Assert.Contains("$HOME/.dotnet", script, StringComparison.Ordinal);
        Assert.Contains("sed -i -E 's/^[[:space:]]*systemd[[:space:]]*=.*/systemd=true/' /etc/wsl.conf", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvisionAsync_MacOs_RespectsSkipDockerAndInstallsMissingToolsViaBrew()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.MacOS, InstallerConstants.RuntimeOsxArm64, _workspace.RootPath);
        platform.Setup(p => p.IsCommandOnPath("brew")).Returns(true);

        var brewInstalled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessRunRequest req, CancellationToken _) =>
            {
                if (req.FileName == "dotnet")
                {
                    return new ProcessExecutionResult(0, "10.0.100", string.Empty);
                }

                if (req.FileName == "brew" && req.Arguments.Count >= 2 && req.Arguments[0] == "install")
                {
                    brewInstalled.Add(req.Arguments[1]);
                    return new ProcessExecutionResult(0, "installed", string.Empty);
                }

                if (req.FileName == "git")
                {
                    return brewInstalled.Contains("git")
                        ? new ProcessExecutionResult(0, "git 2.47", string.Empty)
                        : new ProcessExecutionResult(1, string.Empty, "missing");
                }

                if (req.FileName == "rg")
                {
                    return brewInstalled.Contains("ripgrep")
                        ? new ProcessExecutionResult(0, "ripgrep 14", string.Empty)
                        : new ProcessExecutionResult(1, string.Empty, "missing");
                }

                // Docker runtime is absent/stopped on macOS.
                return new ProcessExecutionResult(1, string.Empty, "docker unavailable");
            });

        var sut = new EnvironmentProvisioner(platform.Object, runner.Object);
        var report = await sut.ProvisionAsync(new ProvisioningOptions(
            WslDistroName: "Ubuntu",
            ProvisionWsl: false,
            ProvisionHostDotNet: false,
            ProvisionLinuxOrWslToolchain: true,
            SkipDocker: true));

        Assert.Equal(ProvisioningStatus.Completed, report.OverallStatus);
        Assert.Contains("ripgrep", brewInstalled);
        Assert.Contains("git", brewInstalled);
    }

    [Fact]
    public async Task ProvisionAsync_Linux_RunsSudoForNonRootAndBashForRoot()
    {
        var runner = new Mock<IProcessRunner>();
        var executedCommands = new List<(string FileName, IReadOnlyList<string> Args)>();
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessRunRequest req, CancellationToken _) =>
            {
                executedCommands.Add((req.FileName, req.Arguments));
                if (req.FileName is InstallerConstants.GitExecutable or InstallerConstants.RipgrepExecutable or InstallerConstants.DockerExecutable)
                {
                    return new ProcessExecutionResult(1, string.Empty, "missing");
                }

                return new ProcessExecutionResult(0, "ok", string.Empty);
            });

        var nonRootPlatform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        nonRootPlatform.Setup(p => p.GetEnvironmentVariable("USER")).Returns("devuser");
        var nonRootSut = new EnvironmentProvisioner(nonRootPlatform.Object, runner.Object);
        var nonRootReport = await nonRootSut.ProvisionAsync(new ProvisioningOptions("Ubuntu", false, false, true, SkipDocker: false));
        Assert.Equal(ProvisioningStatus.Completed, nonRootReport.OverallStatus);
        Assert.Contains(executedCommands, c =>
            c.FileName == InstallerConstants.SudoExecutable &&
            c.Args.Count >= 2 &&
            c.Args[0] == "-n" &&
            c.Args[1] == InstallerConstants.BashExecutable);

        executedCommands.Clear();
        nonRootPlatform.Setup(p => p.GetEnvironmentVariable(InstallerConstants.EnvDisplayUnix)).Returns(":0");
        nonRootPlatform.Setup(p => p.IsCommandOnPath(InstallerConstants.PkexecExecutable)).Returns(true);
        var guiReport = await nonRootSut.ProvisionAsync(new ProvisioningOptions("Ubuntu", false, false, true, SkipDocker: false));
        Assert.Equal(ProvisioningStatus.Completed, guiReport.OverallStatus);
        Assert.Contains(executedCommands, c => c.FileName == InstallerConstants.PkexecExecutable);

        executedCommands.Clear();
        var rootPlatform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        rootPlatform.Setup(p => p.GetEnvironmentVariable("USER")).Returns("root");
        var rootSut = new EnvironmentProvisioner(rootPlatform.Object, runner.Object);
        var rootReport = await rootSut.ProvisionAsync(new ProvisioningOptions("Ubuntu", false, false, true, SkipDocker: false));
        Assert.Equal(ProvisioningStatus.Completed, rootReport.OverallStatus);
        Assert.Contains(executedCommands, c => c.FileName == "bash");
        Assert.DoesNotContain(executedCommands, c => c.FileName == "sudo");
    }

    [Fact]
    public async Task ProvisionAsync_WindowsHostDotNet_FallbackScriptPersistsLocalAppDataDotNetToUserPath()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var runner = new Mock<IProcessRunner>();
        string? capturedPowerShellCommand = null;

        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessRunRequest req, CancellationToken _) =>
            {
                if (req.FileName == "dotnet")
                {
                    return new ProcessExecutionResult(0, capturedPowerShellCommand is null ? "8.0.400" : "10.0.100", string.Empty);
                }

                if (req.FileName == "winget")
                {
                    return new ProcessExecutionResult(1, string.Empty, "winget failed");
                }

                if (req.FileName == "powershell.exe")
                {
                    capturedPowerShellCommand = string.Join(' ', req.Arguments);
                    return new ProcessExecutionResult(0, "ok", string.Empty);
                }

                return new ProcessExecutionResult(0, string.Empty, string.Empty);
            });

        var sut = new EnvironmentProvisioner(platform.Object, runner.Object);
        var report = await sut.ProvisionAsync(new ProvisioningOptions("Ubuntu", false, true, false, true));

        Assert.Equal(ProvisioningStatus.Completed, report.OverallStatus);
        Assert.NotNull(capturedPowerShellCommand);
        Assert.Contains(@"Microsoft\dotnet", capturedPowerShellCommand, StringComparison.Ordinal);
        Assert.Contains("SetEnvironmentVariable('Path'", capturedPowerShellCommand, StringComparison.Ordinal);
        platform.Verify(p => p.RefreshProcessPath(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ProvisionAsync_Windows_ReportsPerItemProgressForVerifiedInstalledAndSkippedSteps()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var runner = new Mock<IProcessRunner>();
        var wslToolchainRan = false;

        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessRunRequest req, CancellationToken _) =>
            {
                var joined = string.Join(' ', req.Arguments);
                if (req.FileName == "dotnet" && joined == "--list-sdks")
                {
                    return new ProcessExecutionResult(0, "10.0.403 [C:\\Program Files\\dotnet\\sdk]", string.Empty);
                }

                if (req.FileName == "wsl.exe" && joined == "--status")
                {
                    return new ProcessExecutionResult(0, "Default Version: 2", string.Empty);
                }

                if (req.FileName == "wsl.exe" && joined == "-l -q")
                {
                    return new ProcessExecutionResult(0, "Ubuntu\r\n", string.Empty);
                }

                if (req.FileName == "wsl.exe" && joined.Contains("bash -lc", StringComparison.Ordinal))
                {
                    return new ProcessExecutionResult(0, wslToolchainRan ? "DOTNET=1\nDOCKER=0\nRG=1\n" : "DOTNET=0\nDOCKER=0\nRG=0\n", string.Empty);
                }

                if (req.FileName == "wsl.exe" && joined.Contains("wslpath", StringComparison.Ordinal))
                {
                    return new ProcessExecutionResult(0, "/mnt/c/temp/provision.sh\n", string.Empty);
                }

                if (req.FileName == "wsl.exe" && joined.Contains("bash /mnt/c/temp/provision.sh", StringComparison.Ordinal))
                {
                    wslToolchainRan = true;
                    return new ProcessExecutionResult(0, "ok", string.Empty);
                }

                return new ProcessExecutionResult(0, string.Empty, string.Empty);
            });

        var itemEvents = new List<InstallerProgressEvent>();
        var sut = new EnvironmentProvisioner(platform.Object, runner.Object);
        var report = await sut.ProvisionAsync(
            new ProvisioningOptions(
                WslDistroName: "Ubuntu",
                ProvisionWsl: true,
                ProvisionHostDotNet: true,
                ProvisionLinuxOrWslToolchain: true,
                SkipDocker: true),
            new SynchronousProgress<InstallerProgressEvent>(e =>
            {
                if (!string.IsNullOrWhiteSpace(e.ItemKey) && e.ItemStatus.HasValue)
                {
                    itemEvents.Add(e);
                }
            }));

        Assert.Equal(ProvisioningStatus.Completed, report.OverallStatus);
        Assert.Contains(itemEvents, e => e.ItemKey == ProvisioningStepNames.Wsl2 && e.ItemStatus == ProvisioningStatus.Verified);
        Assert.Contains(itemEvents, e => e.ItemKey == ProvisioningStepNames.Wsl2Distro && e.ItemStatus == ProvisioningStatus.Verified);
        Assert.Contains(itemEvents, e => e.ItemKey == ProvisioningStepNames.HostDotNet && e.ItemStatus == ProvisioningStatus.Verified);
        Assert.Contains(itemEvents, e => e.ItemKey == ProvisioningStepNames.SubsystemToolchain && e.ItemStatus == ProvisioningStatus.InProgress);
        Assert.Contains(itemEvents, e => e.ItemKey == ProvisioningStepNames.SubsystemToolchain && e.ItemStatus == ProvisioningStatus.Completed);
        Assert.Contains(itemEvents, e => e.ItemKey == ProvisioningStepNames.DockerEngine && e.ItemStatus == ProvisioningStatus.Skipped);
    }

    [Fact]
    public async Task ProvisionAsync_Linux_HostDotNetOnlyWithSkipDockerFalse_SkipsDockerAndExcludesDockerFromScript()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        platform.Setup(p => p.GetEnvironmentVariable(InstallerConstants.EnvUserUnix)).Returns(InstallerConstants.RootUserUnix);

        string? capturedScript = null;
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessRunRequest req, CancellationToken _) =>
            {
                if (req.FileName == InstallerConstants.DotNetExecutable)
                {
                    return new ProcessExecutionResult(1, string.Empty, "not installed");
                }

                if (req.FileName == InstallerConstants.BashExecutable && req.Arguments.Count > 0 && File.Exists(req.Arguments[0]))
                {
                    capturedScript = File.ReadAllText(req.Arguments[0]);
                    return new ProcessExecutionResult(0, "ok", string.Empty);
                }

                return new ProcessExecutionResult(0, "ok", string.Empty);
            });

        var sut = new EnvironmentProvisioner(platform.Object, runner.Object);
        var report = await sut.ProvisionAsync(new ProvisioningOptions(
            WslDistroName: InstallerConstants.DefaultWslDistro,
            ProvisionWsl: false,
            ProvisionHostDotNet: true,
            ProvisionLinuxOrWslToolchain: false,
            SkipDocker: false));

        Assert.Equal(ProvisioningStatus.Completed, report.OverallStatus);
        Assert.Contains(report.Steps, s => s.StepName == ProvisioningStepNames.DockerEngine && s.Status == ProvisioningStatus.Skipped);
        Assert.NotNull(capturedScript);
        Assert.Contains("dotnet-sdk-10.0", capturedScript, StringComparison.Ordinal);
        Assert.DoesNotContain("docker.io", capturedScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvisionAsync_MacOs_ReportsMonotonicProgressPercentages()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.MacOS, InstallerConstants.RuntimeOsxArm64, _workspace.RootPath);
        platform.Setup(p => p.IsCommandOnPath(InstallerConstants.BrewExecutable)).Returns(true);

        var gitInstalled = false;
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessRunRequest req, CancellationToken _) =>
            {
                if (req.FileName == InstallerConstants.DotNetExecutable)
                {
                    return new ProcessExecutionResult(0, "10.0.100", string.Empty);
                }

                if (req.FileName == InstallerConstants.BrewExecutable && req.Arguments.Count >= 2 && req.Arguments[1] == InstallerConstants.GitExecutable)
                {
                    gitInstalled = true;
                    return new ProcessExecutionResult(0, "installed git", string.Empty);
                }

                if (req.FileName == InstallerConstants.GitExecutable)
                {
                    return gitInstalled
                        ? new ProcessExecutionResult(0, "git 2.47", string.Empty)
                        : new ProcessExecutionResult(1, string.Empty, "missing");
                }

                if (req.FileName == InstallerConstants.RipgrepExecutable)
                {
                    return new ProcessExecutionResult(0, "rg 14", string.Empty);
                }

                if (req.FileName == InstallerConstants.DockerExecutable)
                {
                    return new ProcessExecutionResult(0, "Docker running", string.Empty);
                }

                return new ProcessExecutionResult(0, "ok", string.Empty);
            });

        var percentages = new List<double>();
        var sut = new EnvironmentProvisioner(platform.Object, runner.Object);
        var report = await sut.ProvisionAsync(
            new ProvisioningOptions(
                WslDistroName: InstallerConstants.DefaultWslDistro,
                ProvisionWsl: false,
                ProvisionHostDotNet: true,
                ProvisionLinuxOrWslToolchain: true,
                SkipDocker: false),
            new SynchronousProgress<InstallerProgressEvent>(e =>
            {
                if (e.ProgressPercent.HasValue)
                {
                    percentages.Add(e.ProgressPercent.Value);
                }
            }));

        Assert.Equal(ProvisioningStatus.Completed, report.OverallStatus);
        Assert.True(percentages.Count >= 4);
        for (var i = 1; i < percentages.Count; i++)
        {
            Assert.True(percentages[i] >= percentages[i - 1], $"Progress went backwards: {percentages[i - 1]} -> {percentages[i]}");
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
