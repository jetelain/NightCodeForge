namespace NightCodeForge.Installer.Tests;

using Moq;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

public sealed class InstallationDetectorTests : IDisposable
{
    private readonly TempTestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Detect_Windows_IdentifiesLegacyPublishInstallAndReadsVersionFile()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var registry = new Mock<IWindowsRegistryAccessor>();

        var legacyDir = Path.Combine(platform.Object.LocalApplicationDataDirectory, "NightCodeForge");
        Directory.CreateDirectory(legacyDir);
        Directory.CreateDirectory(Path.Combine(legacyDir, "config"));
        Directory.CreateDirectory(Path.Combine(legacyDir, "data"));
        Directory.CreateDirectory(Path.Combine(legacyDir, "logs"));
        File.WriteAllText(Path.Combine(legacyDir, InstallerConstants.WebExeWindows), "binary");
        File.WriteAllText(Path.Combine(legacyDir, InstallerConstants.VersionFileName), "1.0.0\n");

        var sut = new InstallationDetector(platform.Object, registry.Object);
        var result = sut.Detect();

        Assert.True(result.NightCodeForge.IsInstalled);
        Assert.True(result.NightCodeForge.IsLegacyPublishInstall);
        Assert.Equal("1.0.0", result.NightCodeForge.Version);
        Assert.Equal(Path.GetFullPath(legacyDir), result.NightCodeForge.InstallDirectory);
        Assert.True(result.NightCodeForge.HasConfigDirectory);
        Assert.True(result.NightCodeForge.HasDataOrStateDirectory);
        Assert.True(result.NightCodeForge.HasLogsDirectory);
        Assert.Equal(InstallSourceMode.Online, result.RecommendedSourceMode);
    }

    [Fact]
    public void Detect_Windows_HonorsNightCodeForgeHomeAndDetectsManagedInstallAndAirGappedBundle()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var registry = new Mock<IWindowsRegistryAccessor>();

        var customNcfDir = _workspace.CreateSubdirectory("custom-ncf");
        File.WriteAllText(Path.Combine(customNcfDir, InstallerConstants.WebExeWindows), "binary");
        File.WriteAllText(Path.Combine(customNcfDir, InstallerConstants.UninstallManifestFileName), "{}");
        File.WriteAllText(Path.Combine(customNcfDir, InstallerConstants.VersionFileName), "2.1.0");

        var routerDir = Path.Combine(platform.Object.LocalApplicationDataDirectory, "OllamaRouter");
        Directory.CreateDirectory(routerDir);
        File.WriteAllText(Path.Combine(routerDir, InstallerConstants.OllamaRouterServerWindows), "router");
        File.WriteAllText(Path.Combine(routerDir, InstallerConstants.VersionFileName), "0.9.5");

        var bundleDir = Path.Combine(platform.Object.InstallerBaseDirectory, InstallerConstants.BundleDirectoryName);
        Directory.CreateDirectory(bundleDir);
        File.WriteAllText(Path.Combine(bundleDir, "NightCodeForge-Web-win-x64.zip"), "zip");
        File.WriteAllText(Path.Combine(bundleDir, "NightCodeForge.Cli.1.0.0.nupkg"), "nupkg");

        platform.Setup(p => p.GetEnvironmentVariable(InstallerConstants.EnvNightCodeForgeHome)).Returns(customNcfDir);

        var sut = new InstallationDetector(platform.Object, registry.Object);
        var result = sut.Detect();

        Assert.True(result.NightCodeForge.IsInstalled);
        Assert.False(result.NightCodeForge.IsLegacyPublishInstall);
        Assert.Equal(Path.GetFullPath(customNcfDir), result.NightCodeForge.InstallDirectory);
        Assert.Equal("2.1.0", result.NightCodeForge.Version);

        Assert.True(result.OllamaRouter.IsInstalled);
        Assert.Equal("0.9.5", result.OllamaRouter.Version);

        Assert.Equal(InstallSourceMode.AirGappedBundle, result.RecommendedSourceMode);
        Assert.Equal(Path.GetFullPath(bundleDir), result.DetectedBundleDirectory);
        Assert.Equal(2, result.DetectedBundleFiles.Count);
    }

    [Fact]
    public void GetDefaultInstallDirectories_FollowPlatformConventions()
    {
        var registry = new Mock<IWindowsRegistryAccessor>();

        var linuxPlatform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        var linuxDetector = new InstallationDetector(linuxPlatform.Object, registry.Object);
        Assert.Equal(Path.Combine(linuxPlatform.Object.UserHomeDirectory, ".nightcodeforge"), linuxDetector.GetDefaultNightCodeForgeInstallDirectory());
        Assert.Equal(Path.Combine(linuxPlatform.Object.UserHomeDirectory, ".ollamarouter"), linuxDetector.GetDefaultOllamaRouterInstallDirectory());

        var macPlatform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.MacOS, InstallerConstants.RuntimeOsxArm64, _workspace.RootPath);
        var macDetector = new InstallationDetector(macPlatform.Object, registry.Object);
        Assert.Equal(Path.Combine(macPlatform.Object.UserHomeDirectory, "Applications", "NightCodeForge"), macDetector.GetDefaultNightCodeForgeInstallDirectory());
        Assert.Equal(Path.Combine(macPlatform.Object.UserHomeDirectory, ".ollamarouter"), macDetector.GetDefaultOllamaRouterInstallDirectory());
    }

    [Fact]
    public void Detect_Windows_MarksLegacyTrueWhenRegistryPointsToDifferentDirectoryThanEnvOverride()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var envInstallDir = _workspace.CreateSubdirectory("env-ncf");
        File.WriteAllText(Path.Combine(envInstallDir, InstallerConstants.WebExeWindows), "binary");
        platform.Setup(p => p.GetEnvironmentVariable(InstallerConstants.EnvNightCodeForgeHome)).Returns(envInstallDir);

        var registryDir = _workspace.CreateSubdirectory("other-registry-ncf");
        var registry = new Mock<IWindowsRegistryAccessor>();
        registry.Setup(r => r.ReadCurrentUserStringValue(InstallerConstants.WindowsUninstallRegistrySubKey, "InstallLocation"))
            .Returns(registryDir);

        var sut = new InstallationDetector(platform.Object, registry.Object);
        var result = sut.Detect();

        Assert.True(result.NightCodeForge.IsInstalled);
        Assert.Equal(Path.GetFullPath(envInstallDir), result.NightCodeForge.InstallDirectory);
        Assert.True(result.NightCodeForge.IsLegacyPublishInstall);
    }

    [Fact]
    public void Detect_DoesNotRecommendAirGappedBundleWhenOnlyStrayNupkgOrWrongRidArchiveExists()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var registry = new Mock<IWindowsRegistryAccessor>();

        var baseDir = platform.Object.InstallerBaseDirectory;
        File.WriteAllText(Path.Combine(baseDir, "NightCodeForge.Cli.1.0.0.nupkg"), "nupkg");
        File.WriteAllText(Path.Combine(baseDir, "NightCodeForge-Web-linux-x64.zip"), "wrong-rid");

        var sut = new InstallationDetector(platform.Object, registry.Object);
        var result = sut.Detect();

        Assert.Equal(InstallSourceMode.Online, result.RecommendedSourceMode);
        Assert.Null(result.DetectedBundleDirectory);
        Assert.Empty(result.DetectedBundleFiles);
    }

    [Fact]
    public void Detect_WhenNotInstalled_HonorsNightCodeForgeHomeAndOllamaRouterHome()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        var customNcf = Path.Combine(_workspace.RootPath, "custom-ncf-home");
        var customRouter = Path.Combine(_workspace.RootPath, "custom-router-home");

        platform.Setup(p => p.GetEnvironmentVariable(InstallerConstants.EnvNightCodeForgeHome)).Returns(customNcf);
        platform.Setup(p => p.GetEnvironmentVariable(InstallerConstants.EnvOllamaRouterHome)).Returns(customRouter);

        var registry = new Mock<IWindowsRegistryAccessor>();
        var sut = new InstallationDetector(platform.Object, registry.Object);
        var result = sut.Detect();

        Assert.False(result.NightCodeForge.IsInstalled);
        Assert.Equal(Path.GetFullPath(customNcf), result.NightCodeForge.InstallDirectory);
        Assert.False(result.OllamaRouter.IsInstalled);
        Assert.Equal(Path.GetFullPath(customRouter), result.OllamaRouter.InstallDirectory);
    }
}
