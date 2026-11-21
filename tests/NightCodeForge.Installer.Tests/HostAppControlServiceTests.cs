namespace NightCodeForge.Installer.Tests;

using System.ComponentModel;
using Moq;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

public sealed class HostAppControlServiceTests : IDisposable
{
    private readonly TempTestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData("http://*:5270", "http://localhost:5270")]
    [InlineData("https://+:8443/", "https://localhost:8443")]
    [InlineData("http://0.0.0.0:5000", "http://localhost:5000")]
    [InlineData("http://[::]:5270", "http://localhost:5270")]
    [InlineData("http://*", "http://localhost")]
    [InlineData("https://+", "https://localhost")]
    [InlineData("http://0.0.0.0/app/", "http://localhost/app")]
    [InlineData("file:///etc/passwd;https://[::]:9443", "https://localhost:9443")]
    [InlineData("file:///tmp/bad", InstallerConstants.DefaultWebUrls)]
    [InlineData("javascript:alert(1)", InstallerConstants.DefaultWebUrls)]
    [InlineData("", InstallerConstants.DefaultWebUrls)]
    public void NormalizeListeningUrlToLocalhost_HandlesWildcardsPortlessUrlsAndRejectsUnsafeSchemes(
        string rawUrls,
        string expected)
    {
        Assert.Equal(expected, HostAppControlService.NormalizeListeningUrlToLocalhost(rawUrls));
    }

    [Fact]
    public void GetConfiguredWebUrl_HonorsConfigurationPrecedence()
    {
        var installDir = _workspace.CreateSubdirectory("ncf-config");
        Directory.CreateDirectory(Path.Combine(installDir, InstallerConstants.ConfigDirectoryName));
        File.WriteAllText(
            Path.Combine(installDir, InstallerConstants.AppSettingsFileName),
            """{"Urls": "http://localhost:5001"}""");
        File.WriteAllText(
            Path.Combine(installDir, InstallerConstants.AppSettingsProductionFileName),
            """{"Urls": "http://0.0.0.0:5280"}""");

        var sut = CreateSut(HostPlatformType.Windows, out _, out _, out _);
        Assert.Equal("http://localhost:5280", sut.GetConfiguredWebUrl(installDir));

        File.WriteAllText(
            Path.Combine(installDir, InstallerConstants.ConfigDirectoryName, InstallerConstants.UserConfigFileName),
            """{"urls": "https://*:6443/"}""");
        Assert.Equal("https://localhost:6443", sut.GetConfiguredWebUrl(installDir));
    }

    [Fact]
    public async Task StartAndStopWebServerAsync_GuardsAlreadyRunningAndVerifiesStoppedState()
    {
        var installDir = _workspace.CreateSubdirectory("ncf-run");
        Directory.CreateDirectory(Path.Combine(installDir, InstallerConstants.BinDirectoryName));
        var shortLauncher = Path.Combine(installDir, InstallerConstants.BinDirectoryName, InstallerConstants.LauncherShortWindows);
        File.WriteAllText(shortLauncher, "exe");

        var isRunning = true;
        var sut = CreateSut(HostPlatformType.Windows, out var runner, out var processControl, out _);
        processControl.Setup(c => c.IsAnyProcessRunning(It.IsAny<IEnumerable<string>>())).Returns(() => isRunning);
        processControl.Setup(c => c.StopProcessesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Already running -> short-circuits without spawning second instance
        Assert.True(await sut.StartWebServerAsync(installDir));
        runner.Verify(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // Stop succeeds only when processes stop AND IsWebServerRunning becomes false
        Assert.False(await sut.StopWebServerAsync());
        isRunning = false;
        Assert.True(await sut.StopWebServerAsync());

        // Stopped -> spawns preferred bin/ncf-web.exe
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));
        Assert.True(await sut.StartWebServerAsync(installDir));
        runner.Verify(r => r.RunAsync(
            It.Is<ProcessRunRequest>(q => q.FileName == shortLauncher && q.Detached),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpenWebUiInBrowserAsync_AndLaunchers_EnforceAllowedSchemesAndHandleWin32Exception()
    {
        var sut = CreateSut(HostPlatformType.Windows, out var runner, out _, out _);

        Assert.False(await sut.OpenWebUiInBrowserAsync("file:///C:/Windows/System32/cmd.exe"));
        Assert.False(await sut.OpenWebUiInBrowserAsync("ftp://example.com"));
        runner.Verify(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Win32Exception("No browser"));
        Assert.False(await sut.OpenWebUiInBrowserAsync("https://localhost:5270"));
        Assert.False(await sut.OpenOllamaRouterAppAsync());
    }

    [Fact]
    public async Task OpenCliTerminalAsync_Linux_ProbesTerminalFallbacksAndHandlesNoneAvailable()
    {
        var workDir = _workspace.CreateSubdirectory("terminal-dir");
        var sut = CreateSut(HostPlatformType.Linux, out var runner, out _, out var platform);
        platform.Setup(p => p.IsCommandOnPath("kitty")).Returns(true);
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        Assert.True(await sut.OpenCliTerminalAsync(workDir));
        runner.Verify(r => r.RunAsync(
            It.Is<ProcessRunRequest>(q => q.FileName == "kitty" && q.Detached),
            It.IsAny<CancellationToken>()), Times.Once);

        platform.Setup(p => p.IsCommandOnPath("kitty")).Returns(false);
        Assert.False(await sut.OpenCliTerminalAsync(workDir));
    }

    [Fact]
    public void ScanBundleDirectoryAssets_FiltersKnownBundlesAndIgnoresStrayFiles()
    {
        var bundleDir = _workspace.CreateSubdirectory("bundle-scan");
        File.WriteAllText(Path.Combine(bundleDir, "NightCodeForge-Web-win-x64.zip"), "zip");
        File.WriteAllText(Path.Combine(bundleDir, "OllamaRouter-linux-x64.tar.gz"), "tgz");
        File.WriteAllText(Path.Combine(bundleDir, "NightCodeForge.Cli.1.0.0.nupkg"), "nupkg");
        File.WriteAllText(Path.Combine(bundleDir, InstallerConstants.Sha256SumsFileName), "sums");
        File.WriteAllText(Path.Combine(bundleDir, "unrelated.zip"), "ignore");
        File.WriteAllText(Path.Combine(bundleDir, "notes.txt"), "ignore");

        var sut = CreateSut(HostPlatformType.Windows, out _, out _, out _);
        var assets = sut.ScanBundleDirectoryAssets(bundleDir);

        Assert.Equal(4, assets.Count);
        Assert.Contains("NightCodeForge-Web-win-x64.zip", assets);
        Assert.Contains("OllamaRouter-linux-x64.tar.gz", assets);
        Assert.Contains("NightCodeForge.Cli.1.0.0.nupkg", assets);
        Assert.Contains(InstallerConstants.Sha256SumsFileName, assets);
        Assert.DoesNotContain("unrelated.zip", assets);
        Assert.Empty(sut.ScanBundleDirectoryAssets(Path.Combine(_workspace.RootPath, "missing")));
    }

    private HostAppControlService CreateSut(
        HostPlatformType platformType,
        out Mock<IProcessRunner> runner,
        out Mock<IProcessControlService> processControl,
        out Mock<IPlatformEnvironment> platform)
    {
        var rid = platformType switch
        {
            HostPlatformType.Windows => InstallerConstants.RuntimeWinX64,
            HostPlatformType.MacOS => InstallerConstants.RuntimeOsxArm64,
            _ => InstallerConstants.RuntimeLinuxX64
        };
        platform = TestArchiveHelper.CreateMockPlatform(platformType, rid, _workspace.RootPath);
        runner = new Mock<IProcessRunner>();
        processControl = new Mock<IProcessControlService>();
        var detector = new Mock<IInstallationDetector>();
        detector.Setup(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(new InstallationDiscoveryResult(
                new InstalledComponentInfo(false, _workspace.RootPath, null, false, false, false, false, null),
                new InstalledComponentInfo(false, _workspace.RootPath, null, false, false, false, false, null),
                InstallSourceMode.Online,
                null,
                []));

        return new HostAppControlService(platform.Object, runner.Object, processControl.Object, detector.Object);
    }
}
