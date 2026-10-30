using System.Text.Json;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

public enum SkillsResolutionSource
{
    LocalDatabaseOverride,
    RepositoryConfig,
    SystemDefault
}

public record GlobalSkillsResolutionResult(
    bool IsEnabled,
    IReadOnlySet<string> ActiveSkills,
    SkillsResolutionSource Source,
    IReadOnlyList<string> ExcludedSkills);

/// <summary>
/// Pure cascading policy evaluation engine for global skills injection.
/// Precedence order:
/// 1. Workspace Local DB Override (ForceDisabled / ForceEnabled)
/// 2. Repository .nightcodeforge/config.json (Skills.Enabled)
/// 3. System Default (Enabled for supported stacks)
/// Exclusions from repository config and workspace DB are merged.
/// </summary>
public static class GlobalSkillsResolver
{
    public static GlobalSkillsResolutionResult Resolve(
        Workspace workspace,
        WorkspaceProjectConfig? projectConfig,
        IReadOnlyList<string> candidateSkillNames)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        candidateSkillNames ??= Array.Empty<string>();

        // 1. Evaluate Master Enablement
        bool isEnabled;
        SkillsResolutionSource source;

        if (workspace.GlobalSkillsInjectionMode == GlobalSkillsInjectionMode.ForceDisabled)
        {
            return new GlobalSkillsResolutionResult(
                false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                SkillsResolutionSource.LocalDatabaseOverride,
                Array.Empty<string>());
        }

        if (workspace.GlobalSkillsInjectionMode == GlobalSkillsInjectionMode.ForceEnabled)
        {
            isEnabled = true;
            source = SkillsResolutionSource.LocalDatabaseOverride;
        }
        else // Inherit
        {
            if (projectConfig?.Skills?.Enabled.HasValue == true)
            {
                isEnabled = projectConfig.Skills.Enabled.Value;
                source = SkillsResolutionSource.RepositoryConfig;
            }
            else
            {
                isEnabled = true; // System default: enabled
                source = SkillsResolutionSource.SystemDefault;
            }
        }

        if (!isEnabled)
        {
            return new GlobalSkillsResolutionResult(
                false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                source,
                Array.Empty<string>());
        }

        // 2. Evaluate Exclusions
        var exclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (projectConfig?.Skills?.DisabledSkills != null)
        {
            foreach (var s in projectConfig.Skills.DisabledSkills)
            {
                if (!string.IsNullOrWhiteSpace(s))
                {
                    exclusions.Add(s.Trim());
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(workspace.DisabledGlobalSkillsJson))
        {
            try
            {
                var localDisabled = JsonSerializer.Deserialize<List<string>>(workspace.DisabledGlobalSkillsJson);
                if (localDisabled != null)
                {
                    foreach (var s in localDisabled)
                    {
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            exclusions.Add(s.Trim());
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Justified ignore: fallback gracefully on corrupted JSON stored in DB
            }
        }

        var active = candidateSkillNames
            .Where(s => !string.IsNullOrWhiteSpace(s) && !exclusions.Contains(s.Trim()))
            .Select(s => s.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new GlobalSkillsResolutionResult(true, active, source, exclusions.ToList());
    }
}

