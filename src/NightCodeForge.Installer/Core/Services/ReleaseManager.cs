namespace NightCodeForge.Installer.Core.Services;

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Serialization;

/// <summary>
/// Production release manager handling Online GitHub Releases, Air-Gapped Local Bundles,
/// resumable downloads, SHA-256 verification, <c>.zip</c>/<c>.tar.gz</c> extraction,
/// atomic <c>.backup</c> swap/rollback, and post-extract configuration for NightCodeForge and OllamaRouter.
/// </summary>
public sealed class ReleaseManager : IReleaseManager
{
    private const string StepRelease = "Release Delivery";
    private const string StepSwap = "Atomic Swap";
    private const string StepConfigure = "Post-Extract Configuration";
    private const string KeyUrls = "Urls";
    private const string KeyConnectionStrings = "ConnectionStrings";
    private const string KeyDefaultConnection = "DefaultConnection";
    private const string KeyHangfireConnection = "HangfireConnection";
    private const string KeyLogging = "Logging";
    private const string KeyLogLevel = "LogLevel";
    private const string KeyLogsDirectory = "LogsDirectory";
    private const string KeyOllamaRouter = "OllamaRouter";
    private const string KeyStateDirectory = "StateDirectory";

    private readonly HttpClient _httpClient;
    private readonly IPlatformEnvironment _platform;
    private readonly IProcessRunner _processRunner;
    private readonly IProcessControlService _processControl;
    private readonly IInstallationDetector _installationDetector;
    private readonly IOsIntegrationService _osIntegration;

    public ReleaseManager(
        HttpClient httpClient,
        IPlatformEnvironment platform,
        IProcessRunner processRunner,
        IProcessControlService processControl,
        IInstallationDetector installationDetector,
        IOsIntegrationService osIntegration)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _processControl = processControl ?? throw new ArgumentNullException(nameof(processControl));
        _installationDetector = installationDetector ?? throw new ArgumentNullException(nameof(installationDetector));
        _osIntegration = osIntegration ?? throw new ArgumentNullException(nameof(osIntegration));
    }

    public Task<ComponentReleaseAsset> ResolveLatestNightCodeForgeReleaseAsync(
        InstallSourceOptions source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var expectedArchiveName = GetNightCodeForgeArchiveName(_platform.CurrentRuntimeIdentifier);
        return source.Mode == InstallSourceMode.AirGappedBundle
            ? ResolveLocalBundleAssetAsync(
                InstallerConstants.ComponentNightCodeForgeWeb,
                source.BundleDirectory,
                expectedArchiveName,
                cancellationToken)
            : ResolveGitHubAssetAsync(
                InstallerConstants.ComponentNightCodeForgeWeb,
                InstallerConstants.NightCodeForgeRepository,
                expectedArchiveName,
                cancellationToken);
    }

    public Task<ComponentReleaseAsset> ResolveLatestOllamaRouterReleaseAsync(
        InstallSourceOptions source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var expectedArchiveName = GetOllamaRouterArchiveName(_platform.Platform, _platform.CurrentRuntimeIdentifier);
        return source.Mode == InstallSourceMode.AirGappedBundle
            ? ResolveLocalBundleAssetAsync(
                InstallerConstants.ComponentOllamaRouter,
                source.BundleDirectory,
                expectedArchiveName,
                cancellationToken)
            : ResolveGitHubAssetAsync(
                InstallerConstants.ComponentOllamaRouter,
                InstallerConstants.OllamaRouterRepository,
                expectedArchiveName,
                cancellationToken);
    }

    public async Task VerifyChecksumAsync(
        string filePath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File to checksum was not found: '{filePath}'.", filePath);
        }

        var normalizedExpected = expectedSha256.Trim().ToLowerInvariant();
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actualHex = Convert.ToHexStringLower(hashBytes);

        if (!string.Equals(actualHex, normalizedExpected, StringComparison.Ordinal))
        {
            throw new ChecksumVerificationException(Path.GetFileName(filePath), normalizedExpected, actualHex);
        }
    }

    public async Task<ReleaseDeploymentResult> DeployNightCodeForgeAsync(
        ComponentReleaseAsset asset,
        string? targetInstallDirectory,
        bool createDesktopShortcut,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var discovery = _installationDetector.Detect();
        var installDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            string.IsNullOrWhiteSpace(targetInstallDirectory)
                ? discovery.NightCodeForge.InstallDirectory
                : targetInstallDirectory));
        var wasUpgrade = InstallationDetector.FindNightCodeForgeBinary(installDir) is not null;

        var archivePath = await AcquireAndVerifyAssetAsync(asset, progress, cancellationToken).ConfigureAwait(false);
        var stagingRoot = CreateTempDirectory("ncf-web-stage-");

        try
        {
            Report(progress, StepRelease, $"Extracting {Path.GetFileName(archivePath)}...", InstallerLogLevel.Info, 45);
            var payloadRoot = await ExtractArchiveAsync(archivePath, stagingRoot, cancellationToken).ConfigureAwait(false);

            var wasRunning = _processControl.IsAnyProcessRunning(InstallerConstants.NightCodeForgeWebProcesses) ||
                             _processControl.IsAnyProcessRunning(InstallerConstants.NightCodeForgeLauncherProcesses);

            if (wasRunning)
            {
                Report(progress, StepSwap, "Stopping running NightCodeForge launcher and web processes...", InstallerLogLevel.Info, 55);
                await StopNightCodeForgeProcessesAsync(cancellationToken).ConfigureAwait(false);
            }

            var preservedUrls = ReadPreservedWebUrls(installDir);
            var preservedSet = new HashSet<string>(InstallerConstants.NightCodeForgePreservedInstallItems, StringComparer.OrdinalIgnoreCase);

            var selfCopied = false;
            await ExecuteAtomicSwapAsync(
                installDir,
                payloadRoot,
                preservedSet,
                progress,
                async () =>
                {
                    selfCopied = await ConfigureNightCodeForgeDeploymentAsync(
                        installDir,
                        asset.Version,
                        preservedUrls,
                        createDesktopShortcut,
                        discovery.OllamaRouter.IsInstalled ? discovery.OllamaRouter.InstallDirectory : null,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            var restarted = false;
            if (wasRunning)
            {
                restarted = await RestartNightCodeForgeAsync(installDir, cancellationToken).ConfigureAwait(false);
            }

            Report(progress, StepConfigure, $"NightCodeForge {asset.Version} deployed to '{installDir}'.", InstallerLogLevel.Info, 100);
            return new ReleaseDeploymentResult(
                ComponentName: asset.ComponentName,
                Version: asset.Version,
                InstallDirectory: installDir,
                WasUpgrade: wasUpgrade,
                WasPreviouslyRunning: wasRunning,
                RestartedAfterSwap: restarted,
                SelfCopiedInstaller: selfCopied);
        }
        finally
        {
            DeleteDirectoryBestEffort(stagingRoot);
        }
    }

    public async Task<ReleaseDeploymentResult> DeployOllamaRouterAsync(
        ComponentReleaseAsset asset,
        string? targetInstallDirectory,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var discovery = _installationDetector.Detect();
        var installDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            string.IsNullOrWhiteSpace(targetInstallDirectory)
                ? discovery.OllamaRouter.InstallDirectory
                : targetInstallDirectory));
        var wasUpgrade = InstallationDetector.FindOllamaRouterBinary(installDir) is not null;

        var archivePath = await AcquireAndVerifyAssetAsync(asset, progress, cancellationToken).ConfigureAwait(false);
        var stagingRoot = CreateTempDirectory("ncf-or-stage-");

        try
        {
            Report(progress, StepRelease, $"Extracting {Path.GetFileName(archivePath)}...", InstallerLogLevel.Info, 45);
            var payloadRoot = await ExtractArchiveAsync(archivePath, stagingRoot, cancellationToken).ConfigureAwait(false);

            var wasRunning = _processControl.IsAnyProcessRunning(InstallerConstants.OllamaRouterProcesses);
            if (wasRunning)
            {
                Report(progress, StepSwap, "Stopping running OllamaRouter processes...", InstallerLogLevel.Info, 55);
                await StopOllamaRouterProcessesAsync(installDir, _processRunner, _processControl, _platform.Platform, cancellationToken)
                    .ConfigureAwait(false);
            }

            var preservedSet = new HashSet<string>(InstallerConstants.OllamaRouterPreservedItems, StringComparer.OrdinalIgnoreCase);

            await ExecuteAtomicSwapAsync(
                installDir,
                payloadRoot,
                preservedSet,
                progress,
                () => ConfigureOllamaRouterDeploymentAsync(installDir, asset.Version, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            var restarted = false;
            if (wasRunning)
            {
                restarted = await RestartOllamaRouterAsync(installDir, cancellationToken).ConfigureAwait(false);
            }

            Report(progress, StepConfigure, $"OllamaRouter {asset.Version} deployed to '{installDir}'.", InstallerLogLevel.Info, 100);
            return new ReleaseDeploymentResult(
                ComponentName: asset.ComponentName,
                Version: asset.Version,
                InstallDirectory: installDir,
                WasUpgrade: wasUpgrade,
                WasPreviouslyRunning: wasRunning,
                RestartedAfterSwap: restarted,
                SelfCopiedInstaller: false);
        }
        finally
        {
            DeleteDirectoryBestEffort(stagingRoot);
        }
    }

    internal static async Task StopOllamaRouterProcessesAsync(
        string installDir,
        IProcessRunner processRunner,
        IProcessControlService processControl,
        HostPlatformType platform,
        CancellationToken cancellationToken)
    {
        var cliName = platform == HostPlatformType.Windows
            ? InstallerConstants.OllamaRouterCliWindows
            : InstallerConstants.OllamaRouterCliUnix;
        var cliPath = Path.Combine(installDir, cliName);

        if (File.Exists(cliPath))
        {
            try
            {
                await processRunner.RunAsync(
                    new ProcessRunRequest
                    {
                        FileName = cliPath,
                        Arguments = ["stop"],
                        WorkingDirectory = installDir,
                        Timeout = TimeSpan.FromSeconds(InstallerConstants.OllamaRouterStopTimeoutSeconds)
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                // Justification: Best-effort graceful stop falls back to process termination below.
                _ = ex;
            }
        }

        await processControl.StopProcessesAsync(
            InstallerConstants.OllamaRouterProcesses,
            TimeSpan.FromSeconds(InstallerConstants.ProcessStopTimeoutSeconds),
            cancellationToken).ConfigureAwait(false);
    }

    internal static Dictionary<string, string> ParseSha256Sums(string content)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(content))
        {
            return map;
        }

        foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIdx = line.IndexOfAny([' ', '\t']);
            if (separatorIdx <= 0)
            {
                continue;
            }

            var hash = line[..separatorIdx].Trim();
            var filePart = line[(separatorIdx + 1)..].Trim().TrimStart('*');
            var fileName = Path.GetFileName(filePart);

            if (hash.Length == InstallerConstants.Sha256HexLength &&
                hash.All(char.IsAsciiHexDigit) &&
                !string.IsNullOrWhiteSpace(fileName))
            {
                map[fileName] = hash.ToLowerInvariant();
            }
        }

        return map;
    }

    internal static string GetNightCodeForgeArchiveName(string rid) =>
        $"{InstallerConstants.ComponentNightCodeForgeWeb}-{rid}{InstallerConstants.ZipExtension}";

    internal static string GetOllamaRouterArchiveName(HostPlatformType platform, string rid) =>
        platform == HostPlatformType.Windows
            ? $"{InstallerConstants.ComponentOllamaRouter}-{rid}{InstallerConstants.ZipExtension}"
            : $"{InstallerConstants.ComponentOllamaRouter}-{rid}{InstallerConstants.TarGzExtension}";

    private async Task<ComponentReleaseAsset> ResolveLocalBundleAssetAsync(
        string componentName,
        string? bundleDirectory,
        string expectedArchiveName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bundleDirectory) || !Directory.Exists(bundleDirectory))
        {
            throw new BundleValidationException($"Air-gapped bundle directory was not found: '{bundleDirectory}'.");
        }

        var fullBundleDir = Path.GetFullPath(bundleDirectory);
        var archivePath = Path.Combine(fullBundleDir, expectedArchiveName);
        if (!File.Exists(archivePath))
        {
            throw new BundleValidationException(
                $"Required archive '{expectedArchiveName}' was not found in air-gapped bundle directory '{fullBundleDir}'.");
        }

        string? expectedChecksum = null;
        var checksumsPath = Path.Combine(fullBundleDir, InstallerConstants.Sha256SumsFileName);
        if (File.Exists(checksumsPath))
        {
            var checksumsContent = await File.ReadAllTextAsync(checksumsPath, cancellationToken).ConfigureAwait(false);
            var parsed = ParseSha256Sums(checksumsContent);
            parsed.TryGetValue(expectedArchiveName, out expectedChecksum);
        }

        var version = await TryReadVersionFromArchiveAsync(archivePath, cancellationToken).ConfigureAwait(false)
                      ?? InstallerConstants.DefaultFallbackVersion;
        var fileInfo = new FileInfo(archivePath);

        return new ComponentReleaseAsset(
            ComponentName: componentName,
            Version: version,
            SourceLocation: archivePath,
            Sha256Checksum: expectedChecksum,
            FileSizeBytes: fileInfo.Length,
            IsLocalBundle: true);
    }

    private async Task<ComponentReleaseAsset> ResolveGitHubAssetAsync(
        string componentName,
        string repository,
        string expectedArchiveName,
        CancellationToken cancellationToken)
    {
        var apiUrl = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            InstallerConstants.GitHubLatestReleaseUrlFormat,
            repository);

        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.UserAgent.ParseAdd(InstallerConstants.HttpUserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ReleaseDownloadException(
                $"GitHub release lookup for '{repository}' failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync(
            responseStream,
            InstallerJsonContext.Default.GitHubReleaseDto,
            cancellationToken).ConfigureAwait(false);

        if (release is null)
        {
            throw new ReleaseDownloadException($"GitHub release payload for '{repository}' was empty.");
        }

        var matchingAsset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, expectedArchiveName, StringComparison.OrdinalIgnoreCase));
        if (matchingAsset is null)
        {
            throw new ReleaseDownloadException(
                $"Release '{release.TagName}' in '{repository}' does not contain asset '{expectedArchiveName}'.");
        }

        string? expectedChecksum = null;
        var shaAsset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, InstallerConstants.Sha256SumsFileName, StringComparison.OrdinalIgnoreCase));

        if (shaAsset is not null)
        {
            using var shaRequest = new HttpRequestMessage(HttpMethod.Get, shaAsset.BrowserDownloadUrl);
            shaRequest.Headers.UserAgent.ParseAdd(InstallerConstants.HttpUserAgent);
            using var shaResponse = await _httpClient.SendAsync(shaRequest, cancellationToken).ConfigureAwait(false);
            if (!shaResponse.IsSuccessStatusCode)
            {
                throw new ReleaseDownloadException(
                    $"Failed to download '{InstallerConstants.Sha256SumsFileName}' for '{repository}': HTTP {(int)shaResponse.StatusCode} ({shaResponse.ReasonPhrase}).");
            }

            var content = await shaResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = ParseSha256Sums(content);
            parsed.TryGetValue(matchingAsset.Name, out expectedChecksum);
        }

        var normalizedVersion = release.TagName.Trim().TrimStart('v', 'V');
        if (string.IsNullOrWhiteSpace(normalizedVersion))
        {
            normalizedVersion = InstallerConstants.DefaultFallbackVersion;
        }

        return new ComponentReleaseAsset(
            ComponentName: componentName,
            Version: normalizedVersion,
            SourceLocation: matchingAsset.BrowserDownloadUrl,
            Sha256Checksum: expectedChecksum,
            FileSizeBytes: matchingAsset.Size,
            IsLocalBundle: false);
    }

    private async Task<string> AcquireAndVerifyAssetAsync(
        ComponentReleaseAsset asset,
        IProgress<InstallerProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        string localArchivePath;

        if (asset.IsLocalBundle)
        {
            if (!File.Exists(asset.SourceLocation))
            {
                throw new BundleValidationException($"Local bundle archive was not found: '{asset.SourceLocation}'.");
            }

            localArchivePath = asset.SourceLocation;
        }
        else
        {
            localArchivePath = await DownloadWithResumeAsync(asset, progress, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(asset.Sha256Checksum))
        {
            Report(progress, StepRelease, $"Verifying SHA-256 checksum for {Path.GetFileName(localArchivePath)}...", InstallerLogLevel.Info, 40);
            try
            {
                await VerifyChecksumAsync(localArchivePath, asset.Sha256Checksum, cancellationToken).ConfigureAwait(false);
            }
            catch (ChecksumVerificationException)
            {
                if (!asset.IsLocalBundle && File.Exists(localArchivePath))
                {
                    File.Delete(localArchivePath);
                }

                throw;
            }
        }

        return localArchivePath;
    }

    private async Task<string> DownloadWithResumeAsync(
        ComponentReleaseAsset asset,
        IProgress<InstallerProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), InstallerConstants.DownloadCacheDirectoryName);
        Directory.CreateDirectory(cacheDir);

        var sanitizedVersion = SanitizeFileNameSegment(asset.Version);
        var fileName = Path.GetFileName(new Uri(asset.SourceLocation).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"{asset.ComponentName}-{sanitizedVersion}.archive";
        }

        var targetPath = Path.Combine(cacheDir, $"{sanitizedVersion}-{fileName}");
        var partPath = targetPath + InstallerConstants.PartFileExtension;

        if (File.Exists(targetPath) && !string.IsNullOrWhiteSpace(asset.Sha256Checksum))
        {
            try
            {
                await VerifyChecksumAsync(targetPath, asset.Sha256Checksum, cancellationToken).ConfigureAwait(false);
                return targetPath;
            }
            catch (ChecksumVerificationException)
            {
                File.Delete(targetPath);
            }
        }

        var existingPartBytes = File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.SourceLocation);
        request.Headers.UserAgent.ParseAdd(InstallerConstants.HttpUserAgent);
        if (existingPartBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingPartBytes, null);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && File.Exists(partPath))
        {
            File.Delete(partPath);
            await DownloadFreshAsync(asset, partPath, progress, cancellationToken).ConfigureAwait(false);
        }
        else if (response.StatusCode == HttpStatusCode.PartialContent && existingPartBytes > 0)
        {
            var rangeFrom = response.Content.Headers.ContentRange?.From;
            if (rangeFrom is null || rangeFrom == existingPartBytes)
            {
                await WriteDownloadStreamAsync(
                    response,
                    asset,
                    partPath,
                    append: true,
                    initialBytes: existingPartBytes,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (rangeFrom == 0)
            {
                await WriteDownloadStreamAsync(
                    response,
                    asset,
                    partPath,
                    append: false,
                    initialBytes: 0L,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (File.Exists(partPath))
                {
                    File.Delete(partPath);
                }

                await DownloadFreshAsync(asset, partPath, progress, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await WriteDownloadStreamAsync(
                response,
                asset,
                partPath,
                append: false,
                initialBytes: 0L,
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        File.Move(partPath, targetPath, overwrite: true);
        return targetPath;
    }

    private async Task DownloadFreshAsync(
        ComponentReleaseAsset asset,
        string partPath,
        IProgress<InstallerProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        using var retryRequest = new HttpRequestMessage(HttpMethod.Get, asset.SourceLocation);
        retryRequest.Headers.UserAgent.ParseAdd(InstallerConstants.HttpUserAgent);
        using var retryResponse = await _httpClient.SendAsync(retryRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await WriteDownloadStreamAsync(retryResponse, asset, partPath, append: false, initialBytes: 0, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteDownloadStreamAsync(
        HttpResponseMessage response,
        ComponentReleaseAsset asset,
        string partPath,
        bool append,
        long initialBytes,
        IProgress<InstallerProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new ReleaseDownloadException(
                $"Download of '{asset.ComponentName}' failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        var totalBytes = response.Content.Headers.ContentLength.HasValue
            ? initialBytes + response.Content.Headers.ContentLength.Value
            : asset.FileSizeBytes;

        var mode = append ? FileMode.Append : FileMode.Create;
        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = new FileStream(
            partPath,
            mode,
            FileAccess.Write,
            FileShare.None,
            InstallerConstants.DownloadBufferSizeBytes,
            useAsync: true);

        var buffer = new byte[InstallerConstants.DownloadBufferSizeBytes];
        long totalWritten = initialBytes;
        int read;
        while ((read = await sourceStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            totalWritten += read;
            if (totalBytes > 0)
            {
                var pct = Math.Min(
                    InstallerConstants.DownloadMaxProgressPercent,
                    (double)totalWritten / totalBytes * InstallerConstants.DownloadMaxProgressPercent);
                Report(progress, StepRelease, $"Downloading {asset.ComponentName} ({totalWritten / 1024} KB)...", InstallerLogLevel.Info, pct);
            }
        }
    }

    private async Task<string> ExtractArchiveAsync(
        string archivePath,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingDirectory);
        var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(stagingDirectory));
        var trimmedRoot = Path.TrimEndingDirectorySeparator(normalizedRoot);

        if (archivePath.EndsWith(InstallerConstants.ZipExtension, StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                var destinationPath = ResolveSafeArchiveDestination(stagingDirectory, normalizedRoot, entry.FullName);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                if (string.Equals(Path.TrimEndingDirectorySeparator(destinationPath), trimmedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parentDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                entry.ExtractToFile(destinationPath, overwrite: true);
            }
        }
        else if (archivePath.EndsWith(InstallerConstants.TarGzExtension, StringComparison.OrdinalIgnoreCase))
        {
            await using var fileStream = File.OpenRead(archivePath);
            await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            await using var tarReader = new TarReader(gzipStream);

            while (await tarReader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
            {
                if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
                {
                    continue;
                }

                var destinationPath = ResolveSafeArchiveDestination(stagingDirectory, normalizedRoot, entry.Name);
                if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                {
                    if (string.Equals(Path.TrimEndingDirectorySeparator(destinationPath), trimmedRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var parentDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrWhiteSpace(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    await entry.ExtractToFileAsync(destinationPath, overwrite: true, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        else
        {
            throw new BundleValidationException($"Unsupported archive format: '{archivePath}'. Expected .zip or .tar.gz.");
        }

        // Unwrap single top-level directory (e.g., OllamaRouter-{rid}/ inside .tar.gz archives).
        var rootFiles = Directory.GetFiles(stagingDirectory);
        var rootDirs = Directory.GetDirectories(stagingDirectory);
        if (rootFiles.Length == 0 && rootDirs.Length == 1)
        {
            return rootDirs[0];
        }

        return stagingDirectory;
    }

    private async Task ExecuteAtomicSwapAsync(
        string installDir,
        string payloadRoot,
        HashSet<string> preservedItemNames,
        IProgress<InstallerProgressEvent>? progress,
        Func<Task> postConfigureAsync,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(installDir);
        var normalizedInstallDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
        var backupDir = Path.Combine(normalizedInstallDir, $"{InstallerConstants.BackupDirectoryPrefix}{Guid.NewGuid():N}");
        var backupDirName = Path.GetFileName(backupDir);
        var currentProcessFullPath = NormalizeOptionalPath(_processControl.CurrentProcessPath);

        Directory.CreateDirectory(backupDir);
        var movedEntries = new List<string>();

        try
        {
            Report(progress, StepSwap, $"Backing up existing binaries in '{normalizedInstallDir}'...", InstallerLogLevel.Info, 65);
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(normalizedInstallDir))
            {
                var entryName = Path.GetFileName(entryPath);
                if (string.Equals(entryName, backupDirName, StringComparison.OrdinalIgnoreCase) ||
                    entryName.StartsWith(InstallerConstants.BackupDirectoryPrefix, StringComparison.OrdinalIgnoreCase) ||
                    preservedItemNames.Contains(entryName))
                {
                    continue;
                }

                if (currentProcessFullPath is not null &&
                    string.Equals(Path.GetFullPath(entryPath), currentProcessFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var backupTarget = Path.Combine(backupDir, entryName);
                if (Directory.Exists(entryPath))
                {
                    Directory.Move(entryPath, backupTarget);
                }
                else
                {
                    File.Move(entryPath, backupTarget, overwrite: true);
                }

                movedEntries.Add(entryName);
            }

            Report(progress, StepSwap, $"Copying new payload into '{normalizedInstallDir}'...", InstallerLogLevel.Info, 75);
            CopyDirectoryContents(payloadRoot, normalizedInstallDir, preservedItemNames, currentProcessFullPath);

            cancellationToken.ThrowIfCancellationRequested();
            await postConfigureAsync().ConfigureAwait(false);

            DeleteDirectoryBestEffort(backupDir);
        }
        catch (Exception primaryEx)
        {
            Report(progress, StepSwap, "Deployment failed; restoring previous installation from .backup...", InstallerLogLevel.Warning, null);
            var rollbackEx = RestoreFromBackup(normalizedInstallDir, backupDir, movedEntries, preservedItemNames, currentProcessFullPath);
            if (rollbackEx is not null)
            {
                throw new AggregateException("Deployment failed and rollback encountered errors.", primaryEx, rollbackEx);
            }

            throw;
        }
    }

    private async Task<bool> ConfigureNightCodeForgeDeploymentAsync(
        string installDir,
        string version,
        string preservedUrls,
        bool createDesktopShortcut,
        string? detectedOllamaRouterDir,
        IProgress<InstallerProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, StepConfigure, "Configuring appsettings, PATH, and OS registration...", InstallerLogLevel.Info, 85);

        var configDir = Path.Combine(installDir, InstallerConstants.ConfigDirectoryName);
        var dataDir = Path.Combine(installDir, InstallerConstants.DataDirectoryName);
        var logsDir = Path.Combine(installDir, InstallerConstants.LogsDirectoryName);
        var binDir = Path.Combine(installDir, InstallerConstants.BinDirectoryName);
        var skillsDir = Path.Combine(installDir, InstallerConstants.SkillsDirectoryName);

        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(logsDir);
        Directory.CreateDirectory(binDir);
        Directory.CreateDirectory(skillsDir);

        var dbPath = Path.Combine(dataDir, InstallerConstants.MainDatabaseFileName).Replace('\\', '/');
        var hangfireDbPath = Path.Combine(dataDir, InstallerConstants.HangfireDatabaseFileName).Replace('\\', '/');
        var logsPath = logsDir.Replace('\\', '/');

        foreach (var fileName in new[] { InstallerConstants.AppSettingsFileName, InstallerConstants.AppSettingsProductionFileName })
        {
            var filePath = Path.Combine(installDir, fileName);
            await UpdateNightCodeForgeAppSettingsAsync(filePath, dbPath, hangfireDbPath, logsPath, preservedUrls, cancellationToken)
                .ConfigureAwait(false);
        }

        if (_platform.Platform != HostPlatformType.Windows)
        {
            foreach (var exeName in InstallerConstants.NightCodeForgeUnixExecutables)
            {
                _platform.SetUnixExecutablePermissions(Path.Combine(installDir, exeName));
            }

            _platform.SetUnixExecutablePermissions(Path.Combine(binDir, InstallerConstants.LauncherShortUnix));
            _platform.SetUnixExecutablePermissions(Path.Combine(binDir, InstallerConstants.LauncherLongUnix));
        }

        await File.WriteAllTextAsync(
            Path.Combine(installDir, InstallerConstants.VersionFileName),
            version + "\n",
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);

        var selfCopied = CopySelfToInstallDirectory(installDir);

        _osIntegration.AddDirectoryToUserPath(binDir, InstallerConstants.PathBlockLabelNightCodeForge);
        await _osIntegration.RegisterNightCodeForgeInstallationAsync(installDir, version, createDesktopShortcut, cancellationToken)
            .ConfigureAwait(false);

        var manifest = new UninstallManifest(
            Version: version,
            InstallDirectory: installDir,
            InstalledAtUtc: DateTimeOffset.UtcNow,
            OllamaRouterDirectory: detectedOllamaRouterDir,
            Platform: _platform.Platform.ToString());

        await using var manifestStream = File.Create(Path.Combine(installDir, InstallerConstants.UninstallManifestFileName));
        await JsonSerializer.SerializeAsync(manifestStream, manifest, InstallerJsonContext.Default.UninstallManifest, cancellationToken)
            .ConfigureAwait(false);

        return selfCopied;
    }

    private async Task ConfigureOllamaRouterDeploymentAsync(
        string installDir,
        string version,
        IProgress<InstallerProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, StepConfigure, "Configuring OllamaRouter state/logs, PATH, and ollamarouter:// URI handler...", InstallerLogLevel.Info, 85);

        var configDir = Path.Combine(installDir, InstallerConstants.ConfigDirectoryName);
        var stateDir = Path.Combine(installDir, InstallerConstants.StateDirectoryName);
        var logsDir = Path.Combine(installDir, InstallerConstants.LogsDirectoryName);
        var binDir = Path.Combine(installDir, InstallerConstants.BinDirectoryName);

        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(logsDir);
        Directory.CreateDirectory(binDir);

        var prodConfigPath = Path.Combine(installDir, InstallerConstants.AppSettingsProductionFileName);
        await UpdateOllamaRouterAppSettingsAsync(
            prodConfigPath,
            stateDir.Replace('\\', '/'),
            logsDir.Replace('\\', '/'),
            cancellationToken).ConfigureAwait(false);

        if (_platform.Platform != HostPlatformType.Windows)
        {
            foreach (var exeName in InstallerConstants.OllamaRouterUnixExecutables)
            {
                _platform.SetUnixExecutablePermissions(Path.Combine(installDir, exeName));
            }

            _platform.SetUnixExecutablePermissions(Path.Combine(binDir, InstallerConstants.OllamaRouterCliUnix));
            _platform.SetUnixExecutablePermissions(Path.Combine(binDir, InstallerConstants.OllamaRouterAppUnix));
        }

        await File.WriteAllTextAsync(
            Path.Combine(installDir, InstallerConstants.VersionFileName),
            version + "\n",
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);

        _osIntegration.AddDirectoryToUserPath(binDir, InstallerConstants.PathBlockLabelOllamaRouter);

        var appExeName = _platform.Platform == HostPlatformType.Windows
            ? InstallerConstants.OllamaRouterAppWindows
            : InstallerConstants.OllamaRouterAppUnix;
        var appExePath = Path.Combine(installDir, appExeName);
        await _osIntegration.RegisterOllamaRouterProtocolAsync(appExePath, cancellationToken).ConfigureAwait(false);
    }

    private bool CopySelfToInstallDirectory(string installDir)
    {
        var currentExe = _processControl.CurrentProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
        {
            return false;
        }

        var targetName = _platform.Platform == HostPlatformType.Windows
            ? InstallerConstants.InstallerExeWindows
            : InstallerConstants.InstallerExeUnix;
        var targetPath = Path.Combine(installDir, targetName);

        if (string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        File.Copy(currentExe, targetPath, overwrite: true);
        if (_platform.Platform != HostPlatformType.Windows)
        {
            _platform.SetUnixExecutablePermissions(targetPath);
        }

        return true;
    }

    private async Task StopNightCodeForgeProcessesAsync(CancellationToken cancellationToken)
    {
        // Stop watchdogs first so they do not immediately respawn NightCodeForge.Web.
        var stoppedWatchdogs = await _processControl.StopProcessesAsync(
            InstallerConstants.NightCodeForgeLauncherProcesses,
            TimeSpan.FromSeconds(InstallerConstants.ProcessStopTimeoutSeconds),
            cancellationToken).ConfigureAwait(false);

        if (stoppedWatchdogs)
        {
            await Task.Delay(InstallerConstants.LauncherStopDelayMs, cancellationToken).ConfigureAwait(false);
        }

        var stoppedWeb = await _processControl.StopProcessesAsync(
            InstallerConstants.NightCodeForgeWebProcesses,
            TimeSpan.FromSeconds(InstallerConstants.ProcessStopTimeoutSeconds),
            cancellationToken).ConfigureAwait(false);

        if (stoppedWeb)
        {
            await Task.Delay(InstallerConstants.WebStopDelayMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> RestartNightCodeForgeAsync(string installDir, CancellationToken cancellationToken)
    {
        var isWindows = _platform.Platform == HostPlatformType.Windows;
        var launcherName = isWindows ? InstallerConstants.LauncherShortWindows : InstallerConstants.LauncherShortUnix;
        var launcherPath = Path.Combine(installDir, InstallerConstants.BinDirectoryName, launcherName);
        if (!File.Exists(launcherPath))
        {
            return false;
        }

        var result = await _processRunner.RunAsync(
            new ProcessRunRequest
            {
                FileName = launcherPath,
                WorkingDirectory = installDir,
                Detached = true
            },
            cancellationToken).ConfigureAwait(false);

        return result.ExitCode == InstallerConstants.ExitCodeSuccess;
    }

    private async Task<bool> RestartOllamaRouterAsync(string installDir, CancellationToken cancellationToken)
    {
        var cliName = _platform.Platform == HostPlatformType.Windows
            ? InstallerConstants.OllamaRouterCliWindows
            : InstallerConstants.OllamaRouterCliUnix;
        var cliPath = Path.Combine(installDir, cliName);
        if (!File.Exists(cliPath))
        {
            return false;
        }

        var result = await _processRunner.RunAsync(
            new ProcessRunRequest
            {
                FileName = cliPath,
                Arguments = ["start"],
                WorkingDirectory = installDir,
                Detached = true
            },
            cancellationToken).ConfigureAwait(false);

        return result.ExitCode == InstallerConstants.ExitCodeSuccess;
    }

    private static string ReadPreservedWebUrls(string installDir)
    {
        foreach (var fileName in new[] { InstallerConstants.AppSettingsProductionFileName, InstallerConstants.AppSettingsFileName })
        {
            var path = Path.Combine(installDir, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var root = ParseJsonObject(File.ReadAllText(path));
                var urls = root[KeyUrls]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(urls))
                {
                    return urls.Trim();
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
            {
                // Justification: Corrupt or non-string Urls in prior config falls back to default listening URL.
                _ = ex;
            }
        }

        return InstallerConstants.DefaultWebUrls;
    }

    private static async Task UpdateNightCodeForgeAppSettingsAsync(
        string filePath,
        string dbPath,
        string hangfireDbPath,
        string logsPath,
        string urls,
        CancellationToken cancellationToken)
    {
        var root = await LoadOrCreateJsonObjectAsync(filePath, cancellationToken).ConfigureAwait(false);

        var connectionStrings = EnsureChildObject(root, KeyConnectionStrings);
        connectionStrings[KeyDefaultConnection] = $"Data Source={dbPath};";
        connectionStrings[KeyHangfireConnection] = $"Data Source={hangfireDbPath};";

        var logging = EnsureChildObject(root, KeyLogging);
        if (logging[KeyLogLevel] is not JsonObject)
        {
            logging[KeyLogLevel] = new JsonObject
            {
                ["Default"] = "Warning",
                ["NightCodeForge"] = "Information"
            };
        }

        logging[KeyLogsDirectory] = logsPath;
        root[KeyUrls] = urls;

        await WriteJsonObjectAsync(filePath, root, cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateOllamaRouterAppSettingsAsync(
        string filePath,
        string stateDirPath,
        string logsPath,
        CancellationToken cancellationToken)
    {
        var root = await LoadOrCreateJsonObjectAsync(filePath, cancellationToken).ConfigureAwait(false);

        var router = EnsureChildObject(root, KeyOllamaRouter);
        router[KeyStateDirectory] = stateDirPath;

        var logging = EnsureChildObject(root, KeyLogging);
        if (logging[KeyLogLevel] is not JsonObject)
        {
            logging[KeyLogLevel] = new JsonObject
            {
                ["Default"] = "Warning",
                ["OllamaRouter"] = "Information"
            };
        }

        logging[KeyLogsDirectory] = logsPath;

        await WriteJsonObjectAsync(filePath, root, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonObject> LoadOrCreateJsonObjectAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return new JsonObject();
        }

        var raw = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new JsonObject();
        }

        return ParseJsonObject(raw);
    }

    private static JsonObject ParseJsonObject(string json)
    {
        var docOptions = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        return JsonNode.Parse(json, nodeOptions: null, documentOptions: docOptions)?.AsObject()
               ?? new JsonObject();
    }

    private static JsonObject EnsureChildObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static async Task WriteJsonObjectAsync(string filePath, JsonObject root, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(filePath);
        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        root.WriteTo(writer);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> TryReadVersionFromArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        try
        {
            if (archivePath.EndsWith(InstallerConstants.ZipExtension, StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(archivePath);
                ZipArchiveEntry? topDirFallback = null;
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        continue;
                    }

                    var depth = GetVersionEntryDepth(entry.FullName);
                    if (depth == 1)
                    {
                        using var reader = new StreamReader(entry.Open());
                        var text = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text;
                        }
                    }
                    else if (depth == 2 && topDirFallback is null)
                    {
                        topDirFallback = entry;
                    }
                }

                if (topDirFallback is not null)
                {
                    using var reader = new StreamReader(topDirFallback.Open());
                    var text = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();
                    return string.IsNullOrWhiteSpace(text) ? null : text;
                }

                return null;
            }

            if (archivePath.EndsWith(InstallerConstants.TarGzExtension, StringComparison.OrdinalIgnoreCase))
            {
                await using var fileStream = File.OpenRead(archivePath);
                await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
                await using var tarReader = new TarReader(gzipStream);

                string? topDirVersion = null;
                while (await tarReader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                        entry.DataStream is null)
                    {
                        continue;
                    }

                    var depth = GetVersionEntryDepth(entry.Name);
                    if (depth == 1)
                    {
                        using var reader = new StreamReader(entry.DataStream);
                        var text = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text;
                        }
                    }
                    else if (depth == 2 && topDirVersion is null)
                    {
                        using var reader = new StreamReader(entry.DataStream);
                        var text = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            topDirVersion = text;
                        }
                    }
                }

                return topDirVersion;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // Justification: Best-effort version probe from local archive falls back to default version when unreadable.
            _ = ex;
        }

        return null;
    }

    private static int GetVersionEntryDepth(string entryName)
    {
        var normalized = entryName.Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        normalized = normalized.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return 0;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is 1 or 2 &&
            string.Equals(segments[^1], InstallerConstants.VersionFileName, StringComparison.OrdinalIgnoreCase))
        {
            return segments.Length;
        }

        return 0;
    }

    private static string ResolveSafeArchiveDestination(string stagingDir, string normalizedRootWithSep, string entryName)
    {
        var normalizedEntry = entryName.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalizedEntry) ||
            normalizedEntry.StartsWith('/') ||
            (normalizedEntry.Length >= 2 && char.IsLetter(normalizedEntry[0]) && normalizedEntry[1] == ':'))
        {
            throw new BundleValidationException($"Archive entry '{entryName}' uses a disallowed rooted path.");
        }

        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullDestination = Path.GetFullPath(Path.Combine(stagingDir, normalizedEntry));
        if (!fullDestination.StartsWith(normalizedRootWithSep, pathComparison) &&
            !string.Equals(
                Path.TrimEndingDirectorySeparator(fullDestination),
                Path.TrimEndingDirectorySeparator(normalizedRootWithSep),
                pathComparison))
        {
            throw new BundleValidationException($"Archive entry '{entryName}' resolves outside extraction root.");
        }

        return fullDestination;
    }

    private static void CopyDirectoryContents(
        string sourceDir,
        string destinationDir,
        HashSet<string> preservedTopLevelNames,
        string? skipFileFullPath)
    {
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(sourceDir))
        {
            var name = Path.GetFileName(entryPath);
            if (preservedTopLevelNames.Contains(name))
            {
                continue;
            }

            var targetPath = Path.Combine(destinationDir, name);
            if (Directory.Exists(entryPath))
            {
                CopyDirectoryRecursive(entryPath, targetPath);
            }
            else
            {
                if (skipFileFullPath is not null &&
                    string.Equals(Path.GetFullPath(targetPath), skipFileFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Copy(entryPath, targetPath, overwrite: true);
            }
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectoryRecursive(subDir, Path.Combine(destinationDir, Path.GetFileName(subDir)));
        }
    }

    private static Exception? RestoreFromBackup(
        string installDir,
        string backupDir,
        IReadOnlyList<string> movedEntries,
        HashSet<string> preservedNames,
        string? currentProcessFullPath)
    {
        var backupDirName = Path.GetFileName(backupDir);
        var rollbackErrors = new List<Exception>();

        foreach (var entryPath in Directory.EnumerateFileSystemEntries(installDir))
        {
            var entryName = Path.GetFileName(entryPath);
            if (string.Equals(entryName, backupDirName, StringComparison.OrdinalIgnoreCase) ||
                entryName.StartsWith(InstallerConstants.BackupDirectoryPrefix, StringComparison.OrdinalIgnoreCase) ||
                preservedNames.Contains(entryName))
            {
                continue;
            }

            if (currentProcessFullPath is not null &&
                string.Equals(Path.GetFullPath(entryPath), currentProcessFullPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Directory.Exists(entryPath))
            {
                DeleteDirectoryBestEffort(entryPath);
            }
            else
            {
                try
                {
                    DeleteFileIfExists(entryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(ex);
                }
            }
        }

        foreach (var entryName in movedEntries)
        {
            var backupItem = Path.Combine(backupDir, entryName);
            var originalItem = Path.Combine(installDir, entryName);
            try
            {
                if (Directory.Exists(backupItem))
                {
                    if (Directory.Exists(originalItem))
                    {
                        CopyDirectoryRecursive(backupItem, originalItem);
                        DeleteDirectoryBestEffort(backupItem);
                    }
                    else
                    {
                        Directory.Move(backupItem, originalItem);
                    }
                }
                else if (File.Exists(backupItem))
                {
                    File.Move(backupItem, originalItem, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                rollbackErrors.Add(ex);
            }
        }

        DeleteDirectoryBestEffort(backupDir);

        return rollbackErrors.Count switch
        {
            0 => null,
            1 => rollbackErrors[0],
            _ => new AggregateException(rollbackErrors)
        };
    }

    private static string SanitizeFileNameSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return InstallerConstants.DefaultFallbackVersion;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = segment.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is '/' or '\\' || Array.IndexOf(invalidChars, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Justification: Best-effort staging/backup cleanup must not mask primary deployment result.
            _ = ex;
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    private static void Report(
        IProgress<InstallerProgressEvent>? progress,
        string step,
        string message,
        InstallerLogLevel level,
        double? percent)
    {
        progress?.Report(new InstallerProgressEvent(step, message, level, percent));
    }
}
