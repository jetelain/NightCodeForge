using NightCodeForge.Core.Memory;

namespace NightCodeForge.Web.Services;

public enum MemoryIdeationAction
{
    SummarizePinnedMemory = 1,
    DeduplicateLocalMemory = 2,
    PromoteToRepositoryMemory = 3,
    DiscussSelectedFile = 4
}

public sealed record IdeationMemoryActionRequest(string? Action, string? Scope, string? Path);

public sealed record MemoryQuickActionState(
    bool CanSummarizePinned,
    bool EmphasizeSummarizePinned,
    bool CanDeduplicateLocal,
    bool CanPromoteToRepo,
    bool CanDiscussSelected);

/// <summary>
/// Builds Quick Ideation Action URLs, enablement state, and curated prompts for memory maintenance in Ideation.
/// </summary>
public static class IdeationMemoryPromptHelper
{
    public const string MemoryActionQueryParameter = "memoryAction";
    public const string MemoryScopeQueryParameter = MemoryPresentationHelper.ScopeQueryParameter;
    public const string MemoryPathQueryParameter = MemoryPresentationHelper.PathQueryParameter;

    public const string SummarizePinnedActionValue = "summarize-pinned";
    public const string DeduplicateLocalActionValue = "deduplicate-local";
    public const string PromoteToRepoActionValue = "promote-repo";
    public const string DiscussSelectedActionValue = "discuss-file";

    private const string IdeationRoute = "ideation";

    public static string FormatActionQueryValue(MemoryIdeationAction action) => action switch
    {
        MemoryIdeationAction.SummarizePinnedMemory => SummarizePinnedActionValue,
        MemoryIdeationAction.DeduplicateLocalMemory => DeduplicateLocalActionValue,
        MemoryIdeationAction.PromoteToRepositoryMemory => PromoteToRepoActionValue,
        MemoryIdeationAction.DiscussSelectedFile => DiscussSelectedActionValue,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported memory ideation action.")
    };

    public static bool TryParseActionQueryValue(string? value, out MemoryIdeationAction action)
    {
        action = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case SummarizePinnedActionValue:
                action = MemoryIdeationAction.SummarizePinnedMemory;
                return true;
            case DeduplicateLocalActionValue:
                action = MemoryIdeationAction.DeduplicateLocalMemory;
                return true;
            case PromoteToRepoActionValue:
                action = MemoryIdeationAction.PromoteToRepositoryMemory;
                return true;
            case DiscussSelectedActionValue:
                action = MemoryIdeationAction.DiscussSelectedFile;
                return true;
            default:
                return false;
        }
    }

    public static string BuildIdeationMemoryActionUrl(
        int workspaceId,
        MemoryIdeationAction action,
        AgentMemoryScope? scope = null,
        string? relativePath = null)
    {
        var queryParts = new List<string>(3)
        {
            $"{MemoryActionQueryParameter}={Uri.EscapeDataString(FormatActionQueryValue(action))}"
        };

        if (scope.HasValue && scope.Value != AgentMemoryScope.None)
        {
            var scopeValue = MemoryPresentationHelper.FormatScopeQueryValue(scope.Value);
            if (!string.IsNullOrEmpty(scopeValue))
            {
                queryParts.Add($"{MemoryScopeQueryParameter}={Uri.EscapeDataString(scopeValue)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            queryParts.Add($"{MemoryPathQueryParameter}={Uri.EscapeDataString(relativePath.Trim())}");
        }

        return WorkspaceUrlHelper.BuildPath(workspaceId, $"{IdeationRoute}?{string.Join('&', queryParts)}");
    }

    public static string BuildDeduplicationKey(int workspaceId, string? actionParam, string? scopeParam, string? pathParam) =>
        $"{workspaceId}|{actionParam?.Trim().ToLowerInvariant() ?? string.Empty}|{scopeParam?.Trim().ToLowerInvariant() ?? string.Empty}|{pathParam?.Trim() ?? string.Empty}";

    public static string BuildConversationTitle(MemoryIdeationAction action, string? relativePath = null) => action switch
    {
        MemoryIdeationAction.SummarizePinnedMemory => $"Memory Cleanup: Compact {AgentMemoryConstants.PinnedMemoryFileName}",
        MemoryIdeationAction.DeduplicateLocalMemory => "Memory Cleanup: Deduplicate & Prune Local Memory",
        MemoryIdeationAction.PromoteToRepositoryMemory => "Memory Audit: Promote Rules to Repository Memory",
        MemoryIdeationAction.DiscussSelectedFile when !string.IsNullOrWhiteSpace(relativePath) => $"Memory Review: {relativePath.Trim()}",
        MemoryIdeationAction.DiscussSelectedFile => "Memory Review",
        _ => "Memory Maintenance"
    };

    public static MemoryQuickActionState ComputeQuickActionState(
        IReadOnlyList<MemoryFileEntry> localFiles,
        MemoryBudget? pinnedBudget,
        AgentMemoryScope? selectedScope,
        string? selectedPath,
        bool isSelectedFileUnsaved = false)
    {
        ArgumentNullException.ThrowIfNull(localFiles);

        var hasPinned = localFiles.Any(f => MemoryPresentationHelper.IsPinnedMemoryFile(f.RelativePath));
        var emphasizePinned = hasPinned && pinnedBudget is { } budget && (budget.IsNearBudget || budget.IsOverBudget);
        var hasLocal = localFiles.Count > 0;
        var hasSelection = !isSelectedFileUnsaved &&
                           selectedScope is AgentMemoryScope.LocalWorkspace or AgentMemoryScope.SharedRepository &&
                           !string.IsNullOrWhiteSpace(selectedPath);

        return new MemoryQuickActionState(
            CanSummarizePinned: hasPinned,
            EmphasizeSummarizePinned: emphasizePinned,
            CanDeduplicateLocal: hasLocal,
            CanPromoteToRepo: hasLocal,
            CanDiscussSelected: hasSelection);
    }

    public static string BuildPrompt(
        MemoryIdeationAction action,
        AgentMemoryScope? scope = null,
        string? relativePath = null)
    {
        var pinnedVirtualPath = $"{AgentMemoryConstants.VirtualLocalMemoryPath}/{AgentMemoryConstants.PinnedMemoryFileName}";
        var twoTierBoundary = $"""
            Memory Tier Rules (strictly enforce):
            - Update local workspace memory (`{AgentMemoryConstants.VirtualLocalMemoryPath}/*.md`) **directly** using `write` / `edit`.
            - **Never** attempt to edit `{AgentMemoryConstants.RootRulesFileName}`, `{AgentMemoryConstants.AgentsRulesFilePath}`, or `{AgentMemoryConstants.SharedMemoryDirectory}/*` directly — Ideation runs in `RestrictedReadOnly` and cannot modify repository files. Instead, **propose a Stage 2 task** using `propose_tasks` whenever committed repository memory needs to be updated.
            """;

        return action switch
        {
            MemoryIdeationAction.SummarizePinnedMemory => $"""
                Please review and compact `{pinnedVirtualPath}` so it stays concise and comfortably below its pinned injection budget (`{AgentMemoryConstants.MaxPinnedMemoryLines}` lines / `{AgentMemoryConstants.MaxPinnedMemoryBytes}` bytes):
                1. Read `{pinnedVirtualPath}` and inspect existing `{AgentMemoryConstants.VirtualLocalMemoryPath}/*.md` topic files.
                2. Condense verbose entries, move detailed topic-specific notes into focused `{AgentMemoryConstants.VirtualLocalMemoryPath}/<topic>.md` files, and keep `{pinnedVirtualPath}` as a crisp high-signal index.
                3. Verify that `{pinnedVirtualPath}` is well under `{AgentMemoryConstants.MaxPinnedMemoryLines}` lines and `{AgentMemoryConstants.MaxPinnedMemoryBytes}` bytes after editing.

                {twoTierBoundary}
                """,

            MemoryIdeationAction.DeduplicateLocalMemory => $"""
                Please audit all local workspace memory files in `{AgentMemoryConstants.VirtualLocalMemoryPath}/*.md` against the current codebase and recent architectural decisions:
                1. List and read all files under `{AgentMemoryConstants.VirtualLocalMemoryPath}/*.md`.
                2. Merge overlapping or duplicate notes, verify claims against `/workspace/clone`, remove obsolete or contradicted facts, and keep `{pinnedVirtualPath}` updated.
                3. Apply the cleanup directly to `{AgentMemoryConstants.VirtualLocalMemoryPath}/*.md`.

                {twoTierBoundary}
                """,

            MemoryIdeationAction.PromoteToRepositoryMemory => $"""
                Please audit `{AgentMemoryConstants.VirtualLocalMemoryPath}/*.md` against committed repository memory (`{AgentMemoryConstants.RootRulesFileName}`, `{AgentMemoryConstants.AgentsRulesFilePath}`, and `{AgentMemoryConstants.SharedMemoryDirectory}/*.md`):
                1. Compare local workspace notes with the committed repository memory files in `/workspace/clone`.
                2. Identify durable team-wide conventions, build/test commands, or architectural invariants that belong in Git rather than local workspace memory.
                3. Propose a Stage 2 backlog task via `propose_tasks` detailing the exact updates to `{AgentMemoryConstants.RootRulesFileName}` or `{AgentMemoryConstants.SharedMemoryDirectory}/<topic>.md`, and prune or simplify any local duplicates from `{AgentMemoryConstants.VirtualLocalMemoryPath}/*.md` once captured in the task proposal.

                {twoTierBoundary}
                """,

            MemoryIdeationAction.DiscussSelectedFile => BuildDiscussSelectedPrompt(scope, relativePath, twoTierBoundary),

            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported memory ideation action.")
        };
    }

    private static string BuildDiscussSelectedPrompt(
        AgentMemoryScope? scope,
        string? relativePath,
        string twoTierBoundary)
    {
        var trimmedPath = relativePath?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedPath))
        {
            throw new ArgumentException("A selected memory file path is required.", nameof(relativePath));
        }

        var resolvedScope = scope is AgentMemoryScope.LocalWorkspace or AgentMemoryScope.SharedRepository
            ? scope.Value
            : AgentMemoryPathClassifier.Classify(trimmedPath) == AgentMemoryScope.SharedRepository
                ? AgentMemoryScope.SharedRepository
                : AgentMemoryScope.LocalWorkspace;

        var targetReference = resolvedScope == AgentMemoryScope.LocalWorkspace
            ? $"{AgentMemoryConstants.VirtualLocalMemoryPath}/{MemoryPresentationHelper.ToMemoryRelativePath(AgentMemoryScope.LocalWorkspace, trimmedPath)}"
            : MemoryPresentationHelper.ToMemoryRelativePath(AgentMemoryScope.SharedRepository, trimmedPath);

        var scopeGuidance = resolvedScope == AgentMemoryScope.LocalWorkspace
            ? $"This is a Tier 2 local workspace memory file (`{targetReference}`). Read it, verify its accuracy against the codebase, and update it directly with `write`/`edit` (or propose a Stage 2 task with `propose_tasks` if any rule should be promoted to repository memory)."
            : $"This is a Tier 1 committed repository memory file (`{targetReference}`). Read it, verify it against the codebase, update `{AgentMemoryConstants.VirtualLocalMemoryPath}/*.md` directly if local notes need adjustment, or propose a Stage 2 task via `propose_tasks` if `{targetReference}` itself needs updating.";

        return $"""
            Please review the memory file `{targetReference}` with me:
            {scopeGuidance}

            {twoTierBoundary}
            """;
    }
}
