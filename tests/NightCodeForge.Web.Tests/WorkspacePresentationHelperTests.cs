using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class WorkspacePresentationHelperTests
{
    [Theory]
    [InlineData("🚀", "🚀")]
    [InlineData("🛡️", "🛡️")]
    [InlineData("🎨", "🎨")]
    [InlineData(" ⚡ ", "⚡")]
    public void ResolveEmoji_WhenProvided_ReturnsTrimmedEmoji(string input, string expected)
    {
        var result = WorkspacePresentationHelper.ResolveEmoji(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveEmoji_WhenNullOrWhitespace_ReturnsDefaultFolderEmoji(string? input)
    {
        var result = WorkspacePresentationHelper.ResolveEmoji(input);
        Assert.Equal(WorkspacePresentationHelper.DefaultEmoji, result);
    }

    [Theory]
    [InlineData("#abc", true)]
    [InlineData("#ABC", true)]
    [InlineData("#123", true)]
    [InlineData("#6366f1", true)]
    [InlineData("#6366F1", true)]
    [InlineData("#10b981", true)]
    [InlineData("#12345678", true)]
    [InlineData("#AABBCCDD", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("6366f1", false)]
    [InlineData("#12", false)]
    [InlineData("#1234", false)]
    [InlineData("#12345", false)]
    [InlineData("#1234567", false)]
    [InlineData("#123456789", false)]
    [InlineData("#gggggg", false)]
    [InlineData("red", false)]
    public void IsValidHexColor_ValidatesCorrectly(string? input, bool expected)
    {
        var result = WorkspacePresentationHelper.IsValidHexColor(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("#6366f1", "#6366f1")]
    [InlineData(" #10b981 ", "#10b981")]
    [InlineData("#FFF", "#FFF")]
    [InlineData("#AABBCCDD", "#AABBCCDD")]
    public void ResolveColor_WhenValidHex_ReturnsColorTrimmed(string input, string expected)
    {
        var result = WorkspacePresentationHelper.ResolveColor(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, "var(--primary)")]
    [InlineData("", "var(--primary)")]
    [InlineData("not-a-color", "var(--primary)")]
    [InlineData("#invalid", "var(--primary)")]
    public void ResolveColor_WhenInvalidOrNull_ReturnsFallback(string? input, string expected)
    {
        var result = WorkspacePresentationHelper.ResolveColor(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolveColor_WithCustomFallback_ReturnsCustomFallbackOnInvalid()
    {
        var result = WorkspacePresentationHelper.ResolveColor("invalid", fallback: "#ffffff");
        Assert.Equal("#ffffff", result);
    }

    [Fact]
    public void ColorPresets_ContainsExpectedCanonicalPalette()
    {
        var presets = WorkspacePresentationHelper.ColorPresets;
        Assert.NotEmpty(presets);
        Assert.Contains(presets, p => p.Name == "Indigo" && p.HexCode == "#6366f1");
        Assert.Contains(presets, p => p.Name == "Sky Blue" && p.HexCode == "#0ea5e9");
        Assert.Contains(presets, p => p.Name == "Emerald Green" && p.HexCode == "#10b981");
        Assert.Contains(presets, p => p.Name == "Amber Orange" && p.HexCode == "#f59e0b");
        Assert.Contains(presets, p => p.Name == "Rose Red" && p.HexCode == "#f43f5e");
        Assert.Contains(presets, p => p.Name == "Violet Purple" && p.HexCode == "#8b5cf6");
        Assert.Contains(presets, p => p.Name == "Teal" && p.HexCode == "#14b8a6");
        Assert.Contains(presets, p => p.Name == "Slate Gray" && p.HexCode == "#64748b");
    }

    [Fact]
    public void EmojiPresets_ContainsExpectedDeveloperPresets()
    {
        var presets = WorkspacePresentationHelper.EmojiPresets;
        Assert.NotEmpty(presets);
        Assert.Contains(presets, p => p.Emoji == "🚀");
        Assert.Contains(presets, p => p.Emoji == "🛡️");
        Assert.Contains(presets, p => p.Emoji == "🎨");
        Assert.Contains(presets, p => p.Emoji == "🌐");
        Assert.Contains(presets, p => p.Emoji == "⚡");
        Assert.Contains(presets, p => p.Emoji == "🧠");
        Assert.Contains(presets, p => p.Emoji == "📦");
        Assert.Contains(presets, p => p.Emoji == "🏢");
        Assert.Contains(presets, p => p.Emoji == "🔧");
        Assert.Contains(presets, p => p.Emoji == "🔬");
    }
}

