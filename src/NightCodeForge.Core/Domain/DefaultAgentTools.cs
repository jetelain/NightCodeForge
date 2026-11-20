namespace NightCodeForge.Core.Domain;

/// <summary>
/// Baseline tool whitelists assigned to standard agent modes and workflow stages.
/// </summary>
public static class DefaultAgentTools
{
    private static readonly string[] _readOnly =
    [
        CanonicalToolNames.Read,
        CanonicalToolNames.Bash, // Restricted version of bash (restricted to a subset of commands)
        CanonicalToolNames.ProposeTasks,
        CanonicalToolNames.WebSearch,
        CanonicalToolNames.Ask,
        CanonicalToolNames.LoadSkill
    ];

    // Read-only workspace plus scoped host-level backlog tools (ideation additionally grants priority-reordering tools).
    // Write/Edit are granted so Ideation can maintain /workspace/memory/*.md; the RestrictedReadOnly sandbox keeps
    // every other mount (e.g. /workspace/clone) read-only and WorkspaceFileToolModule enforces the Markdown-only guard.
    private static readonly string[] _ideation =
    [
        .. _readOnly,
        CanonicalToolNames.Write,
        CanonicalToolNames.Edit,
        .. CanonicalToolNames.BacklogMutationTools
    ];

    /// <summary>The standard Ideation tool set before memory write access was introduced (used for profile reconciliation).</summary>
    private static readonly string[] _legacyIdeation = [.. _readOnly, .. CanonicalToolNames.BacklogMutationTools];
    public static IReadOnlyList<string> LegacyIdeation => _legacyIdeation;

    // Untrusted intake triage: read-only workspace inspection, skills, and scoped triage backlog tools
    // (excludes filesystem edit/write, web_search, ask, request_changes, and team priority reordering).
    private static readonly string[] _triage =
    [
        CanonicalToolNames.Read,
        CanonicalToolNames.Bash,
        CanonicalToolNames.LoadSkill,
        CanonicalToolNames.ProposeTasks,
        CanonicalToolNames.GetTask,
        CanonicalToolNames.EditTask,
        CanonicalToolNames.RemoveTask
    ];

    private static readonly string[] _full =
    [
        CanonicalToolNames.Read,
        CanonicalToolNames.Edit,
        CanonicalToolNames.Write,
        CanonicalToolNames.Bash,
        CanonicalToolNames.WebSearch,
        CanonicalToolNames.Ask,
        CanonicalToolNames.ProposeTasks,
        CanonicalToolNames.LoadSkill
    ];

    private static readonly string[] _review =
    [
        CanonicalToolNames.Read,
        CanonicalToolNames.Edit,
        CanonicalToolNames.Write,
        CanonicalToolNames.Bash,
        CanonicalToolNames.WebSearch,
        CanonicalToolNames.Ask,
        CanonicalToolNames.ProposeTasks,
        CanonicalToolNames.RequestChanges,
        CanonicalToolNames.LoadSkill
    ];

    public static IReadOnlyList<string> ReadOnly => _readOnly;
    public static IReadOnlyList<string> Ideation => _ideation;
    public static IReadOnlyList<string> Triage => _triage;
    public static IReadOnlyList<string> Full => _full;
    public static IReadOnlyList<string> Review => _review;

    public static IReadOnlyList<string> GetTools(AgentMode mode) => mode switch
    {
        AgentMode.ReadOnly => _readOnly,
        AgentMode.Review => _review,
        _ => _full
    };
}
