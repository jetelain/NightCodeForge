using System.Text.Json;
using System.Text.RegularExpressions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

/// <summary>
/// Parses the <c>POST /api/show</c> payload shared by Ollama and (proxied) OllamaRouter:
/// effective context window and declared capabilities. Never throws on absent fields.
/// </summary>
internal static partial class OllamaShowParser
{
    public const string ThinkingCapability = "thinking";

    private const string ParametersProperty = "parameters";
    private const string ModelInfoProperty = "model_info";
    private const string DetailsProperty = "details";
    private const string CapabilitiesProperty = "capabilities";
    private const string ValuesProperty = "values";
    private const string DefaultProperty = "default";
    private const string ContextLengthSuffix = ".context_length";

    [GeneratedRegex(@"(?:^|\n)\s*num_ctx\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NumCtxRegex();

    /// <summary>
    /// Modelfile <c>num_ctx</c> when set, otherwise the architecture <c>*.context_length</c>.
    /// </summary>
    public static int? ParseContextWindow(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty(ParametersProperty, out var parameters) && parameters.ValueKind == JsonValueKind.String)
        {
            var match = NumCtxRegex().Match(parameters.GetString() ?? string.Empty);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var numCtx) && numCtx > 0)
            {
                return numCtx;
            }
        }

        if (root.TryGetProperty(ModelInfoProperty, out var modelInfo) && modelInfo.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in modelInfo.EnumerateObject())
            {
                if (property.Name.EndsWith(ContextLengthSuffix, StringComparison.OrdinalIgnoreCase)
                    && property.Value.TryGetInt32(out var contextLength)
                    && contextLength > 0)
                {
                    return contextLength;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Parses the <c>thinking</c> capability metadata from <c>POST /api/show</c> JSON response.
    /// Handles <c>thinking.values</c>, <c>thinking.default</c>, and legacy <c>capabilities: ["thinking"]</c>.
    /// </summary>
    public static ModelThinkingCapability ParseThinkingCapability(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ModelThinkingCapability.None;
        }

        if (TryGetThinkingElement(root, out var thinking))
        {
            if (thinking.TryGetProperty(ValuesProperty, out var values) && values.ValueKind == JsonValueKind.Array)
            {
                var stringLevels = new List<string>();
                var hasBooleanTrue = false;

                foreach (var val in values.EnumerateArray())
                {
                    if (val.ValueKind == JsonValueKind.String)
                    {
                        var s = val.GetString()?.Trim().ToLowerInvariant();
                        if (string.IsNullOrEmpty(s) || s == "false")
                        {
                            continue;
                        }

                        if (s == "true")
                        {
                            hasBooleanTrue = true;
                            continue;
                        }

                        if (!stringLevels.Contains(s))
                        {
                            stringLevels.Add(s);
                        }
                    }
                    else if (val.ValueKind == JsonValueKind.True)
                    {
                        hasBooleanTrue = true;
                    }
                }

                if (stringLevels.Count > 0)
                {
                    string? defaultLevel = null;
                    if (thinking.TryGetProperty(DefaultProperty, out var defaultProp) && defaultProp.ValueKind == JsonValueKind.String)
                    {
                        var def = defaultProp.GetString()?.Trim().ToLowerInvariant();
                        if (!string.IsNullOrEmpty(def) && def != "false" && def != "true")
                        {
                            defaultLevel = def;
                        }
                    }

                    return new ModelThinkingCapability(
                        SupportsThinking: true,
                        StringLevels: stringLevels,
                        SupportsBooleanOnly: false,
                        DefaultLevel: defaultLevel);
                }

                if (hasBooleanTrue)
                {
                    return new ModelThinkingCapability(
                        SupportsThinking: true,
                        StringLevels: Array.Empty<string>(),
                        SupportsBooleanOnly: true,
                        DefaultLevel: null);
                }

                return ModelThinkingCapability.None;
            }
        }

        // Legacy capabilities: ["thinking"] fallback
        if (HasLegacyThinkingCapability(root))
        {
            return new ModelThinkingCapability(
                SupportsThinking: true,
                StringLevels: Array.Empty<string>(),
                SupportsBooleanOnly: true,
                DefaultLevel: null);
        }

        return ModelThinkingCapability.None;
    }

    /// <summary>True when the model declares reasoning/thinking support.</summary>
    public static bool SupportsThinking(JsonElement root) => ParseThinkingCapability(root).SupportsThinking;

    private static bool TryGetThinkingElement(JsonElement root, out JsonElement thinkingElement)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty(ThinkingCapability, out thinkingElement) && thinkingElement.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            if (root.TryGetProperty(ModelInfoProperty, out var modelInfo) && modelInfo.ValueKind == JsonValueKind.Object &&
                modelInfo.TryGetProperty(ThinkingCapability, out thinkingElement) && thinkingElement.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            if (root.TryGetProperty(DetailsProperty, out var details) && details.ValueKind == JsonValueKind.Object &&
                details.TryGetProperty(ThinkingCapability, out thinkingElement) && thinkingElement.ValueKind == JsonValueKind.Object)
            {
                return true;
            }
        }

        thinkingElement = default;
        return false;
    }

    private static bool HasLegacyThinkingCapability(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty(CapabilitiesProperty, out var capabilities) && capabilities.ValueKind == JsonValueKind.Array)
        {
            foreach (var capability in capabilities.EnumerateArray())
            {
                if (capability.ValueKind == JsonValueKind.String
                    && string.Equals(capability.GetString(), ThinkingCapability, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
