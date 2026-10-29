using System.Text.Json;
using System.Text.RegularExpressions;

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
    private const string CapabilitiesProperty = "capabilities";
    private const string ContextLengthSuffix = ".context_length";

    [GeneratedRegex(@"(?:^|\n)\s*num_ctx\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NumCtxRegex();

    /// <summary>
    /// Modelfile <c>num_ctx</c> when set, otherwise the architecture <c>*.context_length</c>.
    /// </summary>
    public static int? ParseContextWindow(JsonElement root)
    {
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

    /// <summary>True when the model declares the <c>thinking</c> capability.</summary>
    public static bool SupportsThinking(JsonElement root)
    {
        if (!root.TryGetProperty(CapabilitiesProperty, out var capabilities) || capabilities.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var capability in capabilities.EnumerateArray())
        {
            if (capability.ValueKind == JsonValueKind.String
                && string.Equals(capability.GetString(), ThinkingCapability, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
