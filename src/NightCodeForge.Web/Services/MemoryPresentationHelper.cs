using System.Text;
using System.Text.Json;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Memory;

namespace NightCodeForge.Web.Services;

/// <summary>Constants used by the agent memory UI (page, toast, badges).</summary>
public static class MemoryUiConstants
{
    public const string ToastTitle = "🧠 Agent Memory Updated";
    public const string BadgeLabel = "🧠 Memory";
    public const string ScopeLocalLabel = "Local";
    public const string ScopeSharedLabel = "Shared/Git";
    public const string IdeationOrigin = "Ideation";
    public const string SummaryPreviewEllipsis = "…";
    public const string OversizedFileSummary = "(Exceeds 256 KB preview limit)";
    public const string ErrorPrefix = "Error:";
    public const string StageIdPrefix = "stage-";
    public const int MaxSummaryLength = AgentMemoryConstants.MaxCatalogDescriptionLength;
    public const int MaxMemoryFileBytes = 262_144;
    public const int MaxFileNameLength = 100;
    public const double NearBudgetThresholdPercent = 80d;
    public const string FrontmatterDelimiter = "---";
    public const string FrontmatterDescriptionKey = "description:";
    public const string MemoryNotificationTag = "ncf-memory-updated";
    public const string WorkspaceMemoryUrlSuffix = "memory";
    public const string MainPaneElementId = "memory-main-pane";
    public const string ScrollViewerTopIntoViewJsFunction = "NightCodeForge.scrollElementTopIntoViewIfNeeded";

    public static class ToolNames
    {
        public const string Write = "write";
        public const string Edit = "edit";
    }

    public static class ArgumentKeys
    {
        public static readonly string[] PathKeys = ["path", "filePath", "file_path", "AbsolutePath", "TargetFile"];
    }
}

/// <summary>A tool call (write/edit) that targets an agent memory file.</summary>
public sealed record MemoryToolTarget(AgentMemoryScope Scope, string RelativePath, AgentMemoryChangeKind? ChangeKind)
{
    public string BadgeText => MemoryPresentationHelper.FormatBadgeText(Scope, ChangeKind);

    public string BadgeTooltip => $"Open {RelativePath} in Agent Memory";
}

/// <summary>Line / byte budget of the pinned <c>MEMORY.md</c> file.</summary>
public sealed record MemoryBudget(int Lines, int Bytes, int MaxLines, int MaxBytes)
{
    public double LinesPercent => MaxLines <= 0 ? 0 : Math.Min(100d, Lines * 100d / MaxLines);
    public double BytesPercent => MaxBytes <= 0 ? 0 : Math.Min(100d, Bytes * 100d / MaxBytes);
    public bool IsOverBudget => Lines > MaxLines || Bytes > MaxBytes;
    public bool IsNearBudget => !IsOverBudget &&
        (LinesPercent >= MemoryUiConstants.NearBudgetThresholdPercent ||
         BytesPercent >= MemoryUiConstants.NearBudgetThresholdPercent);
}

/// <summary>Resolved deep-link target for the Agent Memory Explorer page.</summary>
public sealed record MemoryDeepLinkResolution(AgentMemoryScope? Scope, string? RelativePath, string? WarningMessage);

/// <summary>Pure presentation logic for agent memory: tool-call detection, labels, toast text, deep links, summaries, budgets.</summary>
public static class MemoryPresentationHelper
{
    public const string ScopeQueryParameter = "scope";
    public const string PathQueryParameter = "path";
    public const string ScopeLocalQueryValue = "local";
    public const string ScopeSharedQueryValue = "shared";

    public static MemoryToolTarget? TryGetToolTarget(string? toolName, string? argumentsJson, string? result = null, bool isError = false)
    {
        if (isError || string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(result) &&
            result.TrimStart().StartsWith(MemoryUiConstants.ErrorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var isWrite = string.Equals(toolName, MemoryUiConstants.ToolNames.Write, StringComparison.OrdinalIgnoreCase);
        var isEdit = string.Equals(toolName, MemoryUiConstants.ToolNames.Edit, StringComparison.OrdinalIgnoreCase);
        if (!isWrite && !isEdit)
        {
            return null;
        }

        var path = TryExtractPath(argumentsJson);
        var scope = AgentMemoryPathClassifier.Classify(path);
        if (path == null || scope == AgentMemoryScope.None)
        {
            return null;
        }

        var kind = isEdit
            ? AgentMemoryChangeKind.Updated
            : (result != null && result.Contains("created", StringComparison.OrdinalIgnoreCase)
                ? AgentMemoryChangeKind.Created
                : AgentMemoryChangeKind.Updated);

        return new MemoryToolTarget(scope, ToMemoryRelativePath(scope, path), kind);
    }

    public static string? TryExtractPath(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var key in MemoryUiConstants.ArgumentKeys.PathKeys)
            {
                if (doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Streaming / partial tool arguments are not valid JSON yet: not a memory target.
            return null;
        }

        return null;
    }

    /// <summary>Converts any classifier-recognised path into the memory-relative path (file name for Local, repo-relative for Shared).</summary>
    public static string ToMemoryRelativePath(AgentMemoryScope scope, string path)
    {
        var p = path.Trim().Replace('\\', '/');
        if (scope == AgentMemoryScope.LocalWorkspace)
        {
            return p[(p.LastIndexOf('/') + 1)..];
        }

        return AgentMemoryPathClassifier.TryGetCanonicalSharedRelativePath(p, repositoryRoot: null, out var canonical)
            ? canonical
            : p.TrimStart('/');
    }

    public static string FormatScopeQueryValue(AgentMemoryScope scope) => scope switch
    {
        AgentMemoryScope.LocalWorkspace => ScopeLocalQueryValue,
        AgentMemoryScope.SharedRepository => ScopeSharedQueryValue,
        _ => string.Empty
    };

    public static bool TryParseScopeQueryValue(string? value, out AgentMemoryScope scope)
    {
        scope = AgentMemoryScope.None;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, ScopeLocalQueryValue, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, nameof(AgentMemoryScope.LocalWorkspace), StringComparison.OrdinalIgnoreCase))
        {
            scope = AgentMemoryScope.LocalWorkspace;
            return true;
        }

        if (string.Equals(trimmed, ScopeSharedQueryValue, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, AgentMemoryConstants.ScopeSharedGitMetadataValue, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, nameof(AgentMemoryScope.SharedRepository), StringComparison.OrdinalIgnoreCase))
        {
            scope = AgentMemoryScope.SharedRepository;
            return true;
        }

        return false;
    }

    public static string BuildMemoryPageUrl(int? workspaceId, AgentMemoryScope? scope = null, string? relativePath = null)
    {
        var queryParts = new List<string>(2);
        if (scope.HasValue && scope.Value != AgentMemoryScope.None)
        {
            var scopeValue = FormatScopeQueryValue(scope.Value);
            if (!string.IsNullOrEmpty(scopeValue))
            {
                queryParts.Add($"{ScopeQueryParameter}={Uri.EscapeDataString(scopeValue)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            queryParts.Add($"{PathQueryParameter}={Uri.EscapeDataString(relativePath.Trim())}");
        }

        var subPath = queryParts.Count > 0
            ? $"{MemoryUiConstants.WorkspaceMemoryUrlSuffix}?{string.Join('&', queryParts)}"
            : MemoryUiConstants.WorkspaceMemoryUrlSuffix;

        return WorkspaceUrlHelper.BuildPath(workspaceId, subPath);
    }

    public static string BuildMemoryNotificationUrl(int workspaceId, string? memoryScope, string? relativePath, string? changeKind)
    {
        if (ParseChangeKind(changeKind) == AgentMemoryChangeKind.Deleted || string.IsNullOrWhiteSpace(relativePath))
        {
            return BuildMemoryPageUrl(workspaceId);
        }

        var parsedScope = ParseScope(memoryScope);
        AgentMemoryScope? scope = parsedScope == AgentMemoryScope.None ? null : parsedScope;
        var normalizedPath = scope.HasValue
            ? ToMemoryRelativePath(scope.Value, relativePath)
            : relativePath.Trim();

        return BuildMemoryPageUrl(workspaceId, scope, normalizedPath);
    }

    public static MemoryDeepLinkResolution ResolveDeepLink(string? scopeParam, string? pathParam)
    {
        var hasScope = !string.IsNullOrWhiteSpace(scopeParam);
        var hasPath = !string.IsNullOrWhiteSpace(pathParam);

        if (!hasScope && !hasPath)
        {
            return new MemoryDeepLinkResolution(null, null, null);
        }

        if (hasScope)
        {
            if (!TryParseScopeQueryValue(scopeParam, out var parsedScope))
            {
                return new MemoryDeepLinkResolution(null, null, $"Unknown memory scope '{scopeParam!.Trim()}'.");
            }

            if (!hasPath)
            {
                return new MemoryDeepLinkResolution(null, null, "Memory deep link is missing a file path.");
            }

            var trimmed = pathParam!.Trim();
            var resolvedPath = AgentMemoryPathClassifier.Classify(trimmed) == parsedScope
                ? ToMemoryRelativePath(parsedScope, trimmed)
                : trimmed;

            return new MemoryDeepLinkResolution(
                parsedScope,
                resolvedPath,
                null);
        }

        var trimmedPath = pathParam!.Trim();
        var classified = AgentMemoryPathClassifier.Classify(trimmedPath);
        if (classified == AgentMemoryScope.SharedRepository)
        {
            return new MemoryDeepLinkResolution(
                AgentMemoryScope.SharedRepository,
                ToMemoryRelativePath(AgentMemoryScope.SharedRepository, trimmedPath),
                null);
        }

        if (classified == AgentMemoryScope.LocalWorkspace)
        {
            return new MemoryDeepLinkResolution(
                AgentMemoryScope.LocalWorkspace,
                ToMemoryRelativePath(AgentMemoryScope.LocalWorkspace, trimmedPath),
                null);
        }

        return new MemoryDeepLinkResolution(AgentMemoryScope.LocalWorkspace, trimmedPath, null);
    }

    public static MemoryBudget ComputeBudgetFromEntry(MemoryFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new MemoryBudget(
            entry.LineCount,
            (int)Math.Min(entry.SizeBytes, int.MaxValue),
            AgentMemoryConstants.MaxPinnedMemoryLines,
            AgentMemoryConstants.MaxPinnedMemoryBytes);
    }

    public static string FormatScopeLabel(AgentMemoryScope scope) => scope switch
    {
        AgentMemoryScope.LocalWorkspace => MemoryUiConstants.ScopeLocalLabel,
        AgentMemoryScope.SharedRepository => MemoryUiConstants.ScopeSharedLabel,
        _ => string.Empty
    };

    public static string FormatBadgeText(AgentMemoryScope scope, AgentMemoryChangeKind? kind)
    {
        var text = $"{MemoryUiConstants.BadgeLabel} · {FormatScopeLabel(scope)}";
        return kind.HasValue ? $"{text} · {kind.Value}" : text;
    }

    /// <summary>Parses the SignalR memory scope metadata value (<c>Local</c> / <c>SharedGit</c>).</summary>
    public static AgentMemoryScope ParseScope(string? metadataValue) => metadataValue switch
    {
        AgentMemoryConstants.ScopeLocalMetadataValue => AgentMemoryScope.LocalWorkspace,
        AgentMemoryConstants.ScopeSharedGitMetadataValue => AgentMemoryScope.SharedRepository,
        _ => AgentMemoryScope.None
    };

    public static AgentMemoryChangeKind? ParseChangeKind(string? value) =>
        Enum.TryParse<AgentMemoryChangeKind>(value, ignoreCase: true, out var kind) ? kind : null;

    /// <summary>Builds the toast message, e.g. <c>Task #42 (Stage 2) created .agents/memory/x.md</c>.</summary>
    public static string FormatToastMessage(int? taskId, string? stageId, string? changeKind, string relativePath, string? summary)
    {
        var origin = FormatOrigin(taskId, stageId);
        var kind = ParseChangeKind(changeKind);
        if (!kind.HasValue)
        {
            return string.IsNullOrWhiteSpace(summary) ? $"{origin} changed {relativePath}" : summary;
        }

        return $"{origin} {kind.Value.ToString().ToLowerInvariant()} {relativePath}";
    }

    private static string FormatOrigin(int? taskId, string? stageId)
    {
        var trimmedStage = stageId?.Trim() ?? string.Empty;
        if (!taskId.HasValue)
        {
            return string.IsNullOrEmpty(trimmedStage) ||
                   string.Equals(trimmedStage, StandardStageIds.Ideation, StringComparison.OrdinalIgnoreCase)
                ? MemoryUiConstants.IdeationOrigin
                : trimmedStage;
        }

        var stageLabel = FormatStageLabel(trimmedStage);
        return string.IsNullOrEmpty(stageLabel)
            ? $"Task #{taskId.Value}"
            : $"Task #{taskId.Value} ({stageLabel})";
    }

    private static string FormatStageLabel(string stageId)
    {
        if (string.IsNullOrEmpty(stageId))
        {
            return string.Empty;
        }

        if (string.Equals(stageId, StandardStageIds.TaskReview, StringComparison.OrdinalIgnoreCase))
        {
            return "Stage 1";
        }

        if (string.Equals(stageId, StandardStageIds.Implementation, StringComparison.OrdinalIgnoreCase))
        {
            return "Stage 2";
        }

        if (string.Equals(stageId, StandardStageIds.CodeReviewAndFix, StringComparison.OrdinalIgnoreCase))
        {
            return "Stage 3";
        }

        if (stageId.StartsWith(MemoryUiConstants.StageIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = stageId[MemoryUiConstants.StageIdPrefix.Length..];
            if (suffix.Length > 0 && suffix.All(char.IsAsciiDigit))
            {
                return $"Stage {suffix}";
            }
        }

        if (stageId.All(char.IsAsciiDigit))
        {
            return $"Stage {stageId}";
        }

        return stageId;
    }

    /// <summary>Extracts a one-line summary: frontmatter <c>description:</c>, else first non-heading non-empty line, else first heading.</summary>
    public static string ExtractSummary(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var lines = content.Replace("\r\n", "\n").Split('\n');
        var start = 0;
        if (lines[0].Trim() == MemoryUiConstants.FrontmatterDelimiter)
        {
            var closingIndex = -1;
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == MemoryUiConstants.FrontmatterDelimiter)
                {
                    closingIndex = i;
                    break;
                }
            }

            if (closingIndex > 0)
            {
                for (var i = 1; i < closingIndex; i++)
                {
                    var line = lines[i].Trim();
                    if (line.StartsWith(MemoryUiConstants.FrontmatterDescriptionKey, StringComparison.OrdinalIgnoreCase))
                    {
                        var description = line[MemoryUiConstants.FrontmatterDescriptionKey.Length..].Trim().Trim('"', '\'');
                        if (description.Length > 0)
                        {
                            return Truncate(description);
                        }
                    }
                }

                start = closingIndex + 1;
            }
            else
            {
                // Lone leading '---' without a closing delimiter is a horizontal rule, not frontmatter.
                start = 1;
            }
        }

        string? firstHeading = null;
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line == MemoryUiConstants.FrontmatterDelimiter)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                var headingText = line.TrimStart('#').Trim();
                if (headingText.Length > 0)
                {
                    firstHeading ??= headingText;
                }

                continue;
            }

            return Truncate(line);
        }

        return firstHeading != null ? Truncate(firstHeading) : string.Empty;
    }

    public static MemoryBudget ComputeBudget(string? content)
    {
        var text = content ?? string.Empty;
        var lines = text.Length == 0 ? 0 : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Length;
        return new MemoryBudget(
            lines,
            Encoding.UTF8.GetByteCount(text),
            AgentMemoryConstants.MaxPinnedMemoryLines,
            AgentMemoryConstants.MaxPinnedMemoryBytes);
    }

    public static bool IsPinnedMemoryFile(string? relativePath) =>
        string.Equals(relativePath, AgentMemoryConstants.PinnedMemoryFileName, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value) =>
        value.Length <= MemoryUiConstants.MaxSummaryLength
            ? value
            : value[..MemoryUiConstants.MaxSummaryLength] + MemoryUiConstants.SummaryPreviewEllipsis;
}
