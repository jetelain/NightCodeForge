using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Unified virtual path resolver and boundary guard for Host C# file tools and non-container WSL2/BareHost bash commands.
/// Eliminates the need for on-disk symlinks.
/// </summary>
public interface ISandboxPathResolver
{
    SandboxScopeDescriptor Scope { get; }

    SandboxMountTable MountTable { get; }

    /// <summary>
    /// Resolves any agent-supplied path (relative to VirtualRootPath, absolute /workspace/..., /nightcodeforge/..., or /tmp/...)
    /// to its backing Host C# filesystem path, enforcing strict mount boundaries and read-only guards.
    /// </summary>
    string ResolveHostPath(string path, bool forWrite = false);

    /// <summary>
    /// Resolves the effective working directory for a bash command on the given execution tier.
    /// </summary>
    string ResolveBashWorkingDirectory(string? workingDirectory, ExecutionTier targetTier);

    /// <summary>
    /// Translates explicit virtual path prefixes (/workspace/... or /nightcodeforge/&lt;subpath&gt;/...) in bash commands
    /// to their backing WSL2 or Host directory paths when executing outside a container.
    /// In ContainerSandbox mode, returns the command unchanged.
    /// </summary>
    string TranslateBashCommand(string command, ExecutionTier targetTier);
}

/// <summary>
/// Default implementation of <see cref="ISandboxPathResolver"/> performing longest-prefix virtual mount matching,
/// strict path traversal boundary validation, read-only enforcement, and non-container bash command path rewriting.
/// </summary>
public sealed class SandboxPathResolver : ISandboxPathResolver
{
    private readonly ConcurrentDictionary<ExecutionTier, (Regex? Pattern, IReadOnlyList<(string VirtualPrefix, string TargetPrefix)> OrderedMappings)> _compiledTierPatterns = new();

    public SandboxScopeDescriptor Scope { get; }
    public SandboxMountTable MountTable => Scope.MountTable;

    public SandboxPathResolver(SandboxScopeDescriptor scope)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
    }

    public string ResolveHostPath(string path, bool forWrite = false)
    {
        if (forWrite && Scope.IsReadOnly)
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Write operations are not permitted in read-only scope '{Scope.ScopeCacheKey}'.");
        }

        var raw = (path ?? string.Empty).Trim();
        var normalized = raw.Replace('\\', '/');

        // 1. Check if the path is already a rooted host/WSL path inside one of our configured mounts
        if (!string.IsNullOrEmpty(raw) && TryResolveAlreadyMappedHostOrWslPath(raw, normalized, forWrite, out var mappedHostPath))
        {
            return mappedHostPath;
        }

        // 2. Check virtual /tmp path
        if (normalized.Equals("/tmp", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("/tmp/", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveVirtualTmpPath(raw, forWrite);
        }

        // 3. Strip leading ./ or .\
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            raw = raw[2..].TrimStart('/', '\\');
            normalized = raw.Replace('\\', '/');
        }

        // 4. Determine whether the path is an explicit virtual path (/workspace..., /nightcodeforge..., /clone...) or a relative path
        var virtualRoot = Scope.VirtualRootPath;
        var virtualRootNoSlash = virtualRoot.TrimStart('/');

        bool isVirtualPrefixed =
            normalized.Equals(virtualRoot, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(virtualRoot + "/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(virtualRootNoSlash, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(virtualRootNoSlash + "/", StringComparison.OrdinalIgnoreCase) ||
            (virtualRoot.Equals(SandboxMountTable.WorkspaceVirtualRoot, StringComparison.OrdinalIgnoreCase) &&
             (normalized.Equals("/clone", StringComparison.OrdinalIgnoreCase) ||
              normalized.StartsWith("/clone/", StringComparison.OrdinalIgnoreCase)));

        if (isVirtualPrefixed)
        {
            var canonicalVirtual = CanonicalizeExplicitVirtualPath(normalized, path ?? string.Empty);
            return ResolveCanonicalVirtualPathToHost(canonicalVirtual, path ?? string.Empty, forWrite);
        }

        // 5. Reject rooted paths that do not belong to the virtual root or any configured mount
        if (IsRootedNonVirtualPath(raw, normalized))
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Path '{path}' resolves outside the allowed workspace sandbox.");
        }

        // 6. Relative path: resolve relative to DefaultWorkingDirectory and match longest prefix in MountTable
        if (!TryNormalizeRelativeSegments(normalized, out var cleanRel))
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Path '{path}' resolves outside the allowed workspace sandbox.");
        }

        var baseVirtual = !string.IsNullOrWhiteSpace(Scope.DefaultWorkingDirectory)
            ? Scope.DefaultWorkingDirectory.TrimEnd('/')
            : virtualRoot;

        var synthesizedVirtual = string.IsNullOrEmpty(cleanRel)
            ? baseVirtual
            : $"{baseVirtual}/{cleanRel}";

        return ResolveCanonicalVirtualPathToHost(synthesizedVirtual, path ?? string.Empty, forWrite);
    }

    public string ResolveBashWorkingDirectory(string? workingDirectory, ExecutionTier targetTier)
    {
        if (targetTier == ExecutionTier.ContainerSandbox)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory) ||
                workingDirectory.Trim() is "." or "./")
            {
                return Scope.DefaultWorkingDirectory;
            }

            var trimmed = workingDirectory.Trim();
            var norm = trimmed.Replace('\\', '/');
            if (norm.Length > 1)
            {
                norm = norm.TrimEnd('/');
            }

            if (norm.Equals(Scope.VirtualRootPath, StringComparison.OrdinalIgnoreCase))
            {
                return Scope.DefaultWorkingDirectory;
            }

            if (norm.Equals("/tmp", StringComparison.OrdinalIgnoreCase) ||
                norm.StartsWith("/tmp/", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryResolveTmpSubPath(trimmed, out var tmpSub))
                {
                    throw new UnauthorizedAccessException(
                        $"Access denied: Path '{workingDirectory}' resolves outside the allowed workspace sandbox.");
                }
                return string.IsNullOrEmpty(tmpSub) ? "/tmp" : $"/tmp/{tmpSub}";
            }

            if (norm.StartsWith(Scope.VirtualRootPath + "/", StringComparison.OrdinalIgnoreCase) ||
                norm.Equals("/clone", StringComparison.OrdinalIgnoreCase) ||
                norm.StartsWith("/clone/", StringComparison.OrdinalIgnoreCase))
            {
                var canonical = CanonicalizeExplicitVirtualPath(norm, workingDirectory);
                if (!MountTable.TryMatchLongestPrefix(canonical, out _, out _))
                {
                    throw new UnauthorizedAccessException(
                        $"Access denied: Path '{workingDirectory}' resolves outside the allowed workspace sandbox.");
                }
                return canonical;
            }

            if (!IsRootedNonVirtualPath(trimmed, norm))
            {
                var rel = norm;
                while (rel.StartsWith("./", StringComparison.Ordinal))
                {
                    rel = rel[2..].TrimStart('/');
                }
                if (!TryNormalizeRelativeSegments(rel, out var cleanRel))
                {
                    throw new UnauthorizedAccessException(
                        $"Access denied: Path '{workingDirectory}' resolves outside the allowed workspace sandbox.");
                }
                if (string.IsNullOrEmpty(cleanRel))
                {
                    return Scope.DefaultWorkingDirectory;
                }

                var candidateVirtual = $"{Scope.DefaultWorkingDirectory.TrimEnd('/')}/{cleanRel}";
                if (!MountTable.TryMatchLongestPrefix(candidateVirtual, out _, out _))
                {
                    throw new UnauthorizedAccessException(
                        $"Access denied: Path '{workingDirectory}' resolves outside the allowed workspace sandbox.");
                }
                return candidateVirtual;
            }

            // Host or WSL path supplied as workingDirectory: verify it maps to an allowed mount
            _ = ResolveHostPath(workingDirectory, forWrite: false);
            return MapHostOrWslDirectoryToVirtual(norm);
        }

        // Non-container execution tier (WslDirect or BareHost)
        if (string.IsNullOrWhiteSpace(workingDirectory) ||
            workingDirectory.Trim() is "." or "./" ||
            string.Equals(workingDirectory.Trim().Replace('\\', '/').TrimEnd('/'), Scope.VirtualRootPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(workingDirectory.Trim().Replace('\\', '/').TrimEnd('/'), Scope.DefaultWorkingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            if (MountTable.TryMatchLongestPrefix(Scope.DefaultWorkingDirectory, out var defaultMount, out var rem) &&
                defaultMount != null)
            {
                return CombineTargetDirectory(defaultMount, rem, targetTier);
            }

            if (!string.IsNullOrWhiteSpace(MountTable.NonContainerFallbackWorkingDirectory))
            {
                return ResolveFallbackWorkingDirectory(MountTable.NonContainerFallbackWorkingDirectory, targetTier);
            }

            throw new UnauthorizedAccessException(
                $"Access denied: Default working directory '{Scope.DefaultWorkingDirectory}' is not mapped in scope '{Scope.ScopeCacheKey}'.");
        }

        if (targetTier == ExecutionTier.BareHost)
        {
            return ResolveHostPath(workingDirectory, forWrite: false);
        }

        // WslDirect with explicit workingDirectory
        return ResolveWslPath(workingDirectory);
    }

    public string TranslateBashCommand(string command, ExecutionTier targetTier)
    {
        if (string.IsNullOrWhiteSpace(command) || targetTier == ExecutionTier.ContainerSandbox)
        {
            return command;
        }

        var (regex, ordered) = _compiledTierPatterns.GetOrAdd(targetTier, BuildTierTranslationPattern);
        if (regex == null || ordered.Count == 0)
        {
            return command;
        }

        return regex.Replace(command, match =>
        {
            var matchedText = match.Value;
            var mapping = ordered.First(c => string.Equals(c.VirtualPrefix, matchedText, StringComparison.OrdinalIgnoreCase));
            return mapping.TargetPrefix;
        });
    }

    private (Regex? Pattern, IReadOnlyList<(string VirtualPrefix, string TargetPrefix)> OrderedMappings) BuildTierTranslationPattern(ExecutionTier targetTier)
    {
        var candidates = new List<(string VirtualPrefix, string TargetPrefix)>();
        foreach (var entry in MountTable.Entries)
        {
            var target = targetTier == ExecutionTier.WslDirect
                ? entry.WslTargetDirectoryPath
                : entry.HostDirectoryPath;

            if (!string.IsNullOrWhiteSpace(entry.VirtualMountPath) && !string.IsNullOrWhiteSpace(target))
            {
                var normalizedTarget = target.Replace('\\', '/').TrimEnd('/');
                candidates.Add((entry.VirtualMountPath, normalizedTarget));
            }
        }

        if (MountTable.SandboxTmpMount != null)
        {
            var tmpTarget = targetTier == ExecutionTier.WslDirect
                ? MountTable.SandboxTmpMount.WslTargetDirectoryPath
                : MountTable.SandboxTmpMount.HostDirectoryPath;

            var normalizedTmpTarget = tmpTarget?.Replace('\\', '/').TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(normalizedTmpTarget) &&
                !string.Equals(normalizedTmpTarget, "/tmp", StringComparison.Ordinal))
            {
                candidates.Add(("/tmp", normalizedTmpTarget));
            }
        }

        if (candidates.Count == 0)
        {
            return (null, []);
        }

        var ordered = candidates
            .OrderByDescending(c => c.VirtualPrefix.Length)
            .ToList();

        var pattern = string.Join("|", ordered.Select(c => Regex.Escape(c.VirtualPrefix)));
        var regex = new Regex(
            $@"(?<=^|[\s'""=:;(\[{{,+|&><`-])({pattern})(?=$|/|[\s'""=:;)\]}},|&><`])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        return (regex, ordered);
    }

    private string ResolveWslPath(string path)
    {
        var raw = path.Trim();
        var normalized = raw.Replace('\\', '/');

        if (normalized.Equals("/tmp", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("/tmp/", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveTmpSubPath(raw, out var tmpSub))
            {
                throw new UnauthorizedAccessException(
                    $"Access denied: Path '{path}' resolves outside the allowed workspace sandbox.");
            }
            if (MountTable.SandboxTmpMount != null && !string.IsNullOrWhiteSpace(MountTable.SandboxTmpMount.WslTargetDirectoryPath))
            {
                var baseTmp = MountTable.SandboxTmpMount.WslTargetDirectoryPath.Replace('\\', '/').TrimEnd('/');
                return string.IsNullOrEmpty(tmpSub) ? baseTmp : $"{baseTmp}/{tmpSub}";
            }
            return string.IsNullOrEmpty(tmpSub) ? "/tmp" : $"/tmp/{tmpSub}";
        }

        // First validate via ResolveHostPath (enforces boundary & mount existence)
        _ = ResolveHostPath(path, forWrite: false);

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            raw = raw[2..].TrimStart('/', '\\');
            normalized = raw.Replace('\\', '/');
        }

        var virtualRoot = Scope.VirtualRootPath;
        var virtualRootNoSlash = virtualRoot.TrimStart('/');
        bool isVirtualPrefixed =
            normalized.Equals(virtualRoot, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(virtualRoot + "/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(virtualRootNoSlash, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(virtualRootNoSlash + "/", StringComparison.OrdinalIgnoreCase) ||
            (virtualRoot.Equals(SandboxMountTable.WorkspaceVirtualRoot, StringComparison.OrdinalIgnoreCase) &&
             (normalized.Equals("/clone", StringComparison.OrdinalIgnoreCase) ||
              normalized.StartsWith("/clone/", StringComparison.OrdinalIgnoreCase)));

        string canonicalVirtual;
        if (isVirtualPrefixed)
        {
            canonicalVirtual = CanonicalizeExplicitVirtualPath(normalized, path);
        }
        else if (!IsRootedNonVirtualPath(raw, normalized))
        {
            TryNormalizeRelativeSegments(normalized, out var cleanRel);
            canonicalVirtual = string.IsNullOrEmpty(cleanRel)
                ? Scope.DefaultWorkingDirectory
                : $"{Scope.DefaultWorkingDirectory.TrimEnd('/')}/{cleanRel}";
        }
        else
        {
            return normalized;
        }

        if (MountTable.TryMatchLongestPrefix(canonicalVirtual, out var matched, out var rem) && matched != null)
        {
            return CombineTargetDirectory(matched, rem, ExecutionTier.WslDirect);
        }

        throw new UnauthorizedAccessException(
            $"Access denied: Path '{path}' resolves outside the allowed workspace sandbox.");
    }

    private string ResolveCanonicalVirtualPathToHost(string canonicalVirtualPath, string originalPath, bool forWrite)
    {
        if (!MountTable.TryMatchLongestPrefix(canonicalVirtualPath, out var matchedMount, out var remainder) ||
            matchedMount == null)
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Path '{originalPath}' is not mapped in the sandbox mount table and resolves outside the allowed workspace sandbox.");
        }

        if (forWrite && (Scope.IsReadOnly || matchedMount.IsReadOnly))
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Mount '{matchedMount.VirtualMountPath}' is read-only.");
        }

        if (!TryNormalizeRelativeSegments(remainder, out var cleanRemainder))
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Path '{originalPath}' resolves outside the allowed workspace sandbox.");
        }

        return CombineAndValidateHostPath(matchedMount.HostDirectoryPath, cleanRemainder, originalPath);
    }

    private string CanonicalizeExplicitVirtualPath(string normalized, string originalPath)
    {
        var virtualRoot = Scope.VirtualRootPath;
        var virtualRootNoSlash = virtualRoot.TrimStart('/');

        if (virtualRoot.Equals(SandboxMountTable.WorkspaceVirtualRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (normalized.Equals("/clone", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "/workspace/clone";
            }
            else if (normalized.StartsWith("/clone/", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "/workspace/clone/" + normalized["/clone/".Length..].TrimStart('/');
            }
        }

        if (normalized.Equals(virtualRootNoSlash, StringComparison.OrdinalIgnoreCase))
        {
            normalized = virtualRoot;
        }
        else if (normalized.StartsWith(virtualRootNoSlash + "/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "/" + normalized;
        }

        var afterRoot = normalized.Length > virtualRoot.Length
            ? normalized[virtualRoot.Length..].TrimStart('/')
            : string.Empty;

        if (!TryNormalizeRelativeSegments(afterRoot, out var cleanAfterRoot))
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Path '{originalPath}' resolves outside the allowed workspace sandbox.");
        }

        return string.IsNullOrEmpty(cleanAfterRoot)
            ? virtualRoot
            : $"{virtualRoot}/{cleanAfterRoot}";
    }

    private string ResolveVirtualTmpPath(string rawPath, bool forWrite)
    {
        if (!TryResolveTmpSubPath(rawPath, out var tmpSubPath))
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Path '{rawPath}' resolves outside the allowed workspace sandbox.");
        }

        var tmpMount = MountTable.SandboxTmpMount
            ?? MountTable.Entries.FirstOrDefault(e => string.Equals(e.VirtualMountPath, "/tmp", StringComparison.OrdinalIgnoreCase));

        if (tmpMount == null || string.IsNullOrWhiteSpace(tmpMount.HostDirectoryPath))
        {
            throw new UnauthorizedAccessException(
                $"Access denied: '/tmp' is not mounted in scope '{Scope.ScopeCacheKey}'.");
        }

        if (forWrite && (Scope.IsReadOnly || tmpMount.IsReadOnly))
        {
            throw new UnauthorizedAccessException("Access denied: Mount '/tmp' is read-only.");
        }

        return CombineAndValidateHostPath(tmpMount.HostDirectoryPath, tmpSubPath, rawPath);
    }

    private bool TryResolveAlreadyMappedHostOrWslPath(
        string raw,
        string normalized,
        bool forWrite,
        out string resolvedHostPath)
    {
        resolvedHostPath = string.Empty;

        if (Scope.WorkspaceId.HasValue)
        {
            var wksMarker = $".nightcodeforge/wks/{Scope.WorkspaceId.Value}/";
            var wksIdx = normalized.IndexOf(wksMarker, StringComparison.OrdinalIgnoreCase);
            if (wksIdx >= 0)
            {
                var afterMarker = normalized[(wksIdx + wksMarker.Length)..].TrimStart('/');
                if (afterMarker.Equals("tmp", StringComparison.OrdinalIgnoreCase) ||
                    afterMarker.StartsWith("tmp/", StringComparison.OrdinalIgnoreCase))
                {
                    resolvedHostPath = ResolveVirtualTmpPath("/" + afterMarker, forWrite);
                    return true;
                }
            }
        }

        var allMounts = new List<VirtualMountEntry>(MountTable.Entries);
        if (MountTable.SandboxTmpMount != null)
        {
            allMounts.Add(MountTable.SandboxTmpMount);
        }

        foreach (var entry in allMounts.OrderByDescending(e => e.HostDirectoryPath.Length))
        {
            if (TryMatchBackingDirectory(raw, normalized, entry.HostDirectoryPath, out var relFromHost))
            {
                if (forWrite && (Scope.IsReadOnly || entry.IsReadOnly))
                {
                    throw new UnauthorizedAccessException(
                        $"Access denied: Mount '{entry.VirtualMountPath}' is read-only.");
                }
                resolvedHostPath = CombineAndValidateHostPath(entry.HostDirectoryPath, relFromHost, raw);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(entry.WslTargetDirectoryPath) &&
                !string.Equals(entry.WslTargetDirectoryPath, "/tmp", StringComparison.OrdinalIgnoreCase) &&
                TryMatchBackingDirectory(raw, normalized, entry.WslTargetDirectoryPath, out var relFromWsl))
            {
                if (forWrite && (Scope.IsReadOnly || entry.IsReadOnly))
                {
                    throw new UnauthorizedAccessException(
                        $"Access denied: Mount '{entry.VirtualMountPath}' is read-only.");
                }
                resolvedHostPath = CombineAndValidateHostPath(entry.HostDirectoryPath, relFromWsl, raw);
                return true;
            }
        }

        return false;
    }

    private static bool TryMatchBackingDirectory(
        string raw,
        string normalizedInput,
        string backingDir,
        out string cleanRelativeRemainder)
    {
        cleanRelativeRemainder = string.Empty;
        if (string.IsNullOrWhiteSpace(backingDir))
        {
            return false;
        }

        var normBacking = backingDir.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrEmpty(normBacking))
        {
            return false;
        }

        if (normalizedInput.Equals(normBacking, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalizedInput.StartsWith(normBacking + "/", StringComparison.OrdinalIgnoreCase))
        {
            var remainder = normalizedInput[(normBacking.Length + 1)..];
            return TryNormalizeRelativeSegments(remainder, out cleanRelativeRemainder);
        }

        // Also check resolved full path on local OS when input contains relative segments under backingDir
        if (Path.IsPathRooted(raw) && !normalizedInput.StartsWith("//") && !backingDir.StartsWith(@"\\"))
        {
            try
            {
                var fullInput = Path.GetFullPath(raw).Replace('\\', '/').TrimEnd('/');
                var fullBacking = Path.GetFullPath(backingDir).Replace('\\', '/').TrimEnd('/');
                if (fullInput.Equals(fullBacking, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (fullInput.StartsWith(fullBacking + "/", StringComparison.OrdinalIgnoreCase))
                {
                    var remainder = fullInput[(fullBacking.Length + 1)..];
                    return TryNormalizeRelativeSegments(remainder, out cleanRelativeRemainder);
                }
                if (normalizedInput.StartsWith(fullBacking + "/", StringComparison.OrdinalIgnoreCase))
                {
                    // Had '..' that escaped fullBacking!
                    throw new UnauthorizedAccessException(
                        $"Access denied: Path '{raw}' resolves outside the allowed workspace sandbox.");
                }
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Non-local path syntax (e.g., POSIX paths tested on Windows) cannot be canonicalized via Path.GetFullPath; fall back to false.
            }
        }

        return false;
    }

    private string MapHostOrWslDirectoryToVirtual(string normalizedPath)
    {
        var allMounts = new List<VirtualMountEntry>(MountTable.Entries);
        if (MountTable.SandboxTmpMount != null)
        {
            allMounts.Add(MountTable.SandboxTmpMount);
        }

        var orderedByBackingLength = allMounts
            .OrderByDescending(e => Math.Max(e.HostDirectoryPath.Length, e.WslTargetDirectoryPath.Length))
            .ThenByDescending(e => e.VirtualMountPath.Length);

        foreach (var entry in orderedByBackingLength)
        {
            if (TryMatchBackingDirectory(normalizedPath, normalizedPath, entry.HostDirectoryPath, out var rel) ||
                (!string.Equals(entry.WslTargetDirectoryPath, "/tmp", StringComparison.OrdinalIgnoreCase) &&
                 TryMatchBackingDirectory(normalizedPath, normalizedPath, entry.WslTargetDirectoryPath, out rel)))
            {
                return string.IsNullOrEmpty(rel)
                    ? entry.VirtualMountPath
                    : $"{entry.VirtualMountPath.TrimEnd('/')}/{rel}";
            }
        }

        return Scope.DefaultWorkingDirectory;
    }

    private static string CombineTargetDirectory(VirtualMountEntry entry, string relativeRemainder, ExecutionTier targetTier)
    {
        var baseDir = targetTier == ExecutionTier.WslDirect
            ? entry.WslTargetDirectoryPath
            : entry.HostDirectoryPath;

        if (string.IsNullOrEmpty(relativeRemainder))
        {
            return FormatHostPath(baseDir, string.Empty);
        }

        return FormatHostPath(baseDir, relativeRemainder);
    }

    private static string ResolveFallbackWorkingDirectory(string fallbackDir, ExecutionTier targetTier)
    {
        if (targetTier == ExecutionTier.WslDirect)
        {
            return fallbackDir.Replace('\\', '/');
        }

        string hostFallback = fallbackDir;
        if (fallbackDir.StartsWith("~/", StringComparison.Ordinal) || fallbackDir.StartsWith("~\\", StringComparison.Ordinal))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
            {
                hostFallback = Path.Combine(profile, fallbackDir[2..].Replace('/', Path.DirectorySeparatorChar));
            }
        }

        try
        {
            if (!Directory.Exists(hostFallback))
            {
                Directory.CreateDirectory(hostFallback);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Best-effort creation of empty marker directory on BareHost when host filesystem is read-only or synthetic in tests.
        }

        return hostFallback;
    }

    private static string CombineAndValidateHostPath(string baseHostDir, string cleanRelativeSubPath, string originalPath)
    {
        if (string.IsNullOrWhiteSpace(baseHostDir))
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Path '{originalPath}' resolves outside the allowed workspace sandbox.");
        }

        var formatted = FormatHostPath(baseHostDir, cleanRelativeSubPath);

        var normBase = NormalizePathForComparison(baseHostDir);
        var normFormatted = NormalizePathForComparison(formatted);

        if (!normFormatted.Equals(normBase, StringComparison.OrdinalIgnoreCase) &&
            !normFormatted.StartsWith(normBase + "/", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Path '{originalPath}' resolves outside the allowed workspace sandbox.");
        }

        return formatted;
    }

    private static string FormatHostPath(string baseDir, string subPath)
    {
        bool isUnc = baseDir.StartsWith(@"\\", StringComparison.Ordinal) || baseDir.StartsWith("//", StringComparison.Ordinal);
        bool isUnix = !isUnc && (baseDir.StartsWith('/') || baseDir.StartsWith('~'));

        if (string.IsNullOrEmpty(subPath))
        {
            if (isUnc) return baseDir.Replace('/', '\\').TrimEnd('\\');
            if (isUnix) return baseDir.Replace('\\', '/').TrimEnd('/');
            return Path.GetFullPath(baseDir);
        }

        if (isUnc)
        {
            var cleanBase = baseDir.Replace('/', '\\').TrimEnd('\\');
            var cleanSub = subPath.Replace('/', '\\').TrimStart('\\');
            return $@"{cleanBase}\{cleanSub}";
        }

        if (isUnix)
        {
            var cleanBase = baseDir.Replace('\\', '/').TrimEnd('/');
            var cleanSub = subPath.Replace('\\', '/').TrimStart('/');
            return $"{cleanBase}/{cleanSub}";
        }

        return Path.GetFullPath(Path.Combine(baseDir, subPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string NormalizePathForComparison(string path)
    {
        var norm = path.Replace('\\', '/').TrimEnd('/');
        if (!norm.StartsWith("//", StringComparison.Ordinal) &&
            !norm.StartsWith('/') &&
            !norm.StartsWith('~') &&
            Path.IsPathRooted(path))
        {
            try
            {
                norm = Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Keep slash-normalized string when path uses non-local platform syntax.
            }
        }
        return norm;
    }

    private static bool IsRootedNonVirtualPath(string raw, string normalized)
    {
        if (normalized.StartsWith('/') || normalized.StartsWith(@"\\") || normalized.StartsWith("//"))
        {
            return true;
        }

        if (raw.Length >= 2 && char.IsLetter(raw[0]) && raw[1] == ':')
        {
            return true;
        }

        return false;
    }

    public static bool TryNormalizeRelativeSegments(string? relativePath, out string normalizedSubPath)
    {
        normalizedSubPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return true;
        }

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(segments.Length);

        foreach (var seg in segments)
        {
            if (seg == ".")
            {
                continue;
            }

            if (seg == "..")
            {
                if (stack.Count == 0)
                {
                    return false;
                }
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(seg);
        }

        normalizedSubPath = string.Join("/", stack);
        return true;
    }

    public static bool TryResolveTmpSubPath(string? path, out string subPath)
    {
        subPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Trim().Replace('\\', '/');
        if (!normalized.Equals("/tmp", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("/tmp/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = normalized.Length > 4 ? normalized[4..].TrimStart('/') : string.Empty;
        if (string.IsNullOrEmpty(remainder))
        {
            return true;
        }

        return TryNormalizeRelativeSegments(remainder, out subPath);
    }
}

