namespace NightCodeForge.Core.Domain;

/// <summary>
/// Defines an extensible, multi-stage workflow pipeline for task execution.
/// </summary>
public class WorkflowDefinition
{
    public int Id { get; set; }

    /// <summary>
    /// Friendly display name for the workflow (e.g. "Standard 3-Agent", "Rapid Prototyping").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Unique machine identifier/slug for this workflow (e.g. "standard-3-agent", "security-audit").
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// UI Glyph or Emoji icon representing the workflow (e.g. "⚡", "🔄", "🛡️").
    /// </summary>
    public string Icon { get; set; } = "⚡";

    /// <summary>
    /// Human-readable description of this pipeline's purpose and gates.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Indicates if this workflow is the global system default applied to new tasks.
    /// </summary>
    public bool IsDefault { get; set; } = false;

    /// <summary>
    /// Optional workspace scoping. If null, this workflow is a global template available to all workspaces.
    /// </summary>
    public int? WorkspaceId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Workspace? Workspace { get; set; }
    public ICollection<StageDefinition> Stages { get; set; } = new List<StageDefinition>();
    public ICollection<ForgeTask> Tasks { get; set; } = new List<ForgeTask>();
}
