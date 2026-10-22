using System.Text.Json;
using Microsoft.Extensions.AI;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Compaction;

/// <summary>
/// Tracks file read and modification operations across historical tool calls,
/// matching the lossless file operation tracking architecture from Pi.
/// </summary>
public sealed class FileOperationTracker
{
    private readonly HashSet<string> _read = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _edited = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Inspects a tool call and records any file paths being read, written, or edited.
    /// </summary>
    public void RecordToolCall(string toolName, IDictionary<string, object?>? arguments)
    {
        if (string.IsNullOrWhiteSpace(toolName) || arguments == null) return;

        var path = ExtractFilePath(arguments);
        if (string.IsNullOrWhiteSpace(path)) return;

        var normalizedTool = toolName.Trim().ToLowerInvariant();

        if (IsReadTool(normalizedTool))
        {
            _read.Add(path);
        }
        else if (IsWriteTool(normalizedTool))
        {
            _written.Add(path);
        }
        else if (IsEditTool(normalizedTool))
        {
            _edited.Add(path);
        }
    }

    /// <summary>
    /// Inspects an AIContent item from a ChatMessage to extract file operations.
    /// </summary>
    public void RecordContent(AIContent content)
    {
        if (content is FunctionCallContent fcc)
        {
            RecordToolCall(fcc.Name, fcc.Arguments);
        }
    }

    /// <summary>
    /// Inspects a collection of ChatMessages to extract all file operations.
    /// </summary>
    public void RecordMessages(IEnumerable<ChatMessage> messages)
    {
        foreach (var msg in messages)
        {
            foreach (var content in msg.Contents)
            {
                RecordContent(content);
            }
        }
    }

    /// <summary>
    /// Computes the final lists of read-only files and modified files.
    /// Files that were both read and modified are categorized under modified.
    /// </summary>
    public (IReadOnlyList<string> ReadFiles, IReadOnlyList<string> ModifiedFiles) ComputeFileLists()
    {
        var modified = new HashSet<string>(_written, StringComparer.OrdinalIgnoreCase);
        foreach (var editedFile in _edited)
        {
            modified.Add(editedFile);
        }

        var readOnly = _read.Where(f => !modified.Contains(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var modifiedFiles = modified.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        return (readOnly, modifiedFiles);
    }

    /// <summary>
    /// Seeds the tracker with historical file operations extracted from a previous summary XML.
    /// This guarantees lossless preservation of file tracking across repeated compactions, matching Pi.
    /// </summary>
    public void SeedFromSummaryXml(string? summaryText)
    {
        if (string.IsNullOrWhiteSpace(summaryText)) return;

        var readMatch = System.Text.RegularExpressions.Regex.Match(
            summaryText,
            @"<read-files>\s*([\s\S]*?)\s*</read-files>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (readMatch.Success)
        {
            var lines = readMatch.Groups[1].Value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line)) _read.Add(NormalizePath(line));
            }
        }

        var modMatch = System.Text.RegularExpressions.Regex.Match(
            summaryText,
            @"<modified-files>\s*([\s\S]*?)\s*</modified-files>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (modMatch.Success)
        {
            var lines = modMatch.Groups[1].Value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line)) _edited.Add(NormalizePath(line));
            }
        }
    }

    /// <summary>
    /// Directly seeds historical read and modified files from previous compaction details.
    /// </summary>
    public void SeedFromPrevious(IEnumerable<string>? readFiles, IEnumerable<string>? modifiedFiles)
    {
        if (readFiles != null)
        {
            foreach (var f in readFiles)
            {
                if (!string.IsNullOrWhiteSpace(f)) _read.Add(NormalizePath(f));
            }
        }

        if (modifiedFiles != null)
        {
            foreach (var f in modifiedFiles)
            {
                if (!string.IsNullOrWhiteSpace(f)) _edited.Add(NormalizePath(f));
            }
        }
    }

    /// <summary>
    /// Formats computed file lists into structured XML sections for inclusion in summaries.
    /// </summary>
    public string FormatFileOperationsXml()
    {
        var (readFiles, modifiedFiles) = ComputeFileLists();
        var sections = new List<string>();

        if (readFiles.Count > 0)
        {
            sections.Add($"<read-files>\n{string.Join("\n", readFiles)}\n</read-files>");
        }

        if (modifiedFiles.Count > 0)
        {
            sections.Add($"<modified-files>\n{string.Join("\n", modifiedFiles)}\n</modified-files>");
        }

        if (sections.Count == 0) return string.Empty;
        return $"\n\n{string.Join("\n\n", sections)}";
    }

    private static bool IsReadTool(string name) =>
        name is CanonicalToolNames.Read or "read_file" or "view_file" or "cat";

    private static bool IsWriteTool(string name) =>
        name is CanonicalToolNames.Write or "write_file" or "write_to_file";

    private static bool IsEditTool(string name) =>
        name is CanonicalToolNames.Edit or "edit_file" or "replace_file_content" or "multi_replace_file_content" or "patch_file";

    private static readonly string[] CandidatePathKeys = ["path", "filePath", "file_path", "target_file", "targetFile", "absolute_path", "absolutePath", "file", "filename", "fileName"];

    private static string? ExtractFilePath(IDictionary<string, object?> arguments)
    {
        foreach (var key in CandidatePathKeys)
        {
            foreach (var kvp in arguments)
            {
                if (!string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase) || kvp.Value == null) continue;

                if (kvp.Value is string str && !string.IsNullOrWhiteSpace(str))
                {
                    return NormalizePath(str);
                }
                if (kvp.Value is JsonElement je && je.ValueKind == JsonValueKind.String)
                {
                    var s = je.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        return NormalizePath(s);
                    }
                }
            }
        }
        return null;
    }

    private static string NormalizePath(string rawPath) =>
        rawPath.Trim().Trim('\"', '\'').Replace('\\', '/');
}
