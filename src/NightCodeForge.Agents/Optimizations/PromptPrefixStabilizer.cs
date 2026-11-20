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
- Use edit for precise, incremental changes; edits[].oldText must match target content.
- When changing multiple separate locations in one file, use a single edit call with multiple entries in edits[].
- Prefer incremental, targeted edit tool calls over dumping entire multi-thousand-line files in a single turn.
- Use write only for brand new files or complete rewrites.
- Always execute within your assigned working directory; do not modify files in sibling directories or the canonical clone, except markdown notes under /workspace/memory/ when that directory is available.
- Be concise and focused in your responses.
- CRITICAL: You are an autonomous coding agent operating in a multi-step pipeline. Do NOT write monologues about what you plan to do next and stop; execute your planned actions immediately via tools until your task is 100% implemented and verified.
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

    /// <summary>
    /// Layer 2: stage directives plus the role-specific <c>memory_guidelines</c> (kept out of Layer 1 so that
    /// Stages 1-3 continue to share a byte-identical Layer 1 prefix).
    /// </summary>
    public string BuildLayer2StagePersona(string stagePersonaDirective, string? memoryGuidelines = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<stage_directives>");
        sb.AppendLine(stagePersonaDirective.Trim());
        sb.AppendLine("</stage_directives>");
        if (!string.IsNullOrWhiteSpace(memoryGuidelines))
        {
            sb.AppendLine(memoryGuidelines.Trim());
        }
        return sb.ToString().TrimEnd();
    }

    public string BuildLayer3TaskScope(
        string taskScopeAndPlan,
        string? workingDirectory = null,
        string? discoveredSymbols = null,
        string? backlogDigest = null,
        string? taskAttachmentsDigest = null,
        bool? supportsVision = null,
        string? workspaceMemory = null,
        string? memoryCatalog = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<task_scope>");
        sb.AppendLine(taskScopeAndPlan.Trim());

        // The backlog changes whenever any task is created or transitioned: it must stay in this layer
        // so that Layer 1 and Layer 2 remain byte-identical across tasks (KV cache prefix).
        if (!string.IsNullOrWhiteSpace(backlogDigest))
        {
            sb.AppendLine();
            sb.AppendLine(backlogDigest.Trim());
        }

        // Local memory files evolve during ideation and pipeline runs: keeping them in Layer 3 never invalidates
        // the Layer 1 and Layer 2 KV cache prefixes.
        if (!string.IsNullOrWhiteSpace(workspaceMemory))
        {
            sb.AppendLine();
            sb.AppendLine(workspaceMemory.Trim());
        }

        if (!string.IsNullOrWhiteSpace(memoryCatalog))
        {
            sb.AppendLine();
            sb.AppendLine(memoryCatalog.Trim());
        }

        if (!string.IsNullOrWhiteSpace(taskAttachmentsDigest))
        {
            sb.AppendLine();
            sb.AppendLine(taskAttachmentsDigest.Trim());
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory) || supportsVision.HasValue)
        {
            sb.AppendLine();
            sb.AppendLine("<environment>");
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                sb.AppendLine($"- Current Working Directory (PWD): {workingDirectory}");
                sb.AppendLine("- All relative paths, file operations (read, edit, write), bash commands, builds, and test runs execute in this working directory.");
                sb.AppendLine("- The canonical clone on 'main' is read-only. Do not attempt to modify files in other directories (the only exception is /workspace/memory/*.md agent memory notes).");
            }
            if (supportsVision == true)
            {
                sb.AppendLine("- Visual Inspection: Vision is ENABLED for the active model. You may call 'read' on image files (.png, .jpg, .jpeg, .webp, .gif) in the repository or /workspace/attachments/ to inspect screenshots, diagrams, and mockups directly.");
            }
            else if (supportsVision == false)
            {
                sb.AppendLine("- Visual Inspection: Vision is NOT supported by the active model. Do not call 'read' on image files.");
            }
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
        string? workingDirectory = null,
        string? backlogDigest = null,
        string? taskAttachmentsDigest = null,
        bool? supportsVision = null,
        string? workspaceMemory = null,
        string? memoryCatalog = null,
        string? memoryGuidelines = null)
    {
        var layer1 = BuildLayer1GlobalRules(projectGuidelines, skillsCatalog, customRulesPrompt, customRolePersona);
        var layer2 = BuildLayer2StagePersona(stagePersonaDirective, memoryGuidelines);
        var layer3 = BuildLayer3TaskScope(taskScopeAndPlan, workingDirectory, discoveredSymbols, backlogDigest, taskAttachmentsDigest, supportsVision, workspaceMemory, memoryCatalog);
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
        string? workingDirectory = null,
        string? backlogDigest = null,
        string? taskAttachmentsDigest = null,
        bool? supportsVision = null,
        string? workspaceMemory = null,
        string? memoryCatalog = null,
        string? memoryGuidelines = null)
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
            workingDirectory,
            backlogDigest,
            taskAttachmentsDigest,
            supportsVision,
            workspaceMemory,
            memoryCatalog,
            memoryGuidelines);

        var sb = new StringBuilder();
        sb.AppendLine(layers.Layer1GlobalRules);
        sb.AppendLine("---");
        sb.AppendLine(layers.Layer2StagePersona);
        sb.AppendLine("---");
        sb.AppendLine(layers.Layer3TaskScope);
        return sb.ToString().TrimEnd();
    }
}
