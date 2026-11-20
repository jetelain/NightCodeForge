using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Memory;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents.Memory;

/// <summary>
/// Immediate notification of a host-side memory write (Crucible <c>write</c>/<c>edit</c> tools, ACP host RPC,
/// UI drawer). Records the write in the attribution ledger so the post-turn snapshot diff never notifies it twice
/// nor attributes it to a concurrent task.
/// </summary>
public interface IAgentMemoryWriteNotifier
{
    /// <summary>
    /// Notifies a successful host write (<c>/workspace/memory/x.md</c>, <c>.agents/memory/x.md</c>, <c>AGENTS.md</c>,
    /// optionally prefixed by <c>/workspace/clone/</c> or <c>/workspace/task-{id}/</c>, or an absolute host path).
    /// Events whose scope is <see cref="AgentMemoryScope.None"/> are ignored. Returns true when a notification was emitted.
    /// </summary>
    Task<bool> NotifyWriteAsync(
        int workspaceId,
        int taskId,
        string stageId,
        string sessionId,
        AgentMemoryWriteEvent write,
        CancellationToken cancellationToken);

    /// <summary>Emits notices for changes detected by a snapshot diff, recording them in the ledger first.</summary>
    Task<IReadOnlyList<AgentMemoryChangeEvent>> NotifyChangesAsync(
        int taskId,
        string stageId,
        string sessionId,
        IReadOnlyList<AgentMemoryChangeEvent> changes,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class AgentMemoryWriteNotifier : IAgentMemoryWriteNotifier
{
    private const string WorkspacePrefix = "workspace/";
    private const string CloneDirectoryName = "clone";
    private const string TaskDirectoryPrefix = "task-";
    private const string SharedMemoryRoot = ".agents/";

    private readonly IAgentMemoryMonitorService _monitor;
    private readonly IAgentActivityStreamer _activityStreamer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentMemoryWriteNotifier> _logger;

    public AgentMemoryWriteNotifier(
        IAgentMemoryMonitorService monitor,
        IAgentActivityStreamer activityStreamer,
        TimeProvider timeProvider,
        ILogger<AgentMemoryWriteNotifier> logger)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _activityStreamer = activityStreamer ?? throw new ArgumentNullException(nameof(activityStreamer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> NotifyWriteAsync(
        int workspaceId,
        int taskId,
        string stageId,
        string sessionId,
        AgentMemoryWriteEvent write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var scope = write.Scope;
        if (scope == AgentMemoryScope.None)
        {
            return false;
        }

        var kind = write.Kind;
        var displayPath = ToDisplayPath(scope, write.RequestedPath, write.HostFilePath);
        string? sha = null;
        if (kind != AgentMemoryChangeKind.Deleted)
        {
            sha = !string.IsNullOrWhiteSpace(write.ContentSha256)
                ? write.ContentSha256
                : await AgentMemoryFileSystem.ComputeSha256Async(write.HostFilePath, cancellationToken);
        }

        if (!_monitor.RecordWrite(workspaceId, scope, displayPath, sha))
        {
            return false;
        }

        var change = new AgentMemoryChangeEvent(workspaceId, scope, displayPath, kind, sha);
        await BroadcastAsync(taskId, stageId, sessionId, change, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<AgentMemoryChangeEvent>> NotifyChangesAsync(
        int taskId,
        string stageId,
        string sessionId,
        IReadOnlyList<AgentMemoryChangeEvent> changes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var notified = new List<AgentMemoryChangeEvent>();
        foreach (var change in changes)
        {
            // The ledger is the single arbiter: a concurrent diff that already recorded this change wins.
            if (!_monitor.RecordWrite(change.WorkspaceId, change.Scope, change.RelativePath, change.Sha256))
            {
                continue;
            }

            await BroadcastAsync(taskId, stageId, sessionId, change, cancellationToken);
            notified.Add(change);
        }

        return notified;
    }

    private async Task BroadcastAsync(int taskId, string stageId, string sessionId, AgentMemoryChangeEvent change, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Agent memory {Kind}: {Path} (workspace #{WorkspaceId}, task #{TaskId}).", change.Kind, change.RelativePath, change.WorkspaceId, taskId);
        var evt = AgentMemoryActivity.CreateNotice(sessionId, taskId, stageId, change, _timeProvider.GetUtcNow().UtcDateTime);
        await _activityStreamer.BroadcastAsync(evt, cancellationToken);
    }

    internal static string ToDisplayPath(AgentMemoryScope scope, string requestedPath, string hostFilePath)
    {
        if (scope == AgentMemoryScope.LocalWorkspace)
        {
            return $"{AgentMemoryConstants.VirtualLocalMemoryPath}/{Path.GetFileName(requestedPath.Trim().Replace('\\', '/'))}";
        }

        if (AgentMemoryPathClassifier.TryGetCanonicalSharedRelativePath(requestedPath, repositoryRoot: null, out var canonical) ||
            AgentMemoryPathClassifier.TryGetCanonicalSharedRelativePath(hostFilePath, repositoryRoot: null, out canonical))
        {
            return canonical;
        }

        // Absolute host path outside a .nightcodeforge/wks layout: keep the repository-relative tail.
        var host = hostFilePath.Replace('\\', '/');
        var agentsIdx = host.LastIndexOf("/" + SharedMemoryRoot, StringComparison.OrdinalIgnoreCase);
        if (agentsIdx >= 0)
        {
            var tail = host[(agentsIdx + 1)..];
            return AgentMemoryPathClassifier.TryGetCanonicalSharedRelativePath(tail, repositoryRoot: null, out var canonicalTail)
                ? canonicalTail
                : tail;
        }

        return AgentMemoryConstants.RootRulesFileName;
    }
}
