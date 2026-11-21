namespace NightCodeForge.Installer.Tests;

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Moq;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Models;

internal sealed class TempTestWorkspace : IDisposable
{
    public TempTestWorkspace()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "ncf-installer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public string CreateSubdirectory(string relativePath)
    {
        var full = Path.Combine(RootPath, relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _handler(request, cancellationToken);
}

internal static class TestArchiveHelper
{
    public static string CreateZipArchive(string zipPath, IReadOnlyDictionary<string, string> entries)
    {
        var dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (entryPath, content) in entries)
            {
                var entry = archive.CreateEntry(entryPath);
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return ComputeSha256(zipPath);
    }

    public static string CreateTarGzArchive(
        string tarGzPath,
        IReadOnlyDictionary<string, string> entries,
        string? topLevelDirectory = null)
    {
        var dir = Path.GetDirectoryName(tarGzPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(tarGzPath))
        {
            File.Delete(tarGzPath);
        }

        using (var fileStream = File.Create(tarGzPath))
        using (var gzipStream = new GZipStream(fileStream, CompressionMode.Compress))
        using (var tarWriter = new TarWriter(gzipStream))
        {
            if (!string.IsNullOrWhiteSpace(topLevelDirectory))
            {
                tarWriter.WriteEntry(new PaxTarEntry(TarEntryType.Directory, topLevelDirectory.TrimEnd('/') + "/"));
            }

            foreach (var (relativePath, content) in entries)
            {
                var entryName = string.IsNullOrWhiteSpace(topLevelDirectory)
                    ? relativePath
                    : $"{topLevelDirectory.TrimEnd('/')}/{relativePath.TrimStart('/')}";
                var bytes = Encoding.UTF8.GetBytes(content);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
                {
                    DataStream = new MemoryStream(bytes)
                };
                tarWriter.WriteEntry(entry);
            }
        }

        return ComputeSha256(tarGzPath);
    }

    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static Mock<IPlatformEnvironment> CreateMockPlatform(
        HostPlatformType platformType,
        string rid,
        string workspaceRoot)
    {
        var homeDir = Path.Combine(workspaceRoot, "home");
        var localAppData = Path.Combine(workspaceRoot, "localappdata");
        var appData = Path.Combine(workspaceRoot, "appdata");
        var desktop = Path.Combine(workspaceRoot, "desktop");
        var baseDir = Path.Combine(workspaceRoot, "installer-base");

        Directory.CreateDirectory(homeDir);
        Directory.CreateDirectory(localAppData);
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(desktop);
        Directory.CreateDirectory(baseDir);

        var envVars = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var userPath = string.Empty;

        var mock = new Mock<IPlatformEnvironment>();
        mock.SetupGet(p => p.Platform).Returns(platformType);
        mock.SetupGet(p => p.CurrentRuntimeIdentifier).Returns(rid);
        mock.SetupGet(p => p.UserHomeDirectory).Returns(homeDir);
        mock.SetupGet(p => p.LocalApplicationDataDirectory).Returns(localAppData);
        mock.SetupGet(p => p.ApplicationDataDirectory).Returns(appData);
        mock.SetupGet(p => p.DesktopDirectory).Returns(desktop);
        mock.SetupGet(p => p.InstallerBaseDirectory).Returns(baseDir);
        mock.Setup(p => p.GetEnvironmentVariable(It.IsAny<string>()))
            .Returns((string key) => envVars.TryGetValue(key, out var val) ? val : null);
        mock.Setup(p => p.GetUserPathVariable()).Returns(() => userPath);
        mock.Setup(p => p.SetUserPathVariable(It.IsAny<string>())).Callback((string val) => userPath = val);
        mock.Setup(p => p.IsCommandOnPath(It.IsAny<string>())).Returns(false);

        return mock;
    }

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
