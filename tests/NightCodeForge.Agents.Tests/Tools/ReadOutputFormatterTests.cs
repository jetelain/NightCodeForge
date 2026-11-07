using System.Text;
using NightCodeForge.Agents.Tools;

namespace NightCodeForge.Agents.Tests;

public class ReadOutputFormatterTests
{
    [Fact]
    public void FormatCapped_WhenSelectedLinesNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ReadOutputFormatter.FormatCapped(null!, 1, 10, 1000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-500)]
    public void FormatCapped_WhenMaxBytesZeroOrNegative_ThrowsArgumentOutOfRangeException(int invalidMaxBytes)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReadOutputFormatter.FormatCapped(new[] { "line" }, 1, 1, invalidMaxBytes));
        Assert.Equal("maxBytes", ex.ParamName);
    }

    [Fact]
    public void FormatCapped_WhenSelectedLinesEmpty_ReturnsEmptyString()
    {
        var result = ReadOutputFormatter.FormatCapped(Array.Empty<string>(), 1, 0, 1000);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FormatCapped_WhenAllLinesFitWithinBudget_ReturnsAllFormattedLinesWithoutFooter()
    {
        var lines = new[] { "first line", "second line", "third line" };
        var result = ReadOutputFormatter.FormatCapped(lines, startLine: 1, totalFileLines: 3, maxBytes: 5000);

        Assert.Contains("    1 | first line", result);
        Assert.Contains("    2 | second line", result);
        Assert.Contains("    3 | third line", result);
        Assert.DoesNotContain("[Output truncated", result);
    }

    [Fact]
    public void FormatCapped_WhenExactByteBoundaryMatchesCap_ReturnsAllLinesWithoutTruncation()
    {
        // Line 1: $"{1,5} | {line}\n"
        // Prefix: "    1 | " (8 bytes) + text (21 bytes) + '\n' (1 byte) = 30 bytes
        // Line 2: $"{2,5} | {line}\n"
        // Prefix: "    2 | " (8 bytes) + text (21 bytes) + '\n' (1 byte) = 30 bytes
        // Total = 60 bytes exactly
        var line1 = new string('x', 21);
        var line2 = new string('y', 21);
        const int exactCap = 60;

        var result = ReadOutputFormatter.FormatCapped(new[] { line1, line2 }, startLine: 1, totalFileLines: 2, maxBytes: exactCap);

        Assert.DoesNotContain("[Output truncated", result);
        Assert.Contains(line1, result);
        Assert.Contains(line2, result);
        // TrimEnd() trims the trailing '\n' (1 byte), so result byte count is exactCap - 1
        Assert.Equal(exactCap - 1, Encoding.UTF8.GetByteCount(result));
    }

    [Fact]
    public void FormatCapped_WhenMultiLineExceedsBudget_TruncatesCleanlyAtLineBoundaryAndAppendsPagingFooter()
    {
        var lines = Enumerable.Range(1, 50).Select(i => $"Content of line #{i:D3}").ToArray();
        const int capBytes = 400;

        var result = ReadOutputFormatter.FormatCapped(lines, startLine: 1, totalFileLines: 50, maxBytes: capBytes);

        Assert.True(Encoding.UTF8.GetByteCount(result) <= capBytes);
        Assert.Contains("[Output truncated: reached", result);
        Assert.Contains("of 50. Use 'offset'", result);

        // Verify that included lines are complete and not cut off mid-line
        Assert.Contains("    1 | Content of line #001", result);
    }

    [Fact]
    public void FormatCapped_WhenFirstLineExceedsLineBudget_TruncatesFirstLineContentWithTruncatedMarkerAndAppendsLineExceededFooter()
    {
        var hugeLine = "const config = {" + new string('a', 1500) + "};";
        const int capBytes = 350;

        var result = ReadOutputFormatter.FormatCapped(new[] { hugeLine }, startLine: 1, totalFileLines: 1, maxBytes: capBytes);

        Assert.True(Encoding.UTF8.GetByteCount(result) <= capBytes);
        Assert.Contains(ReadOutputFormatter.TruncatedMarker, result);
        Assert.Contains("[Output truncated: line 1 exceeded", result);
        Assert.Contains("total file lines: 1", result);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(250)]
    [InlineData(500)]
    [InlineData(1024)]
    [InlineData(10240)]
    public void FormatCapped_GuaranteeOutputUtf8BytesNeverExceedsMaxBytes(int maxBytes)
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"Line {i}: " + new string('z', 100)).ToArray();
        var result = ReadOutputFormatter.FormatCapped(lines, startLine: 1, totalFileLines: 100, maxBytes: maxBytes);

        Assert.True(Encoding.UTF8.GetByteCount(result) <= maxBytes,
            $"Expected UTF-8 byte count <= {maxBytes}, but got {Encoding.UTF8.GetByteCount(result)}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TruncateStringToUtf8ByteCount_WhenTextNullOrEmpty_ReturnsEmptyString(string? input)
    {
        Assert.Equal(string.Empty, ReadOutputFormatter.TruncateStringToUtf8ByteCount(input!, 100));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void TruncateStringToUtf8ByteCount_WhenMaxBytesZeroOrNegative_ReturnsEmptyString(int maxBytes)
    {
        Assert.Equal(string.Empty, ReadOutputFormatter.TruncateStringToUtf8ByteCount("Hello World", maxBytes));
    }

    [Fact]
    public void TruncateStringToUtf8ByteCount_WhenTextShorterThanMaxBytes_ReturnsOriginalText()
    {
        const string text = "Short text";
        Assert.Equal(text, ReadOutputFormatter.TruncateStringToUtf8ByteCount(text, 50));
    }

    [Fact]
    public void TruncateStringToUtf8ByteCount_WhenTruncatingBetweenSurrogates_DropsHighSurrogateToAvoidCorruptPair()
    {
        // "abc" (3 bytes) + "🚀" (\uD83D\uDE80, 4 bytes) = 7 bytes total
        const string text = "abc\uD83D\uDE80";

        // Budget of 6 bytes cannot fit 7-byte string; dropping low surrogate leaves high surrogate (\uD83D) at index 3.
        // It must drop the high surrogate too and return "abc" (3 bytes).
        var truncated = ReadOutputFormatter.TruncateStringToUtf8ByteCount(text, 6);
        Assert.Equal("abc", truncated);

        // Budget of 7 bytes fits the entire string
        var full = ReadOutputFormatter.TruncateStringToUtf8ByteCount(text, 7);
        Assert.Equal(text, full);
    }

    [Fact]
    public void TruncateStringToUtf8ByteCount_WhenTruncatingMultibyteChars_TruncatesAccurately()
    {
        // "café" -> 'c' (1), 'a' (1), 'f' (1), 'é' (2) = 5 bytes total
        const string text = "café";

        // 4 bytes can fit "caf" (3 bytes), but 'é' needs 2 bytes (total 5 bytes)
        var at4 = ReadOutputFormatter.TruncateStringToUtf8ByteCount(text, 4);
        Assert.Equal("caf", at4);

        // 5 bytes fits "café"
        var at5 = ReadOutputFormatter.TruncateStringToUtf8ByteCount(text, 5);
        Assert.Equal("café", at5);
    }

    [Fact]
    public void BuildLineExceededFooter_FormatsCultureInvariantlyWithMaxKb()
    {
        var footer = ReadOutputFormatter.BuildLineExceededFooter(
            lineNum: 1,
            lineLength: 3500000,
            totalLines: 1,
            maxBytes: 100 * 1024);

        Assert.Contains("line 1 exceeded 100 KB limit", footer);
        Assert.Contains("3,500,000 characters", footer);
        Assert.Contains("total file lines: 1", footer);
    }

    [Fact]
    public void BuildPagingFooter_FormatsCultureInvariantlyWithOffsetAndTotalLines()
    {
        var footer = ReadOutputFormatter.BuildPagingFooter(
            lastIncludedLine: 250,
            totalLines: 1000,
            maxBytes: 100 * 1024);

        Assert.Contains("reached 100 KB limit at line 250 of 1000", footer);
        Assert.Contains("Use 'offset' (e.g. offset: 251) and 'limit'", footer);
    }
}

