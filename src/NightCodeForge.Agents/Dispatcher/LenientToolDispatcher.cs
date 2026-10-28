using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Dispatcher;

public record DispatchedToolCall(string CanonicalName, Dictionary<string, object?> Arguments);

/// <summary>
/// Extensible Forgiving Tool Dispatcher: normalizes tool names, parameters, translates dropped/external tools,
/// repairs malformed JSON payloads, handles type coercions, and executes registered tools.
/// </summary>
public partial class LenientToolDispatcher
{
    private readonly ConcurrentDictionary<string, ToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _aliasToCanonical = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ToolTranslationRule> _translations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<LenientToolDispatcher>? _logger;

    [GeneratedRegex(@"^```(?:json|xml)?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownFenceStartRegex();

    [GeneratedRegex(@"\s*```$")]
    private static partial Regex MarkdownFenceEndRegex();

    public IReadOnlyCollection<ToolDefinition> RegisteredTools => _tools.Values.ToList();

    public LenientToolDispatcher()
        : this(null, null)
    {
    }

    public LenientToolDispatcher(CanonicalTools canonicalTools)
        : this(canonicalTools, null)
    {
    }

    public LenientToolDispatcher(CanonicalTools? canonicalTools, ILogger<LenientToolDispatcher>? logger)
    {
        _logger = logger;
        if (canonicalTools != null)
        {
            RegisterCanonicalTools(canonicalTools);
        }
    }

    public void RegisterTool(ToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools[tool.Name] = tool;
        _aliasToCanonical[tool.Name] = tool.Name;

        foreach (var alias in tool.Aliases)
        {
            _aliasToCanonical[alias] = tool.Name;
        }
    }

    /// <summary>
    /// Registers an AIFunction (from Microsoft Agent Framework or MCP) into the dispatcher.
    /// </summary>
    public void RegisterAIFunction(Microsoft.Extensions.AI.AIFunction aiFunction, IEnumerable<string>? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(aiFunction);
        var toolDef = new ToolDefinition(
            aiFunction.Name,
            aiFunction.Description ?? string.Empty,
            async (args, ct) =>
            {
                var funcArgs = new Microsoft.Extensions.AI.AIFunctionArguments();
                foreach (var (k, v) in args)
                {
                    if (v != null) funcArgs[k] = v;
                }
                var result = await aiFunction.InvokeAsync(funcArgs, ct);
                return result?.ToString() ?? string.Empty;
            },
            aliases: aliases);

        RegisterTool(toolDef);
    }

    /// <summary>
    /// Registers multiple AITools (e.g. from Microsoft Agent Framework AIContextProvider or MCP) into the dispatcher.
    /// </summary>
    public void RegisterAITools(IEnumerable<Microsoft.Extensions.AI.AITool>? tools)
    {
        if (tools == null) return;
        foreach (var tool in tools)
        {
            if (tool is Microsoft.Extensions.AI.AIFunction aiFunction)
            {
                RegisterAIFunction(aiFunction);
            }
        }
    }

    public void RegisterTranslation(ToolTranslationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _translations[rule.SourceTool] = rule;
    }

    public bool TryGetTool(string nameOrAlias, out ToolDefinition? tool)
    {
        var canonical = ResolveToolName(nameOrAlias);
        return _tools.TryGetValue(canonical, out tool);
    }

    public string ResolveToolName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return "read";
        var clean = rawName.Trim();

        // Strip prefixes like "default_api:" or "tools."
        var colonIdx = clean.IndexOf(':');
        if (colonIdx >= 0) clean = clean[(colonIdx + 1)..];
        var dotIdx = clean.IndexOf('.');
        if (dotIdx >= 0) clean = clean[(dotIdx + 1)..];

        clean = clean.ToLowerInvariant();

        if (_aliasToCanonical.TryGetValue(clean, out var canonical))
        {
            return canonical;
        }

        return clean;
    }

    /// <summary>
    /// Normalizes a tool name and argument dictionary into a canonical call.
    /// </summary>
    public DispatchedToolCall NormalizeCall(string rawToolName, Dictionary<string, object?> parsedArgs)
    {
        var canonicalName = ResolveToolName(rawToolName);

        // Check if there is a translation rule (e.g. dropped tools -> bash)
        if (_translations.TryGetValue(canonicalName, out var translationRule))
        {
            var transformedArgs = translationRule.TransformArguments(parsedArgs);
            canonicalName = translationRule.TargetTool;
            parsedArgs = transformedArgs;
        }

        var normalizedArgs = NormalizeParameters(canonicalName, parsedArgs);
        return new DispatchedToolCall(canonicalName, normalizedArgs);
    }

    /// <summary>
    /// Parses a raw tool invocation name and payload string into a normalized canonical call.
    /// </summary>
    public DispatchedToolCall NormalizeCall(string rawToolName, string rawArgumentsJson)
    {
        var sanitizedJson = SanitizeJsonPayload(rawArgumentsJson);
        var parsedArgs = ParseAndCoerceArguments(sanitizedJson);
        return NormalizeCall(rawToolName, parsedArgs);
    }

    /// <summary>
    /// Normalizes and executes the tool call against registered tool definitions.
    /// </summary>
    public async Task<string> DispatchAsync(string rawToolName, string rawArgumentsJson, CancellationToken cancellationToken = default)
    {
        var call = NormalizeCall(rawToolName, rawArgumentsJson);
        return await ExecuteCanonicalCallAsync(call.CanonicalName, call.Arguments, cancellationToken);
    }

    public async Task<string> ExecuteCanonicalCallAsync(string canonicalName, Dictionary<string, object?> args, CancellationToken cancellationToken = default)
    {
        if (_tools.TryGetValue(canonicalName, out var tool) && tool.Handler != null)
        {
            try
            {
                return await tool.Handler(args, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "LenientToolDispatcher: Error executing handler for tool '{CanonicalName}'", canonicalName);
                throw;
            }
        }

        _logger?.LogWarning("LenientToolDispatcher: Unknown or unregistered tool: '{CanonicalName}'", canonicalName);
        throw new NotSupportedException($"Unknown or unregistered tool: '{canonicalName}'");
    }

    public static string SanitizeJsonPayload(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "{}";
        var clean = raw.Trim();

        // Strip markdown code fences (```json ... ``` or ``` ...)
        if (clean.StartsWith("```"))
        {
            clean = MarkdownFenceStartRegex().Replace(clean, "");
            clean = MarkdownFenceEndRegex().Replace(clean, "");
            clean = clean.Trim();
        }

        // Try direct parse. If fails, attempt repairing unescaped newlines inside strings
        try
        {
            using var doc = JsonDocument.Parse(clean);
            return clean;
        }
        catch (JsonException)
        {
            return RepairJsonStringNewlines(clean);
        }
    }

    private static string RepairJsonStringNewlines(string json)
    {
        var sb = new StringBuilder();
        bool inString = false;
        bool isEscaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '\\' && inString)
            {
                isEscaped = !isEscaped;
                sb.Append(c);
                continue;
            }

            if (c == '"' && !isEscaped)
            {
                inString = !inString;
                sb.Append(c);
                continue;
            }

            if (inString && (c == '\r' || c == '\n'))
            {
                if (c == '\r' && i + 1 < json.Length && json[i + 1] == '\n')
                {
                    i++;
                }
                sb.Append("\\n");
                isEscaped = false;
                continue;
            }

            isEscaped = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static Dictionary<string, object?> ParseAndCoerceArguments(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonObject obj)
            {
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var (k, v) in obj)
                {
                    dict[k] = ConvertJsonNode(v);
                }
                return dict;
            }
        }
        catch (JsonException)
        {
            // Invalid JSON arguments format; return empty dictionary
        }

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private static object? ConvertJsonNode(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonValue val)
        {
            if (val.TryGetValue<long>(out var l)) return l;
            if (val.TryGetValue<int>(out var i)) return i;
            if (val.TryGetValue<double>(out var d)) return d;
            if (val.TryGetValue<bool>(out var b)) return b;
            if (val.TryGetValue<string>(out var s)) return s;
            return val.ToString();
        }
        if (node is JsonArray arr)
        {
            return arr.Select(ConvertJsonNode).ToList();
        }
        if (node is JsonObject obj)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in obj)
            {
                dict[k] = ConvertJsonNode(v);
            }
            return dict;
        }
        return node.ToString();
    }

    private Dictionary<string, object?> NormalizeParameters(string canonicalName, Dictionary<string, object?> raw)
    {
        var norm = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // Check tool-specific parameter aliases if tool is registered
        if (_tools.TryGetValue(canonicalName, out var toolDef) && toolDef.ParameterAliases.Count > 0)
        {
            foreach (var (alias, canonParam) in toolDef.ParameterAliases)
            {
                if (raw.TryGetValue(alias, out var v) && v != null && !norm.ContainsKey(canonParam))
                {
                    norm[canonParam] = v;
                }
            }
        }

        // Global default parameter mappings
        var pathKeys = new[] { "path", "file_path", "filePath", "filepath", "filename", "file", "target_file", "TargetFile" };
        foreach (var pk in pathKeys)
        {
            if (!norm.ContainsKey("path") && raw.TryGetValue(pk, out var val) && val != null)
            {
                norm["path"] = val;
                break;
            }
        }

        var cmdKeys = new[] { "command", "cmd", "command_line", "exec", "script", "CommandLine" };
        foreach (var ck in cmdKeys)
        {
            if (!norm.ContainsKey("command") && raw.TryGetValue(ck, out var val) && val != null)
            {
                norm["command"] = val;
                break;
            }
        }

        var contentKeys = new[] { "content", "file_content", "text", "code", "CodeContent", "code_content" };
        foreach (var ck in contentKeys)
        {
            if (!norm.ContainsKey("content") && raw.TryGetValue(ck, out var val) && val != null)
            {
                norm["content"] = val;
                break;
            }
        }

        int? resolvedOffset = null;
        var offsetKeys = new[] { "offset", "start_line", "startLine", "from_line", "line_start", "start", "StartLine" };
        foreach (var ok in offsetKeys)
        {
            if (raw.TryGetValue(ok, out var val) && val != null)
            {
                resolvedOffset = CoerceToInt(val);
                if (resolvedOffset != null)
                {
                    norm["offset"] = resolvedOffset;
                    break;
                }
            }
        }

        if (raw.TryGetValue("limit", out var limVal) && limVal != null)
        {
            norm["limit"] = CoerceToInt(limVal);
        }
        else
        {
            var endLineKeys = new[] { "end_line", "endLine", "to_line", "line_end", "end", "EndLine" };
            foreach (var elk in endLineKeys)
            {
                if (raw.TryGetValue(elk, out var val) && val != null)
                {
                    var endLine = CoerceToInt(val);
                    if (endLine != null)
                    {
                        var start = resolvedOffset ?? 1;
                        norm["limit"] = Math.Max(1, endLine.Value - start + 1);
                        break;
                    }
                }
            }
        }

        var queryKeys = new[] { "query", "q", "search_query", "text", "searchQuery" };
        foreach (var qk in queryKeys)
        {
            if (!norm.ContainsKey("query") && raw.TryGetValue(qk, out var val) && val != null)
            {
                norm["query"] = val;
                break;
            }
        }

        var workDirKeys = new[] { "working_directory", "workingDirectory", "workdir", "work_dir", "cwd" };
        foreach (var wdk in workDirKeys)
        {
            if (!norm.ContainsKey("working_directory") && raw.TryGetValue(wdk, out var val) && val != null)
            {
                norm["working_directory"] = val;
                break;
            }
        }

        var skillKeys = new[] { "skill_name", "skillName", "skill", "name" };
        foreach (var sk in skillKeys)
        {
            if (!norm.ContainsKey("skill_name") && raw.TryGetValue(sk, out var val) && val != null)
            {
                norm["skill_name"] = val;
                break;
            }
        }

        var maxResultsKeys = new[] { "max_results", "maxResults", "limit", "count" };
        foreach (var mrk in maxResultsKeys)
        {
            if (!norm.ContainsKey("max_results") && raw.TryGetValue(mrk, out var val) && val != null)
            {
                norm["max_results"] = CoerceToInt(val);
                break;
            }
        }

        if (!norm.ContainsKey("tasks") && raw.TryGetValue("task", out var taskVal) && taskVal != null)
        {
            norm["tasks"] = taskVal;
        }

        var questionKeys = new[] { "questions", "question_items", "questionItems", "items" };
        foreach (var qk in questionKeys)
        {
            if (!norm.ContainsKey("questions") && raw.TryGetValue(qk, out var qVal) && qVal != null)
            {
                norm["questions"] = qVal;
                break;
            }
        }

        var feedbackKeys = new[] { "feedback", "critique", "message", "comments" };
        foreach (var fk in feedbackKeys)
        {
            if (!norm.ContainsKey("feedback") && raw.TryGetValue(fk, out var fVal) && fVal != null)
            {
                norm["feedback"] = fVal;
                break;
            }
        }

        var reasonKeys = new[] { "reason", "comment", "summary" };
        foreach (var rk in reasonKeys)
        {
            if (!norm.ContainsKey("reason") && raw.TryGetValue(rk, out var rVal) && rVal != null)
            {
                norm["reason"] = rVal;
                break;
            }
        }

        // Map edits
        if (raw.TryGetValue("edits", out var editsVal) && editsVal != null)
        {
            norm["edits"] = editsVal;
        }
        else
        {
            var oldKeys = new[] { "oldText", "old_text", "old_str", "search", "target_string", "targetContent", "TargetContent", "target_content" };
            var newKeys = new[] { "newText", "new_text", "new_str", "replace", "new_string", "replacementContent", "ReplacementContent", "replacement_content" };
            var replaceAllKeys = new[] { "replaceAll", "replace_all", "all", "ReplaceAll" };

            object? oldVal = null;
            object? newVal = null;
            bool replaceAllVal = false;
            foreach (var ok in oldKeys) if (raw.TryGetValue(ok, out oldVal) && oldVal != null) break;
            foreach (var nk in newKeys) if (raw.TryGetValue(nk, out newVal) && newVal != null) break;
            foreach (var rk in replaceAllKeys)
            {
                if (raw.TryGetValue(rk, out var ra) && ra != null)
                {
                    if (ra is bool b) { replaceAllVal = b; break; }
                    if (bool.TryParse(ra.ToString(), out var parsedB)) { replaceAllVal = parsedB; break; }
                }
            }

            if (oldVal != null && newVal != null)
            {
                norm["old_text"] = oldVal;
                norm["new_text"] = newVal;
                norm["replace_all"] = replaceAllVal;
                norm["edits"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["oldText"] = oldVal, ["newText"] = newVal, ["replaceAll"] = replaceAllVal }
                };
            }
        }

        // Pass through any other unmapped keys
        foreach (var (k, v) in raw)
        {
            if (!norm.ContainsKey(k))
            {
                norm[k] = v;
            }
        }

        return norm;
    }

    private void RegisterCanonicalTools(CanonicalTools tools)
    {
        RegisterTool(new ToolDefinition(
            CanonicalToolNames.Read,
            "Read file contents with line slicing (path, offset, limit)",
            async (args, ct) =>
            {
                var path = GetRequiredString(args, "path");
                var offset = GetOptionalInt(args, "offset") ?? GetOptionalInt(args, "start_line") ?? GetOptionalInt(args, "startLine");
                var limit = GetOptionalInt(args, "limit");
                if (!limit.HasValue)
                {
                    var endLine = GetOptionalInt(args, "end_line") ?? GetOptionalInt(args, "endLine");
                    if (endLine.HasValue)
                    {
                        var start = offset ?? 1;
                        limit = Math.Max(1, endLine.Value - start + 1);
                    }
                }
                return await tools.ReadAsync(path, offset, limit, ct);
            },
            aliases: ["read_file", "readfile", "read_file_content", "cat", "view_file"]));

        RegisterTool(new ToolDefinition(
            CanonicalToolNames.Edit,
            "Make precise file edits with exact search-and-replace (path, edits: [{ oldText, newText }])",
            async (args, ct) =>
            {
                var path = GetRequiredString(args, "path");
                var edits = ExtractEdits(args);
                if (edits.Count > 0 && edits.Any(c => !string.IsNullOrEmpty(c.OldText) || !string.IsNullOrEmpty(c.NewText)))
                {
                    return await tools.EditAsync(path, edits, ct);
                }

                string? effectiveOld = null;
                string? effectiveNew = null;
                bool replaceAll = false;
                var oldKeys = new[] { "oldText", "old_text", "targetContent", "target_content", "old_str", "search" };
                var newKeys = new[] { "newText", "new_text", "replacementContent", "replacement_content", "new_str", "replace" };
                var replaceAllKeys = new[] { "replaceAll", "replace_all", "all" };
                foreach (var k in oldKeys) { if (args.TryGetValue(k, out var ot) && ot != null) { effectiveOld = ot.ToString(); break; } }
                foreach (var k in newKeys) { if (args.TryGetValue(k, out var nt) && nt != null) { effectiveNew = nt.ToString(); break; } }
                foreach (var k in replaceAllKeys)
                {
                    if (args.TryGetValue(k, out var ra) && ra != null)
                    {
                        if (ra is bool b) { replaceAll = b; break; }
                        if (bool.TryParse(ra.ToString(), out var parsedB)) { replaceAll = parsedB; break; }
                    }
                }

                if (effectiveOld != null && effectiveNew != null)
                {
                    return await tools.EditAsync(path, effectiveOld, effectiveNew, replaceAll, ct);
                }

                return "Error: provide 'edits' (array of { oldText, newText }) or single-edit replacement ('oldText'/'newText' or 'old_text'/'new_text').";
            },
            aliases: ["replace_file_content", "replacefilecontent", "replace_content", "replacecontent", "edit_file", "str_replace", "patch_file", "editfile"]));

        RegisterTool(new ToolDefinition(
            CanonicalToolNames.Write,
            "Create new files or completely rewrite files (path, content)",
            async (args, ct) =>
            {
                var path = GetRequiredString(args, "path");
                var content = args.TryGetValue("content", out var c) && c != null
                    ? c.ToString()!
                    : (args.TryGetValue("code", out var cd) && cd != null ? cd.ToString()! : GetRequiredString(args, "content"));
                return await tools.WriteAsync(path, content, ct);
            },
            aliases: ["write_file", "writefile", "create_file", "new_file"]));

        RegisterTool(new ToolDefinition(
            CanonicalToolNames.Bash,
            "Execute shell commands, builds, and file discovery (command, timeout, working_directory)",
            async (args, ct) =>
            {
                var command = GetRequiredString(args, "command");
                var timeout = GetOptionalInt(args, "timeout");
                var workDir = args.TryGetValue("working_directory", out var wd) && wd != null
                    ? wd.ToString()
                    : (args.TryGetValue("workingDirectory", out var wdCamel) && wdCamel != null
                        ? wdCamel.ToString()
                        : (args.TryGetValue("cwd", out var cwd) && cwd != null
                            ? cwd.ToString()
                            : (args.TryGetValue("workdir", out var wrk) && wrk != null ? wrk.ToString() : null)));
                return await tools.BashAsync(command, timeout, workDir, ct);
            },
            aliases: ["run_command", "runcommand", "run_cmd", "exec", "execute", "cmd", "terminal", "powershell", "sh"]));

        RegisterTool(new ToolDefinition(
            CanonicalToolNames.LoadSkill,
            "Loads full instructions and reference documentation for a modular skill on demand (skill_name)",
            async (args, ct) =>
            {
                var skillName = args.TryGetValue("skill_name", out var s) && s != null
                    ? s.ToString()!
                    : (args.TryGetValue("skillName", out var sn) && sn != null
                        ? sn.ToString()!
                        : (args.TryGetValue("skill", out var sk) && sk != null
                            ? sk.ToString()!
                            : (args.TryGetValue("name", out var n) && n != null ? n.ToString()! : null)));

                if (string.IsNullOrWhiteSpace(skillName))
                {
                    return "Error: skill_name parameter is required.";
                }

                return await tools.LoadSkillAsync(skillName, ct);
            },
            aliases: ["loadskill", "use_skill", "useskill", "get_skill"]));

        RegisterTool(new ToolDefinition(
            CanonicalToolNames.Ask,
            "Consolidate and ask all clarifying questions to the user when blocked. Accepts 'questions' list (each with prompt, optional rationale, optional options).",
            async (args, ct) =>
            {
                var questionItems = ExtractQuestionItems(args);
                return await tools.AskAsync(questionItems, ct);
            },
            aliases: ["ask_human_question", "ask_human", "ask_user", "askhumanquestion", "ask_question", "askhuman"]));

        RegisterTool(new ToolDefinition(
            CanonicalToolNames.WebSearch,
            "Search technical web documentation for APIs, errors, or libraries (query, max_results)",
            async (args, ct) =>
            {
                var query = args.TryGetValue("query", out var q) && q != null
                    ? q.ToString()!
                    : (args.TryGetValue("q", out var qShort) && qShort != null ? qShort.ToString()! : GetRequiredString(args, "query"));
                var maxResults = GetOptionalInt(args, "max_results")
                    ?? GetOptionalInt(args, "maxResults")
                    ?? GetOptionalInt(args, "limit")
                    ?? GetOptionalInt(args, "count");
                return await tools.WebSearchAsync(query, maxResults, ct);
            },
            aliases: ["search_web", "google_search", "bing_search", "brave_search", "websearch", "internet_search", "search"]));

        RegisterTool(new ToolDefinition(
            CanonicalToolNames.ProposeTasks,
            "Propose structured backlog items (tasks: [{ title, description, priority }])",
            (args, _) =>
            {
                args.TryGetValue("tasks", out var tasks);
                var title = args.TryGetValue("title", out var t) ? t?.ToString() : null;
                var description = args.TryGetValue("description", out var d) ? d?.ToString() : null;
                var priority = GetOptionalInt(args, "priority");
                return Task.FromResult(tools.ProposeTasks(tasks, title, description, priority));
            },
            aliases: ["propose_task", "proposetasks"]));

        RegisterTool(new ToolDefinition(
            CanonicalToolNames.RequestChanges,
            "Reject implementation and send task back to the implementation agent for rework (feedback, reason)",
            (args, _) =>
            {
                var feedback = args.TryGetValue("feedback", out var fb) && fb != null
                    ? fb.ToString()!
                    : (args.TryGetValue("critique", out var cr) && cr != null ? cr.ToString()! : GetRequiredString(args, "feedback"));
                var reason = args.TryGetValue("reason", out var r) && r != null
                    ? r.ToString()
                    : (args.TryGetValue("comment", out var cm) ? cm?.ToString() : null);
                return Task.FromResult(tools.RequestChanges(feedback, reason));
            },
            aliases: ["reject_implementation", "requestchanges", "rejectimplementation", "send_back", "sendback"]));

        // Register default translation rules for dropped legacy tools
        RegisterTranslation(new ToolTranslationRule("list_dir", CanonicalToolNames.Bash, args =>
        {
            var p = args.TryGetValue("path", out var val) ? val?.ToString() : ".";
            return new Dictionary<string, object?> { ["command"] = $"ls -la {p}" };
        }));

        RegisterTranslation(new ToolTranslationRule("grep_search", CanonicalToolNames.Bash, args =>
        {
            var q = args.TryGetValue("query", out var qVal) ? qVal?.ToString() : "";
            var p = args.TryGetValue("path", out var pVal) ? pVal?.ToString() : ".";
            return new Dictionary<string, object?> { ["command"] = $"rg -n \"{q}\" {p}" };
        }));

        RegisterTranslation(new ToolTranslationRule("get_file_outline", CanonicalToolNames.Bash, args =>
        {
            var p = args.TryGetValue("path", out var pVal) ? pVal?.ToString() : "";
            return new Dictionary<string, object?> { ["command"] = $"rg -n \"^(public|private|protected|internal|class|interface|record|def|function)\" {p}" };
        }));

        RegisterTranslation(new ToolTranslationRule("find_files", CanonicalToolNames.Bash, args =>
        {
            var pat = args.TryGetValue("pattern", out var pVal) ? pVal?.ToString() : (args.TryGetValue("name", out var nVal) ? nVal?.ToString() : "*");
            var p = args.TryGetValue("path", out var pathVal) ? pathVal?.ToString() : ".";
            return new Dictionary<string, object?> { ["command"] = $"find {p} -name \"{pat}\"" };
        }));

        _aliasToCanonical["listdir"] = "list_dir";
        _aliasToCanonical["list_directory"] = "list_dir";
        _aliasToCanonical["ls"] = "list_dir";
        _aliasToCanonical["grep"] = "grep_search";
        _aliasToCanonical["search_files"] = "grep_search";
        _aliasToCanonical["find_in_files"] = "grep_search";
        _aliasToCanonical["find"] = "find_files";
        _aliasToCanonical["findfiles"] = "find_files";
        _aliasToCanonical["locate_files"] = "find_files";
        _aliasToCanonical["outline"] = "get_file_outline";
        _aliasToCanonical["view_outline"] = "get_file_outline";
        _aliasToCanonical["file_outline"] = "get_file_outline";
    }

    private static int? CoerceToInt(object? val)
    {
        if (val == null) return null;
        if (val is int i) return i;
        if (val is long l) return (int)l;
        if (val is JsonElement je && je.TryGetInt32(out var jeInt)) return jeInt;
        if (int.TryParse(val.ToString(), out var parsed)) return parsed;
        return null;
    }

    private static string GetRequiredString(Dictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var val) && val != null)
        {
            if (val is JsonElement je && je.ValueKind == JsonValueKind.String)
            {
                var s = je.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            else if (!string.IsNullOrWhiteSpace(val.ToString()))
            {
                return val.ToString()!;
            }
        }
        throw new ArgumentException($"Missing required parameter: '{key}'");
    }

    private static int? GetOptionalInt(Dictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var val))
        {
            return CoerceToInt(val);
        }
        return null;
    }

    private static IReadOnlyList<EditChunk> ExtractEdits(Dictionary<string, object?> args)
    {
        var chunks = new List<EditChunk>();
        bool defaultReplaceAll = false;
        var replaceAllKeys = new[] { "replaceAll", "replace_all", "all", "ReplaceAll" };
        foreach (var k in replaceAllKeys)
        {
            if (args.TryGetValue(k, out var ra) && ra != null)
            {
                if (ra is bool b) { defaultReplaceAll = b; break; }
                if (bool.TryParse(ra.ToString(), out var parsedB)) { defaultReplaceAll = parsedB; break; }
            }
        }

        if (!args.TryGetValue("edits", out var editsObj) || editsObj == null)
        {
            return chunks;
        }

        if (editsObj is string str && !string.IsNullOrWhiteSpace(str) && str.TrimStart().StartsWith("["))
        {
            try
            {
                using var doc = JsonDocument.Parse(str);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.Object)
                        {
                            chunks.Add(ExtractChunkFromJsonElement(el));
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Fallback
            }
        }
        else if (editsObj is IEnumerable<EditChunk> typedChunks)
        {
            chunks.AddRange(typedChunks);
        }
        else if (editsObj is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in je.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.Object)
                    {
                        chunks.Add(ExtractChunkFromJsonElement(el));
                    }
                }
            }
        }
        else if (editsObj is IEnumerable<object?> list)
        {
            foreach (var item in list)
            {
                if (item is EditChunk chunk)
                {
                    chunks.Add(chunk);
                }
                else if (item is IDictionary<string, object?> d)
                {
                    chunks.Add(ExtractChunkFromDictionary(d));
                }
                else if (item is JsonElement el && el.ValueKind == JsonValueKind.Object)
                {
                    chunks.Add(ExtractChunkFromJsonElement(el));
                }
                else if (item is JsonObject jo)
                {
                    var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (k, v) in jo)
                    {
                        dict[k] = v?.ToString();
                    }
                    chunks.Add(ExtractChunkFromDictionary(dict));
                }
            }
        }

        if (defaultReplaceAll)
        {
            return chunks.Select(c => c.ReplaceAll ? c : c with { ReplaceAll = true }).ToList();
        }

        return chunks;
    }

    private static EditChunk ExtractChunkFromDictionary(IDictionary<string, object?> dict)
    {
        string old = string.Empty;
        string @new = string.Empty;
        bool replaceAll = false;

        var oldKeys = new[] { "oldText", "old_text", "targetContent", "target_content", "target_string", "search", "old_str", "old" };
        var newKeys = new[] { "newText", "new_text", "replacementContent", "replacement_content", "replacement_string", "replace", "new_str", "new" };
        var replaceAllKeys = new[] { "replaceAll", "replace_all", "all" };

        foreach (var k in oldKeys)
        {
            if (dict.TryGetValue(k, out var ot) && ot != null)
            {
                old = ot.ToString()!;
                break;
            }
        }

        foreach (var k in newKeys)
        {
            if (dict.TryGetValue(k, out var nt) && nt != null)
            {
                @new = nt.ToString()!;
                break;
            }
        }

        foreach (var k in replaceAllKeys)
        {
            if (dict.TryGetValue(k, out var ra) && ra != null)
            {
                if (ra is bool b) { replaceAll = b; break; }
                if (bool.TryParse(ra.ToString(), out var parsedB)) { replaceAll = parsedB; break; }
            }
        }

        return new EditChunk(old, @new, replaceAll);
    }

    private static EditChunk ExtractChunkFromJsonElement(JsonElement el)
    {
        string old = string.Empty;
        string @new = string.Empty;
        bool replaceAll = false;

        var oldKeys = new[] { "oldText", "old_text", "targetContent", "target_content", "target_string", "search", "old_str", "old" };
        var newKeys = new[] { "newText", "new_text", "replacementContent", "replacement_content", "replacement_string", "replace", "new_str", "new" };
        var replaceAllKeys = new[] { "replaceAll", "replace_all", "all" };

        foreach (var k in oldKeys)
        {
            if (el.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String)
            {
                old = p.GetString() ?? string.Empty;
                break;
            }
        }

        foreach (var k in newKeys)
        {
            if (el.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String)
            {
                @new = p.GetString() ?? string.Empty;
                break;
            }
        }

        foreach (var k in replaceAllKeys)
        {
            if (el.TryGetProperty(k, out var p))
            {
                if (p.ValueKind == JsonValueKind.True) { replaceAll = true; break; }
                if (p.ValueKind == JsonValueKind.False) { replaceAll = false; break; }
                if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out var parsedB)) { replaceAll = parsedB; break; }
            }
        }

        return new EditChunk(old, @new, replaceAll);
    }

    private static IReadOnlyList<string>? ExtractStringList(Dictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var obj) && obj is IEnumerable<object?> list)
        {
            return list.Select(x => x?.ToString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s)).ToList();
        }
        return null;
    }

    private static IReadOnlyList<QuestionItem>? ExtractQuestionItems(Dictionary<string, object?> args)
    {
        var questionKeys = new[] { "questions", "Questions", "question", "Question", "items", "Items", "question_items", "questionItems" };
        foreach (var qk in questionKeys)
        {
            if (args.TryGetValue(qk, out var qObj) && qObj != null)
            {
                var parsed = QuestionItemParser.ParseFromObject(qObj);
                if (parsed != null && parsed.Count > 0)
                {
                    return parsed;
                }
            }
        }

        return QuestionItemParser.ParseFromObject(args);
    }
}
