namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Per-model capability overrides (reasoning effort levels, context window) resolved by case-insensitive
/// substring match of the model identifier (e.g. key "qwen3.8" matches "qwen3.8:35b").
/// </summary>
public class ModelCapabilityProfile
{
    /// <summary>
    /// Reasoning effort levels natively accepted by the model (e.g. low, medium, xhigh).
    /// When empty, any requested level is forwarded unchanged.
    /// </summary>
    public List<string> SupportedEfforts { get; set; } = new();

    /// <summary>
    /// Explicit remapping of requested effort levels onto supported ones (e.g. high -> xhigh).
    /// Applied before the nearest-level fallback.
    /// </summary>
    public Dictionary<string, string> EffortMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional default effort for this model, used when no explicit effort is requested.
    /// </summary>
    public string? DefaultEffort { get; set; }

    /// <summary>
    /// Optional context window (tokens) overriding <see cref="LlmProviderOptions.ContextWindowTokens"/>.
    /// </summary>
    public int? ContextWindowTokens { get; set; }

    /// <summary>
    /// Optional output token budget overriding <see cref="LlmProviderOptions.MaxOutputTokens"/>.
    /// </summary>
    public int? MaxOutputTokens { get; set; }
}
