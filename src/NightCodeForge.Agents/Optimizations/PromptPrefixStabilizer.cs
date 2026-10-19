using System.Text;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Optimizations;

public record PromptLayers(
    string Layer1GlobalRules,
    string Layer2StagePersona,
    string Layer3TaskScope);

/// <summary>
/// Enforces the strict 4-layer byte-identical prompt prefix architecture to maximize
/// KV cache hit rate (90%+) on local inference engines (Ollama, llama.cpp, vLLM).
/// </summary>
public class PromptPrefixStabilizer
{
    public const string CanonicalRulesPrompt = @"<rules>
- Use bash for file discovery commands like ls, rg, find, git log.
- Use read to examine files instead of cat or sed.
- Use edit for precise changes; edits[].oldText must match target content.
- When changing multiple separate locations in one file, use a single edit call with multiple entries in edits[].
- Use write only for brand new files or complete rewrites.
- Always execute within your assigned working directory; do not modify files in sibling directories or the canonical clone.
- Be concise and focused in your responses.
</rules>";

    public string BuildLayer1GlobalRules(
        string? projectGuidelines = null,
        string? skillsCatalog = null,
        string? customRulesPrompt = null,
        string? customRolePersona = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(!string.IsNullOrWhiteSpace(customRolePersona)
            ? customRolePersona
            : "You are an expert AI software engineer operating in NightCodeForge.");
        sb.AppendLine(!string.IsNullOrWhiteSpace(customRulesPrompt) ? customRulesPrompt : CanonicalRulesPrompt);

        if (!string.IsNullOrWhiteSpace(projectGuidelines))
        {
            sb.AppendLine(projectGuidelines);
        }

        if (!string.IsNullOrWhiteSpace(skillsCatalog))
        {
            sb.AppendLine(skillsCatalog);
        }

        return sb.ToString().TrimEnd();
    }

    public string BuildLayer2StagePersona(string stagePersonaDirective)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<stage_directives>");
        sb.AppendLine(stagePersonaDirective.Trim());
        sb.AppendLine("</stage_directives>");
        return sb.ToString().TrimEnd();
    }

    public string BuildLayer3TaskScope(
        string taskScopeAndPlan,
        string? workingDirectory = null,
        string? discoveredSymbols = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<task_scope>");
        sb.AppendLine(taskScopeAndPlan.Trim());

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            sb.AppendLine();
            sb.AppendLine("<environment>");
            sb.AppendLine($"- Current Working Directory (PWD): {workingDirectory}");
            sb.AppendLine("- All relative paths, file operations (read, edit, write), bash commands, builds, and test runs execute in this working directory.");
            sb.AppendLine("- The canonical clone on 'main' is read-only. Do not attempt to modify files in other directories.");
            sb.AppendLine("</environment>");
        }

        if (!string.IsNullOrWhiteSpace(discoveredSymbols))
        {
            sb.AppendLine(discoveredSymbols);
        }
        sb.AppendLine("</task_scope>");
        return sb.ToString().TrimEnd();
    }

    public PromptLayers BuildLayers(
        string stagePersonaDirective,
        string taskScopeAndPlan,
        string? projectGuidelines = null,
        string? skillsCatalog = null,
        string? discoveredSymbols = null,
        LocalOptimizationFlags? flags = null,
        string? customRulesPrompt = null,
        string? customRolePersona = null,
        string? workingDirectory = null)
    {
        var layer1 = BuildLayer1GlobalRules(projectGuidelines, skillsCatalog, customRulesPrompt, customRolePersona);
        var layer2 = BuildLayer2StagePersona(stagePersonaDirective);
        var layer3 = BuildLayer3TaskScope(taskScopeAndPlan, workingDirectory, discoveredSymbols);
        return new PromptLayers(layer1, layer2, layer3);
    }

    public string BuildStabilizedPrefix(
        string stagePersonaDirective,
        string taskScopeAndPlan,
        string? projectGuidelines = null,
        string? skillsCatalog = null,
        string? discoveredSymbols = null,
        LocalOptimizationFlags? flags = null,
        string? customRulesPrompt = null,
        string? customRolePersona = null,
        string? workingDirectory = null)
    {
        var layers = BuildLayers(
            stagePersonaDirective,
            taskScopeAndPlan,
            projectGuidelines,
            skillsCatalog,
            discoveredSymbols,
            flags,
            customRulesPrompt,
            customRolePersona,
            workingDirectory);

        var sb = new StringBuilder();
        sb.AppendLine(layers.Layer1GlobalRules);
        sb.AppendLine("---");
        sb.AppendLine(layers.Layer2StagePersona);
        sb.AppendLine("---");
        sb.AppendLine(layers.Layer3TaskScope);
        return sb.ToString().TrimEnd();
    }
}
