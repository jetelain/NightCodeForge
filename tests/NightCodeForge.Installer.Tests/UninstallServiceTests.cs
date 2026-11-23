namespace NightCodeForge.Installer.Tests;

using System.Text;
using Moq;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
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
        var installerSubDir = Path.Combine(installDir, InstallerConstants.InstallerSubDirectoryName);
        Directory.CreateDirectory(Path.Combine(installDir, "config"));
        Directory.CreateDirectory(Path.Combine(installDir, "data"));
        Directory.CreateDirectory(Path.Combine(installDir, "logs"));
        Directory.CreateDirectory(Path.Combine(installDir, "bin"));
        Directory.CreateDirectory(installerSubDir);
        File.WriteAllText(Path.Combine(installDir, InstallerConstants.WebExeWindows), "web");
        File.WriteAllText(Path.Combine(installDir, "config", "config.json"), "{}");
        File.WriteAllText(Path.Combine(installDir, "data", "nightcodeforge.db"), "db");
        File.WriteAllText(Path.Combine(installDir, "logs", "app.log"), "log");
        var selfExePath = Path.Combine(installerSubDir, InstallerConstants.InstallerExeWindows);
        var nativeDllPath = Path.Combine(installerSubDir, "av_libglesv2.dll");
        File.WriteAllText(selfExePath, "self-exe");
        File.WriteAllText(nativeDllPath, "native-dll");

        var runner = new Mock<IProcessRunner>();
        ProcessRunRequest? cleanupRequest = null;
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessRunRequest, CancellationToken>((req, _) =>
            {
                if (req.FileName == InstallerConstants.PowerShellExecutable)
                {
                    cleanupRequest = req;
                }
            })
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var processControl = new Mock<IProcessControlService>();
        processControl.SetupGet(c => c.CurrentProcessId).Returns(12345);
        processControl.SetupGet(c => c.CurrentProcessPath).Returns(selfExePath);

        var registry = new Mock<IWindowsRegistryAccessor>();
        var nuGet = new Mock<ICompanionNuGetProvisioner>();
        var sut = CreateSut(platform.Object, runner.Object, processControl.Object, registry.Object, nuGet.Object);

        var result = await sut.UninstallAsync(
            new UninstallOptions(KeepData: true, RemoveCompanionTools: false, RemoveOllamaRouter: false, Quiet: true));

        Assert.True(result.Succeeded);
        Assert.True(result.DeferredSelfCleanupScheduled);
        Assert.False(File.Exists(Path.Combine(installDir, InstallerConstants.WebExeWindows)));
        Assert.False(Directory.Exists(Path.Combine(installDir, "bin")));
        Assert.True(File.Exists(selfExePath));
        Assert.True(File.Exists(nativeDllPath));
        Assert.True(File.Exists(Path.Combine(installDir, "config", "config.json")));
        Assert.True(File.Exists(Path.Combine(installDir, "data", "nightcodeforge.db")));
        Assert.True(File.Exists(Path.Combine(installDir, "logs", "app.log")));

        Assert.NotNull(cleanupRequest);
        Assert.True(cleanupRequest!.Detached);
        var encodedIdx = cleanupRequest.Arguments.ToList().IndexOf("-EncodedCommand");
        Assert.True(encodedIdx >= 0 && encodedIdx + 1 < cleanupRequest.Arguments.Count);
        var decodedScript = Encoding.Unicode.GetString(Convert.FromBase64String(cleanupRequest.Arguments[encodedIdx + 1]));
        Assert.Contains("Wait-Process -Id 12345", decodedScript, StringComparison.Ordinal);
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
        var nuGet = new Mock<ICompanionNuGetProvisioner>();
        nuGet.Setup(n => n.UninstallAllAsync(It.IsAny<string?>(), It.IsAny<IProgress<InstallerProgressEvent>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var sut = CreateSut(platform.Object, runner.Object, processControl.Object, registry.Object, nuGet.Object);

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
    public void BuildWindowsDeferredSelfCleanupScript_EncodesTargetsSafelyAndWaitsForProcess()
    {
        var script = UninstallService.BuildWindowsDeferredSelfCleanupScript(
            4242,
            [
                @"C:\App\NightCodeForge\installer",
                @"C:\App\NightCodeForge\O'Brien.exe"
            ]);

        Assert.Contains("Wait-Process -Id 4242", script, StringComparison.Ordinal);
        Assert.Contains("FromBase64String", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\App\NightCodeForge\O'Brien.exe", script, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(@"C:\App\NightCodeForge\installer")), script, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(@"C:\App\NightCodeForge\O'Brien.exe")), script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UninstallAsync_DeferredSelfCleanupSpawnFails_ReturnsSucceededFalse()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var installDir = Path.Combine(platform.Object.LocalApplicationDataDirectory, "NightCodeForge");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, InstallerConstants.WebExeWindows), "web");
        var selfExePath = Path.Combine(installDir, InstallerConstants.InstallerExeWindows);
        File.WriteAllText(selfExePath, "self-exe");

        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.Is<ProcessRunRequest>(q => q.FileName == InstallerConstants.PowerShellExecutable), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(2, string.Empty, "powershell not found"));
        runner.Setup(r => r.RunAsync(It.Is<ProcessRunRequest>(q => q.FileName != InstallerConstants.PowerShellExecutable), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var processControl = new Mock<IProcessControlService>();
        processControl.SetupGet(c => c.CurrentProcessPath).Returns(selfExePath);

        var registry = new Mock<IWindowsRegistryAccessor>();
        var nuGet = new Mock<ICompanionNuGetProvisioner>();
        var sut = CreateSut(platform.Object, runner.Object, processControl.Object, registry.Object, nuGet.Object);
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
        var installerDir = Path.Combine(installDir, InstallerConstants.InstallerSubDirectoryName);
        Directory.CreateDirectory(installerDir);
        var selfExeInSubdir = Path.Combine(installerDir, InstallerConstants.InstallerExeWindows);
        File.WriteAllText(selfExeInSubdir, "self-exe");
        File.WriteAllText(Path.Combine(installerDir, "sibling.txt"), "sibling");
        File.WriteAllText(Path.Combine(installDir, InstallerConstants.WebExeWindows), "web");

        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var processControl = new Mock<IProcessControlService>();
        processControl.SetupGet(c => c.CurrentProcessPath).Returns(selfExeInSubdir);

        var registry = new Mock<IWindowsRegistryAccessor>();
        var nuGet = new Mock<ICompanionNuGetProvisioner>();
        var sut = CreateSut(platform.Object, runner.Object, processControl.Object, registry.Object, nuGet.Object);
        var result = await sut.UninstallAsync(new UninstallOptions(KeepData: false, RemoveCompanionTools: false, RemoveOllamaRouter: false, Quiet: true));

        Assert.True(result.Succeeded);
        Assert.True(result.DeferredSelfCleanupScheduled);
        Assert.False(File.Exists(Path.Combine(installDir, InstallerConstants.WebExeWindows)));
        Assert.False(File.Exists(Path.Combine(installerDir, "sibling.txt")));
        Assert.True(File.Exists(selfExeInSubdir));
    }

    [Fact]
    public async Task UninstallAsync_RemoveOllamaRouter_FallsBackToUninstallManifestDirectory()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        var installDir = _workspace.CreateSubdirectory("ncf-manifest");
        var customRouterDir = _workspace.CreateSubdirectory("custom-router-from-manifest");
        File.WriteAllText(Path.Combine(customRouterDir, InstallerConstants.OllamaRouterServerUnix), "router-file");

        var manifestJson = System.Text.Json.JsonSerializer.Serialize(
            new UninstallManifest("1.0.0", installDir, DateTimeOffset.UtcNow, customRouterDir, "Linux"),
            NightCodeForge.Installer.Core.Serialization.InstallerJsonContext.Default.UninstallManifest);
        File.WriteAllText(Path.Combine(installDir, InstallerConstants.UninstallManifestFileName), manifestJson);

        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));
        var processControl = new Mock<IProcessControlService>();
        var registry = new Mock<IWindowsRegistryAccessor>();
        var nuGet = new Mock<ICompanionNuGetProvisioner>();
        var sut = CreateSut(platform.Object, runner.Object, processControl.Object, registry.Object, nuGet.Object);

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

    [Fact]
    public async Task UninstallAsync_ProtectedHomeOrUnmarkedDirectory_FailsSafetyValidation()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        var unmarkedDir = _workspace.CreateSubdirectory("unmarked-folder");
        File.WriteAllText(Path.Combine(unmarkedDir, "important-notes.txt"), "keep me");

        var sut = CreateSut(
            platform.Object,
            Mock.Of<IProcessRunner>(),
            Mock.Of<IProcessControlService>(),
            Mock.Of<IWindowsRegistryAccessor>(),
            Mock.Of<ICompanionNuGetProvisioner>());

        var homeResult = await sut.UninstallAsync(
            new UninstallOptions(false, false, false, true),
            targetInstallDirectory: platform.Object.UserHomeDirectory);
        Assert.False(homeResult.Succeeded);
        Assert.True(Directory.Exists(platform.Object.UserHomeDirectory));

        var unmarkedResult = await sut.UninstallAsync(
            new UninstallOptions(false, false, false, true),
            targetInstallDirectory: unmarkedDir);
        Assert.False(unmarkedResult.Succeeded);
        Assert.True(File.Exists(Path.Combine(unmarkedDir, "important-notes.txt")));
    }

    private static UninstallService CreateSut(
        IPlatformEnvironment platform,
        IProcessRunner runner,
        IProcessControlService processControl,
        IWindowsRegistryAccessor registry,
        ICompanionNuGetProvisioner nuGet)
    {
        var detector = new InstallationDetector(platform, registry);
        var osIntegration = new OsIntegrationService(platform, registry, runner, TimeProvider.System);
        var envProvisioner = new Mock<IEnvironmentProvisioner>();
        envProvisioner.Setup(e => e.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(InstallerTestFactory.CreateHealthyWindowsProbe());

        return new UninstallService(
            platform,
            runner,
            processControl,
            detector,
            nuGet,
            osIntegration,
            envProvisioner.Object);
    }
}
