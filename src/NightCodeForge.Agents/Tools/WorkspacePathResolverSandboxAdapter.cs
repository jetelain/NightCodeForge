using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Adapts <see cref="IWorkspacePathResolver"/> and <see cref="CanonicalToolScope"/> to <see cref="ISandboxPathResolver"/>
/// when an explicit <see cref="SandboxScopeDescriptor"/> or <see cref="ISandboxPathResolver"/> is not supplied.
/// </summary>
public sealed partial class WorkspacePathResolverSandboxAdapter : ISandboxPathResolver
{
    [GeneratedRegex(
        @"(?<=^|[\s'""=:;(\[{,+|&><`-])(/workspace/attachments)(?=$|/|[\s'""=:;)\]}},|&><`])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex VirtualAttachmentsPrefixRegex();

    [GeneratedRegex(
        @"(?<=^|[\s'""=:;(\[{,+|&><`-])(/workspace/memory)(?=$|/|[\s'""=:;)\]}},|&><`])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex VirtualMemoryPrefixRegex();

    private readonly string _workspaceRoot;
    private readonly int? _workspaceId;
    private readonly int? _taskId;
    private readonly bool _isReadOnly;
    private readonly bool _allowLocalMemoryWriteInReadOnlyScope;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly ILogger? _logger;

    public SandboxScopeDescriptor Scope { get; }

    public SandboxMountTable MountTable => Scope.MountTable;

    public WorkspacePathResolverSandboxAdapter(
        CanonicalToolScope scope,
        IWorkspacePathResolver pathResolver,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(scope);
        _workspaceRoot = scope.ResolveNormalizedWorkspaceRoot();
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workspaceId = scope.WorkspaceId;
        _taskId = (scope.TaskId.HasValue && scope.TaskId.Value > 0) ? scope.TaskId.Value : null;
        _isReadOnly = scope.IsReadOnly;
        _allowLocalMemoryWriteInReadOnlyScope = scope.AllowLocalMemoryWriteInReadOnlyScope;

        var isUnixVirtualRoot = (_workspaceRoot.StartsWith('/') && !_workspaceRoot.StartsWith("//")) ||
                                _workspaceRoot.StartsWith('~');
        var workspaceTmpRoot = isUnixVirtualRoot && string.IsNullOrWhiteSpace(_pathResolver.GetWslDistributionName())
            ? "/tmp"
            : _pathResolver.ResolveCruciblePath(_workspaceRoot, "/tmp", _taskId);
        var workspaceAttachmentsRoot = ResolveEffectiveAttachmentsRoot();
        var mountTable = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: _workspaceRoot,
            isReadOnly: _isReadOnly,
            taskId: _taskId,
            hostTmpDirectory: workspaceTmpRoot,
            additionalEntries: BuildMemoryEntries(),
            hostAttachmentsDirectory: workspaceAttachmentsRoot,
            wslAttachmentsDirectory: VirtualMountEntry.ToDefaultWslPath(workspaceAttachmentsRoot));
        Scope = new SandboxScopeDescriptor(
            scopeKind: _taskId.HasValue ? SandboxScopeKind.TaskWorktree : SandboxScopeKind.WorkspaceClone,
            executionMode: ExecutionMode.BareHost,
            securityTier: _isReadOnly ? SandboxSecurityTier.RestrictedReadOnly : SandboxSecurityTier.Standard,
            mountTable: mountTable,
            workspaceId: _workspaceId,
            taskId: _taskId,
            repositoryPath: _workspaceRoot);
    }

    public string ResolveHostPath(string path, bool forWrite = false)
    {
        if (forWrite && _isReadOnly && !_allowLocalMemoryWriteInReadOnlyScope)
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Write operations are not permitted in read-only scope '{Scope.ScopeCacheKey}'.");
        }

        var fullPath = ResolveRawPath(path, forWrite);
        ValidateWithinWorkspace(fullPath, path, forWrite);
        if (forWrite && _isReadOnly)
        {
            EnsureInsideWritableLocalMemory(fullPath, path);
        }

        RejectMemoryReparsePoints(fullPath, path);
        return fullPath;
    }

    private IEnumerable<VirtualMountEntry> BuildMemoryEntries()
    {
        if (_workspaceId is not > 0)
        {
            return [];
        }

        var hostMemoryRoot = _pathResolver.ResolveCruciblePath(_workspaceId.Value, AgentMemoryConstants.VirtualLocalMemoryPath, _taskId);
        if (string.IsNullOrWhiteSpace(hostMemoryRoot))
        {
            var fallback = _pathResolver.GetWorkspaceMemoryPath(_workspaceId.Value);
            hostMemoryRoot = string.IsNullOrWhiteSpace(fallback) ? string.Empty : _pathResolver.ToHostPath(fallback);
        }

        if (string.IsNullOrWhiteSpace(hostMemoryRoot))
        {
            return [];
        }

        var rawMemoryPath = _pathResolver.GetWorkspaceMemoryPath(_workspaceId.Value);
        var writable = !_isReadOnly || _allowLocalMemoryWriteInReadOnlyScope;
        return
        [
            new VirtualMountEntry(
                virtualSubPath: AgentMemoryConstants.LocalMemoryDirectoryName,
                virtualMountPath: AgentMemoryConstants.VirtualLocalMemoryPath,
                hostDirectoryPath: hostMemoryRoot,
                wslTargetDirectoryPath: VirtualMountEntry.ToDefaultWslPath(
                    !string.IsNullOrWhiteSpace(rawMemoryPath) ? rawMemoryPath : hostMemoryRoot),
                isReadOnly: !writable,
                allowWriteInReadOnlyScope: _allowLocalMemoryWriteInReadOnlyScope)
        ];
    }

    private VirtualMountEntry? FindLocalMemoryEntry() =>
        MountTable.Entries.FirstOrDefault(e =>
            string.Equals(e.VirtualMountPath, AgentMemoryConstants.VirtualLocalMemoryPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>In read-only scope the only writable target is a flat, non-hidden Markdown file in the local memory root.</summary>
    private void EnsureInsideWritableLocalMemory(string fullPath, string requestedPath)
    {
        var memoryEntry = FindLocalMemoryEntry();
        var normFull = NormalizeForSandbox(fullPath);
        var parent = NormalizeForSandbox(Path.GetDirectoryName(fullPath) ?? string.Empty);
        var isWritableMemoryFile =
            memoryEntry is { IsReadOnly: false, AllowWriteInReadOnlyScope: true } &&
            string.Equals(parent, NormalizeForSandbox(memoryEntry.HostDirectoryPath), StringComparison.OrdinalIgnoreCase) &&
            IsFlatMarkdownFileName(Path.GetFileName(normFull));

        if (!isWritableMemoryFile)
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Path '{requestedPath}' is read-only. In read-only mode, only '{AgentMemoryConstants.VirtualLocalMemoryPath}/*{AgentMemoryConstants.MarkdownExtension}' is writable.");
        }
    }

    private static bool IsFlatMarkdownFileName(string fileName) =>
        !fileName.StartsWith('.') &&
        fileName.EndsWith(AgentMemoryConstants.MarkdownExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Rejects symlinks / reparse points under the local memory root and under repository memory (<c>AGENTS.md</c>, <c>.agents/AGENTS.md</c>, <c>.agents/memory</c>).</summary>
    private void RejectMemoryReparsePoints(string fullPath, string requestedPath)
    {
        var normFull = NormalizeForSandbox(fullPath);

        var memoryEntry = FindLocalMemoryEntry();
        if (memoryEntry != null)
        {
            var normMemory = NormalizeForSandbox(memoryEntry.HostDirectoryPath);
            if (!string.IsNullOrEmpty(normMemory) &&
                (normFull.Equals(normMemory, StringComparison.OrdinalIgnoreCase) ||
                 normFull.StartsWith(normMemory + "/", StringComparison.OrdinalIgnoreCase)))
            {
                if (AgentMemoryReparseGuard.IsReparsePoint(memoryEntry.HostDirectoryPath))
                {
                    throw new UnauthorizedAccessException(
                        $"Access denied: Path '{requestedPath}' traverses a symbolic link or reparse point inside agent memory.");
                }

                WalkSegmentsForReparsePoint(normMemory, normFull[normMemory.Length..], requestedPath);
                return;
            }
        }

        var sharedIdx = normFull.IndexOf("/" + AgentMemoryConstants.SharedMemoryDirectory, StringComparison.OrdinalIgnoreCase);
        if (sharedIdx >= 0)
        {
            var afterShared = sharedIdx + 1 + AgentMemoryConstants.SharedMemoryDirectory.Length;
            if (afterShared == normFull.Length || normFull[afterShared] == '/')
            {
                var chainRoot = normFull[..sharedIdx];
                WalkSegmentsForReparsePoint(chainRoot, normFull[chainRoot.Length..], requestedPath);
                return;
            }
        }

        foreach (var candidateRoot in EnumerateWorkspaceRoots())
        {
            var normRoot = NormalizeForSandbox(candidateRoot);
            if (string.IsNullOrEmpty(normRoot) || !normFull.StartsWith(normRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rel = normFull[(normRoot.Length + 1)..];
            if (string.Equals(rel, AgentMemoryConstants.RootRulesFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rel, AgentMemoryConstants.AgentsRulesFilePath, StringComparison.OrdinalIgnoreCase))
            {
                WalkSegmentsForReparsePoint(normRoot, rel, requestedPath);
                return;
            }
        }
    }

    private IEnumerable<string> EnumerateWorkspaceRoots()
    {
        if (!string.IsNullOrWhiteSpace(_workspaceRoot))
        {
            yield return _workspaceRoot;
        }

        if (_workspaceId.HasValue)
        {
            var crucibleRoot = _pathResolver.ResolveCruciblePath(_workspaceId.Value, string.Empty, _taskId);
            if (!string.IsNullOrWhiteSpace(crucibleRoot))
            {
                yield return crucibleRoot;
            }

            var crucibleCloneRoot = _pathResolver.ResolveCruciblePath(_workspaceId.Value, "/workspace/clone", _taskId);
            if (!string.IsNullOrWhiteSpace(crucibleCloneRoot))
            {
                yield return crucibleCloneRoot;
            }
        }
    }

    private static void WalkSegmentsForReparsePoint(string chainRoot, string relativeTail, string requestedPath)
    {
        var current = chainRoot.TrimEnd('/');
        foreach (var segment in relativeTail.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = string.IsNullOrEmpty(current) ? "/" + segment : $"{current}/{segment}";
            if (AgentMemoryReparseGuard.IsReparsePoint(current))
            {
                throw new UnauthorizedAccessException(
                    $"Access denied: Path '{requestedPath}' traverses a symbolic link or reparse point inside agent memory.");
            }
        }
    }

    private bool TryExtractMemorySubPath(string? path, out string subPath)
    {
        subPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var norm = path.Trim().Replace('\\', '/');
        while (norm.StartsWith("./", StringComparison.Ordinal))
        {
            norm = norm[2..].TrimStart('/');
        }

        string? remainder = null;
        if (norm.Equals(AgentMemoryConstants.VirtualLocalMemoryPath, StringComparison.OrdinalIgnoreCase) ||
            norm.Equals("workspace/memory", StringComparison.OrdinalIgnoreCase))
        {
            remainder = string.Empty;
        }
        else if (norm.StartsWith(AgentMemoryConstants.VirtualLocalMemoryPath + "/", StringComparison.OrdinalIgnoreCase))
        {
            remainder = norm[(AgentMemoryConstants.VirtualLocalMemoryPath.Length + 1)..];
        }
        else if (norm.StartsWith("workspace/memory/", StringComparison.OrdinalIgnoreCase))
        {
            remainder = norm["workspace/memory/".Length..];
        }
        else if (norm.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase) ||
                 norm.StartsWith("workspace/", StringComparison.OrdinalIgnoreCase))
        {
            var afterWorkspace = norm.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase)
                ? norm["/workspace/".Length..]
                : norm["workspace/".Length..];
            if (SandboxPathResolver.TryNormalizeRelativeSegments(afterWorkspace, out var collapsedWorkspaceRel))
            {
                if (collapsedWorkspaceRel.Equals(AgentMemoryConstants.LocalMemoryDirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    remainder = string.Empty;
                }
                else if (collapsedWorkspaceRel.StartsWith(AgentMemoryConstants.LocalMemoryDirectoryName + "/", StringComparison.OrdinalIgnoreCase))
                {
                    remainder = collapsedWorkspaceRel[(AgentMemoryConstants.LocalMemoryDirectoryName.Length + 1)..];
                }
            }
        }
        else if (_workspaceId.HasValue)
        {
            var wksMarker = $".nightcodeforge/wks/{_workspaceId.Value}/";
            var idx = norm.IndexOf(wksMarker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var after = norm[(idx + wksMarker.Length)..];
                if (!SandboxPathResolver.TryNormalizeRelativeSegments(after, out var collapsedAfter))
                {
                    throw new UnauthorizedAccessException(
                        $"Access denied: Path '{path}' resolves outside the allowed workspace sandbox.");
                }

                if (collapsedAfter.Equals(AgentMemoryConstants.LocalMemoryDirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    remainder = string.Empty;
                }
                else if (collapsedAfter.StartsWith(AgentMemoryConstants.LocalMemoryDirectoryName + "/", StringComparison.OrdinalIgnoreCase))
                {
                    remainder = collapsedAfter[(AgentMemoryConstants.LocalMemoryDirectoryName.Length + 1)..];
                }
            }
        }

        if (remainder == null)
        {
            return false;
        }

        if (!SandboxPathResolver.TryNormalizeRelativeSegments(remainder, out subPath))
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Path '{path}' resolves outside the allowed workspace sandbox.");
        }

        return true;
    }

    public string ResolveBashWorkingDirectory(string? workingDirectory, ExecutionTier targetTier)
    {
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            if (TryExtractAttachmentsSubPath(workingDirectory, out var attachSub))
            {
                if (!MountTable.HasAttachmentsMount)
                {
                    throw new UnauthorizedAccessException(
                        $"Access denied: '{AttachmentConstants.VirtualAttachmentsMountPath}' is not mounted in scope '{Scope.ScopeCacheKey}'.");
                }

                if (targetTier == ExecutionTier.ContainerSandbox)
                {
                    return string.IsNullOrEmpty(attachSub)
                        ? AttachmentConstants.VirtualAttachmentsMountPath
                        : $"{AttachmentConstants.VirtualAttachmentsMountPath}/{attachSub}";
                }

                if (targetTier == ExecutionTier.BareHost)
                {
                    return ResolveHostPath(workingDirectory, forWrite: false);
                }

                var attachEntry = MountTable.Entries.FirstOrDefault(e =>
                    string.Equals(e.VirtualMountPath, AttachmentConstants.VirtualAttachmentsMountPath, StringComparison.OrdinalIgnoreCase));
                if (attachEntry != null)
                {
                    var baseDir = attachEntry.WslTargetDirectoryPath;
                    if (!string.IsNullOrWhiteSpace(baseDir))
                    {
                        return string.IsNullOrEmpty(attachSub)
                            ? baseDir.Replace('\\', '/').TrimEnd('/')
                            : $"{baseDir.Replace('\\', '/').TrimEnd('/')}/{attachSub}";
                    }
                }
            }

            if (TryExtractMemorySubPath(workingDirectory, out var memorySub))
            {
                var memoryEntry = FindLocalMemoryEntry()
                    ?? throw new UnauthorizedAccessException(
                        $"Access denied: '{AgentMemoryConstants.VirtualLocalMemoryPath}' is not mounted in scope '{Scope.ScopeCacheKey}'.");

                if (targetTier == ExecutionTier.BareHost)
                {
                    return ResolveHostPath(workingDirectory, forWrite: false);
                }

                if (targetTier == ExecutionTier.WslDirect && !string.IsNullOrWhiteSpace(memoryEntry.WslTargetDirectoryPath))
                {
                    var wslBase = memoryEntry.WslTargetDirectoryPath.Replace('\\', '/').TrimEnd('/');
                    return string.IsNullOrEmpty(memorySub) ? wslBase : $"{wslBase}/{memorySub}";
                }

                return string.IsNullOrEmpty(memorySub)
                    ? AgentMemoryConstants.VirtualLocalMemoryPath
                    : $"{AgentMemoryConstants.VirtualLocalMemoryPath}/{memorySub}";
            }

            if (targetTier != ExecutionTier.BareHost &&
                (workingDirectory.StartsWith('/') || workingDirectory.StartsWith('.') || !Path.IsPathRooted(workingDirectory)))
            {
                return workingDirectory;
            }

            return ResolveHostPath(workingDirectory, forWrite: false);
        }

        return targetTier == ExecutionTier.BareHost ? _workspaceRoot : string.Empty;
    }

    public string TranslateBashCommand(string command, ExecutionTier targetTier)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return command;
        }

        SandboxPathResolver.ValidateBashAttachmentsAccess(command, MountTable, Scope.ScopeCacheKey);

        if (targetTier == ExecutionTier.ContainerSandbox)
        {
            return command;
        }

        var translated = command;
        var memoryEntry = FindLocalMemoryEntry();
        if (memoryEntry != null)
        {
            var memoryTarget = targetTier == ExecutionTier.WslDirect
                ? memoryEntry.WslTargetDirectoryPath
                : memoryEntry.HostDirectoryPath;
            if (!string.IsNullOrWhiteSpace(memoryTarget))
            {
                translated = VirtualMemoryPrefixRegex().Replace(translated, memoryTarget.Replace('\\', '/').TrimEnd('/'));
            }
        }

        var attachEntry = MountTable.Entries.FirstOrDefault(e =>
            string.Equals(e.VirtualMountPath, AttachmentConstants.VirtualAttachmentsMountPath, StringComparison.OrdinalIgnoreCase));
        if (attachEntry == null)
        {
            return translated;
        }

        var target = targetTier == ExecutionTier.WslDirect
            ? attachEntry.WslTargetDirectoryPath
            : attachEntry.HostDirectoryPath;
        if (string.IsNullOrWhiteSpace(target))
        {
            return translated;
        }

        var normalizedTarget = target.Replace('\\', '/').TrimEnd('/');
        return VirtualAttachmentsPrefixRegex().Replace(translated, normalizedTarget);
    }

    private string ResolveRawPath(string path, bool forWrite)
    {
        if (TryExtractMemorySubPath(path, out _))
        {
            if (!_workspaceId.HasValue || FindLocalMemoryEntry() == null)
            {
                throw new UnauthorizedAccessException(
                    $"Access denied: '{AgentMemoryConstants.VirtualLocalMemoryPath}' is not mounted in scope '{Scope.ScopeCacheKey}'.");
            }

            return _pathResolver.ResolveCruciblePath(_workspaceId.Value, path, _taskId);
        }

        if (forWrite && !string.IsNullOrWhiteSpace(path))
        {
            var norm = path.Trim().Replace('\\', '/');
            if (norm.Equals("/workspace/skills", StringComparison.OrdinalIgnoreCase) ||
                norm.StartsWith("/workspace/skills/", StringComparison.OrdinalIgnoreCase) ||
                norm.Equals("/nightcodeforge", StringComparison.OrdinalIgnoreCase) ||
                norm.StartsWith("/nightcodeforge/", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException($"Access denied: Mount '{path}' is read-only.");
            }
        }

        if (TryExtractAttachmentsSubPath(path, out var attachSubPath))
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

            var canonicalAttachPath = string.IsNullOrEmpty(attachSubPath)
                ? AttachmentConstants.VirtualAttachmentsMountPath
                : $"{AttachmentConstants.VirtualAttachmentsMountPath}/{attachSubPath}";

            if (_workspaceId.HasValue)
            {
                var rawIdAttach = _pathResolver.GetAttachmentsPath(_workspaceId.Value);
                var hostIdAttach = !string.IsNullOrWhiteSpace(rawIdAttach)
                    ? _pathResolver.ToHostPath(rawIdAttach)
                    : null;
                var rootCrucibleDir = _pathResolver.ResolveCruciblePath(_workspaceId.Value, string.Empty, _taskId);
                var crucibleAttachPath = _pathResolver.ResolveCruciblePath(_workspaceId.Value, canonicalAttachPath, _taskId);

                if ((!string.IsNullOrWhiteSpace(hostIdAttach) && Directory.Exists(hostIdAttach)) ||
                    (!string.IsNullOrWhiteSpace(rootCrucibleDir) && Directory.Exists(rootCrucibleDir)) ||
                    (!string.IsNullOrWhiteSpace(crucibleAttachPath) && (File.Exists(crucibleAttachPath) || Directory.Exists(crucibleAttachPath))))
                {
                    if (!string.IsNullOrWhiteSpace(crucibleAttachPath))
                    {
                        return crucibleAttachPath;
                    }

                    if (!string.IsNullOrWhiteSpace(hostIdAttach))
                    {
                        return CombineHostAttachmentPath(hostIdAttach, attachSubPath);
                    }
                }
            }

            var resolvedFromRoot = _pathResolver.ResolveCruciblePath(_workspaceRoot, canonicalAttachPath, _taskId);
            if (!string.IsNullOrWhiteSpace(resolvedFromRoot))
            {
                return resolvedFromRoot;
            }
        }

        if (_workspaceId.HasValue)
        {
            var rootCrucibleDir = _pathResolver.ResolveCruciblePath(_workspaceId.Value, string.Empty, _taskId);
            if (Directory.Exists(rootCrucibleDir))
            {
                return _pathResolver.ResolveCruciblePath(_workspaceId.Value, path, _taskId);
            }

            if (WorkspacePathResolver.TryResolveTmpSubPath(path, out _))
            {
                var crucibleTmpDir = _pathResolver.ResolveCruciblePath(_workspaceId.Value, "/tmp", _taskId);
                if (Directory.Exists(crucibleTmpDir))
                {
                    return _pathResolver.ResolveCruciblePath(_workspaceId.Value, path, _taskId);
                }
            }

            var cruciblePath = _pathResolver.ResolveCruciblePath(_workspaceId.Value, path, _taskId);
            if (File.Exists(cruciblePath) || Directory.Exists(cruciblePath))
            {
                return cruciblePath;
            }
        }

        return _pathResolver.ResolveCruciblePath(_workspaceRoot, path, _taskId);
    }

    private void ValidateWithinWorkspace(string fullPath, string requestedPath, bool forWrite)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            throw new UnauthorizedAccessException("Access denied: Empty path.");
        }

        var reqNorm = (requestedPath ?? string.Empty).Trim().Replace('\\', '/');
        if ((reqNorm.Equals("/tmp", StringComparison.OrdinalIgnoreCase) ||
             reqNorm.StartsWith("/tmp/", StringComparison.OrdinalIgnoreCase)) &&
            !WorkspacePathResolver.TryResolveTmpSubPath(requestedPath, out _))
        {
            _logger?.LogWarning("CanonicalTools: Path traversal outside /tmp sandbox detected: '{Path}' (resolved: '{FullPath}')", requestedPath, fullPath);
            throw new UnauthorizedAccessException($"Access denied: Path '{requestedPath}' resolves outside the allowed workspace sandbox.");
        }

        var attachmentsRoots = CollectAttachmentsRoots();
        var normFullPath = NormalizeForSandbox(fullPath);

        foreach (var attachRoot in attachmentsRoots)
        {
            var normAttachRoot = NormalizeForSandbox(attachRoot);
            if (!string.IsNullOrEmpty(normAttachRoot) &&
                (normFullPath.Equals(normAttachRoot, StringComparison.OrdinalIgnoreCase) ||
                 normFullPath.StartsWith(normAttachRoot + "/", StringComparison.OrdinalIgnoreCase)))
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

                return;
            }
        }

        var allowedRoots = new List<string>();
        if (!string.IsNullOrWhiteSpace(_workspaceRoot))
        {
            allowedRoots.Add(_workspaceRoot);
            var isUnixVirtualRoot = (_workspaceRoot.StartsWith('/') && !_workspaceRoot.StartsWith("//")) ||
                                    _workspaceRoot.StartsWith('~');
            var workspaceTmpRoot = isUnixVirtualRoot && string.IsNullOrWhiteSpace(_pathResolver.GetWslDistributionName())
                ? "/tmp"
                : _pathResolver.ResolveCruciblePath(_workspaceRoot, "/tmp", _taskId);
            if (!string.IsNullOrWhiteSpace(workspaceTmpRoot))
            {
                allowedRoots.Add(workspaceTmpRoot);
            }

            var workspaceCloneRoot = _pathResolver.ResolveCruciblePath(_workspaceRoot, "/workspace/clone", _taskId);
            if (!string.IsNullOrWhiteSpace(workspaceCloneRoot))
            {
                allowedRoots.Add(workspaceCloneRoot);
            }
        }

        if (_workspaceId.HasValue)
        {
            var crucibleRoot = _pathResolver.ResolveCruciblePath(_workspaceId.Value, string.Empty, _taskId);
            if (!string.IsNullOrWhiteSpace(crucibleRoot))
            {
                allowedRoots.Add(crucibleRoot);
            }

            var crucibleTmpRoot = _pathResolver.ResolveCruciblePath(_workspaceId.Value, "/tmp", _taskId);
            if (!string.IsNullOrWhiteSpace(crucibleTmpRoot))
            {
                allowedRoots.Add(crucibleTmpRoot);
            }

            var crucibleCloneRoot = _pathResolver.ResolveCruciblePath(_workspaceId.Value, "/workspace/clone", _taskId);
            if (!string.IsNullOrWhiteSpace(crucibleCloneRoot))
            {
                allowedRoots.Add(crucibleCloneRoot);
            }

            var memoryEntry = FindLocalMemoryEntry();
            if (memoryEntry != null && !string.IsNullOrWhiteSpace(memoryEntry.HostDirectoryPath))
            {
                allowedRoots.Add(memoryEntry.HostDirectoryPath);
            }
        }

        bool isAllowed = false;
        foreach (var root in allowedRoots)
        {
            var normRoot = NormalizeForSandbox(root);
            if (!string.IsNullOrEmpty(normRoot) &&
                (normFullPath.Equals(normRoot, StringComparison.OrdinalIgnoreCase) ||
                 normFullPath.StartsWith(normRoot + "/", StringComparison.OrdinalIgnoreCase)))
            {
                isAllowed = true;
                break;
            }
        }

        if (!isAllowed)
        {
            _logger?.LogWarning("CanonicalTools: Path traversal outside workspace boundary detected: '{Path}' (resolved: '{FullPath}')", requestedPath, fullPath);
            throw new UnauthorizedAccessException($"Access denied: Path '{requestedPath}' resolves outside the allowed workspace sandbox.");
        }
    }

    private List<string> CollectAttachmentsRoots()
    {
        var roots = new List<string>();

        void AddIfPresent(string? candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                roots.Add(candidate);
            }
        }

        var attachEntry = MountTable.Entries.FirstOrDefault(e =>
            string.Equals(e.VirtualMountPath, AttachmentConstants.VirtualAttachmentsMountPath, StringComparison.OrdinalIgnoreCase));
        if (attachEntry != null)
        {
            AddIfPresent(attachEntry.HostDirectoryPath);
            AddIfPresent(attachEntry.WslTargetDirectoryPath);
        }

        if (!string.IsNullOrWhiteSpace(_workspaceRoot))
        {
            AddIfPresent(_pathResolver.ResolveCruciblePath(_workspaceRoot, AttachmentConstants.VirtualAttachmentsMountPath, _taskId));
        }

        if (_workspaceId.HasValue)
        {
            var rawIdAttach = _pathResolver.GetAttachmentsPath(_workspaceId.Value);
            AddIfPresent(rawIdAttach);
            if (!string.IsNullOrWhiteSpace(rawIdAttach))
            {
                AddIfPresent(_pathResolver.ToHostPath(rawIdAttach));
            }
            AddIfPresent(_pathResolver.ResolveCruciblePath(_workspaceId.Value, AttachmentConstants.VirtualAttachmentsMountPath, _taskId));
        }

        return roots;
    }

    private string ResolveEffectiveAttachmentsRoot()
    {
        if (_workspaceId.HasValue)
        {
            var rawIdAttachments = _pathResolver.GetAttachmentsPath(_workspaceId.Value);
            var hostIdAttachments = !string.IsNullOrWhiteSpace(rawIdAttachments)
                ? _pathResolver.ToHostPath(rawIdAttachments)
                : null;

            if (!string.IsNullOrWhiteSpace(hostIdAttachments) && Directory.Exists(hostIdAttachments))
            {
                return hostIdAttachments;
            }

            var rootCrucibleDir = _pathResolver.ResolveCruciblePath(_workspaceId.Value, string.Empty, _taskId);
            if (!string.IsNullOrWhiteSpace(rootCrucibleDir) && Directory.Exists(rootCrucibleDir))
            {
                if (!string.IsNullOrWhiteSpace(hostIdAttachments))
                {
                    return hostIdAttachments;
                }

                var crucibleIdAttach = _pathResolver.ResolveCruciblePath(_workspaceId.Value, AttachmentConstants.VirtualAttachmentsMountPath, _taskId);
                if (!string.IsNullOrWhiteSpace(crucibleIdAttach))
                {
                    return crucibleIdAttach;
                }
            }
        }

        var normRootCheck = _workspaceRoot.Replace('\\', '/');
        var canResolveRootViaResolver = !OperatingSystem.IsWindows()
            || (!normRootCheck.StartsWith('/') && !normRootCheck.StartsWith('~'))
            || !string.IsNullOrWhiteSpace(_pathResolver.GetWslDistributionName());

        if (canResolveRootViaResolver)
        {
            var resolvedFromRoot = _pathResolver.ResolveCruciblePath(_workspaceRoot, AttachmentConstants.VirtualAttachmentsMountPath, _taskId);
            if (!string.IsNullOrWhiteSpace(resolvedFromRoot))
            {
                return resolvedFromRoot;
            }
        }

        if (_workspaceId.HasValue)
        {
            var rawIdAttachments = _pathResolver.GetAttachmentsPath(_workspaceId.Value);
            if (!string.IsNullOrWhiteSpace(rawIdAttachments))
            {
                var hostIdAttachments = _pathResolver.ToHostPath(rawIdAttachments);
                return !string.IsNullOrWhiteSpace(hostIdAttachments) ? hostIdAttachments : rawIdAttachments;
            }
        }

        var normRoot = _workspaceRoot.Replace('\\', '/').TrimEnd('/');
        return normRoot.StartsWith('/') && !normRoot.StartsWith("//")
            ? $"{normRoot}/{AttachmentConstants.AttachmentsDirectoryName}"
            : Path.GetFullPath(Path.Combine(_workspaceRoot, AttachmentConstants.AttachmentsDirectoryName));
    }

    private bool TryExtractAttachmentsSubPath(string? path, out string subPath)
    {
        subPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var norm = path.Trim().Replace('\\', '/');
        while (norm.StartsWith("./", StringComparison.Ordinal))
        {
            norm = norm[2..].TrimStart('/');
        }

        string? remainder = null;
        if (norm.Equals(AttachmentConstants.VirtualAttachmentsMountPath, StringComparison.OrdinalIgnoreCase) ||
            norm.Equals("workspace/attachments", StringComparison.OrdinalIgnoreCase))
        {
            remainder = string.Empty;
        }
        else if (norm.StartsWith(AttachmentConstants.VirtualAttachmentsMountPath + "/", StringComparison.OrdinalIgnoreCase))
        {
            remainder = norm[(AttachmentConstants.VirtualAttachmentsMountPath.Length + 1)..];
        }
        else if (norm.StartsWith("workspace/attachments/", StringComparison.OrdinalIgnoreCase))
        {
            remainder = norm["workspace/attachments/".Length..];
        }
        else if (norm.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase) ||
                 norm.StartsWith("workspace/", StringComparison.OrdinalIgnoreCase))
        {
            var afterWorkspace = norm.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase)
                ? norm["/workspace/".Length..]
                : norm["workspace/".Length..];
            if (SandboxPathResolver.TryNormalizeRelativeSegments(afterWorkspace, out var collapsedWorkspaceRel))
            {
                if (collapsedWorkspaceRel.Equals(AttachmentConstants.AttachmentsDirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    remainder = string.Empty;
                }
                else if (collapsedWorkspaceRel.StartsWith(AttachmentConstants.AttachmentsDirectoryName + "/", StringComparison.OrdinalIgnoreCase))
                {
                    remainder = collapsedWorkspaceRel[(AttachmentConstants.AttachmentsDirectoryName.Length + 1)..];
                }
            }
        }
        else if (_workspaceId.HasValue)
        {
            var wksAttachMarker = $".nightcodeforge/wks/{_workspaceId.Value}/";
            var idx = norm.IndexOf(wksAttachMarker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var after = norm[(idx + wksAttachMarker.Length)..];
                if (!SandboxPathResolver.TryNormalizeRelativeSegments(after, out var collapsedAfter))
                {
                    throw new UnauthorizedAccessException(
                        $"Access denied: Path '{path}' resolves outside the allowed workspace sandbox.");
                }

                if (collapsedAfter.Equals(AttachmentConstants.AttachmentsDirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    remainder = string.Empty;
                }
                else if (collapsedAfter.StartsWith(AttachmentConstants.AttachmentsDirectoryName + "/", StringComparison.OrdinalIgnoreCase))
                {
                    remainder = collapsedAfter[(AttachmentConstants.AttachmentsDirectoryName.Length + 1)..];
                }
            }
        }

        if (remainder == null)
        {
            return false;
        }

        if (!SandboxPathResolver.TryNormalizeRelativeSegments(remainder, out subPath))
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Path '{path}' resolves outside the allowed workspace sandbox.");
        }

        return true;
    }

    private static string CombineHostAttachmentPath(string baseDir, string subPath)
    {
        if (string.IsNullOrEmpty(subPath))
        {
            return baseDir;
        }

        bool isUnc = baseDir.StartsWith(@"\\", StringComparison.Ordinal) || baseDir.StartsWith("//", StringComparison.Ordinal);
        bool isUnix = !isUnc && (baseDir.StartsWith('/') || baseDir.StartsWith('~'));
        if (isUnc)
        {
            return $@"{baseDir.Replace('/', '\\').TrimEnd('\\')}\{subPath.Replace('/', '\\').TrimStart('\\')}";
        }
        if (isUnix)
        {
            return $"{baseDir.Replace('\\', '/').TrimEnd('/')}/{subPath.Replace('\\', '/').TrimStart('/')}";
        }
        return Path.GetFullPath(Path.Combine(baseDir, subPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string NormalizeForSandbox(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        if (Path.IsPathRooted(path) && !normalized.StartsWith("//") && !normalized.StartsWith(@"\\") &&
            !normalized.StartsWith("/workspace", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("/clone", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("/tmp", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                normalized = Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // In case of non-standard path format, retain normalized string
            }
        }
        return normalized;
    }
}
