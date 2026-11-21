namespace NightCodeForge.Installer.Tests;

using System.Net;
using System.Text;
using Moq;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

public sealed class ForgeDoctorServiceTests
{
    [Fact]
    public async Task RunDiagnosticsAsync_AllHealthyWithValidSqliteDatabase_ReturnsAllSixPassing()
    {
        using var workspace = new TempTestWorkspace();
        var installDir = workspace.CreateSubdirectory("ncf");
        var dataDir = Path.Combine(installDir, InstallerConstants.DataDirectoryName);
        Directory.CreateDirectory(dataDir);

        var dbPath = Path.Combine(dataDir, InstallerConstants.MainDatabaseFileName);
        var headerBytes = Encoding.ASCII.GetBytes(InstallerConstants.SqliteFileHeaderMagic);
        var fullDbBytes = new byte[4096];
        headerBytes.CopyTo(fullDbBytes, 0);
        await File.WriteAllBytesAsync(dbPath, fullDbBytes);

        await File.WriteAllTextAsync(
            Path.Combine(installDir, InstallerConstants.AppSettingsProductionFileName),
            """{"Urls": "http://localhost:5270"}""");

        using var httpClient = new HttpClient(new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("/api/router/recommendations", StringComparison.Ordinal))
            {
                return Task.FromResult(TestArchiveHelper.JsonResponse("{}"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, "win-x64", workspace.RootPath);
        var envProvisioner = new Mock<IEnvironmentProvisioner>();
        envProvisioner.Setup(e => e.ProbeAsync(InstallerConstants.DefaultWslDistro, It.IsAny<CancellationToken>()))
            .ReturnsAsync(InstallerTestFactory.CreateHealthyWindowsProbe());

        var toolStatuses = CompanionToolCatalog.All
            .Select(spec => new CompanionToolStatus(spec, true, "1.0.0", spec.InstallInWsl, spec.InstallInWsl ? "1.0.0" : null))
            .ToList();
        var nuGetProvisioner = new Mock<ICompanionNuGetProvisioner>();
        nuGetProvisioner.Setup(n => n.ProbeToolsAsync(InstallerConstants.DefaultWslDistro, It.IsAny<CancellationToken>()))
            .ReturnsAsync(toolStatuses);

        var processControl = new Mock<IProcessControlService>();
        processControl.Setup(c => c.IsAnyProcessRunning(InstallerConstants.NightCodeForgeWebProcesses)).Returns(true);

        var sut = new ForgeDoctorService(
            httpClient,
            platform.Object,
            envProvisioner.Object,
            nuGetProvisioner.Object,
            Mock.Of<IInstallationDetector>(),
            processControl.Object);

        var report = await sut.RunDiagnosticsAsync(
            installDir,
            InstallerConstants.DefaultWslDistro,
            InstallerConstants.DefaultOllamaRouterEndpoint,
            CancellationToken.None);

        Assert.Equal(6, report.Checks.Count);
        Assert.Equal(6, report.PassedCount);
        Assert.Equal(0, report.WarningCount);
        Assert.Equal(0, report.FailedCount);
    }

    [Fact]
    public async Task RunDiagnosticsAsync_MixedFailureAndWarnings_AggregatesCountsAccurately()
    {
        using var workspace = new TempTestWorkspace();
        var installDir = workspace.CreateSubdirectory("ncf-mixed");
        var dataDir = Path.Combine(installDir, InstallerConstants.DataDirectoryName);
        Directory.CreateDirectory(dataDir); // data/ exists, no DB file yet -> Warning

        using var httpClient = new HttpClient(new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("/api/router/recommendations", StringComparison.Ordinal))
            {
                // 404 from OllamaRouter -> Warning ("run Setup Wizard")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            throw new HttpRequestException("Connection refused");
        }));

        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, "linux-x64", workspace.RootPath);
        var envProvisioner = new Mock<IEnvironmentProvisioner>();
        envProvisioner.Setup(e => e.ProbeAsync(InstallerConstants.DefaultWslDistro, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnvironmentProbeResult(
                HostPlatformType.Linux,
                "linux-x64",
                WslInstalled: false,
                WslDistroInstalled: false,
                ResolvedWslDistroName: null,
                HostDotNet10Installed: true,
                HostGitInstalled: true,
                HostDockerAvailable: false, // Fail
                HostRipgrepInstalled: true,
                WslDotNet10Installed: false,
                WslDockerInstalled: false,
                WslRipgrepInstalled: false,
                DiagnosticNotes: []));

        var toolStatuses = CompanionToolCatalog.All
            .Select(spec => new CompanionToolStatus(spec, spec.ToolCommand == "ncf", "1.0.0", false, null))
            .ToList(); // 4 missing -> Fail
        var nuGetProvisioner = new Mock<ICompanionNuGetProvisioner>();
        nuGetProvisioner.Setup(n => n.ProbeToolsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(toolStatuses);

        var sut = new ForgeDoctorService(
            httpClient,
            platform.Object,
            envProvisioner.Object,
            nuGetProvisioner.Object,
            Mock.Of<IInstallationDetector>(),
            Mock.Of<IProcessControlService>());

        var report = await sut.RunDiagnosticsAsync(
            installDir,
            InstallerConstants.DefaultWslDistro,
            InstallerConstants.DefaultOllamaRouterEndpoint,
            CancellationToken.None);

        // Linux WSL check passes (1 Pass); OllamaRouter 404 + fresh DB (2 Warnings); Docker, Web Port, Tools (3 Fails)
        Assert.Equal(1, report.PassedCount);
        Assert.Equal(2, report.WarningCount);
        Assert.Equal(3, report.FailedCount);
    }

    [Fact]
    public async Task RunDiagnosticsAsync_CorruptSqliteHeader_FailsDatabaseCheck()
    {
        using var workspace = new TempTestWorkspace();
        var installDir = workspace.CreateSubdirectory("ncf-corrupt");
        var dataDir = Path.Combine(installDir, InstallerConstants.DataDirectoryName);
        Directory.CreateDirectory(dataDir);
        await File.WriteAllTextAsync(Path.Combine(dataDir, InstallerConstants.MainDatabaseFileName), "not a sqlite file");

        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));

        var envProvisioner = new Mock<IEnvironmentProvisioner>();
        envProvisioner.Setup(e => e.ProbeAsync(InstallerConstants.DefaultWslDistro, It.IsAny<CancellationToken>()))
            .ReturnsAsync(InstallerTestFactory.CreateHealthyWindowsProbe());

        var nuGetProvisioner = new Mock<ICompanionNuGetProvisioner>();
        nuGetProvisioner.Setup(n => n.ProbeToolsAsync(InstallerConstants.DefaultWslDistro, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CompanionToolCatalog.All
                .Select(s => new CompanionToolStatus(s, true, "1.0.0", s.InstallInWsl, s.InstallInWsl ? "1.0.0" : null))
                .ToList());

        var sut = new ForgeDoctorService(
            httpClient,
            TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, "win-x64", workspace.RootPath).Object,
            envProvisioner.Object,
            nuGetProvisioner.Object,
            Mock.Of<IInstallationDetector>(),
            Mock.Of<IProcessControlService>());

        var report = await sut.RunDiagnosticsAsync(
            installDir,
            InstallerConstants.DefaultWslDistro,
            InstallerConstants.DefaultOllamaRouterEndpoint,
            CancellationToken.None);

        var dbCheck = Assert.Single(report.Checks, c => c.Name == InstallerConstants.DoctorCheckDatabase);
        Assert.Equal(DoctorCheckStatus.Fail, dbCheck.Status);
        Assert.Contains("invalid", dbCheck.Summary, StringComparison.OrdinalIgnoreCase);
    }
}
