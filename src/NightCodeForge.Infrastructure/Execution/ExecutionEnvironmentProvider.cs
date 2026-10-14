using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
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
    private readonly IWorkspacePathResolver? _pathResolver;
    private readonly IHostSdkDiscoveryService? _sdkDiscoveryService;
    private readonly IGitWorkspaceManager? _gitWorkspaceManager;
    private readonly ConcurrentDictionary<(int WorkspaceId, SandboxSecurityTier SecurityTier, int? TaskId), IExecutionEnvironment> _cachedEnvironments = new();

    [ActivatorUtilitiesConstructor]
    public ExecutionEnvironmentProvider(
        BareHostExecutionEnvironment? bareHost = null,
        IProcessRunner? processRunner = null,
        ILogger<NightCodeForgeContainerExecutor>? containerLogger = null,
        IOptions<ExecutionEnvironmentProviderOptions>? options = null,
        IWorkspacePathResolver? pathResolver = null,
        IHostSdkDiscoveryService? sdkDiscoveryService = null,
        IGitWorkspaceManager? gitWorkspaceManager = null)
        : this(options?.Value, bareHost, processRunner, containerLogger, pathResolver, sdkDiscoveryService, gitWorkspaceManager)
    {
    }

    public ExecutionEnvironmentProvider(
        ExecutionEnvironmentProviderOptions? options,
        BareHostExecutionEnvironment bareHost,
        ILogger<NightCodeForgeContainerExecutor>? containerLogger = null,
        IWorkspacePathResolver? pathResolver = null,
        IHostSdkDiscoveryService? sdkDiscoveryService = null,
        IGitWorkspaceManager? gitWorkspaceManager = null)
        : this(options, bareHost, null, containerLogger, pathResolver, sdkDiscoveryService, gitWorkspaceManager)
    {
    }

    public ExecutionEnvironmentProvider(
        ExecutionEnvironmentProviderOptions? options,
        BareHostExecutionEnvironment? bareHost = null,
        IProcessRunner? processRunner = null,
        ILogger<NightCodeForgeContainerExecutor>? containerLogger = null,
        IWorkspacePathResolver? pathResolver = null,
        IHostSdkDiscoveryService? sdkDiscoveryService = null,
        IGitWorkspaceManager? gitWorkspaceManager = null)
    {
        _processRunner = processRunner ?? new SystemProcessRunner();
        _bareHost = bareHost ?? new BareHostExecutionEnvironment(processRunner: _processRunner, pathResolver: pathResolver);
        _containerLogger = containerLogger ?? NullLogger<NightCodeForgeContainerExecutor>.Instance;
        _options = options ?? new ExecutionEnvironmentProviderOptions();
        _pathResolver = pathResolver;
        _sdkDiscoveryService = sdkDiscoveryService;
        _gitWorkspaceManager = gitWorkspaceManager;
    }

    public IExecutionEnvironment GetEnvironment(
        Workspace workspace,
        SandboxSecurityTier securityTier = SandboxSecurityTier.Standard,
        int? taskId = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var effectiveTaskId = taskId is > 0 ? taskId : null;

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
                        : _pathResolver?.GetAgentWorkspaceRootPath(workspace.Id));

                string? hostWorkspacePath = null;
                string? canonicalClonePath = null;

                if (!string.IsNullOrWhiteSpace(rawPath))
                {
                    var normRaw = rawPath.TrimEnd('/', '\\');
                    var dirName = Path.GetFileName(normRaw);
                    string parentDir;
                    string cloneDir;

                    if (dirName.Equals("clone", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("repo", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("mirror", StringComparison.OrdinalIgnoreCase))
                    {
                        cloneDir = normRaw;
                        parentDir = Path.GetDirectoryName(normRaw) ?? normRaw;
                    }
                    else
                    {
                        parentDir = normRaw;
                        cloneDir = $"{normRaw}/clone";
                    }

                    // In container, agent has "/workspace/clone" and "/workspace/task-nnn" (when working on task).
                    // The host workspace root (parentDir) is mounted to /workspace.
                    hostWorkspacePath = parentDir;
                    canonicalClonePath = cloneDir;
                }

                // When working on a task PWD should be "/workspace/task-nnn", and "/workspace/clone" if not scoped to a task.
                var defaultWorkingDir = effectiveTaskId.HasValue
                    ? $"/workspace/task-{effectiveTaskId.Value}"
                    : "/workspace/clone";

                var targetDistro = workspace.WslDistribution ?? _options.DefaultWslDistribution;
                var sdkMountPaths = _sdkDiscoveryService != null
                    ? _sdkDiscoveryService.DiscoverSdkMountPaths(useWsl ? targetDistro : null)
                    : ContainerExecutorOptions.DefaultSdkMountPaths;

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
                    SdkMountPaths = sdkMountPaths.ToList(),
                    AutoStartContainer = true,
                    IsRestrictedReadOnly = securityTier == SandboxSecurityTier.RestrictedReadOnly,
                    UseWsl = useWsl,
                    WslDistribution = targetDistro,
                    WslExecutable = _options.WslExecutable,
                    WslUserName = _pathResolver?.GetWslUserName(),
                    ExpandTildeInMounts = true,
                    ProtectCloneWorkingTree = true
                };
                return new NightCodeForgeContainerExecutor(options, _processRunner, _containerLogger);
            }),

            _ => _bareHost
        };
    }

    private string ResolveWorktreePath(Workspace workspace, int taskId, string? parentDir = null)
    {
        if (_gitWorkspaceManager != null)
        {
            return _gitWorkspaceManager.GetWorktreePath(workspace, taskId);
        }

        if (_pathResolver != null && !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            return _pathResolver.GetAgentWorktreePath(workspace.Id, taskId);
        }

        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            return parentDir.Contains('/') && !parentDir.Contains('\\')
                ? $"{parentDir.TrimEnd('/')}/task-{taskId}"
                : Path.Combine(parentDir, $"task-{taskId}");
        }

        return _pathResolver != null
            ? _pathResolver.GetAgentWorktreePath(workspace.Id, taskId)
            : $"task-{taskId}";
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
        catch { }

        return false;
    }
}
