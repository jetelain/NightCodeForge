using System.Text;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Optimizations;

public record PromptLayers(
    string Layer1GlobalRules,
    string Layer2StagePersona,
    string Layer3IssueScope);

/// <summary>
/// Enforces the strict 4-layer byte-identical prompt prefix architecture to maximize
/// KV cache hit rate (90%+) on local inference engines (Ollama, llama.cpp, vLLM).
/// </summary>
public class PromptPrefixStabilizer
{
    public const string CanonicalToolDefinitionsPrompt = @"<tools>
- read: Read file contents with line slicing (path, offset, limit)
- edit: Make precise file edits with exact search-and-replace, supporting multiple disjoint edits in one turn (path, edits: [{ oldText, newText }])
- write: Create new files or completely rewrite files (path, content)
- bash: Execute shell commands, builds, and file discovery (command, timeout, working_directory)
- ask: Ask clarifying questions to the user when blocked (prompt, rationale, options)
- web_search: Search technical web documentation for APIs, errors, or libraries (query, max_results)
- propose_issues: Propose structured backlog items (issues: [{ title, description, type }])
</tools>";

    public const string CanonicalRulesPrompt = @"<rules>
- Use bash for file discovery commands like ls, rg, find, git log.
- Use read to examine files instead of cat or sed.
- Use edit for precise changes; edits[].oldText must match target content.
- When changing multiple separate locations in one file, use a single edit call with multiple entries in edits[].
- Use write only for brand new files or complete rewrites.
- Be concise and focused in your responses.
</rules>";

    public string BuildStabilizedPrefix(
        string stagePersonaDirective,
        string issueScopeAndPlan,
        string? projectGuidelines = null,
        string? skillsCatalog = null,
        string? discoveredSymbols = null,
        LocalOptimizationFlags? flags = null,
        string? customToolsPrompt = null,
        string? customRulesPrompt = null)
    {
        var sb = new StringBuilder();

        // LAYER 1: Immutable Global Rules & Guidelines (Byte-Identical across ALL turns & issues)
        sb.AppendLine("You are an expert AI software engineer operating in NightCodeForge.");
        sb.AppendLine(!string.IsNullOrWhiteSpace(customToolsPrompt) ? customToolsPrompt : CanonicalToolDefinitionsPrompt);
        sb.AppendLine(!string.IsNullOrWhiteSpace(customRulesPrompt) ? customRulesPrompt : CanonicalRulesPrompt);

        if (!string.IsNullOrWhiteSpace(projectGuidelines))
        {
            sb.AppendLine(projectGuidelines);
        }

        if (!string.IsNullOrWhiteSpace(skillsCatalog))
        {
            sb.AppendLine(skillsCatalog);
        }

        sb.AppendLine("---");

        // LAYER 2: Stage-Specific Persona & Directives (Byte-Identical across turns of that stage)
        sb.AppendLine("<stage_directives>");
        sb.AppendLine(stagePersonaDirective.Trim());
        sb.AppendLine("</stage_directives>");

        sb.AppendLine("---");

        // LAYER 3: Dynamic Issue Scope & Artifacts (Stable per issue stage)
        sb.AppendLine("<issue_scope>");
        sb.AppendLine(issueScopeAndPlan.Trim());

        if (!string.IsNullOrWhiteSpace(discoveredSymbols))
        {
            sb.AppendLine(discoveredSymbols);
        }
        sb.AppendLine("</issue_scope>");

        return sb.ToString().TrimEnd();
    }
}
