using System.Text.Json;
using NightCodeForge.Core.Domain;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class QuestionItemParserTests
{
    [Fact]
    public void Parse_WithNullOrWhitespace_ReturnsFallback()
    {
        var items1 = QuestionItemParser.Parse(null, "Please choose an option");
        Assert.Single(items1);
        Assert.Equal("Please choose an option", items1[0].Prompt);

        var items2 = QuestionItemParser.Parse("", null);
        Assert.Single(items2);
        Assert.Equal("Clarification requested", items2[0].Prompt);
    }

    [Fact]
    public void Parse_WithInvalidJson_ReturnsFallbackOrRawContent()
    {
        var items1 = QuestionItemParser.Parse("not-a-valid-json", "Fallback prompt");
        Assert.Single(items1);
        Assert.Equal("Fallback prompt", items1[0].Prompt);

        var items2 = QuestionItemParser.Parse("just a raw string prompt", null);
        Assert.Single(items2);
        Assert.Equal("just a raw string prompt", items2[0].Prompt);
    }

    [Fact]
    public void Parse_WithQuestionsArray_ParsesItemsWithRationaleAndOptions()
    {
        var json = """
        {
            "questions": [
                {
                    "prompt": "Which DB should we target?",
                    "rationale": "Schema requirements",
                    "options": ["PostgreSQL", "SQLite", "SQL Server"]
                },
                {
                    "prompt": "Provide staging URL:"
                }
            ]
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Equal(2, questions.Count);
        Assert.Equal("Which DB should we target?", questions[0].Prompt);
        Assert.Equal("Schema requirements", questions[0].Rationale);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(3, questions[0].Options!.Count);
        Assert.Equal("PostgreSQL", questions[0].Options![0]);

        Assert.Equal("Provide staging URL:", questions[1].Prompt);
        Assert.Null(questions[1].Rationale);
        Assert.Null(questions[1].Options);
    }

    [Fact]
    public void Parse_WithSinglePromptObject_ParsesSingleQuestion()
    {
        var json = """
        {
            "prompt": "Should we proceed with migration?",
            "options": ["Yes", "No"]
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Single(questions);
        Assert.Equal("Should we proceed with migration?", questions[0].Prompt);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
    }

    [Fact]
    public void Parse_WithStringifiedQuestionsArray_ParsesCorrectly()
    {
        var json = """
        {
            "questions": "[{\"prompt\": \"Select database engine\", \"rationale\": \"Storage design\", \"options\": [\"PostgreSQL\", \"SQLite\"]}]"
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Single(questions);
        Assert.Equal("Select database engine", questions[0].Prompt);
        Assert.Equal("Storage design", questions[0].Rationale);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
        Assert.Equal("PostgreSQL", questions[0].Options![0]);
        Assert.Equal("SQLite", questions[0].Options![1]);
    }

    [Fact]
    public void Parse_WithStringifiedSingleQuestionObject_ParsesCorrectly()
    {
        var json = """
        {
            "questions": "{\"prompt\": \"Confirm deploy to staging?\", \"options\": [\"Yes\", \"No\"]}"
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Single(questions);
        Assert.Equal("Confirm deploy to staging?", questions[0].Prompt);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
        Assert.Equal("Yes", questions[0].Options![0]);
        Assert.Equal("No", questions[0].Options![1]);
    }

    [Fact]
    public void Parse_WithStringifiedNestedOptions_ParsesOptions()
    {
        var json = """
        {
            "questions": [
                {
                    "prompt": "Select target environment",
                    "options": "[\"Staging\", \"Prod\"]"
                }
            ]
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Single(questions);
        Assert.Equal("Select target environment", questions[0].Prompt);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
        Assert.Equal("Staging", questions[0].Options![0]);
        Assert.Equal("Prod", questions[0].Options![1]);
    }

    [Fact]
    public void Parse_WithOptionsJsonProperty_ParsesOptions()
    {
        var json = """
        {
            "questions": [
                {
                    "prompt": "Select caching layer",
                    "optionsJson": "[\"Redis\", \"Memory\"]"
                }
            ]
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Single(questions);
        Assert.Equal("Select caching layer", questions[0].Prompt);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
        Assert.Equal("Redis", questions[0].Options![0]);
        Assert.Equal("Memory", questions[0].Options![1]);
    }

    [Fact]
    public void Parse_WithQuestionObjectProperty_ParsesPrompt()
    {
        var json = """
        {
            "question": {
                "prompt": "Confirm database rollback?",
                "options": ["Proceed", "Abort"]
            }
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Single(questions);
        Assert.Equal("Confirm database rollback?", questions[0].Prompt);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
        Assert.Equal("Proceed", questions[0].Options![0]);
        Assert.Equal("Abort", questions[0].Options![1]);
    }

    [Fact]
    public void Parse_WithQuestionStringContainingJsonObject_ParsesPrompt()
    {
        var json = """
        {
            "question": "{\"prompt\": \"Confirm database rollback?\", \"options\": [\"Proceed\", \"Abort\"]}"
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Single(questions);
        Assert.Equal("Confirm database rollback?", questions[0].Prompt);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
        Assert.Equal("Proceed", questions[0].Options![0]);
        Assert.Equal("Abort", questions[0].Options![1]);
    }

    [Fact]
    public void ParseFromObject_WithTypedList_ReturnsList()
    {
        var original = new List<QuestionItem> { new("Prompt A") };
        var result = QuestionItemParser.ParseFromObject(original);
        Assert.Same(original, result);
    }

    [Fact]
    public void ParseFromObject_WithJsonElementArray_ReturnsItems()
    {
        using var doc = JsonDocument.Parse("""[{"prompt": "Pick one", "options": ["A", "B"]}]""");
        var result = QuestionItemParser.ParseFromObject(doc.RootElement);
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Pick one", result[0].Prompt);
        Assert.Equal(2, result[0].Options?.Count);
    }

    [Fact]
    public void ExtractOptionsFromObject_WithVariousInputs_ReturnsExpected()
    {
        Assert.Null(QuestionItemParser.ExtractOptionsFromObject(null));
        Assert.Null(QuestionItemParser.ExtractOptionsFromObject(new List<string>()));
        Assert.Null(QuestionItemParser.ExtractOptionsFromObject("not a json array"));

        var list = QuestionItemParser.ExtractOptionsFromObject(new List<string> { "X", "Y" });
        Assert.NotNull(list);
        Assert.Equal(2, list.Count);

        var fromString = QuestionItemParser.ExtractOptionsFromObject("[\"Option1\", \"Option2\"]");
        Assert.NotNull(fromString);
        Assert.Equal(2, fromString.Count);
        Assert.Equal("Option1", fromString[0]);

        using var doc = JsonDocument.Parse("[\"Alpha\", \"Beta\"]");
        var fromElement = QuestionItemParser.ExtractOptionsFromObject(doc.RootElement);
        Assert.NotNull(fromElement);
        Assert.Equal(2, fromElement.Count);
    }
}
