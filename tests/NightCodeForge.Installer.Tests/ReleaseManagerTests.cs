namespace NightCodeForge.Installer.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Moq;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

public sealed class ReleaseManagerTests : IDisposable
{
    private readonly TempTestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task ResolveLatestNightCodeForgeReleaseAsync_Online_ParsesReleaseAndSha256Sums()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var expectedHash = new string('a', 64);

        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("/repos/jetelain/NightCodeForge/releases/latest", StringComparison.Ordinal))
            {
                return Task.FromResult(TestArchiveHelper.JsonResponse("""
                {
                  "tag_name": "v1.4.0",
                  "assets": [
                    { "name": "NightCodeForge-Web-win-x64.zip", "browser_download_url": "https://dl.test/web.zip", "size": 9001 },
                    { "name": "SHA256SUMS.txt", "browser_download_url": "https://dl.test/SHA256SUMS.txt", "size": 100 }
                  ]
                }
                """));
            }

            if (url == "https://dl.test/SHA256SUMS.txt")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{expectedHash}  NightCodeForge-Web-win-x64.zip\n", Encoding.UTF8)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var httpClient = new HttpClient(handler);
        var sut = CreateSut(httpClient, platform.Object, out _, out _, out _);

        var asset = await sut.ResolveLatestNightCodeForgeReleaseAsync(new InstallSourceOptions(InstallSourceMode.Online, null));

        Assert.Equal("1.4.0", asset.Version);
        Assert.Equal("https://dl.test/web.zip", asset.SourceLocation);
        Assert.Equal(expectedHash, asset.Sha256Checksum);
        Assert.False(asset.IsLocalBundle);
    }

    [Fact]
    public async Task DeployNightCodeForgeAsync_LegacyInPlaceUpgrade_PreservesUserFilesAdaptsConfigFansOutLaunchersAndRestarts()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var installDir = Path.Combine(platform.Object.LocalApplicationDataDirectory, "NightCodeForge");
        var bundleDir = _workspace.CreateSubdirectory("bundle");
        var runningInstallerSource = Path.Combine(_workspace.RootPath, "download-src", InstallerConstants.InstallerExeWindows);
        Directory.CreateDirectory(Path.GetDirectoryName(runningInstallerSource)!);
        File.WriteAllText(runningInstallerSource, "new-installer-binary");

        // Pre-populate legacy publish.ps1 installation.
        Directory.CreateDirectory(Path.Combine(installDir, "config"));
        Directory.CreateDirectory(Path.Combine(installDir, "data"));
        Directory.CreateDirectory(Path.Combine(installDir, "logs"));
        Directory.CreateDirectory(Path.Combine(installDir, "wks"));
        File.WriteAllText(Path.Combine(installDir, InstallerConstants.WebExeWindows), "old-web");
        File.WriteAllText(Path.Combine(installDir, "nightcodeforge-web.cmd"), "legacy cmd");
        File.WriteAllText(Path.Combine(installDir, "config", "config.json"), """{"UserKey":"keep-me"}""");
        File.WriteAllText(Path.Combine(installDir, "config", "default-key.dat"), "secret-key");
        File.WriteAllText(Path.Combine(installDir, "data", "nightcodeforge.db"), "sqlite-bytes");
        File.WriteAllText(Path.Combine(installDir, "launcher.conf"), "logs_dir=logs\n");
        File.WriteAllText(Path.Combine(installDir, "appsettings.Production.json"), """{"Urls":"http://localhost:6100"}""");

        var archivePath = Path.Combine(bundleDir, "NightCodeForge-Web-win-x64.zip");
        var sha = TestArchiveHelper.CreateZipArchive(archivePath, new Dictionary<string, string>
        {
            ["NightCodeForge.Web.exe"] = "new-web-v2",
            ["ncf-web.exe"] = "new-launcher-v2",
            ["appsettings.json"] = """{"Logging":{"LogLevel":{"Default":"Warning"}},"AllowedHosts":"*"}""",
            ["skills/ncf-backlog/SKILL.md"] = "# Skill",
            ["VERSION"] = "2.0.0\n",
            ["README.md"] = "readme",
            ["LICENSE"] = "MIT"
        });
        File.WriteAllText(Path.Combine(bundleDir, "SHA256SUMS.txt"), $"{sha}  NightCodeForge-Web-win-x64.zip\n");

        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var sut = CreateSut(httpClient, platform.Object, out var runner, out var processControl, out var registry);

        processControl.SetupGet(c => c.CurrentProcessPath).Returns(runningInstallerSource);
        processControl.Setup(c => c.IsAnyProcessRunning(It.IsAny<IEnumerable<string>>()))
            .Returns((IEnumerable<string> names) => names.Contains("NightCodeForge.Web"));
        processControl.Setup(c => c.StopProcessesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var asset = await sut.ResolveLatestNightCodeForgeReleaseAsync(new InstallSourceOptions(InstallSourceMode.AirGappedBundle, bundleDir));
        var deployment = await sut.DeployNightCodeForgeAsync(asset, targetInstallDirectory: null, createDesktopShortcut: false);

        Assert.True(deployment.WasUpgrade);
        Assert.True(deployment.WasPreviouslyRunning);
        Assert.True(deployment.RestartedAfterSwap);
        Assert.True(deployment.SelfCopiedInstaller);
        Assert.Equal("2.0.0", deployment.Version);

        // User data strictly preserved
        Assert.Equal("""{"UserKey":"keep-me"}""", File.ReadAllText(Path.Combine(installDir, "config", "config.json")));
        Assert.Equal("secret-key", File.ReadAllText(Path.Combine(installDir, "config", "default-key.dat")));
        Assert.Equal("sqlite-bytes", File.ReadAllText(Path.Combine(installDir, "data", "nightcodeforge.db")));
        Assert.True(Directory.Exists(Path.Combine(installDir, "wks")));
        Assert.True(File.Exists(Path.Combine(installDir, "launcher.conf")));

        // Launcher fan-out and cleanup
        Assert.False(File.Exists(Path.Combine(installDir, "ncf-web.exe")));
        Assert.False(File.Exists(Path.Combine(installDir, "nightcodeforge-web.cmd")));
        Assert.True(File.Exists(Path.Combine(installDir, "nightcodeforge-web.exe")));
        Assert.True(File.Exists(Path.Combine(installDir, "bin", "nightcodeforge-web.exe")));
        Assert.True(File.Exists(Path.Combine(installDir, "bin", "ncf-web.exe")));
        Assert.True(File.Exists(Path.Combine(installDir, "bin", "nightcodeforge-web")));
        Assert.True(File.Exists(Path.Combine(installDir, InstallerConstants.InstallerExeWindows)));

        // Preserved Urls & adapted connection strings
        using var prodDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(installDir, "appsettings.Production.json")));
        Assert.Equal("http://localhost:6100", prodDoc.RootElement.GetProperty("Urls").GetString());
        Assert.Contains("nightcodeforge.db", prodDoc.RootElement.GetProperty("ConnectionStrings").GetProperty("DefaultConnection").GetString(), StringComparison.Ordinal);

        // Restarted detached after swap
        runner.Verify(
            r => r.RunAsync(
                It.Is<ProcessRunRequest>(q => q.Detached && q.FileName.EndsWith("ncf-web.exe", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // HKCU uninstall entry updated
        registry.Verify(
            reg => reg.WriteCurrentUserValues(InstallerConstants.WindowsUninstallRegistrySubKey, It.IsAny<IReadOnlyDictionary<string, object>>()),
            Times.Once);
    }

    [Fact]
    public async Task DeployOllamaRouterAsync_LinuxTarGzWithJsonComments_ExtractsUnwrapsAndRegistersProtocol()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        var bundleDir = _workspace.CreateSubdirectory("or-bundle");
        var archivePath = Path.Combine(bundleDir, "OllamaRouter-linux-x64.tar.gz");

        TestArchiveHelper.CreateTarGzArchive(
            archivePath,
            new Dictionary<string, string>
            {
                ["ollamarouter-server"] = "server-elf",
                ["ollamarouter"] = "cli-elf",
                ["ollamarouter-app"] = "app-elf",
                ["appsettings.Production.json"] = "// JSONC comment\n{\n  \"OllamaRouter\": {\n    \"BindAddress\": \"http://+:11434/\"\n  }\n}\n",
                ["VERSION"] = "1.5.0\n"
            },
            topLevelDirectory: "OllamaRouter-linux-x64");

        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var sut = CreateSut(httpClient, platform.Object, out _, out var processControl, out _);

        var asset = await sut.ResolveLatestOllamaRouterReleaseAsync(new InstallSourceOptions(InstallSourceMode.AirGappedBundle, bundleDir));
        var deployment = await sut.DeployOllamaRouterAsync(asset, targetInstallDirectory: null);

        Assert.Equal("1.5.0", deployment.Version);
        var routerDir = deployment.InstallDirectory;
        Assert.True(File.Exists(Path.Combine(routerDir, "ollamarouter-server")));
        Assert.True(File.Exists(Path.Combine(routerDir, "bin", "ollamarouter")));
        Assert.True(File.Exists(Path.Combine(routerDir, "bin", "ollamarouter-app")));

        using var prodDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(routerDir, "appsettings.Production.json")));
        var stateDir = prodDoc.RootElement.GetProperty("OllamaRouter").GetProperty("StateDirectory").GetString();
        Assert.Equal(Path.Combine(routerDir, "state").Replace('\\', '/'), stateDir);

        var desktopEntry = Path.Combine(platform.Object.UserHomeDirectory, ".local", "share", "applications", "ollamarouter.desktop");
        Assert.True(File.Exists(desktopEntry));
        Assert.Contains("x-scheme-handler/ollamarouter", File.ReadAllText(desktopEntry), StringComparison.Ordinal);

        processControl.Verify(
            c => c.StopProcessesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeployNightCodeForgeAsync_CorruptChecksum_ThrowsAndDoesNotTouchInstallDir()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var bundleDir = _workspace.CreateSubdirectory("bad-bundle");
        var archivePath = Path.Combine(bundleDir, "NightCodeForge-Web-win-x64.zip");
        TestArchiveHelper.CreateZipArchive(archivePath, new Dictionary<string, string>
        {
            ["NightCodeForge.Web.exe"] = "payload"
        });
        File.WriteAllText(Path.Combine(bundleDir, "SHA256SUMS.txt"), $"{new string('0', 64)}  NightCodeForge-Web-win-x64.zip\n");

        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var sut = CreateSut(httpClient, platform.Object, out _, out _, out _);

        var asset = await sut.ResolveLatestNightCodeForgeReleaseAsync(new InstallSourceOptions(InstallSourceMode.AirGappedBundle, bundleDir));

        await Assert.ThrowsAsync<ChecksumVerificationException>(() =>
            sut.DeployNightCodeForgeAsync(asset, targetInstallDirectory: null, createDesktopShortcut: false));
    }

    [Fact]
    public async Task DeployNightCodeForgeAsync_ZipSlipPathTraversal_ThrowsBundleValidationException()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var bundleDir = _workspace.CreateSubdirectory("zipslip-bundle");
        var archivePath = Path.Combine(bundleDir, "NightCodeForge-Web-win-x64.zip");
        TestArchiveHelper.CreateZipArchive(archivePath, new Dictionary<string, string>
        {
            ["../../outside-escape.txt"] = "malicious"
        });

        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var sut = CreateSut(httpClient, platform.Object, out _, out _, out _);

        var asset = await sut.ResolveLatestNightCodeForgeReleaseAsync(new InstallSourceOptions(InstallSourceMode.AirGappedBundle, bundleDir));

        await Assert.ThrowsAsync<BundleValidationException>(() =>
            sut.DeployNightCodeForgeAsync(asset, _workspace.CreateSubdirectory("target-zipslip"), createDesktopShortcut: false));
    }

    [Fact]
    public async Task DeployNightCodeForgeAsync_FailureDuringPostConfigure_RollsBackPreviousInstallation()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var installDir = _workspace.CreateSubdirectory("existing-ncf");
        File.WriteAllText(Path.Combine(installDir, InstallerConstants.WebExeWindows), "original-working-binary");
        File.WriteAllText(Path.Combine(installDir, InstallerConstants.VersionFileName), "1.0.0");

        var bundleDir = _workspace.CreateSubdirectory("rollback-bundle");
        var archivePath = Path.Combine(bundleDir, "NightCodeForge-Web-win-x64.zip");
        TestArchiveHelper.CreateZipArchive(archivePath, new Dictionary<string, string>
        {
            ["NightCodeForge.Web.exe"] = "broken-new-binary",
            ["appsettings.json"] = "{INVALID_JSON_CAUSING_ROLLBACK"
        });

        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var sut = CreateSut(httpClient, platform.Object, out _, out _, out _);

        var asset = await sut.ResolveLatestNightCodeForgeReleaseAsync(new InstallSourceOptions(InstallSourceMode.AirGappedBundle, bundleDir));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            sut.DeployNightCodeForgeAsync(asset, installDir, createDesktopShortcut: false));

        // Previous installation restored cleanly from .backup
        Assert.Equal("original-working-binary", File.ReadAllText(Path.Combine(installDir, InstallerConstants.WebExeWindows)));
        Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(installDir, InstallerConstants.VersionFileName)));
        Assert.Empty(Directory.GetDirectories(installDir, ".backup-*"));
    }

    [Fact]
    public async Task DeployNightCodeForgeAsync_OnlineDownload_SupportsHttpRangeResume()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var sourceZip = Path.Combine(_workspace.RootPath, "server-archive.zip");
        var sha = TestArchiveHelper.CreateZipArchive(sourceZip, new Dictionary<string, string>
        {
            ["NightCodeForge.Web.exe"] = "resumed-web-exe",
            ["ncf-web.exe"] = "resumed-launcher-exe",
            ["VERSION"] = "3.0.0"
        });
        var fullBytes = await File.ReadAllBytesAsync(sourceZip);
        var splitOffset = fullBytes.Length / 2;

        // Seed partial download cache file.
        var cacheDir = Path.Combine(Path.GetTempPath(), "ncf-installer-downloads");
        Directory.CreateDirectory(cacheDir);
        var partPath = Path.Combine(cacheDir, "3.0.0-NightCodeForge-Web-win-x64.zip.part");
        var finalCachedPath = Path.Combine(cacheDir, "3.0.0-NightCodeForge-Web-win-x64.zip");
        if (File.Exists(finalCachedPath))
        {
            File.Delete(finalCachedPath);
        }

        await File.WriteAllBytesAsync(partPath, fullBytes[..splitOffset]);

        long? observedRangeStart = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.Headers.Range?.Ranges.FirstOrDefault() is { From: { } from })
            {
                observedRangeStart = from;
                var remainder = fullBytes[(int)from..];
                var msg = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(remainder)
                };
                msg.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, fullBytes.Length - 1, fullBytes.Length);
                return Task.FromResult(msg);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(fullBytes)
            });
        });

        try
        {
            using var httpClient = new HttpClient(handler);
            var sut = CreateSut(httpClient, platform.Object, out _, out _, out _);

            var asset = new ComponentReleaseAsset(
                InstallerConstants.ComponentNightCodeForgeWeb,
                "3.0.0",
                "https://dl.test/NightCodeForge-Web-win-x64.zip",
                sha,
                fullBytes.Length,
                IsLocalBundle: false);

            var targetDir = _workspace.CreateSubdirectory("resumed-install");
            var deployment = await sut.DeployNightCodeForgeAsync(asset, targetDir, createDesktopShortcut: false);

            Assert.Equal(splitOffset, observedRangeStart);
            Assert.Equal("3.0.0", deployment.Version);
            Assert.Equal("resumed-web-exe", await File.ReadAllTextAsync(Path.Combine(targetDir, "NightCodeForge.Web.exe")));
        }
        finally
        {
            if (File.Exists(partPath))
            {
                File.Delete(partPath);
            }

            if (File.Exists(finalCachedPath))
            {
                File.Delete(finalCachedPath);
            }
        }
    }

    private ReleaseManager CreateSut(
        HttpClient httpClient,
        IPlatformEnvironment platform,
        out Mock<IProcessRunner> runnerMock,
        out Mock<IProcessControlService> processControlMock,
        out Mock<IWindowsRegistryAccessor> registryMock)
    {
        runnerMock = new Mock<IProcessRunner>();
        runnerMock.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        processControlMock = new Mock<IProcessControlService>();
        processControlMock.SetupGet(c => c.CurrentProcessId).Returns(99999);
        processControlMock.SetupGet(c => c.CurrentProcessPath).Returns((string?)null);

        registryMock = new Mock<IWindowsRegistryAccessor>();

        var detector = new InstallationDetector(platform, registryMock.Object);
        var osIntegration = new OsIntegrationService(platform, registryMock.Object, runnerMock.Object, TimeProvider.System);

        return new ReleaseManager(
            httpClient,
            platform,
            runnerMock.Object,
            processControlMock.Object,
            detector,
            osIntegration);
    }

    [Fact]
    public async Task ResolveLatestNightCodeForgeReleaseAsync_Sha256SumsDownloadFails_ThrowsReleaseDownloadException()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("/repos/jetelain/NightCodeForge/releases/latest", StringComparison.Ordinal))
            {
                return Task.FromResult(TestArchiveHelper.JsonResponse("""
                {
                  "tag_name": "v1.4.0",
                  "assets": [
                    { "name": "NightCodeForge-Web-win-x64.zip", "browser_download_url": "https://dl.test/web.zip", "size": 9001 },
                    { "name": "SHA256SUMS.txt", "browser_download_url": "https://dl.test/SHA256SUMS.txt", "size": 100 }
                  ]
                }
                """));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });

        using var httpClient = new HttpClient(handler);
        var sut = CreateSut(httpClient, platform.Object, out _, out _, out _);

        await Assert.ThrowsAsync<ReleaseDownloadException>(() =>
            sut.ResolveLatestNightCodeForgeReleaseAsync(new InstallSourceOptions(InstallSourceMode.Online, null)));
    }

    [Fact]
    public async Task ResolveLatestNightCodeForgeReleaseAsync_LocalBundle_IgnoresNestedVersionFileAndReadsRootVersion()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var bundleDir = _workspace.CreateSubdirectory("nested-version-bundle");
        var archivePath = Path.Combine(bundleDir, "NightCodeForge-Web-win-x64.zip");

        TestArchiveHelper.CreateZipArchive(archivePath, new Dictionary<string, string>
        {
            ["wwwroot/lib/third-party/VERSION"] = "9.9.9-wrong",
            ["VERSION"] = "2.3.4\n",
            ["NightCodeForge.Web.exe"] = "bin"
        });

        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var sut = CreateSut(httpClient, platform.Object, out _, out _, out _);

        var asset = await sut.ResolveLatestNightCodeForgeReleaseAsync(new InstallSourceOptions(InstallSourceMode.AirGappedBundle, bundleDir));
        Assert.Equal("2.3.4", asset.Version);
    }

    [Fact]
    public async Task DeployNightCodeForgeAsync_FreshCustomTargetDirectory_SetsWasUpgradeFalseEvenWhenDefaultExists()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var defaultDir = Path.Combine(platform.Object.LocalApplicationDataDirectory, "NightCodeForge");
        Directory.CreateDirectory(defaultDir);
        File.WriteAllText(Path.Combine(defaultDir, InstallerConstants.WebExeWindows), "existing-default");

        var bundleDir = _workspace.CreateSubdirectory("fresh-target-bundle");
        var archivePath = Path.Combine(bundleDir, "NightCodeForge-Web-win-x64.zip");
        TestArchiveHelper.CreateZipArchive(archivePath, new Dictionary<string, string>
        {
            ["NightCodeForge.Web.exe"] = "fresh-web",
            ["ncf-web.exe"] = "fresh-launcher",
            ["VERSION"] = "1.0.0"
        });

        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var sut = CreateSut(httpClient, platform.Object, out _, out _, out _);

        var freshCustomDir = _workspace.CreateSubdirectory("brand-new-custom-dir");
        var asset = await sut.ResolveLatestNightCodeForgeReleaseAsync(new InstallSourceOptions(InstallSourceMode.AirGappedBundle, bundleDir));
        var result = await sut.DeployNightCodeForgeAsync(asset, freshCustomDir, createDesktopShortcut: false);

        Assert.False(result.WasUpgrade);
    }

    [Fact]
    public async Task DeployNightCodeForgeAsync_OnlineDownload_SanitizesSlashInVersionTagAndHandlesHttp416AndMismatchedRange()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var sourceZip = Path.Combine(_workspace.RootPath, "slash-tag.zip");
        var sha = TestArchiveHelper.CreateZipArchive(sourceZip, new Dictionary<string, string>
        {
            ["NightCodeForge.Web.exe"] = "slash-web-exe",
            ["ncf-web.exe"] = "slash-launcher-exe",
            ["VERSION"] = "4.1.0"
        });
        var fullBytes = await File.ReadAllBytesAsync(sourceZip);

        var uniqueTag = $"release/4.1.{Guid.NewGuid():N}"[..20];
        var sanitizedVersion = uniqueTag.Replace('/', '_');
        var cacheDir = Path.Combine(Path.GetTempPath(), InstallerConstants.DownloadCacheDirectoryName);
        Directory.CreateDirectory(cacheDir);
        var partPath = Path.Combine(cacheDir, $"{sanitizedVersion}-NightCodeForge-Web-win-x64.zip.part");
        var finalCachedPath = Path.Combine(cacheDir, $"{sanitizedVersion}-NightCodeForge-Web-win-x64.zip");

        await File.WriteAllBytesAsync(partPath, new byte[fullBytes.Length + 64]);

        var requestCount = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            requestCount++;
            if (requestCount == 1 && req.Headers.Range is not null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(fullBytes)
            });
        });

        try
        {
            using var httpClient = new HttpClient(handler);
            var sut = CreateSut(httpClient, platform.Object, out _, out _, out _);

            var asset = new ComponentReleaseAsset(
                InstallerConstants.ComponentNightCodeForgeWeb,
                uniqueTag,
                "https://dl.test/NightCodeForge-Web-win-x64.zip",
                sha,
                fullBytes.Length,
                IsLocalBundle: false);

            var targetDir = _workspace.CreateSubdirectory("slash-install");
            var deployment = await sut.DeployNightCodeForgeAsync(asset, targetDir, createDesktopShortcut: false);

            Assert.Equal(2, requestCount);
            Assert.Equal(uniqueTag, deployment.Version);
            Assert.Equal("slash-web-exe", await File.ReadAllTextAsync(Path.Combine(targetDir, "NightCodeForge.Web.exe")));
        }
        finally
        {
            if (File.Exists(partPath))
            {
                File.Delete(partPath);
            }

            if (File.Exists(finalCachedPath))
            {
                File.Delete(finalCachedPath);
            }
        }
    }

    [Fact]
    public void ParseSha256Sums_RejectsNonHexHashes()
    {
        var invalidHash = new string('g', 64);
        var validHash = new string('a', 64);
        var parsed = ReleaseManager.ParseSha256Sums(
            $"{invalidHash}  NightCodeForge-Web-win-x64.zip\n{validHash}  OllamaRouter-win-x64.zip\n");

        Assert.False(parsed.ContainsKey("NightCodeForge-Web-win-x64.zip"));
        Assert.Equal(validHash, parsed["OllamaRouter-win-x64.zip"]);
    }

    [Fact]
    public async Task DeployOllamaRouterAsync_TarGzAbsoluteOrTraversalEntry_ThrowsBundleValidationException()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        var bundleDir = _workspace.CreateSubdirectory("bad-targz-bundle");
        var archivePath = Path.Combine(bundleDir, "OllamaRouter-linux-x64.tar.gz");

        TestArchiveHelper.CreateTarGzArchive(
            archivePath,
            new Dictionary<string, string>
            {
                ["/etc/passwd-overwrite"] = "evil"
            });

        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var sut = CreateSut(httpClient, platform.Object, out _, out _, out _);

        var asset = await sut.ResolveLatestOllamaRouterReleaseAsync(new InstallSourceOptions(InstallSourceMode.AirGappedBundle, bundleDir));

        await Assert.ThrowsAsync<BundleValidationException>(() =>
            sut.DeployOllamaRouterAsync(asset, _workspace.CreateSubdirectory("or-bad-target")));
    }

    [Fact]
    public async Task DeployOllamaRouterAsync_CustomPathWithoutOllamaRouterName_UsesOllamaRouterPathBlockLabel()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        var bundleDir = _workspace.CreateSubdirectory("or-label-bundle");
        var archivePath = Path.Combine(bundleDir, "OllamaRouter-linux-x64.tar.gz");

        TestArchiveHelper.CreateTarGzArchive(
            archivePath,
            new Dictionary<string, string>
            {
                ["ollamarouter-server"] = "server-elf",
                ["ollamarouter"] = "cli-elf",
                ["ollamarouter-app"] = "app-elf",
                ["VERSION"] = "1.0.0"
            });

        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var sut = CreateSut(httpClient, platform.Object, out _, out _, out _);

        var customRouterDir = _workspace.CreateSubdirectory("custom-model-proxy");
        var asset = await sut.ResolveLatestOllamaRouterReleaseAsync(new InstallSourceOptions(InstallSourceMode.AirGappedBundle, bundleDir));
        await sut.DeployOllamaRouterAsync(asset, customRouterDir);

        var profileText = await File.ReadAllTextAsync(Path.Combine(platform.Object.UserHomeDirectory, ".profile"));
        Assert.Contains("NightCodeForge Installer PATH (OllamaRouter)", profileText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployNightCodeForgeAsync_OnlineDownload_MismatchedPartialContentRangeFallsBackToFreshDownload()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var sourceZip = Path.Combine(_workspace.RootPath, "mismatch-range.zip");
        var sha = TestArchiveHelper.CreateZipArchive(sourceZip, new Dictionary<string, string>
        {
            ["NightCodeForge.Web.exe"] = "fresh-after-mismatch",
            ["ncf-web.exe"] = "launcher",
            ["VERSION"] = "5.0.0"
        });
        var fullBytes = await File.ReadAllBytesAsync(sourceZip);

        var uniqueVersion = $"5.0.{Guid.NewGuid():N}"[..12];
        var cacheDir = Path.Combine(Path.GetTempPath(), InstallerConstants.DownloadCacheDirectoryName);
        Directory.CreateDirectory(cacheDir);
        var partPath = Path.Combine(cacheDir, $"{uniqueVersion}-NightCodeForge-Web-win-x64.zip.part");
        var finalCachedPath = Path.Combine(cacheDir, $"{uniqueVersion}-NightCodeForge-Web-win-x64.zip");

        await File.WriteAllBytesAsync(partPath, new byte[10]);

        var requestCount = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            requestCount++;
            if (requestCount == 1 && req.Headers.Range is not null)
            {
                // Return mismatched Content-Range (From=5 instead of 10).
                var msg = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(fullBytes[5..])
                };
                msg.Content.Headers.ContentRange = new ContentRangeHeaderValue(5, fullBytes.Length - 1, fullBytes.Length);
                return Task.FromResult(msg);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(fullBytes)
            });
        });

        try
        {
            using var httpClient = new HttpClient(handler);
            var sut = CreateSut(httpClient, platform.Object, out _, out _, out _);

            var asset = new ComponentReleaseAsset(
                InstallerConstants.ComponentNightCodeForgeWeb,
                uniqueVersion,
                "https://dl.test/NightCodeForge-Web-win-x64.zip",
                sha,
                fullBytes.Length,
                IsLocalBundle: false);

            var targetDir = _workspace.CreateSubdirectory("mismatch-install");
            var deployment = await sut.DeployNightCodeForgeAsync(asset, targetDir, createDesktopShortcut: false);

            Assert.Equal(2, requestCount);
            Assert.Equal("fresh-after-mismatch", await File.ReadAllTextAsync(Path.Combine(targetDir, "NightCodeForge.Web.exe")));
            Assert.Equal(uniqueVersion, deployment.Version);
        }
        finally
        {
            if (File.Exists(partPath))
            {
                File.Delete(partPath);
            }

            if (File.Exists(finalCachedPath))
            {
                File.Delete(finalCachedPath);
            }
        }
    }
}
