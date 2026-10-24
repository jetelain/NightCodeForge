using NightCodeForge.Core.Domain;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class TaskPriorityTests
{
    [Theory]
    [InlineData(TaskPriority.Critical, 1)]
    [InlineData(TaskPriority.High, 2)]
    [InlineData(TaskPriority.Normal, 3)]
    [InlineData(TaskPriority.Low, 4)]
    [InlineData(TaskPriority.Trivial, 5)]
    [InlineData("1", 1)]
    [InlineData("5", 5)]
    [InlineData("P1", 1)]
    [InlineData("P2", 2)]
    [InlineData("P3", 3)]
    [InlineData("P4", 4)]
    [InlineData("P5", 5)]
    [InlineData("p1", 1)]
    [InlineData("Critical", 1)]
    [InlineData("Urgent", 1)]
    [InlineData("critical", 1)]
    [InlineData("urgent", 1)]
    [InlineData("High", 2)]
    [InlineData("high", 2)]
    [InlineData("Normal", 3)]
    [InlineData("normal", 3)]
    [InlineData("Medium", 3)]
    [InlineData("medium", 3)]
    [InlineData("Low", 4)]
    [InlineData("low", 4)]
    [InlineData("Lowest", 5)]
    [InlineData("lowest", 5)]
    [InlineData("Trivial", 5)]
    [InlineData("trivial", 5)]
    public void TryParse_ValidInputs_ReturnsTrueAndCorrectPriority(object input, int expected)
    {
        var success = TaskPriority.TryParse(input, out var priority);
        Assert.True(success);
        Assert.Equal(expected, priority);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unknown")]
    [InlineData("P99")]
    [InlineData("NotAPriority")]
    public void TryParse_InvalidInputs_ReturnsFalse(object? input)
    {
        var success = TaskPriority.TryParse(input, out var priority);
        Assert.False(success);
        Assert.Equal(0, priority);
    }

    [Theory]
    [InlineData("Critical", 1)]
    [InlineData("P2", 2)]
    [InlineData(null, 3)]
    [InlineData("invalid", 3)]
    [InlineData(4, 4)]
    public void ParseOrDefault_FallbackBehavior(object? input, int expected)
    {
        var result = TaskPriority.ParseOrDefault(input, defaultPriority: TaskPriority.Normal);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Critical / Urgent", 1)]
    [InlineData("Critical/Urgent", 1)]
    [InlineData("Lowest / Trivial", 5)]
    [InlineData("Lowest/Trivial", 5)]
    [InlineData("Priority 1", 1)]
    [InlineData("Priority 2", 2)]
    [InlineData("Priority P1", 1)]
    [InlineData("Priority P5", 5)]
    [InlineData("priority 4", 4)]
    public void TryParse_CompoundLabelsAndPrefixes_ReturnsCorrectPriority(string input, int expected)
    {
        var success = TaskPriority.TryParse(input, out var priority);
        Assert.True(success);
        Assert.Equal(expected, priority);
    }

    [Fact]
    public void TryParse_RoundTripsAllGetLabelValues()
    {
        foreach (var p in TaskPriority.All)
        {
            var label = TaskPriority.GetLabel(p);
            var success = TaskPriority.TryParse(label, out var parsed);
            Assert.True(success, $"Failed to round-trip GetLabel({p}) = '{label}'");
            Assert.Equal(p, parsed);
        }
    }

    [Fact]
    public void TryParse_JsonElement_SupportsNumbersAndStrings()
    {
        using var docNumber = System.Text.Json.JsonDocument.Parse("1");
        Assert.True(TaskPriority.TryParse(docNumber.RootElement, out var p1));
        Assert.Equal(TaskPriority.Critical, p1);

        using var docString = System.Text.Json.JsonDocument.Parse("\"Critical / Urgent\"");
        Assert.True(TaskPriority.TryParse(docString.RootElement, out var p2));
        Assert.Equal(TaskPriority.Critical, p2);

        using var docBool = System.Text.Json.JsonDocument.Parse("true");
        Assert.False(TaskPriority.TryParse(docBool.RootElement, out var _));
    }

    [Fact]
    public void LabelsAndDescriptions_AreConsistent()
    {
        Assert.Equal("P1", TaskPriority.GetShortLabel(TaskPriority.Critical));
        Assert.Equal("Critical / Urgent", TaskPriority.GetLabel(TaskPriority.Critical));
        Assert.Equal("P3", TaskPriority.GetShortLabel(TaskPriority.Normal));
        Assert.Equal("Normal", TaskPriority.GetLabel(TaskPriority.Normal));
        Assert.Equal("#ef4444", TaskPriority.GetBadgeColor(TaskPriority.Critical));
        Assert.Equal("#3b82f6", TaskPriority.GetBadgeColor(TaskPriority.Normal));
    }
}

