using System.Text;
using Microsoft.Agents.AI;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Builds a human-readable summary of the Microsoft Agent Framework pipeline configuration
/// (built-in providers enabled/disabled, iteration limits) for full-fidelity transcripts.
/// </summary>
public static class MafConfigurationSummary
{
    public static string Build(HarnessAgentOptions options, string? detectedAtRuntime = null)
    {
        static string State(bool disabled) => disabled ? "disabled" : "enabled";

        var sb = new StringBuilder();
        sb.AppendLine($"- TodoProvider: {State(options.DisableTodoProvider)}");
        sb.AppendLine($"- AgentModeProvider: {State(options.DisableAgentModeProvider)}");
        sb.AppendLine($"- FileMemory: {State(options.DisableFileMemory)}");
        sb.AppendLine($"- AgentSkillsProvider: {State(options.DisableAgentSkillsProvider)} (skills catalog injected via PromptPrefixStabilizer)");
        sb.AppendLine($"- WebSearch (hosted): {State(options.DisableWebSearch)}");
        sb.AppendLine($"- ToolAutoApproval: {State(options.DisableToolAutoApproval)}");
        sb.AppendLine($"- MaximumIterationsPerRequest: {options.MaximumIterationsPerRequest}");
        sb.Append("- Runtime-detected MAF injections: ");
        sb.Append(string.IsNullOrWhiteSpace(detectedAtRuntime) ? "none" : detectedAtRuntime);
        return sb.ToString();
    }
}
