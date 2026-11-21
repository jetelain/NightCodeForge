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
    private const string UninstallLocationValueName = "InstallLocation";

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

    public InstallationDiscoveryResult Detect(string? explicitBundleDirectory = null)
    {
        var registryInstallDir = _platform.Platform == HostPlatformType.Windows
            ? _registry.ReadCurrentUserStringValue(InstallerConstants.WindowsUninstallRegistrySubKey, UninstallLocationValueName)
            : null;

        var ncfInfo = DetectNightCodeForge(registryInstallDir);
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

    private InstalledComponentInfo DetectNightCodeForge(string? registryInstallDir)
    {
        var normalizedRegistryDir = NormalizeOptionalDirectory(registryInstallDir);
        var candidates = BuildNightCodeForgeProbeCandidates(normalizedRegistryDir);
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
                                   string.Equals(candidate, normalizedRegistryDir, StringComparison.OrdinalIgnoreCase);
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

        var defaultDir = NormalizeOptionalDirectory(_platform.GetEnvironmentVariable(InstallerConstants.EnvNightCodeForgeHome))
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
        var candidates = BuildOllamaRouterProbeCandidates();
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

    private List<string> BuildNightCodeForgeProbeCandidates(string? registryInstallDir)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, _platform.GetEnvironmentVariable(InstallerConstants.EnvNightCodeForgeHome));

        switch (_platform.Platform)
        {
            case HostPlatformType.Windows:
                AddCandidate(candidates, registryInstallDir);
                AddCandidate(candidates, Path.Combine(_platform.LocalApplicationDataDirectory, InstallerConstants.ApplicationDisplayName));
                AddCandidate(candidates, Path.Combine(_platform.LocalApplicationDataDirectory, "Programs", InstallerConstants.ApplicationDisplayName));
                break;

            case HostPlatformType.Linux:
                AddCandidate(candidates, Path.Combine(_platform.UserHomeDirectory, InstallerConstants.UnixNightCodeForgeDirName));
                AddCandidate(candidates, Path.Combine(_platform.UserHomeDirectory, ".local", "share", "nightcodeforge"));
                break;

            case HostPlatformType.MacOS:
                AddCandidate(candidates, Path.Combine(_platform.UserHomeDirectory, InstallerConstants.UnixNightCodeForgeDirName));
                AddCandidate(candidates, Path.Combine(_platform.UserHomeDirectory, "Applications", InstallerConstants.ApplicationDisplayName));
                break;
        }

        return candidates;
    }

    private List<string> BuildOllamaRouterProbeCandidates()
    {
        var candidates = new List<string>();
        AddCandidate(candidates, _platform.GetEnvironmentVariable(InstallerConstants.EnvOllamaRouterHome));
        AddCandidate(candidates, GetDefaultOllamaRouterInstallDirectory());
        return candidates;
    }

    private (string? BundleDirectory, IReadOnlyList<string> BundleFiles) DetectAirGappedBundle(string? explicitBundleDirectory)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, explicitBundleDirectory);
        AddCandidate(candidates, Path.Combine(_platform.InstallerBaseDirectory, InstallerConstants.BundleDirectoryName));
        AddCandidate(candidates, _platform.InstallerBaseDirectory);

        var expectedNcfArchive = ReleaseManager.GetNightCodeForgeArchiveName(_platform.CurrentRuntimeIdentifier);

        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            var matchingFiles = Directory.EnumerateFiles(candidate)
                .Select(Path.GetFileName)
                .Where(static name => !string.IsNullOrEmpty(name) && IsBundleAssetFile(name!))
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (matchingFiles.Contains(expectedNcfArchive, StringComparer.OrdinalIgnoreCase))
            {
                return (candidate, matchingFiles!);
            }
        }

        return (null, []);
    }

    private static bool IsBundleAssetFile(string fileName)
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

    private static string? ReadVersion(string installDirectory, string primaryBinaryPath)
    {
        var versionFilePath = Path.Combine(installDirectory, InstallerConstants.VersionFileName);
        if (File.Exists(versionFilePath))
        {
            var raw = File.ReadAllText(versionFilePath).Trim();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw;
            }
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(primaryBinaryPath);
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

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawPath.Trim()));
    }

    private static void AddCandidate(List<string> candidates, string? rawPath)
    {
        var normalized = NormalizeOptionalDirectory(rawPath);
        if (normalized is null)
        {
            return;
        }

        if (!candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(normalized);
        }
    }
}
