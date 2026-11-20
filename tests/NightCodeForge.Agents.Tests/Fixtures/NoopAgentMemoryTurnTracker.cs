using NightCodeForge.Agents.Memory;
using NightCodeForge.Core.Memory;

namespace NightCodeForge.Agents.Tests.Fixtures;

/// <summary>Turn tracker stub: empty snapshots, no changes, no notifications. Records the writes it is asked to notify.</summary>
public sealed class NoopAgentMemoryTurnTracker : IAgentMemoryTurnTracker
{
    public List<AgentMemoryWriteEvent> NotifiedWrites { get; } = new();

    public Task<AgentMemorySnapshot> CaptureAsync(int workspaceId, string? repositoryWorkingDirectory, CancellationToken cancellationToken) =>
        Task.FromResult(new AgentMemorySnapshot(workspaceId, new Dictionary<(AgentMemoryScope Scope, string RelativePath), string>()));

    public Task<bool> NotifyWriteAsync(int workspaceId, int taskId, string stageId, string sessionId, AgentMemoryWriteEvent write, CancellationToken cancellationToken)
    {
        NotifiedWrites.Add(write);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<AgentMemoryChangeEvent>> PublishChangesAsync(
        AgentMemorySnapshot before,
        string? repositoryWorkingDirectory,
        int taskId,
        string stageId,
        string sessionId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgentMemoryChangeEvent>>(Array.Empty<AgentMemoryChangeEvent>());
}
