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

    [Fact]
    public void Parse_WithStringifiedQuestionsArrayMissingClosingBracket_AutoFixesAndParsesAllQuestions()
    {
        // Repro Task 31 / Task 41
        var json = """
        {
            "questions": "[{\"prompt\": \"Q1\", \"options\": [\"A\", \"B\"]}, {\"prompt\": \"Q2\", \"options\": [\"C\", \"D\"]}"
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Equal(2, questions.Count);
        Assert.Equal("Q1", questions[0].Prompt);
        Assert.Equal("Q2", questions[1].Prompt);
        Assert.Equal(2, questions[1].Options?.Count);
    }

    [Fact]
    public void ParseFromObject_WithStringMissingClosingSquareBracket_AutoFixesAndParses()
    {
        var rawStr = "[{\"prompt\": \"Q1\"}, {\"prompt\": \"Q2\"}";
        var questions = QuestionItemParser.ParseFromObject(rawStr);

        Assert.NotNull(questions);
        Assert.Equal(2, questions!.Count);
        Assert.Equal("Q1", questions[0].Prompt);
        Assert.Equal("Q2", questions[1].Prompt);
    }

    [Fact]
    public void Parse_WithTruncatedStringifiedOptionsArray_RepairsAndExtractsOptions()
    {
        var json = """
        {
            "questions": [
                {
                    "prompt": "Select target environment",
                    "options": "[\"Staging\", \"Prod\""
                }
            ]
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Single(questions);
        Assert.Equal("Select target environment", questions[0].Prompt);
        Assert.Equal(["Staging", "Prod"], questions[0].Options);
    }

    [Fact]
    public void Parse_WithTruncatedDanglingKeyInQuestionObject_RepairsAndParsesQuestion()
    {
        var truncated = """{"questions": [{"prompt": "Which DB?", "options": ["SQLite", "Postgres"], "ration""";

        var questions = QuestionItemParser.Parse(truncated);

        Assert.Single(questions);
        Assert.Equal("Which DB?", questions[0].Prompt);
        Assert.Equal(["SQLite", "Postgres"], questions[0].Options);
    }

    [Fact]
    public void Parse_WithDoubleNestedJsonArrayInPrompt_PreservesAllQuestions()
    {
        // Live repro: Task 66 double-nested JSON prompt
        var json = """
        {
            "questions": [
                {
                    "prompt": "[{\"prompt\": \"Where should recommended.json persist?\", \"rationale\": \"Persistence design\", \"options\": [\"Repo root\", \"Crucible dir\"]}, {\"prompt\": \"What should the semantics of default be?\", \"rationale\": \"Behavior\", \"options\": [\"Opt A\", \"Opt B\"]}]"
                }
            ]
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Equal(2, questions.Count);
        Assert.Equal("Where should recommended.json persist?", questions[0].Prompt);
        Assert.Equal("Persistence design", questions[0].Rationale);
        Assert.Equal(["Repo root", "Crucible dir"], questions[0].Options);

        Assert.Equal("What should the semantics of default be?", questions[1].Prompt);
        Assert.Equal("Behavior", questions[1].Rationale);
        Assert.Equal(["Opt A", "Opt B"], questions[1].Options);
    }

    [Fact]
    public void Normalize_WithNestedJsonPrompt_FlattensIntoDistinctQuestions()
    {
        var rawPrompt = "[{\"prompt\": \"Q1\", \"options\": [\"A\", \"B\"]}, {\"prompt\": \"Q2\", \"options\": [\"C\"]}]";
        var input = new List<QuestionItem>
        {
            new(rawPrompt)
        };

        var normalized = QuestionItemParser.Normalize(input);

        Assert.Equal(2, normalized.Count);
        Assert.Equal("Q1", normalized[0].Prompt);
        Assert.Equal(["A", "B"], normalized[0].Options);
        Assert.Equal("Q2", normalized[1].Prompt);
        Assert.Equal(["C"], normalized[1].Options);
    }

    [Fact]
    public void Normalize_WithCleanItems_ReturnsSameListWithoutAllocation()
    {
        var input = new List<QuestionItem>
        {
            new("Clean prompt 1", null, new[] { "A", "B" }),
            new("Clean prompt 2")
        };

        var normalized = QuestionItemParser.Normalize(input);

        Assert.Same(input, normalized);
    }

    [Fact]
    public void ToQuestionItems_WithDoubleNestedPromptEntity_UnpacksBothQuestions()
    {
        var entity = new Question
        {
            Id = 129,
            TaskId = 66,
            Prompt = "[{\"prompt\": \"Where to persist?\", \"rationale\": \"Design\", \"options\": [\"A\", \"B\"]}, {\"prompt\": \"Semantics?\", \"options\": [\"1\", \"2\"]}]",
            Rationale = "",
            OptionsJson = null
        };

        var items = QuestionItemParser.ToQuestionItems(entity);

        Assert.Equal(2, items.Count);
        Assert.Equal("Where to persist?", items[0].Prompt);
        Assert.Equal("Design", items[0].Rationale);
        Assert.Equal(["A", "B"], items[0].Options);

        Assert.Equal("Semantics?", items[1].Prompt);
        Assert.Equal(["1", "2"], items[1].Options);
    }

    [Fact]
    public void ToQuestionItems_WithCleanEntity_ReturnsMappedItem()
    {
        var entity = new Question
        {
            Id = 1,
            TaskId = 10,
            Prompt = "Target runtime?",
            Rationale = "Configuration",
            OptionsJson = "[\"net9.0\", \"net10.0\"]"
        };

        var items = QuestionItemParser.ToQuestionItems(entity);

        Assert.Single(items);
        Assert.Equal("Target runtime?", items[0].Prompt);
        Assert.Equal("Configuration", items[0].Rationale);
        Assert.Equal(["net9.0", "net10.0"], items[0].Options);
    }
}
