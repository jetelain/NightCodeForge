using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Git;

/// <summary>
/// Scans standard developer workspace directories up to a bounded depth for Git repositories,
/// extracting branch and origin remote metadata via direct file reads without spawning git processes.
/// </summary>
public partial class LocalRepositoryDiscoveryService : ILocalRepositoryDiscoveryService, IDisposable
{
    private sealed record DiscoveryCacheEntry(
        IReadOnlyList<DiscoveredRepository> Repositories,
        DateTimeOffset ExpiresAtUtc);

    private const string GitEntryName = ".git";
    private const string IgnoreMarkerFileName = ".ncf-ignore";
    private const string HeadFileName = "HEAD";
    private const string ConfigFileName = "config";
    private const string IndexFileName = "index";
    private const string CommonDirFileName = "commondir";
    private const string GitDirPrefix = "gitdir:";
    private const string RefPrefix = "ref:";
    private const string HeadsRefPrefix = "refs/heads/";
    private const int DetachedShaDisplayLength = 7;
    private const string WslUsersRootPath = "/mnt/c/Users";

    private static readonly HashSet<string> PrunedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules",
        "bin",
        "obj",
        ".nuget",
        ".local",
        ".vs",
        ".git"
    };

    private static readonly HashSet<string> ExcludedWindowsUserDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Default",
        "Default User",
        "Public",
        "All Users",
        "defaultuser0"
    };

    [GeneratedRegex(@"^\[\s*remote\s+""origin""\s*\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoteOriginSectionRegex();

    private readonly IOptions<LocalDiscoveryOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LocalRepositoryDiscoveryService> _logger;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    private volatile DiscoveryCacheEntry? _cache;

    public LocalRepositoryDiscoveryService(
        IOptions<LocalDiscoveryOptions> options,
        TimeProvider timeProvider,
        ILogger<LocalRepositoryDiscoveryService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Dispose()
    {
        _scanLock.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredRepository>> GetDiscoveredRepositoriesAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var cached = _cache;
        if (!forceRefresh && cached is not null && now < cached.ExpiresAtUtc)
        {
            return cached.Repositories;
        }

        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            cached = _cache;
            if (!forceRefresh && cached is not null && now < cached.ExpiresAtUtc)
            {
                return cached.Repositories;
            }

            var currentOptions = _options.Value;
            var roots = GetConfiguredRoots(currentOptions);
            var maxDepth = Math.Max(0, currentOptions.MaxDepth);

            var discovered = await Task.Run(
                () => ScanRoots(roots, maxDepth, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            var cacheMinutes = currentOptions.CacheDurationMinutes;
            _cache = cacheMinutes > 0
                ? new DiscoveryCacheEntry(discovered, _timeProvider.GetUtcNow().AddMinutes(cacheMinutes))
                : null;

            return discovered;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetStandardRootsAsync(CancellationToken cancellationToken = default)
    {
        var configuredRoots = GetConfiguredRoots(_options.Value);
        return await Task.Run(
            () => configuredRoots.Where(Directory.Exists).ToList(),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public string ResolveDefaultNewRepositoryPath(string repositoryName)
    {
        if (string.IsNullOrWhiteSpace(repositoryName))
        {
            throw new LocalRepositoryDiscoveryException("Repository name cannot be empty.");
        }

        var trimmedName = repositoryName.Trim();
        if (trimmedName is "." or ".." ||
            trimmedName.Contains('/') ||
            trimmedName.Contains('\\') ||
            trimmedName.Contains(Path.DirectorySeparatorChar) ||
            trimmedName.Contains(Path.AltDirectorySeparatorChar) ||
            trimmedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new LocalRepositoryDiscoveryException($"Repository name '{repositoryName}' contains invalid path characters.");
        }

        var configuredRoots = GetConfiguredRoots(_options.Value);
        var selectedRoot = configuredRoots.FirstOrDefault(Directory.Exists)
            ?? configuredRoots.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(selectedRoot))
        {
            throw new LocalRepositoryDiscoveryException("Unable to determine a default local repository root directory.");
        }

        return Path.Combine(selectedRoot, trimmedName);
    }

    internal static IReadOnlyList<string> BuildStandardRoots(
        bool isWindows,
        bool isWsl,
        string? userProfile,
        string? homeDirectory,
        string? wslUsersRoot,
        IEnumerable<string>? additionalPaths)
    {
        var candidates = new List<string>();

        if (isWindows)
        {
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                candidates.Add(Path.Combine(userProfile, "source", "repos"));
                candidates.Add(Path.Combine(userProfile, "repos"));
                candidates.Add(Path.Combine(userProfile, "projects"));
                candidates.Add(Path.Combine(userProfile, "Development"));
                candidates.Add(Path.Combine(userProfile, "workspace"));
                candidates.Add(Path.Combine(userProfile, "Documents", "GitHub"));
            }
        }
        else
        {
            var effectiveHome = !string.IsNullOrWhiteSpace(homeDirectory) ? homeDirectory : userProfile;
            if (!string.IsNullOrWhiteSpace(effectiveHome))
            {
                candidates.Add(Path.Combine(effectiveHome, "source", "repos"));
                candidates.Add(Path.Combine(effectiveHome, "repos"));
                candidates.Add(Path.Combine(effectiveHome, "projects"));
                candidates.Add(Path.Combine(effectiveHome, "Development"));
                candidates.Add(Path.Combine(effectiveHome, "workspace"));
                candidates.Add(Path.Combine(effectiveHome, "code"));
            }

            if (isWsl && !string.IsNullOrWhiteSpace(wslUsersRoot) && Directory.Exists(wslUsersRoot))
            {
                try
                {
                    foreach (var userDir in Directory.EnumerateDirectories(wslUsersRoot))
                    {
                        var folderName = Path.GetFileName(userDir);
                        if (string.IsNullOrWhiteSpace(folderName) ||
                            folderName.StartsWith('.') ||
                            ExcludedWindowsUserDirectories.Contains(folderName))
                        {
                            continue;
                        }

                        candidates.Add(Path.Combine(userDir, "source", "repos"));
                        candidates.Add(Path.Combine(userDir, "repos"));
                        candidates.Add(Path.Combine(userDir, "projects"));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    // Skip unreadable WSL Windows Users directory
                }
            }
        }

        if (additionalPaths is not null)
        {
            var effectiveHome = !string.IsNullOrWhiteSpace(homeDirectory) ? homeDirectory : userProfile;
            foreach (var rawPath in additionalPaths)
            {
                var expanded = ExpandPath(rawPath, effectiveHome);
                if (!string.IsNullOrWhiteSpace(expanded))
                {
                    candidates.Add(expanded);
                }
            }
        }

        var comparer = isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var uniqueRoots = new List<string>();

        foreach (var candidate in candidates)
        {
            var normalized = NormalizePathForComparison(candidate);
            if (normalized is not null && seen.Add(normalized))
            {
                uniqueRoots.Add(normalized);
            }
        }

        return uniqueRoots;
    }

    private IReadOnlyList<string> GetConfiguredRoots(LocalDiscoveryOptions currentOptions)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var homeDirectory = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            homeDirectory = userProfile;
        }

        var isWsl = !isWindows && (
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_INTEROP")) ||
            Directory.Exists(WslUsersRootPath));

        return BuildStandardRoots(
            isWindows,
            isWsl,
            userProfile,
            homeDirectory,
            WslUsersRootPath,
            currentOptions.AdditionalScanPaths);
    }

    private IReadOnlyList<DiscoveredRepository> ScanRoots(
        IReadOnlyList<string> roots,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        var pathComparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var seenPaths = new HashSet<string>(pathComparer);
        var repositories = new List<DiscoveredRepository>();

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(root))
            {
                continue;
            }

            ScanDirectoryRecursive(root, 0, maxDepth, seenPaths, repositories, cancellationToken);
        }

        return repositories
            .OrderByDescending(r => r.LastModified)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ScanDirectoryRecursive(
        string directoryPath,
        int depth,
        int maxDepth,
        HashSet<string> seenPaths,
        List<DiscoveredRepository> results,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dirName = Path.GetFileName(directoryPath);
        if (depth > 0 && !string.IsNullOrEmpty(dirName) && PrunedDirectoryNames.Contains(dirName))
        {
            return;
        }

        try
        {
            if (File.Exists(Path.Combine(directoryPath, IgnoreMarkerFileName)))
            {
                return;
            }

            var dotGitPath = Path.Combine(directoryPath, GitEntryName);
            if (Directory.Exists(dotGitPath) || File.Exists(dotGitPath))
            {
                var normalizedPath = NormalizePathForComparison(directoryPath) ?? directoryPath;
                if (seenPaths.Add(normalizedPath))
                {
                    var repo = ExtractRepositoryMetadata(normalizedPath, dotGitPath);
                    results.Add(repo);
                }

                // Stop traversing deeper inside a detected Git repository
                return;
            }

            if (depth >= maxDepth)
            {
                return;
            }

            foreach (var subDir in Directory.EnumerateDirectories(directoryPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScanDirectoryRecursive(subDir, depth + 1, maxDepth, seenPaths, results, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogWarning(ex, "Skipping inaccessible directory '{DirectoryPath}' during repository discovery.", directoryPath);
        }
    }

    private DiscoveredRepository ExtractRepositoryMetadata(string repoDir, string dotGitPath)
    {
        var repoName = new DirectoryInfo(repoDir).Name;
        var resolvedGitDir = ResolveGitDirectory(repoDir, dotGitPath);
        var commonDir = resolvedGitDir is not null ? ResolveCommonDirectory(resolvedGitDir) : null;

        var currentBranch = resolvedGitDir is not null ? ReadHeadBranch(resolvedGitDir) : null;
        var remoteOriginUrl = ReadRemoteOriginUrl(commonDir, resolvedGitDir);
        var lastModified = ComputeLastModifiedUtc(repoDir, resolvedGitDir);

        return new DiscoveredRepository(
            Name: repoName,
            FullPath: repoDir,
            CurrentBranch: currentBranch,
            LastModified: lastModified,
            HasRemoteOrigin: !string.IsNullOrWhiteSpace(remoteOriginUrl),
            RemoteOriginUrl: remoteOriginUrl);
    }

    private string? ResolveGitDirectory(string repoDir, string dotGitPath)
    {
        if (Directory.Exists(dotGitPath))
        {
            return dotGitPath;
        }

        if (!File.Exists(dotGitPath))
        {
            return null;
        }

        try
        {
            foreach (var line in File.ReadLines(dotGitPath))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith(GitDirPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rawTarget = trimmed[GitDirPrefix.Length..].Trim();
                if (string.IsNullOrWhiteSpace(rawTarget))
                {
                    return null;
                }

                return Path.IsPathRooted(rawTarget)
                    ? Path.GetFullPath(rawTarget)
                    : Path.GetFullPath(Path.Combine(repoDir, rawTarget));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogWarning(ex, "Failed to read .git pointer file at '{DotGitPath}'.", dotGitPath);
        }

        return null;
    }

    private string? ResolveCommonDirectory(string gitDir)
    {
        var commonDirFile = Path.Combine(gitDir, CommonDirFileName);
        if (!File.Exists(commonDirFile))
        {
            return gitDir;
        }

        try
        {
            var rawCommon = File.ReadLines(commonDirFile).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
            if (string.IsNullOrWhiteSpace(rawCommon))
            {
                return gitDir;
            }

            return Path.IsPathRooted(rawCommon)
                ? Path.GetFullPath(rawCommon)
                : Path.GetFullPath(Path.Combine(gitDir, rawCommon));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogWarning(ex, "Failed to read commondir file at '{CommonDirFile}'.", commonDirFile);
            return gitDir;
        }
    }

    private string? ReadHeadBranch(string gitDir)
    {
        var headFile = Path.Combine(gitDir, HeadFileName);
        if (!File.Exists(headFile))
        {
            return null;
        }

        try
        {
            var firstLine = File.ReadLines(headFile).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return null;
            }

            if (firstLine.StartsWith(RefPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var target = firstLine[RefPrefix.Length..].Trim();
                return target.StartsWith(HeadsRefPrefix, StringComparison.OrdinalIgnoreCase)
                    ? target[HeadsRefPrefix.Length..]
                    : target;
            }

            return firstLine.Length > DetachedShaDisplayLength
                ? firstLine[..DetachedShaDisplayLength]
                : firstLine;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogWarning(ex, "Failed to read HEAD file at '{HeadFile}'.", headFile);
            return null;
        }
    }

    private string? ReadRemoteOriginUrl(string? commonDir, string? gitDir)
    {
        var candidateConfig = commonDir is not null ? Path.Combine(commonDir, ConfigFileName) : null;
        if (candidateConfig is null || !File.Exists(candidateConfig))
        {
            candidateConfig = gitDir is not null ? Path.Combine(gitDir, ConfigFileName) : null;
        }

        if (candidateConfig is null || !File.Exists(candidateConfig))
        {
            return null;
        }

        try
        {
            var inOriginSection = false;
            foreach (var rawLine in File.ReadLines(candidateConfig))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith(';'))
                {
                    continue;
                }

                if (line.StartsWith('['))
                {
                    var closeBracketIndex = line.IndexOf(']');
                    if (closeBracketIndex > 0)
                    {
                        var header = line[..(closeBracketIndex + 1)];
                        var trailing = line[(closeBracketIndex + 1)..].Trim();
                        if (string.IsNullOrEmpty(trailing) || trailing.StartsWith('#') || trailing.StartsWith(';'))
                        {
                            inOriginSection = RemoteOriginSectionRegex().IsMatch(header);
                            continue;
                        }
                    }

                    inOriginSection = false;
                    continue;
                }

                if (!inOriginSection)
                {
                    continue;
                }

                var eqIndex = line.IndexOf('=');
                if (eqIndex <= 0)
                {
                    continue;
                }

                var key = line[..eqIndex].Trim();
                if (!string.Equals(key, "url", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = ParseGitConfigValue(line[(eqIndex + 1)..]);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogWarning(ex, "Failed to read git config at '{ConfigFile}'.", candidateConfig);
        }

        return null;
    }

    private static string? ParseGitConfigValue(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var trimmed = rawValue.Trim();
        if (trimmed.StartsWith('"'))
        {
            var sb = new StringBuilder();
            var escaped = false;
            for (var i = 1; i < trimmed.Length; i++)
            {
                var ch = trimmed[i];
                if (escaped)
                {
                    sb.Append(ch);
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    var quotedResult = sb.ToString().Trim();
                    return string.IsNullOrWhiteSpace(quotedResult) ? null : quotedResult;
                }

                sb.Append(ch);
            }

            var unterminatedResult = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(unterminatedResult) ? null : unterminatedResult;
        }

        var commentIndex = trimmed.IndexOfAny(['#', ';']);
        var unquoted = commentIndex >= 0 ? trimmed[..commentIndex] : trimmed;
        unquoted = unquoted.Trim();
        return string.IsNullOrWhiteSpace(unquoted) ? null : unquoted;
    }

    private static DateTimeOffset ComputeLastModifiedUtc(string repoDir, string? gitDir)
    {
        var latestUtc = Directory.GetLastWriteTimeUtc(repoDir);

        if (gitDir is not null)
        {
            if (Directory.Exists(gitDir))
            {
                var gitDirWrite = Directory.GetLastWriteTimeUtc(gitDir);
                if (gitDirWrite > latestUtc)
                {
                    latestUtc = gitDirWrite;
                }
            }

            var headFile = Path.Combine(gitDir, HeadFileName);
            if (File.Exists(headFile))
            {
                var headWrite = File.GetLastWriteTimeUtc(headFile);
                if (headWrite > latestUtc)
                {
                    latestUtc = headWrite;
                }
            }

            var indexFile = Path.Combine(gitDir, IndexFileName);
            if (File.Exists(indexFile))
            {
                var indexWrite = File.GetLastWriteTimeUtc(indexFile);
                if (indexWrite > latestUtc)
                {
                    latestUtc = indexWrite;
                }
            }
        }

        return new DateTimeOffset(DateTime.SpecifyKind(latestUtc, DateTimeKind.Utc));
    }

    private static string? ExpandPath(string? rawPath, string? homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim());
        if ((expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal)) &&
            !string.IsNullOrWhiteSpace(homeDirectory))
        {
            expanded = Path.Combine(homeDirectory, expanded[2..]);
        }
        else if (expanded == "~" && !string.IsNullOrWhiteSpace(homeDirectory))
        {
            expanded = homeDirectory;
        }

        return expanded;
    }

    private static string? NormalizePathForComparison(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (!string.Equals(fullPath, root, StringComparison.Ordinal))
            {
                fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return null;
        }
    }
}
