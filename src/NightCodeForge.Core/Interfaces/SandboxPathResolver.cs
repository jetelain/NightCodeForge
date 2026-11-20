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
public sealed partial class SandboxPathResolver : ISandboxPathResolver
{
    [GeneratedRegex(
        @"(?<=^|[\s'""=:;(\[{,+|&><`-])(?:/workspace/attachments|workspace/attachments|[^\s'""]*\.nightcodeforge/wks/\d+/attachments)(?=$|/|[\s'""=:;)\]}},|&><`])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentsReferenceRegex();

    private static readonly HashSet<string> MutatingBashBinaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "rmdir", "mv", "touch", "mkdir", "chmod", "chown", "chgrp",
        "ln", "truncate", "dd", "tee", "install", "unlink", "shred", "mktemp", "cpio"
    };

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
                if (IsVirtualAttachmentsPath(canonical) && !MountTable.HasAttachmentsMount)
                {
                    throw new UnauthorizedAccessException(
                        $"Access denied: '{AttachmentConstants.VirtualAttachmentsMountPath}' is not mounted in scope '{Scope.ScopeCacheKey}'.");
                }
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
                if (IsVirtualAttachmentsPath(candidateVirtual) && !MountTable.HasAttachmentsMount)
                {
                    throw new UnauthorizedAccessException(
                        $"Access denied: '{AttachmentConstants.VirtualAttachmentsMountPath}' is not mounted in scope '{Scope.ScopeCacheKey}'.");
                }
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
        if (string.IsNullOrWhiteSpace(command))
        {
            return command;
        }

        ValidateBashAttachmentsAccess(command, MountTable, Scope.ScopeCacheKey);

        if (targetTier == ExecutionTier.ContainerSandbox)
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
        if (IsVirtualAttachmentsPath(canonicalVirtualPath) && !MountTable.HasAttachmentsMount)
        {
            throw new UnauthorizedAccessException(
                $"Access denied: '{AttachmentConstants.VirtualAttachmentsMountPath}' is not mounted in scope '{Scope.ScopeCacheKey}'.");
        }

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
                if (!TryNormalizeRelativeSegments(afterMarker, out var normalizedAfterMarker))
                {
                    throw new UnauthorizedAccessException(
                        $"Access denied: Path '{raw}' resolves outside the allowed workspace sandbox.");
                }

                if (normalizedAfterMarker.Equals("tmp", StringComparison.OrdinalIgnoreCase) ||
                    normalizedAfterMarker.StartsWith("tmp/", StringComparison.OrdinalIgnoreCase))
                {
                    resolvedHostPath = ResolveVirtualTmpPath("/" + normalizedAfterMarker, forWrite);
                    return true;
                }

                if (normalizedAfterMarker.Equals(AttachmentConstants.AttachmentsDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                    normalizedAfterMarker.StartsWith(AttachmentConstants.AttachmentsDirectoryName + "/", StringComparison.OrdinalIgnoreCase))
                {
                    var canonicalVirtual = CanonicalizeExplicitVirtualPath(
                        $"{SandboxMountTable.WorkspaceVirtualRoot}/{normalizedAfterMarker}",
                        raw);
                    resolvedHostPath = ResolveCanonicalVirtualPathToHost(canonicalVirtual, raw, forWrite);
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
                if (string.Equals(entry.VirtualMountPath, SandboxMountTable.WorkspaceVirtualRoot, StringComparison.OrdinalIgnoreCase) &&
                    IsRelativeAttachmentsSubPath(relFromHost))
                {
                    if (!MountTable.HasAttachmentsMount)
                    {
                        throw new UnauthorizedAccessException(
                            $"Access denied: '{AttachmentConstants.VirtualAttachmentsMountPath}' is not mounted in scope '{Scope.ScopeCacheKey}'.");
                    }

                    if (forWrite)
                    {
                        throw new UnauthorizedAccessException(
                            $"Access denied: Mount '{AttachmentConstants.VirtualAttachmentsMountPath}' is read-only.");
                    }
                }

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
                if (string.Equals(entry.VirtualMountPath, SandboxMountTable.WorkspaceVirtualRoot, StringComparison.OrdinalIgnoreCase) &&
                    IsRelativeAttachmentsSubPath(relFromWsl))
                {
                    if (!MountTable.HasAttachmentsMount)
                    {
                        throw new UnauthorizedAccessException(
                            $"Access denied: '{AttachmentConstants.VirtualAttachmentsMountPath}' is not mounted in scope '{Scope.ScopeCacheKey}'.");
                    }

                    if (forWrite)
                    {
                        throw new UnauthorizedAccessException(
                            $"Access denied: Mount '{AttachmentConstants.VirtualAttachmentsMountPath}' is read-only.");
                    }
                }

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

    /// <summary>
    /// Verifies that <paramref name="candidatePath"/> resolves to or inside <paramref name="directoryPath"/>
    /// using full canonical filesystem paths and platform-appropriate case sensitivity.
    /// </summary>
    public static bool IsPathInsideDirectory(string candidatePath, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        try
        {
            var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
            var fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (string.Equals(fullCandidate, fullDir, comparison))
            {
                return true;
            }

            return fullCandidate.StartsWith(fullDir + Path.DirectorySeparatorChar, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
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

    public static bool IsVirtualAttachmentsPath(string? virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            return false;
        }

        var normalized = virtualPath.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..].TrimStart('/');
        }

        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        var trimmed = normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
        var withLeadingSlash = trimmed.StartsWith('/') ? trimmed : "/" + trimmed;

        if (withLeadingSlash.Equals(AttachmentConstants.VirtualAttachmentsMountPath, StringComparison.OrdinalIgnoreCase) ||
            withLeadingSlash.StartsWith(AttachmentConstants.VirtualAttachmentsMountPath + "/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var workspacePrefix = SandboxMountTable.WorkspaceVirtualRoot + "/";
        if (withLeadingSlash.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase) &&
            TryNormalizeRelativeSegments(withLeadingSlash[workspacePrefix.Length..], out var collapsedUnderWorkspace) &&
            IsRelativeAttachmentsSubPath(collapsedUnderWorkspace))
        {
            return true;
        }

        const string wksSegment = ".nightcodeforge/wks/";
        var wksIdx = normalized.IndexOf(wksSegment, StringComparison.OrdinalIgnoreCase);
        if (wksIdx >= 0)
        {
            var afterWks = normalized[(wksIdx + wksSegment.Length)..];
            var slashIdx = afterWks.IndexOf('/');
            if (slashIdx > 0 &&
                long.TryParse(afterWks[..slashIdx], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                var afterId = afterWks[(slashIdx + 1)..];
                if (IsRelativeAttachmentsSubPath(afterId))
                {
                    return true;
                }

                if (TryNormalizeRelativeSegments(afterId, out var collapsedAfterId) &&
                    IsRelativeAttachmentsSubPath(collapsedAfterId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsAttachmentsPathOrReference(
        string? path,
        SandboxMountTable? mountTable = null,
        string? defaultWorkingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();
        if (IsVirtualAttachmentsPath(trimmed) || IsAttachmentsToken(trimmed, mountTable))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(defaultWorkingDirectory))
        {
            var norm = trimmed.Replace('\\', '/');
            if (!IsRootedNonVirtualPath(trimmed, norm))
            {
                var combined = $"{defaultWorkingDirectory.TrimEnd('/')}/{norm.TrimStart('/')}";
                if (IsVirtualAttachmentsPath(combined))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool ContainsAttachmentsReference(string? command, SandboxMountTable? mountTable = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        if (AttachmentsReferenceRegex().IsMatch(command) ||
            ReferencesMountedAttachmentsBackingDir(command, mountTable))
        {
            return true;
        }

        foreach (var segment in SplitShellSegments(command))
        {
            foreach (var token in TokenizeShellSegment(segment))
            {
                if (IsAttachmentsToken(token, mountTable))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsRelativeAttachmentsSubPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var normalized = relativePath.Trim().Replace('\\', '/').TrimStart('/');
        return normalized.Equals(AttachmentConstants.AttachmentsDirectoryName, StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(AttachmentConstants.AttachmentsDirectoryName + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static void ValidateBashAttachmentsAccess(
        string? command,
        SandboxMountTable? mountTable,
        string? scopeCacheKey = null,
        string? workingDirectory = null,
        string? defaultWorkingDirectory = null)
    {
        var workingDirIsAttachments = IsAttachmentsPathOrReference(workingDirectory, mountTable, defaultWorkingDirectory);
        if (workingDirIsAttachments && mountTable != null && !mountTable.HasAttachmentsMount)
        {
            throw new UnauthorizedAccessException(
                $"Access denied: '{AttachmentConstants.VirtualAttachmentsMountPath}' is not mounted in scope '{scopeCacheKey ?? "default"}'.");
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        var hasAttachmentsRef = workingDirIsAttachments ||
                                ContainsAttachmentsReference(command, mountTable);
        if (!hasAttachmentsRef)
        {
            return;
        }

        if (mountTable != null && !mountTable.HasAttachmentsMount)
        {
            throw new UnauthorizedAccessException(
                $"Access denied: '{AttachmentConstants.VirtualAttachmentsMountPath}' is not mounted in scope '{scopeCacheKey ?? "default"}'.");
        }

        var initialVirtualWorkingDir = workingDirIsAttachments
            ? NormalizeInitialVirtualAttachmentsDir(workingDirectory, defaultWorkingDirectory)
            : null;

        if (TargetsAttachmentsForMutation(command, mountTable, workingDirIsAttachments, initialVirtualWorkingDir))
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Mount '{AttachmentConstants.VirtualAttachmentsMountPath}' is read-only.");
        }
    }

    private static string? NormalizeInitialVirtualAttachmentsDir(string? workingDirectory, string? defaultWorkingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        var norm = workingDirectory.Trim().Replace('\\', '/');
        while (norm.StartsWith("./", StringComparison.Ordinal))
        {
            norm = norm[2..].TrimStart('/');
        }

        if (!IsRootedNonVirtualPath(workingDirectory.Trim(), norm) && !string.IsNullOrWhiteSpace(defaultWorkingDirectory))
        {
            norm = $"{defaultWorkingDirectory.TrimEnd('/')}/{norm.TrimStart('/')}";
        }

        var withSlash = norm.StartsWith('/') ? norm : "/" + norm;
        if (withSlash.StartsWith(SandboxMountTable.WorkspaceVirtualRoot + "/", StringComparison.OrdinalIgnoreCase) &&
            TryNormalizeRelativeSegments(withSlash[(SandboxMountTable.WorkspaceVirtualRoot.Length + 1)..], out var collapsed) &&
            IsRelativeAttachmentsSubPath(collapsed))
        {
            return $"{SandboxMountTable.WorkspaceVirtualRoot}/{collapsed}";
        }

        return null;
    }

    private static bool ReferencesMountedAttachmentsBackingDir(string command, SandboxMountTable? mountTable)
    {
        var attachEntry = mountTable?.Entries.FirstOrDefault(e =>
            string.Equals(e.VirtualMountPath, AttachmentConstants.VirtualAttachmentsMountPath, StringComparison.OrdinalIgnoreCase));
        if (attachEntry == null)
        {
            return false;
        }

        var normCommand = command.Replace('\\', '/');
        return ContainsPathPrefix(normCommand, attachEntry.HostDirectoryPath) ||
               ContainsPathPrefix(normCommand, attachEntry.WslTargetDirectoryPath);
    }

    private static bool ContainsPathPrefix(string normalizedCommand, string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return false;
        }

        var normDir = dir.Replace('\\', '/').TrimEnd('/');
        if (normDir.Length <= 1)
        {
            return false;
        }

        var idx = 0;
        while ((idx = normalizedCommand.IndexOf(normDir, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var endIdx = idx + normDir.Length;
            var hasValidBoundaryAfter = endIdx == normalizedCommand.Length ||
                                        normalizedCommand[endIdx] is '/' or ' ' or '\t' or '\r' or '\n' or '\'' or '"' or '=' or ':' or ';' or ')' or ']' or '}' or ',' or '|' or '&' or '>' or '<' or '`';
            if (hasValidBoundaryAfter)
            {
                return true;
            }

            idx = endIdx;
        }

        return false;
    }

    private static bool TargetsAttachmentsForMutation(
        string command,
        SandboxMountTable? mountTable,
        bool workingDirectoryIsAttachments = false,
        string? currentVirtualAttachmentsDir = null)
    {
        bool inAttachmentsWorkingDir = workingDirectoryIsAttachments;
        var activeVirtualDir = currentVirtualAttachmentsDir;

        foreach (var segment in SplitShellSegments(command))
        {
            var tokens = TokenizeShellSegment(segment);
            if (tokens.Count == 0)
            {
                continue;
            }

            // 1. Output redirections (> / >> / 1> / 1>>) targeting attachments
            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (IsRedirectOperatorToken(token))
                {
                    if (i + 1 < tokens.Count &&
                        TokenTargetsAttachments(tokens[i + 1], mountTable, inAttachmentsWorkingDir))
                    {
                        return true;
                    }
                }
                else if (TryExtractInlineRedirectTarget(token, out var inlineTarget) &&
                         TokenTargetsAttachments(inlineTarget, mountTable, inAttachmentsWorkingDir))
                {
                    return true;
                }
            }

            // 2. Command binary & arguments
            var cmdIndex = 0;
            while (cmdIndex < tokens.Count && IsEnvironmentAssignmentToken(tokens[cmdIndex]))
            {
                cmdIndex++;
            }

            if (cmdIndex >= tokens.Count)
            {
                continue;
            }

            var binaryToken = tokens[cmdIndex];
            var binaryName = binaryToken.Contains('/')
                ? binaryToken[(binaryToken.LastIndexOf('/') + 1)..]
                : binaryToken;

            var args = tokens.Skip(cmdIndex + 1).ToList();

            if (string.Equals(binaryName, "cd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(binaryName, "pushd", StringComparison.OrdinalIgnoreCase))
            {
                var targetDir = args.FirstOrDefault(a => !a.StartsWith('-'));
                (inAttachmentsWorkingDir, activeVirtualDir) = ResolveWorkingDirectoryTransition(
                    targetDir,
                    mountTable,
                    inAttachmentsWorkingDir,
                    activeVirtualDir);
                continue;
            }

            if (!args.Any(a => TokenTargetsAttachments(a, mountTable, inAttachmentsWorkingDir)))
            {
                continue;
            }

            if (MutatingBashBinaries.Contains(binaryName))
            {
                return true;
            }

            if (string.Equals(binaryName, "sed", StringComparison.OrdinalIgnoreCase) &&
                args.Any(IsSedInPlaceFlag))
            {
                return true;
            }

            if (string.Equals(binaryName, "perl", StringComparison.OrdinalIgnoreCase) &&
                args.Any(a => a.StartsWith("-", StringComparison.Ordinal) &&
                              !a.StartsWith("--", StringComparison.Ordinal) &&
                              a.Contains('i')))
            {
                return true;
            }

            if (string.Equals(binaryName, "find", StringComparison.OrdinalIgnoreCase) &&
                IsMutatingFindCommand(args))
            {
                return true;
            }

            if (string.Equals(binaryName, "cp", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < args.Count; i++)
                {
                    var arg = args[i];
                    if ((arg == "-t" || arg == "--target-directory") &&
                        i + 1 < args.Count &&
                        TokenTargetsAttachments(args[i + 1], mountTable, inAttachmentsWorkingDir))
                    {
                        return true;
                    }
                    if (arg.StartsWith("--target-directory=", StringComparison.Ordinal) &&
                        TokenTargetsAttachments(arg["--target-directory=".Length..], mountTable, inAttachmentsWorkingDir))
                    {
                        return true;
                    }
                }

                var positionalArgs = args.Where(a => !a.StartsWith('-')).ToList();
                if (positionalArgs.Count >= 1 &&
                    TokenTargetsAttachments(positionalArgs[^1], mountTable, inAttachmentsWorkingDir))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static (bool InAttachments, string? VirtualDir) ResolveWorkingDirectoryTransition(
        string? targetDir,
        SandboxMountTable? mountTable,
        bool currentlyInAttachments,
        string? currentVirtualDir)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            return (false, null);
        }

        if (IsAttachmentsToken(targetDir, mountTable))
        {
            return (true, NormalizeInitialVirtualAttachmentsDir(targetDir, null) ?? AttachmentConstants.VirtualAttachmentsMountPath);
        }

        if (!currentlyInAttachments)
        {
            return (false, null);
        }

        var norm = targetDir.Trim().Replace('\\', '/');
        if (IsRootedNonVirtualPath(targetDir.Trim(), norm) || norm.StartsWith('~'))
        {
            return (false, null);
        }

        if (!string.IsNullOrWhiteSpace(currentVirtualDir))
        {
            var combined = $"{currentVirtualDir.TrimEnd('/')}/{norm}";
            var workspacePrefix = SandboxMountTable.WorkspaceVirtualRoot + "/";
            if (combined.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase) &&
                TryNormalizeRelativeSegments(combined[workspacePrefix.Length..], out var collapsed) &&
                IsRelativeAttachmentsSubPath(collapsed))
            {
                return (true, $"{SandboxMountTable.WorkspaceVirtualRoot}/{collapsed}");
            }

            return (false, null);
        }

        return !norm.StartsWith("..", StringComparison.Ordinal)
            ? (true, null)
            : (false, null);
    }

    private static bool TokenTargetsAttachments(string token, SandboxMountTable? mountTable, bool inAttachmentsWorkingDir)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (IsAttachmentsToken(token, mountTable))
        {
            return true;
        }

        if (!inAttachmentsWorkingDir)
        {
            return false;
        }

        if (token.StartsWith('-'))
        {
            var eqInFlag = token.IndexOf('=');
            if (eqInFlag > 0)
            {
                var flagValue = token[(eqInFlag + 1)..];
                return IsRelativeWorkingDirTarget(flagValue);
            }
            return false;
        }

        var eqIdx = token.IndexOf('=');
        if (eqIdx > 0 && !token.StartsWith('/'))
        {
            return IsRelativeWorkingDirTarget(token[(eqIdx + 1)..]);
        }

        return IsRelativeWorkingDirTarget(token);
    }

    private static bool IsRelativeWorkingDirTarget(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var norm = trimmed.Replace('\\', '/');
        return !norm.StartsWith('/') &&
               !norm.StartsWith('~') &&
               !IsRootedNonVirtualPath(trimmed, norm);
    }

    private static bool IsSedInPlaceFlag(string arg) =>
        arg.StartsWith("--in-place", StringComparison.Ordinal) ||
        (arg.StartsWith("-", StringComparison.Ordinal) &&
         !arg.StartsWith("--", StringComparison.Ordinal) &&
         !arg.StartsWith("-e", StringComparison.Ordinal) &&
         !arg.StartsWith("-f", StringComparison.Ordinal) &&
         arg.Contains('i'));

    private static bool IsMutatingFindCommand(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("-delete", StringComparison.Ordinal))
            {
                return true;
            }

            if ((arg.Equals("-exec", StringComparison.Ordinal) ||
                 arg.Equals("-execdir", StringComparison.Ordinal) ||
                 arg.Equals("-ok", StringComparison.Ordinal) ||
                 arg.Equals("-okdir", StringComparison.Ordinal)) &&
                i + 1 < args.Count)
            {
                var execBinary = args[i + 1];
                var execName = execBinary.Contains('/')
                    ? execBinary[(execBinary.LastIndexOf('/') + 1)..]
                    : execBinary;
                if (MutatingBashBinaries.Contains(execName) ||
                    string.Equals(execName, "sed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(execName, "perl", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsAttachmentsToken(string token, SandboxMountTable? mountTable)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (TryExtractInlineRedirectTarget(token, out var inlineTarget))
        {
            token = inlineTarget;
        }
        else if (!token.StartsWith('/') && !token.StartsWith('\\'))
        {
            var eqIdx = token.IndexOf('=');
            if (eqIdx > 0 && eqIdx < token.Length - 1)
            {
                token = token[(eqIdx + 1)..];
            }
        }

        if (IsVirtualAttachmentsPath(token) || AttachmentsReferenceRegex().IsMatch(token))
        {
            return true;
        }

        var normToken = token.Replace('\\', '/');
        var attachEntry = mountTable?.Entries.FirstOrDefault(e =>
            string.Equals(e.VirtualMountPath, AttachmentConstants.VirtualAttachmentsMountPath, StringComparison.OrdinalIgnoreCase));
        if (attachEntry == null)
        {
            return false;
        }

        if (ContainsPathPrefix(normToken, attachEntry.HostDirectoryPath) ||
            ContainsPathPrefix(normToken, attachEntry.WslTargetDirectoryPath))
        {
            return true;
        }

        if (normToken.Contains("/../", StringComparison.Ordinal) ||
            normToken.Contains("/./", StringComparison.Ordinal) ||
            normToken.EndsWith("/..", StringComparison.Ordinal))
        {
            if (TryCollapseRootedPath(normToken, out var collapsedToken) &&
                (ContainsPathPrefix(collapsedToken, attachEntry.HostDirectoryPath) ||
                 ContainsPathPrefix(collapsedToken, attachEntry.WslTargetDirectoryPath)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryCollapseRootedPath(string normalizedPath, out string collapsedPath)
    {
        collapsedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        if (normalizedPath.StartsWith("//", StringComparison.Ordinal))
        {
            var afterLeading = normalizedPath[2..];
            var serverEnd = afterLeading.IndexOf('/');
            if (serverEnd <= 0 || serverEnd >= afterLeading.Length - 1)
            {
                return false;
            }
            var shareEnd = afterLeading.IndexOf('/', serverEnd + 1);
            if (shareEnd <= 0)
            {
                return false;
            }
            var rootPrefix = normalizedPath[..(2 + shareEnd)];
            var rest = afterLeading[(shareEnd + 1)..];
            if (!TryNormalizeRelativeSegments(rest, out var cleanRest))
            {
                return false;
            }
            collapsedPath = string.IsNullOrEmpty(cleanRest) ? rootPrefix : $"{rootPrefix}/{cleanRest}";
            return true;
        }

        if (normalizedPath.StartsWith('/'))
        {
            if (!TryNormalizeRelativeSegments(normalizedPath[1..], out var cleanRest))
            {
                return false;
            }
            collapsedPath = string.IsNullOrEmpty(cleanRest) ? "/" : "/" + cleanRest;
            return true;
        }

        if (normalizedPath.Length >= 3 && char.IsAsciiLetter(normalizedPath[0]) && normalizedPath[1] == ':' && normalizedPath[2] == '/')
        {
            if (!TryNormalizeRelativeSegments(normalizedPath[3..], out var cleanRest))
            {
                return false;
            }
            collapsedPath = normalizedPath[..3] + cleanRest;
            return true;
        }

        return false;
    }

    private static bool IsRedirectOperatorToken(string token) =>
        token is ">" or ">>" or "1>" or "1>>" or "&>" or "&>>" or ">|" or "2>" or "2>>";

    private static bool TryExtractInlineRedirectTarget(string token, out string target)
    {
        target = string.Empty;
        string[] prefixes = ["1>>", "2>>", "&>>", "1>", "2>", "&>", ">>", ">|", ">"];
        foreach (var prefix in prefixes)
        {
            if (token.StartsWith(prefix, StringComparison.Ordinal) && token.Length > prefix.Length)
            {
                target = token[prefix.Length..];
                return true;
            }
        }

        return false;
    }

    private static bool IsEnvironmentAssignmentToken(string token)
    {
        var eqIdx = token.IndexOf('=');
        return eqIdx > 0 && !token.StartsWith('/') && !token.StartsWith('-');
    }

    private static List<string> SplitShellSegments(string command)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        char quote = '\0';

        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"' && i + 1 < command.Length)
                {
                    current.Append(c);
                    current.Append(command[++i]);
                    continue;
                }
                if (c == quote)
                {
                    quote = '\0';
                }
                current.Append(c);
                continue;
            }

            if (c == '\\' && i + 1 < command.Length && command[i + 1] is ';' or '|' or '&' or '(' or ')')
            {
                current.Append(c);
                current.Append(command[++i]);
                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                current.Append(c);
                continue;
            }

            if (c is ';' or '\n' or '\r' or '|' or '(' or ')')
            {
                if (c == '|' && i + 1 < command.Length && command[i + 1] == '|')
                {
                    i++;
                }
                segments.Add(current.ToString());
                current.Clear();
                continue;
            }

            if (c == '&')
            {
                if (i + 1 < command.Length && command[i + 1] == '&')
                {
                    i++;
                    segments.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                var isRedirectionAmp = (i + 1 < command.Length && command[i + 1] == '>') ||
                                       (i > 0 && command[i - 1] == '>');
                if (!isRedirectionAmp)
                {
                    segments.Add(current.ToString());
                    current.Clear();
                    continue;
                }
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            segments.Add(current.ToString());
        }

        return segments;
    }

    private static List<string> TokenizeShellSegment(string segment)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        char quote = '\0';

        for (int i = 0; i < segment.Length; i++)
        {
            char c = segment[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"' && i + 1 < segment.Length)
                {
                    current.Append(segment[++i]);
                    continue;
                }
                if (c == quote)
                {
                    quote = '\0';
                    continue;
                }
                current.Append(c);
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                continue;
            }

            if (c == '>' && current.Length > 0 && current.ToString() is not ("1" or "2" or "&" or ">"))
            {
                tokens.Add(current.ToString());
                current.Clear();
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}

