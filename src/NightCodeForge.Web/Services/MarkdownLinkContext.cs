using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Identifies the origin surface of a Markdown document for context-aware link resolution.
/// </summary>
public enum MarkdownDocumentScope
{
    GeneralWorkspace = 0,
    Task = 1,
    LocalMemory = 2,
    SharedRepositoryMemory = 3,
    RepositoryFile = 4
}

/// <summary>
/// Named route and query parameter constants for the task-by-file resolver route.
/// </summary>
public static class TaskFileResolverConstants
{
    public const string ByFileRouteSubPath = "tasks/by-file";
    public const string PathQueryParameter = "path";
    public const string RefQueryParameter = "ref";
    public const string TaskIdQueryParameter = "taskId";
}

/// <summary>
/// Immutable rendering context passed synchronously into <see cref="MarkdownHelper"/> to resolve
/// workspace, memory, task, and repository file links deterministically.
/// </summary>
public sealed record MarkdownLinkContext
{
    public static MarkdownLinkContext Empty { get; } = new();

    public int? WorkspaceId { get; init; }

    public MarkdownDocumentScope DocumentScope { get; init; } = MarkdownDocumentScope.GeneralWorkspace;

    public string? ActiveBranchOrRef { get; init; }

    public int? TaskId { get; init; }

    public string? CurrentDocumentPath { get; init; }

    public IReadOnlyDictionary<string, int>? TaskPathLookup { get; init; }

    public string? DefaultBaseBranch { get; init; }

    public string EffectiveDefaultBaseBranch
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DefaultBaseBranch))
            {
                return DefaultBaseBranch.Trim();
            }

            if (DocumentScope is MarkdownDocumentScope.GeneralWorkspace or MarkdownDocumentScope.LocalMemory or MarkdownDocumentScope.SharedRepositoryMemory &&
                !string.IsNullOrWhiteSpace(ActiveBranchOrRef))
            {
                return ActiveBranchOrRef.Trim();
            }

            return GitRepositoryBrowserConstants.DefaultBranchName;
        }
    }

    public string EffectiveActiveRef =>
        !string.IsNullOrWhiteSpace(ActiveBranchOrRef)
            ? ActiveBranchOrRef.Trim()
            : EffectiveDefaultBaseBranch;

    public static MarkdownLinkContext ForGeneralWorkspace(
        Workspace? workspace,
        IReadOnlyDictionary<string, int>? taskPathLookup = null) =>
        workspace == null
            ? Empty
            : ForGeneralWorkspace(workspace.Id, workspace.DefaultBaseBranch, taskPathLookup);

    public static MarkdownLinkContext ForGeneralWorkspace(
        int? workspaceId,
        string? defaultBaseBranch = null,
        IReadOnlyDictionary<string, int>? taskPathLookup = null)
    {
        if (!workspaceId.HasValue || workspaceId.Value <= 0)
        {
            return Empty;
        }

        var effectiveBase = FirstNonEmpty(defaultBaseBranch, GitRepositoryBrowserConstants.DefaultBranchName);
        return new MarkdownLinkContext
        {
            WorkspaceId = workspaceId.Value,
            DocumentScope = MarkdownDocumentScope.GeneralWorkspace,
            ActiveBranchOrRef = effectiveBase,
            DefaultBaseBranch = effectiveBase,
            TaskPathLookup = taskPathLookup
        };
    }

    public static MarkdownLinkContext ForTask(
        ForgeTask? task,
        Workspace? activeWorkspace = null,
        IReadOnlyDictionary<string, int>? taskPathLookup = null)
    {
        if (task == null)
        {
            return ForGeneralWorkspace(activeWorkspace, taskPathLookup);
        }

        var workspaceId = task.WorkspaceId > 0 ? task.WorkspaceId : activeWorkspace?.Id;
        if (!workspaceId.HasValue || workspaceId.Value <= 0)
        {
            return Empty;
        }

        var defaultBase = FirstNonEmpty(
            task.Workspace?.DefaultBaseBranch,
            activeWorkspace?.DefaultBaseBranch,
            task.BaseBranch,
            GitRepositoryBrowserConstants.DefaultBranchName);
        var activeRef = FirstNonEmpty(task.FeatureBranch, task.BaseBranch, defaultBase);

        return new MarkdownLinkContext
        {
            WorkspaceId = workspaceId.Value,
            DocumentScope = MarkdownDocumentScope.Task,
            ActiveBranchOrRef = activeRef,
            DefaultBaseBranch = defaultBase,
            TaskId = task.Id > 0 ? task.Id : null,
            TaskPathLookup = taskPathLookup
        };
    }

    public static MarkdownLinkContext ForMemory(
        Workspace? workspace,
        AgentMemoryScope? scope,
        string? currentDocumentPath)
    {
        if (workspace == null || workspace.Id <= 0)
        {
            return Empty;
        }

        var defaultBase = FirstNonEmpty(workspace.DefaultBaseBranch, GitRepositoryBrowserConstants.DefaultBranchName);
        var docScope = scope switch
        {
            AgentMemoryScope.LocalWorkspace => MarkdownDocumentScope.LocalMemory,
            AgentMemoryScope.SharedRepository => MarkdownDocumentScope.SharedRepositoryMemory,
            _ => MarkdownDocumentScope.GeneralWorkspace
        };

        return new MarkdownLinkContext
        {
            WorkspaceId = workspace.Id,
            DocumentScope = docScope,
            ActiveBranchOrRef = defaultBase,
            DefaultBaseBranch = defaultBase,
            CurrentDocumentPath = string.IsNullOrWhiteSpace(currentDocumentPath) ? null : currentDocumentPath.Trim()
        };
    }

    public static MarkdownLinkContext ForRepositoryFile(
        Workspace? workspace,
        string? activeBranchOrRef,
        string? currentDocumentPath,
        int? taskId = null)
    {
        if (workspace == null || workspace.Id <= 0)
        {
            return Empty;
        }

        var defaultBase = FirstNonEmpty(workspace.DefaultBaseBranch, GitRepositoryBrowserConstants.DefaultBranchName);
        var effectiveRef = FirstNonEmpty(activeBranchOrRef, defaultBase);

        return new MarkdownLinkContext
        {
            WorkspaceId = workspace.Id,
            DocumentScope = MarkdownDocumentScope.RepositoryFile,
            ActiveBranchOrRef = effectiveRef,
            DefaultBaseBranch = defaultBase,
            CurrentDocumentPath = string.IsNullOrWhiteSpace(currentDocumentPath) ? null : currentDocumentPath.Trim(),
            TaskId = taskId is > 0 ? taskId.Value : null
        };
    }

    public static MarkdownLinkContext ForTranscript(
        int? workspaceId,
        string? defaultBaseBranch,
        int transcriptTaskId)
    {
        if (!workspaceId.HasValue || workspaceId.Value <= 0)
        {
            return Empty;
        }

        var effectiveBase = FirstNonEmpty(defaultBaseBranch, GitRepositoryBrowserConstants.DefaultBranchName);
        if (transcriptTaskId > 0)
        {
            return new MarkdownLinkContext
            {
                WorkspaceId = workspaceId.Value,
                DocumentScope = MarkdownDocumentScope.Task,
                ActiveBranchOrRef = $"{GitRepositoryBrowserConstants.TaskBranchPrefix}{transcriptTaskId}",
                DefaultBaseBranch = effectiveBase,
                TaskId = transcriptTaskId
            };
        }

        return new MarkdownLinkContext
        {
            WorkspaceId = workspaceId.Value,
            DocumentScope = MarkdownDocumentScope.GeneralWorkspace,
            ActiveBranchOrRef = effectiveBase,
            DefaultBaseBranch = effectiveBase
        };
    }

    /// <summary>
    /// Builds an optional synchronous lookup mapping unambiguous task slugs to task IDs.
    /// Ambiguous slugs shared by multiple tasks are omitted so callers fall through to <c>/tasks/by-file</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, int>? BuildTaskPathLookup(IEnumerable<ForgeTask>? tasks)
    {
        if (tasks == null)
        {
            return null;
        }

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks)
        {
            if (task == null || task.Id <= 0 || string.IsNullOrWhiteSpace(task.Title))
            {
                continue;
            }

            var slug = TaskMarkdownSyncService.GenerateSlug(task.Title);
            if (string.IsNullOrEmpty(slug) || ambiguous.Contains(slug))
            {
                continue;
            }

            if (map.TryGetValue(slug, out var existingId) && existingId != task.Id)
            {
                map.Remove(slug);
                ambiguous.Add(slug);
                continue;
            }

            map[slug] = task.Id;
        }

        return map.Count > 0 ? map : null;
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return GitRepositoryBrowserConstants.DefaultBranchName;
    }
}
