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
    private static readonly string[] _ideation = [.. _readOnly, .. CanonicalToolNames.BacklogMutationTools];

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
