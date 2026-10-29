using System.Globalization;
using System.Text.Json;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

/// <summary>
/// Parser for OpenAI-compatible <c>/api/v1/models</c> JSON payloads (e.g. OpenRouter, LiteLLM, vLLM, LM Studio, OpenAI).
/// Robustly extracts model identifiers, display names, context lengths, pricing, and architectural traits.
/// </summary>
public static class OpenAiModelsResponseParser
{

    public static IReadOnlyList<AvailableModelInfo> Parse(string jsonContent, int? defaultContextTokens = null)
    {
        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            return Array.Empty<AvailableModelInfo>();
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            JsonElement modelsArray;
            if (root.ValueKind == JsonValueKind.Array)
            {
                modelsArray = root;
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
            {
                modelsArray = dataProp;
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var modelsProp) && modelsProp.ValueKind == JsonValueKind.Array)
            {
                modelsArray = modelsProp;
            }
            else
            {
                return Array.Empty<AvailableModelInfo>();
            }

            var results = new List<AvailableModelInfo>();
            foreach (var item in modelsArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // Model ID: extract "id", fallback to "name"
                string? modelId = null;
                if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                {
                    modelId = idProp.GetString();
                }
                else if (item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                {
                    modelId = nameProp.GetString();
                }

                if (string.IsNullOrWhiteSpace(modelId))
                {
                    continue;
                }

                var trimmedId = modelId.Trim();

                // Display name: check "display_name", fallback to "name", fallback to model ID
                string displayName = trimmedId;
                if (item.TryGetProperty("display_name", out var dispProp) && dispProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(dispProp.GetString()))
                {
                    displayName = dispProp.GetString()!.Trim();
                }
                else if (item.TryGetProperty("name", out var dNameProp) && dNameProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(dNameProp.GetString()))
                {
                    displayName = dNameProp.GetString()!.Trim();
                }

                // Context window: look for "context_length", "max_model_len", "context_window"
                int? contextLength = null;
                if (TryGetIntProperty(item, "context_length", out var cl) && cl > 0)
                {
                    contextLength = cl;
                }
                else if (TryGetIntProperty(item, "max_model_len", out var mml) && mml > 0)
                {
                    contextLength = mml;
                }
                else if (TryGetIntProperty(item, "context_window", out var cw) && cw > 0)
                {
                    contextLength = cw;
                }

                var effectiveContext = contextLength ?? (defaultContextTokens is > 0 ? defaultContextTokens : null);

                // Pricing: check "pricing" object
                decimal? promptCost = null;
                decimal? completionCost = null;
                if (item.TryGetProperty("pricing", out var pricingProp) && pricingProp.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetDecimalProperty(pricingProp, "prompt", out var pCost))
                    {
                        promptCost = NormalizeCostPerMillion(pCost);
                    }

                    if (TryGetDecimalProperty(pricingProp, "completion", out var cCost))
                    {
                        completionCost = NormalizeCostPerMillion(cCost);
                    }
                }

                // Reasoning support heuristic: check model ID and flags
                var supportsReasoning = CheckSupportsReasoning(item, trimmedId);

                // MoE heuristic: check model ID and architecture
                var isMoe = CheckIsMoe(item, trimmedId);

                results.Add(new AvailableModelInfo(
                    ModelId: trimmedId,
                    DisplayName: displayName,
                    ContextWindowTokens: effectiveContext,
                    SizeBytes: null,
                    ProviderType: LlmProviderType.OpenAiCompatible,
                    SupportsReasoning: supportsReasoning,
                    PromptCostPerMillion: promptCost,
                    CompletionCostPerMillion: completionCost,
                    IsMoE: isMoe));
            }

            return results;
        }
        catch (JsonException)
        {
            return Array.Empty<AvailableModelInfo>();
        }
    }

    private static bool TryGetIntProperty(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number)
            {
                if (prop.TryGetInt32(out value))
                {
                    return true;
                }
                if (prop.TryGetDouble(out var d) && d > 0 && d <= int.MaxValue)
                {
                    value = (int)d;
                    return true;
                }
            }
            if (prop.ValueKind == JsonValueKind.String)
            {
                if (int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    return true;
                }
                if (double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sd) && sd > 0 && sd <= int.MaxValue)
                {
                    value = (int)sd;
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryGetDecimalProperty(JsonElement element, string propertyName, out decimal value)
    {
        value = 0;
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number)
            {
                if (prop.TryGetDecimal(out value))
                {
                    return true;
                }
                if (prop.TryGetDouble(out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
                {
                    value = (decimal)d;
                    return true;
                }
            }
            if (prop.ValueKind == JsonValueKind.String)
            {
                if (decimal.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    return true;
                }
                if (double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sd) && !double.IsNaN(sd) && !double.IsInfinity(sd))
                {
                    value = (decimal)sd;
                    return true;
                }
            }
        }
        return false;
    }

    private static decimal? NormalizeCostPerMillion(decimal rawCost)
    {
        if (rawCost < 0) return null;
        if (rawCost == 0) return 0m;

        // If rawCost is per-token (typically < $0.01 per token, e.g. 0.0000015), scale to per-million
        if (rawCost < 0.01m)
        {
            return rawCost * 1_000_000m;
        }

        // Already expressed per million or very high cost
        return rawCost;
    }

    private static bool CheckSupportsReasoning(JsonElement item, string modelId)
    {
        if (item.TryGetProperty("supports_reasoning", out var sr) && sr.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (item.TryGetProperty("supported_parameters", out var sp) && sp.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in sp.EnumerateArray())
            {
                if (p.ValueKind == JsonValueKind.String)
                {
                    var paramStr = p.GetString()?.ToLowerInvariant();
                    if (paramStr == "reasoning_effort" || paramStr == "thinking")
                    {
                        return true;
                    }
                }
            }
        }

        var lowerId = modelId.ToLowerInvariant();
        if (lowerId.Contains("reasoning") || lowerId.Contains("thinking") || lowerId.Contains("qwq"))
        {
            return true;
        }

        if (IsTokenMatch(lowerId, "r1") || IsTokenMatch(lowerId, "o1") || IsTokenMatch(lowerId, "o3"))
        {
            return true;
        }

        if (item.TryGetProperty("architecture", out var arch) && arch.ValueKind == JsonValueKind.Object)
        {
            if (arch.TryGetProperty("instruct_type", out var it) && it.ValueKind == JsonValueKind.String)
            {
                var val = it.GetString()?.ToLowerInvariant() ?? string.Empty;
                if (val.Contains("reasoning") || val.Contains("thinking") || val.Contains("qwq") ||
                    IsTokenMatch(val, "r1") || IsTokenMatch(val, "o1") || IsTokenMatch(val, "o3"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool CheckIsMoe(JsonElement item, string modelId)
    {
        var lowerId = modelId.ToLowerInvariant();
        if (lowerId.Contains("mixtral") || lowerId.Contains("-a3b") || lowerId.Contains("dbrx"))
        {
            return true;
        }

        if (IsTokenMatch(lowerId, "moe"))
        {
            return true;
        }

        if (item.TryGetProperty("architecture", out var arch) && arch.ValueKind == JsonValueKind.Object)
        {
            if (TryGetIntProperty(arch, "expert_count", out var ec) && ec > 1)
            {
                return true;
            }
            if (TryGetIntProperty(arch, "num_experts", out var ne) && ne > 1)
            {
                return true;
            }

            if (arch.TryGetProperty("instruct_type", out var it) && it.ValueKind == JsonValueKind.String)
            {
                var val = it.GetString()?.ToLowerInvariant() ?? string.Empty;
                if (val.Contains("mixtral") || IsTokenMatch(val, "moe"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsTokenMatch(string text, string token)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token)) return false;

        int index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            bool startOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            int afterIndex = index + token.Length;
            bool endOk = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);

            if (startOk && endOk)
            {
                return true;
            }

            index += token.Length;
        }
        return false;
    }
}

