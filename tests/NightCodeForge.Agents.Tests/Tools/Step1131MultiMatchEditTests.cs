namespace NightCodeForge.Agents.Tests;

using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using Xunit;

public class Step1131MultiMatchEditTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CanonicalTools _tools;

    public Step1131MultiMatchEditTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf-multimatch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _tools = TestCanonicalToolsBuilder.Create(_tempDir, pathResolver: TestPathResolver.Create());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    [Fact]
    public async Task EditAsync_WithReplaceAllTrue_SingleEdit_ReplacesAllOccurrences()
    {
        var filePath = Path.Combine(_tempDir, "file.txt");
        await File.WriteAllTextAsync(filePath, "testMethod(1);\ntestMethod(2);\ntestMethod(3);\n");

        var result = await _tools.EditAsync("file.txt", "testMethod", "renamedMethod", replaceAll: true);

        Assert.Contains("Successfully applied 1 edit(s) (3 occurrence(s) replaced) to 'file.txt'", result);

        var content = await File.ReadAllTextAsync(filePath);
        Assert.Equal("renamedMethod(1);\nrenamedMethod(2);\nrenamedMethod(3);\n", content);
    }

    [Fact]
    public async Task EditAsync_WithReplaceAllTrue_ChunkArray_ReplacesAllOccurrencesAcrossMultipleChunks()
    {
        var filePath = Path.Combine(_tempDir, "MultiChunk.cs");
        await File.WriteAllTextAsync(filePath, "var a = foo; var b = foo; var c = bar; var d = bar;");

        var edits = new List<EditChunk>
        {
            new("foo", "qux", ReplaceAll: true),
            new("bar", "baz", ReplaceAll: true)
        };

        var result = await _tools.EditAsync("MultiChunk.cs", edits);

        Assert.Contains("Successfully applied 2 edit(s) (4 occurrence(s) replaced) to 'MultiChunk.cs'", result);

        var content = await File.ReadAllTextAsync(filePath);
        Assert.Equal("var a = qux; var b = qux; var c = baz; var d = baz;\n", content);
    }

    [Fact]
    public async Task EditAsync_WithReplaceAllFalse_WhenMultipleMatches_ThrowsAmbiguityException()
    {
        var filePath = Path.Combine(_tempDir, "ambiguous.txt");
        await File.WriteAllTextAsync(filePath, "hello world\nhello world\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.EditAsync("ambiguous.txt", "hello world", "goodbye world", replaceAll: false));

        Assert.Contains("matches multiple locations in 'ambiguous.txt'. Please provide more surrounding context to disambiguate or use replaceAll: true.", ex.Message);
    }

    [Fact]
    public async Task EditAsync_WithReplaceAllTrue_WhenZeroMatches_ThrowsNotFoundException()
    {
        var filePath = Path.Combine(_tempDir, "missing.txt");
        await File.WriteAllTextAsync(filePath, "existing text here\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.EditAsync("missing.txt", "does_not_exist", "replacement", replaceAll: true));

        Assert.Contains("could not locate target chunk in 'missing.txt'", ex.Message);
    }

    [Fact]
    public async Task LenientToolDispatcher_NormalizesReplaceAllVariants()
    {
        var filePath = Path.Combine(_tempDir, "dispatcher_test.txt");
        await File.WriteAllTextAsync(filePath, "alpha beta alpha gamma alpha");

        var dispatcher = new LenientToolDispatcher(_tools);

        // Test with replace_all in snake_case
        var jsonSnake = "{\"path\": \"dispatcher_test.txt\", \"old_text\": \"alpha\", \"new_text\": \"delta\", \"replace_all\": true}";
        var resultSnake = await dispatcher.DispatchAsync("edit", jsonSnake);

        Assert.Contains("Successfully applied 1 edit(s) (3 occurrence(s) replaced)", resultSnake);

        var updatedContent = await File.ReadAllTextAsync(filePath);
        Assert.Equal("delta beta delta gamma delta\n", updatedContent);

        // Test array edits with "all": true
        var jsonArray = "{\"path\": \"dispatcher_test.txt\", \"edits\": [{\"old_text\": \"delta\", \"new_text\": \"omega\", \"all\": true}]}";
        var resultArray = await dispatcher.DispatchAsync("edit", jsonArray);

        Assert.Contains("Successfully applied 1 edit(s) (3 occurrence(s) replaced)", resultArray);

        var finalContent = await File.ReadAllTextAsync(filePath);
        Assert.Equal("omega beta omega gamma omega\n", finalContent);
    }

    [Fact]
    public async Task CanonicalAIFunctionFactory_WhenEditsProvidedWithTopLevelReplaceAll_PropagatesReplaceAllToChunks()
    {
        var filePath = Path.Combine(_tempDir, "factory_test.txt");
        await File.WriteAllTextAsync(filePath, "foo bar foo baz foo");

        var tools = CanonicalAIFunctionFactory.CreateTools(_tools);
        var editTool = tools.Single(t => t.Name == "edit");

        var funcArgs = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["path"] = "factory_test.txt",
            ["replace_all"] = true,
            ["edits"] = new List<EditChunk>
            {
                new("foo", "qux", ReplaceAll: false)
            }
        };

        var result = await editTool.InvokeAsync(funcArgs);
        var resultStr = result?.ToString() ?? string.Empty;

        Assert.Contains("Successfully applied 1 edit(s) (3 occurrence(s) replaced)", resultStr);

        var updated = await File.ReadAllTextAsync(filePath);
        Assert.Equal("qux bar qux baz qux\n", updated);
    }

    [Fact]
    public async Task EditAsync_WithReplaceAllTrue_MultiLine_WhenFileHasCrlfAndOldTextHasLf_MatchesAndReplaces()
    {
        var filePath = Path.Combine(_tempDir, "crlf_file.txt");
        // File on disk has CRLF line endings
        await File.WriteAllBytesAsync(filePath, System.Text.Encoding.UTF8.GetBytes("line1\r\nline2\r\nline1\r\nline2\r\n"));

        // LLM generates oldText with LF only
        var oldText = "line1\nline2";
        var newText = "alpha\nbeta";

        var result = await _tools.EditAsync("crlf_file.txt", oldText, newText, replaceAll: true);

        Assert.Contains("Successfully applied 1 edit(s) (2 occurrence(s) replaced)", result);

        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("alpha\r\nbeta\r\nalpha\r\nbeta\r\n", content);
    }

    [Fact]
    public async Task EditAsync_WithReplaceAllTrue_MultiLine_WhenFileHasLfAndOldTextHasCrlf_MatchesAndReplaces()
    {
        var filePath = Path.Combine(_tempDir, "lf_file.txt");
        // File on disk has LF line endings
        await File.WriteAllBytesAsync(filePath, System.Text.Encoding.UTF8.GetBytes("blockA\nblockB\nblockA\nblockB\n"));

        // oldText provided with CRLF
        var oldText = "blockA\r\nblockB";
        var newText = "newA\r\nnewB";

        var result = await _tools.EditAsync("lf_file.txt", oldText, newText, replaceAll: true);

        Assert.Contains("Successfully applied 1 edit(s) (2 occurrence(s) replaced)", result);

        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("newA\nnewB\nnewA\nnewB\n", content);
    }

    [Fact]
    public async Task EditAsync_WithReplaceAllTrue_WhenNewTextIsNull_DeletesAllOccurrences()
    {
        var filePath = Path.Combine(_tempDir, "delete_file.txt");
        await File.WriteAllTextAsync(filePath, "KEEP_A REMOVE_ME KEEP_B REMOVE_ME KEEP_C");

        var result = await _tools.EditAsync("delete_file.txt", "REMOVE_ME ", null!, replaceAll: true);

        Assert.Contains("Successfully applied 1 edit(s) (2 occurrence(s) replaced)", result);

        var content = await File.ReadAllTextAsync(filePath);
        Assert.Equal("KEEP_A KEEP_B KEEP_C\n", content);
    }
}
