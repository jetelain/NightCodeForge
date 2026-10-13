using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tools;

public record EditChunk(string OldText, string NewText);

public record IssueProposal(
    string Title,
    string Description,
    string? Type = "feature",
    IReadOnlyList<string>? AffectedFiles = null,
    IReadOnlyList<string>? AcceptanceCriteria = null);

public partial class CanonicalTools
{
    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex NewlineRegex();

    private readonly string _workspaceRoot;
    private readonly IExecutionEnvironment? _executionEnv;
    private readonly IEditorConfigService _editorConfigService;
    private readonly IEditorConfigFileApplier _editorConfigFileApplier;
    private readonly IWebSearchService _webSearchService;

    public CanonicalTools(
        string workspaceRoot,
        IExecutionEnvironment? executionEnv = null,
        IEditorConfigService? editorConfigService = null,
        IEditorConfigFileApplier? editorConfigFileApplier = null,
        IWebSearchService? webSearchService = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _executionEnv = executionEnv;
        _editorConfigService = editorConfigService ?? new EditorConfigService();
        _editorConfigFileApplier = editorConfigFileApplier ?? new EditorConfigFileApplier();
        _webSearchService = webSearchService ?? new MockWebSearchService();
    }

    public string ResolveFullPath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }
        return Path.GetFullPath(Path.Combine(_workspaceRoot, path));
    }

    /// <summary>
    /// read: Read file contents with line slicing via 1-indexed offset and limit.
    /// </summary>
    public async Task<string> ReadAsync(string path, int? offset = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File not found: '{path}' (resolved: '{fullPath}')");
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
        var fullPath = ResolveFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var editorConfig = await _editorConfigService.GetSettingsForPathAsync(fullPath, cancellationToken);
        string? existing = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath, cancellationToken) : null;
        var bytes = _editorConfigFileApplier.ApplyFormattingAndEncode(content, editorConfig, existing);

        await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken);
        return $"Successfully wrote {bytes.Length} bytes to '{path}'.";
    }

    /// <summary>
    /// edit: Precision search-and-replace chunk editing supporting multi-disjoint edits and indentation tolerance.
    /// </summary>
    public async Task<string> EditAsync(string path, IReadOnlyList<EditChunk> edits, CancellationToken cancellationToken = default)
    {
        if (edits == null || edits.Count == 0)
        {
            throw new ArgumentException("At least one edit chunk must be provided.");
        }

        var fullPath = ResolveFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File not found for edit: '{path}'");
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
        var formattedBytes = _editorConfigFileApplier.ApplyFormattingAndEncode(currentContent, editorConfig, originalContent);

        await File.WriteAllBytesAsync(fullPath, formattedBytes, cancellationToken);
        return $"Successfully applied {edits.Count} edit(s) to '{path}'.";
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

    private static bool IsIndentationSignificant(string extension)
    {
        return extension switch
        {
            ".py" or ".yaml" or ".yml" or ".nim" or ".pug" or "makefile" => true,
            _ => false
        };
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

        var workDir = !string.IsNullOrEmpty(workingDirectory) ? ResolveFullPath(workingDirectory) : _workspaceRoot;

        if (_executionEnv != null)
        {
            var res = await _executionEnv.ExecuteAsync(command, workDir, timeout, null, cancellationToken);
            var output = string.IsNullOrWhiteSpace(res.StandardOutput) ? res.StandardError : res.StandardOutput;
            if (!string.IsNullOrWhiteSpace(res.StandardError) && !string.IsNullOrWhiteSpace(res.StandardOutput))
            {
                output = res.StandardOutput + "\n[STDERR]\n" + res.StandardError;
            }

            return $"Exit Code: {res.ExitCode}\n{output}".TrimEnd();
        }

        throw new InvalidOperationException("No execution environment configured for bash tool.");
    }

    /// <summary>
    /// ask: Asynchronous human interruption yielding HumanInterruptionException.
    /// </summary>
    public string Ask(string prompt, string? rationale = null, IReadOnlyList<string>? options = null)
    {
        string? optionsJson = options != null && options.Count > 0 ? JsonSerializer.Serialize(options) : null;
        var qType = options != null && options.Count > 0 ? QuestionType.SingleChoice : QuestionType.FreeText;
        throw new HumanInterruptionException(prompt, qType, rationale, optionsJson);
    }

    /// <summary>
    /// web_search: Pluggable web search tool.
    /// </summary>
    public async Task<string> WebSearchAsync(string query, int? maxResults = null, CancellationToken cancellationToken = default)
    {
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

    /// <summary>
    /// propose_issues: Propose structured backlog items for triage/discovery.
    /// </summary>
    public string ProposeIssues(IReadOnlyList<IssueProposal> issues)
    {
        if (issues == null || issues.Count == 0)
        {
            return "No issues proposed.";
        }

        var json = JsonSerializer.Serialize(issues, new JsonSerializerOptions { WriteIndented = true });
        return $"Proposed {issues.Count} backlog issue(s):\n{json}";
    }
}
