namespace NightCodeForge.Core.Memory;

/// <summary>Symlink-safe snapshot of memory files: (scope, relative path) -> Sha256.</summary>
public sealed record AgentMemorySnapshot(
    int WorkspaceId,
    IReadOnlyDictionary<(AgentMemoryScope Scope, string RelativePath), string> Files);

/// <summary>
/// Detects memory file changes (pre/post-turn snapshot diffing) and keeps a workspace-wide
/// recent-write attribution ledger so that a single write is never notified twice
/// nor attributed to an unrelated concurrent task.
/// </summary>
public interface IAgentMemoryMonitorService
{
    /// <summary>
    /// Captures a snapshot of the local workspace memory directory and, when <paramref name="repositoryWorkingDirectory"/>
    /// is provided, of <c>AGENTS.md</c> and <c>.agents/memory/*.md</c> of that working directory.
    /// Symlinks / reparse points are skipped.
    /// </summary>
    Task<AgentMemorySnapshot> CaptureSnapshotAsync(
        int workspaceId,
        string? repositoryWorkingDirectory,
        CancellationToken cancellationToken);

    /// <summary>
    /// Diffs two snapshots, excluding changes already recorded in the attribution ledger
    /// (hash matches a ledger entry). Returned changes are not yet notified.
    /// </summary>
    IReadOnlyList<AgentMemoryChangeEvent> Diff(AgentMemorySnapshot before, AgentMemorySnapshot after);

    /// <summary>
    /// Records a host-side write in the attribution ledger. Returns false when the same write
    /// (same hash) was already recorded (duplicate notification must be suppressed).
    /// </summary>
    bool RecordWrite(int workspaceId, AgentMemoryScope scope, string relativePath, string? postWriteSha256);
}
