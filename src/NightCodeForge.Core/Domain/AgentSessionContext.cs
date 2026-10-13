using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Domain;

/// <summary>
/// Execution session context provided to an IAgentHarness for executing a stage.
/// </summary>
public class AgentSessionContext
{
    public required Issue Issue { get; init; }
    public Workspace? Workspace { get; init; }
    public required AgentProfile Profile { get; init; }
    public PipelineContext? PipelineContext { get; init; }
    public LocalOptimizationFlags OptimizationFlags { get; init; } = new();
    public string StageId { get; init; } = string.Empty;
    public string BootstrapPrompt { get; init; } = string.Empty;
    public string? SerializedStateJson { get; init; }
    public string WorkspacePath { get; init; } = string.Empty;
    public string? PendingHumanAnswer { get; init; }
    public IReadOnlyList<string>? DiscoveredSymbols { get; init; }
}
