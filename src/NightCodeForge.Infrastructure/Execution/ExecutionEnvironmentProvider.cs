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
    private readonly ConcurrentDictionary<(int WorkspaceId, SandboxSecurityTier SecurityTier), IExecutionEnvironment> _cachedEnvironments = new();

    [ActivatorUtilitiesConstructor]
    public ExecutionEnvironmentProvider(
        BareHostExecutionEnvironment? bareHost = null,
        IProcessRunner? processRunner = null,
        ILogger<NightCodeForgeContainerExecutor>? containerLogger = null,
        IOptions<ExecutionEnvironmentProviderOptions>? options = null,
        IWorkspacePathResolver? pathResolver = null)
        : this(options?.Value, bareHost, processRunner, containerLogger, pathResolver)
    {
    }

    public ExecutionEnvironmentProvider(
        ExecutionEnvironmentProviderOptions? options,
        BareHostExecutionEnvironment bareHost,
        ILogger<NightCodeForgeContainerExecutor>? containerLogger = null,
        IWorkspacePathResolver? pathResolver = null)
        : this(options, bareHost, null, containerLogger, pathResolver)
    {
    }

    public ExecutionEnvironmentProvider(
        ExecutionEnvironmentProviderOptions? options,
        BareHostExecutionEnvironment? bareHost = null,
        IProcessRunner? processRunner = null,
        ILogger<NightCodeForgeContainerExecutor>? containerLogger = null,
        IWorkspacePathResolver? pathResolver = null)
    {
        _bareHost = bareHost ?? new BareHostExecutionEnvironment(pathResolver: pathResolver);
        _processRunner = processRunner ?? new SystemProcessRunner();
        _containerLogger = containerLogger ?? NullLogger<NightCodeForgeContainerExecutor>.Instance;
        _options = options ?? new ExecutionEnvironmentProviderOptions();
        _pathResolver = pathResolver;
    }

    public IExecutionEnvironment GetEnvironment(Workspace workspace, SandboxSecurityTier securityTier = SandboxSecurityTier.Standard)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return workspace.ExecutionMode switch
        {
            ExecutionMode.BareHost => _bareHost,

            ExecutionMode.WslDirect => _cachedEnvironments.GetOrAdd((workspace.Id, securityTier), _ =>
                new WslExecutionEnvironment(
                    distribution: workspace.WslDistribution ?? _options.DefaultWslDistribution,
                    defaultWorkingDirectory: !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)
                        ? workspace.Ext4MirrorPath
                        : workspace.RepositoryPath)),

            ExecutionMode.ContainerSandbox => _cachedEnvironments.GetOrAdd((workspace.Id, securityTier), _ =>
            {
                var useWsl = workspace.UseWslForContainers ||
                             !string.IsNullOrWhiteSpace(workspace.WslDistribution) ||
                             _options.UseWslForContainers ||
                             (_options.AutoDetectWsl && ShouldAutoUseWslForContainers(workspace));

                var containerName = !string.IsNullOrWhiteSpace(workspace.ContainerName)
                    ? workspace.ContainerName
                    : (!string.IsNullOrWhiteSpace(workspace.ContainerImage)
                        ? (workspace.ContainerImage.Contains(':') || workspace.ContainerImage.Contains('/')
                            ? $"ncf-sandbox-{workspace.Id}"
                            : workspace.ContainerImage)
                        : "ncf-sandbox");

                var hostWorkspacePath = !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)
                    ? workspace.Ext4MirrorPath
                    : (!string.IsNullOrWhiteSpace(workspace.RepositoryPath)
                        ? workspace.RepositoryPath
                        : _pathResolver?.GetAgentWorkingClonePath(workspace.Id));

                var options = new ContainerExecutorOptions
                {
                    ContainerNameOrId = containerName,
                    ContainerImage = !string.IsNullOrWhiteSpace(workspace.ContainerImage)
                        ? workspace.ContainerImage
                        : "ubuntu:24.04",
                    HostWorkspacePath = hostWorkspacePath,
                    ContainerWorkspacePath = "/workspace",
                    DefaultWorkingDirectory = "/workspace",
                    MountPackageCaches = true,
                    MountHostSdks = true,
                    AutoStartContainer = true,
                    IsRestrictedReadOnly = securityTier == SandboxSecurityTier.RestrictedReadOnly,
                    UseWsl = useWsl,
                    WslDistribution = workspace.WslDistribution ?? _options.DefaultWslDistribution,
                    WslExecutable = _options.WslExecutable,
                    WslUserName = _pathResolver?.GetWslUserName(),
                    ExpandTildeInMounts = true
                };
                return new NightCodeForgeContainerExecutor(options, _processRunner, _containerLogger);
            }),

            _ => _bareHost
        };
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
