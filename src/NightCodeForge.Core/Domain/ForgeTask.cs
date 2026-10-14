namespace NightCodeForge.Core.Domain;

/// <summary>
/// Represents a task or task processed by NightCodeForge.
/// </summary>
public class ForgeTask
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TaskOverallStatus Status { get; set; } = TaskOverallStatus.Suggested;
    public MergeMode MergeMode { get; set; } = MergeMode.ManualReviewRequired;
    public bool IsWaitingForHuman { get; set; } = false;
    public string? InterruptedAgentProfileId { get; set; }
    public string? InterruptedStageId { get; set; }
    public string CurrentStageId { get; set; } = string.Empty;
    public int Priority { get; set; } = 1;
    public string BaseBranch { get; set; } = "main";
    public string FeatureBranch { get; set; } = string.Empty;
    public string? PipelineDataJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? LastHangfireJobId { get; set; }

    public Workspace? Workspace { get; set; }
    public ICollection<TaskDependency> Dependencies { get; set; } = new List<TaskDependency>();
    public ICollection<TaskDependency> DependentTasks { get; set; } = new List<TaskDependency>();
    public ICollection<Question> Questions { get; set; } = new List<Question>();
    public ICollection<StageCheckpoint> Checkpoints { get; set; } = new List<StageCheckpoint>();
    public ICollection<StageExecutionRecord> ExecutionRecords { get; set; } = new List<StageExecutionRecord>();
    public ICollection<ExecutionLog> ExecutionLogs { get; set; } = new List<ExecutionLog>();
}

/// <summary>
/// Structured backlog proposal emitted by triage or discovery agents via propose_tasks.
/// </summary>
public record TaskProposal(
    string Title,
    string Description,
    string? Type = "feature",
    IReadOnlyList<string>? AffectedFiles = null,
    IReadOnlyList<string>? AcceptanceCriteria = null);

