namespace NightCodeForge.Web.Services;

/// <summary>
/// Preset context window token sizes for quick one-click manual configuration.
/// </summary>
public record ContextTokenPreset(string Label, int Tokens);

public static class ContextTokenPresetCatalog
{
    public static readonly IReadOnlyList<ContextTokenPreset> Presets =
    [
        new("32k", 32768),
        new("64k", 65536),
        new("128k", 131072),
        new("200k", 200000)
    ];
}
