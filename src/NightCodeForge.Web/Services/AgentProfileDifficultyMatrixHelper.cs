using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Agents.Acp;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Web.Services;

public enum EscalationMatrixRole
{
    ArchitectureAndReview,
    CoderImplementation
}

public sealed class DifficultyMatrixCellModel
{
    public TaskDifficulty Difficulty { get; init; }
    public EscalationMatrixRole Role { get; init; }
    public string HarnessId { get; set; } = StandardHarnessIds.Crucible;
    public string ModelName { get; set; } = string.Empty;
    public bool IsCustomModel { get; set; }
    public ThinkingEffort ThinkingEffort { get; set; } = ThinkingEffort.Medium;
    public int? ThinkingTokenBudget { get; set; }
    public OllamaRoutingTarget RoutingTarget { get; set; } = OllamaRoutingTarget.Auto;
    public int? ContextWindowTokens { get; set; }
    public int? MaxOutputTokens { get; set; }
    public string? HarnessConfigurationJson { get; set; }
}

/// <summary>
/// Presentation and persistence helper mapping between individual pipeline <see cref="AgentProfile"/> entities
/// and the Role-Based Task Difficulty Escalation Matrix in Settings.
/// </summary>
public static class AgentProfileDifficultyMatrixHelper
{
    public static IReadOnlyList<EscalationMatrixRole> AllRoles { get; } =
    [
        EscalationMatrixRole.ArchitectureAndReview,
        EscalationMatrixRole.CoderImplementation
    ];

    public static IReadOnlyList<AcpModelOption> GetBuiltInAcpModels() =>
        AcpHarnessCatalogService.DefaultAntigravityModels;

    public static async Task<AcpCatalogProviderStatus?> TryGetAcpStatusAsync(
        IServiceProvider? serviceProvider,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var catalog = serviceProvider?.GetService<IAcpHarnessCatalogService>();
        if (catalog == null)
        {
            return null;
        }

        return await catalog.ProbeProviderAsync(KnownAcpCatalogIds.Antigravity, forceRefresh, cancellationToken);
    }

    public static AgentProfile? FindProfileByBaseStageId(IEnumerable<AgentProfile> profiles, string baseStageId)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles.FirstOrDefault(p =>
            string.Equals(AgentProfileAcpPresentationHelper.ExtractBaseStageId(p.Id), baseStageId, StringComparison.OrdinalIgnoreCase));
    }

    public static string GetRoleTitle(EscalationMatrixRole role) => role switch
    {
        EscalationMatrixRole.ArchitectureAndReview => "Architecture & Review",
        EscalationMatrixRole.CoderImplementation => "Coder / Implementation",
        _ => role.ToString()
    };

    public static string GetRoleStageHint(EscalationMatrixRole role) => role switch
    {
        EscalationMatrixRole.ArchitectureAndReview => "task-review, code-review-fix",
        EscalationMatrixRole.CoderImplementation => "implementation",
        _ => string.Empty
    };

    public static DifficultyMatrixCellModel BuildCell(
        IReadOnlyList<AgentProfile> profiles,
        TaskDifficulty difficulty,
        EscalationMatrixRole role,
        IReadOnlyList<string> discoveredCrucibleModels,
        AcpCatalogProviderStatus? acpStatus)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(discoveredCrucibleModels);

        var architectProfile = FindProfileByBaseStageId(profiles, StandardStageIds.TaskReview)
            ?? FindProfileByBaseStageId(profiles, StandardStageIds.CodeReviewAndFix);
        var sourceProfile = role == EscalationMatrixRole.ArchitectureAndReview
            ? architectProfile
            : FindProfileByBaseStageId(profiles, StandardStageIds.Implementation);

        var resolved = sourceProfile != null
            ? sourceProfile.ResolveForDifficulty(difficulty, architectProfile)
            : new AgentProfile();

        var isAcp = string.Equals(resolved.HarnessId, StandardHarnessIds.Acp, StringComparison.OrdinalIgnoreCase);
        var normalizedHarness = isAcp ? StandardHarnessIds.Acp : StandardHarnessIds.Crucible;
        var modelName = resolved.ModelName ?? string.Empty;

        var knownModels = isAcp
            ? AgentProfileAcpPresentationHelper.GetEffectiveAcpModels(acpStatus).Select(m => m.ModelId).ToList()
            : discoveredCrucibleModels.ToList();

        var isCustom = !string.IsNullOrWhiteSpace(modelName) &&
                       !knownModels.Any(m => string.Equals(m, modelName, StringComparison.OrdinalIgnoreCase));

        return new DifficultyMatrixCellModel
        {
            Difficulty = difficulty,
            Role = role,
            HarnessId = normalizedHarness,
            ModelName = modelName,
            IsCustomModel = isCustom,
            ThinkingEffort = resolved.ThinkingEffort,
            ThinkingTokenBudget = resolved.ThinkingTokenBudget,
            RoutingTarget = resolved.RoutingTarget,
            ContextWindowTokens = resolved.ContextWindowTokens,
            MaxOutputTokens = resolved.MaxOutputTokens,
            HarnessConfigurationJson = resolved.HarnessConfigurationJson
        };
    }

    public static void ApplyHarnessSwitch(
        DifficultyMatrixCellModel cell,
        string? selectedHarnessId,
        string? fallbackCrucibleModel,
        AcpCatalogProviderStatus? acpStatus)
    {
        ArgumentNullException.ThrowIfNull(cell);

        var normalizedHarness = string.Equals(selectedHarnessId, StandardHarnessIds.Acp, StringComparison.OrdinalIgnoreCase)
            ? StandardHarnessIds.Acp
            : StandardHarnessIds.Crucible;

        var harnessChanged = !string.Equals(cell.HarnessId, normalizedHarness, StringComparison.OrdinalIgnoreCase);
        cell.HarnessId = normalizedHarness;
        var acpModels = AgentProfileAcpPresentationHelper.GetEffectiveAcpModels(acpStatus);

        if (normalizedHarness == StandardHarnessIds.Acp)
        {
            if (string.IsNullOrWhiteSpace(cell.ModelName) ||
                !acpModels.Any(m => string.Equals(m.ModelId, cell.ModelName, StringComparison.OrdinalIgnoreCase)))
            {
                cell.ModelName = acpModels[0].ModelId;
            }

            cell.IsCustomModel = false;
            cell.RoutingTarget = OllamaRoutingTarget.Auto;
            cell.ThinkingTokenBudget = null;
            cell.ContextWindowTokens = null;
            cell.ThinkingEffort = NormalizeAcpThinkingEffort(acpStatus, cell.ModelName, cell.ThinkingEffort);
        }
        else
        {
            if (acpModels.Any(m => string.Equals(m.ModelId, cell.ModelName, StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(fallbackCrucibleModel))
            {
                cell.ModelName = fallbackCrucibleModel;
                cell.IsCustomModel = false;
                cell.ContextWindowTokens = null;
            }
            else if (harnessChanged)
            {
                cell.ContextWindowTokens = null;
            }
        }
    }

    public static void ApplyCellToProfiles(
        IList<AgentProfile> profiles,
        DifficultyMatrixCellModel cell,
        LlmProviderType providerType,
        IReadOnlyDictionary<string, int> discoveredContextWindows,
        AcpCatalogProviderStatus? acpStatus)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(discoveredContextWindows);

        var isAcp = string.Equals(cell.HarnessId, StandardHarnessIds.Acp, StringComparison.OrdinalIgnoreCase);
        var normalizedHarness = isAcp ? StandardHarnessIds.Acp : StandardHarnessIds.Crucible;
        cell.HarnessId = normalizedHarness;

        if (isAcp || providerType != LlmProviderType.OllamaRouter)
        {
            cell.RoutingTarget = OllamaRoutingTarget.Auto;
        }

        if (isAcp)
        {
            cell.ThinkingTokenBudget = null;
            cell.ContextWindowTokens = null;
            cell.ThinkingEffort = NormalizeAcpThinkingEffort(acpStatus, cell.ModelName, cell.ThinkingEffort);
        }
        else if (!string.IsNullOrWhiteSpace(cell.ModelName) &&
                 discoveredContextWindows.TryGetValue(cell.ModelName, out var ctxTokens))
        {
            cell.ContextWindowTokens = ctxTokens;
        }

        var targetStageIds = GetStageIdsForRole(cell.Role);
        foreach (var stageId in targetStageIds)
        {
            var profile = FindProfileByBaseStageId(profiles, stageId);
            if (profile == null)
            {
                continue;
            }

            if (cell.Difficulty == TaskDifficulty.Standard)
            {
                profile.HarnessId = normalizedHarness;
                profile.ModelName = cell.ModelName;
                profile.ThinkingEffort = cell.ThinkingEffort;
                profile.ThinkingTokenBudget = cell.ThinkingTokenBudget;
                profile.RoutingTarget = cell.RoutingTarget;
                profile.ContextWindowTokens = cell.ContextWindowTokens;
                profile.MaxOutputTokens = cell.MaxOutputTokens;
                profile.HarnessConfigurationJson = cell.HarnessConfigurationJson;
                if (isAcp)
                {
                    profile.OptimizationFlags = null;
                    profile.SecurityTier = SandboxSecurityTier.Standard;
                }
            }
            else
            {
                profile.DifficultyOverrides ??= new Dictionary<TaskDifficulty, DifficultyStageOverride>();
                profile.DifficultyOverrides[cell.Difficulty] = new DifficultyStageOverride
                {
                    HarnessId = normalizedHarness,
                    ModelName = string.IsNullOrWhiteSpace(cell.ModelName) ? string.Empty : cell.ModelName.Trim(),
                    ThinkingEffort = cell.ThinkingEffort,
                    ThinkingTokenBudget = cell.ThinkingTokenBudget,
                    RoutingTarget = cell.RoutingTarget,
                    ContextWindowTokens = cell.ContextWindowTokens,
                    MaxOutputTokens = cell.MaxOutputTokens,
                    HarnessConfigurationJson = cell.HarnessConfigurationJson
                };
            }
        }
    }

    public static bool IsCoderSameAsArchitect(
        IReadOnlyList<AgentProfile> profiles,
        TaskDifficulty difficulty)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var architectProfile = FindProfileByBaseStageId(profiles, StandardStageIds.TaskReview)
            ?? FindProfileByBaseStageId(profiles, StandardStageIds.CodeReviewAndFix);
        var coderProfile = FindProfileByBaseStageId(profiles, StandardStageIds.Implementation);
        if (architectProfile == null || coderProfile == null)
        {
            return false;
        }

        var archResolved = architectProfile.ResolveForDifficulty(difficulty, architectProfile);
        var coderResolved = coderProfile.ResolveForDifficulty(difficulty, architectProfile);

        return string.Equals(archResolved.HarnessId, coderResolved.HarnessId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(archResolved.ModelName, coderResolved.ModelName, StringComparison.OrdinalIgnoreCase) &&
               archResolved.ThinkingEffort == coderResolved.ThinkingEffort &&
               archResolved.ThinkingTokenBudget == coderResolved.ThinkingTokenBudget &&
               archResolved.RoutingTarget == coderResolved.RoutingTarget;
    }

    public static void CopyArchitectToCoder(
        IList<AgentProfile> profiles,
        TaskDifficulty difficulty,
        LlmProviderType providerType,
        IReadOnlyDictionary<string, int> discoveredContextWindows,
        AcpCatalogProviderStatus? acpStatus)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(discoveredContextWindows);

        var architectCell = BuildCell(
            profiles as IReadOnlyList<AgentProfile> ?? profiles.ToList(),
            difficulty,
            EscalationMatrixRole.ArchitectureAndReview,
            Array.Empty<string>(),
            acpStatus);

        var coderCell = new DifficultyMatrixCellModel
        {
            Difficulty = difficulty,
            Role = EscalationMatrixRole.CoderImplementation,
            HarnessId = architectCell.HarnessId,
            ModelName = architectCell.ModelName,
            IsCustomModel = architectCell.IsCustomModel,
            ThinkingEffort = architectCell.ThinkingEffort,
            ThinkingTokenBudget = architectCell.ThinkingTokenBudget,
            RoutingTarget = architectCell.RoutingTarget,
            ContextWindowTokens = architectCell.ContextWindowTokens,
            MaxOutputTokens = architectCell.MaxOutputTokens,
            HarnessConfigurationJson = architectCell.HarnessConfigurationJson
        };

        ApplyCellToProfiles(profiles, coderCell, providerType, discoveredContextWindows, acpStatus);
    }

    public static IReadOnlyList<string> GetDistinctCrucibleModelNames(IEnumerable<AgentProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void TryAdd(string? harnessId, string? modelName)
        {
            if (string.Equals(harnessId, StandardHarnessIds.Acp, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(modelName))
            {
                return;
            }

            var trimmed = modelName.Trim();
            if (distinct.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        foreach (var profile in profiles)
        {
            if (profile == null)
            {
                continue;
            }

            TryAdd(profile.HarnessId, profile.ModelName);

            if (profile.DifficultyOverrides == null)
            {
                continue;
            }

            foreach (var stageOverride in profile.DifficultyOverrides.Values)
            {
                if (stageOverride == null)
                {
                    continue;
                }

                var effectiveHarness = string.IsNullOrWhiteSpace(stageOverride.HarnessId)
                    ? profile.HarnessId
                    : stageOverride.HarnessId;
                TryAdd(effectiveHarness, stageOverride.ModelName);
            }
        }

        return result;
    }

    public static void NormalizeDifficultyOverridesForSave(
        AgentProfile profile,
        LlmProviderType providerType,
        IReadOnlyDictionary<string, int> discoveredContextWindows,
        AcpCatalogProviderStatus? acpStatus)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(discoveredContextWindows);

        if (!AgentProfileAcpPresentationHelper.IsStageHarnessSelectable(profile.Id))
        {
            profile.DifficultyOverrides = null;
            return;
        }

        if (profile.DifficultyOverrides == null || profile.DifficultyOverrides.Count == 0)
        {
            return;
        }

        foreach (var difficulty in profile.DifficultyOverrides.Keys.ToList())
        {
            if (difficulty == TaskDifficulty.Standard)
            {
                profile.DifficultyOverrides.Remove(difficulty);
                continue;
            }

            var ov = profile.DifficultyOverrides[difficulty];
            if (ov == null)
            {
                profile.DifficultyOverrides.Remove(difficulty);
                continue;
            }

            var effectiveHarness = string.IsNullOrWhiteSpace(ov.HarnessId) ? profile.HarnessId : ov.HarnessId;
            var isAcp = string.Equals(effectiveHarness, StandardHarnessIds.Acp, StringComparison.OrdinalIgnoreCase);
            ov.HarnessId = isAcp ? StandardHarnessIds.Acp : StandardHarnessIds.Crucible;

            if (isAcp || providerType != LlmProviderType.OllamaRouter)
            {
                ov.RoutingTarget = OllamaRoutingTarget.Auto;
            }

            var effectiveModel = !string.IsNullOrWhiteSpace(ov.ModelName) ? ov.ModelName : profile.ModelName;
            if (isAcp)
            {
                ov.ThinkingTokenBudget = null;
                ov.ContextWindowTokens = null;
                ov.ThinkingEffort = NormalizeAcpThinkingEffort(
                    acpStatus,
                    effectiveModel,
                    ov.ThinkingEffort);
            }
            else if (!string.IsNullOrWhiteSpace(effectiveModel) &&
                     discoveredContextWindows.TryGetValue(effectiveModel, out var ctxTokens))
            {
                ov.ContextWindowTokens = ctxTokens;
            }
        }

        if (profile.DifficultyOverrides.Count == 0)
        {
            profile.DifficultyOverrides = null;
        }
    }

    private static IReadOnlyList<string> GetStageIdsForRole(EscalationMatrixRole role) => role switch
    {
        EscalationMatrixRole.ArchitectureAndReview => [StandardStageIds.TaskReview, StandardStageIds.CodeReviewAndFix],
        EscalationMatrixRole.CoderImplementation => [StandardStageIds.Implementation],
        _ => Array.Empty<string>()
    };

    private static ThinkingEffort NormalizeAcpThinkingEffort(
        AcpCatalogProviderStatus? acpStatus,
        string? modelName,
        ThinkingEffort requestedEffort)
    {
        var supported = AgentProfileAcpPresentationHelper.GetSupportedAcpThinkingEfforts(acpStatus, modelName);
        if (supported.Any(o => o.Effort == requestedEffort))
        {
            return requestedEffort;
        }

        return supported.Any(o => o.Effort == ThinkingEffort.High)
            ? ThinkingEffort.High
            : supported[^1].Effort;
    }
}
