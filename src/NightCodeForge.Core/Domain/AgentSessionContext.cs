using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Domain;

/// <summary>
/// Execution session context provided to an IAgentHarness for executing a stage.
/// </summary>
public class AgentSessionContext
{
    public required ForgeTask ForgeTask { get; init; }
    public Workspace? Workspace { get; init; }
    public required AgentProfile Profile { get; init; }
    public PipelineContext? PipelineContext { get; init; }
    public LocalOptimizationFlags? OptimizationFlags { get; set; }
    public string StageId { get; init; } = string.Empty;
    public string StageDirective { get; init; } = string.Empty;
    public AgentMode RequiredMode { get; init; } = AgentMode.Full;
    public string BootstrapPrompt { get; init; } = string.Empty;
    public string? SerializedStateJson { get; init; }
    public string WorkspacePath { get; init; } = string.Empty;
    public string? PendingHumanAnswer { get; init; }
    public IReadOnlyList<string>? DiscoveredSymbols { get; init; }

    // --- Promoted Typed Properties ---
    public string? TaskMarkdownPath { get; init; }
    public string? ReviewDiffStat { get; init; }
    public string? ApprovedPlan { get; init; }
    public string? ReviewerFeedback { get; init; }
    public IReadOnlyList<string>? PriorDiagnostics { get; init; }
}
