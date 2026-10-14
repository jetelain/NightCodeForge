namespace NightCodeForge.Core.Domain;

/// <summary>
/// Baseline tool whitelists assigned to standard agent modes and workflow stages.
/// </summary>
public static class DefaultAgentTools
{
    private static readonly string[] _readOnly =
    [
        CanonicalToolNames.Read,
        CanonicalToolNames.ProposeTasks,
        CanonicalToolNames.WebSearch,
        CanonicalToolNames.Ask,
        CanonicalToolNames.LoadSkill,
        CanonicalToolNames.ReadSkillResource
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
        CanonicalToolNames.LoadSkill,
        CanonicalToolNames.ReadSkillResource,
        CanonicalToolNames.RunSkillScript
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
        CanonicalToolNames.LoadSkill,
        CanonicalToolNames.ReadSkillResource,
        CanonicalToolNames.RunSkillScript
    ];

    public static IReadOnlyList<string> ReadOnly => _readOnly;
    public static IReadOnlyList<string> Full => _full;
    public static IReadOnlyList<string> Review => _review;

    public static IReadOnlyList<string> GetTools(AgentMode mode) => mode switch
    {
        AgentMode.ReadOnly => _readOnly,
        AgentMode.Review => _review,
        _ => _full
    };
}
