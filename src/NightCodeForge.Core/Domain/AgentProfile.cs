namespace NightCodeForge.Core.Domain;

/// <summary>
/// Configuration profile specifying model, reasoning parameters, harness, and permissions for an agent.
/// </summary>
public class AgentProfile
{
    public string Id { get; set; } = string.Empty;
    public int? WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string HarnessId { get; set; } = HarnessType.Crucible;
    public string ModelName { get; set; } = string.Empty;
    public ThinkingEffort ThinkingEffort { get; set; } = ThinkingEffort.None;
    public int? ThinkingTokenBudget { get; set; }
    public double? Temperature { get; set; }
    public string? HarnessConfigurationJson { get; set; }
    public LocalOptimizationFlags? OptimizationFlags { get; set; }
    public IReadOnlyList<string>? PermittedTools { get; set; }
    public Dictionary<string, string>? StageDirectives { get; set; }
    public AgentMode Mode { get; set; } = AgentMode.Full;
    public SandboxSecurityTier SecurityTier { get; set; } = SandboxSecurityTier.Standard;
    public string? SystemPrompt { get; set; }
    public int? MaxTurns { get; set; }

    public Workspace? Workspace { get; set; }

    /// <summary>
    /// Default agent profile used for interactive ideation and codebase discovery.
    /// Not scheduled like other pipeline workloads.
    /// </summary>
    public static AgentProfile CreateDefaultIdeationProfile(int? workspaceId = null) => new()
    {
        Id = "ideation",
        WorkspaceId = workspaceId,
        Name = "Ideation & Codebase Discovery Agent",
        HarnessId = HarnessType.Crucible,
        ModelName = "", // Defaults to LlmProviderOptions.ModelId from appsettings.json
        Mode = AgentMode.ReadOnly,
        PermittedTools = new[] { "read", "bash", "web_search", "propose_tasks" },
        SystemPrompt = @"You are the NightCodeForge Ideation & Codebase Discovery Agent.
Your objective is to explore the codebase, discuss architectural features, and help the developer plan new tasks.",
        Temperature = 0.3
    };
}
