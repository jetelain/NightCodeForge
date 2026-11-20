namespace NightCodeForge.Agents.Context;

/// <summary>Role-specific <c>&lt;memory_guidelines&gt;</c> directives telling agents what to remember and where to write it.</summary>
public static class AgentMemoryGuidelines
{
    public const string IdeationRole = "ideation";
    public const string PipelineRole = "pipeline";

    public const string Ideation = @"<memory_guidelines>
- Read <workspace_memory> and the relevant <memory_catalog> files (via `read`) before asking questions the developer already answered in earlier sessions.
- Whenever an interview or discussion establishes durable product vision, domain terminology, architectural preferences, user workflow preferences, or explicitly rejected approaches, use `write` or `edit` ONLY on `/workspace/memory/MEMORY.md` (concise bullet index) or `/workspace/memory/<topic>.md` (detailed topic notes with optional `description:` YAML frontmatter). Keep MEMORY.md concise and move detailed notes into topic files.
- `/workspace/memory/` is the only directory you may write. Never attempt to edit `AGENTS.md`, `.agents/memory/*` or repository code from Ideation (`/workspace/clone` is read-only). When a local convention or decision should be shared with all developers via Git, include ""Update `AGENTS.md` or `.agents/memory/<topic>.md`"" as an explicit sub-task in the proposed task specification.
</memory_guidelines>";

    public const string Pipeline = @"<memory_guidelines>
- Consult <workspace_memory> and the relevant <memory_catalog> entries when scoping tasks, implementing changes, or reviewing diffs.
- Local memory (`/workspace/memory/<topic>.md`, shared live across tasks, never committed): write immediate operational or debugging discoveries, environment quirks, recurring review lessons and cross-task notes there. This directory is the only allowed exception to the rule of not modifying files outside your working directory.
- Shared memory (committed with the PR and reviewed by the user): when a task establishes a permanent architectural invariant, project convention, or explicitly promotes local knowledge into team documentation, create or update `.agents/memory/<topic>.md` or `AGENTS.md` inside your task worktree.
- Committed repository rules are authoritative: developers may edit `AGENTS.md` and `.agents/memory/*.md` directly at any time.
</memory_guidelines>";

    public static string ForRole(string role) => role switch
    {
        IdeationRole => Ideation,
        PipelineRole => Pipeline,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown memory guidelines role.")
    };
}
