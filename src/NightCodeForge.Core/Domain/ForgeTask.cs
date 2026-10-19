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
    public int Priority { get; set; } = TaskPriority.Normal;
    public string BaseBranch { get; set; } = "main";
    public string FeatureBranch { get; set; } = string.Empty;
    public string? PipelineDataJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? LastHangfireJobId { get; set; }
    public string? SquashCommitSha { get; set; }
    public int? WorkflowId { get; set; }
    public int Version { get; set; } = 1;

    public Workspace? Workspace { get; set; }
    public WorkflowDefinition? Workflow { get; set; }
    public ICollection<TaskDependency> Dependencies { get; set; } = new List<TaskDependency>();
    public ICollection<TaskDependency> DependentTasks { get; set; } = new List<TaskDependency>();
    public ICollection<Question> Questions { get; set; } = new List<Question>();
    public ICollection<StageCheckpoint> Checkpoints { get; set; } = new List<StageCheckpoint>();
    public ICollection<StageExecutionRecord> ExecutionRecords { get; set; } = new List<StageExecutionRecord>();
    public ICollection<ExecutionLog> ExecutionLogs { get; set; } = new List<ExecutionLog>();
    public ICollection<Conversation> Conversations { get; set; } = new List<Conversation>();
    public ICollection<ForgeTaskHistory> History { get; set; } = new List<ForgeTaskHistory>();
}

/// <summary>
/// Structured backlog proposal emitted by triage or discovery agents via propose_tasks.
/// </summary>
public record TaskProposal
{
    [System.Text.Json.Serialization.JsonConstructor]
    public TaskProposal(
        string title,
        string description,
        string? type = "feature",
        IReadOnlyList<string>? affectedFiles = null,
        IReadOnlyList<string>? acceptanceCriteria = null,
        int priority = TaskPriority.Normal,
        IReadOnlyList<string>? dependsOnTitles = null)
    {
        Title = title;
        Description = description;
        Type = type;
        AffectedFiles = affectedFiles;
        AcceptanceCriteria = acceptanceCriteria;
        Priority = priority;
        DependsOnTitles = dependsOnTitles;
    }

    public TaskProposal(string title, string description, int priority, IReadOnlyList<string>? dependsOnTitles = null)
        : this(title, description, "feature", null, null, priority, dependsOnTitles)
    {
    }

    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? Type { get; init; } = "feature";
    public IReadOnlyList<string>? AffectedFiles { get; init; }
    public IReadOnlyList<string>? AcceptanceCriteria { get; init; }
    public int Priority { get; init; } = TaskPriority.Normal;
    public IReadOnlyList<string>? DependsOnTitles { get; init; }
}

