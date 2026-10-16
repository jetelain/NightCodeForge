namespace NightCodeForge.Core.Domain;

/// <summary>
/// Represents a conversation session (autonomous stage run, ideation discovery chat, or ad-hoc consultation),
/// tracking messages, thinking traces, and tool executions.
/// </summary>
public class Conversation
{
    public int Id { get; set; }

    /// <summary>
    /// Nullable link to ForgeTask. Present for StageRun conversations; null for general Workspace ideation sessions.
    /// </summary>
    public int? TaskId { get; set; }

    /// <summary>
    /// Link to Workspace. Required for Ideation sessions; inferred from Task for stage runs.
    /// </summary>
    public int? WorkspaceId { get; set; }

    /// <summary>
    /// Classification of conversation purpose (StageRun, Ideation, AdHoc).
    /// </summary>
    public ConversationType ConversationType { get; set; } = ConversationType.StageRun;

    /// <summary>
    /// Friendly session title for Ideation/AdHoc chats (e.g. "Architecture Review - Auth Subsystem").
    /// </summary>
    public string? Title { get; set; }

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
    public Workspace? Workspace { get; set; }
    public ICollection<ConversationTurn> Turns { get; set; } = new List<ConversationTurn>();
}
