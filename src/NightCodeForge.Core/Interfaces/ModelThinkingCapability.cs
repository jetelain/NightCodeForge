namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Reasoning/thinking capability metadata discovered from Ollama or OllamaRouter via <c>POST /api/show</c>.
/// </summary>
/// <param name="SupportsThinking">True if the model supports any thinking/reasoning mode.</param>
/// <param name="StringLevels">Supported string reasoning levels (e.g. ["low", "medium", "xhigh"]).</param>
/// <param name="SupportsBooleanOnly">True if the model accepts only boolean think toggles ([false, true] or legacy thinking capability).</param>
/// <param name="DefaultLevel">Default thinking level reported by the model, or null if none/boolean.</param>
public record ModelThinkingCapability(
    bool SupportsThinking,
    IReadOnlyList<string> StringLevels,
    bool SupportsBooleanOnly,
    string? DefaultLevel)
{
    public static ModelThinkingCapability None { get; } = new(false, Array.Empty<string>(), false, null);

    /// <summary>Formatted badge text for UI presentation (keeps presentation formatting out of Razor @code blocks).</summary>
    public string FormattedBadge => !SupportsThinking
        ? string.Empty
        : (StringLevels.Count > 0
            ? $"🧠 Reasoning: {string.Join(", ", StringLevels)}"
            : "🧠 Reasoning: boolean (on/off)");
}

