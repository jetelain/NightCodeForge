using System.Text.Json;
using System.Text.RegularExpressions;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Resolves effective native tool availability and renders conditional Scriban expressions inside workflow
/// <c>StageDirective</c> templates (such as dual tool-enabled vs. XML-fallback review instructions).
/// </summary>
public static partial class StageDirectiveRenderer
{
    private const string AllowedToolsPropertyName = "AllowedTools";

    [GeneratedRegex(@"\{\{[^}]*\btools\.", RegexOptions.CultureInvariant)]
    private static partial Regex ToolsTemplateExpressionRegex();

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="directive"/> contains Scriban template syntax
    /// referencing the <c>tools.</c> context.
    /// </summary>
    public static bool ContainsTemplateExpression(string? directive) =>
        !string.IsNullOrWhiteSpace(directive) && ToolsTemplateExpressionRegex().IsMatch(directive);

    /// <summary>
    /// Only the in-process Crucible harness exposes NightCodeForge's native canonical tools
    /// (such as <c>request_changes</c> and <c>ask</c>); external harnesses (ACP, Pi RPC) rely on structured text tags.
    /// </summary>
    public static bool SupportsNativeCanonicalTools(string? harnessId) =>
        string.Equals(harnessId, StandardHarnessIds.Crucible, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the effective native canonical tool names available to <paramref name="profile"/> for a stage.
    /// Returns an empty list for external harnesses that do not expose NightCodeForge's native canonical tools.
    /// </summary>
    public static IReadOnlyList<string> ResolveEffectivePermittedTools(
        string? harnessId,
        AgentProfile? profile,
        AgentMode requiredMode,
        bool isTriageSession = false)
    {
        if (!SupportsNativeCanonicalTools(harnessId) || profile == null)
        {
            return Array.Empty<string>();
        }

        var configAllowedTools = ExtractAllowedTools(profile.HarnessConfigurationJson);
        return ResolveEffectivePermittedTools(
            harnessId,
            profile,
            requiredMode,
            configAllowedTools,
            isTriageSession);
    }

    /// <summary>
    /// Resolves the effective native canonical tool names available to <paramref name="profile"/> for a stage
    /// using an explicit <paramref name="configAllowedToolsOverride"/> list when already parsed.
    /// </summary>
    public static IReadOnlyList<string> ResolveEffectivePermittedTools(
        string? harnessId,
        AgentProfile? profile,
        AgentMode requiredMode,
        IReadOnlyList<string>? configAllowedToolsOverride,
        bool isTriageSession = false)
    {
        if (!SupportsNativeCanonicalTools(harnessId) || profile == null)
        {
            return Array.Empty<string>();
        }

        var configAllowedTools = configAllowedToolsOverride ?? Array.Empty<string>();

        if (isTriageSession || requiredMode == AgentMode.ReadOnly || profile.Mode == AgentMode.ReadOnly)
        {
            var defaultBaseline = isTriageSession ? DefaultAgentTools.Triage : DefaultAgentTools.ReadOnly;
            var baselineAllowed = (profile.PermittedTools != null && profile.PermittedTools.Count > 0)
                ? profile.PermittedTools
                : defaultBaseline;

            if (isTriageSession || (requiredMode == AgentMode.ReadOnly && profile.Mode != AgentMode.ReadOnly))
            {
                var allowedSet = new HashSet<string>(defaultBaseline, StringComparer.OrdinalIgnoreCase);
                var filtered = baselineAllowed
                    .Where(t => !string.IsNullOrWhiteSpace(t) && allowedSet.Contains(t.Trim()))
                    .Select(t => t.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                baselineAllowed = filtered.Count > 0 ? filtered : defaultBaseline;
            }

            if (configAllowedTools.Count > 0)
            {
                var baselineSet = new HashSet<string>(baselineAllowed, StringComparer.OrdinalIgnoreCase);
                var intersected = configAllowedTools
                    .Where(t => !string.IsNullOrWhiteSpace(t) && baselineSet.Contains(t.Trim()))
                    .Select(t => t.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return intersected.Count > 0 ? intersected : baselineAllowed;
            }

            return baselineAllowed;
        }

        if (configAllowedTools.Count > 0)
        {
            return configAllowedTools;
        }

        if (profile.PermittedTools != null && profile.PermittedTools.Count > 0)
        {
            return profile.PermittedTools;
        }

        return DefaultAgentTools.GetTools(requiredMode);
    }

    /// <summary>
    /// Renders conditional Scriban tool expressions inside <paramref name="stageDirective"/> against <paramref name="permittedTools"/>.
    /// Returns plain directives unchanged.
    /// </summary>
    public static string Render(
        string? stageDirective,
        IReadOnlyList<string>? permittedTools,
        IPromptTemplateRenderer? renderer = null)
    {
        if (string.IsNullOrWhiteSpace(stageDirective))
        {
            return string.Empty;
        }

        if (!ContainsTemplateExpression(stageDirective))
        {
            return stageDirective;
        }

        var permittedSet = new HashSet<string>(
            (permittedTools ?? Array.Empty<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var model = new
        {
            tools = new
            {
                permitted = permittedSet.ToList(),
                has_request_changes = permittedSet.Contains(CanonicalToolNames.RequestChanges),
                has_ask = permittedSet.Contains(CanonicalToolNames.Ask)
            }
        };

        return (renderer ?? new ScribanPromptTemplateRenderer()).Render(stageDirective, model);
    }

    private static IReadOnlyList<string> ExtractAllowedTools(string? harnessConfigurationJson)
    {
        if (string.IsNullOrWhiteSpace(harnessConfigurationJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var doc = JsonDocument.Parse(harnessConfigurationJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<string>();
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(prop.Name, AllowedToolsPropertyName, StringComparison.OrdinalIgnoreCase) ||
                    prop.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var list = new List<string>();
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var val = item.GetString();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            list.Add(val.Trim());
                        }
                    }
                }
                return list;
            }
        }
        catch (JsonException)
        {
            // Justification: malformed HarnessConfigurationJson falls back to profile/mode tool defaults at runtime.
        }

        return Array.Empty<string>();
    }
}
