using System.Text.RegularExpressions;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Immutable preset representing a brand color palette option.
/// </summary>
public record ColorPreset(string Name, string HexCode);

/// <summary>
/// Immutable preset representing a recommended workspace emoji icon.
/// </summary>
public record EmojiPreset(string Emoji, string Label);

/// <summary>
/// Domain-driven presentation helper providing visual identity defaults, validation, and preset palettes
/// for workspaces across navigation, cards, and settings.
/// </summary>
public static partial class WorkspacePresentationHelper
{
    public const string DefaultEmoji = "📦";
    public const string DefaultColor = "#6366f1";
    public const string DefaultColorFallbackCss = "var(--primary)";

    [GeneratedRegex("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColorRegex();

    public static readonly IReadOnlyList<ColorPreset> ColorPresets = new List<ColorPreset>
    {
        new("Indigo", "#6366f1"),
        new("Sky Blue", "#0ea5e9"),
        new("Emerald Green", "#10b981"),
        new("Amber Orange", "#f59e0b"),
        new("Rose Red", "#f43f5e"),
        new("Violet Purple", "#8b5cf6"),
        new("Teal", "#14b8a6"),
        new("Slate Gray", "#64748b")
    }.AsReadOnly();

    public static readonly IReadOnlyList<EmojiPreset> EmojiPresets = new List<EmojiPreset>
    {
        new("🚀", "Speed / Launch / Router"),
        new("🛡️", "Security / Core / Forge"),
        new("🎨", "Creative / Media / Frontend"),
        new("🌐", "Web / Gateway / API"),
        new("⚡", "Performance / Microservice"),
        new("🧠", "AI / Inference / Research"),
        new("📦", "Library / Package / Tools"),
        new("🏢", "Enterprise / Monorepo"),
        new("🔧", "CLI / DevOps / Infrastructure"),
        new("🔬", "Testing / Experiments / Benchmark")
    }.AsReadOnly();

    /// <summary>
    /// Resolves the workspace emoji, falling back to a folder icon if not set or whitespace.
    /// </summary>
    public static string ResolveEmoji(string? emoji)
    {
        return string.IsNullOrWhiteSpace(emoji) ? DefaultEmoji : emoji.Trim();
    }

    /// <summary>
    /// Validates whether a given string is a valid 3, 6, or 8 digit hex color code (#RGB, #RRGGBB, #RRGGBBAA).
    /// </summary>
    public static bool IsValidHexColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return false;
        }

        return HexColorRegex().IsMatch(color.Trim());
    }

    /// <summary>
    /// Resolves a workspace color code to a valid hex string or falls back to the specified CSS token.
    /// </summary>
    public static string ResolveColor(string? colorCode, string fallback = DefaultColorFallbackCss)
    {
        if (IsValidHexColor(colorCode))
        {
            return colorCode!.Trim();
        }

        return fallback;
    }
}

