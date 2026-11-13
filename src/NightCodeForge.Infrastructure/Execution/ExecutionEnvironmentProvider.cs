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
/// Singleton provider that asynchronously resolves and caches execution environments
/// (<see cref="BareHostExecutionEnvironment"/>, <see cref="WslExecutionEnvironment"/>, or <see cref="NightCodeForgeContainerExecutor"/>)
/// keyed by <see cref="SandboxScopeDescriptor.ScopeCacheKey"/>.
/// </summary>
public class ExecutionEnvironmentProvider : IExecutionEnvironmentProvider
{
    private sealed record CachedEnvironmentEntry(
        string CacheKey,
        int? WorkspaceId,
        SandboxSecurityTier SecurityTier,
        int? TaskId,
        IExecutionEnvironment Environment,
        SandboxScopeDescriptor? Scope);

    private readonly BareHostExecutionEnvironment _bareHost;
    private readonly IProcessRunner _processRunner;
    private readonly IGitWorkspaceManager _gitWorkspaceManager;
    private readonly IContainerEnvironmentFactory _containerEnvironmentFactory;
    private readonly ILogger<ExecutionEnvironmentProvider> _logger;
    private readonly ConcurrentDictionary<string, CachedEnvironmentEntry> _cachedEnvironments = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new(StringComparer.Ordinal);

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

    /// <inheritdoc />
    public async Task<IExecutionEnvironment> GetEnvironmentAsync(
        SandboxScopeDescriptor scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.ExecutionMode == ExecutionMode.BareHost)
        {
            return _bareHost;
        }

        var effectiveTaskId = scope.TaskId is > 0 ? scope.TaskId : null;
        var cacheKey = !string.IsNullOrWhiteSpace(scope.ScopeCacheKey)
            ? scope.ScopeCacheKey
            : SandboxScopeDescriptor.BuildCacheKey(scope.ScopeKind, scope.WorkspaceId, effectiveTaskId, scope.SecurityTier);

        if (_cachedEnvironments.TryGetValue(cacheKey, out var cached))
        {
            return cached.Environment;
        }

        var keyLock = _keyLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedEnvironments.TryGetValue(cacheKey, out cached))
            {
                return cached.Environment;
            }

            return await CreateAndCacheScopeEnvironmentUnderLockAsync(scope, cacheKey, effectiveTaskId, cancellationToken);
        }
        finally
        {
            keyLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IExecutionEnvironment> GetEnvironmentAsync(
        Workspace workspace,
        SandboxSecurityTier securityTier = SandboxSecurityTier.Standard,
        int? taskId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (workspace.ExecutionMode == ExecutionMode.BareHost)
        {
            return _bareHost;
        }

        var effectiveTaskId = taskId is > 0 ? taskId : null;
        var scopeKind = effectiveTaskId.HasValue ? SandboxScopeKind.TaskWorktree : SandboxScopeKind.WorkspaceClone;
        var cacheKey = SandboxScopeDescriptor.BuildCacheKey(scopeKind, workspace.Id, effectiveTaskId, securityTier);

        if (_cachedEnvironments.TryGetValue(cacheKey, out var cached))
        {
            return cached.Environment;
        }

        var keyLock = _keyLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedEnvironments.TryGetValue(cacheKey, out cached))
            {
                return cached.Environment;
            }

            return await CreateAndCacheWorkspaceEnvironmentUnderLockAsync(
                workspace,
                securityTier,
                cacheKey,
                effectiveTaskId,
                cancellationToken);
        }
        finally
        {
            keyLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IExecutionEnvironment> RecreateEnvironmentContainerAsync(
        SandboxScopeDescriptor scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.ExecutionMode == ExecutionMode.BareHost)
        {
            return _bareHost;
        }

        var effectiveTaskId = scope.TaskId is > 0 ? scope.TaskId : null;
        var cacheKey = !string.IsNullOrWhiteSpace(scope.ScopeCacheKey)
            ? scope.ScopeCacheKey
            : SandboxScopeDescriptor.BuildCacheKey(scope.ScopeKind, scope.WorkspaceId, effectiveTaskId, scope.SecurityTier);

        var keyLock = _keyLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedEnvironments.TryRemove(cacheKey, out var removed) && removed.Environment is IDisposable disposable)
            {
                disposable.Dispose();
            }

            if (scope.ExecutionMode == ExecutionMode.ContainerSandbox)
            {
                await _containerEnvironmentFactory.RemoveContainerAsync(scope, cancellationToken);
            }

            return await CreateAndCacheScopeEnvironmentUnderLockAsync(scope, cacheKey, effectiveTaskId, cancellationToken);
        }
        finally
        {
            keyLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IExecutionEnvironment> RecreateEnvironmentContainerAsync(
        Workspace workspace,
        SandboxSecurityTier securityTier = SandboxSecurityTier.Standard,
        int? taskId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (workspace.ExecutionMode == ExecutionMode.BareHost)
        {
            return _bareHost;
        }

        var effectiveTaskId = taskId is > 0 ? taskId : null;
        var scopeKind = effectiveTaskId.HasValue ? SandboxScopeKind.TaskWorktree : SandboxScopeKind.WorkspaceClone;
        var cacheKey = SandboxScopeDescriptor.BuildCacheKey(scopeKind, workspace.Id, effectiveTaskId, securityTier);

        var keyLock = _keyLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedEnvironments.TryRemove(cacheKey, out var removed) && removed.Environment is IDisposable disposable)
            {
                disposable.Dispose();
            }

            if (workspace.ExecutionMode == ExecutionMode.ContainerSandbox)
            {
                var scope = removed?.Scope ?? _containerEnvironmentFactory.BuildWorkspaceScope(workspace, securityTier, effectiveTaskId);
                await _containerEnvironmentFactory.RemoveContainerAsync(scope, cancellationToken);
            }

            return await CreateAndCacheWorkspaceEnvironmentUnderLockAsync(
                workspace,
                securityTier,
                cacheKey,
                effectiveTaskId,
                cancellationToken);
        }
        finally
        {
            keyLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task CleanupTaskContainerAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.ExecutionMode != ExecutionMode.ContainerSandbox || taskId <= 0)
        {
            return;
        }

        await DiscardCachedTaskEnvironmentsAsync(workspace.Id, taskId, cancellationToken);
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

        await DiscardCachedWorkspaceEnvironmentsAsync(workspace.Id, cancellationToken);

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

        var workspaceEntries = _cachedEnvironments.Values
            .Where(e => e.WorkspaceId == workspace.Id && e.TaskId == null)
            .ToList();

        if (workspaceEntries.Count == 0)
        {
            return 0;
        }

        var executors = new List<(string Key, NightCodeForgeContainerExecutor Executor)>();
        foreach (var entry in workspaceEntries)
        {
            if (entry.Environment is NightCodeForgeContainerExecutor containerExecutor)
            {
                if (containerExecutor.IsExecuting || (now - containerExecutor.LastExecutionTimestampUtc) < idleTimeout)
                {
                    return 0;
                }

                executors.Add((entry.CacheKey, containerExecutor));
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
            if (_cachedEnvironments.TryRemove(key, out var removedEntry))
            {
                anyRemoved = true;
                if (removedEntry.Environment is IDisposable disposable)
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

    private WslExecutionEnvironment CreateWslEnvironmentForWorkspace(Workspace workspace, int? effectiveTaskId)
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
    }

    private WslExecutionEnvironment CreateWslEnvironmentForScope(SandboxScopeDescriptor scope)
    {
        string defaultWorkingDir;
        if (scope.MountTable.TryMatchLongestPrefix(scope.DefaultWorkingDirectory, out var defaultMount, out var rem) &&
            defaultMount != null &&
            !string.IsNullOrWhiteSpace(defaultMount.WslTargetDirectoryPath))
        {
            var baseWsl = defaultMount.WslTargetDirectoryPath.Replace('\\', '/').TrimEnd('/');
            defaultWorkingDir = string.IsNullOrEmpty(rem) ? baseWsl : $"{baseWsl}/{rem}";
        }
        else if (scope.ScopeKind == SandboxScopeKind.SystemNightwatch)
        {
            defaultWorkingDir = scope.MountTable.NonContainerFallbackWorkingDirectory
                ?? SandboxMountTable.NightwatchFallbackWorkingDirectory;
        }
        else
        {
            defaultWorkingDir = !string.IsNullOrWhiteSpace(scope.Ext4MirrorPath)
                ? scope.Ext4MirrorPath
                : (!string.IsNullOrWhiteSpace(scope.RepositoryPath)
                    ? scope.RepositoryPath
                    : scope.DefaultWorkingDirectory);
        }

        return new WslExecutionEnvironment(
            _processRunner,
            distribution: scope.WslDistribution ?? _containerEnvironmentFactory.DefaultWslDistribution,
            defaultWorkingDirectory: defaultWorkingDir,
            logger: _logger);
    }

    private async Task<IExecutionEnvironment> CreateAndCacheScopeEnvironmentUnderLockAsync(
        SandboxScopeDescriptor scope,
        string cacheKey,
        int? effectiveTaskId,
        CancellationToken cancellationToken)
    {
        await EnsureWorktreeNormalizedIfNeededAsync(scope.Workspace, scope.ExecutionMode, effectiveTaskId, cancellationToken);

        IExecutionEnvironment created = scope.ExecutionMode switch
        {
            ExecutionMode.WslDirect => CreateWslEnvironmentForScope(scope),
            ExecutionMode.ContainerSandbox => await _containerEnvironmentFactory.CreateContainerEnvironmentAsync(scope, cancellationToken),
            _ => _bareHost
        };

        if (scope.ExecutionMode is ExecutionMode.WslDirect or ExecutionMode.ContainerSandbox)
        {
            _cachedEnvironments[cacheKey] = new CachedEnvironmentEntry(
                cacheKey,
                scope.WorkspaceId,
                scope.SecurityTier,
                effectiveTaskId,
                created,
                scope);
        }

        return created;
    }

    private async Task<IExecutionEnvironment> CreateAndCacheWorkspaceEnvironmentUnderLockAsync(
        Workspace workspace,
        SandboxSecurityTier securityTier,
        string cacheKey,
        int? effectiveTaskId,
        CancellationToken cancellationToken)
    {
        await EnsureWorktreeNormalizedIfNeededAsync(workspace, workspace.ExecutionMode, effectiveTaskId, cancellationToken);

        IExecutionEnvironment created = workspace.ExecutionMode switch
        {
            ExecutionMode.WslDirect => CreateWslEnvironmentForWorkspace(workspace, effectiveTaskId),
            ExecutionMode.ContainerSandbox => await _containerEnvironmentFactory.CreateContainerEnvironmentAsync(
                workspace,
                securityTier,
                effectiveTaskId,
                cancellationToken),
            _ => _bareHost
        };

        if (workspace.ExecutionMode is ExecutionMode.WslDirect or ExecutionMode.ContainerSandbox)
        {
            var attachedScope = (created as NightCodeForgeContainerExecutor)?.Options.Scope;
            _cachedEnvironments[cacheKey] = new CachedEnvironmentEntry(
                cacheKey,
                workspace.Id,
                securityTier,
                effectiveTaskId,
                created,
                attachedScope);
        }

        return created;
    }

    private async Task EnsureWorktreeNormalizedIfNeededAsync(
        Workspace? workspace,
        ExecutionMode executionMode,
        int? effectiveTaskId,
        CancellationToken cancellationToken)
    {
        if (workspace == null || !effectiveTaskId.HasValue || executionMode == ExecutionMode.BareHost)
        {
            return;
        }

        try
        {
            await _gitWorkspaceManager.EnsureWorktreeRelativePathsAsync(workspace, effectiveTaskId.Value, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to normalize relative worktree paths for Workspace #{WorkspaceId} Task #{TaskId}",
                workspace.Id, effectiveTaskId.Value);
        }
    }

    private async Task DiscardCachedWorkspaceEnvironmentsAsync(int workspaceId, CancellationToken cancellationToken)
    {
        var matchingKeys = _cachedEnvironments.Values
            .Where(e => e.WorkspaceId == workspaceId)
            .Select(e => e.CacheKey)
            .ToList();

        foreach (var key in matchingKeys)
        {
            await RemoveCachedKeyUnderLockAsync(key, cancellationToken);
        }
    }

    private async Task DiscardCachedTaskEnvironmentsAsync(int workspaceId, int taskId, CancellationToken cancellationToken)
    {
        var matchingKeys = _cachedEnvironments.Values
            .Where(e => e.WorkspaceId == workspaceId && e.TaskId == taskId)
            .Select(e => e.CacheKey)
            .ToList();

        foreach (var key in matchingKeys)
        {
            await RemoveCachedKeyUnderLockAsync(key, cancellationToken);
        }
    }

    private async Task RemoveCachedKeyUnderLockAsync(string cacheKey, CancellationToken cancellationToken)
    {
        var keyLock = _keyLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedEnvironments.TryRemove(cacheKey, out var entry) && entry.Environment is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        finally
        {
            keyLock.Release();
        }
    }
}
