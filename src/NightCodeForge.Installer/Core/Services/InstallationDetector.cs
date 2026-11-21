namespace NightCodeForge.Installer.Core.Services;

using System.Diagnostics;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Probes standard platform installation directories, environment overrides, registry keys,
/// legacy <c>publish.ps1</c> installations, and air-gapped bundle folders.
/// </summary>
public sealed class InstallationDetector : IInstallationDetector
{
    private readonly IPlatformEnvironment _platform;
    private readonly IWindowsRegistryAccessor _registry;

    public InstallationDetector(
        IPlatformEnvironment platform,
        IWindowsRegistryAccessor registry)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public string GetDefaultNightCodeForgeInstallDirectory()
    {
        return _platform.Platform switch
        {
            HostPlatformType.Windows => Path.Combine(_platform.LocalApplicationDataDirectory, InstallerConstants.ApplicationDisplayName),
            HostPlatformType.MacOS => Path.Combine(_platform.UserHomeDirectory, "Applications", InstallerConstants.ApplicationDisplayName),
            _ => Path.Combine(_platform.UserHomeDirectory, InstallerConstants.UnixNightCodeForgeDirName)
        };
    }

    public string GetDefaultOllamaRouterInstallDirectory()
    {
        return _platform.Platform switch
        {
            HostPlatformType.Windows => Path.Combine(_platform.LocalApplicationDataDirectory, InstallerConstants.ComponentOllamaRouter),
            _ => Path.Combine(_platform.UserHomeDirectory, InstallerConstants.UnixOllamaRouterDirName)
        };
    }

    public InstallationDiscoveryResult Detect(
        string? explicitBundleDirectory = null,
        string? explicitInstallDirectory = null)
    {
        var registryInstallDir = _platform.Platform == HostPlatformType.Windows
            ? _registry.ReadCurrentUserStringValue(
                InstallerConstants.WindowsUninstallRegistrySubKey,
                InstallerConstants.WindowsUninstallInstallLocationValueName)
            : null;

        var ncfInfo = DetectNightCodeForge(registryInstallDir, explicitInstallDirectory);
        var routerInfo = DetectOllamaRouter();
        var (bundleDir, bundleFiles) = DetectAirGappedBundle(explicitBundleDirectory);
        var recommendedSource = bundleDir is not null
            ? InstallSourceMode.AirGappedBundle
            : InstallSourceMode.Online;

        return new InstallationDiscoveryResult(
            NightCodeForge: ncfInfo,
            OllamaRouter: routerInfo,
            RecommendedSourceMode: recommendedSource,
            DetectedBundleDirectory: bundleDir,
            DetectedBundleFiles: bundleFiles);
    }

    private InstalledComponentInfo DetectNightCodeForge(string? registryInstallDir, string? explicitInstallDirectory)
    {
        var normalizedExplicitDir = NormalizeOptionalDirectory(explicitInstallDirectory);
        var normalizedRegistryDir = NormalizeOptionalDirectory(registryInstallDir);
        var pathComparer = GetPathComparer();
        var candidates = BuildNightCodeForgeProbeCandidates(normalizedExplicitDir, normalizedRegistryDir, pathComparer);

        foreach (var candidate in candidates)
        {
            var binaryPath = FindNightCodeForgeBinary(candidate);
            if (binaryPath is null)
            {
                continue;
            }

            var version = ReadVersion(candidate, binaryPath);
            var hasManifest = File.Exists(Path.Combine(candidate, InstallerConstants.UninstallManifestFileName));
            var hasRegistryMatch = _platform.Platform == HostPlatformType.Windows &&
                                   normalizedRegistryDir is not null &&
                                   pathComparer.Equals(candidate, normalizedRegistryDir);
            var isLegacy = !hasManifest && !hasRegistryMatch;

            return new InstalledComponentInfo(
                IsInstalled: true,
                InstallDirectory: candidate,
                Version: version,
                IsLegacyPublishInstall: isLegacy,
                HasConfigDirectory: Directory.Exists(Path.Combine(candidate, InstallerConstants.ConfigDirectoryName)),
                HasDataOrStateDirectory: Directory.Exists(Path.Combine(candidate, InstallerConstants.DataDirectoryName)),
                HasLogsDirectory: Directory.Exists(Path.Combine(candidate, InstallerConstants.LogsDirectoryName)),
                PrimaryBinaryPath: binaryPath);
        }

        var defaultDir = normalizedExplicitDir
                         ?? NormalizeOptionalDirectory(_platform.GetEnvironmentVariable(InstallerConstants.EnvNightCodeForgeHome))
                         ?? GetDefaultNightCodeForgeInstallDirectory();
        return new InstalledComponentInfo(
            IsInstalled: false,
            InstallDirectory: defaultDir,
            Version: null,
            IsLegacyPublishInstall: false,
            HasConfigDirectory: Directory.Exists(Path.Combine(defaultDir, InstallerConstants.ConfigDirectoryName)),
            HasDataOrStateDirectory: Directory.Exists(Path.Combine(defaultDir, InstallerConstants.DataDirectoryName)),
            HasLogsDirectory: Directory.Exists(Path.Combine(defaultDir, InstallerConstants.LogsDirectoryName)),
            PrimaryBinaryPath: null);
    }

    private InstalledComponentInfo DetectOllamaRouter()
    {
        var candidates = BuildOllamaRouterProbeCandidates(GetPathComparer());
        foreach (var candidate in candidates)
        {
            var binaryPath = FindOllamaRouterBinary(candidate);
            if (binaryPath is null)
            {
                continue;
            }

            var version = ReadVersion(candidate, binaryPath);
            return new InstalledComponentInfo(
                IsInstalled: true,
                InstallDirectory: candidate,
                Version: version,
                IsLegacyPublishInstall: false,
                HasConfigDirectory: Directory.Exists(Path.Combine(candidate, InstallerConstants.ConfigDirectoryName)),
                HasDataOrStateDirectory: Directory.Exists(Path.Combine(candidate, InstallerConstants.StateDirectoryName)),
                HasLogsDirectory: Directory.Exists(Path.Combine(candidate, InstallerConstants.LogsDirectoryName)),
                PrimaryBinaryPath: binaryPath);
        }

        var defaultDir = NormalizeOptionalDirectory(_platform.GetEnvironmentVariable(InstallerConstants.EnvOllamaRouterHome))
                         ?? GetDefaultOllamaRouterInstallDirectory();
        return new InstalledComponentInfo(
            IsInstalled: false,
            InstallDirectory: defaultDir,
            Version: null,
            IsLegacyPublishInstall: false,
            HasConfigDirectory: Directory.Exists(Path.Combine(defaultDir, InstallerConstants.ConfigDirectoryName)),
            HasDataOrStateDirectory: Directory.Exists(Path.Combine(defaultDir, InstallerConstants.StateDirectoryName)),
            HasLogsDirectory: Directory.Exists(Path.Combine(defaultDir, InstallerConstants.LogsDirectoryName)),
            PrimaryBinaryPath: null);
    }

    private List<string> BuildNightCodeForgeProbeCandidates(
        string? explicitInstallDir,
        string? registryInstallDir,
        StringComparer pathComparer)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, explicitInstallDir, pathComparer);
        AddCandidate(candidates, _platform.GetEnvironmentVariable(InstallerConstants.EnvNightCodeForgeHome), pathComparer);

        switch (_platform.Platform)
        {
            case HostPlatformType.Windows:
                AddCandidate(candidates, registryInstallDir, pathComparer);
                AddCandidate(candidates, Path.Combine(_platform.LocalApplicationDataDirectory, InstallerConstants.ApplicationDisplayName), pathComparer);
                AddCandidate(candidates, Path.Combine(_platform.LocalApplicationDataDirectory, "Programs", InstallerConstants.ApplicationDisplayName), pathComparer);
                break;

            case HostPlatformType.Linux:
                AddCandidate(candidates, Path.Combine(_platform.UserHomeDirectory, InstallerConstants.UnixNightCodeForgeDirName), pathComparer);
                AddCandidate(candidates, Path.Combine(_platform.UserHomeDirectory, ".local", "share", "nightcodeforge"), pathComparer);
                break;

            case HostPlatformType.MacOS:
                AddCandidate(candidates, Path.Combine(_platform.UserHomeDirectory, InstallerConstants.UnixNightCodeForgeDirName), pathComparer);
                AddCandidate(candidates, Path.Combine(_platform.UserHomeDirectory, "Applications", InstallerConstants.ApplicationDisplayName), pathComparer);
                break;
        }

        return candidates;
    }

    private List<string> BuildOllamaRouterProbeCandidates(StringComparer pathComparer)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, _platform.GetEnvironmentVariable(InstallerConstants.EnvOllamaRouterHome), pathComparer);
        AddCandidate(candidates, GetDefaultOllamaRouterInstallDirectory(), pathComparer);
        return candidates;
    }

    private (string? BundleDirectory, IReadOnlyList<string> BundleFiles) DetectAirGappedBundle(string? explicitBundleDirectory)
    {
        var pathComparer = GetPathComparer();
        var fileComparer = _platform.Platform == HostPlatformType.Windows
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var candidates = new List<(string Path, bool RequireCompanionAssets)>();
        var normalizedExplicit = NormalizeOptionalDirectory(explicitBundleDirectory);
        if (normalizedExplicit is not null)
        {
            candidates.Add((normalizedExplicit, false));
        }
        else
        {
            var baseBundleSubdir = NormalizeOptionalDirectory(
                Path.Combine(_platform.InstallerBaseDirectory, InstallerConstants.BundleDirectoryName));
            if (baseBundleSubdir is not null)
            {
                candidates.Add((baseBundleSubdir, false));
            }

            var baseDir = NormalizeOptionalDirectory(_platform.InstallerBaseDirectory);
            if (baseDir is not null && !pathComparer.Equals(baseDir, baseBundleSubdir))
            {
                candidates.Add((baseDir, true));
            }
        }

        var expectedNcfArchive = ReleaseManager.GetNightCodeForgeArchiveName(_platform.CurrentRuntimeIdentifier);
        var expectedRouterArchive = ReleaseManager.GetOllamaRouterArchiveName(_platform.Platform, _platform.CurrentRuntimeIdentifier);

        foreach (var (candidate, requireCompanionAssets) in candidates)
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            List<string> matchingFiles;
            try
            {
                matchingFiles = Directory.EnumerateFiles(candidate)
                    .Select(Path.GetFileName)
                    .Where(static name => !string.IsNullOrEmpty(name) && IsBundleAssetFile(name!))
                    .Select(static name => name!)
                    .OrderBy(static name => name, fileComparer)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Justification: Unreadable candidate folder cannot serve as an air-gapped bundle source.
                _ = ex;
                continue;
            }

            if (!matchingFiles.Contains(expectedNcfArchive, fileComparer))
            {
                continue;
            }

            if (requireCompanionAssets)
            {
                var hasRouter = matchingFiles.Contains(expectedRouterArchive, fileComparer);
                var hasNupkg = matchingFiles.Any(static f =>
                    f.EndsWith(InstallerConstants.NupkgExtension, StringComparison.OrdinalIgnoreCase));
                if (!hasRouter && !hasNupkg)
                {
                    continue;
                }
            }

            return (candidate, matchingFiles);
        }

        return (null, []);
    }

    internal static bool IsBundleAssetFile(string fileName)
    {
        if (fileName.StartsWith(InstallerConstants.ComponentNightCodeForgeWeb, StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(InstallerConstants.ZipExtension, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fileName.StartsWith(InstallerConstants.ComponentOllamaRouter, StringComparison.OrdinalIgnoreCase) &&
            (fileName.EndsWith(InstallerConstants.ZipExtension, StringComparison.OrdinalIgnoreCase) ||
             fileName.EndsWith(InstallerConstants.TarGzExtension, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (fileName.EndsWith(InstallerConstants.NupkgExtension, StringComparison.OrdinalIgnoreCase) &&
            CompanionToolCatalog.All.Any(spec =>
                fileName.StartsWith(spec.PackageId + ".", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    internal static string? FindNightCodeForgeBinary(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        string[] binaries =
        [
            InstallerConstants.WebExeWindows,
            InstallerConstants.WebExeUnix,
            InstallerConstants.WebDll
        ];

        foreach (var binary in binaries)
        {
            var fullPath = Path.Combine(directory, binary);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    internal static string? FindOllamaRouterBinary(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        string[] binaries =
        [
            InstallerConstants.OllamaRouterServerWindows,
            InstallerConstants.OllamaRouterServerUnix
        ];

        foreach (var binary in binaries)
        {
            var fullPath = Path.Combine(directory, binary);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    private StringComparer GetPathComparer() =>
        _platform.Platform == HostPlatformType.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string? ReadVersion(string installDirectory, string primaryBinaryPath)
    {
        var versionFilePath = Path.Combine(installDirectory, InstallerConstants.VersionFileName);
        if (File.Exists(versionFilePath))
        {
            try
            {
                var raw = File.ReadAllText(versionFilePath).Trim();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return raw;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Justification: Best-effort VERSION file read falls back to binary metadata inspection.
                _ = ex;
            }
        }

        var metadataVersion = TryReadFileVersion(primaryBinaryPath);
        if (metadataVersion is not null)
        {
            return metadataVersion;
        }

        // On Linux/macOS, the native apphost binary does not carry Win32 PE version resources; probe NightCodeForge.Web.dll.
        var webDllPath = Path.Combine(installDirectory, InstallerConstants.WebDll);
        if (!string.Equals(primaryBinaryPath, webDllPath, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(webDllPath))
        {
            return TryReadFileVersion(webDllPath);
        }

        return null;
    }

    private static string? TryReadFileVersion(string filePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(filePath);
            if (!string.IsNullOrWhiteSpace(info.ProductVersion))
            {
                return info.ProductVersion.Trim();
            }

            if (!string.IsNullOrWhiteSpace(info.FileVersion))
            {
                return info.FileVersion.Trim();
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            // Justification: Fallback assembly version inspection is best-effort when VERSION file is absent.
            _ = ex;
        }

        return null;
    }

    private static string? NormalizeOptionalDirectory(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawPath.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Justification: Malformed optional environment/CLI candidate path is ignored during probe.
            _ = ex;
            return null;
        }
    }

    private static void AddCandidate(List<string> candidates, string? rawPath, StringComparer pathComparer)
    {
        var normalized = NormalizeOptionalDirectory(rawPath);
        if (normalized is null)
        {
            return;
        }

        if (!candidates.Contains(normalized, pathComparer))
        {
            candidates.Add(normalized);
        }
    }
}
