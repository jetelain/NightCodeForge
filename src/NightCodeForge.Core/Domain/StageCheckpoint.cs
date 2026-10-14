namespace NightCodeForge.Core.Domain;

/// <summary>
/// Checkpoint saving conversation memory state and git commit snapshot at stage boundaries.
/// </summary>
public class StageCheckpoint
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public string StageId { get; set; } = string.Empty;
    public string SerializedMemory { get; set; } = string.Empty;
    public string GitCommitHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ForgeTask? ForgeTask { get; set; }
}
