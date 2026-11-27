using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Web.Services;

public enum MarkdownLinkResolutionKind
{
    Unchanged = 0,
    Rewritten = 1,
    Neutralized = 2
}

public sealed record MarkdownLinkResolution(MarkdownLinkResolutionKind Kind, string? TargetUrl);

/// <summary>
/// Deterministic, synchronous, thread-safe classifier and URL builder for context-aware Markdown links.
/// </summary>
public static partial class MarkdownLinkBuilder
{
    private const string HttpScheme = "http";
    private const string HttpsScheme = "https";
    private const string MailtoScheme = "mailto";

    private const string WorkspaceSegment = "workspace";
    private const string CloneSegment = "clone";
    private const string HostWorkspaceMarker = ".nightcodeforge/wks/";
    private static readonly char[] InvalidFileNameChars = [':', '*', '?', '"', '<', '>', '|'];

    private static readonly HashSet<string> UnsafeSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "javascript",
        "vbscript",
        "data",
        "file",
        "blob",
        "about",
        "tel",
        "sms",
        "ftp",
        "ftps",
        "ws",
        "wss",
        "view-source",
        "chrome",
        "edge",
        "filesystem",
        "jar",
        "urn",
        "intent"
    };

    [GeneratedRegex(@"^task-(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TaskWorktreeSegmentRegex();

    [GeneratedRegex(@"^(.+?):(\d+)(?:-(\d+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingColonLineRangeRegex();

    [GeneratedRegex(@"^(\d+)(?:-(\d+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex LineRangeDigitsOnlyRegex();

    private enum ResolvedSubRootKind
    {
        LocalMemory = 0,
        Repository = 1
    }

    private sealed record ResolvedTarget(
        ResolvedSubRootKind Kind,
        string Path,
        string? TargetRef,
        int? TargetTaskId,
        bool IsExplicitTaskRef);

    /// <summary>
    /// Validates and canonicalizes a flat task markdown path under <c>docs/tasks/</c> or <c>docs/tasks/archive/</c>.
    /// </summary>
    public static bool TryNormalizeTaskMarkdownPath(
        string? path,
        out string canonicalPath,
        out bool isArchived,
        out string fileName)
    {
        canonicalPath = string.Empty;
        isArchived = false;
        fileName = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Trim().Replace('\\', '/');
        if (normalized.StartsWith('/') || ContainsControlCharacters(normalized))
        {
            return false;
        }

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        var segments = normalized.Split('/');
        if (segments.Any(s => string.IsNullOrEmpty(s) || s is "." or ".."))
        {
            return false;
        }

        if (segments.Length == 3 &&
            string.Equals(segments[0], TaskSyncConstants.DocsSegment, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[1], TaskSyncConstants.TasksSegment, StringComparison.OrdinalIgnoreCase) &&
            IsFlatMarkdownFileName(segments[2]))
        {
            isArchived = false;
            fileName = segments[2];
            canonicalPath = $"{TaskSyncConstants.DocsSegment}/{TaskSyncConstants.TasksSegment}/{fileName}";
            return true;
        }

        if (segments.Length == 4 &&
            string.Equals(segments[0], TaskSyncConstants.DocsSegment, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[1], TaskSyncConstants.TasksSegment, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[2], TaskSyncConstants.ArchiveSegment, StringComparison.OrdinalIgnoreCase) &&
            IsFlatMarkdownFileName(segments[3]))
        {
            isArchived = true;
            fileName = segments[3];
            canonicalPath = $"{TaskSyncConstants.DocsSegment}/{TaskSyncConstants.TasksSegment}/{TaskSyncConstants.ArchiveSegment}/{fileName}";
            return true;
        }

        return false;
    }

    public static string BuildTaskByFileUrl(
        int workspaceId,
        string canonicalTaskPath,
        string? gitRef = null,
        int? taskIdHint = null)
    {
        var queryParts = new List<string>(3)
        {
            $"{TaskFileResolverConstants.PathQueryParameter}={Uri.EscapeDataString(canonicalTaskPath)}"
        };

        if (!string.IsNullOrWhiteSpace(gitRef))
        {
            queryParts.Add($"{TaskFileResolverConstants.RefQueryParameter}={Uri.EscapeDataString(gitRef.Trim())}");
        }

        if (taskIdHint.HasValue && taskIdHint.Value > 0)
        {
            queryParts.Add($"{TaskFileResolverConstants.TaskIdQueryParameter}={taskIdHint.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        var subPath = $"{TaskFileResolverConstants.ByFileRouteSubPath}?{string.Join('&', queryParts)}";
        return WorkspaceUrlHelper.BuildPath(workspaceId, subPath);
    }

    public static string AppendFragment(string baseUrl, string? nonLineFragment)
    {
        if (string.IsNullOrWhiteSpace(nonLineFragment))
        {
            return baseUrl;
        }

        var trimmed = nonLineFragment.Trim();
        if (trimmed.StartsWith('#'))
        {
            trimmed = trimmed[1..];
        }

        if (string.IsNullOrEmpty(trimmed) || ContainsControlCharacters(trimmed))
        {
            return baseUrl;
        }

        var decoded = Uri.UnescapeDataString(trimmed);
        if (ContainsControlCharacters(decoded))
        {
            return baseUrl;
        }

        var encoded = Uri.EscapeDataString(decoded);
        return string.IsNullOrEmpty(encoded) ? baseUrl : $"{baseUrl}#{encoded}";
    }

    /// <summary>
    /// Classifies and rewrites (or neutralizes) a Markdown hyperlink URL using the supplied <paramref name="context"/>.
    /// </summary>
    public static MarkdownLinkResolution ResolveLink(string? rawUrl, MarkdownLinkContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return new MarkdownLinkResolution(MarkdownLinkResolutionKind.Unchanged, rawUrl);
        }

        var trimmed = rawUrl.Trim();
        var normalized = trimmed.Replace('\\', '/');

        // Rule 1: Pure fragment anchors
        if (normalized.StartsWith('#'))
        {
            return new MarkdownLinkResolution(MarkdownLinkResolutionKind.Unchanged, trimmed);
        }

        // Rule 1: Already-resolved workspace app routes (/w/{id}/...)
        if (!ContainsControlCharacters(trimmed) &&
            !trimmed.Contains('\\') &&
            trimmed.StartsWith($"/{WorkspaceUrlHelper.WorkspacePrefixSegment}/", StringComparison.OrdinalIgnoreCase) &&
            WorkspaceUrlHelper.TryExtractWorkspaceId(trimmed, out _, out var appRemaining, out _) &&
            !ContainsParentTraversalSegment(appRemaining))
        {
            return new MarkdownLinkResolution(MarkdownLinkResolutionKind.Unchanged, trimmed);
        }

        // Rule 1: Scheme inspection (entity-decoded + control/whitespace-stripped)
        if (TryInspectLeadingColon(trimmed, out var colonStatus))
        {
            if (colonStatus == ColonLeadClassification.SafeExternal)
            {
                return new MarkdownLinkResolution(MarkdownLinkResolutionKind.Unchanged, trimmed);
            }

            if (colonStatus == ColonLeadClassification.UnsafeOrUnknownScheme)
            {
                return new MarkdownLinkResolution(MarkdownLinkResolutionKind.Neutralized, string.Empty);
            }
        }

        // Rule 6: Workspace-less rendering neutralizes non-external workspace/repository targets
        if (!context.WorkspaceId.HasValue || context.WorkspaceId.Value <= 0)
        {
            return new MarkdownLinkResolution(MarkdownLinkResolutionKind.Neutralized, string.Empty);
        }

        var workspaceId = context.WorkspaceId.Value;

        // Rule 2: Extract fragment (#L42, #L42-L58, or #heading) and query, then :line / :start-end
        var hashIdx = normalized.IndexOf('#');
        var pathAndQuery = hashIdx >= 0 ? normalized[..hashIdx] : normalized;
        var rawFragment = hashIdx >= 0 ? normalized[(hashIdx + 1)..] : null;

        FileLineRange? lineRange = null;
        string? nonLineFragment = null;

        if (!string.IsNullOrEmpty(rawFragment))
        {
            if (FileLineRangeHelper.TryParseFragment("#" + rawFragment, out var parsedFragRange))
            {
                lineRange = parsedFragRange;
            }
            else
            {
                nonLineFragment = rawFragment;
            }
        }

        var queryIdx = pathAndQuery.IndexOf('?');
        var pathPart = queryIdx >= 0 ? pathAndQuery[..queryIdx] : pathAndQuery;

        if (TryStripTrailingColonLineRange(pathPart, out var strippedPath, out var colonRange))
        {
            pathPart = strippedPath;
            if (lineRange == null)
            {
                lineRange = colonRange;
                nonLineFragment = null;
            }
        }

        // Rule 3a: Percent-decode path segments cleanly and reject control characters
        var decodedPath = Uri.UnescapeDataString(pathPart).Replace('\\', '/').Trim();
        if (string.IsNullOrEmpty(decodedPath) || ContainsControlCharacters(decodedPath))
        {
            return new MarkdownLinkResolution(MarkdownLinkResolutionKind.Neutralized, string.Empty);
        }

        // Rule 3b & 4: Resolve to LocalMemory or Repository target
        if (!TryResolveTarget(decodedPath, workspaceId, context, out var target))
        {
            return new MarkdownLinkResolution(MarkdownLinkResolutionKind.Neutralized, string.Empty);
        }

        // Rule 5: Classify target into Memory, Task, or File Viewer URL
        if (target.Kind == ResolvedSubRootKind.LocalMemory)
        {
            var memUrl = MemoryPresentationHelper.BuildMemoryPageUrl(workspaceId, AgentMemoryScope.LocalWorkspace, target.Path);
            if (lineRange == null && !string.IsNullOrEmpty(nonLineFragment))
            {
                memUrl = AppendFragment(memUrl, nonLineFragment);
            }

            return new MarkdownLinkResolution(MarkdownLinkResolutionKind.Rewritten, memUrl);
        }

        var targetRef = !string.IsNullOrWhiteSpace(target.TargetRef)
            ? target.TargetRef
            : context.EffectiveActiveRef;

        // Tier 1 Shared Repository Memory (with deterministic exceptions routing to Files.razor)
        if (!string.IsNullOrEmpty(target.Path) &&
            AgentMemoryPathClassifier.TryGetCanonicalSharedRelativePath(target.Path, repositoryRoot: null, out var canonicalShared))
        {
            var routeToFileViewer =
                lineRange != null ||
                target.IsExplicitTaskRef ||
                (context.DocumentScope == MarkdownDocumentScope.RepositoryFile &&
                 !string.Equals(targetRef, context.EffectiveDefaultBaseBranch, StringComparison.Ordinal));

            if (!routeToFileViewer)
            {
                var sharedUrl = MemoryPresentationHelper.BuildMemoryPageUrl(workspaceId, AgentMemoryScope.SharedRepository, canonicalShared);
                if (!string.IsNullOrEmpty(nonLineFragment))
                {
                    sharedUrl = AppendFragment(sharedUrl, nonLineFragment);
                }

                return new MarkdownLinkResolution(MarkdownLinkResolutionKind.Rewritten, sharedUrl);
            }
        }

        // Task Markdown targets (docs/tasks/[archive/]<filename>.md)
        if (!string.IsNullOrEmpty(target.Path) &&
            TryNormalizeTaskMarkdownPath(target.Path, out var canonicalTaskPath, out _, out var taskFileName))
        {
            if (TryLookupTaskId(context.TaskPathLookup, canonicalTaskPath, taskFileName, out var matchedTaskId))
            {
                var directTaskUrl = WorkspaceUrlHelper.BuildPath(workspaceId, $"tasks/{matchedTaskId}");
                return new MarkdownLinkResolution(MarkdownLinkResolutionKind.Rewritten, directTaskUrl);
            }

            var byFileUrl = BuildTaskByFileUrl(workspaceId, canonicalTaskPath, targetRef, target.TargetTaskId);
            return new MarkdownLinkResolution(MarkdownLinkResolutionKind.Rewritten, byFileUrl);
        }

        // Arbitrary repository file or directory target -> Step 12.46A File Viewer
        var fileUrl = FileViewerPresentationHelper.BuildFileViewerUrl(
            workspaceId,
            targetRef,
            target.Path,
            target.TargetTaskId,
            lineRange);

        if (lineRange == null && !string.IsNullOrEmpty(nonLineFragment))
        {
            fileUrl = AppendFragment(fileUrl, nonLineFragment);
        }

        return new MarkdownLinkResolution(MarkdownLinkResolutionKind.Rewritten, fileUrl);
    }

    private enum ColonLeadClassification
    {
        NotAScheme = 0,
        SafeExternal = 1,
        UnsafeOrUnknownScheme = 2
    }

    private static bool TryInspectLeadingColon(string trimmedUrl, out ColonLeadClassification classification)
    {
        classification = ColonLeadClassification.NotAScheme;

        var decoded = WebUtility.HtmlDecode(trimmedUrl);
        var sb = new StringBuilder(decoded.Length);
        foreach (var ch in decoded)
        {
            if (ch > 0x20 && ch != 0x7F)
            {
                sb.Append(ch == '\\' ? '/' : ch);
            }
        }

        var candidate = sb.ToString();
        if (candidate.Length == 0)
        {
            return false;
        }

        var colonIdx = candidate.IndexOf(':');
        if (colonIdx < 0)
        {
            return false;
        }

        var slashIdx = candidate.IndexOf('/');
        var queryIdx = candidate.IndexOf('?');
        var fragmentIdx = candidate.IndexOf('#');
        if ((slashIdx >= 0 && slashIdx < colonIdx) ||
            (queryIdx >= 0 && queryIdx < colonIdx) ||
            (fragmentIdx >= 0 && fragmentIdx < colonIdx))
        {
            return false;
        }

        var schemePrefix = candidate[..colonIdx];
        var afterColon = candidate[(colonIdx + 1)..];

        if ((string.Equals(schemePrefix, HttpScheme, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(schemePrefix, HttpsScheme, StringComparison.OrdinalIgnoreCase)) &&
            afterColon.StartsWith("//", StringComparison.Ordinal))
        {
            classification = ColonLeadClassification.SafeExternal;
            return true;
        }

        if (string.Equals(schemePrefix, MailtoScheme, StringComparison.OrdinalIgnoreCase))
        {
            classification = ColonLeadClassification.SafeExternal;
            return true;
        }

        if (UnsafeSchemes.Contains(schemePrefix) ||
            string.Equals(schemePrefix, HttpScheme, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(schemePrefix, HttpsScheme, StringComparison.OrdinalIgnoreCase) ||
            afterColon.StartsWith("//", StringComparison.Ordinal))
        {
            classification = ColonLeadClassification.UnsafeOrUnknownScheme;
            return true;
        }

        // Windows drive prefix (e.g. C:/Users/...)
        if (schemePrefix.Length == 1 && char.IsAsciiLetter(schemePrefix[0]) && afterColon.StartsWith('/'))
        {
            return false;
        }

        // If raw prefix before ':' had control chars, whitespace, or entity encoding, treat as obfuscated scheme.
        var rawColonIdx = trimmedUrl.IndexOf(':');
        if (rawColonIdx >= 0)
        {
            var rawPrefix = trimmedUrl[..rawColonIdx];
            foreach (var ch in rawPrefix)
            {
                if (ch <= 0x20 || ch == 0x7F || ch == '&')
                {
                    classification = ColonLeadClassification.UnsafeOrUnknownScheme;
                    return true;
                }
            }
        }

        // Allow root-level file line links like README.md:42 or Program.cs:10-20 (before query/fragment)
        var afterBeforeHash = afterColon;
        var stopIdx = afterBeforeHash.IndexOfAny(['?', '#']);
        if (stopIdx >= 0)
        {
            afterBeforeHash = afterBeforeHash[..stopIdx];
        }

        if (LineRangeDigitsOnlyRegex().IsMatch(afterBeforeHash))
        {
            return false;
        }

        classification = ColonLeadClassification.UnsafeOrUnknownScheme;
        return true;
    }

    private static bool TryStripTrailingColonLineRange(string pathPart, out string strippedPath, out FileLineRange? lineRange)
    {
        strippedPath = pathPart;
        lineRange = null;

        var match = TrailingColonLineRangeRegex().Match(pathPart);
        if (!match.Success)
        {
            return false;
        }

        var prefix = match.Groups[1].Value;
        // Never strip a single-letter drive prefix like C:
        if (prefix.Length == 1 && char.IsAsciiLetter(prefix[0]))
        {
            return false;
        }

        if (!int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var startLine) ||
            startLine <= 0)
        {
            return false;
        }

        var endLine = startLine;
        if (match.Groups[3].Success)
        {
            if (!int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out endLine) ||
                endLine <= 0)
            {
                return false;
            }
        }

        if (endLine < startLine)
        {
            (startLine, endLine) = (endLine, startLine);
        }

        strippedPath = prefix;
        lineRange = new FileLineRange(startLine, endLine);
        return true;
    }

    private static bool TryResolveTarget(
        string decodedPath,
        int workspaceId,
        MarkdownLinkContext context,
        out ResolvedTarget target)
    {
        target = default!;

        // 1. Virtual /workspace/... or workspace/...
        if (TryExtractWorkspaceSubRoot(decodedPath, out var virtualSubPath))
        {
            return TryResolveSubRootTarget(virtualSubPath, context, out target);
        }

        // 2. Host / WSL workspace path (.../.nightcodeforge/wks/{id}/...)
        if (TryExtractHostWorkspaceSubRoot(decodedPath, out var embeddedWorkspaceId, out var hostSubPath))
        {
            if (embeddedWorkspaceId != workspaceId)
            {
                // Cross-workspace isolation
                return false;
            }

            return TryResolveSubRootTarget(hostSubPath, context, out target);
        }

        // 3. Reject any other rooted, home-relative, UNC, or Windows drive path
        if (IsRootedOrDrivePath(decodedPath))
        {
            return false;
        }

        // 4. Relative path resolution based on DocumentScope
        switch (context.DocumentScope)
        {
            case MarkdownDocumentScope.LocalMemory:
            {
                var candidate = StripLeadingDotSlash(decodedPath);
                if (!IsFlatMarkdownFileName(candidate))
                {
                    return false;
                }

                target = new ResolvedTarget(
                    ResolvedSubRootKind.LocalMemory,
                    candidate,
                    TargetRef: null,
                    TargetTaskId: null,
                    IsExplicitTaskRef: false);
                return true;
            }

            case MarkdownDocumentScope.SharedRepositoryMemory:
            case MarkdownDocumentScope.RepositoryFile:
            {
                var baseDir = GetDirectoryPart(context.CurrentDocumentPath);
                var combined = string.IsNullOrEmpty(baseDir) ? decodedPath : $"{baseDir}/{decodedPath}";
                if (!SandboxPathResolver.TryNormalizeRelativeSegments(combined, out var cleanRepoPath) ||
                    string.IsNullOrEmpty(cleanRepoPath))
                {
                    return false;
                }

                target = new ResolvedTarget(
                    ResolvedSubRootKind.Repository,
                    cleanRepoPath,
                    context.EffectiveActiveRef,
                    context.TaskId,
                    IsExplicitTaskRef: false);
                return true;
            }

            case MarkdownDocumentScope.GeneralWorkspace:
            case MarkdownDocumentScope.Task:
            default:
            {
                if (!SandboxPathResolver.TryNormalizeRelativeSegments(decodedPath, out var cleanRepoPath) ||
                    string.IsNullOrEmpty(cleanRepoPath))
                {
                    return false;
                }

                target = new ResolvedTarget(
                    ResolvedSubRootKind.Repository,
                    cleanRepoPath,
                    context.EffectiveActiveRef,
                    context.TaskId,
                    IsExplicitTaskRef: false);
                return true;
            }
        }
    }

    private static bool TryResolveSubRootTarget(
        string afterWksOrWorkspace,
        MarkdownLinkContext context,
        out ResolvedTarget target)
    {
        target = default!;

        var trimmed = afterWksOrWorkspace.Trim('/');
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        var parts = trimmed.Split('/', 2);
        var subRoot = parts[0];
        var remainder = parts.Length > 1 ? parts[1].TrimStart('/') : string.Empty;

        // Tier 2 Local Memory: /workspace/memory/<file>.md (reject traversal out of memory/)
        if (string.Equals(subRoot, AgentMemoryConstants.LocalMemoryDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            var candidate = StripLeadingDotSlash(remainder);
            if (!IsFlatMarkdownFileName(candidate))
            {
                return false;
            }

            target = new ResolvedTarget(
                ResolvedSubRootKind.LocalMemory,
                candidate,
                TargetRef: null,
                TargetTaskId: null,
                IsExplicitTaskRef: false);
            return true;
        }

        // Main clone: /workspace/clone[/<subpath>]
        if (string.Equals(subRoot, CloneSegment, StringComparison.OrdinalIgnoreCase))
        {
            if (!SandboxPathResolver.TryNormalizeRelativeSegments(remainder, out var cleanClonePath))
            {
                return false;
            }

            target = new ResolvedTarget(
                ResolvedSubRootKind.Repository,
                cleanClonePath,
                context.EffectiveDefaultBaseBranch,
                TargetTaskId: null,
                IsExplicitTaskRef: false);
            return true;
        }

        // Task worktree: /workspace/task-{nnn}[/<subpath>]
        var taskMatch = TaskWorktreeSegmentRegex().Match(subRoot);
        if (taskMatch.Success &&
            int.TryParse(taskMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTaskId) &&
            parsedTaskId > 0)
        {
            if (!SandboxPathResolver.TryNormalizeRelativeSegments(remainder, out var cleanTaskPath))
            {
                return false;
            }

            target = new ResolvedTarget(
                ResolvedSubRootKind.Repository,
                cleanTaskPath,
                $"{GitRepositoryBrowserConstants.TaskBranchPrefix}{parsedTaskId.ToString(CultureInfo.InvariantCulture)}",
                parsedTaskId,
                IsExplicitTaskRef: true);
            return true;
        }

        return false;
    }

    private static bool TryExtractWorkspaceSubRoot(string decodedPath, out string remainder)
    {
        const string slashPrefix = "/" + WorkspaceSegment + "/";
        const string barePrefix = WorkspaceSegment + "/";

        if (decodedPath.StartsWith(slashPrefix, StringComparison.OrdinalIgnoreCase))
        {
            remainder = decodedPath[slashPrefix.Length..];
            return true;
        }

        if (decodedPath.StartsWith(barePrefix, StringComparison.OrdinalIgnoreCase))
        {
            remainder = decodedPath[barePrefix.Length..];
            return true;
        }

        remainder = string.Empty;
        return false;
    }

    private static bool TryExtractHostWorkspaceSubRoot(
        string decodedPath,
        out int embeddedWorkspaceId,
        out string remainder)
    {
        embeddedWorkspaceId = 0;
        remainder = string.Empty;

        var idx = decodedPath.IndexOf(HostWorkspaceMarker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        if (idx > 0 && decodedPath[idx - 1] != '/')
        {
            return false;
        }

        var afterMarker = decodedPath[(idx + HostWorkspaceMarker.Length)..];
        var slashIdx = afterMarker.IndexOf('/');
        if (slashIdx <= 0)
        {
            return false;
        }

        if (!int.TryParse(afterMarker[..slashIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out embeddedWorkspaceId) ||
            embeddedWorkspaceId <= 0)
        {
            embeddedWorkspaceId = 0;
            return true; // Marker was matched with an invalid ID -> caller will reject.
        }

        remainder = afterMarker[(slashIdx + 1)..];
        return true;
    }

    private static bool TryLookupTaskId(
        IReadOnlyDictionary<string, int>? lookup,
        string canonicalTaskPath,
        string fileName,
        out int taskId)
    {
        taskId = 0;
        if (lookup == null || lookup.Count == 0)
        {
            return false;
        }

        var normalizedKey = TaskMarkdownSyncService.NormalizeDependencyKey(fileName);
        if (TryGetPositive(lookup, canonicalTaskPath, out taskId) ||
            TryGetPositive(lookup, fileName, out taskId) ||
            (!string.IsNullOrEmpty(normalizedKey) && TryGetPositive(lookup, normalizedKey, out taskId)))
        {
            return true;
        }

        foreach (var kvp in lookup)
        {
            if (kvp.Value <= 0)
            {
                continue;
            }

            if (string.Equals(kvp.Key, canonicalTaskPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, fileName, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(normalizedKey) &&
                 string.Equals(kvp.Key, normalizedKey, StringComparison.OrdinalIgnoreCase)))
            {
                taskId = kvp.Value;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetPositive(IReadOnlyDictionary<string, int> lookup, string key, out int value)
    {
        if (lookup.TryGetValue(key, out value) && value > 0)
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static string GetDirectoryPart(string? currentDocumentPath)
    {
        if (string.IsNullOrWhiteSpace(currentDocumentPath))
        {
            return string.Empty;
        }

        var norm = currentDocumentPath.Trim().Replace('\\', '/').Trim('/');
        var lastSlash = norm.LastIndexOf('/');
        return lastSlash > 0 ? norm[..lastSlash] : string.Empty;
    }

    private static string StripLeadingDotSlash(string value)
    {
        var current = value;
        while (current.StartsWith("./", StringComparison.Ordinal))
        {
            current = current[2..];
        }

        return current;
    }

    private static bool IsFlatMarkdownFileName(string name) =>
        name.Length > AgentMemoryConstants.MarkdownExtension.Length &&
        !name.Contains('/') &&
        !name.StartsWith('.') &&
        !name.Contains("..", StringComparison.Ordinal) &&
        name.IndexOfAny(InvalidFileNameChars) < 0 &&
        name.EndsWith(AgentMemoryConstants.MarkdownExtension, StringComparison.OrdinalIgnoreCase);

    private static bool IsRootedOrDrivePath(string slashNormalized) =>
        slashNormalized.StartsWith('/') ||
        slashNormalized.StartsWith('~') ||
        (slashNormalized.Length >= 2 && char.IsAsciiLetter(slashNormalized[0]) && slashNormalized[1] == ':');

    private static bool ContainsControlCharacters(string value)
    {
        foreach (var ch in value)
        {
            if (ch < 0x20 || ch == 0x7F)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsParentTraversalSegment(string path)
    {
        var pathOnly = path;
        var qIdx = pathOnly.IndexOfAny(['?', '#']);
        if (qIdx >= 0)
        {
            pathOnly = pathOnly[..qIdx];
        }

        var decoded = Uri.UnescapeDataString(pathOnly);
        if (ContainsControlCharacters(decoded))
        {
            return true;
        }

        return decoded
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(s => s == "..");
    }
}
