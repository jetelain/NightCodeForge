using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private readonly IGitWorkspaceManager _gitWorkspaceManager;
    private readonly IContainerEnvironmentFactory _containerEnvironmentFactory;
    private readonly ILogger<ExecutionEnvironmentProvider> _logger;
    private readonly ConcurrentDictionary<(int WorkspaceId, SandboxSecurityTier SecurityTier, int? TaskId), IExecutionEnvironment> _cachedEnvironments = new();

    [ActivatorUtilitiesConstructor]
    public ExecutionEnvironmentProvider(
        BareHostExecutionEnvironment bareHost,
        IProcessRunner processRunner,
        IGitWorkspaceManager gitWorkspaceManager,
        IContainerEnvironmentFactory containerEnvironmentFactory,
        ILogger<ExecutionEnvironmentProvider> logger)
    {
        _bareHost = bareHost ?? throw new ArgumentNullException(nameof(bareHost));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _gitWorkspaceManager = gitWorkspaceManager ?? throw new ArgumentNullException(nameof(gitWorkspaceManager));
        _containerEnvironmentFactory = containerEnvironmentFactory ?? throw new ArgumentNullException(nameof(containerEnvironmentFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
                Task.Run(() => _gitWorkspaceManager.EnsureWorktreeRelativePathsAsync(workspace, effectiveTaskId.Value)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to normalize relative worktree paths for Workspace #{WorkspaceId} Task #{TaskId}",
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
                    defaultWorkingDir = _gitWorkspaceManager.GetWorktreePath(workspace, effectiveTaskId.Value);
                }
                else
                {
                    defaultWorkingDir = !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)
                        ? workspace.Ext4MirrorPath
                        : workspace.RepositoryPath;
                }

                return new WslExecutionEnvironment(
                    _processRunner,
                    distribution: workspace.WslDistribution ?? _containerEnvironmentFactory.DefaultWslDistribution,
                    defaultWorkingDirectory: defaultWorkingDir,
                    logger: _logger);
            }),

            ExecutionMode.ContainerSandbox => _cachedEnvironments.GetOrAdd(
                (workspace.Id, securityTier, effectiveTaskId),
                _ => _containerEnvironmentFactory.CreateContainerEnvironment(workspace, securityTier, effectiveTaskId)),

            _ => _bareHost
        };
    }

    /// <inheritdoc />
    public async Task CleanupTaskContainerAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.ExecutionMode != ExecutionMode.ContainerSandbox || taskId <= 0)
        {
            return;
        }

        DiscardCachedTaskEnvironments(workspace.Id, taskId);
        await _containerEnvironmentFactory.RemoveTaskContainerAsync(workspace, taskId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task CleanupWorkspaceContainersAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.ExecutionMode != ExecutionMode.ContainerSandbox)
        {
            return;
        }

        DiscardCachedWorkspaceEnvironments(workspace.Id);

        foreach (var taskId in await ListTaskContainerIdsAsync(workspace, cancellationToken))
        {
            await CleanupTaskContainerAsync(workspace, taskId, cancellationToken);
        }

        await _containerEnvironmentFactory.RemoveBaseContainerAsync(workspace, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<int>> ListTaskContainerIdsAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.ExecutionMode != ExecutionMode.ContainerSandbox)
        {
            return Task.FromResult<IReadOnlyList<int>>([]);
        }

        return _containerEnvironmentFactory.ListTaskContainerIdsAsync(workspace, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> EvictIdleWorkspaceContainersAsync(Workspace workspace, TimeSpan idleTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.ExecutionMode != ExecutionMode.ContainerSandbox)
        {
            return 0;
        }

        var now = DateTime.UtcNow;

        var workspaceKeys = _cachedEnvironments.Keys
            .Where(k => k.WorkspaceId == workspace.Id && k.TaskId == null)
            .ToList();

        if (workspaceKeys.Count == 0)
        {
            return 0;
        }

        var executors = new List<((int WorkspaceId, SandboxSecurityTier SecurityTier, int? TaskId) Key, NightCodeForgeContainerExecutor Executor)>();
        foreach (var key in workspaceKeys)
        {
            if (_cachedEnvironments.TryGetValue(key, out var env) && env is NightCodeForgeContainerExecutor containerExecutor)
            {
                // All security tiers with TaskId == null share the same underlying workspace base container.
                // If any tier is currently executing or active within the idle window, keep the base container alive.
                if (containerExecutor.IsExecuting || (now - containerExecutor.LastExecutionTimestampUtc) < idleTimeout)
                {
                    return 0;
                }

                executors.Add((key, containerExecutor));
            }
        }

        if (executors.Count == 0)
        {
            return 0;
        }

        var anyRemoved = false;
        var minIdleDuration = executors.Min(e => now - e.Executor.LastExecutionTimestampUtc);
        foreach (var (key, _) in executors)
        {
            if (_cachedEnvironments.TryRemove(key, out var removedEnv))
            {
                anyRemoved = true;
                if (removedEnv is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

        if (!anyRemoved)
        {
            return 0;
        }

        await _containerEnvironmentFactory.RemoveBaseContainerAsync(workspace, cancellationToken);
        _logger.LogInformation("Evicted idle workspace container for Workspace #{WorkspaceId} (idle duration: {IdleDuration}).",
            workspace.Id, minIdleDuration);
        return 1;
    }

    private void DiscardCachedWorkspaceEnvironments(int workspaceId)
    {
        foreach (var key in _cachedEnvironments.Keys.Where(k => k.WorkspaceId == workspaceId).ToList())
        {
            if (_cachedEnvironments.TryRemove(key, out var env) && env is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private void DiscardCachedTaskEnvironments(int workspaceId, int taskId)
    {
        foreach (var key in _cachedEnvironments.Keys.Where(k => k.WorkspaceId == workspaceId && k.TaskId == taskId).ToList())
        {
            if (_cachedEnvironments.TryRemove(key, out var env) && env is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
