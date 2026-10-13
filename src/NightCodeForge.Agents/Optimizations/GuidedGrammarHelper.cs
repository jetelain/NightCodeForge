using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Optimizations;

/// <summary>
/// Helper for guided grammar decoding and structured JSON schema outputs.
/// </summary>
public static class GuidedGrammarHelper
{
    public static void ApplyGrammarConstraints(ChatOptions options, LocalOptimizationFlags? flags, JsonElement? jsonSchema = null)
    {
        if (flags != null && !flags.GuidedGrammarDecoding)
        {
            return;
        }

        if (jsonSchema.HasValue)
        {
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties["format"] = jsonSchema.Value;
        }
    }
}
