using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using Pmad.AI.Tools;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Wraps <see cref="CanonicalTools"/> methods into <see cref="AITool"/> instances
/// using <see cref="AIFunctionFactory"/> for seamless integration with <see cref="FunctionInvokingChatClient"/>
/// and <see cref="LenientToolDispatcher"/>.
/// </summary>
public static class CanonicalAIFunctionFactory
{
    private const string MissingToolsInstanceMessage = "CanonicalTools instance is not bound to this schema-only AIFunction.";
    private const string MissingEditParametersMessage = "Error: Edit failed: either 'edits' array or both 'old_text' and 'new_text' must be provided.";

    /// <summary>
    /// Creates <see cref="AITool"/> instances for the canonical toolset, optionally filtered
    /// by a set of permitted tool names and registered with a <see cref="LenientToolDispatcher"/>.
    /// </summary>
    /// <param name="tools">The <see cref="CanonicalTools"/> instance to bind.</param>
    /// <param name="permittedTools">
    /// Optional set of allowed canonical tool names (e.g. from <see cref="Core.Interfaces.SkillPromptResult.PermittedTools"/>).
    /// When <c>null</c>, all configured canonical tools are included.
    /// </param>
    /// <param name="dispatcher">
    /// Optional <see cref="LenientToolDispatcher"/> on which to register all canonical <see cref="AIFunction"/> schemas and handlers.
    /// </param>
    public static IList<AIFunction> CreateTools(
        CanonicalTools tools,
        IEnumerable<string>? permittedTools = null,
        LenientToolDispatcher? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var allCanonicalFunctions = CreateAllCanonicalFunctions(tools);
        dispatcher?.RegisterTools(allCanonicalFunctions);

        HashSet<string>? permittedSet = permittedTools != null
            ? new HashSet<string>(permittedTools, StringComparer.OrdinalIgnoreCase)
            : null;

        var advertisedTools = new List<AIFunction>(allCanonicalFunctions.Count);
        foreach (var fn in allCanonicalFunctions)
        {
            if (string.Equals(fn.Name, CanonicalToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) &&
                !tools.IsWebSearchConfigured)
            {
                continue;
            }

            if (IsBacklogTool(fn.Name) && tools.BacklogMutationService == null)
            {
                continue;
            }

            if (permittedSet != null && !permittedSet.Contains(fn.Name))
            {
                continue;
            }

            advertisedTools.Add(fn);
        }

        return advertisedTools;
    }

    /// <summary>
    /// Creates all 14 canonical <see cref="AIFunction"/> instances with their canonical JSON schemas.
    /// When <paramref name="tools"/> is <c>null</c>, creates schema-equivalent <see cref="AIFunction"/> definitions
    /// suitable for <see cref="LenientToolDispatcher"/> schema inspection and validation.
    /// </summary>
    public static IReadOnlyList<AIFunction> CreateAllCanonicalFunctions(CanonicalTools? tools = null)
    {
        int capKb = (tools?.MaxReadFileBytes ?? WorkflowDefaults.MaxReadFileBytes) / 1024;
        return new List<AIFunction>
        {
            AIFunctionFactory.Create(
                ([Description("Workspace-relative path to read")] string path,
                 [Description("1-indexed start line offset")] int? offset = null,
                 [Description("Maximum lines to read")] int? limit = null,
                 CancellationToken ct = default) =>
                    tools != null
                        ? tools.ReadAsync(path, offset, limit, ct)
                        : throw new InvalidOperationException(MissingToolsInstanceMessage),
                CanonicalToolNames.Read,
                $"Reads a file with line numbers and optional line-based pagination. Output is capped to {capKb} KB to avoid overflowing context; use offset and limit to page through large files."),

            AIFunctionFactory.Create(
                async ([Description("Workspace-relative path to write")] string path,
                       [Description("Full file content")] string content,
                       CancellationToken ct = default) =>
                {
                    if (tools == null)
                    {
                        throw new InvalidOperationException(MissingToolsInstanceMessage);
                    }

                    try
                    {
                        return await tools.WriteAsync(path, content, ct);
                    }
                    catch (InvalidOperationException ex)
                    {
                        return $"Error: {ex.Message}";
                    }
                },
                CanonicalToolNames.Write,
                "Creates or overwrites a workspace file."),

            AIFunctionFactory.Create(
                async ([Description("Workspace-relative path to edit")] string path,
                       [Description("One or more disjoint edits applied in order; each chunk provides oldText (or old_text), newText (or new_text), and optional replaceAll (or replace_all)")] IReadOnlyList<EditChunk>? edits = null,
                       [Description("Shortcut for a single edit: exact character sequence to be replaced")] string? old_text = null,
                       [Description("Shortcut for a single edit: replacement character sequence")] string? new_text = null,
                       [Description("When true, replaces all occurrences of old_text")] bool replace_all = false,
                       CancellationToken ct = default) =>
                {
                    if (tools == null)
                    {
                        throw new InvalidOperationException(MissingToolsInstanceMessage);
                    }

                    try
                    {
                        if (edits is { Count: > 0 } && edits.Any(c => !string.IsNullOrEmpty(c.OldText) || !string.IsNullOrEmpty(c.NewText)))
                        {
                            var effectiveEdits = replace_all
                                ? edits.Select(e => e.ReplaceAll ? e : e with { ReplaceAll = true }).ToList()
                                : edits;
                            return await tools.EditAsync(path, effectiveEdits, ct);
                        }

                        if (old_text != null && new_text != null)
                        {
                            return await tools.EditAsync(path, old_text, new_text, replace_all, ct);
                        }

                        return MissingEditParametersMessage;
                    }
                    catch (InvalidOperationException ex)
                    {
                        return $"Error: {ex.Message}";
                    }
                },
                CanonicalToolNames.Edit,
                "Replaces exact text snippets in a workspace file. Use 'edits' for one or several disjoint replacements in a single call, or 'replace_all' to replace all occurrences."),

            AIFunctionFactory.Create(
                ([Description("Shell command line to execute")] string command,
                 [Description("Timeout in seconds")] int? timeout = null,
                 [Description("Working directory relative to repo root")] string? working_directory = null,
                 CancellationToken ct = default) =>
                    tools != null
                        ? tools.BashAsync(command, timeout, working_directory, ct)
                        : throw new InvalidOperationException(MissingToolsInstanceMessage),
                CanonicalToolNames.Bash,
                "Executes a bash or shell command within the workspace sandbox."),

            AIFunctionFactory.Create(
                ([Description("Exact name of the skill to load from the <skills> catalog")] string skill_name,
                 CancellationToken ct = default) =>
                    tools != null
                        ? tools.LoadSkillAsync(skill_name, ct)
                        : throw new InvalidOperationException(MissingToolsInstanceMessage),
                CanonicalToolNames.LoadSkill,
                "Loads full instructions and reference documentation for a modular skill on demand."),

            AIFunctionFactory.Create(
                ([Description("Consolidate ALL clarifying questions into this single list. Each question must include a prompt, with optional technical rationale and choice options.")]
                 IReadOnlyList<QuestionItem>? questions = null,
                 CancellationToken ct = default) =>
                    tools != null
                        ? tools.AskAsync(questions ?? Array.Empty<QuestionItem>(), ct)
                        : throw new InvalidOperationException(MissingToolsInstanceMessage),
                CanonicalToolNames.Ask,
                "Asks clarifying questions to the human user when blocked. Consolidate all requirements, ambiguities, and architectural questions into a single batched call."),

            AIFunctionFactory.Create(
                ([Description("List of structured backlog task proposals, or omit for single task")] object? tasks = null,
                 [Description("Task title if single proposal")] string? title = null,
                 [Description("Task description with functional scope, acceptance criteria, and testing plan if single proposal")] string? description = null,
                 [Description("Task priority (1 Highest - 5 Lowest) if single proposal")] int? priority = null,
                 [Description("Prerequisite task IDs")] IReadOnlyList<int>? depends_on_ids = null,
                 CancellationToken ct = default) =>
                    tools != null
                        ? tools.ProposeTasksAsync(tasks, title, description, priority, depends_on_ids, ct)
                        : throw new InvalidOperationException(MissingToolsInstanceMessage),
                CanonicalToolNames.ProposeTasks,
                "Proposes structured backlog tasks for triage and discovery. Returns the draft task(s) created with assigned numeric ID(s). Each task represents a full Pull Request / Merge Request unit of work with build, test, and code review gates. Resolve functional and business questions with the maintainer first, then include scope, acceptance criteria, and testing plan in the description. Subsequent tasks can reference earlier created IDs in depends_on_ids."),

            AIFunctionFactory.Create(
                ([Description("Detailed critique and rework feedback for the implementation agent")] string feedback,
                 [Description("Summary reason for rejection")] string? reason = null) =>
                    tools != null
                        ? tools.RequestChanges(feedback, reason)
                        : throw new InvalidOperationException(MissingToolsInstanceMessage),
                CanonicalToolNames.RequestChanges,
                "Rejects implementation and sends task back to the implementation agent for rework."),

            AIFunctionFactory.Create(
                ([Description("Search query string")] string query,
                 [Description("Maximum results to return (1-10)")] int? max_results = null,
                 CancellationToken ct = default) =>
                    tools != null
                        ? tools.WebSearchAsync(query, max_results, ct)
                        : throw new InvalidOperationException(MissingToolsInstanceMessage),
                CanonicalToolNames.WebSearch,
                "Searches technical web documentation for APIs, errors, or libraries. Large results are capped inline and saved in full under /tmp/web-search-*.md."),

            AIFunctionFactory.Create(
                ([Description("Numeric ID of the backlog task")] int task_id,
                 CancellationToken ct = default) =>
                    tools != null
                        ? tools.GetTaskAsync(task_id, ct)
                        : throw new InvalidOperationException(MissingToolsInstanceMessage),
                CanonicalToolNames.GetTask,
                "Fetches the complete details of one backlog task (description, status, priority, prerequisites, dependents) on demand."),

            AIFunctionFactory.Create(
                ([Description("Numeric ID of the unstarted task to edit")] int task_id,
                 [Description("New title")] string? title = null,
                 [Description("New full description (replaces the existing one)")] string? description = null,
                 [Description("New priority (1 Critical - 5 Trivial)")] int? priority = null,
                 [Description("Complete new list of prerequisite task IDs (replaces the existing list; cycles are rejected)")] int[]? depends_on_ids = null,
                 [Description("New merge mode: Automatic or ManualReviewRequired")] string? merge_mode = null,
                 [Description("New base branch")] string? base_branch = null,
                 CancellationToken ct = default) =>
                    tools != null
                        ? tools.EditTaskAsync(task_id, title, description, priority, depends_on_ids, merge_mode, base_branch, ct)
                        : throw new InvalidOperationException(MissingToolsInstanceMessage),
                CanonicalToolNames.EditTask,
                "Edits an unstarted (Suggested or Open, never executed) backlog task. Omitted arguments are left unchanged. Only functionally resolved changes agreed with the maintainer should be applied."),

            AIFunctionFactory.Create(
                ([Description("Numeric ID of the unstarted task")] int task_id,
                 [Description("New priority (1 Critical - 5 Trivial)")] int priority,
                 CancellationToken ct = default) =>
                    tools != null
                        ? tools.SetTaskPriorityAsync(task_id, priority, ct)
                        : throw new InvalidOperationException(MissingToolsInstanceMessage),
                CanonicalToolNames.SetTaskPriority,
                "Changes the priority tier of one unstarted backlog task."),

            AIFunctionFactory.Create(
                ([Description("Array of { task_id, priority } entries (priority 1 Critical - 5 Trivial)")] IReadOnlyList<TaskPriorityUpdate> updates,
                 CancellationToken ct = default) =>
                    tools != null
                        ? tools.ReworkPrioritiesAsync(updates, ct)
                        : throw new InvalidOperationException(MissingToolsInstanceMessage),
                CanonicalToolNames.ReworkPriorities,
                "Changes the priority tier of several unstarted backlog tasks in one call."),

            AIFunctionFactory.Create(
                ([Description("Numeric ID of the unstarted task to remove")] int task_id,
                 [Description("Why the task is removed")] string? reason = null,
                 CancellationToken ct = default) =>
                    tools != null
                        ? tools.RemoveTaskAsync(task_id, reason, ct)
                        : throw new InvalidOperationException(MissingToolsInstanceMessage),
                CanonicalToolNames.RemoveTask,
                "Removes (cancels) an unstarted backlog task. Fails when active tasks still depend on it.")
        };
    }

    private static bool IsBacklogTool(string toolName) =>
        CanonicalToolNames.BacklogMutationTools.Contains(toolName, StringComparer.OrdinalIgnoreCase);
}
