using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;

namespace NightCodeForge.Agents.Memory;

/// <summary>
/// Pre/post-turn memory tracking used by every harness: captures a snapshot before a turn or stage and, afterwards,
/// notifies the changes that no immediate host write hook has already attributed.
/// </summary>
public interface IAgentMemoryTurnTracker
{
    Task<AgentMemorySnapshot> CaptureAsync(int workspaceId, string? repositoryWorkingDirectory, CancellationToken cancellationToken);

    /// <summary>Immediate notification of a host-side memory write (Crucible tools, ACP host RPC).</summary>
    Task<bool> NotifyWriteAsync(int workspaceId, int taskId, string stageId, string sessionId, AgentMemoryWriteEvent write, CancellationToken cancellationToken);

    /// <summary>Diffs against a fresh snapshot, excludes ledger-attributed writes and broadcasts the remaining changes.</summary>
    Task<IReadOnlyList<AgentMemoryChangeEvent>> PublishChangesAsync(
        AgentMemorySnapshot before,
        string? repositoryWorkingDirectory,
        int taskId,
        string stageId,
        string sessionId,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class AgentMemoryTurnTracker : IAgentMemoryTurnTracker
{
    private readonly IAgentMemoryMonitorService _monitor;
    private readonly IAgentMemoryWriteNotifier _notifier;

    public AgentMemoryTurnTracker(IAgentMemoryMonitorService monitor, IAgentMemoryWriteNotifier notifier)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
    }

    public Task<AgentMemorySnapshot> CaptureAsync(int workspaceId, string? repositoryWorkingDirectory, CancellationToken cancellationToken) =>
        _monitor.CaptureSnapshotAsync(workspaceId, repositoryWorkingDirectory, cancellationToken);

    public Task<bool> NotifyWriteAsync(int workspaceId, int taskId, string stageId, string sessionId, AgentMemoryWriteEvent write, CancellationToken cancellationToken) =>
        _notifier.NotifyWriteAsync(workspaceId, taskId, stageId, sessionId, write, cancellationToken);

    public async Task<IReadOnlyList<AgentMemoryChangeEvent>> PublishChangesAsync(
        AgentMemorySnapshot before,
        string? repositoryWorkingDirectory,
        int taskId,
        string stageId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(before);
        var after = await _monitor.CaptureSnapshotAsync(before.WorkspaceId, repositoryWorkingDirectory, cancellationToken);
        var changes = _monitor.Diff(before, after);
        return changes.Count == 0
            ? changes
            : await _notifier.NotifyChangesAsync(taskId, stageId, sessionId, changes, cancellationToken);
    }
}
