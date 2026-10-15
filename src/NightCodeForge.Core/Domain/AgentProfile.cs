namespace NightCodeForge.Core.Domain;

/// <summary>
/// Target pinning policy for OllamaRouter reverse proxy dispatch backends.
/// Propagated as X-OllamaRouter-Target HTTP request header.
/// </summary>
public enum OllamaRoutingTarget
{
    Auto = 0,
    Local = 1,
    Remote = 2,
    Cloud = 3
}

/// <summary>
/// Configuration profile specifying model, reasoning parameters, harness, and permissions for an agent.
/// </summary>
public class AgentProfile
{    
    /// <summary>
    /// Generous default turn budget ensuring full agent autonomy without premature stalls.
    /// </summary>
    public const int DefaultMaxTurns = 500;

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
    public int? MaxTurns { get; set; } = DefaultMaxTurns;

    /// <summary>
    /// OllamaRouter target pinning policy (propagated as X-OllamaRouter-Target header).
    /// </summary>
    public OllamaRoutingTarget RoutingTarget { get; set; } = OllamaRoutingTarget.Auto;

    public Workspace? Workspace { get; set; }

    /// <summary>
    /// Default agent profile used for interactive ideation and codebase discovery.
    /// Not scheduled like other pipeline workloads.
    /// </summary>
    public static AgentProfile CreateDefaultIdeationProfile(int? workspaceId = null, string defaultModelName = "") => new()
    {
        Id = "ideation",
        WorkspaceId = workspaceId,
        Name = "Ideation & Codebase Discovery Agent",
        HarnessId = HarnessType.Crucible,
        ModelName = defaultModelName,
        Mode = AgentMode.ReadOnly,
        PermittedTools = DefaultAgentTools.ReadOnly,
        OptimizationFlags = null,
        RoutingTarget = OllamaRoutingTarget.Auto,
        SystemPrompt = @"You are the NightCodeForge Ideation & Codebase Discovery Agent.
Your objective is to explore the codebase, discuss architectural features, and help the developer plan new tasks.
Each proposed task represents a full Pull Request / Merge Request unit of work with autonomous build, test, and code review gates.
Avoid over-decomposing into micro-tasks; structure cohesive PR-sized tasks with sub-tasks and open questions to be answered in the task review process.",
        Temperature = 1,
        ThinkingEffort = ThinkingEffort.Medium,
        MaxTurns = 300
    };

    /// <summary>
    /// Creates the baseline set of 5 standard global agent profiles (WorkspaceId == null)
    /// configured with the designated default model. OptimizationFlags is null by default to inherit global optimizations.
    /// </summary>
    public static IReadOnlyList<AgentProfile> CreateStandardGlobalProfiles(string defaultModelName) => new List<AgentProfile>
    {
        new()
        {
            Id = "triage",
            WorkspaceId = null,
            Name = "Stage 0 Untrusted Intake Triage Agent",
            HarnessId = HarnessType.Crucible,
            ModelName = defaultModelName,
            Mode = AgentMode.ReadOnly,
            SecurityTier = SandboxSecurityTier.RestrictedReadOnly,
            RoutingTarget = OllamaRoutingTarget.Local, // Pinned strictly to local GPU to prevent data leaks
            PermittedTools = DefaultAgentTools.ReadOnly,
            OptimizationFlags = null,
            Temperature = 1.0,
            ThinkingEffort = ThinkingEffort.Medium,
            MaxTurns = 250
        },
        CreateDefaultIdeationProfile(null, defaultModelName),
        new()
        {
            Id = "task-review",
            WorkspaceId = null,
            Name = "ForgeTask Review Agent",
            HarnessId = HarnessType.Crucible,
            ModelName = defaultModelName,
            Mode = AgentMode.Full,
            SecurityTier = SandboxSecurityTier.Standard,
            RoutingTarget = OllamaRoutingTarget.Auto,
            PermittedTools = DefaultAgentTools.Full,
            OptimizationFlags = null,
            Temperature = 1.0,
            ThinkingEffort = ThinkingEffort.High,
            MaxTurns = 500
        },
        new()
        {
            Id = "implementation",
            WorkspaceId = null,
            Name = "Implementation Agent",
            HarnessId = HarnessType.Crucible,
            ModelName = defaultModelName,
            Mode = AgentMode.Full,
            SecurityTier = SandboxSecurityTier.Standard,
            RoutingTarget = OllamaRoutingTarget.Auto,
            PermittedTools = DefaultAgentTools.Full,
            OptimizationFlags = null,
            Temperature = 1.0,
            ThinkingEffort = ThinkingEffort.Medium,
            MaxTurns = 500
        },
        new()
        {
            Id = "code-review-fix",
            WorkspaceId = null,
            Name = "Code Review & Targeted Fix Agent",
            HarnessId = HarnessType.Crucible,
            ModelName = defaultModelName,
            Mode = AgentMode.Review,
            SecurityTier = SandboxSecurityTier.Standard,
            RoutingTarget = OllamaRoutingTarget.Auto,
            PermittedTools = DefaultAgentTools.Review,
            OptimizationFlags = null,
            Temperature = 1.0,
            ThinkingEffort = ThinkingEffort.Medium,
            MaxTurns = 300
        }
    };
}
