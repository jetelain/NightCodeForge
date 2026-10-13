namespace NightCodeForge.Core.Domain;

/// <summary>
/// Represents a task or issue processed by NightCodeForge.
/// </summary>
public class Issue
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public IssueOverallStatus Status { get; set; } = IssueOverallStatus.Pending;
    public string CurrentStageId { get; set; } = string.Empty;
    public int Priority { get; set; } = 1;
    public string BaseBranch { get; set; } = "main";
    public string FeatureBranch { get; set; } = string.Empty;
    public string? PipelineDataJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? LastHangfireJobId { get; set; }

    public Workspace? Workspace { get; set; }
    public ICollection<IssueDependency> Dependencies { get; set; } = new List<IssueDependency>();
    public ICollection<IssueDependency> DependentIssues { get; set; } = new List<IssueDependency>();
    public ICollection<Question> Questions { get; set; } = new List<Question>();
    public ICollection<StageCheckpoint> Checkpoints { get; set; } = new List<StageCheckpoint>();
    public ICollection<StageExecutionRecord> ExecutionRecords { get; set; } = new List<StageExecutionRecord>();
    public ICollection<ExecutionLog> ExecutionLogs { get; set; } = new List<ExecutionLog>();
}
