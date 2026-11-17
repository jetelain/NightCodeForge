using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
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

    private readonly string _workspaceRoot;
    private readonly int? _workspaceId;
    private readonly int? _taskId;
    private readonly bool _isReadOnly;
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
        if (forWrite && _isReadOnly)
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Write operations are not permitted in read-only scope '{Scope.ScopeCacheKey}'.");
        }

        var fullPath = ResolveRawPath(path, forWrite);
        ValidateWithinWorkspace(fullPath, path, forWrite);
        return fullPath;
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

        var attachEntry = MountTable.Entries.FirstOrDefault(e =>
            string.Equals(e.VirtualMountPath, AttachmentConstants.VirtualAttachmentsMountPath, StringComparison.OrdinalIgnoreCase));
        if (attachEntry == null)
        {
            return command;
        }

        var target = targetTier == ExecutionTier.WslDirect
            ? attachEntry.WslTargetDirectoryPath
            : attachEntry.HostDirectoryPath;
        if (string.IsNullOrWhiteSpace(target))
        {
            return command;
        }

        var normalizedTarget = target.Replace('\\', '/').TrimEnd('/');
        return VirtualAttachmentsPrefixRegex().Replace(command, normalizedTarget);
    }

    private string ResolveRawPath(string path, bool forWrite)
    {
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
