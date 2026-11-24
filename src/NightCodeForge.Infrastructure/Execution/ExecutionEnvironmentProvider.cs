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

    private sealed class RefCountedKeyLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int RefCount;
    }

    private sealed class KeyLockLease : IDisposable
    {
        private readonly ExecutionEnvironmentProvider _owner;
        private readonly string _cacheKey;
        private readonly RefCountedKeyLock _entry;
        private int _disposed;

        public KeyLockLease(ExecutionEnvironmentProvider owner, string cacheKey, RefCountedKeyLock entry)
        {
            _owner = owner;
            _cacheKey = cacheKey;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner.ReleaseKeyLock(_cacheKey, _entry, releaseSemaphore: true);
        }
    }

    private readonly BareHostExecutionEnvironment _bareHost;
    private readonly IProcessRunner _processRunner;
    private readonly IGitWorkspaceManager _gitWorkspaceManager;
    private readonly IContainerEnvironmentFactory _containerEnvironmentFactory;
    private readonly ILogger<ExecutionEnvironmentProvider> _logger;
    private readonly ConcurrentDictionary<string, CachedEnvironmentEntry> _cachedEnvironments = new(StringComparer.Ordinal);
    private readonly object _keyLocksSync = new();
    private readonly Dictionary<string, RefCountedKeyLock> _keyLocks = new(StringComparer.Ordinal);

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

        using var _ = await AcquireKeyLockAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (_cachedEnvironments.TryGetValue(cacheKey, out cached))
        {
            return cached.Environment;
        }

        return await CreateAndCacheScopeEnvironmentUnderLockAsync(scope, cacheKey, effectiveTaskId, cancellationToken);
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

        using var _ = await AcquireKeyLockAsync(cacheKey, cancellationToken).ConfigureAwait(false);
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

        using var _ = await AcquireKeyLockAsync(cacheKey, cancellationToken).ConfigureAwait(false);

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

        using var _ = await AcquireKeyLockAsync(cacheKey, cancellationToken).ConfigureAwait(false);

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

    /// <inheritdoc />
    public async Task CleanupTaskContainerAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.ExecutionMode != ExecutionMode.ContainerSandbox || taskId <= 0)
        {
            return;
        }

        using var _ = await AcquireMultiKeyLocksAsync(GetTaskLockKeys(workspace.Id, taskId), cancellationToken).ConfigureAwait(false);
        DiscardMatchingCachedEnvironmentsUnderLock(e => e.WorkspaceId == workspace.Id && e.TaskId == taskId);
        await _containerEnvironmentFactory.RemoveTaskContainerAsync(workspace, taskId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CleanupWorkspaceContainersAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.ExecutionMode != ExecutionMode.ContainerSandbox)
        {
            return;
        }

        var cachedTaskIds = _cachedEnvironments.Values
            .Where(e => e.WorkspaceId == workspace.Id && e.TaskId is > 0)
            .Select(e => e.TaskId!.Value);
        var listedTaskIds = await ListTaskContainerIdsAsync(workspace, cancellationToken).ConfigureAwait(false);
        var allTaskIds = cachedTaskIds.Concat(listedTaskIds).Distinct().ToList();

        foreach (var taskId in allTaskIds)
        {
            await CleanupTaskContainerAsync(workspace, taskId, cancellationToken).ConfigureAwait(false);
        }

        using var _ = await AcquireMultiKeyLocksAsync(GetWorkspaceBaseLockKeys(workspace.Id), cancellationToken).ConfigureAwait(false);
        DiscardMatchingCachedEnvironmentsUnderLock(e => e.WorkspaceId == workspace.Id);
        await _containerEnvironmentFactory.RemoveBaseContainerAsync(workspace, cancellationToken).ConfigureAwait(false);
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

        if (!HasOnlyIdleWorkspaceContainerCandidates(workspace.Id, idleTimeout))
        {
            return 0;
        }

        using var _ = await AcquireMultiKeyLocksAsync(GetWorkspaceBaseLockKeys(workspace.Id), cancellationToken).ConfigureAwait(false);

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

        await _containerEnvironmentFactory.RemoveBaseContainerAsync(workspace, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Evicted idle workspace container for Workspace #{WorkspaceId} (idle duration: {IdleDuration}).",
            workspace.Id, minIdleDuration);
        return 1;
    }

    /// <inheritdoc />
    public async Task CleanupScopeEnvironmentAsync(SandboxScopeDescriptor scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.ExecutionMode == ExecutionMode.BareHost)
        {
            return;
        }

        var effectiveTaskId = scope.TaskId is > 0 ? scope.TaskId : null;
        var cacheKey = !string.IsNullOrWhiteSpace(scope.ScopeCacheKey)
            ? scope.ScopeCacheKey
            : SandboxScopeDescriptor.BuildCacheKey(scope.ScopeKind, scope.WorkspaceId, effectiveTaskId, scope.SecurityTier);

        using var _ = await AcquireKeyLockAsync(cacheKey, cancellationToken).ConfigureAwait(false);

        if (_cachedEnvironments.TryRemove(cacheKey, out var entry) && entry.Environment is IDisposable disposable)
        {
            disposable.Dispose();
        }

        if (scope.ExecutionMode == ExecutionMode.ContainerSandbox)
        {
            await _containerEnvironmentFactory.RemoveContainerAsync(scope, cancellationToken).ConfigureAwait(false);
        }
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

    private bool HasOnlyIdleWorkspaceContainerCandidates(int workspaceId, TimeSpan idleTimeout)
    {
        var now = DateTime.UtcNow;
        var hasAnyContainerExecutor = false;

        foreach (var entry in _cachedEnvironments.Values)
        {
            if (entry.WorkspaceId != workspaceId || entry.TaskId != null)
            {
                continue;
            }

            if (entry.Environment is NightCodeForgeContainerExecutor containerExecutor)
            {
                if (containerExecutor.IsExecuting || (now - containerExecutor.LastExecutionTimestampUtc) < idleTimeout)
                {
                    return false;
                }

                hasAnyContainerExecutor = true;
            }
        }

        return hasAnyContainerExecutor;
    }

    private IEnumerable<string> GetTaskLockKeys(int workspaceId, int taskId)
    {
        foreach (var tier in Enum.GetValues<SandboxSecurityTier>())
        {
            yield return SandboxScopeDescriptor.BuildCacheKey(SandboxScopeKind.TaskWorktree, workspaceId, taskId, tier);
        }

        foreach (var entry in _cachedEnvironments.Values)
        {
            if (entry.WorkspaceId == workspaceId && entry.TaskId == taskId)
            {
                yield return entry.CacheKey;
            }
        }
    }

    private IEnumerable<string> GetWorkspaceBaseLockKeys(int workspaceId)
    {
        foreach (var tier in Enum.GetValues<SandboxSecurityTier>())
        {
            yield return SandboxScopeDescriptor.BuildCacheKey(SandboxScopeKind.WorkspaceClone, workspaceId, null, tier);
        }

        foreach (var entry in _cachedEnvironments.Values)
        {
            if (entry.WorkspaceId == workspaceId && entry.TaskId == null)
            {
                yield return entry.CacheKey;
            }
        }
    }

    private void DiscardMatchingCachedEnvironmentsUnderLock(Func<CachedEnvironmentEntry, bool> predicate)
    {
        var matchingKeys = _cachedEnvironments.Values
            .Where(predicate)
            .Select(e => e.CacheKey)
            .ToList();

        foreach (var key in matchingKeys)
        {
            if (_cachedEnvironments.TryRemove(key, out var entry) && entry.Environment is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private async Task<IDisposable> AcquireMultiKeyLocksAsync(IEnumerable<string> cacheKeys, CancellationToken cancellationToken)
    {
        var orderedKeys = cacheKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var acquired = new List<IDisposable>(orderedKeys.Count);
        try
        {
            foreach (var key in orderedKeys)
            {
                acquired.Add(await AcquireKeyLockAsync(key, cancellationToken).ConfigureAwait(false));
            }
        }
        catch
        {
            for (var i = acquired.Count - 1; i >= 0; i--)
            {
                acquired[i].Dispose();
            }
            throw;
        }

        return new MultiKeyLockLease(acquired);
    }

    private sealed class MultiKeyLockLease(List<IDisposable> leases) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            for (var i = leases.Count - 1; i >= 0; i--)
            {
                leases[i].Dispose();
            }
        }
    }

    private async Task<IDisposable> AcquireKeyLockAsync(string cacheKey, CancellationToken cancellationToken)
    {
        RefCountedKeyLock entry;
        lock (_keyLocksSync)
        {
            if (!_keyLocks.TryGetValue(cacheKey, out entry!))
            {
                entry = new RefCountedKeyLock();
                _keyLocks[cacheKey] = entry;
            }

            entry.RefCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseKeyLock(cacheKey, entry, releaseSemaphore: false);
            throw;
        }

        return new KeyLockLease(this, cacheKey, entry);
    }

    private void ReleaseKeyLock(string cacheKey, RefCountedKeyLock entry, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            entry.Semaphore.Release();
        }

        bool disposeSemaphore = false;
        lock (_keyLocksSync)
        {
            entry.RefCount--;
            if (entry.RefCount == 0 && _keyLocks.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, entry))
            {
                _keyLocks.Remove(cacheKey);
                disposeSemaphore = true;
            }
        }

        if (disposeSemaphore)
        {
            entry.Semaphore.Dispose();
        }
    }
}
