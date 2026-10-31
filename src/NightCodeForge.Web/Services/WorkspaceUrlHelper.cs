using System;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Stateless URL helper for parsing, building, and transitioning workspace-scoped routes (/w/{workspaceId}/...).
/// </summary>
public static class WorkspaceUrlHelper
{
    public const string WorkspacePrefixSegment = "w";

    private static readonly HashSet<string> NonWorkspaceRootSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Error",
        "not-found",
        "hangfire",
        "_blazor",
        "_framework",
        "hubs",
        "css",
        "js",
        "lib"
    };

    private static readonly HashSet<string> TaskRouteSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "tasks",
        "inspector",
        "review",
        "monitoring"
    };

    /// <summary>
    /// Extracts the workspace ID from a "/w/{workspaceId}/..." URI or relative path if present.
    /// </summary>
    public static bool TryExtractWorkspaceId(
        string? uriOrPath,
        out int workspaceId,
        out string remainingPath,
        out string queryAndFragment)
    {
        workspaceId = 0;
        remainingPath = string.Empty;
        queryAndFragment = string.Empty;

        if (string.IsNullOrWhiteSpace(uriOrPath))
        {
            return false;
        }

        var pathPart = ExtractPathAndQuery(uriOrPath.Trim(), out queryAndFragment);
        var trimmedPath = pathPart.Trim('/');
        if (string.IsNullOrEmpty(trimmedPath))
        {
            return false;
        }

        var segments = trimmedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 &&
            string.Equals(segments[0], WorkspacePrefixSegment, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(segments[1], out var parsedId) &&
            parsedId > 0)
        {
            workspaceId = parsedId;
            remainingPath = segments.Length > 2
                ? string.Join('/', segments[2..])
                : string.Empty;
            return true;
        }

        remainingPath = string.Join('/', segments);
        return false;
    }

    /// <summary>
    /// Determines whether the route (after any /w/{id} prefix) targets a specific task ID
    /// (e.g. "tasks/15", "inspector/15", "review/15", "monitoring/15").
    /// </summary>
    public static bool TryExtractTaskRoute(string? uriOrPath, out string section, out int taskId)
    {
        section = string.Empty;
        taskId = 0;

        TryExtractWorkspaceId(uriOrPath, out _, out var remainingPath, out _);
        if (string.IsNullOrEmpty(remainingPath))
        {
            return false;
        }

        var segments = remainingPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 2 &&
            TaskRouteSections.Contains(segments[0]) &&
            int.TryParse(segments[1], out var parsedTaskId) &&
            parsedTaskId > 0)
        {
            section = segments[0].ToLowerInvariant();
            taskId = parsedTaskId;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the given relative path is a system/non-workspace endpoint (e.g. /Error, /hangfire).
    /// </summary>
    public static bool IsNonWorkspaceRoute(string? remainingPath)
    {
        if (string.IsNullOrWhiteSpace(remainingPath))
        {
            return false;
        }

        var firstSegment = remainingPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return !string.IsNullOrEmpty(firstSegment) && NonWorkspaceRootSegments.Contains(firstSegment);
    }

    /// <summary>
    /// Builds a root-relative path prefixed with "/w/{workspaceId}" when workspaceId is positive.
    /// Existing "/w/{oldId}" prefixes on <paramref name="subPath"/> are replaced.
    /// </summary>
    public static string BuildPath(int? workspaceId, string? subPath = null)
    {
        TryExtractWorkspaceId(subPath, out _, out var cleanPath, out var queryAndFragment);

        if (workspaceId.HasValue && workspaceId.Value > 0 && !IsNonWorkspaceRoute(cleanPath))
        {
            return string.IsNullOrEmpty(cleanPath)
                ? $"/{WorkspacePrefixSegment}/{workspaceId.Value}{queryAndFragment}"
                : $"/{WorkspacePrefixSegment}/{workspaceId.Value}/{cleanPath}{queryAndFragment}";
        }

        return string.IsNullOrEmpty(cleanPath)
            ? $"/{queryAndFragment}"
            : $"/{cleanPath}{queryAndFragment}";
    }

    /// <summary>
    /// Builds a relative href suitable for Blazor NavLink components (without leading slash).
    /// </summary>
    public static string BuildNavHref(int? workspaceId, string? subPath = null)
    {
        TryExtractWorkspaceId(subPath, out _, out var cleanPath, out var queryAndFragment);

        if (workspaceId.HasValue && workspaceId.Value > 0 && !IsNonWorkspaceRoute(cleanPath))
        {
            return string.IsNullOrEmpty(cleanPath)
                ? $"{WorkspacePrefixSegment}/{workspaceId.Value}{queryAndFragment}"
                : $"{WorkspacePrefixSegment}/{workspaceId.Value}/{cleanPath}{queryAndFragment}";
        }

        return $"{cleanPath}{queryAndFragment}";
    }

    /// <summary>
    /// Computes the destination URL when switching the active workspace in the top navigation bar.
    /// Task-bound routes (/tasks/{id}, /review/{id}, /inspector/{id}) transition cleanly to the new workspace's view.
    /// </summary>
    public static string GetTargetUrlForWorkspaceSwitch(string? currentUri, int newWorkspaceId)
    {
        TryExtractWorkspaceId(currentUri, out _, out var remainingPath, out var queryAndFragment);

        if (TryExtractTaskRoute(remainingPath, out var section, out _))
        {
            return section switch
            {
                "tasks" => BuildPath(newWorkspaceId, "backlog"),
                "review" => BuildPath(newWorkspaceId, "review"),
                "inspector" => BuildPath(newWorkspaceId, "inspector"),
                "monitoring" => BuildPath(newWorkspaceId, "inspector"),
                _ => BuildPath(newWorkspaceId, section)
            };
        }

        if (remainingPath.StartsWith("transcripts/", StringComparison.OrdinalIgnoreCase))
        {
            return BuildPath(newWorkspaceId, "transcripts");
        }

        if (IsNonWorkspaceRoute(remainingPath))
        {
            return BuildPath(newWorkspaceId, string.Empty);
        }

        return BuildPath(newWorkspaceId, $"{remainingPath}{queryAndFragment}");
    }

    /// <summary>
    /// Resolves the human-readable breadcrumb section title for the current URI.
    /// </summary>
    public static string GetSectionDisplayName(string? uriOrPath)
    {
        TryExtractWorkspaceId(uriOrPath, out _, out var remainingPath, out _);
        var primarySegment = remainingPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

        return primarySegment.ToLowerInvariant() switch
        {
            "" => "Overview",
            "ideation" => "Ideation Chat",
            "backlog" => "Backlog & DAG",
            "monitoring" => "Live Telemetry",
            "inspector" => "Task Inspector",
            "inbox" => "Question Inbox",
            "review" => "Review Room",
            "git-sync" => "Git & Host Sync",
            "dev-queue" => "Dev-Queue & Locks",
            "settings" => "Settings",
            "transcripts" => "Transcripts & AI",
            "triage" => "Untrusted Intake",
            "tasks" => "Tasks",
            _ => char.ToUpperInvariant(primarySegment[0]) + primarySegment[1..]
        };
    }

    private static string ExtractPathAndQuery(string input, out string queryAndFragment)
    {
        queryAndFragment = string.Empty;
        var working = input;

        if (Uri.TryCreate(working, UriKind.Absolute, out var absoluteUri))
        {
            working = absoluteUri.PathAndQuery + absoluteUri.Fragment;
        }

        var queryIdx = working.IndexOfAny(['?', '#']);
        if (queryIdx >= 0)
        {
            queryAndFragment = working[queryIdx..];
            return working[..queryIdx];
        }

        return working;
    }
}

