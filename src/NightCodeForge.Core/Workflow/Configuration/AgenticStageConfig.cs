namespace NightCodeForge.Core.Workflow.Configuration;

using NightCodeForge.Core.Domain;

/// <summary>
/// Configuration payload for an Agentic (LLM-driven) stage.
/// </summary>
public class AgenticStageConfig
{
    /// <summary>
    /// Explicit AgentProfile ID to execute this stage (e.g. "task-review-architect", "developer-agent").
    /// If null or empty, falls back to the workspace default or matching stage identifier.
    /// </summary>
    public string? AgentProfileId { get; set; }

    /// <summary>
    /// Custom stage directive (system prompt role and rules). Overrides default stage prompt.
    /// </summary>
    public string? StageDirective { get; set; }

    /// <summary>
    /// Minimum required tool capability mode.
    /// </summary>
    public AgentMode RequiredMode { get; set; } = AgentMode.Full;

    /// <summary>
    /// Target stage ID to loop back to when the agent rejects or requests changes (e.g. "implementation").
    /// </summary>
    public string? LoopBackStageId { get; set; }

    /// <summary>
    /// Maximum retry attempts before marking the stage as Failed.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Optional execution timeout in minutes.
    /// </summary>
    public int? TimeoutMinutes { get; set; }
}
