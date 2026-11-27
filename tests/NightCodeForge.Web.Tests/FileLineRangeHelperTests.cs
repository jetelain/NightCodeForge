using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class FileLineRangeHelperTests
{
    [Theory]
    [InlineData("#L42", 42, 42)]
    [InlineData("#l42", 42, 42)]
    [InlineData("http://localhost/w/1/files?ref=main&path=a.cs#L12-L34", 12, 34)]
    [InlineData("http://localhost/w/1/files#l12-34", 12, 34)]
    [InlineData("#L58-L42", 42, 58)]
    public void TryParseFragment_ValidAnchors_ReturnsNormalizedRange(string input, int expectedStart, int expectedEnd)
    {
        Assert.True(FileLineRangeHelper.TryParseFragment(input, out var range));
        Assert.NotNull(range);
        Assert.Equal(expectedStart, range!.StartLine);
        Assert.Equal(expectedEnd, range.EndLine);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://localhost/w/1/files")]
    [InlineData("#L0")]
    [InlineData("#L-5")]
    [InlineData("#overview")]
    public void TryParseFragment_InvalidOrNonLineAnchors_ReturnsFalse(string? input)
    {
        Assert.False(FileLineRangeHelper.TryParseFragment(input, out var range));
        Assert.Null(range);
    }

    [Theory]
    [InlineData("42", 42, 42)]
    [InlineData("42-58", 42, 58)]
    [InlineData("L15-L10", 10, 15)]
    public void TryParseQueryValue_ValidValues_ReturnsRange(string input, int expectedStart, int expectedEnd)
    {
        Assert.True(FileLineRangeHelper.TryParseQueryValue(input, out var range));
        Assert.NotNull(range);
        Assert.Equal(expectedStart, range!.StartLine);
        Assert.Equal(expectedEnd, range.EndLine);
    }

    [Fact]
    public void ResolveEffectiveRange_PrefersFragmentAndClampsToTotalLines()
    {
        var fromFragment = FileLineRangeHelper.ResolveEffectiveRange(
            "http://localhost/w/1/files?lines=1-2#L8-L999",
            "1-2",
            totalLines: 20);

        Assert.Equal(new FileLineRange(8, 20), fromFragment);

        var fromQuery = FileLineRangeHelper.ResolveEffectiveRange(
            "http://localhost/w/1/files?lines=5-12",
            "5-12",
            totalLines: 10);

        Assert.Equal(new FileLineRange(5, 10), fromQuery);
        Assert.Null(FileLineRangeHelper.ResolveEffectiveRange("http://localhost/w/1/files#L1", null, totalLines: 0));
    }

    [Fact]
    public void ComputeClickedRange_PlainAndShiftClick_ProducesExpectedSelection()
    {
        var first = FileLineRangeHelper.ComputeClickedRange(null, 10, isShiftKey: false);
        Assert.Equal(new FileLineRange(10, 10), first);

        var expandedDown = FileLineRangeHelper.ComputeClickedRange(first, 25, isShiftKey: true);
        Assert.Equal(new FileLineRange(10, 25), expandedDown);

        var expandedUp = FileLineRangeHelper.ComputeClickedRange(first, 4, isShiftKey: true);
        Assert.Equal(new FileLineRange(4, 10), expandedUp);
    }

    [Fact]
    public void FormatFragment_FormatsSingleAndRangeCorrectly()
    {
        Assert.Equal("#L7", FileLineRangeHelper.FormatFragment(new FileLineRange(7, 7)));
        Assert.Equal("#L7-L19", FileLineRangeHelper.FormatFragment(new FileLineRange(7, 19)));
    }
}
