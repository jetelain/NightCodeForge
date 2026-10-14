using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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

    [GeneratedRegex(@"^```(?:json|xml)?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownFenceStartRegex();

    [GeneratedRegex(@"\s*```$")]
    private static partial Regex MarkdownFenceEndRegex();

    public IReadOnlyCollection<ToolDefinition> RegisteredTools => _tools.Values.ToList();

    public LenientToolDispatcher(CanonicalTools? canonicalTools = null)
    {
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
    /// Parses a raw tool invocation name and payload string into a normalized canonical call.
    /// </summary>
    public DispatchedToolCall NormalizeCall(string rawToolName, string rawArgumentsJson)
    {
        var sanitizedJson = SanitizeJsonPayload(rawArgumentsJson);
        var parsedArgs = ParseAndCoerceArguments(sanitizedJson);

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
            return await tool.Handler(args, cancellationToken);
        }

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
        catch { }

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

        var contentKeys = new[] { "content", "file_content", "text", "CodeContent", "code_content" };
        foreach (var ck in contentKeys)
        {
            if (!norm.ContainsKey("content") && raw.TryGetValue(ck, out var val) && val != null)
            {
                norm["content"] = val;
                break;
            }
        }

        var offsetKeys = new[] { "offset", "start_line", "startLine", "from_line", "line_start", "start", "StartLine" };
        foreach (var ok in offsetKeys)
        {
            if (!norm.ContainsKey("offset") && raw.TryGetValue(ok, out var val) && val != null)
            {
                norm["offset"] = CoerceToInt(val);
                break;
            }
        }

        var limitKeys = new[] { "limit", "end_line", "endLine", "to_line", "line_end", "end", "EndLine" };
        foreach (var lk in limitKeys)
        {
            if (!norm.ContainsKey("limit") && raw.TryGetValue(lk, out var val) && val != null)
            {
                norm["limit"] = CoerceToInt(val);
                break;
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

        // Map edits
        if (raw.TryGetValue("edits", out var editsVal) && editsVal != null)
        {
            norm["edits"] = editsVal;
        }
        else
        {
            var oldKeys = new[] { "oldText", "old_text", "old_str", "search", "target_string", "targetContent", "TargetContent", "target_content" };
            var newKeys = new[] { "newText", "new_text", "new_str", "replace", "new_string", "replacementContent", "ReplacementContent", "replacement_content" };

            object? oldVal = null;
            object? newVal = null;
            foreach (var ok in oldKeys) if (raw.TryGetValue(ok, out oldVal) && oldVal != null) break;
            foreach (var nk in newKeys) if (raw.TryGetValue(nk, out newVal) && newVal != null) break;

            if (oldVal != null && newVal != null)
            {
                norm["edits"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["oldText"] = oldVal, ["newText"] = newVal }
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
            "read",
            "Read file contents with line slicing (path, offset, limit)",
            async (args, ct) =>
            {
                var path = GetRequiredString(args, "path");
                var offset = GetOptionalInt(args, "offset");
                var limit = GetOptionalInt(args, "limit");
                return await tools.ReadAsync(path, offset, limit, ct);
            },
            aliases: ["read_file", "readfile", "read_file_content", "cat", "view_file"]));

        RegisterTool(new ToolDefinition(
            "edit",
            "Make precise file edits with exact search-and-replace (path, edits: [{ oldText, newText }])",
            async (args, ct) =>
            {
                var path = GetRequiredString(args, "path");
                var edits = ExtractEdits(args);
                return await tools.EditAsync(path, edits, ct);
            },
            aliases: ["replace_file_content", "replacefilecontent", "replace_content", "replacecontent", "edit_file", "str_replace", "patch_file", "editfile"]));

        RegisterTool(new ToolDefinition(
            "write",
            "Create new files or completely rewrite files (path, content)",
            async (args, ct) =>
            {
                var path = GetRequiredString(args, "path");
                var content = GetRequiredString(args, "content");
                return await tools.WriteAsync(path, content, ct);
            },
            aliases: ["write_file", "writefile", "create_file", "new_file"]));

        RegisterTool(new ToolDefinition(
            "bash",
            "Execute shell commands, builds, and file discovery (command, timeout, working_directory)",
            async (args, ct) =>
            {
                var command = GetRequiredString(args, "command");
                var timeout = GetOptionalInt(args, "timeout");
                var workDir = args.TryGetValue("working_directory", out var wd) ? wd?.ToString() : null;
                return await tools.BashAsync(command, timeout, workDir, ct);
            },
            aliases: ["run_command", "runcommand", "run_cmd", "exec", "execute", "cmd", "terminal", "powershell", "sh"]));

        RegisterTool(new ToolDefinition(
            "ask",
            "Ask clarifying questions to the user when blocked (prompt, rationale, options)",
            (args, _) =>
            {
                var prompt = GetRequiredString(args, "prompt");
                var rationale = args.TryGetValue("rationale", out var r) ? r?.ToString() : null;
                var options = ExtractStringList(args, "options");
                return Task.FromResult(tools.Ask(prompt, rationale, options));
            },
            aliases: ["ask_human_question", "ask_human", "ask_user", "askhumanquestion", "ask_question", "askhuman"]));

        RegisterTool(new ToolDefinition(
            "web_search",
            "Search technical web documentation for APIs, errors, or libraries (query, max_results)",
            async (args, ct) =>
            {
                var query = GetRequiredString(args, "query");
                var maxResults = GetOptionalInt(args, "max_results");
                return await tools.WebSearchAsync(query, maxResults, ct);
            },
            aliases: ["search_web", "google_search", "bing_search", "brave_search", "websearch", "internet_search", "search"]));

        RegisterTool(new ToolDefinition(
            "propose_issues",
            "Propose structured backlog items (issues: [{ title, description, type }])",
            (args, _) =>
            {
                var issues = ExtractIssues(args);
                return Task.FromResult(tools.ProposeIssues(issues));
            },
            aliases: ["propose_issue", "proposeissues"]));

        RegisterTool(new ToolDefinition(
            "request_changes",
            "Reject implementation and send issue back to the implementation agent for rework (feedback, reason)",
            (args, _) =>
            {
                var feedback = GetRequiredString(args, "feedback");
                var reason = args.TryGetValue("reason", out var r) ? r?.ToString() : null;
                return Task.FromResult(tools.RequestChanges(feedback, reason));
            },
            aliases: ["reject_implementation", "requestchanges", "rejectimplementation", "send_back", "sendback"]));

        // Register default translation rules for dropped legacy tools
        RegisterTranslation(new ToolTranslationRule("list_dir", "bash", args =>
        {
            var p = args.TryGetValue("path", out var val) ? val?.ToString() : ".";
            return new Dictionary<string, object?> { ["command"] = $"ls -la {p}" };
        }));

        RegisterTranslation(new ToolTranslationRule("grep_search", "bash", args =>
        {
            var q = args.TryGetValue("query", out var qVal) ? qVal?.ToString() : "";
            var p = args.TryGetValue("path", out var pVal) ? pVal?.ToString() : ".";
            return new Dictionary<string, object?> { ["command"] = $"rg -n \"{q}\" {p}" };
        }));

        RegisterTranslation(new ToolTranslationRule("get_file_outline", "bash", args =>
        {
            var p = args.TryGetValue("path", out var pVal) ? pVal?.ToString() : "";
            return new Dictionary<string, object?> { ["command"] = $"rg -n \"^(public|private|protected|internal|class|interface|record|def|function)\" {p}" };
        }));

        _aliasToCanonical["listdir"] = "list_dir";
        _aliasToCanonical["list_directory"] = "list_dir";
        _aliasToCanonical["ls"] = "list_dir";
        _aliasToCanonical["grep"] = "grep_search";
        _aliasToCanonical["search_files"] = "grep_search";
        _aliasToCanonical["find_in_files"] = "grep_search";
        _aliasToCanonical["outline"] = "get_file_outline";
        _aliasToCanonical["view_outline"] = "get_file_outline";
        _aliasToCanonical["file_outline"] = "get_file_outline";
    }

    private static int? CoerceToInt(object? val)
    {
        if (val == null) return null;
        if (val is int i) return i;
        if (val is long l) return (int)l;
        if (int.TryParse(val.ToString(), out var parsed)) return parsed;
        return null;
    }

    private static string GetRequiredString(Dictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var val) && val != null && !string.IsNullOrWhiteSpace(val.ToString()))
        {
            return val.ToString()!;
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
        if (args.TryGetValue("edits", out var editsObj) && editsObj is IEnumerable<object?> list)
        {
            foreach (var item in list)
            {
                if (item is Dictionary<string, object?> d)
                {
                    string oldText = string.Empty;
                    string newText = string.Empty;

                    foreach (var k in new[] { "oldText", "old_text", "targetContent", "target_content" })
                    {
                        if (d.TryGetValue(k, out var ot) && ot != null) { oldText = ot.ToString()!; break; }
                    }
                    foreach (var k in new[] { "newText", "new_text", "replacementContent", "replacement_content" })
                    {
                        if (d.TryGetValue(k, out var nt) && nt != null) { newText = nt.ToString()!; break; }
                    }

                    chunks.Add(new EditChunk(oldText, newText));
                }
            }
        }

        return chunks;
    }

    private static IReadOnlyList<string>? ExtractStringList(Dictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var obj) && obj is IEnumerable<object?> list)
        {
            return list.Select(x => x?.ToString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s)).ToList();
        }
        return null;
    }

    private static IReadOnlyList<IssueProposal> ExtractIssues(Dictionary<string, object?> args)
    {
        var issues = new List<IssueProposal>();
        if (args.TryGetValue("issues", out var obj) && obj != null)
        {
            if (obj is string str && str.TrimStart().StartsWith("["))
            {
                try
                {
                    var parsedNode = JsonNode.Parse(str);
                    if (parsedNode is JsonArray arr)
                    {
                        obj = arr.Select(ConvertJsonNode).ToList();
                    }
                }
                catch { }
            }

            if (obj is IEnumerable<object?> list)
            {
                foreach (var item in list)
                {
                    if (item is Dictionary<string, object?> d)
                    {
                        var title = d.TryGetValue("title", out var t) ? t?.ToString() ?? "" : "";
                        var desc = d.TryGetValue("description", out var ds) ? ds?.ToString() ?? "" : "";
                        var type = d.TryGetValue("type", out var tp) ? tp?.ToString() ?? "feature" : "feature";
                        issues.Add(new IssueProposal(title, desc, type));
                    }
                    else if (item is JsonElement elem && elem.ValueKind == JsonValueKind.Object)
                    {
                        var title = elem.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                        var desc = elem.TryGetProperty("description", out var ds) ? ds.GetString() ?? "" : "";
                        var type = elem.TryGetProperty("type", out var tp) ? tp.GetString() ?? "feature" : "feature";
                        issues.Add(new IssueProposal(title, desc, type));
                    }
                }
            }
        }
        return issues;
    }
}
