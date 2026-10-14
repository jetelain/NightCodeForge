namespace NightCodeForge.Core.Domain;

/// <summary>
/// Canonical tool identifiers recognized across agent harnesses, dispatcher registrations,
/// and profile capability whitelists.
/// </summary>
public static class CanonicalToolNames
{
    public const string Read = "read";
    public const string Edit = "edit";
    public const string Write = "write";
    public const string Bash = "bash";
    public const string Ask = "ask";
    public const string WebSearch = "web_search";
    public const string ProposeTasks = "propose_tasks";
    public const string RequestChanges = "request_changes";
    public const string LoadSkill = "load_skill";
    public const string ReadSkillResource = "read_skill_resource";
    public const string RunSkillScript = "run_skill_script";

    /// <summary>
    /// Complete collection of all canonical tools supported by the platform.
    /// Useful for UI configuration lists, validation, and test harness setup.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Read,
        Edit,
        Write,
        Bash,
        Ask,
        WebSearch,
        ProposeTasks,
        RequestChanges,
        LoadSkill,
        ReadSkillResource,
        RunSkillScript
    };
}
