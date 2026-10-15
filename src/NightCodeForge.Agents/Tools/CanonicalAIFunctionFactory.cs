using System.ComponentModel;
using Microsoft.Extensions.AI;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Factory that creates strongly-typed Microsoft.Extensions.AI AIFunction instances
/// directly from CanonicalTools methods with complete JSON parameter schemas and descriptions.
/// Eliminates dummy stub delegates and manual prompt-injected tool XML.
/// </summary>
public static class CanonicalAIFunctionFactory
{
    public static IReadOnlyList<AIFunction> CreateTools(CanonicalTools tools, IReadOnlyList<string>? permittedTools = null)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var allTools = new List<AIFunction>
        {
            AIFunctionFactory.Create(
                ([Description("Workspace-relative path to read")] string path,
                 [Description("1-indexed start line offset")] int? offset = null,
                 [Description("Maximum lines to read")] int? limit = null,
                 CancellationToken ct = default) => tools.ReadAsync(path, offset, limit, ct),
                "read",
                "Reads a file with line numbers and optional line-based pagination."),

            AIFunctionFactory.Create(
                ([Description("Workspace-relative path to write")] string path,
                 [Description("Full file content")] string content,
                 CancellationToken ct = default) => tools.WriteAsync(path, content, ct),
                "write",
                "Creates or overwrites a workspace file."),

            AIFunctionFactory.Create(
                ([Description("Workspace-relative path to edit")] string path,
                 [Description("One or more disjoint edits applied in order; each oldText must match the file exactly once")] IReadOnlyList<EditChunk>? edits = null,
                 [Description("Shortcut for a single edit: exact character sequence to be replaced")] string? old_text = null,
                 [Description("Shortcut for a single edit: replacement character sequence")] string? new_text = null,
                 CancellationToken ct = default) =>
                {
                    if (edits is { Count: > 0 })
                    {
                        return tools.EditAsync(path, edits, ct);
                    }

                    if (old_text != null && new_text != null)
                    {
                        return tools.EditAsync(path, old_text, new_text, ct);
                    }

                    return Task.FromResult("Error: provide 'edits' (array of { oldText, newText }) or both 'old_text' and 'new_text'.");
                },
                "edit",
                "Replaces exact text snippets in a workspace file. Use 'edits' for one or several disjoint replacements in a single call."),

            AIFunctionFactory.Create(
                ([Description("Shell command line to execute")] string command,
                 [Description("Timeout in seconds")] int? timeout = null,
                 [Description("Working directory relative to repo root")] string? working_directory = null,
                 CancellationToken ct = default) => tools.BashAsync(command, timeout, working_directory, ct),
                "bash",
                "Executes a bash or shell command within the workspace sandbox."),

            AIFunctionFactory.Create(
                ([Description("Exact name of the skill to load from the <skills> catalog")] string skill_name,
                 CancellationToken ct = default) => tools.LoadSkillAsync(skill_name, ct),
                "load_skill",
                "Loads full instructions and reference documentation for a modular skill on demand."),

            AIFunctionFactory.Create(
                ([Description("Question or clarification prompt to ask the user")] string prompt,
                 [Description("Reason or context for asking")] string? rationale = null,
                 [Description("Optional choice options for the user")] IReadOnlyList<string>? options = null,
                 CancellationToken ct = default) => tools.AskAsync(prompt, rationale, options, ct),
                "ask",
                "Asks clarifying questions to the user when blocked."),

            AIFunctionFactory.Create(
                ([Description("List of structured backlog task proposals")] object? tasks = null,
                 [Description("Task title if single proposal")] string? title = null,
                 [Description("Task description with sub-tasks, testing plan, and open questions if single proposal")] string? description = null,
                 [Description("Task priority (1 Highest - 5 Lowest) if single proposal")] int? priority = null) =>
                tools.ProposeTasks(tasks, title, description, priority),
                "propose_tasks",
                "Proposes structured backlog items for triage and discovery. Each task represents a full Pull Request / Merge Request unit of work with build, test, and code review gates. Include sub-tasks and open questions in the description."),

            AIFunctionFactory.Create(
                ([Description("Detailed critique and rework feedback for the implementation agent")] string feedback,
                 [Description("Summary reason for rejection")] string? reason = null) => tools.RequestChanges(feedback, reason),
                "request_changes",
                "Rejects implementation and sends task back to the implementation agent for rework.")
        };

        if (tools.IsWebSearchConfigured)
        {
            allTools.Add(AIFunctionFactory.Create(
                ([Description("Search query string")] string query,
                 [Description("Maximum results to return (1-10)")] int? max_results = null,
                 CancellationToken ct = default) => tools.WebSearchAsync(query, max_results, ct),
                "web_search",
                "Searches technical web documentation for APIs, errors, or libraries."));
        }

        if (permittedTools == null || permittedTools.Count == 0)
        {
            return allTools;
        }

        return allTools
            .Where(t => permittedTools.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }
}
