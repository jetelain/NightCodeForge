using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using Xunit;

namespace NightCodeForge.Agents.Tests.Inference;

public class OllamaThinkingEffortMapperTests
{
    private static readonly ModelThinkingCapability Qwen38Capability = new(
        SupportsThinking: true,
        StringLevels: ["low", "medium", "xhigh"],
        SupportsBooleanOnly: false,
        DefaultLevel: "medium");

    private static readonly ModelThinkingCapability StandardLevelsCapability = new(
        SupportsThinking: true,
        StringLevels: ["low", "medium", "high"],
        SupportsBooleanOnly: false,
        DefaultLevel: "medium");

    private static readonly ModelThinkingCapability TwoLevelCapability = new(
        SupportsThinking: true,
        StringLevels: ["low", "high"],
        SupportsBooleanOnly: false,
        DefaultLevel: "low");

    private static readonly ModelThinkingCapability BooleanOnlyCapability = new(
        SupportsThinking: true,
        StringLevels: [],
        SupportsBooleanOnly: true,
        DefaultLevel: null);

    private static readonly ModelThinkingCapability NonThinkingCapability = ModelThinkingCapability.None;

    [Theory]
    [InlineData(ThinkingEffort.None, false)]
    [InlineData(ThinkingEffort.Low, "low")]
    [InlineData(ThinkingEffort.Medium, "medium")]
    [InlineData(ThinkingEffort.High, "xhigh")]
    [InlineData(ThinkingEffort.CustomBudget, "xhigh")]
    public void MapThinkingEffort_ForQwen38_ResolvesAccurately(ThinkingEffort effort, object expected)
    {
        var mapped = OllamaThinkingEffortMapper.MapThinkingEffort(effort, Qwen38Capability);
        Assert.Equal(expected, mapped);
    }

    [Fact]
    public void MapThinkingEffort_ForStandardModel_MapsHighToHigh()
    {
        var mapped = OllamaThinkingEffortMapper.MapThinkingEffort(ThinkingEffort.High, StandardLevelsCapability);
        Assert.Equal("high", mapped);
    }

    [Fact]
    public void MapThinkingEffort_ForTwoLevelModelWithDefaultLow_MapsMediumToDefaultLow()
    {
        var mapped = OllamaThinkingEffortMapper.MapThinkingEffort(ThinkingEffort.Medium, TwoLevelCapability);
        Assert.Equal("low", mapped);
    }

    [Theory]
    [InlineData(ThinkingEffort.None, false)]
    [InlineData(ThinkingEffort.Low, true)]
    [InlineData(ThinkingEffort.Medium, true)]
    [InlineData(ThinkingEffort.High, true)]
    [InlineData(ThinkingEffort.CustomBudget, true)]
    public void MapThinkingEffort_ForBooleanOnlyModel_EmitsBooleans(ThinkingEffort effort, bool expected)
    {
        var mapped = OllamaThinkingEffortMapper.MapThinkingEffort(effort, BooleanOnlyCapability);
        Assert.Equal(expected, mapped);
    }

    [Theory]
    [InlineData(ThinkingEffort.None)]
    [InlineData(ThinkingEffort.Low)]
    [InlineData(ThinkingEffort.Medium)]
    [InlineData(ThinkingEffort.High)]
    [InlineData(ThinkingEffort.CustomBudget)]
    public void MapThinkingEffort_ForConfirmedNonThinkingModel_AlwaysReturnsNull(ThinkingEffort effort)
    {
        var mapped = OllamaThinkingEffortMapper.MapThinkingEffort(effort, NonThinkingCapability);
        Assert.Null(mapped);
    }

    [Theory]
    [InlineData("high", "xhigh")]
    [InlineData("xhigh", "xhigh")]
    [InlineData("maximum", "xhigh")]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData(null, "medium")]
    [InlineData("", "medium")]
    public void ResolveReasoningEffort_ForQwen38_MapsCorrectly(string? requested, string expected)
    {
        var resolved = OllamaThinkingEffortMapper.ResolveReasoningEffort(requested, Qwen38Capability);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ResolveReasoningEffort_ForBooleanOnlyModel_ReturnsTrueString()
    {
        var resolved = OllamaThinkingEffortMapper.ResolveReasoningEffort("high", BooleanOnlyCapability);
        Assert.Equal("true", resolved);
    }

    [Fact]
    public void ResolveReasoningEffort_ForNonThinkingModel_ReturnsEmptyString()
    {
        var resolved = OllamaThinkingEffortMapper.ResolveReasoningEffort("high", NonThinkingCapability);
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void ResolveReasoningEffort_WhenUnsupportedLevel_PicksClosestCanonicalRank()
    {
        // "minimal" is rank 0. Supported in Qwen3.8 are low (1), medium (2), xhigh (4). Closest rank is "low".
        var resolved = OllamaThinkingEffortMapper.ResolveReasoningEffort("minimal", Qwen38Capability);
        Assert.Equal("low", resolved);
    }

    [Fact]
    public void ResolveReasoningEffort_WhenHighAndXhighBothSupported_MapsHighToXhighAsHighestRank()
    {
        var fullRangeCapability = new ModelThinkingCapability(
            SupportsThinking: true,
            StringLevels: ["minimal", "low", "medium", "high", "xhigh"],
            SupportsBooleanOnly: false,
            DefaultLevel: "medium");

        Assert.Equal("xhigh", OllamaThinkingEffortMapper.MapThinkingEffort(ThinkingEffort.High, fullRangeCapability));
        Assert.Equal("xhigh", OllamaThinkingEffortMapper.ResolveReasoningEffort("high", fullRangeCapability));
    }

    [Fact]
    public void ResolveReasoningEffort_WhenRequestedEmptyAndNoDefaultLevel_FallsBackToMediumOrMedian()
    {
        var twoLevelNoDefault = new ModelThinkingCapability(
            SupportsThinking: true,
            StringLevels: ["low", "high"],
            SupportsBooleanOnly: false,
            DefaultLevel: null);

        // Median of ["low", "high"] is index 0 -> "low"
        Assert.Equal("low", OllamaThinkingEffortMapper.ResolveReasoningEffort(null, twoLevelNoDefault));
        Assert.Equal("low", OllamaThinkingEffortMapper.ResolveReasoningEffort("   ", twoLevelNoDefault));
    }
}

