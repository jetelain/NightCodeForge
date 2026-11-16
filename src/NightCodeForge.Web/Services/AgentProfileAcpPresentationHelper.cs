using NightCodeForge.Agents.Acp;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Presentation and persistence mapping helpers for ACP harnesses and Antigravity status inside <c>AgentProfilesTab</c>.
/// Keeps Razor components focused purely on layout and user interaction.
/// </summary>
public static class AgentProfileAcpPresentationHelper
{
    public const string CrucibleHarnessDisplayLabel = "Crucible (Local / Open-Weight & API)";
    public const string AcpHarnessDisplayLabel = "Antigravity ACP (Google AI / Gemini CLI)";
    public const string CustomModelSentinel = "__custom__";
    public const string TriagePinnedReason = "Untrusted Stage 0 intake triage must run inside the sandboxed Crucible harness (--network none).";
    public const string IdeationPinnedReason = "Interactive codebase ideation uses the Crucible agent factory and host backlog mutation tools.";

    public sealed record AntigravityStatusBadge(string DotClass, string BadgeClass, string Text);

    public sealed record AcpThinkingEffortOption(ThinkingEffort Effort, string DisplayLabel);

    public static string ExtractBaseStageId(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return string.Empty;
        }

        var colonIndex = profileId.LastIndexOf(':');
        return colonIndex >= 0 && colonIndex < profileId.Length - 1
            ? profileId[(colonIndex + 1)..]
            : profileId;
    }

    public static bool IsStageHarnessSelectable(string? profileId)
    {
        var stageId = ExtractBaseStageId(profileId);
        return !string.Equals(stageId, StandardStageIds.Triage, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(stageId, StandardStageIds.Ideation, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetPinnedHarnessReason(string? profileId)
    {
        var stageId = ExtractBaseStageId(profileId);
        if (string.Equals(stageId, StandardStageIds.Triage, StringComparison.OrdinalIgnoreCase))
        {
            return TriagePinnedReason;
        }

        if (string.Equals(stageId, StandardStageIds.Ideation, StringComparison.OrdinalIgnoreCase))
        {
            return IdeationPinnedReason;
        }

        return "This stage is pinned to the Crucible harness.";
    }

    public static bool IsAcpProfile(AgentProfile? profile) =>
        profile != null &&
        string.Equals(profile.HarnessId, StandardHarnessIds.Acp, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<AcpModelOption> GetEffectiveAcpModels(AcpCatalogProviderStatus? status) =>
        status?.AvailableModels is { Count: > 0 }
            ? status.AvailableModels
            : AcpHarnessCatalogService.DefaultAntigravityModels;

    public static IReadOnlyList<AcpThinkingEffortOption> GetSupportedAcpThinkingEfforts(
        AcpCatalogProviderStatus? status,
        string? modelName)
    {
        var models = GetEffectiveAcpModels(status);
        var matchedModel = !string.IsNullOrWhiteSpace(modelName)
            ? models.FirstOrDefault(m => string.Equals(m.ModelId, modelName.Trim(), StringComparison.OrdinalIgnoreCase))
            : null;

        var rawLevels = matchedModel?.SupportedThoughtLevels is { Count: > 0 }
            ? matchedModel.SupportedThoughtLevels
            : AcpHarnessCatalogService.DefaultThoughtLevels;

        var options = new List<AcpThinkingEffortOption>();
        foreach (var raw in rawLevels)
        {
            var mapped = MapThoughtLevelToEffort(raw);
            if (mapped != null && !options.Any(o => o.Effort == mapped.Effort))
            {
                options.Add(mapped);
            }
        }

        if (options.Count == 0)
        {
            return
            [
                new(ThinkingEffort.Low, "Low (thought_level: low)"),
                new(ThinkingEffort.Medium, "Medium (thought_level: medium)"),
                new(ThinkingEffort.High, "High (thought_level: high)")
            ];
        }

        return options;
    }

    public static void NormalizeAcpProfileSettings(AgentProfile profile, AcpCatalogProviderStatus? status)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!IsAcpProfile(profile))
        {
            return;
        }

        profile.OptimizationFlags = null;
        profile.RoutingTarget = OllamaRoutingTarget.Auto;
        profile.SecurityTier = SandboxSecurityTier.Standard;
        profile.ThinkingTokenBudget = null;

        var supported = GetSupportedAcpThinkingEfforts(status, profile.ModelName);
        if (!supported.Any(o => o.Effort == profile.ThinkingEffort))
        {
            profile.ThinkingEffort = supported.Any(o => o.Effort == ThinkingEffort.High)
                ? ThinkingEffort.High
                : supported[^1].Effort;
        }
    }

    private static AcpThinkingEffortOption? MapThoughtLevelToEffort(string? level) =>
        level?.Trim().ToLowerInvariant() switch
        {
            "low" => new(ThinkingEffort.Low, "Low (thought_level: low)"),
            "medium" => new(ThinkingEffort.Medium, "Medium (thought_level: medium)"),
            "high" => new(ThinkingEffort.High, "High (thought_level: high)"),
            _ => null
        };

    public static AntigravityStatusBadge FormatAntigravityBadge(AcpCatalogProviderStatus? status, bool isRefreshing)
    {
        if (isRefreshing)
        {
            return new AntigravityStatusBadge("waiting", "badge-indigo", "Probing Antigravity ACP CLI...");
        }

        if (status == null || !status.IsInstalled)
        {
            return new AntigravityStatusBadge("blocked", "badge-amber", "⚪ Antigravity ACP Not Detected");
        }

        if (!status.IsAuthenticated)
        {
            return new AntigravityStatusBadge(
                "waiting",
                "badge-amber",
                "🟡 Antigravity ACP Installed — Authentication Required (Run 'wsl -e agy login')");
        }

        var versionPart = !string.IsNullOrWhiteSpace(status.ServerVersion)
            ? $"v{status.ServerVersion} "
            : string.Empty;
        var targetPart = !string.IsNullOrWhiteSpace(status.ExecutionTargetDescription)
            ? status.ExecutionTargetDescription
            : "WSL2 + Container";
        var tierPart = !string.IsNullOrWhiteSpace(status.AccountTierName)
            ? $" | {status.AccountTierName}"
            : string.Empty;
        var modelCount = GetEffectiveAcpModels(status).Count;

        return new AntigravityStatusBadge(
            "running",
            "badge-teal",
            $"🟢 Antigravity ACP {versionPart}Ready ({targetPart}{tierPart} | {modelCount} model families)");
    }

    public static void ApplyHarnessChange(
        AgentProfile profile,
        string? selectedHarnessId,
        AcpCatalogProviderStatus? acpStatus,
        string? fallbackCrucibleModel)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!IsStageHarnessSelectable(profile.Id))
        {
            profile.HarnessId = StandardHarnessIds.Crucible;
            return;
        }

        var normalizedHarness = string.Equals(selectedHarnessId, StandardHarnessIds.Acp, StringComparison.OrdinalIgnoreCase)
            ? StandardHarnessIds.Acp
            : StandardHarnessIds.Crucible;

        profile.HarnessId = normalizedHarness;
        var acpModels = GetEffectiveAcpModels(acpStatus);

        if (normalizedHarness == StandardHarnessIds.Acp)
        {
            if (string.IsNullOrWhiteSpace(profile.ModelName) ||
                !acpModels.Any(m => string.Equals(m.ModelId, profile.ModelName, StringComparison.OrdinalIgnoreCase)))
            {
                profile.ModelName = acpModels[0].ModelId;
            }

            NormalizeAcpProfileSettings(profile, acpStatus);
        }
        else
        {
            if (acpModels.Any(m => string.Equals(m.ModelId, profile.ModelName, StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(fallbackCrucibleModel))
            {
                profile.ModelName = fallbackCrucibleModel;
            }
        }
    }

    public static AgentProfile CloneToDedicatedProfile(AgentProfile globalProfile, int workspaceId, string? workspaceName)
    {
        ArgumentNullException.ThrowIfNull(globalProfile);

        return new AgentProfile
        {
            Id = $"ws-{workspaceId}:{globalProfile.Id}",
            WorkspaceId = workspaceId,
            Name = $"{globalProfile.Name} ({workspaceName ?? "Workspace"})",
            HarnessId = IsStageHarnessSelectable(globalProfile.Id) ? globalProfile.HarnessId : StandardHarnessIds.Crucible,
            HarnessConfigurationJson = globalProfile.HarnessConfigurationJson,
            ModelName = globalProfile.ModelName,
            Mode = globalProfile.Mode,
            SecurityTier = globalProfile.SecurityTier,
            RoutingTarget = globalProfile.RoutingTarget,
            PermittedTools = globalProfile.PermittedTools?.ToList(),
            StageDirectives = globalProfile.StageDirectives != null ? new Dictionary<string, string>(globalProfile.StageDirectives) : null,
            Temperature = globalProfile.Temperature,
            ThinkingEffort = globalProfile.ThinkingEffort,
            ThinkingTokenBudget = globalProfile.ThinkingTokenBudget,
            MaxTurns = globalProfile.MaxTurns,
            OptimizationFlags = globalProfile.OptimizationFlags?.Clone(),
            DifficultyOverrides = globalProfile.CloneDifficultyOverrides(),
            ContextWindowTokens = globalProfile.ContextWindowTokens,
            MaxOutputTokens = globalProfile.MaxOutputTokens,
            SystemPrompt = globalProfile.SystemPrompt
        };
    }

    public static void SynchronizeProfileForSave(
        AgentProfile existing,
        AgentProfile source,
        LlmProviderType providerType,
        IReadOnlyDictionary<string, int> discoveredContextWindows)
        => SynchronizeProfileForSave(existing, source, providerType, discoveredContextWindows, acpStatus: null);

    public static void SynchronizeProfileForSave(
        AgentProfile existing,
        AgentProfile source,
        LlmProviderType providerType,
        IReadOnlyDictionary<string, int> discoveredContextWindows,
        AcpCatalogProviderStatus? acpStatus)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(discoveredContextWindows);

        var isSelectable = IsStageHarnessSelectable(source.Id);
        var effectiveHarnessId = isSelectable ? source.HarnessId : StandardHarnessIds.Crucible;
        var isAcp = string.Equals(effectiveHarnessId, StandardHarnessIds.Acp, StringComparison.OrdinalIgnoreCase);

        if (isAcp || providerType != LlmProviderType.OllamaRouter)
        {
            source.RoutingTarget = OllamaRoutingTarget.Auto;
        }

        if (isAcp)
        {
            source.HarnessId = StandardHarnessIds.Acp;
            NormalizeAcpProfileSettings(source, acpStatus);
        }
        else if (!string.IsNullOrWhiteSpace(source.ModelName) &&
                 discoveredContextWindows.TryGetValue(source.ModelName, out var ctxTokens))
        {
            source.ContextWindowTokens = ctxTokens;
        }

        AgentProfileDifficultyMatrixHelper.NormalizeDifficultyOverridesForSave(
            source,
            providerType,
            discoveredContextWindows,
            acpStatus);

        existing.HarnessId = effectiveHarnessId;
        existing.HarnessConfigurationJson = source.HarnessConfigurationJson;
        existing.ModelName = source.ModelName;
        existing.RoutingTarget = source.RoutingTarget;
        existing.ThinkingEffort = source.ThinkingEffort;
        existing.ThinkingTokenBudget = source.ThinkingTokenBudget;
        existing.Temperature = source.Temperature;
        existing.MaxTurns = source.MaxTurns;
        existing.SecurityTier = source.SecurityTier;
        existing.SystemPrompt = source.SystemPrompt;
        existing.OptimizationFlags = source.OptimizationFlags;
        existing.DifficultyOverrides = source.CloneDifficultyOverrides();
        existing.ContextWindowTokens = source.ContextWindowTokens;
        existing.MaxOutputTokens = source.MaxOutputTokens;
    }
}
