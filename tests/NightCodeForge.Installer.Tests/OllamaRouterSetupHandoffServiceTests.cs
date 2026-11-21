namespace NightCodeForge.Installer.Tests;

using System.ComponentModel;
using Moq;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

public sealed class OllamaRouterSetupHandoffServiceTests
{
    [Fact]
    public void ResolveOllamaRouterCliPath_PrefersEnvThenInstallDirsThenPath()
    {
        using var workspace = new TempTestWorkspace();
        var platformMock = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, workspace.RootPath);

        var defaultRouterDir = Path.Combine(platformMock.Object.LocalApplicationDataDirectory, InstallerConstants.ComponentOllamaRouter);
        var customHomeDir = workspace.CreateSubdirectory("custom-router-home");
        var defaultExe = Path.Combine(defaultRouterDir, InstallerConstants.OllamaRouterCliWindows);
        var customExe = Path.Combine(customHomeDir, InstallerConstants.OllamaRouterCliWindows);

        var detector = CreateDetectorMock(defaultRouterDir);
        var processRunner = new Mock<IProcessRunner>();
        var syncService = new Mock<IOllamaRouterRecommendationSyncService>();
        var service = new OllamaRouterSetupHandoffService(platformMock.Object, processRunner.Object, detector.Object, syncService.Object);

        // Missing everywhere
        Assert.False(service.IsOllamaRouterInstalled());
        Assert.Null(service.ResolveOllamaRouterCliPath());

        // Present on PATH
        platformMock.Setup(p => p.IsCommandOnPath(InstallerConstants.OllamaRouterCliUnix)).Returns(true);
        Assert.True(service.IsOllamaRouterInstalled());
        Assert.Equal(InstallerConstants.OllamaRouterCliUnix, service.ResolveOllamaRouterCliPath());

        // Present in default Windows install dir (%LOCALAPPDATA%\OllamaRouter\ollamarouter.exe)
        Directory.CreateDirectory(defaultRouterDir);
        File.WriteAllText(defaultExe, "stub");
        Assert.Equal(defaultExe, service.ResolveOllamaRouterCliPath());

        // $OLLAMAROUTER_HOME takes precedence over default install dir
        File.WriteAllText(customExe, "stub");
        platformMock.Setup(p => p.GetEnvironmentVariable(InstallerConstants.EnvOllamaRouterHome)).Returns(customHomeDir);
        Assert.Equal(customExe, service.ResolveOllamaRouterCliPath());
    }

    [Fact]
    public void ResolveOllamaRouterCliPath_OnLinux_ResolvesUnixBinary()
    {
        using var workspace = new TempTestWorkspace();
        var platformMock = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, workspace.RootPath);
        var defaultRouterDir = Path.Combine(platformMock.Object.UserHomeDirectory, InstallerConstants.UnixOllamaRouterDirName);
        Directory.CreateDirectory(defaultRouterDir);
        var unixCliPath = Path.Combine(defaultRouterDir, InstallerConstants.OllamaRouterCliUnix);
        File.WriteAllText(unixCliPath, "stub");

        var detector = CreateDetectorMock(defaultRouterDir);
        var service = new OllamaRouterSetupHandoffService(
            platformMock.Object,
            Mock.Of<IProcessRunner>(),
            detector.Object,
            Mock.Of<IOllamaRouterRecommendationSyncService>());

        Assert.True(service.IsOllamaRouterInstalled());
        Assert.Equal(unixCliPath, service.ResolveOllamaRouterCliPath());
    }

    [Fact]
    public async Task RunSetupWizardAndSyncAsync_WhenCliMissing_ReturnsFailureWithoutRunningProcessOrSync()
    {
        using var workspace = new TempTestWorkspace();
        var platformMock = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, workspace.RootPath);
        var defaultRouterDir = Path.Combine(platformMock.Object.LocalApplicationDataDirectory, InstallerConstants.ComponentOllamaRouter);

        var processRunner = new Mock<IProcessRunner>();
        var syncService = new Mock<IOllamaRouterRecommendationSyncService>();
        var service = new OllamaRouterSetupHandoffService(
            platformMock.Object,
            processRunner.Object,
            CreateDetectorMock(defaultRouterDir).Object,
            syncService.Object);

        var result = await service.RunSetupWizardAndSyncAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ExitCode);
        Assert.Null(result.SyncedRecommendations);
        Assert.Contains("ollamarouter", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        processRunner.Verify(p => p.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        syncService.Verify(s => s.SyncRecommendationsToConfigAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IProgress<InstallerProgressEvent>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunSetupWizardAndSyncAsync_WhenExitCodeZero_InvokesSetupWizardWaitAndSyncsRecommendations()
    {
        using var workspace = new TempTestWorkspace();
        var platformMock = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, workspace.RootPath);
        var routerDir = Path.Combine(platformMock.Object.LocalApplicationDataDirectory, InstallerConstants.ComponentOllamaRouter);
        Directory.CreateDirectory(routerDir);
        var cliPath = Path.Combine(routerDir, InstallerConstants.OllamaRouterCliWindows);
        File.WriteAllText(cliPath, "stub");

        ProcessRunRequest? capturedRequest = null;
        var processRunner = new Mock<IProcessRunner>();
        processRunner
            .Setup(p => p.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessRunRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var expectedSummary = new RouterRoleRecommendationsSummary(
            ProviderType: "OllamaRouter",
            Endpoint: "http://localhost:11434",
            ArchitectModelId: "qwen3.8:27b",
            CoderModelId: "qwen3.8:27b",
            DefaultModelId: "qwen3.8:27b",
            ContextWindowTokens: 262144,
            GeneratedAt: DateTimeOffset.UtcNow,
            ConfigFilePath: Path.Combine(workspace.RootPath, "config", "config.json"),
            Roles: []);

        var syncService = new Mock<IOllamaRouterRecommendationSyncService>();
        syncService
            .Setup(s => s.SyncRecommendationsToConfigAsync(
                workspace.RootPath,
                "http://localhost:11434",
                It.IsAny<IProgress<InstallerProgressEvent>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedSummary);

        var service = new OllamaRouterSetupHandoffService(
            platformMock.Object,
            processRunner.Object,
            CreateDetectorMock(routerDir).Object,
            syncService.Object);

        var result = await service.RunSetupWizardAndSyncAsync(workspace.RootPath, "http://localhost:11434");

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(expectedSummary, result.SyncedRecommendations);

        Assert.NotNull(capturedRequest);
        Assert.Equal(cliPath, capturedRequest.FileName);
        Assert.Equal(["setup", "--wizard", "--wait"], capturedRequest.Arguments);
        Assert.Equal(routerDir, capturedRequest.WorkingDirectory);
        Assert.Null(capturedRequest.Timeout);
    }

    [Theory]
    [InlineData(1, "Wizard cancelled by user")]
    [InlineData(2, "")]
    public async Task RunSetupWizardAndSyncAsync_WhenExitCodeNonZero_SkipsSyncAndReturnsClearRetryMessage(
        int exitCode,
        string stderr)
    {
        using var workspace = new TempTestWorkspace();
        var platformMock = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, workspace.RootPath);
        platformMock.Setup(p => p.IsCommandOnPath(InstallerConstants.OllamaRouterCliUnix)).Returns(true);

        var processRunner = new Mock<IProcessRunner>();
        processRunner
            .Setup(p => p.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(exitCode, string.Empty, stderr));

        var syncService = new Mock<IOllamaRouterRecommendationSyncService>();
        var detector = CreateDetectorMock(Path.Combine(workspace.RootPath, "missing-router"));
        var service = new OllamaRouterSetupHandoffService(
            platformMock.Object,
            processRunner.Object,
            detector.Object,
            syncService.Object);

        var result = await service.RunSetupWizardAndSyncAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Null(result.SyncedRecommendations);
        Assert.Contains("retry", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skip", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(stderr))
        {
            Assert.Contains(stderr, result.ErrorMessage);
        }

        syncService.Verify(
            s => s.SyncRecommendationsToConfigAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IProgress<InstallerProgressEvent>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunSetupWizardAndSyncAsync_WhenSyncThrowsAfterZeroExit_ReturnsFailureWithExitCodeZero()
    {
        using var workspace = new TempTestWorkspace();
        var platformMock = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, workspace.RootPath);
        platformMock.Setup(p => p.IsCommandOnPath(InstallerConstants.OllamaRouterCliUnix)).Returns(true);

        var processRunner = new Mock<IProcessRunner>();
        processRunner
            .Setup(p => p.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var syncService = new Mock<IOllamaRouterRecommendationSyncService>();
        syncService
            .Setup(s => s.SyncRecommendationsToConfigAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IProgress<InstallerProgressEvent>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterSyncException("Endpoint returned HTTP 500"));

        var detector = CreateDetectorMock(Path.Combine(workspace.RootPath, "missing-router"));
        var service = new OllamaRouterSetupHandoffService(
            platformMock.Object,
            processRunner.Object,
            detector.Object,
            syncService.Object);

        var result = await service.RunSetupWizardAndSyncAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.SyncedRecommendations);
        Assert.Contains("Endpoint returned HTTP 500", result.ErrorMessage);
    }

    [Fact]
    public void ResolveOllamaRouterCliPath_OnWindows_PrefersWindowsExeOverExtensionlessOnPath()
    {
        using var workspace = new TempTestWorkspace();
        var platformMock = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, workspace.RootPath);
        platformMock.Setup(p => p.IsCommandOnPath(InstallerConstants.OllamaRouterCliUnix)).Returns(true);
        platformMock.Setup(p => p.IsCommandOnPath(InstallerConstants.OllamaRouterCliWindows)).Returns(true);

        var missingRouterDir = Path.Combine(workspace.RootPath, "missing-router");
        var service = new OllamaRouterSetupHandoffService(
            platformMock.Object,
            Mock.Of<IProcessRunner>(),
            CreateDetectorMock(missingRouterDir).Object,
            Mock.Of<IOllamaRouterRecommendationSyncService>());

        Assert.Equal(InstallerConstants.OllamaRouterCliWindows, service.ResolveOllamaRouterCliPath());
    }

    [Fact]
    public async Task RunSetupWizardAndSyncAsync_WhenProcessLaunchThrows_ReturnsFailureWithRetryMessageWithoutSync()
    {
        using var workspace = new TempTestWorkspace();
        var platformMock = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, workspace.RootPath);
        platformMock.Setup(p => p.IsCommandOnPath(InstallerConstants.OllamaRouterCliWindows)).Returns(true);

        var processRunner = new Mock<IProcessRunner>();
        processRunner
            .Setup(p => p.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Win32Exception(5, "Access denied"));

        var syncService = new Mock<IOllamaRouterRecommendationSyncService>();
        var service = new OllamaRouterSetupHandoffService(
            platformMock.Object,
            processRunner.Object,
            CreateDetectorMock(Path.Combine(workspace.RootPath, "missing-router")).Object,
            syncService.Object);

        var result = await service.RunSetupWizardAndSyncAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(InstallerConstants.ExitCodeError, result.ExitCode);
        Assert.Null(result.SyncedRecommendations);
        Assert.Contains("Access denied", result.ErrorMessage);
        Assert.Contains("retry", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skip", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        syncService.Verify(
            s => s.SyncRecommendationsToConfigAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IProgress<InstallerProgressEvent>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunSetupWizardAndSyncAsync_WhenCancelled_PropagatesOperationCanceledException()
    {
        using var workspace = new TempTestWorkspace();
        var platformMock = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, workspace.RootPath);
        platformMock.Setup(p => p.IsCommandOnPath(InstallerConstants.OllamaRouterCliUnix)).Returns(true);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var processRunner = new Mock<IProcessRunner>();
        processRunner
            .Setup(p => p.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var service = new OllamaRouterSetupHandoffService(
            platformMock.Object,
            processRunner.Object,
            CreateDetectorMock(Path.Combine(workspace.RootPath, "missing-router")).Object,
            Mock.Of<IOllamaRouterRecommendationSyncService>());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunSetupWizardAndSyncAsync(cts.Token));
    }

    [Fact]
    public async Task RunSetupWizardAndSyncAsync_WhenExitCodeNonZeroWithEmptyStderr_FallsBackToStdoutAndAvoidsDoublePeriod()
    {
        using var workspace = new TempTestWorkspace();
        var platformMock = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, workspace.RootPath);
        platformMock.Setup(p => p.IsCommandOnPath(InstallerConstants.OllamaRouterCliUnix)).Returns(true);

        var processRunner = new Mock<IProcessRunner>();
        processRunner
            .Setup(p => p.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(2, "Cancelled in UI.", string.Empty));

        var service = new OllamaRouterSetupHandoffService(
            platformMock.Object,
            processRunner.Object,
            CreateDetectorMock(Path.Combine(workspace.RootPath, "missing-router")).Object,
            Mock.Of<IOllamaRouterRecommendationSyncService>());

        var result = await service.RunSetupWizardAndSyncAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Cancelled in UI.", result.ErrorMessage);
        Assert.DoesNotContain("..", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSetupWizardAndSyncAsync_RemapsSyncProgressPercentagesMonotonically()
    {
        using var workspace = new TempTestWorkspace();
        var platformMock = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, workspace.RootPath);
        platformMock.Setup(p => p.IsCommandOnPath(InstallerConstants.OllamaRouterCliUnix)).Returns(true);

        var processRunner = new Mock<IProcessRunner>();
        processRunner
            .Setup(p => p.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var expectedSummary = new RouterRoleRecommendationsSummary(
            ProviderType: "OllamaRouter",
            Endpoint: "http://localhost:11434",
            ArchitectModelId: "qwen3.8:27b",
            CoderModelId: "qwen3.8:27b",
            DefaultModelId: "qwen3.8:27b",
            ContextWindowTokens: 262144,
            GeneratedAt: DateTimeOffset.UtcNow,
            ConfigFilePath: Path.Combine(workspace.RootPath, "config", "config.json"),
            Roles: []);

        var syncService = new Mock<IOllamaRouterRecommendationSyncService>();
        syncService
            .Setup(s => s.SyncRecommendationsToConfigAsync(
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<IProgress<InstallerProgressEvent>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string?, string?, IProgress<InstallerProgressEvent>?, CancellationToken>((_, _, prog, _) =>
            {
                prog?.Report(new InstallerProgressEvent("OllamaRouter Sync", "Fetching", InstallerLogLevel.Info, 20));
                prog?.Report(new InstallerProgressEvent("OllamaRouter Sync", "Updating", InstallerLogLevel.Info, 70));
                prog?.Report(new InstallerProgressEvent("OllamaRouter Sync", "Synced", InstallerLogLevel.Info, 100));
            })
            .ReturnsAsync(expectedSummary);

        var recordedPercents = new List<double>();
        var progress = new SynchronousTestProgress<InstallerProgressEvent>(ev =>
        {
            if (ev.ProgressPercent.HasValue)
            {
                recordedPercents.Add(ev.ProgressPercent.Value);
            }
        });

        var service = new OllamaRouterSetupHandoffService(
            platformMock.Object,
            processRunner.Object,
            CreateDetectorMock(Path.Combine(workspace.RootPath, "missing-router")).Object,
            syncService.Object);

        var result = await service.RunSetupWizardAndSyncAsync(null, null, progress);

        Assert.True(result.Succeeded);
        Assert.Equal([10d, 60d, 68d, 88d, 100d], recordedPercents);
    }

    private sealed class SynchronousTestProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static Mock<IInstallationDetector> CreateDetectorMock(string ollamaRouterDir)
    {
        var mock = new Mock<IInstallationDetector>();
        mock.Setup(d => d.GetDefaultOllamaRouterInstallDirectory()).Returns(ollamaRouterDir);
        mock.Setup(d => d.Detect(null)).Returns(new InstallationDiscoveryResult(
            NightCodeForge: new InstalledComponentInfo(false, Path.GetDirectoryName(ollamaRouterDir)!, null, false, false, false, false, null),
            OllamaRouter: new InstalledComponentInfo(false, ollamaRouterDir, null, false, false, false, false, null),
            RecommendedSourceMode: InstallSourceMode.Online,
            DetectedBundleDirectory: null,
            DetectedBundleFiles: []));
        return mock;
    }
}
