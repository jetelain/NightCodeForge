using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Identifies the kind of execution sandbox scope.
/// </summary>
public enum SandboxScopeKind
{
    WorkspaceClone = 0,
    TaskWorktree = 1,
    SystemNightwatch = 2
}

/// <summary>
/// Declarative virtual mount entry mapping a virtual sandbox path to its backing Host C# and WSL2 directories
/// without requiring on-disk symlinks.
/// </summary>
public record VirtualMountEntry
{
    public string VirtualSubPath { get; init; } = string.Empty;
    public string VirtualMountPath { get; init; } = "/workspace";
    public string HostDirectoryPath { get; init; } = string.Empty;
    public string WslTargetDirectoryPath { get; init; } = string.Empty;
    public bool IsReadOnly { get; init; }

    public VirtualMountEntry() { }

    public VirtualMountEntry(
        string virtualSubPath,
        string virtualMountPath,
        string hostDirectoryPath,
        string wslTargetDirectoryPath,
        bool isReadOnly = false)
    {
        VirtualSubPath = NormalizeSubPath(virtualSubPath);
        VirtualMountPath = NormalizeVirtualPath(virtualMountPath);
        HostDirectoryPath = hostDirectoryPath ?? string.Empty;
        WslTargetDirectoryPath = !string.IsNullOrWhiteSpace(wslTargetDirectoryPath)
            ? wslTargetDirectoryPath
            : ToDefaultWslPath(hostDirectoryPath);
        IsReadOnly = isReadOnly;
    }

    /// <summary>
    /// Converts a Host directory path (Windows drive path, WSL UNC path, or POSIX path)
    /// into its canonical WSL2 Linux path representation when an explicit WSL target path is omitted.
    /// </summary>
    public static string ToDefaultWslPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim();
        var slashNormalized = trimmed.Replace('\\', '/');

        // WSL UNC path: \\wsl.localhost\<distro>\<rest> or \\wsl$\<distro>\<rest>
        if (slashNormalized.StartsWith("//wsl.localhost/", StringComparison.OrdinalIgnoreCase) ||
            slashNormalized.StartsWith("//wsl$/", StringComparison.OrdinalIgnoreCase))
        {
            var afterHost = slashNormalized[2..]; // "wsl.localhost/<distro>/<rest>"
            var firstSlash = afterHost.IndexOf('/');
            if (firstSlash > 0 && firstSlash < afterHost.Length - 1)
            {
                var afterPrefix = afterHost[(firstSlash + 1)..]; // "<distro>/<rest>"
                var secondSlash = afterPrefix.IndexOf('/');
                if (secondSlash < 0)
                {
                    return "/";
                }

                var rest = afterPrefix[(secondSlash + 1)..].Trim('/');
                return string.IsNullOrEmpty(rest) ? "/" : "/" + rest;
            }
        }

        // Windows drive path: C:\foo\bar or C:/foo/bar or C:
        if (trimmed.Length >= 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':' &&
            (trimmed.Length == 2 || trimmed[2] is '\\' or '/'))
        {
            var drive = char.ToLowerInvariant(trimmed[0]);
            var rest = slashNormalized[2..].Trim('/');
            return string.IsNullOrEmpty(rest) ? $"/mnt/{drive}" : $"/mnt/{drive}/{rest}";
        }

        return slashNormalized;
    }

    public static string NormalizeSubPath(string? subPath)
    {
        if (string.IsNullOrWhiteSpace(subPath))
        {
            return string.Empty;
        }

        return subPath.Trim().Replace('\\', '/').Trim('/');
    }

    public static string NormalizeVirtualPath(string? virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            return "/workspace";
        }

        var normalized = virtualPath.Trim().Replace('\\', '/');
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }
}

/// <summary>
/// Declarative mount table mapping virtual paths under a virtual root (/workspace or /nightcodeforge)
/// to Host C# paths, WSL2 paths, and Docker bind mounts.
/// </summary>
public sealed class SandboxMountTable
{
    public const string WorkspaceVirtualRoot = "/workspace";
    public const string NightwatchVirtualRoot = "/nightcodeforge";
    public const string DefaultNightwatchFallbackWorkingDirectory = "~/.nightcodeforge/nightwatch-root";
    public const string NightwatchFallbackWorkingDirectory = DefaultNightwatchFallbackWorkingDirectory;

    private readonly IReadOnlyList<VirtualMountEntry> _sortedEntries;

    public string VirtualRootPath { get; }
    public IReadOnlyList<VirtualMountEntry> Entries => _sortedEntries;
    public VirtualMountEntry? SandboxTmpMount { get; }
    public string? NonContainerFallbackWorkingDirectory { get; }

    public SandboxMountTable(
        string virtualRootPath,
        IEnumerable<VirtualMountEntry>? entries = null,
        VirtualMountEntry? sandboxTmpMount = null,
        string? nonContainerFallbackWorkingDirectory = null)
    {
        VirtualRootPath = VirtualMountEntry.NormalizeVirtualPath(virtualRootPath);
        _sortedEntries = (entries ?? [])
            .OrderByDescending(e => e.VirtualMountPath.Length)
            .ThenBy(e => e.VirtualMountPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SandboxTmpMount = sandboxTmpMount;
        NonContainerFallbackWorkingDirectory = nonContainerFallbackWorkingDirectory;
    }

    /// <summary>
    /// Matches the longest <see cref="VirtualMountEntry.VirtualMountPath"/> prefix against a normalized virtual path.
    /// </summary>
    public bool TryMatchLongestPrefix(
        string normalizedVirtualPath,
        out VirtualMountEntry? matchedEntry,
        out string relativeRemainder)
    {
        var norm = VirtualMountEntry.NormalizeVirtualPath(normalizedVirtualPath);

        foreach (var entry in _sortedEntries)
        {
            var mountPath = entry.VirtualMountPath;
            if (string.Equals(norm, mountPath, StringComparison.OrdinalIgnoreCase))
            {
                matchedEntry = entry;
                relativeRemainder = string.Empty;
                return true;
            }

            var prefixWithSlash = mountPath.EndsWith('/') ? mountPath : mountPath + "/";
            if (norm.StartsWith(prefixWithSlash, StringComparison.OrdinalIgnoreCase))
            {
                matchedEntry = entry;
                relativeRemainder = norm[prefixWithSlash.Length..];
                return true;
            }
        }

        matchedEntry = null;
        relativeRemainder = string.Empty;
        return false;
    }

    public static SandboxMountTable ForWorkspace(
        string hostWorkingDirectory,
        string? wslWorkingDirectory = null,
        bool isReadOnly = false,
        int? taskId = null,
        string? hostCloneDirectory = null,
        string? wslCloneDirectory = null,
        string? hostTmpDirectory = null,
        string? wslTmpDirectory = null,
        IEnumerable<VirtualMountEntry>? additionalEntries = null)
    {
        var effectiveWslWorking = !string.IsNullOrWhiteSpace(wslWorkingDirectory)
            ? wslWorkingDirectory
            : VirtualMountEntry.ToDefaultWslPath(hostWorkingDirectory);

        var entries = new List<VirtualMountEntry>();

        if (taskId is > 0)
        {
            entries.Add(new VirtualMountEntry(
                virtualSubPath: $"task-{taskId.Value}",
                virtualMountPath: $"{WorkspaceVirtualRoot}/task-{taskId.Value}",
                hostDirectoryPath: hostWorkingDirectory,
                wslTargetDirectoryPath: effectiveWslWorking,
                isReadOnly: isReadOnly));
        }

        if (!string.IsNullOrWhiteSpace(hostCloneDirectory))
        {
            var effectiveWslClone = !string.IsNullOrWhiteSpace(wslCloneDirectory)
                ? wslCloneDirectory
                : VirtualMountEntry.ToDefaultWslPath(hostCloneDirectory);

            entries.Add(new VirtualMountEntry(
                virtualSubPath: "clone",
                virtualMountPath: $"{WorkspaceVirtualRoot}/clone",
                hostDirectoryPath: hostCloneDirectory,
                wslTargetDirectoryPath: effectiveWslClone,
                isReadOnly: isReadOnly));
        }
        else if (taskId == null)
        {
            entries.Add(new VirtualMountEntry(
                virtualSubPath: "clone",
                virtualMountPath: $"{WorkspaceVirtualRoot}/clone",
                hostDirectoryPath: hostWorkingDirectory,
                wslTargetDirectoryPath: effectiveWslWorking,
                isReadOnly: isReadOnly));
        }

        entries.Add(new VirtualMountEntry(
            virtualSubPath: string.Empty,
            virtualMountPath: WorkspaceVirtualRoot,
            hostDirectoryPath: hostWorkingDirectory,
            wslTargetDirectoryPath: effectiveWslWorking,
            isReadOnly: isReadOnly));

        if (additionalEntries != null)
        {
            entries.AddRange(additionalEntries);
        }

        VirtualMountEntry? tmpMount = null;
        if (!string.IsNullOrWhiteSpace(hostTmpDirectory))
        {
            var effectiveWslTmp = !string.IsNullOrWhiteSpace(wslTmpDirectory)
                ? wslTmpDirectory
                : VirtualMountEntry.ToDefaultWslPath(hostTmpDirectory);

            tmpMount = new VirtualMountEntry(
                virtualSubPath: "tmp",
                virtualMountPath: "/tmp",
                hostDirectoryPath: hostTmpDirectory,
                wslTargetDirectoryPath: effectiveWslTmp,
                isReadOnly: isReadOnly);
        }

        return new SandboxMountTable(
            virtualRootPath: WorkspaceVirtualRoot,
            entries: entries,
            sandboxTmpMount: tmpMount);
    }

    public static SandboxMountTable ForNightwatch(
        IEnumerable<VirtualMountEntry> entries,
        string? nonContainerFallbackWorkingDirectory = DefaultNightwatchFallbackWorkingDirectory,
        VirtualMountEntry? sandboxTmpMount = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return new SandboxMountTable(
            virtualRootPath: NightwatchVirtualRoot,
            entries: entries.Select(e => e with { IsReadOnly = true }),
            sandboxTmpMount: sandboxTmpMount,
            nonContainerFallbackWorkingDirectory: nonContainerFallbackWorkingDirectory ?? DefaultNightwatchFallbackWorkingDirectory);
    }

    public static SandboxMountTable ForNightwatch(
        string? hostNcfLogsPath,
        string? wslNcfLogsPath,
        string? hostSkillsPath,
        string? wslSkillsPath,
        string? hostWorkspacesRootPath = null,
        string? wslWorkspacesRootPath = null,
        IEnumerable<VirtualMountEntry>? additionalMounts = null,
        string? nonContainerFallbackWorkingDirectory = DefaultNightwatchFallbackWorkingDirectory)
    {
        var entries = new List<VirtualMountEntry>();

        if (!string.IsNullOrWhiteSpace(hostNcfLogsPath) || !string.IsNullOrWhiteSpace(wslNcfLogsPath))
        {
            entries.Add(new VirtualMountEntry(
                virtualSubPath: "ncf-logs",
                virtualMountPath: $"{NightwatchVirtualRoot}/ncf-logs",
                hostDirectoryPath: hostNcfLogsPath ?? wslNcfLogsPath!,
                wslTargetDirectoryPath: wslNcfLogsPath ?? VirtualMountEntry.ToDefaultWslPath(hostNcfLogsPath),
                isReadOnly: true));
        }

        if (!string.IsNullOrWhiteSpace(hostSkillsPath) || !string.IsNullOrWhiteSpace(wslSkillsPath))
        {
            entries.Add(new VirtualMountEntry(
                virtualSubPath: "skills",
                virtualMountPath: $"{NightwatchVirtualRoot}/skills",
                hostDirectoryPath: hostSkillsPath ?? wslSkillsPath!,
                wslTargetDirectoryPath: wslSkillsPath ?? VirtualMountEntry.ToDefaultWslPath(hostSkillsPath),
                isReadOnly: true));
        }

        if (!string.IsNullOrWhiteSpace(hostWorkspacesRootPath) || !string.IsNullOrWhiteSpace(wslWorkspacesRootPath))
        {
            entries.Add(new VirtualMountEntry(
                virtualSubPath: "wks",
                virtualMountPath: $"{NightwatchVirtualRoot}/wks",
                hostDirectoryPath: hostWorkspacesRootPath ?? wslWorkspacesRootPath!,
                wslTargetDirectoryPath: wslWorkspacesRootPath ?? VirtualMountEntry.ToDefaultWslPath(hostWorkspacesRootPath),
                isReadOnly: true));
        }

        if (additionalMounts != null)
        {
            entries.AddRange(additionalMounts.Select(e => e with { IsReadOnly = true }));
        }

        return ForNightwatch(entries, nonContainerFallbackWorkingDirectory);
    }
}

/// <summary>
/// Strongly-typed execution scope descriptor replacing ad-hoc (Workspace, SecurityTier, TaskId) tuples.
/// Supports WorkspaceClone, TaskWorktree, and SystemNightwatch scopes.
/// </summary>
public sealed record SandboxScopeDescriptor
{
    public const string NightwatchContainerBaseName = "ncf-nightwatch";
    public const string ReadOnlyContainerSuffix = "-ro";

    public SandboxScopeKind ScopeKind { get; init; }
    public string ScopeCacheKey { get; init; }
    public Workspace? Workspace { get; init; }
    public int? WorkspaceId { get; init; }
    public int? TaskId { get; init; }
    public ExecutionMode ExecutionMode { get; init; }
    public SandboxSecurityTier SecurityTier { get; init; }
    public string? WslDistribution { get; init; }
    public string? ContainerImage { get; init; }
    public string? ContainerName { get; init; }
    public bool UseWslForContainers { get; init; }
    public string? RepositoryPath { get; init; }
    public string? Ext4MirrorPath { get; init; }
    public string? HostWorkspacePath { get; init; }
    public string? CanonicalClonePath { get; init; }
    public string VirtualRootPath { get; init; }
    public string DefaultWorkingDirectory { get; init; }
    public SandboxMountTable MountTable { get; init; }

    public bool IsReadOnly => SecurityTier == SandboxSecurityTier.RestrictedReadOnly;

    public SandboxScopeDescriptor(
        SandboxScopeKind scopeKind,
        ExecutionMode executionMode,
        SandboxSecurityTier securityTier,
        SandboxMountTable mountTable,
        int? workspaceId = null,
        int? taskId = null,
        string? wslDistribution = null,
        string? containerImage = null,
        string? containerName = null,
        bool useWslForContainers = false,
        string? repositoryPath = null,
        string? ext4MirrorPath = null,
        string? virtualRootPath = null,
        string? defaultWorkingDirectory = null,
        string? scopeCacheKey = null,
        Workspace? workspace = null,
        string? hostWorkspacePath = null,
        string? canonicalClonePath = null)
    {
        ArgumentNullException.ThrowIfNull(mountTable);
        var effectiveTaskId = taskId is > 0 ? taskId : null;
        var effectiveWorkspaceId = workspaceId ?? workspace?.Id;

        ScopeKind = scopeKind;
        Workspace = workspace;
        WorkspaceId = effectiveWorkspaceId;
        TaskId = effectiveTaskId;
        ExecutionMode = executionMode;
        SecurityTier = securityTier;
        WslDistribution = wslDistribution;
        ContainerImage = containerImage;
        ContainerName = containerName;
        UseWslForContainers = useWslForContainers;
        RepositoryPath = repositoryPath;
        Ext4MirrorPath = ext4MirrorPath;
        HostWorkspacePath = hostWorkspacePath;
        CanonicalClonePath = canonicalClonePath;
        VirtualRootPath = !string.IsNullOrWhiteSpace(virtualRootPath)
            ? VirtualMountEntry.NormalizeVirtualPath(virtualRootPath)
            : (scopeKind == SandboxScopeKind.SystemNightwatch
                ? SandboxMountTable.NightwatchVirtualRoot
                : SandboxMountTable.WorkspaceVirtualRoot);
        DefaultWorkingDirectory = !string.IsNullOrWhiteSpace(defaultWorkingDirectory)
            ? VirtualMountEntry.NormalizeVirtualPath(defaultWorkingDirectory)
            : VirtualRootPath;
        MountTable = mountTable;
        ScopeCacheKey = !string.IsNullOrWhiteSpace(scopeCacheKey)
            ? scopeCacheKey
            : BuildCacheKey(scopeKind, effectiveWorkspaceId, effectiveTaskId, securityTier);
    }

    public static string BuildCacheKey(
        SandboxScopeKind scopeKind,
        int? workspaceId,
        int? taskId,
        SandboxSecurityTier securityTier)
    {
        if (scopeKind == SandboxScopeKind.SystemNightwatch)
        {
            return $"system:nightwatch:tier:{securityTier}";
        }

        var effectiveTaskId = taskId is > 0 ? taskId : null;
        return effectiveTaskId.HasValue
            ? $"ws:{workspaceId ?? 0}:task:{effectiveTaskId.Value}:tier:{securityTier}"
            : $"ws:{workspaceId ?? 0}:tier:{securityTier}";
    }

    public static SandboxScopeDescriptor ForWorkspace(
        Workspace workspace,
        SandboxSecurityTier securityTier,
        int? taskId,
        SandboxMountTable? mountTable,
        string? defaultWorkingDirectory = null,
        string? hostWorkspacePath = null,
        string? canonicalClonePath = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var effectiveTaskId = taskId is > 0 ? taskId : null;
        var scopeKind = effectiveTaskId.HasValue ? SandboxScopeKind.TaskWorktree : SandboxScopeKind.WorkspaceClone;
        var isReadOnly = securityTier == SandboxSecurityTier.RestrictedReadOnly;

        var rawPath = !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)
            ? workspace.Ext4MirrorPath
            : (workspace.RepositoryPath ?? string.Empty);

        var effectiveMountTable = mountTable ?? SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: rawPath,
            wslWorkingDirectory: VirtualMountEntry.ToDefaultWslPath(rawPath),
            isReadOnly: isReadOnly,
            taskId: effectiveTaskId);

        var effectiveHostWorkspacePath = !string.IsNullOrWhiteSpace(hostWorkspacePath)
            ? hostWorkspacePath
            : (mountTable == null ? rawPath : null);

        var effectiveDefaultWorkDir = !string.IsNullOrWhiteSpace(defaultWorkingDirectory)
            ? defaultWorkingDirectory
            : (effectiveTaskId.HasValue
                ? $"{SandboxMountTable.WorkspaceVirtualRoot}/task-{effectiveTaskId.Value}"
                : SandboxMountTable.WorkspaceVirtualRoot);

        return new SandboxScopeDescriptor(
            scopeKind: scopeKind,
            executionMode: workspace.ExecutionMode,
            securityTier: securityTier,
            mountTable: effectiveMountTable,
            workspaceId: workspace.Id,
            taskId: effectiveTaskId,
            wslDistribution: workspace.WslDistribution,
            containerImage: workspace.ContainerImage,
            containerName: workspace.ContainerName,
            useWslForContainers: workspace.UseWslForContainers,
            repositoryPath: workspace.RepositoryPath,
            ext4MirrorPath: workspace.Ext4MirrorPath,
            virtualRootPath: SandboxMountTable.WorkspaceVirtualRoot,
            defaultWorkingDirectory: effectiveDefaultWorkDir,
            workspace: workspace,
            hostWorkspacePath: effectiveHostWorkspacePath,
            canonicalClonePath: canonicalClonePath);
    }

    public static SandboxScopeDescriptor ForWorkspace(
        Workspace workspace,
        SandboxSecurityTier securityTier = SandboxSecurityTier.Standard,
        int? taskId = null,
        string? wslRootPath = null,
        string? clonePath = null,
        string? taskWorktreePath = null,
        string? skillsDirectoryPath = null,
        string? tmpDirectoryPath = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var effectiveTaskId = taskId is > 0 ? taskId : null;
        var isReadOnly = securityTier == SandboxSecurityTier.RestrictedReadOnly;
        var rawRoot = !string.IsNullOrWhiteSpace(wslRootPath)
            ? wslRootPath
            : (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)
                ? workspace.Ext4MirrorPath
                : (workspace.RepositoryPath ?? string.Empty));

        var workingDir = effectiveTaskId.HasValue && !string.IsNullOrWhiteSpace(taskWorktreePath)
            ? taskWorktreePath
            : (!string.IsNullOrWhiteSpace(clonePath) ? clonePath : rawRoot);

        var additionalEntries = new List<VirtualMountEntry>();
        if (!string.IsNullOrWhiteSpace(skillsDirectoryPath))
        {
            additionalEntries.Add(new VirtualMountEntry(
                virtualSubPath: "skills",
                virtualMountPath: $"{SandboxMountTable.WorkspaceVirtualRoot}/skills",
                hostDirectoryPath: skillsDirectoryPath,
                wslTargetDirectoryPath: VirtualMountEntry.ToDefaultWslPath(skillsDirectoryPath),
                isReadOnly: true));
        }

        var mountTable = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: workingDir,
            wslWorkingDirectory: VirtualMountEntry.ToDefaultWslPath(workingDir),
            isReadOnly: isReadOnly,
            taskId: effectiveTaskId,
            hostCloneDirectory: clonePath,
            wslCloneDirectory: VirtualMountEntry.ToDefaultWslPath(clonePath),
            hostTmpDirectory: isReadOnly ? null : tmpDirectoryPath,
            wslTmpDirectory: isReadOnly ? null : VirtualMountEntry.ToDefaultWslPath(tmpDirectoryPath),
            additionalEntries: additionalEntries);

        var defaultWorkDir = effectiveTaskId.HasValue
            ? $"{SandboxMountTable.WorkspaceVirtualRoot}/task-{effectiveTaskId.Value}"
            : (!string.IsNullOrWhiteSpace(clonePath)
                ? $"{SandboxMountTable.WorkspaceVirtualRoot}/clone"
                : SandboxMountTable.WorkspaceVirtualRoot);

        return ForWorkspace(
            workspace,
            securityTier,
            effectiveTaskId,
            mountTable,
            defaultWorkDir,
            hostWorkspacePath: rawRoot,
            canonicalClonePath: clonePath);
    }

    public static SandboxScopeDescriptor ForNightwatch(
        string? hostNcfLogsPath,
        string? wslNcfLogsPath,
        string? hostSkillsPath,
        string? wslSkillsPath,
        string? hostWorkspacesRootPath = null,
        string? wslWorkspacesRootPath = null,
        IEnumerable<VirtualMountEntry>? additionalMounts = null,
        ExecutionMode executionMode = ExecutionMode.ContainerSandbox,
        SandboxSecurityTier securityTier = SandboxSecurityTier.RestrictedReadOnly,
        string? wslDistribution = null,
        string? containerImage = null,
        bool useWslForContainers = false,
        string? containerName = null)
    {
        var mountTable = SandboxMountTable.ForNightwatch(
            hostNcfLogsPath: hostNcfLogsPath,
            wslNcfLogsPath: wslNcfLogsPath,
            hostSkillsPath: hostSkillsPath,
            wslSkillsPath: wslSkillsPath,
            hostWorkspacesRootPath: hostWorkspacesRootPath,
            wslWorkspacesRootPath: wslWorkspacesRootPath,
            additionalMounts: additionalMounts);

        return ForNightwatch(
            mountTable: mountTable,
            executionMode: executionMode,
            securityTier: securityTier,
            wslDistribution: wslDistribution,
            containerImage: containerImage,
            useWslForContainers: useWslForContainers,
            containerName: containerName);
    }

    public static SandboxScopeDescriptor ForNightwatch(
        SandboxMountTable mountTable,
        ExecutionMode executionMode = ExecutionMode.ContainerSandbox,
        SandboxSecurityTier securityTier = SandboxSecurityTier.RestrictedReadOnly,
        string? wslDistribution = null,
        string? containerImage = null,
        bool useWslForContainers = false,
        string? containerName = null)
    {
        ArgumentNullException.ThrowIfNull(mountTable);

        return new SandboxScopeDescriptor(
            scopeKind: SandboxScopeKind.SystemNightwatch,
            executionMode: executionMode,
            securityTier: securityTier,
            mountTable: mountTable,
            workspaceId: null,
            taskId: null,
            wslDistribution: wslDistribution,
            containerImage: containerImage,
            containerName: !string.IsNullOrWhiteSpace(containerName) ? containerName : NightwatchContainerBaseName,
            useWslForContainers: useWslForContainers,
            virtualRootPath: SandboxMountTable.NightwatchVirtualRoot,
            defaultWorkingDirectory: SandboxMountTable.NightwatchVirtualRoot);
    }
}
