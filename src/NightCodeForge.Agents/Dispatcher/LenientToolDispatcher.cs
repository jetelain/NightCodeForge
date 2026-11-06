using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using Pmad.AI.Tools;

namespace NightCodeForge.Agents.Dispatcher;

/// <summary>
/// Declarative configuration and factory for NightCodeForge's <see cref="LenientToolDispatcher"/>
/// powered by <c>Pmad.AI.Tools</c>.
/// </summary>
public static class NightCodeForgeToolDispatcher
{
    private static readonly string[] PathAliases =
        ["file_path", "filePath", "filepath", "file", "filename", "target_file", "targetFile", "target", "uri", "absolute_path", "relative_path"];

    private static readonly string[] ReadOffsetAliases =
        ["start_line", "startLine", "line_start", "from_line", "from", "start", "line_offset", "lineOffset"];

    private static readonly string[] ReadEndLineAliases =
        ["end_line", "endLine", "line_end", "lineEnd", "to_line", "toLine", "to", "end"];

    private static readonly string[] ReadLimitAliases =
        ["max_lines", "maxLines", "line_count", "lineCount", "count", "lines", "num_lines"];

    private static readonly string[] EditOldTextAliases =
        ["oldText", "old_text", "old_str", "oldStr", "old_string", "oldString", "search", "target", "find", "original", "target_string", "targetString", "targetContent", "target_content", "from", "old"];

    private static readonly string[] EditNestedOldTextAliases =
        ["old_text", "oldText", "old_str", "oldStr", "old_string", "oldString", "search", "target", "find", "original", "target_string", "targetString", "targetContent", "target_content", "from", "old"];

    private static readonly string[] EditNewTextAliases =
        ["newText", "new_text", "new_str", "newStr", "new_string", "newString", "replace", "replacement", "updated", "replacementContent", "replacement_content", "to", "content", "value", "new"];

    private static readonly string[] EditNestedNewTextAliases =
        ["new_text", "newText", "new_str", "newStr", "new_string", "newString", "replace", "replacement", "updated", "replacementContent", "replacement_content", "to", "content", "value", "new"];

    private static readonly string[] EditReplaceAllAliases =
        ["replaceAll", "replace_all", "all", "global", "multi", "allowMultiple", "replace_all_occurrences", "replaceAllOccurrences"];

    private static readonly string[] EditNestedReplaceAllAliases =
        ["replace_all", "replaceAll", "all", "global", "multi", "allowMultiple", "replace_all_occurrences", "replaceAllOccurrences"];

    private static readonly string[] EditChunksAliases =
        ["replacementChunks", "replacement_chunks", "chunks", "changes", "patches", "modifications", "replacements", "operations", "items"];

    private static readonly string[] TaskIdAliases =
        ["id", "taskId", "task", "number", "task_number", "taskNumber"];

    private static readonly string[] NestedTaskIdAliases =
        ["task_id", "id", "task", "number", "task_number", "taskNumber"];

    private static readonly string[] PriorityAliases =
        ["prio", "order", "rank", "importance", "new_priority", "newPriority"];

    private static readonly string[] DependsOnIdsAliases =
        ["dependsOnIds", "depends_on", "dependsOn", "dependencies", "dependency_ids", "dependencyIds", "prerequisites", "prerequisite_ids", "deps"];

    private static readonly string[] ListDirPathAliases = ["dir", "directory", "folder", "target", "dir_path", "directory_path"];
    private static readonly string[] GrepQueryAliases = ["pattern", "search", "regex", "term", "text"];
    private static readonly string[] GrepPathAliases = ["dir", "directory", "folder", "include", "file"];
    private static readonly string[] FileOutlinePathAliases = ["file_path", "filepath", "file", "filename", "target", "target_file", "targetFile"];
    private static readonly string[] FindFilesPathAliases = ["dir", "directory", "folder", "search_path", "root"];
    private static readonly string[] FindFilesPatternAliases = ["query", "name", "glob", "filter", "file_pattern"];

    /// <summary>
    /// Creates a configured <see cref="LenientToolDispatcher"/> with all canonical tool schemas,
    /// dropped-tool shell translations, and optional bound <see cref="CanonicalTools"/> and custom tools.
    /// </summary>
    public static LenientToolDispatcher Create(
        CanonicalTools? tools = null,
        IReadOnlyList<CustomToolConfig>? customTools = null,
        ILogger? logger = null,
        string? sessionId = null,
        LenientDispatchTelemetry? telemetry = null,
        bool enableParameterCoercion = true)
    {
        var configuration = CreateConfiguration(customTools);
        var options = CreateOptions(enableParameterCoercion);
        options.SessionId = sessionId;
        options.Telemetry = telemetry;

        var dispatcher = new LenientToolDispatcher(configuration, options);
        dispatcher.RegisterTools(CanonicalAIFunctionFactory.CreateAllCanonicalFunctions(tools));

        return dispatcher;
    }

    /// <summary>
    /// Registers or updates all 14 canonical <see cref="AIFunction"/> instances bound to <paramref name="tools"/>
    /// on the specified <paramref name="dispatcher"/>.
    /// </summary>
    public static void RegisterCanonicalTools(this LenientToolDispatcher dispatcher, CanonicalTools tools)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(tools);
        dispatcher.RegisterTools(CanonicalAIFunctionFactory.CreateAllCanonicalFunctions(tools));
    }

    /// <summary>
    /// Builds NightCodeForge's <see cref="LenientToolDispatcherOptions"/> using <see cref="ReadOnlyCommandValidator.Instance"/>.
    /// </summary>
    public static LenientToolDispatcherOptions CreateOptions(bool enableParameterCoercion = true)
    {
        return new LenientToolDispatcherOptions
        {
            EnableParameterCoercion = enableParameterCoercion,
            CommandValidator = NightCodeForge.Agents.Tools.ReadOnlyCommandValidator.Instance,
            ValidateTranslationsWithReadOnlyValidator = true,
            PropagatedParameters = new HashSet<string>(["replace_all", "replaceAll"], StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    /// Builds NightCodeForge's declarative <see cref="ToolDispatcherConfiguration"/> for all canonical tools,
    /// backlog mutation tools, dropped-tool shell translations, and optional custom database tools.
    /// </summary>
    public static ToolDispatcherConfiguration CreateConfiguration(IReadOnlyList<CustomToolConfig>? customTools = null)
    {
        var config = new ToolDispatcherConfiguration();

        ConfigureRead(config);
        ConfigureEdit(config);
        ConfigureWrite(config);
        ConfigureBash(config);
        ConfigureLoadSkill(config);
        ConfigureAsk(config);
        ConfigureWebSearch(config);
        ConfigureProposeTasks(config);
        ConfigureRequestChanges(config);
        ConfigureBacklogTools(config);
        ConfigureTranslations(config);

        if (customTools is { Count: > 0 })
        {
            foreach (var customTool in customTools)
            {
                if (string.IsNullOrWhiteSpace(customTool.Name))
                {
                    continue;
                }

                var mapping = new ToolMapping();
                foreach (var alias in customTool.Aliases)
                {
                    if (!string.IsNullOrWhiteSpace(alias))
                    {
                        mapping.Aliases.Add(alias);
                    }
                }

                foreach (var (alias, canonicalParam) in customTool.ParameterAliases)
                {
                    if (!string.IsNullOrWhiteSpace(alias) && !string.IsNullOrWhiteSpace(canonicalParam))
                    {
                        mapping.ParameterAliases[alias] = canonicalParam;
                    }
                }

                config.Tools[customTool.Name] = mapping;
            }
        }

        return config;
    }

    private static void ConfigureRead(ToolDispatcherConfiguration config)
    {
        var mapping = new ToolMapping
        {
            Aliases = new HashSet<string>(
                ["view_file", "read_file", "readfile", "read_file_content", "cat", "get_file_content", "get_file_contents", "open_file", "view_code"],
                StringComparer.OrdinalIgnoreCase)
        };

        mapping.AddParameterAliases("path", PathAliases);
        mapping.AddParameterAliases("offset", ReadOffsetAliases);
        mapping.AddParameterAliases("limit", ReadLimitAliases);
        mapping.MapEndLineToLimit("offset", "limit", ReadEndLineAliases);

        config.Tools[CanonicalToolNames.Read] = mapping;
    }

    private static void ConfigureEdit(ToolDispatcherConfiguration config)
    {
        var mapping = new ToolMapping
        {
            Aliases = new HashSet<string>(
                ["replace_file_content", "replacefilecontent", "replace_content", "replacecontent", "multi_replace_file_content", "apply_diff", "edit_file", "editfile", "patch_file", "update_file", "modify_file", "str_replace", "replace_in_file", "multi_edit"],
                StringComparer.OrdinalIgnoreCase),
            PropagatedParameters = new HashSet<string>(["replace_all", "replaceAll"], StringComparer.OrdinalIgnoreCase)
        };

        mapping.AddParameterAliases("path", PathAliases);
        mapping.AddParameterAliases("old_text", EditOldTextAliases);
        mapping.AddParameterAliases("new_text", EditNewTextAliases);
        mapping.AddParameterAliases("replace_all", EditReplaceAllAliases);
        mapping.AddParameterAliases("edits", EditChunksAliases);

        mapping.AddNestedParameterAlias("edits", "oldText", [.. EditNestedOldTextAliases]);
        mapping.AddNestedParameterAlias("edits", "newText", [.. EditNestedNewTextAliases]);
        mapping.AddNestedParameterAlias("edits", "replaceAll", [.. EditNestedReplaceAllAliases]);

        mapping.AddPromotedCollectionParameter("edits", "old_text", "new_text", "replace_all");
        mapping.AddNullAsEmptyStringParameter("new_text", "edits[].newText");

        config.Tools[CanonicalToolNames.Edit] = mapping;
    }

    private static void ConfigureWrite(ToolDispatcherConfiguration config)
    {
        var mapping = new ToolMapping
        {
            Aliases = new HashSet<string>(
                ["write_file", "writefile", "write_to_file", "create_file", "new_file", "save_file", "overwrite_file"],
                StringComparer.OrdinalIgnoreCase)
        };

        mapping.AddParameterAliases("path", PathAliases);
        mapping.AddParameterAliases("content", ["code", "CodeContent", "code_content", "contents", "text", "body", "data", "file_content", "new_content", "value"]);

        config.Tools[CanonicalToolNames.Write] = mapping;
    }

    private static void ConfigureBash(ToolDispatcherConfiguration config)
    {
        var mapping = new ToolMapping
        {
            Aliases = new HashSet<string>(
                ["run_command", "runcommand", "run_cmd", "execute", "execute_command", "shell", "terminal", "cmd", "powershell", "exec", "sh", "run_bash"],
                StringComparer.OrdinalIgnoreCase)
        };

        mapping.AddParameterAliases("command", ["cmd", "command_line", "commandLine", "exec", "script", "bash_command", "shell_command", "input"]);
        mapping.AddParameterAliases("timeout", ["timeout_ms", "timeout_sec", "timeout_seconds", "wait_ms"]);
        mapping.AddParameterAliases("working_directory", ["workingDirectory", "cwd", "workdir", "working_dir", "dir", "directory", "folder", "work_dir"]);

        config.Tools[CanonicalToolNames.Bash] = mapping;
    }

    private static void ConfigureLoadSkill(ToolDispatcherConfiguration config)
    {
        var mapping = new ToolMapping
        {
            Aliases = new HashSet<string>(
                ["get_skill", "read_skill", "use_skill", "useskill", "loadskill", "activate_skill", "skill"],
                StringComparer.OrdinalIgnoreCase)
        };

        mapping.AddParameterAliases("skill_name", ["name", "skill", "skillName", "id", "skill_id"]);

        config.Tools[CanonicalToolNames.LoadSkill] = mapping;
    }

    private static void ConfigureAsk(ToolDispatcherConfiguration config)
    {
        var mapping = new ToolMapping
        {
            Aliases = new HashSet<string>(
                ["ask_user", "ask_human", "ask_human_question", "askhumanquestion", "askhuman", "ask_question", "ask_questions", "clarify", "question", "prompt_user", "request_clarification"],
                StringComparer.OrdinalIgnoreCase),
            PrimaryCollectionProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["questions"] = "prompt",
                ["questions[]"] = "prompt"
            }
        };

        mapping.AddParameterAliases("questions", ["question_items", "questionItems", "items", "prompts", "queries", "question", "text", "message", "prompt", "query"]);

        mapping.AddNestedParameterAlias("questions", "prompt", "question", "text", "message", "query", "title", "label", "description");
        mapping.AddNestedParameterAlias("questions", "rationale", "reason", "why", "context", "explanation", "header", "category", "section", "topic", "group");
        mapping.AddNestedParameterAlias("questions", "options", "choices", "values", "candidates", "items", "answers", "select_options", "selectOptions", "options_json", "optionsJson");

        config.Tools[CanonicalToolNames.Ask] = mapping;
    }

    private static void ConfigureWebSearch(ToolDispatcherConfiguration config)
    {
        var mapping = new ToolMapping
        {
            Aliases = new HashSet<string>(
                ["search_web", "websearch", "google", "internet_search", "bing_search", "search"],
                StringComparer.OrdinalIgnoreCase)
        };

        mapping.AddParameterAliases("query", ["q", "search_query", "searchQuery", "term", "keywords", "text"]);
        mapping.AddParameterAliases("max_results", ["limit", "count", "num_results", "maxResults", "top_k"]);

        config.Tools[CanonicalToolNames.WebSearch] = mapping;
    }

    private static void ConfigureProposeTasks(ToolDispatcherConfiguration config)
    {
        var mapping = new ToolMapping
        {
            Aliases = new HashSet<string>(
                [
                    "propose_task",
                    "proposetask",
                    "propose_single_task",
                    "create_task",
                    "create_tasks",
                    "add_task",
                    "add_tasks",
                    "new_task",
                    "new_tasks",
                    "proposetasks",
                    "submit_tasks",
                    "propose_backlog",
                    "propose_follow_up",
                    "follow_up_tasks"
                ],
                StringComparer.OrdinalIgnoreCase)
        };

        mapping.AddParameterAliases("tasks", ["proposals", "items", "follow_up_tasks", "task_list", "task"]);
        mapping.AddParameterAliases("title", ["name", "task_title", "summary", "header"]);
        mapping.AddParameterAliases("description", ["desc", "details", "body", "prompt", "specification", "spec", "content"]);
        mapping.AddParameterAliases("priority", ["prio", "order", "rank", "importance"]);
        mapping.AddParameterAliases("depends_on_ids", DependsOnIdsAliases);

        mapping.AddNestedParameterAlias("tasks", "title", "name", "summary", "task_title", "header");
        mapping.AddNestedParameterAlias("tasks", "description", "desc", "details", "body", "content", "spec", "specification", "prompt");
        mapping.AddNestedParameterAlias("tasks", "priority", "prio", "order", "rank", "importance");
        mapping.AddNestedParameterAlias("tasks", "dependsOnTitles", "depends_on_titles", "dependsOnIndices", "depends_on_indices", "depends_on", "dependsOn", "dependencies", "deps");
        mapping.AddNestedParameterAlias("tasks", "depends_on_ids", DependsOnIdsAliases);

        config.Tools[CanonicalToolNames.ProposeTasks] = mapping;
    }

    private static void ConfigureRequestChanges(ToolDispatcherConfiguration config)
    {
        var mapping = new ToolMapping
        {
            Aliases = new HashSet<string>(
                ["reject_changes", "reject_implementation", "request_changes", "requestchanges", "rejectimplementation", "send_back", "sendback", "reject_stage", "loopback", "request_rework", "changes_requested", "fail_review", "reject_review", "reject", "return_to_implementation"],
                StringComparer.OrdinalIgnoreCase)
        };

        mapping.AddParameterAliases("feedback", ["comments", "review_feedback", "review", "message", "details", "issues", "findings", "changes", "critique", "text", "body", "description"]);
        mapping.AddParameterAliases("reason", ["comment", "summary", "rationale", "cause", "why", "title"]);

        config.Tools[CanonicalToolNames.RequestChanges] = mapping;
    }

    private static void ConfigureBacklogTools(ToolDispatcherConfiguration config)
    {
        // get_task
        var getTask = new ToolMapping
        {
            Aliases = new HashSet<string>(
                ["view_task", "read_task", "get_task", "gettask", "show_task", "inspect_task", "fetch_task", "task_details"],
                StringComparer.OrdinalIgnoreCase)
        };
        getTask.AddParameterAliases("task_id", TaskIdAliases);
        config.Tools[CanonicalToolNames.GetTask] = getTask;

        // edit_task
        var editTask = new ToolMapping
        {
            Aliases = new HashSet<string>(
                ["update_task", "edit_task", "edittask", "updatetask", "modify_task", "patch_task", "change_task"],
                StringComparer.OrdinalIgnoreCase)
        };
        editTask.AddParameterAliases("task_id", TaskIdAliases);
        editTask.AddParameterAliases("title", ["name", "task_title", "taskTitle", "summary", "header"]);
        editTask.AddParameterAliases("description", ["desc", "details", "body", "prompt", "specification", "spec", "content"]);
        editTask.AddParameterAliases("priority", PriorityAliases);
        editTask.AddParameterAliases("depends_on_ids", DependsOnIdsAliases);
        editTask.AddParameterAliases("merge_mode", ["mergeMode", "merge", "merge_strategy", "mergeStrategy"]);
        editTask.AddParameterAliases("base_branch", ["baseBranch", "branch", "target_branch", "targetBranch"]);
        config.Tools[CanonicalToolNames.EditTask] = editTask;

        // set_task_priority
        var setTaskPriority = new ToolMapping
        {
            Aliases = new HashSet<string>(
                ["change_task_priority", "update_task_priority", "prioritize_task", "set_task_priority", "settaskpriority", "set_priority"],
                StringComparer.OrdinalIgnoreCase)
        };
        setTaskPriority.AddParameterAliases("task_id", TaskIdAliases);
        setTaskPriority.AddParameterAliases("priority", PriorityAliases);
        config.Tools[CanonicalToolNames.SetTaskPriority] = setTaskPriority;

        // rework_priorities
        var reworkPriorities = new ToolMapping
        {
            Aliases = new HashSet<string>(
                ["reorder_tasks", "reprioritize_tasks", "reprioritize", "batch_set_priority", "batch_set_priorities", "update_priorities", "set_priorities", "rework_priorities", "reworkpriorities", "rework_priority", "set_task_priorities"],
                StringComparer.OrdinalIgnoreCase)
        };
        reworkPriorities.AddParameterAliases("updates", ["items", "tasks", "priorities", "changes", "entries", "list"]);
        reworkPriorities.AddNestedParameterAlias("updates", "taskId", [.. NestedTaskIdAliases]);
        reworkPriorities.AddNestedParameterAlias("updates", "priority", [.. PriorityAliases]);
        config.Tools[CanonicalToolNames.ReworkPriorities] = reworkPriorities;

        // remove_task
        var removeTask = new ToolMapping
        {
            Aliases = new HashSet<string>(
                ["delete_task", "remove_task", "removetask", "deletetask", "cancel_task", "drop_task", "discard_task", "archive_task"],
                StringComparer.OrdinalIgnoreCase)
        };
        removeTask.AddParameterAliases("task_id", TaskIdAliases);
        removeTask.AddParameterAliases("reason", ["rationale", "why", "comment", "message", "note", "explanation"]);
        config.Tools[CanonicalToolNames.RemoveTask] = removeTask;
    }

    private static void ConfigureTranslations(ToolDispatcherConfiguration config)
    {
        var listDir = new ToolTranslation
        {
            SourceTool = "list_dir",
            TargetTool = CanonicalToolNames.Bash,
            CommandTemplate = "ls -la {path:.}",
            EscapeMethod = ParameterEscapeMethod.Bash,
            Aliases = new HashSet<string>(["listdir", "list_directory", "ls"], StringComparer.OrdinalIgnoreCase)
        };
        foreach (var alias in ListDirPathAliases)
        {
            listDir.ParameterAliases[alias] = "path";
        }
        config.Translations.Add(listDir);

        var grepSearch = new ToolTranslation
        {
            SourceTool = "grep_search",
            TargetTool = CanonicalToolNames.Bash,
            CommandTemplate = "rg -n -- {query} {path:.}",
            EscapeMethod = ParameterEscapeMethod.Bash,
            Aliases = new HashSet<string>(["grep", "search_files", "find_in_files"], StringComparer.OrdinalIgnoreCase)
        };
        foreach (var alias in GrepQueryAliases)
        {
            grepSearch.ParameterAliases[alias] = "query";
        }
        foreach (var alias in GrepPathAliases)
        {
            grepSearch.ParameterAliases[alias] = "path";
        }
        config.Translations.Add(grepSearch);

        var fileOutline = new ToolTranslation
        {
            SourceTool = "get_file_outline",
            TargetTool = CanonicalToolNames.Bash,
            CommandTemplate = "rg -n \"^(public|private|protected|internal|class|interface|record|def|function)\" {path}",
            EscapeMethod = ParameterEscapeMethod.Bash,
            Aliases = new HashSet<string>(["outline", "view_outline", "file_outline"], StringComparer.OrdinalIgnoreCase)
        };
        foreach (var alias in FileOutlinePathAliases)
        {
            fileOutline.ParameterAliases[alias] = "path";
        }
        config.Translations.Add(fileOutline);

        var findFiles = new ToolTranslation
        {
            SourceTool = "find_files",
            TargetTool = CanonicalToolNames.Bash,
            CommandTemplate = "find {path:.} -maxdepth 4 -iname {pattern:*}",
            EscapeMethod = ParameterEscapeMethod.Bash,
            Aliases = new HashSet<string>(["find", "findfiles", "locate_files"], StringComparer.OrdinalIgnoreCase)
        };
        foreach (var alias in FindFilesPathAliases)
        {
            findFiles.ParameterAliases[alias] = "path";
        }
        foreach (var alias in FindFilesPatternAliases)
        {
            findFiles.ParameterAliases[alias] = "pattern";
        }
        config.Translations.Add(findFiles);
    }
}
