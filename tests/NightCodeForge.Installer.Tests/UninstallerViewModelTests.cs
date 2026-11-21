namespace NightCodeForge.Installer.Tests;

using Moq;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;
using NightCodeForge.Installer.ViewModels;

public sealed class UninstallerViewModelTests
{
    private static InstallationDiscoveryResult CreateInstalledDiscovery() =>
        new(
            new InstalledComponentInfo(true, @"C:\Apps\NightCodeForge", "1.0.0", false, true, true, true, @"C:\Apps\NightCodeForge\NightCodeForge.Web.exe"),
            new InstalledComponentInfo(true, @"C:\Apps\OllamaRouter", "1.0.0", false, true, true, true, @"C:\Apps\OllamaRouter\ollamarouter-server.exe"),
            InstallSourceMode.Online,
            null,
            []);

    [Fact]
    public async Task Uninstaller_StandaloneLaunch_DefaultsKeepDataTrueAndStreamsProgress()
    {
        var discovery = CreateInstalledDiscovery();
        var uninstallService = new Mock<IUninstallService>();
        uninstallService
            .Setup(u => u.UninstallAsync(
                It.IsAny<UninstallOptions>(),
                @"C:\Apps\NightCodeForge",
                InstallerConstants.DefaultWslDistro,
                It.IsAny<IProgress<InstallerProgressEvent>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<UninstallOptions, string?, string?, IProgress<InstallerProgressEvent>?, CancellationToken>((_, _, _, p, _) =>
            {
                p?.Report(new InstallerProgressEvent("Stop Processes", "Stopped NightCodeForge.Web", InstallerLogLevel.Info, 50));
            })
            .ReturnsAsync(new UninstallResult(
                Succeeded: true,
                DeferredSelfCleanupScheduled: true,
                Steps: [new UninstallStepResult("Stop Processes", ProvisioningStatus.Completed, "Stopped")],
                ErrorMessage: null));

        var uiHost = new Mock<IInstallerUiHost>();
        using var container = InstallerTestFactory.CreateContainer(
            installationDetector: Mock.Of<IInstallationDetector>(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>()) == discovery),
            uninstallService: uninstallService.Object);

        var plan = InstallerTestFactory.CreateStartupPlan(discovery, ["uninstall"]);
        var mainVm = new MainWindowViewModel(container, plan, uiHost.Object, new ImmediateUiDispatcher());
        var vm = mainVm.Uninstaller;

        Assert.Equal(InstallerUiMode.InteractiveUninstaller, mainVm.CurrentMode);
        Assert.True(vm.KeepData);
        Assert.False(vm.RemoveCompanionTools);
        Assert.False(vm.RemoveOllamaRouter);

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.True(vm.IsCompleted);
        Assert.True(vm.Succeeded);
        Assert.True(vm.DeferredSelfCleanupScheduled);
        Assert.Contains("cleaning up after this window closes", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Single(vm.Steps);
        Assert.Single(vm.LogLines);

        uninstallService.Verify(u => u.UninstallAsync(
            new UninstallOptions(KeepData: true, RemoveCompanionTools: false, RemoveOllamaRouter: false, Quiet: false),
            @"C:\Apps\NightCodeForge",
            InstallerConstants.DefaultWslDistro,
            It.IsAny<IProgress<InstallerProgressEvent>?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        vm.CloseCommand.Execute(null);
        uiHost.Verify(h => h.RequestExit(InstallerConstants.ExitCodeSuccess), Times.Once);
    }

    [Fact]
    public async Task Uninstaller_CancelFromStandaloneExits_AndFailureCloseExitsWithOne()
    {
        var discovery = CreateInstalledDiscovery();
        var uninstallService = new Mock<IUninstallService>();
        uninstallService
            .Setup(u => u.UninstallAsync(
                It.IsAny<UninstallOptions>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<IProgress<InstallerProgressEvent>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UninstallResult(false, false, [], "Access denied"));

        var uiHost = new Mock<IInstallerUiHost>();
        var plan = InstallerTestFactory.CreateStartupPlan(discovery, ["uninstall", "--remove-tools", "--remove-ollamarouter"]);

        var vm = new UninstallerViewModel(
            uninstallService.Object,
            Mock.Of<IInstallationDetector>(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>()) == discovery),
            plan,
            uiHost.Object,
            new ImmediateUiDispatcher(),
            () => { },
            canReturnToHub: false,
            CancellationToken.None);

        Assert.True(vm.RemoveCompanionTools);
        Assert.True(vm.RemoveOllamaRouter);

        vm.CancelCommand.Execute(null);
        uiHost.Verify(h => h.RequestExit(InstallerConstants.ExitCodeSuccess), Times.Once);

        await vm.UninstallCommand.ExecuteAsync(null);
        Assert.False(vm.Succeeded);
        Assert.False(vm.IsCompleted);
        Assert.True(vm.HasError);
        Assert.Equal("Access denied", vm.ErrorMessage);
        Assert.True(vm.CanUninstall);
        Assert.True(vm.CanCancel);
        Assert.True(vm.CanClose);

        vm.CloseCommand.Execute(null);
        uiHost.Verify(h => h.RequestExit(InstallerConstants.ExitCodeError), Times.Once);
    }
}
