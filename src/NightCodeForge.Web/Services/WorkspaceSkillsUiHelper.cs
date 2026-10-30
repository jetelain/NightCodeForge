using System.Text.Json;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

public record WorkspaceSkillItem(
    string Name,
    string Description,
    bool IsActive,
    bool IsLocallyDisabled);

public record WorkspaceSkillsResolutionStatus(
    bool IsEnabled,
    string BadgeText,
    string BadgeClass,
    IReadOnlyList<WorkspaceSkillItem> CandidateSkills);

/// <summary>
/// Helper for managing workspace global skills injection UI, resolution badges,
/// and local exclusion JSON serialization with zero Razor @code bloat.
/// </summary>
public static class WorkspaceSkillsUiHelper
{
    public static List<string> ParseDisabledSkills(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    public static string SerializeDisabledSkills(IEnumerable<string> disabledSkills)
    {
        var distinct = disabledSkills
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return JsonSerializer.Serialize(distinct);
    }

    public static string ToggleSkillExclusion(string? currentJson, string skillName, bool isExcluded)
    {
        var list = ParseDisabledSkills(currentJson);
        if (isExcluded)
        {
            if (!list.Contains(skillName, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(skillName);
            }
        }
        else
        {
            list.RemoveAll(s => string.Equals(s, skillName, StringComparison.OrdinalIgnoreCase));
        }
        return SerializeDisabledSkills(list);
    }

    public static WorkspaceSkillsResolutionStatus GetStatus(
        Workspace workspace,
        WorkspaceProjectConfig? projectConfig,
        IReadOnlyList<SkillDescriptor> candidateDescriptors)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        candidateDescriptors ??= Array.Empty<SkillDescriptor>();

        var candidateNames = candidateDescriptors.Select(d => d.Name).ToList();
        var resolution = GlobalSkillsResolver.Resolve(workspace, projectConfig, candidateNames);
        var localDisabled = ParseDisabledSkills(workspace.DisabledGlobalSkillsJson);

        string badgeText;
        string badgeClass;

        if (!resolution.IsEnabled)
        {
            badgeText = workspace.GlobalSkillsInjectionMode == GlobalSkillsInjectionMode.ForceDisabled
                ? "🔴 Disabled via Local Workspace Override"
                : "🔴 Disabled via .nightcodeforge/config.json";
            badgeClass = "badge-amber";
        }
        else
        {
            badgeText = resolution.Source switch
            {
                SkillsResolutionSource.LocalDatabaseOverride => "🔵 Force Enabled (Local Override)",
                SkillsResolutionSource.RepositoryConfig => "🟡 Active via .nightcodeforge/config.json",
                _ => "🟢 Active via System Default"
            };
            badgeClass = resolution.Source == SkillsResolutionSource.SystemDefault ? "badge-teal" : "badge-indigo";
        }

        var items = candidateDescriptors
            .OrderBy(d => d.Name)
            .Select(d => new WorkspaceSkillItem(
                Name: d.Name,
                Description: d.Description,
                IsActive: resolution.ActiveSkills.Contains(d.Name),
                IsLocallyDisabled: localDisabled.Contains(d.Name, StringComparer.OrdinalIgnoreCase)
            )).ToList();

        return new WorkspaceSkillsResolutionStatus(resolution.IsEnabled, badgeText, badgeClass, items);
    }
}

