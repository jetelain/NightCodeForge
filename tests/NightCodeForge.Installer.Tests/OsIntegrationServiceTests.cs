namespace NightCodeForge.Installer.Tests;

using Moq;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

public sealed class OsIntegrationServiceTests : IDisposable
{
    private readonly TempTestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void CreateWindowsUninstallRegistryPlan_PopulatesExactHkcuSchema()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var registry = new Mock<IWindowsRegistryAccessor>();
        var runner = new Mock<IProcessRunner>();
        var sut = new OsIntegrationService(platform.Object, registry.Object, runner.Object, TimeProvider.System);

        var installDir = @"C:\Users\Dev\AppData\Local\NightCodeForge";
        var plan = sut.CreateWindowsUninstallRegistryPlan(
            installDir,
            "1.2.3",
            estimatedSizeKb: 42000,
            new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(InstallerConstants.WindowsUninstallRegistrySubKey, plan.RegistryKeyPath);
        Assert.Equal("NightCodeForge", plan.DisplayName);
        Assert.Equal("1.2.3", plan.DisplayVersion);
        Assert.Equal("Pmad", plan.Publisher);
        Assert.Equal(installDir, plan.InstallLocation);
        var expectedInstallerExe = $@"{installDir}\{InstallerConstants.InstallerSubDirectoryName}\{InstallerConstants.InstallerExeWindows}";
        Assert.Equal($"{expectedInstallerExe},0", plan.DisplayIcon);
        Assert.Equal($"\"{expectedInstallerExe}\" uninstall", plan.UninstallString);
        Assert.Equal($"\"{expectedInstallerExe}\" uninstall --quiet", plan.QuietUninstallString);
        Assert.Equal($"\"{expectedInstallerExe}\"", plan.ModifyPath);
        Assert.Equal(0, plan.NoModify);
        Assert.Equal(1, plan.NoRepair);
        Assert.Equal(42000, plan.EstimatedSizeKb);
        Assert.Equal("20261121", plan.InstallDate);
    }

    [Fact]
    public void CreateProtocolAndDesktopPlans_MatchCrossPlatformSpecs()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        var registry = new Mock<IWindowsRegistryAccessor>();
        var runner = new Mock<IProcessRunner>();
        var sut = new OsIntegrationService(platform.Object, registry.Object, runner.Object, TimeProvider.System);

        var winProto = sut.CreateWindowsOllamaRouterProtocolPlan(@"C:\OR\ollamarouter-app.exe");
        Assert.Equal(@"Software\Classes\ollamarouter", winProto.SchemeKeyPath);
        Assert.Equal("URL:OllamaRouter Protocol", winProto.DisplayName);
        Assert.Equal(string.Empty, winProto.UrlProtocolValue);
        Assert.Equal("\"C:\\OR\\ollamarouter-app.exe\",0", winProto.IconValue);
        Assert.Equal("\"C:\\OR\\ollamarouter-app.exe\" \"%1\"", winProto.CommandValue);

        var linuxProto = sut.CreateLinuxOllamaRouterProtocolPlan("/home/dev/.ollamarouter/ollamarouter-app", "/home/dev", xdgMimeAvailable: true);
        Assert.Equal(Path.Combine("/home/dev", ".local", "share", "applications", "ollamarouter.desktop"), linuxProto.DesktopEntryPath);
        Assert.Contains("MimeType=x-scheme-handler/ollamarouter;", linuxProto.DesktopEntryContent, StringComparison.Ordinal);
        Assert.Equal("xdg-mime default ollamarouter.desktop x-scheme-handler/ollamarouter", linuxProto.XdgMimeCommand);

        var linuxDesktop = sut.CreateLinuxDesktopEntryPlans("/home/dev/.nightcodeforge", "/home/dev");
        Assert.Contains("ncf-web", linuxDesktop.WebAppDesktopContent, StringComparison.Ordinal);
        Assert.Contains("nightcodeforge-installer", linuxDesktop.InstallerDesktopContent, StringComparison.Ordinal);

        var macPlan = sut.CreateMacOsAppWrapperPlan("/Users/dev/Applications/NightCodeForge", "/Users/dev");
        Assert.Equal(Path.Combine("/Users/dev", "Applications", "NightCodeForge.app"), macPlan.AppBundleDirectory);
        Assert.Contains("CFBundleExecutable", macPlan.InfoPlistContent, StringComparison.Ordinal);
        Assert.Contains("nightcodeforge-installer", macPlan.LauncherScriptContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterAndUnregister_Windows_WritesRegistryAndInvokesShortcutScript()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var installDir = _workspace.CreateSubdirectory("ncf-win");
        var installerSubDir = Directory.CreateDirectory(Path.Combine(installDir, InstallerConstants.InstallerSubDirectoryName)).FullName;
        File.WriteAllText(Path.Combine(installerSubDir, InstallerConstants.InstallerExeWindows), "exe");

        var registry = new Mock<IWindowsRegistryAccessor>();
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var sut = new OsIntegrationService(platform.Object, registry.Object, runner.Object, TimeProvider.System);
        await sut.RegisterNightCodeForgeInstallationAsync(installDir, "1.0.0", createDesktopShortcut: true);

        registry.Verify(
            r => r.WriteCurrentUserValues(
                InstallerConstants.WindowsUninstallRegistrySubKey,
                It.Is<IReadOnlyDictionary<string, object>>(d =>
                    (string)d["DisplayName"] == "NightCodeForge" &&
                    (string)d["DisplayVersion"] == "1.0.0" &&
                    (int)d["NoModify"] == 0 &&
                    (int)d["NoRepair"] == 1)),
            Times.Once);

        runner.Verify(
            r => r.RunAsync(
                It.Is<ProcessRunRequest>(q => q.FileName == "powershell.exe" && q.Arguments.Contains("-EncodedCommand")),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        await sut.UnregisterNightCodeForgeInstallationAsync(installDir);
        registry.Verify(r => r.DeleteCurrentUserSubKeyTree(InstallerConstants.WindowsUninstallRegistrySubKey), Times.Once);
    }

    [Fact]
    public void UserPathManagement_IsIdempotentAcrossWindowsAndUnix()
    {
        var winPlatform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var winRegistry = new Mock<IWindowsRegistryAccessor>();
        var runner = new Mock<IProcessRunner>();
        var winSut = new OsIntegrationService(winPlatform.Object, winRegistry.Object, runner.Object, TimeProvider.System);

        var binDir = Path.Combine(_workspace.RootPath, "ncf", "bin");
        winSut.AddDirectoryToUserPath(binDir, "NightCodeForge");
        winSut.AddDirectoryToUserPath(binDir + Path.DirectorySeparatorChar, "NightCodeForge");
        Assert.Equal(Path.GetFullPath(binDir), winPlatform.Object.GetUserPathVariable());

        winSut.RemoveDirectoryFromUserPath(binDir, "NightCodeForge");
        Assert.Equal(string.Empty, winPlatform.Object.GetUserPathVariable());

        var linuxPlatform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        var linuxSut = new OsIntegrationService(linuxPlatform.Object, winRegistry.Object, runner.Object, TimeProvider.System);
        var unixProfile = Path.Combine(linuxPlatform.Object.UserHomeDirectory, ".profile");
        File.WriteAllText(unixProfile, "export FOO=bar\n");

        linuxSut.AddDirectoryToUserPath(binDir, "NightCodeForge");
        linuxSut.AddDirectoryToUserPath(binDir, "NightCodeForge");
        var profileAfterAdd = File.ReadAllText(unixProfile);
        Assert.Equal(1, profileAfterAdd.Split("NightCodeForge Installer PATH (NightCodeForge)", StringSplitOptions.None).Length / 2);

        linuxSut.RemoveDirectoryFromUserPath(binDir, "NightCodeForge");
        var profileAfterRemove = File.ReadAllText(unixProfile);
        Assert.DoesNotContain("NightCodeForge Installer PATH", profileAfterRemove, StringComparison.Ordinal);
        Assert.Contains("export FOO=bar", profileAfterRemove, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateWindowsUninstallRegistryPlan_NormalizesBackslashesDeterministically()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        var registry = new Mock<IWindowsRegistryAccessor>();
        var runner = new Mock<IProcessRunner>();
        var sut = new OsIntegrationService(platform.Object, registry.Object, runner.Object, TimeProvider.System);

        var plan = sut.CreateWindowsUninstallRegistryPlan(
            "C:/Users/Dev/AppData/Local/NightCodeForge/",
            "1.0.0",
            estimatedSizeKb: 100,
            new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero));

        var expectedInstallerExe = @"C:\Users\Dev\AppData\Local\NightCodeForge\installer\NightCodeForge.Installer.exe";
        Assert.Equal(@"C:\Users\Dev\AppData\Local\NightCodeForge", plan.InstallLocation);
        Assert.Equal($"{expectedInstallerExe},0", plan.DisplayIcon);
        Assert.Equal($"\"{expectedInstallerExe}\" uninstall", plan.UninstallString);
    }

    [Fact]
    public void CreateLinuxAndMacOsPlans_EmitPurePosixSlashesInScriptAndDesktopContents()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var registry = new Mock<IWindowsRegistryAccessor>();
        var runner = new Mock<IProcessRunner>();
        var sut = new OsIntegrationService(platform.Object, registry.Object, runner.Object, TimeProvider.System);

        var linuxProto = sut.CreateLinuxOllamaRouterProtocolPlan(@"\home\dev\.ollamarouter\ollamarouter-app", "/home/dev", xdgMimeAvailable: false);
        Assert.Contains("Exec=\"/home/dev/.ollamarouter/ollamarouter-app\" %u", linuxProto.DesktopEntryContent, StringComparison.Ordinal);
        Assert.Null(linuxProto.XdgMimeCommand);

        var linuxDesktop = sut.CreateLinuxDesktopEntryPlans(@"/home/dev/.nightcodeforge\", "/home/dev");
        Assert.Contains("Exec=\"/home/dev/.nightcodeforge/bin/ncf-web\"", linuxDesktop.WebAppDesktopContent, StringComparison.Ordinal);
        Assert.Contains("Path=/home/dev/.nightcodeforge", linuxDesktop.WebAppDesktopContent, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', linuxDesktop.WebAppDesktopContent);

        var macPlan = sut.CreateMacOsAppWrapperPlan(@"\Users\dev\Applications\NightCodeForge\", "/Users/dev");
        Assert.Contains("exec \"/Users/dev/Applications/NightCodeForge/installer/nightcodeforge-installer\" \"$@\"", macPlan.LauncherScriptContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterNightCodeForgeInstallationAsync_WindowsShortcutFailure_ThrowsInstallerException()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var installDir = _workspace.CreateSubdirectory("ncf-shortcut-fail");
        var registry = new Mock<IWindowsRegistryAccessor>();
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(1, string.Empty, "COM error creating shortcut"));

        var sut = new OsIntegrationService(platform.Object, registry.Object, runner.Object, TimeProvider.System);

        await Assert.ThrowsAsync<NightCodeForge.Installer.Core.Exceptions.InstallerException>(() =>
            sut.RegisterNightCodeForgeInstallationAsync(installDir, "1.0.0", createDesktopShortcut: false));
    }

    [Fact]
    public void ApplyManagedUnixPathBlock_PreservesTrailingProfileWhenStartMarkerIsUnclosed()
    {
        const string corruptedProfile = """
            export BEFORE=1
            # >>> NightCodeForge Installer PATH (NightCodeForge) >>>
            export PATH="$PATH:/old/path"
            export IMPORTANT_AFTER=2
            """;

        var updated = OsIntegrationService.ApplyManagedUnixPathBlock(
            corruptedProfile,
            "/new/bin",
            InstallerConstants.PathBlockLabelNightCodeForge,
            removeOnly: false);

        Assert.Contains("export BEFORE=1", updated, StringComparison.Ordinal);
        Assert.Contains("export IMPORTANT_AFTER=2", updated, StringComparison.Ordinal);
        Assert.Contains("export PATH=\"$PATH:/new/bin\"", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAndRemoveDirectoryToUserPath_Windows_HandlesQuotedEntriesAndDistinctComponentLabels()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var customPath = Path.Combine(_workspace.RootPath, "my-bin");
        var userPath = $"\"{Path.GetFullPath(customPath)}\";C:\\Other\\Bin";
        platform.Setup(p => p.GetUserPathVariable()).Returns(() => userPath);
        platform.Setup(p => p.SetUserPathVariable(It.IsAny<string>())).Callback((string v) => userPath = v);

        var registry = new Mock<IWindowsRegistryAccessor>();
        var runner = new Mock<IProcessRunner>();
        var sut = new OsIntegrationService(platform.Object, registry.Object, runner.Object, TimeProvider.System);

        sut.AddDirectoryToUserPath(customPath, InstallerConstants.PathBlockLabelNightCodeForge);
        Assert.Equal($"\"{Path.GetFullPath(customPath)}\";C:\\Other\\Bin", userPath);

        sut.RemoveDirectoryFromUserPath(customPath, InstallerConstants.PathBlockLabelNightCodeForge);
        Assert.Equal(@"C:\Other\Bin", userPath);
    }

    [Fact]
    public async Task ApplyShortcutPreferencesAsync_CreatesAndRemovesWindowsShortcuts_AndRejectsNewlines()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var installDir = _workspace.CreateSubdirectory("ncf-prefs");
        var registry = new Mock<IWindowsRegistryAccessor>();
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var sut = new OsIntegrationService(platform.Object, registry.Object, runner.Object, TimeProvider.System);

        await sut.ApplyShortcutPreferencesAsync(installDir, createStartMenuShortcut: true, createDesktopShortcut: false);
        runner.Verify(r => r.RunAsync(
            It.Is<ProcessRunRequest>(q => q.FileName == InstallerConstants.PowerShellExecutable && q.Arguments.Contains("-EncodedCommand")),
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Throws<ArgumentException>(() => sut.CreateLinuxDesktopEntryPlans("/home/dev/.ncf\nEvil=true", "/home/dev"));
    }
}
