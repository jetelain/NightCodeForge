using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Execution;

public class ExecutionEnvironmentProviderOptions
{
    public const string SectionName = "ExecutionEnvironment";

    /// <summary>
    /// If true, container operations (e.g. docker exec) are invoked via wsl.exe into a WSL2 environment.
    /// </summary>
    public bool UseWslForContainers { get; set; } = false;

    /// <summary>
    /// Optional default WSL2 distribution name to target when using WSL2.
    /// </summary>
    public string? DefaultWslDistribution { get; set; }

    /// <summary>
    /// Path or name of the WSL2 executable on Windows (defaults to "wsl.exe").
    /// </summary>
    public string WslExecutable { get; set; } = "wsl.exe";

    /// <summary>
    /// If true and running on Windows, automatically routes Docker commands through WSL2 if Docker is not found on Windows PATH.
    /// </summary>
    public bool AutoDetectWsl { get; set; } = true;

    /// <summary>
    /// Additional host SDK directories to mount into containers.
    /// </summary>
    public IList<string> AdditionalSdkMountPaths { get; set; } = new List<string>();
}

/// <summary>
/// Resolves the appropriate execution environment (BareHost, WslDirect, or ContainerSandbox)
/// based on the workspace's configured ExecutionMode.
/// </summary>
public class ExecutionEnvironmentProvider : IExecutionEnvironmentProvider
{
    private readonly BareHostExecutionEnvironment _bareHost;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<NightCodeForgeContainerExecutor> _containerLogger;
    private readonly ExecutionEnvironmentProviderOptions _options;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IHostSdkDiscoveryService _sdkDiscoveryService;
    private readonly IGitWorkspaceManager _gitWorkspaceManager;
    private readonly IHostUserIdentityResolver _userIdentityResolver;
    private readonly ConcurrentDictionary<(int WorkspaceId, SandboxSecurityTier SecurityTier, int? TaskId), IExecutionEnvironment> _cachedEnvironments = new();

    [ActivatorUtilitiesConstructor]
    public ExecutionEnvironmentProvider(
        BareHostExecutionEnvironment bareHost,
        IProcessRunner processRunner,
        IOptions<ExecutionEnvironmentProviderOptions> options,
        IWorkspacePathResolver pathResolver,
        IHostSdkDiscoveryService sdkDiscoveryService,
        IGitWorkspaceManager gitWorkspaceManager,
        ILogger<NightCodeForgeContainerExecutor> containerLogger,
        IHostUserIdentityResolver userIdentityResolver)
    {
        _bareHost = bareHost ?? throw new ArgumentNullException(nameof(bareHost));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value ?? new ExecutionEnvironmentProviderOptions();
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _sdkDiscoveryService = sdkDiscoveryService ?? throw new ArgumentNullException(nameof(sdkDiscoveryService));
        _gitWorkspaceManager = gitWorkspaceManager ?? throw new ArgumentNullException(nameof(gitWorkspaceManager));
        _containerLogger = containerLogger ?? throw new ArgumentNullException(nameof(containerLogger));
        _userIdentityResolver = userIdentityResolver ?? throw new ArgumentNullException(nameof(userIdentityResolver));
    }

    public IExecutionEnvironment GetEnvironment(
        Workspace workspace,
        SandboxSecurityTier securityTier = SandboxSecurityTier.Standard,
        int? taskId = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var effectiveTaskId = taskId is > 0 ? taskId : null;

        // Normalization does blocking file I/O: run it only when a new (non-bare-host) environment
        // is about to be created, not on every cached lookup.
        if (effectiveTaskId.HasValue
            && workspace.ExecutionMode != ExecutionMode.BareHost
            && !_cachedEnvironments.ContainsKey((workspace.Id, securityTier, effectiveTaskId)))
        {
            try
            {
                _gitWorkspaceManager.EnsureWorktreeRelativePathsAsync(workspace, effectiveTaskId.Value).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _containerLogger.LogWarning(ex, "Failed to normalize relative worktree paths for Workspace #{WorkspaceId} Task #{TaskId}",
                    workspace.Id, effectiveTaskId.Value);
            }
        }

        return workspace.ExecutionMode switch
        {
            ExecutionMode.BareHost => _bareHost,

            ExecutionMode.WslDirect => _cachedEnvironments.GetOrAdd((workspace.Id, securityTier, effectiveTaskId), _ =>
            {
                string defaultWorkingDir;
                if (effectiveTaskId.HasValue)
                {
                    defaultWorkingDir = ResolveWorktreePath(workspace, effectiveTaskId.Value);
                }
                else
                {
                    defaultWorkingDir = !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)
                        ? workspace.Ext4MirrorPath
                        : workspace.RepositoryPath;
                }

                return new WslExecutionEnvironment(
                    _processRunner,
                    distribution: workspace.WslDistribution ?? _options.DefaultWslDistribution,
                    defaultWorkingDirectory: defaultWorkingDir);
            }),

            ExecutionMode.ContainerSandbox => _cachedEnvironments.GetOrAdd((workspace.Id, securityTier, effectiveTaskId), _ =>
            {
                var useWsl = workspace.UseWslForContainers ||
                             !string.IsNullOrWhiteSpace(workspace.WslDistribution) ||
                             _options.UseWslForContainers ||
                             (_options.AutoDetectWsl && ShouldAutoUseWslForContainers(workspace));

                var baseContainerName = !string.IsNullOrWhiteSpace(workspace.ContainerName)
                    ? workspace.ContainerName
                    : (!string.IsNullOrWhiteSpace(workspace.ContainerImage)
                        ? (workspace.ContainerImage.Contains(':') || workspace.ContainerImage.Contains('/')
                            ? $"ncf-sandbox-{workspace.Id}"
                            : workspace.ContainerImage)
                        : "ncf-sandbox");

                var containerName = effectiveTaskId.HasValue
                    ? $"{baseContainerName}-task-{effectiveTaskId.Value}"
                    : baseContainerName;

                var rawPath = !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)
                    ? workspace.Ext4MirrorPath
                    : (!string.IsNullOrWhiteSpace(workspace.RepositoryPath)
                        ? workspace.RepositoryPath
                        : _pathResolver.GetAgentWorkspaceRootPath(workspace.Id));

                if (string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath) && workspace.Id > 0)
                {
                    var resolvedClone = _pathResolver.ResolveCruciblePath(workspace.Id, string.Empty);
                    if (Directory.Exists(resolvedClone))
                    {
                        rawPath = _pathResolver.GetAgentWorkingClonePath(workspace.Id);
                    }
                }

                string? hostWorkspacePath = null;
                string? canonicalClonePath = null;

                if (!string.IsNullOrWhiteSpace(rawPath))
                {
                    var normRaw = rawPath.TrimEnd('/', '\\');
                    var isUnixOrWsl = normRaw.StartsWith('/') || normRaw.StartsWith('~') || normRaw.StartsWith('\\');
                    var dirName = Path.GetFileName(normRaw);
                    string parentDir;
                    string? cloneDir;

                    if (dirName.Equals("clone", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("repo", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("mirror", StringComparison.OrdinalIgnoreCase))
                    {
                        cloneDir = normRaw.Replace('\\', '/');
                        parentDir = (Path.GetDirectoryName(normRaw) ?? normRaw).Replace('\\', '/');
                    }
                    else if (Directory.Exists(Path.Combine(normRaw, "clone")))
                    {
                        parentDir = normRaw.Replace('\\', '/');
                        cloneDir = $"{normRaw.Replace('\\', '/')}/clone";
                    }
                    else
                    {
                        parentDir = normRaw.Replace('\\', '/');
                        cloneDir = null;
                    }

                    if (isUnixOrWsl)
                    {
                        if (normRaw.StartsWith('/') && !parentDir.StartsWith('/')) parentDir = "/" + parentDir;
                        if (cloneDir != null && normRaw.StartsWith('/') && !cloneDir.StartsWith('/')) cloneDir = "/" + cloneDir;
                    }

                    // In container, agent has "/workspace/clone" and "/workspace/task-nnn" (when working on task) if cloneDir is present.
                    // If no clone subdirectory exists, the host workspace root (parentDir) is mounted directly to /workspace.
                    hostWorkspacePath = parentDir;
                    canonicalClonePath = cloneDir;
                }

                // When working on a task PWD should be "/workspace/task-nnn", and "/workspace/clone" if clone subdirectory exists, or "/workspace".
                var defaultWorkingDir = effectiveTaskId.HasValue
                    ? $"/workspace/task-{effectiveTaskId.Value}"
                    : (!string.IsNullOrEmpty(canonicalClonePath) ? "/workspace/clone" : "/workspace");

                var targetDistro = workspace.WslDistribution ?? _options.DefaultWslDistribution;
                var sdkMountPaths = _sdkDiscoveryService.DiscoverSdkMountPaths(useWsl ? targetDistro : null);

                var options = new ContainerExecutorOptions
                {
                    ContainerNameOrId = containerName,
                    ContainerImage = !string.IsNullOrWhiteSpace(workspace.ContainerImage)
                        ? workspace.ContainerImage
                        : ContainerExecutorOptions.DefaultAgentBaseImage,
                    HostWorkspacePath = hostWorkspacePath,
                    CanonicalClonePath = canonicalClonePath,
                    ContainerWorkspacePath = "/workspace",
                    DefaultWorkingDirectory = defaultWorkingDir,
                    MountPackageCaches = true,
                    MountHostSdks = true,
                    SdkMountPaths = sdkMountPaths?.ToList() ?? new List<string>(),
                    AutoStartContainer = true,
                    IsRestrictedReadOnly = securityTier == SandboxSecurityTier.RestrictedReadOnly,
                    UseWsl = useWsl,
                    WslDistribution = targetDistro,
                    WslExecutable = _options.WslExecutable,
                    WslUserName = _pathResolver.GetWslUserName(),
                    ExpandTildeInMounts = true,
                    ProtectCloneWorkingTree = true,
                    UserUidGid = _userIdentityResolver.ResolveUserIdentity(useWsl, targetDistro, _options.WslExecutable)
                };
                return new NightCodeForgeContainerExecutor(options, _processRunner, _containerLogger, _userIdentityResolver);
            }),

            _ => _bareHost
        };
    }

    private string ResolveWorktreePath(Workspace workspace, int taskId, string? parentDir = null)
    {
        return _gitWorkspaceManager.GetWorktreePath(workspace, taskId);
    }

    private static bool ShouldAutoUseWslForContainers(Workspace workspace)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        // Explicit environment variable override
        var envVar = Environment.GetEnvironmentVariable("NCF_USE_WSL_DOCKER") ??
                     Environment.GetEnvironmentVariable("NCF_USE_WSL");
        if (string.Equals(envVar, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(envVar, "1", StringComparison.Ordinal))
        {
            return true;
        }

        // If an ext4 mirror path is configured on Windows, it's inside WSL2
        if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            return true;
        }

        // If docker is not on the Windows host PATH, but wsl.exe is available
        if (!IsExecutableOnWindowsPath("docker.exe") && !IsExecutableOnWindowsPath("docker") &&
            (IsExecutableOnWindowsPath("wsl.exe") || File.Exists(Path.Combine(Environment.SystemDirectory, "wsl.exe"))))
        {
            return true;
        }

        return false;
    }

    private static bool IsExecutableOnWindowsPath(string executable)
    {
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathEnv)) return false;

            var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in paths)
            {
                var full = Path.Combine(dir.Trim(), executable);
                if (File.Exists(full)) return true;
            }
        }
        catch (Exception)
        {
            // Expected when environment variables or PATH entries contain invalid characters or restricted permissions
        }

        return false;
    }
}
