using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Composable tool module encapsulating workspace file operations (<c>read</c>, <c>write</c>, <c>edit</c>),
/// fuzzy indentation-tolerant matching, <see cref="ReadOutputFormatter"/> byte-budgeting, <c>.editorconfig</c>
/// formatting, and <see cref="ITaskMarkdownValidator"/> dependency-cycle validation.
/// </summary>
public sealed partial class WorkspaceFileToolModule : IAgentToolModule
{
    public const string MissingModuleInstanceMessage = IAgentToolModule.MissingBoundModuleInstanceMessage;
    public const string MissingEditParametersMessage = "Error: Edit failed: either 'edits' array or both 'old_text' and 'new_text' must be provided.";

    public static readonly HashSet<string> IndentationSignificantExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".yaml", ".yml", ".nim", ".pug", "makefile"
    };

    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex NewlineRegex();

    private readonly int? _workspaceId;
    private readonly int? _taskId;
    private readonly bool _isReadOnly;
    private readonly int _maxReadFileBytes;
    private readonly IEditorConfigService _editorConfigService;
    private readonly IEditorConfigFileApplier _editorConfigFileApplier;
    private readonly ILogger _logger;
    private ITaskMarkdownValidator? _defaultTaskMarkdownValidator;
    private ITaskMarkdownValidator? _explicitTaskMarkdownValidator;
    private bool _hasExplicitTaskMarkdownValidator;

    public ISandboxPathResolver SandboxPathResolver { get; set; }

    public IServiceScopeFactory? ScopeFactory { get; set; }

    public ITaskMarkdownValidator? TaskMarkdownValidator
    {
        get => _hasExplicitTaskMarkdownValidator ? _explicitTaskMarkdownValidator : _defaultTaskMarkdownValidator;
        set
        {
            _explicitTaskMarkdownValidator = value;
            _hasExplicitTaskMarkdownValidator = true;
        }
    }

    public int MaxReadFileBytes => _maxReadFileBytes;

    private bool IsEffectiveReadOnly => _isReadOnly || SandboxPathResolver?.Scope?.IsReadOnly == true;

    public WorkspaceFileToolModule(
        CanonicalToolScope scope,
        ISandboxPathResolver sandboxPathResolver,
        IEditorConfigService editorConfigService,
        IEditorConfigFileApplier editorConfigFileApplier,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(scope);
        SandboxPathResolver = sandboxPathResolver ?? throw new ArgumentNullException(nameof(sandboxPathResolver));
        _editorConfigService = editorConfigService ?? throw new ArgumentNullException(nameof(editorConfigService));
        _editorConfigFileApplier = editorConfigFileApplier ?? throw new ArgumentNullException(nameof(editorConfigFileApplier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _workspaceId = scope.WorkspaceId;
        _taskId = (scope.TaskId.HasValue && scope.TaskId.Value > 0) ? scope.TaskId.Value : null;
        _isReadOnly = scope.IsReadOnly;

        if (scope.MaxReadFileBytes.HasValue && scope.MaxReadFileBytes.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope.MaxReadFileBytes.Value, "MaxReadFileBytes must be greater than zero.");
        }

        _maxReadFileBytes = scope.MaxReadFileBytes ?? WorkflowDefaults.MaxReadFileBytes;
    }

    public WorkspaceFileToolModule(
        CanonicalToolScope scope,
        ISandboxPathResolver sandboxPathResolver,
        IEditorConfigService editorConfigService,
        IEditorConfigFileApplier editorConfigFileApplier,
        IServiceScopeFactory scopeFactory,
        ILogger logger)
        : this(scope, sandboxPathResolver, editorConfigService, editorConfigFileApplier, logger)
    {
        ScopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public void ConfigureDefaultTaskMarkdownValidator(ITaskMarkdownValidator? defaultValidator, IServiceScopeFactory? scopeFactory)
    {
        _defaultTaskMarkdownValidator = defaultValidator;
        if (scopeFactory != null)
        {
            ScopeFactory = scopeFactory;
        }
    }

    public IReadOnlyList<AIFunction> CreateFunctions() => CreateFunctions(this, _maxReadFileBytes);

    public static IReadOnlyList<AIFunction> CreateFunctions(WorkspaceFileToolModule? module, int maxReadFileBytes)
    {
        int capKb = maxReadFileBytes / 1024;
        return
        [
            AIFunctionFactory.Create(
                ([Description("Workspace-relative path to read")] string path,
                 [Description("1-indexed start line offset")] int? offset = null,
                 [Description("Maximum lines to read")] int? limit = null,
                 CancellationToken ct = default) =>
                    module != null
                        ? module.ReadAsync(path, offset, limit, ct)
                        : throw new InvalidOperationException(MissingModuleInstanceMessage),
                CanonicalToolNames.Read,
                $"Reads a file with line numbers and optional line-based pagination. Output is capped to {capKb} KB to avoid overflowing context; use offset and limit to page through large files."),

            AIFunctionFactory.Create(
                async ([Description("Workspace-relative path to write")] string path,
                       [Description("Full file content")] string content,
                       CancellationToken ct = default) =>
                {
                    if (module == null)
                    {
                        throw new InvalidOperationException(MissingModuleInstanceMessage);
                    }

                    try
                    {
                        return await module.WriteAsync(path, content, ct);
                    }
                    catch (InvalidOperationException ex)
                    {
                        return $"Error: {ex.Message}";
                    }
                },
                CanonicalToolNames.Write,
                "Creates or overwrites a workspace file."),

            AIFunctionFactory.Create(
                async ([Description("Workspace-relative path to edit")] string path,
                       [Description("One or more disjoint edits applied in order; each chunk provides oldText (or old_text), newText (or new_text), and optional replaceAll (or replace_all)")] IReadOnlyList<EditChunk>? edits = null,
                       [Description("Shortcut for a single edit: exact character sequence to be replaced")] string? old_text = null,
                       [Description("Shortcut for a single edit: replacement character sequence")] string? new_text = null,
                       [Description("When true, replaces all occurrences of old_text")] bool replace_all = false,
                       CancellationToken ct = default) =>
                {
                    if (module == null)
                    {
                        throw new InvalidOperationException(MissingModuleInstanceMessage);
                    }

                    try
                    {
                        if (edits is { Count: > 0 } && edits.Any(c => !string.IsNullOrEmpty(c.OldText) || !string.IsNullOrEmpty(c.NewText)))
                        {
                            var effectiveEdits = replace_all
                                ? edits.Select(e => e.ReplaceAll ? e : e with { ReplaceAll = true }).ToList()
                                : edits;
                            return await module.EditAsync(path, effectiveEdits, ct);
                        }

                        if (old_text != null && new_text != null)
                        {
                            return await module.EditAsync(path, old_text, new_text, replace_all, ct);
                        }

                        return MissingEditParametersMessage;
                    }
                    catch (InvalidOperationException ex)
                    {
                        return $"Error: {ex.Message}";
                    }
                },
                CanonicalToolNames.Edit,
                "Replaces exact text snippets in a workspace file. Use 'edits' for one or several disjoint replacements in a single call, or 'replace_all' to replace all occurrences.")
        ];
    }

    public string ResolveFullPath(string path, bool forWrite = false) =>
        SandboxPathResolver.ResolveHostPath(path, forWrite);

    public async Task<string> ReadAsync(string path, int? offset = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        string fullPath;
        try
        {
            fullPath = SandboxPathResolver.ResolveHostPath(path, forWrite: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Error: {ex.Message}";
        }

        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("CanonicalTools.read: File not found '{Path}' (resolved: '{FullPath}')", path, fullPath);
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

        return ReadOutputFormatter.FormatCapped(selectedLines, startLine, lines.Length, _maxReadFileBytes);
    }

    public async Task<string> WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        if (IsEffectiveReadOnly)
        {
            throw new InvalidOperationException("Write operations are not permitted in read-only mode.");
        }

        string fullPath;
        try
        {
            fullPath = SandboxPathResolver.ResolveHostPath(path, forWrite: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Error: {ex.Message}";
        }

        var rejection = await ValidateTaskSpecificationAsync(path, content, cancellationToken);
        if (rejection != null)
        {
            return rejection;
        }

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

    public async Task<string> EditAsync(string path, IReadOnlyList<EditChunk> edits, CancellationToken cancellationToken = default)
    {
        if (IsEffectiveReadOnly)
        {
            throw new InvalidOperationException("Edit operations are not permitted in read-only mode.");
        }

        if (edits == null || edits.Count == 0)
        {
            return "Error: At least one edit chunk must be provided.";
        }

        string fullPath;
        try
        {
            fullPath = SandboxPathResolver.ResolveHostPath(path, forWrite: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Error: {ex.Message}";
        }

        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("CanonicalTools.edit: File not found '{Path}' (resolved: '{FullPath}')", path, fullPath);
            return $"Error: File not found for edit: '{path}'";
        }

        var originalContent = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var currentContent = originalContent;
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        bool isStrictWhitespace = IsIndentationSignificant(ext);
        int totalOccurrencesReplaced = 0;

        try
        {
            for (int i = 0; i < edits.Count; i++)
            {
                var edit = edits[i];
                var (updatedContent, occurrences) = ApplySingleEdit(currentContent, edit.OldText, edit.NewText, edit.ReplaceAll, isStrictWhitespace, path, i + 1);
                currentContent = updatedContent;
                totalOccurrencesReplaced += occurrences;
            }
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }

        var rejection = await ValidateTaskSpecificationAsync(path, currentContent, cancellationToken);
        if (rejection != null)
        {
            return rejection;
        }

        var editorConfig = await _editorConfigService.GetSettingsForPathAsync(fullPath, cancellationToken);
        var formattedBytes = _editorConfigFileApplier.ApplyFormattingAndEncode(currentContent, editorConfig, originalContent, fullPath);

        await File.WriteAllBytesAsync(fullPath, formattedBytes, cancellationToken);
        return $"Successfully applied {edits.Count} edit(s) ({totalOccurrencesReplaced} occurrence(s) replaced) to '{path}'.";
    }

    public Task<string> EditAsync(string path, string oldText, string newText, bool replaceAll = false, CancellationToken cancellationToken = default) =>
        EditAsync(path, [new EditChunk(oldText, newText, replaceAll)], cancellationToken);

    private async Task<string?> ValidateTaskSpecificationAsync(string path, string prospectiveContent, CancellationToken cancellationToken)
    {
        if (!_workspaceId.HasValue)
        {
            return null;
        }

        if (_hasExplicitTaskMarkdownValidator)
        {
            return await RunValidatorIfApplicableAsync(_explicitTaskMarkdownValidator, _workspaceId.Value, _taskId, path, prospectiveContent, cancellationToken);
        }

        if (ScopeFactory != null)
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            var scopedValidator = scope.ServiceProvider.GetRequiredService<ITaskMarkdownValidator>();
            return await RunValidatorIfApplicableAsync(scopedValidator, _workspaceId.Value, _taskId, path, prospectiveContent, cancellationToken);
        }

        return await RunValidatorIfApplicableAsync(_defaultTaskMarkdownValidator, _workspaceId.Value, _taskId, path, prospectiveContent, cancellationToken);
    }

    private static async Task<string?> RunValidatorIfApplicableAsync(
        ITaskMarkdownValidator? validator,
        int workspaceId,
        int? taskId,
        string path,
        string prospectiveContent,
        CancellationToken cancellationToken)
    {
        if (validator == null || !validator.IsTaskSpecificationPath(path))
        {
            return null;
        }

        var result = await validator.ValidateAsync(workspaceId, taskId, path, prospectiveContent, cancellationToken);
        return result.IsValid ? null : result.ErrorMessage;
    }

    private (string Content, int Occurrences) ApplySingleEdit(string source, string oldText, string newText, bool replaceAll, bool isStrictWhitespace, string path, int editIndex)
    {
        if (replaceAll)
        {
            if (string.IsNullOrEmpty(oldText))
            {
                throw new InvalidOperationException($"Edit #{editIndex} failed: oldText cannot be empty.");
            }

            string effectiveOldText = oldText;
            string effectiveNewText = newText ?? string.Empty;
            if (effectiveOldText.Contains('\n') && !source.Contains(effectiveOldText, StringComparison.Ordinal))
            {
                if (source.Contains("\r\n") && !effectiveOldText.Contains("\r\n"))
                {
                    effectiveOldText = effectiveOldText.Replace("\n", "\r\n");
                    effectiveNewText = effectiveNewText.Replace("\n", "\r\n");
                }
                else if (!source.Contains("\r\n") && effectiveOldText.Contains("\r\n"))
                {
                    effectiveOldText = effectiveOldText.Replace("\r\n", "\n");
                    effectiveNewText = effectiveNewText.Replace("\r\n", "\n");
                }
            }

            int count = 0;
            int searchIdx = 0;
            while ((searchIdx = source.IndexOf(effectiveOldText, searchIdx, StringComparison.Ordinal)) != -1)
            {
                count++;
                searchIdx += effectiveOldText.Length;
            }

            if (count == 0)
            {
                throw new InvalidOperationException($"Edit #{editIndex} failed: could not locate target chunk in '{path}'.");
            }

            var replaced = source.Replace(effectiveOldText, effectiveNewText, StringComparison.Ordinal);
            return (replaced, count);
        }

        int matchIndex = source.IndexOf(oldText, StringComparison.Ordinal);
        if (matchIndex >= 0)
        {
            int nextMatch = source.IndexOf(oldText, matchIndex + oldText.Length, StringComparison.Ordinal);
            if (nextMatch >= 0)
            {
                throw new InvalidOperationException($"Edit #{editIndex} failed: oldText matches multiple locations in '{path}'. Please provide more surrounding context to disambiguate or use replaceAll: true.");
            }

            return (source.Substring(0, matchIndex) + newText + source.Substring(matchIndex + oldText.Length), 1);
        }

        if (isStrictWhitespace)
        {
            throw new InvalidOperationException($"Edit #{editIndex} failed: exact oldText not found in whitespace-sensitive file '{path}'. Exact indentation is required.");
        }

        return (ApplyIndentationTolerantEdit(source, oldText, newText, path, editIndex), 1);
    }

    private static string ApplyIndentationTolerantEdit(string source, string oldText, string newText, string path, int editIndex)
    {
        var sourceLines = NewlineRegex().Split(source);
        var oldLines = NewlineRegex().Split(oldText);
        var trimmedOldLines = oldLines.Select(l => l.Trim()).ToArray();

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

        string fileBaseIndent = GetLeadingWhitespace(sourceLines[matchLineStart]);
        string oldBaseIndent = GetLeadingWhitespace(oldLines[patternStart]);

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

    private static bool IsIndentationSignificant(string extension) =>
        IndentationSignificantExtensions.Contains(extension);
}
