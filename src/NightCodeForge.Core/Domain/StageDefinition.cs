namespace NightCodeForge.Core.Domain;

/// <summary>
/// Represents an ordered step within a WorkflowDefinition, referencing a registered C# implementation.
/// </summary>
public class StageDefinition
{
    public int Id { get; set; }
    public int WorkflowId { get; set; }

    /// <summary>
    /// Machine identifier for the stage within this workflow, e.g. "task-review", "implementation".
    /// </summary>
    public string StageId { get; set; } = string.Empty;

    /// <summary>
    /// Display name shown in UI steppers, e.g. "ForgeTask Review", "Deterministic Compilation Gate".
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// UI Glyph or Emoji icon representing this stage (e.g. "🔍", "⚙️", "💻", "🧪", "🧐", "🛡️").
    /// </summary>
    public string Icon { get; set; } = "⚙️";

    /// <summary>
    /// Execution order (1-indexed: 1, 2, 3...).
    /// </summary>
    public int SequenceOrder { get; set; }

    /// <summary>
    /// Execution priority queue tier designated by this stage (Tier 1 CodeReview, Tier 2 Implementation, Tier 3 TaskReview).
    /// Required; stages without a valid designated tier fail workflow validation.
    /// </summary>
    public StageQueueTier QueueTier { get; set; }

    /// <summary>
    /// Operational kind: Agentic (LLM-driven) or Deterministic (CPU tool execution).
    /// </summary>
    public WorkflowStageKind Kind { get; set; } = WorkflowStageKind.Agentic;

    /// <summary>
    /// Identifier or Type name of the C# implementation registered in IWorkflowStageRegistry.
    /// </summary>
    public string CsharpImplementationType { get; set; } = string.Empty;

    /// <summary>
    /// Serialized JSON configuration payload for the C# implementation.
    /// Deserialized to AgenticStageConfig or DeterministicStageConfig.
    /// </summary>
    public string ConfigurationJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public WorkflowDefinition? Workflow { get; set; }
}
