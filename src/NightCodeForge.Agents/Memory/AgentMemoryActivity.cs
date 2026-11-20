using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Memory;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents.Memory;

/// <summary>Builds the <see cref="AgentActivityKind.Notice"/> events announcing agent memory changes.</summary>
public static class AgentMemoryActivity
{
    public const string IdeationActorLabel = "Ideation";
    public const string UserActorLabel = "User";

    public static string BuildActorLabel(int taskId, string stageId) =>
        taskId > 0
            ? $"Task #{taskId} ({stageId})"
            : (string.Equals(stageId, StandardStageIds.Ideation, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(stageId)
                ? IdeationActorLabel
                : stageId);

    public static string BuildSummary(string actor, AgentMemoryChangeEvent change) =>
        $"{actor} {ToVerb(change.Kind)} {change.RelativePath}";

    public static AgentActivityEvent CreateNotice(
        string sessionId,
        int taskId,
        string stageId,
        AgentMemoryChangeEvent change,
        DateTime timestamp)
    {
        ArgumentNullException.ThrowIfNull(change);
        var metadata = new Dictionary<string, object?>
        {
            [AgentActivityMetadataKeys.ActionType] = AgentActivityMetadataKeys.ActionTypeMemoryUpdated,
            [AgentActivityMetadataKeys.WorkspaceId] = change.WorkspaceId,
            [AgentActivityMetadataKeys.MemoryScope] = AgentMemoryConstants.ToMetadataValue(change.Scope),
            [AgentActivityMetadataKeys.MemoryRelativePath] = change.RelativePath,
            [AgentActivityMetadataKeys.MemoryChangeKind] = change.Kind.ToString()
        };

        return new AgentActivityEvent(
            sessionId,
            taskId,
            stageId,
            AgentActivityKind.Notice,
            BuildSummary(BuildActorLabel(taskId, stageId), change),
            timestamp,
            metadata);
    }

    private static string ToVerb(AgentMemoryChangeKind kind) => kind switch
    {
        AgentMemoryChangeKind.Created => "created",
        AgentMemoryChangeKind.Updated => "updated",
        AgentMemoryChangeKind.Deleted => "deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown memory change kind.")
    };
}
