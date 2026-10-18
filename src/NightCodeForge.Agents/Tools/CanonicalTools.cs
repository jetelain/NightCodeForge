using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tools;

public record EditChunk(string OldText, string NewText);

/// <summary>
/// Handler that presents batched questions to a live human and returns the numbered answers without ending the agent turn.
/// </summary>
public delegate Task<string> AskHandler(IReadOnlyList<QuestionItem> questions, CancellationToken cancellationToken);

public partial class CanonicalTools
{
    /// <summary>
    /// Optional interactive handler for the ask tool. When null (pipeline stages), ask throws
    /// HumanInterruptionException so the orchestrator can checkpoint the workspace.
    /// </summary>
    public AskHandler? AskHandler { get; set; }

    /// <summary>
    /// Optional handler for the request_changes tool. When present, invoked before throwing
    /// RequestChangesException so callers (such as agent harnesses) can intercept the rejection.
    /// </summary>
    public Action<string, string?>? RequestChangesHandler { get; set; }

    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex NewlineRegex();

    private readonly string _workspaceRoot;
    private readonly IExecutionEnvironment _executionEnv;
    private readonly IEditorConfigService _editorConfigService;
    private readonly IEditorConfigFileApplier _editorConfigFileApplier;
    private readonly IWebSearchService _webSearchService;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly int? _workspaceId;
    private readonly int? _taskId;

    private readonly int? _buildTimeoutSeconds;
    private readonly int? _testTimeoutSeconds;
    private readonly bool _isReadOnly;
    private readonly ILogger<CanonicalTools> _logger;

    public CanonicalTools(
        string workspaceRoot,
        IWorkspacePathResolver pathResolver,
        IExecutionEnvironment executionEnv,
        IEditorConfigService editorConfigService,
        IEditorConfigFileApplier editorConfigFileApplier,
        IWebSearchService webSearchService,
        ILogger<CanonicalTools> logger)
        : this(workspaceRoot, pathResolver, executionEnv, editorConfigService, editorConfigFileApplier, webSearchService, logger, null, null, null, null, false)
    {
    }

    public CanonicalTools(
        string workspaceRoot,
        IWorkspacePathResolver pathResolver,
        IExecutionEnvironment executionEnv,
        IEditorConfigService editorConfigService,
        IEditorConfigFileApplier editorConfigFileApplier,
        IWebSearchService webSearchService,
        ILogger<CanonicalTools> logger,
        int? buildTimeoutSeconds,
        int? testTimeoutSeconds,
        int? workspaceId,
        int? taskId,
        bool isReadOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _executionEnv = executionEnv ?? throw new ArgumentNullException(nameof(executionEnv));
        _editorConfigService = editorConfigService ?? throw new ArgumentNullException(nameof(editorConfigService));
        _editorConfigFileApplier = editorConfigFileApplier ?? throw new ArgumentNullException(nameof(editorConfigFileApplier));
        _webSearchService = webSearchService ?? throw new ArgumentNullException(nameof(webSearchService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _workspaceRoot = (workspaceRoot.StartsWith('/') || workspaceRoot.StartsWith('~') || workspaceRoot.StartsWith(@"\\") || workspaceRoot.StartsWith("//"))
            ? workspaceRoot
            : Path.GetFullPath(workspaceRoot);
        _buildTimeoutSeconds = buildTimeoutSeconds;
        _testTimeoutSeconds = testTimeoutSeconds;
        _workspaceId = workspaceId;
        _taskId = (taskId.HasValue && taskId.Value > 0) ? taskId.Value : null;
        _isReadOnly = isReadOnly;
    }

    public IWebSearchService WebSearchService => _webSearchService;
    public bool IsWebSearchConfigured => _webSearchService.IsConfigured;

    public string ResolveFullPath(string path)
    {
        if (_workspaceId.HasValue)
        {
            var rootCrucibleDir = _pathResolver.ResolveCruciblePath(_workspaceId.Value, string.Empty, _taskId);
            if (Directory.Exists(rootCrucibleDir))
            {
                return _pathResolver.ResolveCruciblePath(_workspaceId.Value, path, _taskId);
            }

            var cruciblePath = _pathResolver.ResolveCruciblePath(_workspaceId.Value, path, _taskId);
            if (File.Exists(cruciblePath) || Directory.Exists(cruciblePath))
            {
                return cruciblePath;
            }
        }

        return _pathResolver.ResolveCruciblePath(_workspaceRoot, path, _taskId);
    }

    /// <summary>
    /// read: Read file contents with line slicing via 1-indexed offset and limit.
    /// </summary>
    public async Task<string> ReadAsync(string path, int? offset = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveFullPath(path);
        if (!File.Exists(fullPath))
        {
            _logger?.LogWarning("CanonicalTools.read: File not found '{Path}' (resolved: '{FullPath}')", path, fullPath);
            return $"Error: File not found: '{path}'";
        }

        var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);
        int startLine = Math.Max(1, offset ?? 1);
        int startIndex = startLine - 1;

        if (startIndex >= lines.Length)
        {
            return $"[File has {lines.Length} lines. Requested offset {startLine} is beyond end of file.]";
        }

        int count = limit.HasValue ? Math.Max(1, limit.Value) : lines.Length - startIndex;
        var selectedLines = lines.Skip(startIndex).Take(count).ToList();

        var sb = new StringBuilder();
        for (int i = 0; i < selectedLines.Count; i++)
        {
            int lineNum = startLine + i;
            sb.AppendLine($"{lineNum,5} | {selectedLines[i]}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// write: Create a brand-new file or full overwrite enforcing EditorConfig.
    /// </summary>
    public async Task<string> WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        if (_isReadOnly)
        {
            throw new InvalidOperationException("Write operations are not permitted in read-only mode.");
        }

        var fullPath = ResolveFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var editorConfig = await _editorConfigService.GetSettingsForPathAsync(fullPath, cancellationToken);
        string? existing = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath, cancellationToken) : null;
        var bytes = _editorConfigFileApplier.ApplyFormattingAndEncode(content, editorConfig, existing, fullPath);

        await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken);
        return $"Successfully wrote {bytes.Length} bytes to '{path}'.";
    }

    /// <summary>
    /// edit: Precision search-and-replace chunk editing supporting multi-disjoint edits and indentation tolerance.
    /// </summary>
    public async Task<string> EditAsync(string path, IReadOnlyList<EditChunk> edits, CancellationToken cancellationToken = default)
    {
        if (_isReadOnly)
        {
            throw new InvalidOperationException("Edit operations are not permitted in read-only mode.");
        }

        if (edits == null || edits.Count == 0)
        {
            return "Error: At least one edit chunk must be provided.";
        }

        var fullPath = ResolveFullPath(path);
        if (!File.Exists(fullPath))
        {
            _logger?.LogWarning("CanonicalTools.edit: File not found '{Path}' (resolved: '{FullPath}')", path, fullPath);
            return $"Error: File not found for edit: '{path}'";
        }

        var originalContent = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var currentContent = originalContent;
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        bool isStrictWhitespace = IsIndentationSignificant(ext);

        for (int i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            currentContent = ApplySingleEdit(currentContent, edit.OldText, edit.NewText, isStrictWhitespace, path, i + 1);
        }

        // Apply EditorConfig compliance
        var editorConfig = await _editorConfigService.GetSettingsForPathAsync(fullPath, cancellationToken);
        var formattedBytes = _editorConfigFileApplier.ApplyFormattingAndEncode(currentContent, editorConfig, originalContent, fullPath);

        await File.WriteAllBytesAsync(fullPath, formattedBytes, cancellationToken);
        return $"Successfully applied {edits.Count} edit(s) to '{path}'.";
    }

    /// <summary>
    /// edit: Precision search-and-replace for a single chunk.
    /// </summary>
    public Task<string> EditAsync(string path, string oldText, string newText, CancellationToken cancellationToken = default)
    {
        return EditAsync(path, new[] { new EditChunk(oldText, newText) }, cancellationToken);
    }

    private string ApplySingleEdit(string source, string oldText, string newText, bool isStrictWhitespace, string path, int editIndex)
    {
        // Pass 1: Exact substring match
        int matchIndex = source.IndexOf(oldText, StringComparison.Ordinal);
        if (matchIndex >= 0)
        {
            // Verify uniqueness
            int nextMatch = source.IndexOf(oldText, matchIndex + oldText.Length, StringComparison.Ordinal);
            if (nextMatch >= 0)
            {
                throw new InvalidOperationException($"Edit #{editIndex} failed: oldText matches multiple locations in '{path}'. Please provide more surrounding context to disambiguate.");
            }

            return source.Substring(0, matchIndex) + newText + source.Substring(matchIndex + oldText.Length);
        }

        // If language has significant indentation (e.g. Python, YAML), reject non-exact match
        if (isStrictWhitespace)
        {
            throw new InvalidOperationException($"Edit #{editIndex} failed: exact oldText not found in whitespace-sensitive file '{path}'. Exact indentation is required.");
        }

        // Pass 2: Fuzzy Indentation-Tolerant Matching
        return ApplyIndentationTolerantEdit(source, oldText, newText, path, editIndex);
    }

    private string ApplyIndentationTolerantEdit(string source, string oldText, string newText, string path, int editIndex)
    {
        var sourceLines = NewlineRegex().Split(source);
        var oldLines = NewlineRegex().Split(oldText);
        var trimmedOldLines = oldLines.Select(l => l.Trim()).ToArray();

        // Remove empty leading/trailing lines from pattern
        int patternStart = 0;
        while (patternStart < trimmedOldLines.Length && string.IsNullOrWhiteSpace(trimmedOldLines[patternStart]))
            patternStart++;

        int patternEnd = trimmedOldLines.Length - 1;
        while (patternEnd >= patternStart && string.IsNullOrWhiteSpace(trimmedOldLines[patternEnd]))
            patternEnd--;

        if (patternStart > patternEnd)
        {
            throw new InvalidOperationException($"Edit #{editIndex} failed: oldText is empty or whitespace-only.");
        }

        var activePattern = trimmedOldLines[patternStart..(patternEnd + 1)];
        int matchLineStart = -1;
        int matchesFound = 0;

        for (int i = 0; i <= sourceLines.Length - activePattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < activePattern.Length; j++)
            {
                if (sourceLines[i + j].Trim() != activePattern[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                matchLineStart = i;
                matchesFound++;
            }
        }

        if (matchesFound == 0)
        {
            throw new InvalidOperationException($"Edit #{editIndex} failed: could not locate target chunk in '{path}'. Neither exact nor fuzzy indentation match succeeded.");
        }

        if (matchesFound > 1)
        {
            throw new InvalidOperationException($"Edit #{editIndex} failed: fuzzy indentation match found {matchesFound} matching blocks in '{path}'. Please include more unique surrounding lines.");
        }

        // Measure base indentation of target file vs oldText
        string fileBaseIndent = GetLeadingWhitespace(sourceLines[matchLineStart]);
        string oldBaseIndent = GetLeadingWhitespace(oldLines[patternStart]);

        // Re-indent newText
        var newLines = NewlineRegex().Split(newText);
        var reindentedNewLines = new List<string>(newLines.Length);

        foreach (var nl in newLines)
        {
            if (string.IsNullOrWhiteSpace(nl))
            {
                reindentedNewLines.Add(string.Empty);
                continue;
            }

            string nlIndent = GetLeadingWhitespace(nl);
            string trimmedContent = nl.Substring(nlIndent.Length);

            int delta = Math.Max(0, nlIndent.Length - oldBaseIndent.Length);
            string adjustedIndent = fileBaseIndent + new string(' ', delta);
            reindentedNewLines.Add(adjustedIndent + trimmedContent);
        }

        // Detect dominant EOL in source
        string eol = source.Contains("\r\n") ? "\r\n" : "\n";
        var finalLines = new List<string>();
        for (int i = 0; i < matchLineStart; i++) finalLines.Add(sourceLines[i]);
        finalLines.AddRange(reindentedNewLines);
        for (int i = matchLineStart + activePattern.Length; i < sourceLines.Length; i++) finalLines.Add(sourceLines[i]);

        return string.Join(eol, finalLines);
    }

    private static string GetLeadingWhitespace(string line)
    {
        int count = 0;
        while (count < line.Length && (line[count] == ' ' || line[count] == '\t'))
        {
            count++;
        }
        return line.Substring(0, count);
    }

    public static readonly HashSet<string> IndentationSignificantExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".yaml", ".yml", ".nim", ".pug", "makefile"
    };

    private static bool IsIndentationSignificant(string extension)
    {
        return IndentationSignificantExtensions.Contains(extension);
    }

    /// <summary>
    /// bash: Execute shell commands in the execution environment with timeout governance.
    /// </summary>
    public async Task<string> BashAsync(string command, int? timeout = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "[Empty bash command]";
        }

        if (_isReadOnly)
        {
            var (isAllowed, errorMsg) = ReadOnlyCommandValidator.ValidateCommand(command);
            if (!isAllowed)
            {
                _logger?.LogWarning("CanonicalTools.bash: Read-only command violation: '{Command}'. Reason: {Error}", command, errorMsg);
                return $"[READ-ONLY VIOLATION]: {errorMsg}";
            }
        }

        string? workDir = null;
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            if (_executionEnv != null && _executionEnv.Tier != ExecutionTier.BareHost &&
                (workingDirectory.StartsWith('/') || workingDirectory.StartsWith('.') || !Path.IsPathRooted(workingDirectory)))
            {
                workDir = workingDirectory;
            }
            else
            {
                workDir = ResolveFullPath(workingDirectory);
            }
        }
        else if (_executionEnv == null || _executionEnv.Tier == ExecutionTier.BareHost)
        {
            workDir = _workspaceRoot;
        }

        int? effectiveTimeout = timeout;
        if (!effectiveTimeout.HasValue)
        {
            if (command.Contains("test", StringComparison.OrdinalIgnoreCase))
            {
                effectiveTimeout = _testTimeoutSeconds ?? WorkflowDefaults.DefaultTestTimeoutSeconds;
            }
            else if (command.Contains("build", StringComparison.OrdinalIgnoreCase))
            {
                effectiveTimeout = _buildTimeoutSeconds ?? WorkflowDefaults.DefaultBuildTimeoutSeconds;
            }
            else
            {
                effectiveTimeout = WorkflowDefaults.DefaultCommandTimeoutSeconds;
            }
        }

        if (_executionEnv != null)
        {
            NightCodeForge.Core.Interfaces.CommandResult res;
            try
            {
                res = await _executionEnv.ExecuteAsync(command, workDir, effectiveTimeout, null, cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger?.LogWarning("CanonicalTools.bash: Command timed out after {Timeout}s: '{Command}' in '{WorkDir}'", effectiveTimeout, command, workDir);
                return $"Exit Code: {WorkflowDefaults.ProcessTimeoutExitCode}\n[TIMEOUT]: Command execution timed out after {effectiveTimeout}s. Execution was aborted to prevent resource lockup. Check for hanging tests, infinite loops, or deadlocks.";
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "CanonicalTools.bash: Execution failed for '{Command}' in '{WorkDir}'", command, workDir);
                throw;
            }

            var output = string.IsNullOrWhiteSpace(res.StandardOutput) ? res.StandardError : res.StandardOutput;
            if (!string.IsNullOrWhiteSpace(res.StandardError) && !string.IsNullOrWhiteSpace(res.StandardOutput))
            {
                output = res.StandardOutput + "\n[STDERR]\n" + res.StandardError;
            }

            bool isTimeout = res.ExitCode == WorkflowDefaults.ProcessTimeoutExitCode ||
                             res.Truncated && (res.StandardError.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                                               res.StandardOutput.Contains("timed out", StringComparison.OrdinalIgnoreCase));

            if (isTimeout)
            {
                _logger?.LogWarning("CanonicalTools.bash: Command timed out (detected exit code {ExitCode} or buffer): '{Command}'", WorkflowDefaults.ProcessTimeoutExitCode, command);
                return $"Exit Code: {WorkflowDefaults.ProcessTimeoutExitCode}\n[TIMEOUT]: Command execution timed out after {effectiveTimeout}s. Execution was aborted to prevent resource lockup. Check for hanging tests, infinite loops, or deadlocks.";
            }

            if (res.ExitCode != 0)
            {
                _logger?.LogWarning("CanonicalTools.bash: Command returned exit code {ExitCode}: '{Command}'. Output summary: {Output}",
                    res.ExitCode, command, output.Length > WorkflowDefaults.DefaultOutputSummaryTruncationLength ? output[..WorkflowDefaults.DefaultOutputSummaryTruncationLength] + "..." : output);
            }

            if (command.Contains("build", StringComparison.OrdinalIgnoreCase) || command.Contains("test", StringComparison.OrdinalIgnoreCase))
            {
                var extractor = new Optimizations.StructuredDiagnosticExtractor();
                var diag = extractor.ExtractDiagnostics(output);
                if (diag.CompilerErrors.Count > 0 || diag.TestFailures.Count > 0)
                {
                    output = $"{output}\n\n[Structured Diagnostics Extracted]:\n{diag.FormattedMarkdown}";
                }
            }

            return $"Exit Code: {res.ExitCode}\n{output}".TrimEnd();
        }

        _logger?.LogError("CanonicalTools.bash: No execution environment configured for command '{Command}'", command);
        throw new InvalidOperationException("No execution environment configured for bash tool.");
    }

    /// <summary>
    /// ask: Asynchronous human clarification tool accepting a batched list of structured questions.
    /// Returns model-visible validation errors when inputs are invalid.
    /// When an AskHandler is present (ideation), awaits answers and returns numbered Q&A pairs.
    /// Otherwise, throws HumanInterruptionException carrying the full collection.
    /// </summary>
    public async Task<string> AskAsync(IReadOnlyList<QuestionItem>? questions, CancellationToken cancellationToken = default)
    {
        if (questions == null || questions.Count == 0)
        {
            return "Error: At least one question must be provided in 'questions'. Consolidate all clarifying questions into a list.";
        }

        for (int i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            if (string.IsNullOrWhiteSpace(q.Prompt))
            {
                return $"Error: Question at index {i} has a blank or empty prompt.";
            }
        }

        if (AskHandler == null)
        {
            throw new HumanInterruptionException(questions);
        }

        return await AskHandler(questions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// web_search: Pluggable web search tool.
    /// </summary>
    public async Task<string> WebSearchAsync(string query, int? maxResults = null, CancellationToken cancellationToken = default)
    {
        if (!_webSearchService.IsConfigured)
        {
            return "Web search is disabled because no API key is configured. Please solve the task using local repository inspection tools.";
        }

        var response = await _webSearchService.SearchAsync(query, maxResults ?? 5, cancellationToken);
        if (response.Results.Count == 0)
        {
            return $"No web search results found for: '{query}'.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Web search results for: '{query}' ({response.Results.Count} items):");
        for (int i = 0; i < response.Results.Count; i++)
        {
            var r = response.Results[i];
            sb.AppendLine($"[{i + 1}] {r.Title} ({r.Url})");
            sb.AppendLine($"    {r.Content}");
        }
        return sb.ToString().TrimEnd();
    }

    public List<TaskProposal> PendingProposals { get; } = new();

    /// <summary>
    /// propose_tasks: Propose structured backlog items for triage/discovery.
    /// </summary>
    public string ProposeTasks(IReadOnlyList<TaskProposal> tasks)
    {
        return ProposeTasks((object?)tasks, null, null, null);
    }

    /// <summary>
    /// propose_tasks: Flexible overload that handles JSON collections, single-task parameters, and raw JSON strings.
    /// </summary>
    public string ProposeTasks(
        object? tasks = null,
        string? title = null,
        string? description = null,
        int? priority = null)
    {
        var discovered = new List<TaskProposal>();

        if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(description))
        {
            var effectiveTitle = !string.IsNullOrWhiteSpace(title) && !string.Equals(title.Trim(), "Task", StringComparison.OrdinalIgnoreCase)
                ? title.Trim()
                : DeriveTitleFromDescription(description);
            discovered.Add(new TaskProposal(effectiveTitle, description ?? string.Empty, priority ?? 1));
        }

        if (tasks != null)
        {
            if (tasks is IEnumerable<TaskProposal> enumProposals)
            {
                discovered.AddRange(enumProposals);
            }
            else if (tasks is JsonElement je)
            {
                discovered.AddRange(ParseJsonElementTasks(je));
            }
            else if (tasks is string str)
            {
                if (!string.IsNullOrWhiteSpace(str))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(str);
                        discovered.AddRange(ParseJsonElementTasks(doc.RootElement));
                    }
                    catch
                    {
                        // Ignore parse error
                    }
                }
            }
            else
            {
                try
                {
                    var jsonStr = JsonSerializer.Serialize(tasks);
                    using var doc = JsonDocument.Parse(jsonStr);
                    discovered.AddRange(ParseJsonElementTasks(doc.RootElement));
                }
                catch
                {
                    // Ignore parse error
                }
            }
        }

        if (discovered.Count == 0)
        {
            return "No tasks proposed.";
        }

        for (int i = 0; i < discovered.Count; i++)
        {
            var task = discovered[i];
            var deps = task.DependsOnTitles?.ToList() ?? new List<string>();
            if (deps.Count == 0 && i > 0 && discovered.Count > 1)
            {
                deps.Add(discovered[i - 1].Title);
            }
            PendingProposals.Add(task with { DependsOnTitles = deps });
        }

        var json = JsonSerializer.Serialize(PendingProposals, new JsonSerializerOptions { WriteIndented = true });
        return $"Proposed {discovered.Count} backlog task(s):\n{json}";
    }

    private static List<TaskProposal> ParseJsonElementTasks(JsonElement element)
    {
        var list = new List<TaskProposal>();
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var p = ParseSingleTaskElement(item);
                if (p != null) list.Add(p);
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("tasks", out var tasksProp))
            {
                list.AddRange(ParseJsonElementTasks(tasksProp));
            }
            else
            {
                var p = ParseSingleTaskElement(element);
                if (p != null) list.Add(p);
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            var str = element.GetString();
            if (!string.IsNullOrWhiteSpace(str) && str.TrimStart().StartsWith("["))
            {
                try
                {
                    using var innerDoc = JsonDocument.Parse(str);
                    list.AddRange(ParseJsonElementTasks(innerDoc.RootElement));
                }
                catch (JsonException)
                {
                    // String starting with '[' was not valid JSON; fallback to other parsing heuristics
                }
            }
        }
        return list;
    }

    private static TaskProposal? ParseSingleTaskElement(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        string title = string.Empty;
        if (item.TryGetProperty("title", out var t) || item.TryGetProperty("name", out t))
        {
            title = t.GetString() ?? string.Empty;
        }

        string desc = string.Empty;
        if (item.TryGetProperty("description", out var d) || item.TryGetProperty("desc", out d))
        {
            desc = d.GetString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(desc)) return null;

        if (string.IsNullOrWhiteSpace(title) || string.Equals(title.Trim(), "Task", StringComparison.OrdinalIgnoreCase))
        {
            title = DeriveTitleFromDescription(desc);
        }

        int priority = 1;
        if (item.TryGetProperty("priority", out var p) && p.TryGetInt32(out var pVal))
        {
            priority = pVal;
        }

        List<string>? deps = null;
        if (item.TryGetProperty("dependsOnTitles", out var depsProp) && depsProp.ValueKind == JsonValueKind.Array)
        {
            deps = new List<string>();
            foreach (var dep in depsProp.EnumerateArray())
            {
                var depStr = dep.GetString();
                if (!string.IsNullOrEmpty(depStr)) deps.Add(depStr);
            }
        }

        string type = "feature";
        if (item.TryGetProperty("type", out var ty))
        {
            type = ty.GetString() ?? "feature";
        }

        return new TaskProposal(title, desc, type, null, null, priority, deps);
    }

    private static string DeriveTitleFromDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "Task";
        var firstLine = description.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "Task";
        firstLine = firstLine.TrimStart('#', ' ', '-', '*', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ':');
        if (firstLine.Length > 80)
        {
            var truncated = firstLine[..80];
            var lastSpace = truncated.LastIndexOf(' ');
            if (lastSpace > 30) truncated = truncated[..lastSpace];
            return truncated;
        }
        return string.IsNullOrWhiteSpace(firstLine) ? "Task" : firstLine;
    }

    /// <summary>
    /// request_changes: Reject implementation and send task back to the implementation agent with detailed feedback.
    /// </summary>
    public string RequestChanges(string feedback, string? reason = null)
    {
        RequestChangesHandler?.Invoke(feedback, reason);
        throw new RequestChangesException(feedback, reason);
    }

    /// <summary>
    /// load_skill: Loads full instructions and reference documentation for a modular skill on demand.
    /// </summary>
    public async Task<string> LoadSkillAsync(string skillName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return "Error: skill_name parameter is required.";
        }

        var skillsService = new Skills.SkillsService();
        return await skillsService.LoadSkillAsync(_workspaceRoot, skillName, cancellationToken);
    }
}

