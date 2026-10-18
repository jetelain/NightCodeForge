namespace NightCodeForge.Core.Domain;

/// <summary>
/// Immutable snapshot representing a discrete state or specification mutation of a ForgeTask.
/// </summary>
public class ForgeTaskHistory
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int Version { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TaskOverallStatus Status { get; set; }
    public int Priority { get; set; }
    public MergeMode MergeMode { get; set; }
    public string BaseBranch { get; set; } = string.Empty;
    public string? FeatureBranch { get; set; }
    public string? CurrentStageId { get; set; }
    public string SerializedDependencies { get; set; } = "[]";
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
    public string ChangedBy { get; set; } = string.Empty;
    public string? ChangeReason { get; set; }

    public ForgeTask? ForgeTask { get; set; }
}
