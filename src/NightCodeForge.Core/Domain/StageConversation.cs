namespace NightCodeForge.Core.Domain;

/// <summary>
/// Represents a stage conversation run or attempt for a task, tracking messages, thinking traces, and tool executions.
/// </summary>
public class StageConversation
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public string StageId { get; set; } = string.Empty;
    public WorkflowStageKind StageKind { get; set; } = WorkflowStageKind.Agentic;
    public int AttemptIndex { get; set; } = 1;
    public string? AgentProfileId { get; set; }
    public string? HarnessId { get; set; }
    public string? ModelName { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public StageExecutionStatus Status { get; set; } = StageExecutionStatus.Running;
    public string? Summary { get; set; }
    public string? TranscriptFileName { get; set; }

    public ForgeTask? ForgeTask { get; set; }
    public ICollection<StageConversationTurn> Turns { get; set; } = new List<StageConversationTurn>();
}
