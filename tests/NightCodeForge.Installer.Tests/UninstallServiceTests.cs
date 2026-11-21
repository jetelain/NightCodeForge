namespace NightCodeForge.Installer.Tests;

using Moq;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

public sealed class UninstallServiceTests : IDisposable
{
    private readonly TempTestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task UninstallAsync_KeepDataTrue_PreservesConfigDataLogsAndSchedulesDeferredExeDelete()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var installDir = Path.Combine(platform.Object.LocalApplicationDataDirectory, "NightCodeForge");
        Directory.CreateDirectory(Path.Combine(installDir, "config"));
        Directory.CreateDirectory(Path.Combine(installDir, "data"));
        Directory.CreateDirectory(Path.Combine(installDir, "logs"));
        Directory.CreateDirectory(Path.Combine(installDir, "bin"));
        File.WriteAllText(Path.Combine(installDir, InstallerConstants.WebExeWindows), "web");
        File.WriteAllText(Path.Combine(installDir, "config", "config.json"), "{}");
        File.WriteAllText(Path.Combine(installDir, "data", "nightcodeforge.db"), "db");
        File.WriteAllText(Path.Combine(installDir, "logs", "app.log"), "log");
        var selfExePath = Path.Combine(installDir, InstallerConstants.InstallerExeWindows);
        File.WriteAllText(selfExePath, "self-exe");

        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var processControl = new Mock<IProcessControlService>();
        processControl.SetupGet(c => c.CurrentProcessId).Returns(12345);
        processControl.SetupGet(c => c.CurrentProcessPath).Returns(selfExePath);

        var registry = new Mock<IWindowsRegistryAccessor>();
        var detector = new InstallationDetector(platform.Object, registry.Object);
        var nuGet = new Mock<ICompanionNuGetProvisioner>();
        var osIntegration = new OsIntegrationService(platform.Object, registry.Object, runner.Object, TimeProvider.System);

        var sut = new UninstallService(
            platform.Object,
            runner.Object,
            processControl.Object,
            detector,
            nuGet.Object,
            osIntegration);

        var result = await sut.UninstallAsync(
            new UninstallOptions(KeepData: true, RemoveCompanionTools: false, RemoveOllamaRouter: false, Quiet: true));

        Assert.True(result.Succeeded);
        Assert.True(result.DeferredSelfCleanupScheduled);
        Assert.False(File.Exists(Path.Combine(installDir, InstallerConstants.WebExeWindows)));
        Assert.False(Directory.Exists(Path.Combine(installDir, "bin")));
        Assert.True(File.Exists(Path.Combine(installDir, "config", "config.json")));
        Assert.True(File.Exists(Path.Combine(installDir, "data", "nightcodeforge.db")));
        Assert.True(File.Exists(Path.Combine(installDir, "logs", "app.log")));

        runner.Verify(
            r => r.RunAsync(
                It.Is<ProcessRunRequest>(q =>
                    q.FileName == "cmd.exe" &&
                    q.Detached &&
                    q.Arguments.Count == 0 &&
                    q.RawArguments != null &&
                    q.RawArguments.StartsWith("/c \"", StringComparison.Ordinal) &&
                    q.RawArguments.Contains("del /f /q", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        nuGet.Verify(n => n.UninstallAllAsync(It.IsAny<string?>(), It.IsAny<IProgress<InstallerProgressEvent>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UninstallAsync_FullRemovalWithToolsAndOllamaRouter_CleansAllComponents()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var installDir = Path.Combine(platform.Object.LocalApplicationDataDirectory, "NightCodeForge");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, InstallerConstants.WebExeWindows), "web");

        var routerDir = Path.Combine(platform.Object.LocalApplicationDataDirectory, "OllamaRouter");
        Directory.CreateDirectory(Path.Combine(routerDir, "config"));
        Directory.CreateDirectory(Path.Combine(routerDir, "bin"));
        File.WriteAllText(Path.Combine(routerDir, InstallerConstants.OllamaRouterServerWindows), "server");
        File.WriteAllText(Path.Combine(routerDir, InstallerConstants.OllamaRouterCliWindows), "cli");

        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var processControl = new Mock<IProcessControlService>();
        processControl.SetupGet(c => c.CurrentProcessPath).Returns(Path.Combine(_workspace.RootPath, "external", InstallerConstants.InstallerExeWindows));

        var registry = new Mock<IWindowsRegistryAccessor>();
        var detector = new InstallationDetector(platform.Object, registry.Object);
        var nuGet = new Mock<ICompanionNuGetProvisioner>();
        nuGet.Setup(n => n.UninstallAllAsync(It.IsAny<string?>(), It.IsAny<IProgress<InstallerProgressEvent>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var osIntegration = new OsIntegrationService(platform.Object, registry.Object, runner.Object, TimeProvider.System);

        var sut = new UninstallService(
            platform.Object,
            runner.Object,
            processControl.Object,
            detector,
            nuGet.Object,
            osIntegration);

        var result = await sut.UninstallAsync(
            new UninstallOptions(KeepData: false, RemoveCompanionTools: true, RemoveOllamaRouter: true, Quiet: true),
            wslDistroName: "Ubuntu");

        Assert.True(result.Succeeded);
        Assert.False(result.DeferredSelfCleanupScheduled);
        Assert.False(Directory.Exists(installDir));
        Assert.False(Directory.Exists(routerDir));

        nuGet.Verify(n => n.UninstallAllAsync("Ubuntu", It.IsAny<IProgress<InstallerProgressEvent>?>(), It.IsAny<CancellationToken>()), Times.Once);
        registry.Verify(r => r.DeleteCurrentUserSubKeyTree(InstallerConstants.WindowsUninstallRegistrySubKey), Times.Once);
        registry.Verify(r => r.DeleteCurrentUserSubKeyTree(InstallerConstants.WindowsOllamaRouterProtocolSubKey), Times.Once);
        runner.Verify(
            r => r.RunAsync(
                It.Is<ProcessRunRequest>(q => q.FileName.EndsWith(InstallerConstants.OllamaRouterCliWindows, StringComparison.OrdinalIgnoreCase) && q.Arguments.Contains("stop")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void BuildWindowsDeferredSelfCleanupCommand_HandlesKeepDataToggle()
    {
        var keepCmd = UninstallService.BuildWindowsDeferredSelfCleanupCommand(
            @"C:\App\NightCodeForge",
            @"C:\App\NightCodeForge\NightCodeForge.Installer.exe",
            keepData: true);
        Assert.Contains("del /f /q \"C:\\App\\NightCodeForge\\NightCodeForge.Installer.exe\"", keepCmd, StringComparison.Ordinal);

        var fullCmd = UninstallService.BuildWindowsDeferredSelfCleanupCommand(
            @"C:\App\NightCodeForge",
            @"C:\App\NightCodeForge\NightCodeForge.Installer.exe",
            keepData: false);
        Assert.Contains("rmdir /s /q \"C:\\App\\NightCodeForge\"", fullCmd, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UninstallAsync_DeferredSelfCleanupSpawnFails_ReturnsSucceededFalse()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var installDir = Path.Combine(platform.Object.LocalApplicationDataDirectory, "NightCodeForge");
        Directory.CreateDirectory(installDir);
        var selfExePath = Path.Combine(installDir, InstallerConstants.InstallerExeWindows);
        File.WriteAllText(selfExePath, "self-exe");

        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.Is<ProcessRunRequest>(q => q.FileName == "cmd.exe"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(2, string.Empty, "cmd.exe not found"));
        runner.Setup(r => r.RunAsync(It.Is<ProcessRunRequest>(q => q.FileName != "cmd.exe"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var processControl = new Mock<IProcessControlService>();
        processControl.SetupGet(c => c.CurrentProcessPath).Returns(selfExePath);

        var registry = new Mock<IWindowsRegistryAccessor>();
        var detector = new InstallationDetector(platform.Object, registry.Object);
        var nuGet = new Mock<ICompanionNuGetProvisioner>();
        var osIntegration = new OsIntegrationService(platform.Object, registry.Object, runner.Object, TimeProvider.System);

        var sut = new UninstallService(platform.Object, runner.Object, processControl.Object, detector, nuGet.Object, osIntegration);
        var result = await sut.UninstallAsync(new UninstallOptions(KeepData: false, RemoveCompanionTools: false, RemoveOllamaRouter: false, Quiet: true));

        Assert.False(result.Succeeded);
        Assert.False(result.DeferredSelfCleanupScheduled);
        Assert.Contains("self-cleanup", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UninstallAsync_RunningExeInSubdirectoryOfInstallDir_CleansSiblingsWithoutThrowing()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var installDir = Path.Combine(platform.Object.LocalApplicationDataDirectory, "NightCodeForge");
        var binDir = Path.Combine(installDir, "bin");
        Directory.CreateDirectory(binDir);
        var selfExeInBin = Path.Combine(binDir, InstallerConstants.InstallerExeWindows);
        File.WriteAllText(selfExeInBin, "self-exe");
        File.WriteAllText(Path.Combine(binDir, "sibling.exe"), "sibling");
        File.WriteAllText(Path.Combine(installDir, InstallerConstants.WebExeWindows), "web");

        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var processControl = new Mock<IProcessControlService>();
        processControl.SetupGet(c => c.CurrentProcessPath).Returns(selfExeInBin);

        var registry = new Mock<IWindowsRegistryAccessor>();
        var detector = new InstallationDetector(platform.Object, registry.Object);
        var nuGet = new Mock<ICompanionNuGetProvisioner>();
        var osIntegration = new OsIntegrationService(platform.Object, registry.Object, runner.Object, TimeProvider.System);

        var sut = new UninstallService(platform.Object, runner.Object, processControl.Object, detector, nuGet.Object, osIntegration);
        var result = await sut.UninstallAsync(new UninstallOptions(KeepData: false, RemoveCompanionTools: false, RemoveOllamaRouter: false, Quiet: true));

        Assert.True(result.Succeeded);
        Assert.True(result.DeferredSelfCleanupScheduled);
        Assert.False(File.Exists(Path.Combine(installDir, InstallerConstants.WebExeWindows)));
        Assert.False(File.Exists(Path.Combine(binDir, "sibling.exe")));
        Assert.True(File.Exists(selfExeInBin));
    }

    [Fact]
    public async Task UninstallAsync_RemoveOllamaRouter_FallsBackToUninstallManifestDirectory()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        var installDir = _workspace.CreateSubdirectory("ncf-manifest");
        var customRouterDir = _workspace.CreateSubdirectory("custom-router-from-manifest");
        File.WriteAllText(Path.Combine(customRouterDir, "marker.txt"), "router-file");

        var manifestJson = System.Text.Json.JsonSerializer.Serialize(
            new UninstallManifest("1.0.0", installDir, DateTimeOffset.UtcNow, customRouterDir, "Linux"),
            NightCodeForge.Installer.Core.Serialization.InstallerJsonContext.Default.UninstallManifest);
        File.WriteAllText(Path.Combine(installDir, InstallerConstants.UninstallManifestFileName), manifestJson);

        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));
        var processControl = new Mock<IProcessControlService>();
        var registry = new Mock<IWindowsRegistryAccessor>();
        var detector = new InstallationDetector(platform.Object, registry.Object);
        var nuGet = new Mock<ICompanionNuGetProvisioner>();
        var osIntegration = new OsIntegrationService(platform.Object, registry.Object, runner.Object, TimeProvider.System);

        var sut = new UninstallService(platform.Object, runner.Object, processControl.Object, detector, nuGet.Object, osIntegration);
        var result = await sut.UninstallAsync(
            new UninstallOptions(KeepData: false, RemoveCompanionTools: false, RemoveOllamaRouter: true, Quiet: true),
            targetInstallDirectory: installDir);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(customRouterDir));
    }

    [Fact]
    public void IsPathInsideDirectory_HandlesDriveRootWithoutDoubleSeparator()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(UninstallService.IsPathInsideDirectory(@"C:\NightCodeForge.Installer.exe", @"C:\"));
            Assert.False(UninstallService.IsPathInsideDirectory(@"D:\NightCodeForge.Installer.exe", @"C:\"));
        }
        else
        {
            Assert.True(UninstallService.IsPathInsideDirectory("/tmp/installer", "/"));
        }
    }
}
