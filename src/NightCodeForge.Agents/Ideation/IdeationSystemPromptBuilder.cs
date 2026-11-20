using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Context;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Agents.Ideation;

/// <summary>
/// Builds stabilized system prompts, incremental workspace context updates, date enrichments,
/// and fallback proposal extractions for the Ideation conversation coordinator.
/// </summary>
internal static partial class IdeationSystemPromptBuilder
{
    private const string TaskGuidanceText = @"<task_planning_guidelines>
When proposing implementation tasks to the developer, adhere to the following rules:
1. PULL-REQUEST / MERGE-REQUEST SIZING:
   - Each task in NightCodeForge represents a full, independent 'pull-request' / 'merge-request' (PR/MR).
   - Each task automatically passes through rigorous pipeline gates: Stage 1 (ForgeTask Review & ambiguity clarification), Pre-Implementation Baseline build & test gates, Stage 2 (Implementation), Post-Implementation Ratchet build & test gates, Stage 3 (Code Review & automated fix gate), and a Merge Gate.
   - Each task will have its own tests (unit, integration, or functional tests) verifying the implementation.
   - DO NOT over-decompose into excessively small or trivial micro-tasks (e.g., do NOT create separate tasks for creating a single file, adding a single method, updating a configuration line, or adding a single dependency). Over-decomposition creates unnecessary pipeline, build, and review overhead.

2. SUB-TASKS WITHIN A TASK:
   - A task may have sub-tasks! When a feature, refactoring, or fix consists of multiple logical steps or components (e.g., database schema changes, backend service implementation, API endpoints, UI updates), group them together into a single cohesive task.
   - Detail these sub-tasks as an implementation checklist or breakdown inside the task's 'description'.
   - The downstream Implementation Agent will execute all sub-tasks together within the single PR/task lifecycle.

3. MANDATORY UPFRONT FUNCTIONAL & BUSINESS RESOLUTION (ZERO OPEN BUSINESS QUESTIONS):
   - Tasks must enter the execution queue fully determined from a functional and business standpoint (""zero-surprise scheduling"").
   - Before invoking 'propose_tasks' or 'edit_task', conduct an interactive interview with the maintainer (using 'ask') to resolve the user problem, expected behaviors, business rules, edge cases, and acceptance criteria.
   - Do NOT leave 'Open Questions', 'TBD', or similar placeholder sections about functional or business matters in a task description. If a functional question remains, ask it now.
   - You are NOT required to identify all technical impacts (such as exhaustive affected-file lists or symbol mapping). Detailed technical scoping is handled downstream by Stage 1 (ForgeTask Review), which can still ask purely technical questions after inspecting the code.

4. TASK PROPOSAL INVOCATION:
   - When you recommend one or more implementation tasks, do NOT output markdown JSON codeblocks in your response.
   - Instead, invoke the 'propose_tasks' tool with your proposed task(s). Proposing tasks creates draft task(s) and returns their details and numeric IDs so you can reference them in subsequent calls (e.g. dependencies, edit_task, etc.).
   - Structure each task's description with clear Markdown sections (## Summary, ## Functional Scope / Sub-tasks, ## Acceptance Criteria, ## Testing Plan).
   - Multiple tasks proposed in a single call are independent by default (no implicit sequential dependencies are created). Whenever a task genuinely depends on an existing task or on another task proposed in the same batch, specify it explicitly via 'depends_on_ids' / 'dependsOnTitles' (or update it afterwards with 'edit_task').
   - After proposing tasks, explain your proposal and architectural rationale clearly to the developer.

5. EXISTING BACKLOG & ISSUE AWARENESS:
   - Existing backlog tasks and issues are maintained as Markdown specifications in 'docs/tasks/'.
   - Before proposing new tasks, inspect 'docs/tasks/' (using 'read' or 'bash' e.g. ls docs/tasks) to understand currently planned, in-progress, or completed work.
   - Do NOT propose duplicate tasks for issues already present in the backlog.
   - You may reference existing task IDs or link dependencies (via depends_on_ids or dependsOnTitles) to existing backlog tasks when planning follow-ups or prerequisite work.

6. WORKSPACE ATTACHMENTS & CITATIONS:
   - When '<workspace_attachments>' is present, workspace context files are mounted read-only at '/workspace/attachments/'.
   - Inspect relevant attachments directly using 'read' (passing the entry's 'path', e.g., read(""/workspace/attachments/001-spec.md"")) or 'bash' on '/workspace/attachments' instead of guessing their contents.
   - Cite relied-upon attachments in task descriptions using '@attachment:{id} {Title}' (e.g., ""Design the routing table per @attachment:2 OpenAI API Specification"") and/or pass their IDs in 'attachment_ids' when invoking 'propose_tasks' or 'edit_task'.

7. DEPENDENCY IDENTIFICATION:
<dependency_identification_rules>
When planning or proposing tasks:
1. Cross-reference your ideas against the `<existing_workspace_tasks>` list above (which contains top active backlog tasks ordered by priority).
2. DO NOT propose duplicate tasks for capabilities already captured in existing tasks or already merged in the codebase.
3. If your proposed task builds on, calls, or extends interfaces or data models introduced in an existing open, suggested, or draft task (or an earlier task proposed in the same batch), you MUST set 'depends_on_ids' in your 'propose_tasks' call (or 'dependsOnTitles') with the numeric ID (or title) of the prerequisite task.
4. Explain to the developer why this dependency exists and the recommended order of execution.
5. AVOID CIRCULAR DEPENDENCIES: Ensure your dependencies form a strict acyclic graph. Circular dependencies will be rejected with an error.
</dependency_identification_rules>
</task_planning_guidelines>

<backlog_management_guidelines>
You cannot edit repository files or 'docs/tasks/*.md' directly. Existing backlog tasks are managed exclusively through these host-level tools, which are audited and keep the markdown specifications in sync:
- 'get_task' (task_id): fetch the full details (description, status, priority, difficulty, merge mode, base branch, prerequisites, dependents, attachmentIds) of a task listed in <existing_workspace_tasks>. Call it before refining a task, instead of guessing its content.
- 'edit_task' (task_id, title?, description?, priority?, depends_on_ids?, attachment_ids?, merge_mode?, base_branch?, difficulty?): refine an existing task. Omitted arguments are unchanged; 'description' and 'depends_on_ids' replace the existing value, so start from the 'get_task' output. Resolve functional questions with the maintainer ('ask') BEFORE calling it.
- 'set_task_priority' (task_id, priority) and 'rework_priorities' (updates: [{ task_id, priority }]): change priority tiers (1 Critical to 5 Trivial) of one or several tasks; prefer 'rework_priorities' when reordering several tasks.
- 'remove_task' (task_id, reason): cancel a task that is obsolete or duplicated. It is refused while active tasks depend on it; re-link or remove those dependents first.
Only tasks that are Draft, Suggested or Open, never executed, and not currently running can be changed. Tasks that already entered the pipeline (or are Reviewed, Implemented, Ready, Closed, Rejected, Cancelled) are rejected with an error: do not retry, create a follow-up task via 'propose_tasks' instead. Never mutate the backlog without the maintainer's agreement; summarize every change you made afterwards.
</backlog_management_guidelines>";

    [GeneratedRegex(@"```(?:json)?\s*\[\s*\{.*?\}\s*\]\s*```", RegexOptions.Singleline)]
    private static partial Regex TaskJsonArrayRegex();

    public static string BuildSystemPrompt(
        Workspace workspace,
        AgentProfile profile,
        AgentWorkspaceLocations locations,
        string? topLevelSummary,
        AgentPromptContext promptContext,
        string backlogDigest,
        string attachmentsDigest,
        string hotspotsDigest,
        string? customSystemPrompt,
        bool? supportsVision = null)
    {
        var memorySection = BuildMemorySections(promptContext);
        var environmentSection = new StringBuilder();
        environmentSection.AppendLine("<environment>");
        environmentSection.AppendLine($"- Current Working Directory (PWD): {locations.AgentWorkingDir}");
        environmentSection.AppendLine($"- Repository Root: {locations.AgentWorkingDir}");
        environmentSection.AppendLine($"- Execution Mode: {workspace.ExecutionMode}");
        environmentSection.AppendLine($"- All relative paths, file operations (read, edit, write), and bash commands execute in this working directory ({locations.AgentWorkingDir}).");
        if (!string.IsNullOrEmpty(topLevelSummary))
        {
            environmentSection.AppendLine($"- Top-level files and directories in repository root: {topLevelSummary}");
        }
        if (supportsVision == true)
        {
            environmentSection.AppendLine("- Visual Inspection: Vision is ENABLED for the active model. You may call 'read' on image files (.png, .jpg, .jpeg, .webp, .gif) in the repository or /workspace/attachments/ to inspect them directly.");
        }
        else if (supportsVision == false)
        {
            environmentSection.AppendLine("- Visual Inspection: Vision is NOT supported by the active model. Do not call 'read' on image files.");
        }
        environmentSection.AppendLine($"- When exploring the codebase, begin by inspecting the directory structure or key files in '{locations.AgentWorkingDir}'.");
        environmentSection.AppendLine("</environment>");

        var basePersona = !string.IsNullOrWhiteSpace(customSystemPrompt)
            ? customSystemPrompt
            : (!string.IsNullOrWhiteSpace(profile.SystemPrompt)
                ? profile.SystemPrompt
                : AgentProfile.DefaultIdeationSystemPrompt);

        var skillsSection = string.IsNullOrEmpty(promptContext.SkillsCatalog)
            ? string.Empty
            : "\n\n" + promptContext.SkillsCatalog;

        var backlogSection = string.IsNullOrWhiteSpace(backlogDigest) ? string.Empty : backlogDigest + "\n\n";
        var attachmentsSection = string.IsNullOrWhiteSpace(attachmentsDigest) ? string.Empty : attachmentsDigest + "\n\n";
        var hotspotsSection = string.IsNullOrWhiteSpace(hotspotsDigest) ? string.Empty : hotspotsDigest + "\n\n";

        return $@"{basePersona}
{environmentSection.ToString().TrimEnd()}
{promptContext.ProjectContext}{skillsSection}{memorySection}

{backlogSection}{attachmentsSection}{hotspotsSection}{TaskGuidanceText}";
    }

    /// <summary>Role guidelines (when memory is writable), pinned local memory and memory catalog sections.</summary>
    private static string BuildMemorySections(AgentPromptContext promptContext)
    {
        var sb = new StringBuilder();
        if (promptContext.CanWriteMemory)
        {
            sb.Append("\n\n").Append(AgentMemoryGuidelines.Ideation);
        }

        if (!string.IsNullOrWhiteSpace(promptContext.WorkspaceMemory))
        {
            sb.Append("\n\n").Append(promptContext.WorkspaceMemory.Trim());
        }

        if (!string.IsNullOrWhiteSpace(promptContext.MemoryCatalog))
        {
            sb.Append("\n\n").Append(promptContext.MemoryCatalog.Trim());
        }

        return sb.ToString();
    }

    public static string? BuildWorkspaceContextUpdateDelta(
        string? previousBacklogDigest,
        string currentBacklogDigest,
        string? previousAttachmentsDigest,
        string currentAttachmentsDigest,
        string? previousHotspotsDigest,
        string currentHotspotsDigest,
        string? previousWorkspaceMemory = null,
        string? currentWorkspaceMemory = null,
        string? previousMemoryCatalog = null,
        string? currentMemoryCatalog = null)
    {
        var changedSections = new List<string>();

        if (!string.Equals(previousBacklogDigest ?? string.Empty, currentBacklogDigest, StringComparison.Ordinal))
        {
            changedSections.Add(string.IsNullOrWhiteSpace(currentBacklogDigest)
                ? "<existing_workspace_tasks>(none)</existing_workspace_tasks>"
                : currentBacklogDigest);
        }

        if (!string.Equals(previousAttachmentsDigest ?? string.Empty, currentAttachmentsDigest, StringComparison.Ordinal))
        {
            changedSections.Add(string.IsNullOrWhiteSpace(currentAttachmentsDigest)
                ? "<workspace_attachments>(none)</workspace_attachments>"
                : currentAttachmentsDigest);
        }

        if (!string.Equals(previousHotspotsDigest ?? string.Empty, currentHotspotsDigest, StringComparison.Ordinal))
        {
            changedSections.Add(string.IsNullOrWhiteSpace(currentHotspotsDigest)
                ? "<workspace_friction_hotspots>(none)</workspace_friction_hotspots>"
                : currentHotspotsDigest);
        }

        if (!string.Equals(previousWorkspaceMemory ?? string.Empty, currentWorkspaceMemory ?? string.Empty, StringComparison.Ordinal))
        {
            changedSections.Add(string.IsNullOrWhiteSpace(currentWorkspaceMemory)
                ? "<workspace_memory>(empty)</workspace_memory>"
                : currentWorkspaceMemory);
        }

        if (!string.Equals(previousMemoryCatalog ?? string.Empty, currentMemoryCatalog ?? string.Empty, StringComparison.Ordinal))
        {
            changedSections.Add(string.IsNullOrWhiteSpace(currentMemoryCatalog)
                ? "<memory_catalog>(none)</memory_catalog>"
                : currentMemoryCatalog);
        }

        if (changedSections.Count == 0)
        {
            return null;
        }

        return $"<workspace_context_update>\n{string.Join("\n\n", changedSections)}\n</workspace_context_update>";
    }

    public static string FormatUserPromptWithCurrentDate(
        string userPrompt,
        IReadOnlyList<ChatMessage> existingHistory,
        TimeProvider timeProvider)
    {
        var currentDate = timeProvider.GetUtcNow().ToString("yyyy-MM-dd");
        var effectiveUserPrompt = userPrompt;
        var hasDateInHistory = existingHistory.Any(m =>
            (m.Text ?? string.Empty).Contains("Current Date:", StringComparison.OrdinalIgnoreCase));

        if (!hasDateInHistory && !effectiveUserPrompt.Contains("Current Date:", StringComparison.OrdinalIgnoreCase))
        {
            if (effectiveUserPrompt.Contains("[Context]:", StringComparison.OrdinalIgnoreCase))
            {
                var contextIdx = effectiveUserPrompt.IndexOf("[Context]:", StringComparison.OrdinalIgnoreCase);
                effectiveUserPrompt = effectiveUserPrompt.Insert(contextIdx + "[Context]:".Length, $"\n- Current Date: {currentDate}");
            }
            else
            {
                effectiveUserPrompt = $"{effectiveUserPrompt}\n\n[Context]:\n- Current Date: {currentDate}".Trim();
            }
        }

        return effectiveUserPrompt;
    }

    public static List<IdeationProposal> ParseFallbackProposals(string text, ILogger logger)
    {
        var proposals = new List<IdeationProposal>();
        var match = TaskJsonArrayRegex().Match(text);
        if (!match.Success)
        {
            return proposals;
        }

        var rawJson = match.Value.Trim();
        if (rawJson.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = rawJson.IndexOf('\n');
            var lastTicks = rawJson.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine > 0 && lastTicks > firstLine)
            {
                rawJson = rawJson.Substring(firstLine + 1, lastTicks - firstLine - 1).Trim();
            }
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<IdeationProposalDto>>(
                rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (parsed is { Count: > 0 })
            {
                foreach (var p in parsed)
                {
                    var deps = new List<string>();
                    if (p.DependsOnTitles is { Count: > 0 })
                    {
                        deps.AddRange(p.DependsOnTitles.Where(d => !string.IsNullOrWhiteSpace(d)));
                    }

                    var attachmentIds = AttachmentCitationParser.UnionAttachmentIds(p.AttachmentIds, p.Description);
                    proposals.Add(new IdeationProposal(
                        p.Title ?? "Untitled Proposal",
                        p.Description ?? string.Empty,
                        p.Priority <= 0 ? TaskPriority.Normal : p.Priority,
                        deps,
                        DependsOnIds: p.DependsOnIds is { Count: > 0 } ? p.DependsOnIds : null,
                        Difficulty: p.Difficulty,
                        AttachmentIds: attachmentIds.Count > 0 ? attachmentIds : null));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse proposed tasks JSON from ideation response.");
        }

        return proposals;
    }

    public static IdeationProposal ToIdeationProposal(TaskProposal proposal)
    {
        var attachmentIds = AttachmentCitationParser.UnionAttachmentIds(proposal.AttachmentIds, proposal.Description);
        return new IdeationProposal(
            proposal.Title,
            proposal.Description,
            proposal.Priority,
            proposal.DependsOnTitles,
            proposal.TaskId,
            proposal.DependsOnIds,
            proposal.MergeMode,
            proposal.Difficulty,
            attachmentIds.Count > 0 ? attachmentIds : null);
    }

    public sealed class IdeationProposalDto
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int Priority { get; set; } = TaskPriority.Normal;
        public List<string>? DependsOnTitles { get; set; }
        public List<int>? DependsOnIds { get; set; }
        public string? Difficulty { get; set; }
        public List<int>? AttachmentIds { get; set; }
    }
}
