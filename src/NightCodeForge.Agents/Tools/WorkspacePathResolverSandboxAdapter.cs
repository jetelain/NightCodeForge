using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Adapts <see cref="IWorkspacePathResolver"/> and <see cref="CanonicalToolScope"/> to <see cref="ISandboxPathResolver"/>
/// when an explicit <see cref="SandboxScopeDescriptor"/> or <see cref="ISandboxPathResolver"/> is not supplied.
/// </summary>
public sealed class WorkspacePathResolverSandboxAdapter : ISandboxPathResolver
{
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
        var mountTable = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: _workspaceRoot,
            isReadOnly: _isReadOnly,
            taskId: _taskId,
            hostTmpDirectory: workspaceTmpRoot);
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
        ValidateWithinWorkspace(fullPath, path);
        return fullPath;
    }

    public string ResolveBashWorkingDirectory(string? workingDirectory, ExecutionTier targetTier)
    {
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            if (targetTier != ExecutionTier.BareHost &&
                (workingDirectory.StartsWith('/') || workingDirectory.StartsWith('.') || !Path.IsPathRooted(workingDirectory)))
            {
                return workingDirectory;
            }

            return ResolveHostPath(workingDirectory, forWrite: false);
        }

        return targetTier == ExecutionTier.BareHost ? _workspaceRoot : string.Empty;
    }

    public string TranslateBashCommand(string command, ExecutionTier targetTier) => command;

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

    private void ValidateWithinWorkspace(string fullPath, string requestedPath)
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

        var normFullPath = NormalizeForSandbox(fullPath);

        bool isAllowed = false;
        foreach (var root in allowedRoots)
        {
            var normRoot = NormalizeForSandbox(root);
            if (normFullPath.Equals(normRoot, StringComparison.OrdinalIgnoreCase) ||
                normFullPath.StartsWith(normRoot + "/", StringComparison.OrdinalIgnoreCase))
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

