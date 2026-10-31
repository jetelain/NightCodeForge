using System.Text.Json;
using NightCodeForge.Core.Domain;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class JsonRepairHelperTests
{
    [Fact]
    public void Repair_WithNullOrEmpty_ReturnsEmptyObject()
    {
        Assert.Equal("{}", JsonRepairHelper.Repair(null));
        Assert.Equal("{}", JsonRepairHelper.Repair(""));
        Assert.Equal("{}", JsonRepairHelper.Repair("   "));
    }

    [Fact]
    public void Repair_AlreadyValidJson_ReturnsOriginal()
    {
        var json = """{"questions": [{"prompt": "Do we want X?"}]}""";
        var repaired = JsonRepairHelper.Repair(json);
        Assert.Equal(json, repaired);
    }

    [Fact]
    public void Repair_MissingClosingSquareBracket_AutoFixes()
    {
        // Simulates array where outer ']' was omitted: [{"prompt": "q1"}, {"prompt": "q2"}
        var truncated = """[{"prompt": "q1"}, {"prompt": "q2"}]""".TrimEnd(']');
        var repaired = JsonRepairHelper.Repair(truncated);

        using var doc = JsonDocument.Parse(repaired);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal("q1", doc.RootElement[0].GetProperty("prompt").GetString());
        Assert.Equal("q2", doc.RootElement[1].GetProperty("prompt").GetString());
    }

    [Fact]
    public void Repair_Task31AndTask41Repro_MissingClosingBracketAfterNestedOptions()
    {
        // Exact pattern from Task 31 and Task 41:
        // [{"prompt": "Q1", "options": ["A", "B"]}, {"prompt": "Q2", "options": ["C", "D"]}
        // Notice it closed the inner options ']' and the inner question '}', but forgot the outer ']'
        var taskPattern = """[{"prompt": "Q1", "options": ["A", "B"]}, {"prompt": "Q2", "options": ["C", "D"]}""";
        var repaired = JsonRepairHelper.Repair(taskPattern);

        using var doc = JsonDocument.Parse(repaired);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal("Q1", doc.RootElement[0].GetProperty("prompt").GetString());
        Assert.Equal("Q2", doc.RootElement[1].GetProperty("prompt").GetString());
        Assert.Equal("C", doc.RootElement[1].GetProperty("options")[0].GetString());
    }

    [Fact]
    public void Repair_MissingClosingBrace_AutoFixes()
    {
        var truncated = """{"prompt": "Should we migrate?", "options": ["Yes", "No"]""";
        var repaired = JsonRepairHelper.Repair(truncated);

        using var doc = JsonDocument.Parse(repaired);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Equal("Should we migrate?", doc.RootElement.GetProperty("prompt").GetString());
    }

    [Fact]
    public void Repair_MissingClosingBracketAndBrace_AutoFixes()
    {
        var truncated = """{"questions": [{"prompt": "Deploy now?"}""";
        var repaired = JsonRepairHelper.Repair(truncated);

        using var doc = JsonDocument.Parse(repaired);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Single(doc.RootElement.GetProperty("questions").EnumerateArray());
    }

    [Fact]
    public void Repair_WithTrailingCommaBeforeClose_StripsTrailingComma()
    {
        var input = """[{"prompt": "Continue?"}, """;
        var repaired = JsonRepairHelper.Repair(input);

        using var doc = JsonDocument.Parse(repaired);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Single(doc.RootElement.EnumerateArray());
    }

    [Fact]
    public void Repair_UnclosedString_ClosesStringAndContainers()
    {
        var input = """{"prompt": "Incomplete string value""";
        var repaired = JsonRepairHelper.Repair(input);

        using var doc = JsonDocument.Parse(repaired);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Equal("Incomplete string value", doc.RootElement.GetProperty("prompt").GetString());
    }

    [Fact]
    public void Repair_StrippedMarkdownFences()
    {
        var input = "```json\n[{\"prompt\": \"Test?\"}]\n```";
        var repaired = JsonRepairHelper.Repair(input);

        using var doc = JsonDocument.Parse(repaired);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal("Test?", doc.RootElement[0].GetProperty("prompt").GetString());
    }

    [Fact]
    public void Repair_SingleLineMarkdownFences_StripsFences()
    {
        var input = "```json [{\"prompt\": \"SingleLine?\"}] ```";
        var repaired = JsonRepairHelper.Repair(input);

        using var doc = JsonDocument.Parse(repaired);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal("SingleLine?", doc.RootElement[0].GetProperty("prompt").GetString());
    }

    [Fact]
    public void Repair_MissingInnerClosingBracketBeforeOuterClosingBrace_AutoFixes()
    {
        // Outer '}' is present, but inner ']' for "questions" array was omitted before '}'
        var input = """{"questions": [{"prompt": "Q1", "options": ["A", "B"]}, {"prompt": "Q2", "options": ["C", "D"]}}""";
        var repaired = JsonRepairHelper.Repair(input);

        using var doc = JsonDocument.Parse(repaired);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        var questions = doc.RootElement.GetProperty("questions");
        Assert.Equal(2, questions.GetArrayLength());
        Assert.Equal("Q1", questions[0].GetProperty("prompt").GetString());
        Assert.Equal("Q2", questions[1].GetProperty("prompt").GetString());
    }

    [Fact]
    public void Repair_MissingInnerClosingBraceBeforeOuterClosingBracket_AutoFixes()
    {
        // Outer ']' is present, but inner '}' for second question object was omitted before ']'
        var input = """[{"prompt": "Q1"}, {"prompt": "Q2", "options": ["A", "B"]]""";
        var repaired = JsonRepairHelper.Repair(input);

        using var doc = JsonDocument.Parse(repaired);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal("Q2", doc.RootElement[1].GetProperty("prompt").GetString());
    }

    [Fact]
    public void Repair_ExcessClosingBrackets_IgnoresUnmatchedDelimiters()
    {
        var input = """[{"prompt": "Q1"}]]""";
        var repaired = JsonRepairHelper.Repair(input);

        using var doc = JsonDocument.Parse(repaired);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Single(doc.RootElement.EnumerateArray());
    }

    [Fact]
    public void Repair_TrailingCommaInsideClosedArray_StripsCommaBeforeBracket()
    {
        var input = """[{"prompt": "Q1"}, ]""";
        var repaired = JsonRepairHelper.Repair(input);

        using var doc = JsonDocument.Parse(repaired);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Single(doc.RootElement.EnumerateArray());
    }

    [Fact]
    public void Repair_UnclosedStringWithTrailingBackslash_EscapesBackslashAndClosesString()
    {
        var input = "{\"prompt\": \"Path C:\\";
        var repaired = JsonRepairHelper.Repair(input);

        using var doc = JsonDocument.Parse(repaired);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Equal("Path C:\\", doc.RootElement.GetProperty("prompt").GetString());
    }
}
