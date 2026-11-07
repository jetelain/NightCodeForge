using System.Text;
using System.Text.RegularExpressions;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tests;

public class CanonicalToolsReadCapTests : IDisposable
{
    private readonly string _tempDir;

    public CanonicalToolsReadCapTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nc_readcap_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Fact]
    public void WorkflowDefaults_MaxReadFileBytes_Is100KB()
    {
        Assert.Equal(100 * 1024, WorkflowDefaults.MaxReadFileBytes);
    }

    [Fact]
    public void CanonicalTools_DefaultMaxReadFileBytes_MatchesWorkflowDefaults()
    {
        var tools = TestCanonicalToolsBuilder.Create(_tempDir, pathResolver: TestPathResolver.Create());
        Assert.Equal(WorkflowDefaults.MaxReadFileBytes, tools.MaxReadFileBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void CanonicalTools_Constructor_WhenMaxReadFileBytesIsZeroOrNegative_ThrowsArgumentOutOfRangeException(int invalidBytes)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            TestCanonicalToolsBuilder.Create(_tempDir, pathResolver: TestPathResolver.Create(), maxReadFileBytes: invalidBytes));
        Assert.Equal("scope", ex.ParamName);
    }

    [Fact]
    public async Task ReadAsync_WhenContentUnderCap_ReturnsFullLinesWithoutTruncationFooter()
    {
        var filePath = Path.Combine(_tempDir, "small.txt");
        var originalLines = new[]
        {
            "const a = 1;",
            "const b = 2;",
            "console.log(a + b);"
        };
        await File.WriteAllLinesAsync(filePath, originalLines);

        var tools = TestCanonicalToolsBuilder.Create(_tempDir, pathResolver: TestPathResolver.Create());
        var result = await tools.ReadAsync("small.txt");

        Assert.Contains("const a = 1;", result);
        Assert.Contains("const b = 2;", result);
        Assert.Contains("console.log(a + b);", result);
        Assert.DoesNotContain("[Output truncated", result);
    }

    [Fact]
    public async Task ReadAsync_WhenSliceExactlyAtCap_DoesNotTruncate()
    {
        var filePath = Path.Combine(_tempDir, "exact_cap.txt");
        // Line 1: $"{1,5} | {line}\n"
        // prefix: "    1 | " (8 chars/bytes) + newline (1 byte) = 9 bytes overhead
        // With 41 chars of ASCII, formatted line is 8 + 41 + 1 = 50 bytes.
        // Two lines of 41 chars => 50 + 50 = 100 bytes.
        var line1 = new string('a', 41);
        var line2 = new string('b', 41);
        await File.WriteAllLinesAsync(filePath, new[] { line1, line2 });

        const int capBytes = 100;
        var tools = TestCanonicalToolsBuilder.Create(
            _tempDir,
            pathResolver: TestPathResolver.Create(),
            maxReadFileBytes: capBytes);

        var result = await tools.ReadAsync("exact_cap.txt");

        Assert.DoesNotContain("[Output truncated", result);
        Assert.Contains(line1, result);
        Assert.Contains(line2, result);
        Assert.True(Encoding.UTF8.GetByteCount(result) <= capBytes);
    }

    [Fact]
    public async Task ReadAsync_WhenMultiLineFileExceedsCap_TruncatesCleanlyAtLineBoundaryAndAppendsNotice()
    {
        var filePath = Path.Combine(_tempDir, "large_lines.txt");
        var lines = Enumerable.Range(1, 200).Select(i => $"console.log('line number {i:D4}');").ToArray();
        await File.WriteAllLinesAsync(filePath, lines);

        const int capBytes = 600;
        var tools = TestCanonicalToolsBuilder.Create(
            _tempDir,
            pathResolver: TestPathResolver.Create(),
            maxReadFileBytes: capBytes);

        var result = await tools.ReadAsync("large_lines.txt");

        Assert.True(Encoding.UTF8.GetByteCount(result) <= capBytes,
            $"Expected byte count <= {capBytes}, but was {Encoding.UTF8.GetByteCount(result)}");
        Assert.Contains("[Output truncated: reached", result);
        Assert.Contains("of 200. Use 'offset'", result);

        Assert.Contains("line number 0001", result);
        Assert.Contains("line number 0002", result);
    }

    [Fact]
    public async Task ReadAsync_WhenSingleLineExceedsCap_TruncatesWithinLineAndAppendsLineExceededNotice()
    {
        var filePath = Path.Combine(_tempDir, "bundle.min.js");
        var hugeLine = "var bundle = {" + new string('x', 2000) + "};";
        await File.WriteAllTextAsync(filePath, hugeLine);

        const int capBytes = 500;
        var tools = TestCanonicalToolsBuilder.Create(
            _tempDir,
            pathResolver: TestPathResolver.Create(),
            maxReadFileBytes: capBytes);

        var result = await tools.ReadAsync("bundle.min.js");

        Assert.True(Encoding.UTF8.GetByteCount(result) <= capBytes,
            $"Expected byte count <= {capBytes}, but was {Encoding.UTF8.GetByteCount(result)}");
        Assert.Contains(ReadOutputFormatter.TruncatedMarker, result);
        Assert.Contains("[Output truncated: line 1 exceeded", result);
        Assert.Contains("total file lines: 1", result);
    }

    [Fact]
    public async Task ReadAsync_LargeFileWithDefaultCap_CappedAt100KB()
    {
        var filePath = Path.Combine(_tempDir, "very_large.js");
        var lines = Enumerable.Range(1, 20_000).Select(i => $"function doWork_{i}() {{ return {i}; }}").ToArray();
        await File.WriteAllLinesAsync(filePath, lines);

        var tools = TestCanonicalToolsBuilder.Create(_tempDir, pathResolver: TestPathResolver.Create());
        var result = await tools.ReadAsync("very_large.js");

        var byteCount = Encoding.UTF8.GetByteCount(result);
        Assert.True(byteCount <= WorkflowDefaults.MaxReadFileBytes,
            $"Expected byte count <= {WorkflowDefaults.MaxReadFileBytes}, but was {byteCount}");
        Assert.Contains($"[Output truncated: reached {WorkflowDefaults.MaxReadFileBytes / 1024} KB limit at line", result);
        Assert.Contains("of 20000. Use 'offset'", result);
    }

    [Fact]
    public async Task ReadAsync_SliceWithLimitThatFitsWithinCap_DoesNotTruncate()
    {
        var filePath = Path.Combine(_tempDir, "large_file.txt");
        var lines = Enumerable.Range(1, 5000).Select(i => $"Line {i} content data here").ToArray();
        await File.WriteAllLinesAsync(filePath, lines);

        var tools = TestCanonicalToolsBuilder.Create(_tempDir, pathResolver: TestPathResolver.Create());
        var result = await tools.ReadAsync("large_file.txt", offset: 100, limit: 5);

        Assert.DoesNotContain("[Output truncated", result);
        Assert.Contains("Line 100 content data here", result);
        Assert.Contains("Line 104 content data here", result);
        Assert.DoesNotContain("Line 99 content data here", result);
        Assert.DoesNotContain("Line 105 content data here", result);
    }

    [Fact]
    public async Task ReadAsync_Pagination_EnsuresNoLinesSkippedOrDuplicated()
    {
        var filePath = Path.Combine(_tempDir, "paged_file.txt");
        const int totalLines = 60;
        var lines = Enumerable.Range(1, totalLines).Select(i => $"Record entry number {i:D3}").ToArray();
        await File.WriteAllLinesAsync(filePath, lines);

        // Cap at 450 bytes
        const int capBytes = 450;
        var tools = TestCanonicalToolsBuilder.Create(
            _tempDir,
            pathResolver: TestPathResolver.Create(),
            maxReadFileBytes: capBytes);

        var collectedLineNumbers = new List<int>();
        int currentOffset = 1;

        while (currentOffset <= totalLines)
        {
            var page = await tools.ReadAsync("paged_file.txt", offset: currentOffset);

            // Extract line numbers rendered in the page: "    1 | ..."
            var lineMatches = Regex.Matches(page, @"^\s*(\d+)\s*\|", RegexOptions.Multiline);
            foreach (Match m in lineMatches)
            {
                collectedLineNumbers.Add(int.Parse(m.Groups[1].Value));
            }

            var truncMatch = Regex.Match(page, @"at line (\d+) of \d+\. Use 'offset' \(e\.g\. offset: (\d+)\)");
            if (truncMatch.Success)
            {
                var reportedLastLine = int.Parse(truncMatch.Groups[1].Value);
                var suggestedNextOffset = int.Parse(truncMatch.Groups[2].Value);
                Assert.Equal(reportedLastLine + 1, suggestedNextOffset);
                Assert.Equal(reportedLastLine, collectedLineNumbers[^1]);
                currentOffset = suggestedNextOffset;
            }
            else
            {
                // Finished last page without truncation
                break;
            }
        }

        // Verify that all 60 lines were collected in exact consecutive order with no skips and no duplicates
        Assert.Equal(totalLines, collectedLineNumbers.Count);
        for (int i = 0; i < totalLines; i++)
        {
            Assert.Equal(i + 1, collectedLineNumbers[i]);
        }
    }

    [Fact]
    public async Task ReadAsync_WhenContentNearCapHasMultibyteAndSurrogateCharacters_DoesNotSplitSurrogatesAndRespectsCap()
    {
        var filePath = Path.Combine(_tempDir, "unicode_surrogates.txt");
        // Emoji 😀 is surrogate pair \uD83D\uDE00 (4 bytes in UTF-8).
        // Accented é is 2 bytes in UTF-8.
        var unicodeLine = "Emojis: " + string.Concat(Enumerable.Repeat("😀é", 300));
        await File.WriteAllTextAsync(filePath, unicodeLine);

        const int capBytes = 250;
        var tools = TestCanonicalToolsBuilder.Create(
            _tempDir,
            pathResolver: TestPathResolver.Create(),
            maxReadFileBytes: capBytes);

        var result = await tools.ReadAsync("unicode_surrogates.txt");

        Assert.True(Encoding.UTF8.GetByteCount(result) <= capBytes);
        Assert.False(char.IsHighSurrogate(result[^1]), "Result must not end on a split high surrogate.");

        // Check for round-trip encoding validity (no replacement characters due to split surrogate)
        var utf8Bytes = Encoding.UTF8.GetBytes(result);
        var roundTripped = Encoding.UTF8.GetString(utf8Bytes);
        Assert.Equal(result, roundTripped);
    }

    [Fact]
    public void ReadOutputFormatter_TruncateStringToUtf8ByteCount_HandlesSplitSurrogatePairDirectly()
    {
        // "abc" (3 bytes) + "😀" (\uD83D\uDE00, 4 bytes) = 7 bytes total
        string text = "abc\uD83D\uDE00";

        // If maxBytes is 6, including high surrogate would use 3 + 3 = 6 bytes and split the pair.
        // It must drop the high surrogate and return "abc" (3 bytes).
        var truncated = ReadOutputFormatter.TruncateStringToUtf8ByteCount(text, 6);
        Assert.Equal("abc", truncated);

        // If maxBytes is 7, the full emoji fits
        var full = ReadOutputFormatter.TruncateStringToUtf8ByteCount(text, 7);
        Assert.Equal(text, full);
    }

    [Fact]
    public void CanonicalAIFunctionFactory_Description_ReflectsOverriddenMaxReadFileBytes()
    {
        var tools = TestCanonicalToolsBuilder.Create(_tempDir, pathResolver: TestPathResolver.Create(), maxReadFileBytes: 50 * 1024);
        var aiTools = CanonicalAIFunctionFactory.CreateTools(tools);
        var readTool = aiTools.Single(t => t.Name == CanonicalToolNames.Read);

        Assert.Contains("Output is capped to 50 KB", readTool.Description);
    }

    [Fact]
    public void CanonicalAIFunctionFactory_Description_WithoutToolsInstance_ReflectsWorkflowDefaults()
    {
        var functions = CanonicalAIFunctionFactory.CreateAllCanonicalFunctions();
        var readTool = functions.Single(f => f.Name == CanonicalToolNames.Read);

        Assert.Contains($"Output is capped to {WorkflowDefaults.MaxReadFileBytes / 1024} KB", readTool.Description);
    }
}
