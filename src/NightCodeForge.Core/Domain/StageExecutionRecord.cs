namespace NightCodeForge.Core.Domain;

/// <summary>
/// Execution metrics and outcome record for a completed or failed stage.
/// </summary>
public class StageExecutionRecord
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public string StageId { get; set; } = string.Empty;
    public string StageKind { get; set; } = string.Empty;
    public string AgentProfileId { get; set; } = string.Empty;
    public string HarnessId { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public int DurationMs { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public string? TargetRouting { get; set; }
    public StageExecutionStatus Status { get; set; } = StageExecutionStatus.Pending;
    public string? OutputDataJson { get; set; }

    public ForgeTask? ForgeTask { get; set; }
}
