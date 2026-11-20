using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;

namespace NightCodeForge.Agents.Memory;

/// <summary>
/// Symlink-safe memory snapshot differ with a workspace-wide recent-write attribution ledger.
/// Registered as a singleton: the ledger is shared by every agent session of the process.
/// </summary>
public sealed class AgentMemoryMonitorService : IAgentMemoryMonitorService
{
    /// <summary>How long a recorded write keeps shielding identical snapshot diffs from re-notification.</summary>
    public static readonly TimeSpan LedgerRetention = TimeSpan.FromMinutes(10);

    private const int LedgerPruneThreshold = 512;

    private readonly record struct LedgerKey(int WorkspaceId, AgentMemoryScope Scope, string RelativePath)
    {
        public bool Equals(LedgerKey other) =>
            WorkspaceId == other.WorkspaceId &&
            Scope == other.Scope &&
            string.Equals(RelativePath, other.RelativePath, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() =>
            HashCode.Combine(WorkspaceId, Scope, StringComparer.OrdinalIgnoreCase.GetHashCode(RelativePath));
    }

    private readonly record struct LedgerEntry(string? Sha256, DateTimeOffset RecordedAt);

    private readonly IWorkspacePathResolver _pathResolver;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentMemoryMonitorService> _logger;
    private readonly object _ledgerGate = new();
    private readonly Dictionary<LedgerKey, LedgerEntry> _ledger = new();

    public AgentMemoryMonitorService(
        IWorkspacePathResolver pathResolver,
        TimeProvider timeProvider,
        ILogger<AgentMemoryMonitorService> logger)
    {
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AgentMemorySnapshot> CaptureSnapshotAsync(
        int workspaceId,
        string? repositoryWorkingDirectory,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<(AgentMemoryScope Scope, string RelativePath), string>();

        if (workspaceId > 0)
        {
            var memoryDir = _pathResolver.ToHostPath(_pathResolver.GetWorkspaceMemoryPath(workspaceId));
            if (!string.IsNullOrWhiteSpace(memoryDir))
            {
                var wksRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(memoryDir)));
                if (!string.IsNullOrEmpty(wksRoot))
                {
                    foreach (var file in AgentMemoryFileSystem.ListMarkdownFiles(wksRoot, memoryDir))
                    {
                        var relative = $"{AgentMemoryConstants.VirtualLocalMemoryPath}/{Path.GetFileName(file)}";
                        await TryAddAsync(files, AgentMemoryScope.LocalWorkspace, relative, file, cancellationToken);
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(repositoryWorkingDirectory) && Directory.Exists(repositoryWorkingDirectory))
        {
            foreach (var rulesPath in new[] { AgentMemoryConstants.RootRulesFileName, AgentMemoryConstants.AgentsRulesFilePath })
            {
                var full = Path.Combine(repositoryWorkingDirectory, rulesPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full) && !AgentMemoryFileSystem.HasReparsePointInChain(repositoryWorkingDirectory, full))
                {
                    await TryAddAsync(files, AgentMemoryScope.SharedRepository, rulesPath, full, cancellationToken);
                }
            }

            var sharedDir = Path.Combine(repositoryWorkingDirectory, AgentMemoryConstants.SharedMemoryDirectory.Replace('/', Path.DirectorySeparatorChar));
            foreach (var file in AgentMemoryFileSystem.ListMarkdownFiles(repositoryWorkingDirectory, sharedDir))
            {
                var relative = $"{AgentMemoryConstants.SharedMemoryDirectory}/{Path.GetFileName(file)}";
                await TryAddAsync(files, AgentMemoryScope.SharedRepository, relative, file, cancellationToken);
            }
        }

        return new AgentMemorySnapshot(workspaceId, files);
    }

    public IReadOnlyList<AgentMemoryChangeEvent> Diff(AgentMemorySnapshot before, AgentMemorySnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changes = new List<AgentMemoryChangeEvent>();
        foreach (var ((scope, path), sha) in after.Files)
        {
            if (!before.Files.TryGetValue((scope, path), out var previousSha))
            {
                changes.Add(new AgentMemoryChangeEvent(after.WorkspaceId, scope, path, AgentMemoryChangeKind.Created, sha));
            }
            else if (!string.Equals(previousSha, sha, StringComparison.Ordinal))
            {
                changes.Add(new AgentMemoryChangeEvent(after.WorkspaceId, scope, path, AgentMemoryChangeKind.Updated, sha));
            }
        }

        foreach (var (scope, path) in before.Files.Keys)
        {
            if (!after.Files.ContainsKey((scope, path)))
            {
                changes.Add(new AgentMemoryChangeEvent(after.WorkspaceId, scope, path, AgentMemoryChangeKind.Deleted, null));
            }
        }

        return changes
            .Where(c => !IsAlreadyAttributed(c))
            .OrderBy(c => c.Scope)
            .ThenBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool RecordWrite(int workspaceId, AgentMemoryScope scope, string relativePath, string? postWriteSha256)
    {
        if (scope == AgentMemoryScope.None)
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Only memory scopes can be recorded in the attribution ledger.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var now = _timeProvider.GetUtcNow();
        var key = new LedgerKey(workspaceId, scope, relativePath);
        lock (_ledgerGate)
        {
            PruneExpiredLocked(now);
            if (_ledger.TryGetValue(key, out var existing) &&
                now - existing.RecordedAt <= LedgerRetention &&
                string.Equals(existing.Sha256, postWriteSha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _ledger[key] = new LedgerEntry(postWriteSha256, now);
            return true;
        }
    }

    private bool IsAlreadyAttributed(AgentMemoryChangeEvent change)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_ledgerGate)
        {
            return _ledger.TryGetValue(new LedgerKey(change.WorkspaceId, change.Scope, change.RelativePath), out var entry)
                && now - entry.RecordedAt <= LedgerRetention
                && string.Equals(entry.Sha256, change.Sha256, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void PruneExpiredLocked(DateTimeOffset now)
    {
        if (_ledger.Count < LedgerPruneThreshold)
        {
            return;
        }

        foreach (var key in _ledger.Where(kv => now - kv.Value.RecordedAt > LedgerRetention).Select(kv => kv.Key).ToList())
        {
            _ledger.Remove(key);
        }
    }

    private async Task TryAddAsync(
        Dictionary<(AgentMemoryScope Scope, string RelativePath), string> files,
        AgentMemoryScope scope,
        string relativePath,
        string fullPath,
        CancellationToken cancellationToken)
    {
        try
        {
            files[(scope, relativePath)] = await AgentMemoryFileSystem.ComputeSha256Async(fullPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file being replaced concurrently is re-observed by the next snapshot; never fail the agent turn for it.
            _logger.LogWarning(ex, "Skipped memory file '{Path}' in snapshot because it could not be read.", fullPath);
        }
    }
}
