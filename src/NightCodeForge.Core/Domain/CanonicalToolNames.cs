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
    public const string GetTask = "get_task";
    public const string EditTask = "edit_task";
    public const string SetTaskPriority = "set_task_priority";
    public const string ReworkPriorities = "rework_priorities";
    public const string RemoveTask = "remove_task";

    /// <summary>
    /// Host-level backlog tools exposed to the ideation agent (scoped task mutations; no filesystem writes).
    /// </summary>
    public static readonly IReadOnlyList<string> BacklogMutationTools = new[]
    {
        GetTask,
        EditTask,
        SetTaskPriority,
        ReworkPriorities,
        RemoveTask
    };

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
        RunSkillScript,
        GetTask,
        EditTask,
        SetTaskPriority,
        ReworkPriorities,
        RemoveTask
    };
}
